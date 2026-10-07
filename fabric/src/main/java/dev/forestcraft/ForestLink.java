package dev.forestcraft;

import java.nio.MappedByteBuffer;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.world.phys.Vec3;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;

public final class ForestLink {
	public static final Logger LOG = LoggerFactory.getLogger("ForestCraft");

	private static MappedByteBuffer map;
	private static int appliedTeleport = -1;
	private static int clientAppliedTeleport = -1;
	private static volatile int settledSeq = -1;
	private static int appliedMouse = -1;
	private static int mouseAck;
	private static float[] heights = new float[Proto.GRID * Proto.GRID];
	private static int originX, originZ, gridSeq;
	private static net.minecraft.world.phys.shapes.VoxelShape[] solids = new net.minecraft.world.phys.shapes.VoxelShape[0];
	private static final java.util.HashMap<MaskKey, net.minecraft.world.phys.shapes.VoxelShape> SHAPES = new java.util.HashMap<>();
	private static final int WORDS = 8;

	private record MaskKey(long[] words) {
		@Override public boolean equals(Object o) { return o instanceof MaskKey k && java.util.Arrays.equals(words, k.words); }
		@Override public int hashCode() { return java.util.Arrays.hashCode(words); }
	}
	private static int solidX, solidY, solidZ, solidSX, solidSY, solidSZ, solidSeq;
	private static int tickCount;

	private ForestLink() {}

	public static boolean open() {
		if (map != null) return true;
		try {
			if (!java.nio.file.Files.isRegularFile(Proto.linkFile())) return false;
			map = Proto.open();
			int magic = map.getInt(Proto.OFF_HEADER);
			int version = map.getInt(Proto.OFF_HEADER + 4);
			if (magic != Proto.MAGIC || version != Proto.VERSION) {
				LOG.warn("link magic/version {} / {}, expected {} / {}", magic, version, Proto.MAGIC, Proto.VERSION);
				map = null;
				return false;
			}
			map.putInt(Proto.OFF_HEADER + 12, (int) ProcessHandle.current().pid());
			// Entity textures are numbered per Minecraft session (tex_N.bin): tell The Forest a new
			// session started so it forgets the previous numbering instead of loading stale files.
			map.putInt(Proto.OFF_MC + 164, 0);
			map.putInt(Proto.OFF_MC + 176, (int) (System.nanoTime() & 0x7fffffff) | 1);
			heartbeat();
			LOG.info("linked to The Forest");
			return true;
		} catch (Exception e) {
			LOG.warn("link not open yet: {}", e.toString());
			map = null;
			return false;
		}
	}

	public static MappedByteBuffer buffer() {
		return map;
	}

	public static boolean forestClosed() {
		if (map == null) return false;
		long beat = map.getLong(Proto.OFF_HEADER + 16);
		long now = System.currentTimeMillis();
		long age = beat <= 0 ? Long.MAX_VALUE : now - beat;
		// A loading hitch freezes The Forest's heartbeat for a second or two.
		// That is not an exit. The quit flag is the exit; a long silence is a killed process.
		if (age >= 0 && age < 3000) return false;
		boolean quit = map.getInt(Proto.OFF_HEADER + 32) != 0;
		// Loading a save can freeze The Forest for well over 20 s: silence alone is not an exit.
		// Only the quit flag, or The Forest's process actually being gone, is.
		boolean gone = false;
		int pid = map.getInt(Proto.OFF_HEADER + 8);
		if (!quit && age > 5000 && pid != 0) gone = ProcessHandle.of(pid).map(h -> !h.isAlive()).orElse(true);
		if (quit || gone) {
			LOG.info("forest link idle for {} ms, quit flag {}, process gone {}", age == Long.MAX_VALUE ? -1 : age, quit, gone);
			return true;
		}
		return false;
	}

	private static net.minecraft.server.level.ServerPlayer lastPlayer;
	private static Vec3 respawnPos;

	public static void rescue(net.minecraft.server.level.ServerPlayer player) {
		refreshGrid();
		// After a death Minecraft makes a new player at the world spawn, which is the void here.
		// Put it back where it first stood on the island (like a bed at the crash site).
		// A bed (or respawn anchor) wins: Minecraft already put the player there, on real blocks.
		if (lastPlayer != null && player != lastPlayer) {
			if (player.getRespawnConfig() == null && respawnPos != null) {
				player.teleportTo(respawnPos.x, respawnPos.y + 0.05, respawnPos.z);
				LOG.info("respawned on the island at {} {} {}", respawnPos.x, respawnPos.y, respawnPos.z);
			} else {
				LOG.info("respawned at the bed {} {} {}", player.getX(), player.getY(), player.getZ());
			}
			hold(player);
		}
		lastPlayer = player;
		holdTick(player);
		ForestSnapshot forest = readForest();
		if (forest == null) return;
		stepUp(player);
		boolean placed = (forest.flags() & Proto.IN_GAME) != 0 && (forest.flags() & Proto.MENU) == 0;
		// A pause menu or the inventory is not a hand-back: keep settled, just wait.
		if (!placed && forest.teleportSeq() != 0) return;
		// Seq 0 is the plane. Minecraft stays parked until The Forest hands over the beach.
		if (!placed || forest.teleportSeq() == 0) {
			settledSeq = -1;
			if (player.getY() < 0 || !player.isAlive()) {
				if (!player.isAlive()) player.setHealth(player.getMaxHealth());
				player.teleportTo(player.getX(), 80, player.getZ());
				player.setDeltaMovement(Vec3.ZERO);
				player.resetFallDistance();
			}
			return;
		}
		if (forest.teleportSeq() != appliedTeleport) {
			settledSeq = -1;
			maybeTeleport(player);
			return;
		}
		// Arrived. Never pull the body back: that fight is the rollback.
		if (settledSeq == forest.teleportSeq()) {
			// Inside something for half a second (a cave floor met at an angle, the plane's hull
			// arriving after the body): climb out instead of staying stuck.
			if (!player.isSpectator() && !player.level().noCollision(player, player.getBoundingBox().deflate(1.0e-3))) {
				if (++stuckTicks >= 10) { stuckTicks = 0; unstuck(player); }
			} else stuckTicks = 0;
			return;
		}
		double dx = player.getX() - forest.x();
		double dy = player.getY() - forest.y();
		double dz = player.getZ() - forest.z();
		boolean near = dx * dx + dz * dz < 4.0 && dy > -1.25 && dy < 2.5;
		if (near && player.onGround() && gridSeq != 0 && Math.abs(player.getDeltaMovement().y) < 0.08) {
			settledSeq = forest.teleportSeq();
			if (respawnPos == null) respawnPos = player.position();
			starterKit(player);
			LOG.info("Minecraft is standing on the island");
			return;
		}
		if (!player.isAlive()) player.setHealth(player.getMaxHealth());
		player.teleportTo(forest.x(), forest.y() + 0.05, forest.z());
		player.setDeltaMovement(Vec3.ZERO);
		player.resetFallDistance();
	}

	/** The island is a heightmap sampled per block; a 0.6 step stalls on any hillside. */
	public static void stepUp(net.minecraft.world.entity.LivingEntity entity) {
		net.minecraft.world.entity.ai.attributes.AttributeInstance step = entity.getAttribute(net.minecraft.world.entity.ai.attributes.Attributes.STEP_HEIGHT);
		if (step != null && step.getBaseValue() != 1.0) step.setBaseValue(1.0);
	}

	/**
	 * First time on the island: nothing but a redstone torch, The Forest's lighter (held, it
	 * lights the way at night). The rest is found on the island, like The Forest's axe in the plane.
	 */
	public static void starterKit(ServerPlayer player) {
		if (player.entityTags().contains("forestcraft_kit")) return;
		var inv = player.getInventory();
		if (inv.getItem(0).isEmpty()) inv.setItem(0, new net.minecraft.world.item.ItemStack(net.minecraft.world.item.Items.REDSTONE_TORCH, 1));
		player.addTag("forestcraft_kit");
		LOG.info("starter kit given: a redstone torch (the lighter)");
	}

	private static int worldTicks;
	private static long lastTimeSet = Long.MIN_VALUE;

	/**
	 * Minecraft's sky follows The Forest's sun (like SkyCraft drives dayTime from Skyrim's hour),
	 * so the hand and items are lit like the scene, and Minecraft never rains on top of it.
	 */
	public static void syncWorld(ServerPlayer player) {
		if (++worldTicks % 40 != 0) return;
		if (map == null) return;
		float sun = map.getFloat(Proto.OFF_FOREST + 80);
		var server = player.level().getServer();
		if (server == null) return;
		var source = server.createCommandSourceStack().withSuppressedOutput();
		if (!Float.isNaN(sun) && sun >= -90f && sun <= 90f) {
			long time = sun >= 0f ? Math.round(6000.0 - (90.0 - sun) / 90.0 * 6000.0) : Math.round(18000.0 - (90.0 + sun) / 90.0 * 6000.0);
			if (Math.abs(time - lastTimeSet) > 300) {
				server.getCommands().performPrefixedCommand(source, "time set " + time);
				lastTimeSet = time;
			}
		}
		if (worldTicks % 1200 == 0 || worldTicks == 40) server.getCommands().performPrefixedCommand(source, "weather clear 1000000");
	}

	// ---- hold: after a jump to a far place (respawn), The Forest's colliders there (the plane's
	// floor, rocks...) are not known yet. Keep the player still until The Forest says the blocks
	// around it are scanned (Forest block +132), so it can't fall through a floor that isn't there yet.
	private static Vec3 holdAt;
	private static int stuckTicks;
	private static int holdTicks, readyTicks;

	public static void hold(net.minecraft.server.level.ServerPlayer player) {
		// Tell The Forest the body was moved on purpose (respawn): its puppet jumps there too
		// instead of seeing a body "fallen through the ground" and taking it back.
		if (map != null) map.putInt(Proto.OFF_MC + 188, map.getInt(Proto.OFF_MC + 188) + 1);
		holdAt = player.position();
		holdTicks = 0;
		readyTicks = 0;
		player.setDeltaMovement(Vec3.ZERO);
		player.resetFallDistance();
	}

	private static void holdTick(net.minecraft.server.level.ServerPlayer player) {
		if (holdAt == null || map == null) return;
		holdTicks++;
		refreshGrid();
		// Ready means: The Forest has scanned its colliders (the plane's hull, rocks...) around
		// THIS spot. Right after a respawn its flag still describes where the player died.
		boolean here = solids.length > 0
			&& Math.abs(holdAt.x - (solidX + solidSX / 2.0)) < 3.0
			&& Math.abs(holdAt.z - (solidZ + solidSZ / 2.0)) < 3.0
			&& holdAt.y >= solidY && holdAt.y < solidY + solidSY;
		boolean ready = here && map.getInt(Proto.OFF_FOREST + 132) == 1;
		readyTicks = ready && holdTicks > 4 ? readyTicks + 1 : 0;
		if (readyTicks >= 3 || holdTicks > 300) {
			holdAt = null;
			unstuck(player);
			return;
		}
		if (player.position().distanceToSqr(holdAt) > 1.0e-4) player.teleportTo(holdAt.x, holdAt.y, holdAt.z);
		player.setDeltaMovement(Vec3.ZERO);
		player.resetFallDistance();
	}

	/** Inside the plane's hull or a rock once its collisions arrive: climb out, at most 4 blocks. */
	public static void unstuck(net.minecraft.server.level.ServerPlayer player) {
		var level = player.level();
		var box = player.getBoundingBox().deflate(1.0e-3);
		if (level.noCollision(player, box)) return;
		for (int i = 1; i <= 32; i++) {
			double up = i * 0.125;
			if (level.noCollision(player, box.move(0, up, 0))) {
				player.teleportTo(player.getX(), player.getY() + up, player.getZ());
				player.setDeltaMovement(Vec3.ZERO);
				player.resetFallDistance();
				LOG.info("respawn spot was inside something: moved up {} blocks", up);
				return;
			}
		}
	}

	/** A new world is about to open: nothing of the previous one carries over. */
	public static void resetWorldState() {
		respawnPos = null;
		lastPlayer = null;
		holdAt = null;
		settledSeq = -1;
		appliedTeleport = -1;
		clientAppliedTeleport = -1;
	}

	public static void heartbeat() {
		if (map == null) return;
		map.putLong(Proto.OFF_HEADER + 24, System.currentTimeMillis() & 0x7fffffffffffffffL);
	}

	/** The Forest is in a game (menus included): Minecraft draws one frame per Forest frame. */
	public static boolean forestPlaying() {
		if (map == null) return false;
		return (map.getInt(Proto.OFF_FOREST + 4) & Proto.IN_GAME) != 0;
	}

	private static int pacedSeq;

	/**
	 * Wait (at most 50 ms) until The Forest starts a new frame (its block at OFF_FOREST is
	 * rewritten once per frame), so Minecraft renders exactly one frame per Forest frame: the
	 * hand moves at The Forest's pace, without frames skipped or shown twice, and no GPU time
	 * is spent on frames nobody sees.
	 */
	public static void paceToForest() {
		long start = System.nanoTime();
		if (paceWorkFrom != 0) {
			long work = start - paceWorkFrom;
			paceFrames++;
			paceWork += work;
			if (work > paceWorst) paceWorst = work;
			McPerf.endFrame(work);
		}
		if (!forestPlaying()) { paceWorkFrom = 0; return; }
		long deadline = start + 50_000_000L;
		int seq = map.getInt(Proto.OFF_FOREST) & ~1;
		while (seq == pacedSeq && System.nanoTime() < deadline) {
			java.util.concurrent.locks.LockSupport.parkNanos(200_000L);
			seq = map.getInt(Proto.OFF_FOREST) & ~1;
		}
		pacedSeq = seq;
		paceWorkFrom = System.nanoTime();
	}

	// Minecraft's own frame cost (from the end of one wait for The Forest to the next).
	private static long paceWorkFrom, paceWork, paceWorst;
	private static int paceFrames, perfTicks;

	/** Every 30 s in the log: what Minecraft costs, to compare with The Forest's perf line. */
	public static void perfLog(net.minecraft.client.Minecraft mc) {
		if (++perfTicks < 600) return;
		perfTicks = 0;
		StringBuilder sb = new StringBuilder("perf last 30 s: ");
		if (paceFrames > 0) sb.append(paceFrames / 30).append(" frames/s, frame work ")
				.append(String.format(java.util.Locale.ROOT, "%.1f", paceWork / 1e6 / paceFrames)).append(" ms avg, worst ")
				.append(String.format(java.util.Locale.ROOT, "%.0f", paceWorst / 1e6)).append(" ms");
		var server = mc.getSingleplayerServer();
		if (server != null) sb.append(", server tick ").append(String.format(java.util.Locale.ROOT, "%.1f", server.getAverageTickTimeNanos() / 1e6)).append(" ms");
		if (mc.level != null) {
			sb.append(", entities ").append(mc.level.getEntityCount());
			sb.append(", sky darken ").append(mc.level.getSkyDarken());
		}
		sb.append(", particles ").append(mc.particleEngine.countParticles());
		sb.append(", frame ").append(mc.getWindow().getWidth()).append('x').append(mc.getWindow().getHeight());
		sb.append(McPerf.summary());
		LOG.info(sb.toString());
		paceFrames = 0;
		paceWork = paceWorst = 0;
	}

	public static boolean forestInGame() {
		ForestSnapshot snap = readForest();
		return snap != null && (snap.flags() & Proto.IN_GAME) != 0 && (snap.flags() & Proto.MENU) == 0;
	}

	public record ForestSnapshot(int flags, double x, double y, double z, float yaw, float pitch, int teleportSeq, int input, float mouseX, float mouseY, int mouseSeq) {}

	/** What The Forest wants from Minecraft's view: F5 mode, frame size, hotbar slot, 3rd-person distance. */
	public record ForestView(int cameraMode, int frameW, int frameH, int slot, float camDist) {}

	public static ForestView readView() {
		if (map == null && !open()) return null;
		return new ForestView(
			map.getInt(Proto.OFF_FOREST + 60),
			map.getInt(Proto.OFF_FOREST + 64),
			map.getInt(Proto.OFF_FOREST + 68),
			map.getInt(Proto.OFF_FOREST + 72),
			map.getFloat(Proto.OFF_FOREST + 76));
	}

	/** Minecraft's camera feel, every frame: eye height (sneak), FOV (sprint), view bobbing. */
	public static void publishView(float eyeHeight, float fov, float walk, float bob) {
		if (map == null) return;
		map.putFloat(Proto.OFF_MC + 76, eyeHeight);
		map.putFloat(Proto.OFF_MC + 80, fov);
		map.putFloat(Proto.OFF_MC + 84, walk);
		map.putFloat(Proto.OFF_MC + 88, bob);
	}

	public static volatile float frameWalk, frameBob;

	public static ForestSnapshot readForest() {
		if (map == null && !open()) return null;
		for (int n = 0; n < 8; n++) {
			int seq = map.getInt(Proto.OFF_FOREST);
			if (seq == 0) return null;
			if ((seq & 1) != 0) continue;
			int flags = map.getInt(Proto.OFF_FOREST + 4);
			double x = map.getDouble(Proto.OFF_FOREST + 8);
			double y = map.getDouble(Proto.OFF_FOREST + 16);
			double z = map.getDouble(Proto.OFF_FOREST + 24);
			float yaw = map.getFloat(Proto.OFF_FOREST + 32);
			float pitch = map.getFloat(Proto.OFF_FOREST + 36);
			int teleport = map.getInt(Proto.OFF_FOREST + 40);
			int input = map.getInt(Proto.OFF_FOREST + 44);
			float mouseX = map.getFloat(Proto.OFF_FOREST + 48);
			float mouseY = map.getFloat(Proto.OFF_FOREST + 52);
			int mouseSeq = map.getInt(Proto.OFF_FOREST + 56);
			if (map.getInt(Proto.OFF_FOREST) == seq) return new ForestSnapshot(flags, x, y, z, yaw, pitch, teleport, input, mouseX, mouseY, mouseSeq);
		}
		return null;
	}

	public static void publishPlayer(double px, double py, double pz, double x, double y, double z, float yaw, float pitch, boolean onGround, int teleportAck) {
		if (map == null && !open()) return;
		int seq = map.getInt(Proto.OFF_MC) + 1;
		if ((seq & 1) == 0) seq++;
		int flags = Proto.MC_IN_WORLD;
		if (onGround) flags |= Proto.MC_ON_GROUND;
		if (settledSeq != -1) flags |= Proto.MC_SETTLED;
		map.putInt(Proto.OFF_MC, seq);
		map.putInt(Proto.OFF_MC + 4, flags);
		map.putDouble(Proto.OFF_MC + 8, x);
		map.putDouble(Proto.OFF_MC + 16, y);
		map.putDouble(Proto.OFF_MC + 24, z);
		map.putFloat(Proto.OFF_MC + 32, yaw);
		map.putFloat(Proto.OFF_MC + 36, pitch);
		map.putInt(Proto.OFF_MC + 40, teleportAck);
		map.putInt(Proto.OFF_MC + 44, mouseAck);
		// Previous tick position + tick counter: The Forest interpolates between the two every frame.
		map.putDouble(Proto.OFF_MC + 48, px);
		map.putDouble(Proto.OFF_MC + 56, py);
		map.putDouble(Proto.OFF_MC + 64, pz);
		map.putInt(Proto.OFF_MC + 72, ++tickCount);
		map.putInt(Proto.OFF_MC, seq + 1);
		heartbeat();
	}

	public static void maybeTeleport(ServerPlayer player) {
		ForestSnapshot forest = readForest();
		if (forest == null || (forest.flags() & Proto.IN_GAME) == 0) return;
		if (forest.teleportSeq() == appliedTeleport) return;
		player.teleportTo(forest.x(), forest.y() + 0.05, forest.z());
		player.setYRot(forest.yaw() + 180.0f);
		player.setXRot(forest.pitch());
		player.setDeltaMovement(Vec3.ZERO);
		player.resetFallDistance();
		appliedTeleport = forest.teleportSeq;
		LOG.info("teleport to forest feet {}, {}, {}", forest.x(), forest.y(), forest.z());
		// Held still there until The Forest has scanned its colliders around this spot (the
		// plane's cabin floor at the start of a game): it fell through them otherwise.
		hold(player);
	}

	/** The Forest owns the look direction (mouse is read there every frame, no 20 Hz lag). */
	public static void applyLook(net.minecraft.world.entity.player.Player player, ForestSnapshot forest, boolean frame) {
		if (player == null || forest == null) return;
		float target = forest.yaw() + 180.0f;
		float yaw = player.getYRot() + net.minecraft.util.Mth.wrapDegrees(target - player.getYRot());
		float pitch = net.minecraft.util.Mth.clamp(forest.pitch(), -90.0f, 90.0f);
		player.setYRot(yaw);
		player.setXRot(pitch);
		player.setYHeadRot(yaw);
		if (frame) {
			player.yRotO = yaw;
			player.xRotO = pitch;
		}
	}

	public static int teleportAck() {
		return appliedTeleport < 0 ? 0 : appliedTeleport;
	}

	public static int clientAppliedTeleport() {
		return clientAppliedTeleport;
	}

	public static void noteClientTeleport(int seq) {
		clientAppliedTeleport = seq;
	}

	public static int appliedMouse() {
		return appliedMouse;
	}

	public static void ackMouse(int seq) {
		appliedMouse = seq;
		mouseAck = seq;
	}

	// ---- size: Forest units per block, fixed per Minecraft world (OFF_MC+196) -----------------

	private static volatile float worldScale = 1.5f;

	/** The size this world was made with (The Forest reads it at OFF_MC+196). */
	public static void setWorldScale(float k) {
		worldScale = k;
		if (map != null) map.putFloat(Proto.OFF_MC + 196, k);
	}

	/** The size The Forest would like for a new world (config or auto, OFF_FOREST+232). */
	public static float wantedScale() {
		float w = map == null ? 0f : map.getFloat(Proto.OFF_FOREST + 232);
		return w >= 0.5f && w <= 3f ? w : 1.8f;
	}

	/**
	 * Steve's hitbox is narrower when blocks are bigger than 1.5 Forest units, so in The Forest
	 * he stays as wide as at 1.5 (0.9 units): doors, the plane's cabin, cave passages still fit.
	 */
	public static float hitboxWidthFactor() {
		float k = worldScale;
		return k > 1.5f ? Math.max(0.6f, 1.5f / k) : 1f;
	}

	// ---- Minecraft commands typed in The Forest's console ("mc <command>"), OFF 0xA19000 ------

	private static final int OFF_COMMAND = 0xA19000;
	private static int commandSeq = Integer.MIN_VALUE;

	/** Server tick: run the command The Forest's console sent, as the player. */
	/** Where Minecraft writes the last command block it took, so The Forest can send the next one. */
	private static final int COMMAND_ACK = 1016;

	/**
	 * Commands from The Forest's console and the mod menu (F8). One block can hold several
	 * commands, one per line; they run with operator rights (solo, the player's own test tools),
	 * so summon/give/effect work even in a world without cheats.
	 */
	public static void runForestCommand(ServerPlayer player) {
		if (map == null) return;
		int seq = map.getInt(OFF_COMMAND);
		if (commandSeq == Integer.MIN_VALUE) { commandSeq = seq; map.putInt(OFF_COMMAND + COMMAND_ACK, seq); return; }
		if (seq == commandSeq) return;
		commandSeq = seq;
		int len = Math.max(0, Math.min(1000, map.getInt(OFF_COMMAND + 4)));
		byte[] raw = new byte[len];
		map.get(OFF_COMMAND + 8, raw);
		map.putInt(OFF_COMMAND + COMMAND_ACK, seq);
		String text = new String(raw, java.nio.charset.StandardCharsets.UTF_8);
		var server = player.level().getServer();
		if (server == null) return;
		var source = player.createCommandSourceStack()
				.withMaximumPermission(net.minecraft.server.permissions.LevelBasedPermissionSet.GAMEMASTER);
		for (String line : text.split("\n")) {
			String command = line.trim();
			// "quiet:" = ForestCraft's own moves (cave mouths, unstuck): nothing said in the chat.
			boolean quiet = command.startsWith("quiet:");
			if (quiet) command = command.substring(6).trim();
			if (command.startsWith("/")) command = command.substring(1);
			if (command.isEmpty()) continue;
			LOG.info("command from The Forest: /{}", command);
			server.getCommands().performPrefixedCommand(quiet ? source.withSuppressedOutput() : source, command);
		}
	}

	/**
	 * Bit 1: in the caves, bit 2: in a cave entrance (The Forest, OFF_FOREST+212). Bit 4: just out
	 * of a cave but still under the island's surface (a cave mouth under a hill): the island's
	 * ground level with the body and above it is left out (it would wall the player in), what is
	 * under the feet (feet height at OFF_FOREST+244) stays.
	 */
	private static volatile int caveFlags;
	private static volatile float mouthFeet = Float.NaN;

	/** Under the island (caves) or in an entrance hole: the heightmap ground isn't the floor. */
	public static boolean underground() {
		return (caveFlags & 3) != 0;
	}

	/** The island's ground in this block is left out (caves, entrance, or a cave mouth at the body). */
	private static boolean groundOff(int x, int y, int z) {
		if ((caveFlags & 3) != 0) return true;
		if ((caveFlags & 4) == 0 || !(y + 1 > mouthFeet + 0.02)) return false;
		// Only around the player (a mob up on the hill keeps its ground).
		return Math.abs(x - originX - Proto.GRID / 2) <= CAVE_REACH && Math.abs(z - originZ - Proto.GRID / 2) <= CAVE_REACH;
	}

	public static void refreshGrid() {
		if (map == null) return;
		caveFlags = map.getInt(Proto.OFF_FOREST + 212);
		mouthFeet = map.getFloat(Proto.OFF_FOREST + 244);
		refreshSolids();
		refreshGround();
		refreshWater();
		refreshFar();
		int seq = map.getInt(Proto.OFF_GRID);
		if ((seq & 1) != 0 || seq == 0 || seq == gridSeq) return;
		int ox = map.getInt(Proto.OFF_GRID + 4);
		int oz = map.getInt(Proto.OFF_GRID + 8);
		int size = map.getInt(Proto.OFF_GRID + 12);
		if (size != Proto.GRID) return;
		float[] next = new float[Proto.GRID * Proto.GRID];
		int at = Proto.OFF_GRID + 16;
		for (int i = 0; i < next.length; i++) {
			next[i] = map.getFloat(at);
			at += 4;
		}
		if (map.getInt(Proto.OFF_GRID) != seq) return;
		heights = next;
		originX = ox;
		originZ = oz;
		gridSeq = seq;
	}

	private static synchronized void refreshSolids() {
		int seq = map.getInt(Proto.OFF_SOLIDS);
		if ((seq & 1) != 0 || seq == 0 || seq == solidSeq) return;
		int ox = map.getInt(Proto.OFF_SOLIDS + 4);
		int oy = map.getInt(Proto.OFF_SOLIDS + 8);
		int oz = map.getInt(Proto.OFF_SOLIDS + 12);
		int sx = map.getInt(Proto.OFF_SOLIDS + 16);
		int sy = map.getInt(Proto.OFF_SOLIDS + 20);
		int sz = map.getInt(Proto.OFF_SOLIDS + 24);
		if (sx <= 0 || sy <= 0 || sz <= 0 || sx * sy * sz > Proto.SOLIDS_MAX) return;
		int count = sx * sy * sz;
		long[] raw = new long[count * WORDS];
		for (int i = 0; i < raw.length; i++) raw[i] = map.getLong(Proto.OFF_SOLIDS + 32 + i * 8);
		if (map.getInt(Proto.OFF_SOLIDS) != seq) return;
		net.minecraft.world.phys.shapes.VoxelShape[] next = new net.minecraft.world.phys.shapes.VoxelShape[count];
		if (SHAPES.size() > 60000) SHAPES.clear();
		for (int i = 0; i < count; i++) {
			int at = i * WORDS;
			boolean any = false;
			for (int w = 0; w < WORDS && !any; w++) any = raw[at + w] != 0L;
			if (!any) continue;
			long[] words = java.util.Arrays.copyOfRange(raw, at, at + WORDS);
			next[i] = SHAPES.computeIfAbsent(new MaskKey(words), k -> buildShape(k.words()));
		}
		solidX = ox; solidY = oy; solidZ = oz;
		solidSX = sx; solidSY = sy; solidSZ = sz;
		solids = next;
		solidSeq = seq;
	}

	/**
	 * The Forest colliders inside this block at 1/8 resolution (like a slab or stair shape), or null.
	 * Same idea as SkyCraft's collision field: tight places (the plane's door) stay passable.
	 */
	public static net.minecraft.world.phys.shapes.VoxelShape solidShape(int x, int y, int z) {
		net.minecraft.world.phys.shapes.VoxelShape[] cells = solids;
		int sx = solidSX, sy = solidSY, sz = solidSZ;
		int lx = x - solidX, ly = y - solidY, lz = z - solidZ;
		if (lx < 0 || ly < 0 || lz < 0 || lx >= sx || ly >= sy || lz >= sz) return null;
		int i = (ly * sz + lz) * sx + lx;
		return i < cells.length ? cells[i] : null;
	}

	/** bit = y*64 + z*8 + x: one long per Y layer of 8x8 eighth-cells. */
	private static net.minecraft.world.phys.shapes.VoxelShape buildShape(long[] words) {
		var bits = new net.minecraft.world.phys.shapes.BitSetDiscreteVoxelShape(8, 8, 8);
		for (int y = 0; y < 8; y++) {
			long w = words[y];
			while (w != 0L) {
				int b = Long.numberOfTrailingZeros(w);
				w &= w - 1;
				bits.fill(b & 7, y, b >>> 3);
			}
		}
		return new net.minecraft.world.phys.shapes.CubeVoxelShape(bits).optimize();
	}

	/** Everything of The Forest in this block (island surface + trees/rocks/plane), or null. Used for aiming. */
	public static net.minecraft.world.phys.shapes.VoxelShape forestShape(int x, int y, int z) {
		net.minecraft.world.phys.shapes.VoxelShape shape = null;
		double top = surfaceIn(x, y, z);
		if (!Double.isNaN(top)) shape = net.minecraft.world.phys.shapes.Shapes.box(0, 0, 0, 1, top, 1);
		net.minecraft.world.phys.shapes.VoxelShape solid = solidShape(x, y, z);
		if (solid != null) shape = shape == null ? solid : net.minecraft.world.phys.shapes.Shapes.or(shape, solid);
		return shape;
	}

	/**
	 * What the crosshair hits of The Forest in this block: trees/rocks/plane at 1/8, and the
	 * island's ground as it really is in that block (4x4 columns, 1/8 high), so the cell outlined
	 * and dug is the one the surface is in where you look. Dug or revealed cells are not ground.
	 */
	public static net.minecraft.world.phys.shapes.VoxelShape aimShape(int x, int y, int z) {
		net.minecraft.world.phys.shapes.VoxelShape shape = solidShape(x, y, z);
		net.minecraft.world.phys.shapes.VoxelShape ground = groundShape(x, y, z);
		if (ground == null) return shape;
		return shape == null ? ground : net.minecraft.world.phys.shapes.Shapes.or(shape, ground);
	}

	private static final java.util.concurrent.ConcurrentHashMap<Long, net.minecraft.world.phys.shapes.VoxelShape> GROUND_SHAPES = new java.util.concurrent.ConcurrentHashMap<>();

	private static net.minecraft.world.phys.shapes.VoxelShape groundShape(int x, int y, int z) {
		if (groundOff(x, y, z) || TerrainDig.isConverted(x, y, z)) return null;
		double hc = heightAt(x, z);
		if (Double.isNaN(hc)) return null;
		double min = minHeight(x, z), max = maxHeight(x, z);
		if (Double.isNaN(min) || Double.isNaN(max)) { min = hc; max = hc; }
		if (y >= max - 1.0e-3 || y < Math.floor(hc) - 8) return null;
		if (y + 1 <= min + 1.0e-3) return net.minecraft.world.phys.shapes.Shapes.block();
		long key = 0L;
		boolean any = false, full = true;
		for (int sz = 0; sz < 4; sz++) {
			for (int sx = 0; sx < 4; sx++) {
				double h = surfaceAt(x + (sx + 0.5) / 4.0, z + (sz + 0.5) / 4.0);
				if (Double.isNaN(h)) h = hc;
				int q = (int) Math.ceil((h - y) * 8.0 - 0.05);
				q = Math.max(0, Math.min(8, q));
				if (q > 0) any = true;
				if (q < 8) full = false;
				key |= (long) q << ((sz * 4 + sx) * 4);
			}
		}
		// A sliver of ground in this cell (the surface only just enters it): still aimable.
		if (!any) return max > y + 0.01 ? net.minecraft.world.phys.shapes.Shapes.box(0, 0, 0, 1, 0.125, 1) : null;
		if (full) return net.minecraft.world.phys.shapes.Shapes.block();
		final long mask = key;
		return GROUND_SHAPES.computeIfAbsent(mask, k -> {
			var bits = new net.minecraft.world.phys.shapes.BitSetDiscreteVoxelShape(4, 8, 4);
			for (int i = 0; i < 16; i++) {
				int q = (int) ((k >>> (i * 4)) & 15L);
				for (int yy = 0; yy < q; yy++) bits.fill(i & 3, yy, i >> 2);
			}
			return new net.minecraft.world.phys.shapes.CubeVoxelShape(bits).optimize();
		});
	}

	/** A cell of the island's ground only partly under the surface (not dug, not revealed). */
	public static boolean isPartialGround(int x, int y, int z) {
		if (groundOff(x, y, z) || TerrainDig.isConverted(x, y, z)) return false;
		double hc = heightAt(x, z), min = minHeight(x, z), max = maxHeight(x, z);
		if (Double.isNaN(hc) || Double.isNaN(min) || Double.isNaN(max)) return false;
		return y < max - 1.0e-3 && y + 1 > min - 0.02 && y >= Math.floor(hc) - 8;
	}

	/** Y of the island's top block in this column (its top face is the rounded surface), or MIN_VALUE. */
	public static int groundTop(int x, int z) {
		double h = heightAt(x, z);
		return Double.isNaN(h) ? Integer.MIN_VALUE : (int) Math.round(h) - 1;
	}

	/** The island's original surface (MC units) at the centre of this column, or NaN outside the grid. */
	public static double heightAt(int x, int z) {
		int lx = x - originX;
		int lz = z - originZ;
		if (lx < 0 || lz < 0 || lx >= Proto.GRID || lz >= Proto.GRID || gridSeq == 0) return Double.NaN;
		float h = heights[lz * Proto.GRID + lx];
		return Float.isNaN(h) ? Double.NaN : h;
	}

	/**
	 * Top of the Forest surface inside this block, or NaN (none, or the cell was dug/revealed).
	 * Around the player from the detailed grid; further out (animals and monsters) from the
	 * coarse far grid. In the caves only the island's ground around the player is ignored:
	 * a cow up on the surface keeps standing on it.
	 */
	public static double surfaceIn(int x, int y, int z) {
		int lx = x - originX;
		int lz = z - originZ;
		boolean fine = gridSeq != 0 && lx >= 0 && lz >= 0 && lx < Proto.GRID && lz < Proto.GRID;
		float h;
		if (fine) h = heights[lz * Proto.GRID + lx];
		else {
			double f = farHeight(x, z);
			if (Double.isNaN(f)) return Double.NaN;
			h = (float) f;
		}
		if (Float.isNaN(h)) return Double.NaN;
		int block = (int) Math.floor(h - 1e-4);
		if (y > block || y < block - 8) return Double.NaN;
		if (groundOff(x, y, z) && fine && Math.abs(lx - Proto.GRID / 2) <= CAVE_REACH && Math.abs(lz - Proto.GRID / 2) <= CAVE_REACH) return Double.NaN;
		if (TerrainDig.isConverted(x, y, z)) return Double.NaN;
		if (y < block) return 1.0;
		double top = h - block;
		if (top < 1.0e-3) return 1.0;
		return Math.min(1.0, top);
	}

	/** In the caves, the island's ground is ignored this far (blocks) around the player. */
	private static final int CAVE_REACH = 10;

	// ---- the island's surface far around the player, for mobs (The Forest, OFF_FAR) -----------

	private static final int OFF_FAR = 0x9F0000;
	private static final int FAR = 128;
	private static float[] far = new float[0];
	private static int farX, farZ, farSeq;

	private static void refreshFar() {
		int seq = map.getInt(OFF_FAR);
		if ((seq & 1) != 0 || seq == 0 || seq == farSeq) return;
		if (map.getInt(OFF_FAR + 12) != FAR) return;
		int ox = map.getInt(OFF_FAR + 4), oz = map.getInt(OFF_FAR + 8);
		float[] next = new float[FAR * FAR];
		int at = OFF_FAR + 16;
		for (int i = 0; i < next.length; i++) { next[i] = map.getFloat(at); at += 4; }
		if (map.getInt(OFF_FAR) != seq) return;
		far = next;
		farX = ox; farZ = oz;
		farSeq = seq;
	}

	/** The island's original surface at the centre of this column from the far grid, or NaN. */
	public static double farHeight(int x, int z) {
		float[] f = far;
		int lx = x - farX, lz = z - farZ;
		if (f.length == 0 || lx < 0 || lz < 0 || lx >= FAR || lz >= FAR) return Double.NaN;
		float h = f[lz * FAR + lx];
		return Float.isNaN(h) ? Double.NaN : h;
	}

	/**
	 * What The Forest has in this air block, as a Minecraft block for its sounds and particles
	 * (footsteps, landing, sprint dust): grass on top of the island, sand on beaches, dirt under,
	 * stone deep down and for rocks and caves. Null when there is nothing of The Forest there.
	 */
	public static net.minecraft.world.level.block.state.BlockState groundState(int x, int y, int z) {
		if (map == null) return null;
		double top = surfaceIn(x, y, z);
		if (!Double.isNaN(top)) {
			var b = TerrainDig.groundBlock(x, y, z);
			if (b.is(net.minecraft.world.level.block.Blocks.DIRT)) {
				double h = heightAt(x, z);
				if (Double.isNaN(h)) h = farHeight(x, z);
				if (!Double.isNaN(h) && y == (int) Math.floor(h - 1e-4)) return net.minecraft.world.level.block.Blocks.GRASS_BLOCK.defaultBlockState();
			}
			return b;
		}
		if (solidShape(x, y, z) != null) return net.minecraft.world.level.block.Blocks.STONE.defaultBlockState();
		return null;
	}

	/** Y of the island's top ground cell in this column (near or far grid), or MIN_VALUE. */
	public static int groundTopAny(int x, int z) {
		double h = heightAt(x, z);
		if (Double.isNaN(h)) h = farHeight(x, z);
		return Double.isNaN(h) ? Integer.MIN_VALUE : (int) Math.floor(h - 1e-4);
	}

	/** Minecraft knows the island's ground in this column (near or far grid). */
	public static boolean knownGround(int x, int z) {
		return !Double.isNaN(heightAt(x, z)) || !Double.isNaN(farHeight(x, z));
	}

	/** Highest point of The Forest (ground or tree/rock/plane) inside this block, 0..1, or NaN. */
	public static double forestTop(int x, int y, int z) {
		double top = surfaceIn(x, y, z);
		net.minecraft.world.phys.shapes.VoxelShape solid = solidShape(x, y, z);
		if (solid != null && !solid.isEmpty()) {
			double s = solid.max(net.minecraft.core.Direction.Axis.Y);
			top = Double.isNaN(top) ? s : Math.max(top, s);
		}
		return top;
	}

	// ---- the original surface in detail (The Forest, OFF_GROUND) --------------------------------

	private static final int OFF_GROUND = 0xA30000;
	private static float[] corners = new float[(Proto.GRID + 1) * (Proto.GRID + 1)];
	private static float[] mins = new float[Proto.GRID * Proto.GRID];
	private static float[] maxs = new float[Proto.GRID * Proto.GRID];
	private static byte[] kinds = new byte[Proto.GRID * Proto.GRID];
	private static int groundX, groundZ, groundSeq;

	private static void refreshGround() {
		int seq = map.getInt(OFF_GROUND);
		if ((seq & 1) != 0 || seq == 0 || seq == groundSeq) return;
		int ox = map.getInt(OFF_GROUND + 4), oz = map.getInt(OFF_GROUND + 8);
		if (map.getInt(OFF_GROUND + 12) != Proto.GRID) return;
		int n = Proto.GRID + 1;
		float[] c = new float[n * n], lo = new float[Proto.GRID * Proto.GRID], hi = new float[Proto.GRID * Proto.GRID];
		int at = OFF_GROUND + 16;
		for (int i = 0; i < c.length; i++) { c[i] = map.getFloat(at); at += 4; }
		for (int i = 0; i < lo.length; i++) { lo[i] = map.getFloat(at); at += 4; }
		for (int i = 0; i < hi.length; i++) { hi[i] = map.getFloat(at); at += 4; }
		byte[] k = new byte[Proto.GRID * Proto.GRID];
		map.get(OFF_GROUND + 0x3200, k);
		if (map.getInt(OFF_GROUND) != seq) return;
		corners = c; mins = lo; maxs = hi; kinds = k;
		groundX = ox; groundZ = oz;
		groundSeq = seq;
	}

	/** The island is sand in this column (beaches), as The Forest paints it. */
	public static boolean sandAt(int x, int z) {
		int lx = x - groundX, lz = z - groundZ;
		if (groundSeq == 0 || lx < 0 || lz < 0 || lx >= Proto.GRID || lz >= Proto.GRID) return false;
		return kinds[lz * Proto.GRID + lx] == 1;
	}

	/** Lowest point of the original surface over this column, or NaN. */
	public static double minHeight(int x, int z) {
		int lx = x - groundX, lz = z - groundZ;
		// Beyond the detailed grid around the player (a TNT lit from afar): the coarse far grid,
		// or the crater's cells there were "no ground" and left without a floor.
		if (groundSeq == 0 || lx < 0 || lz < 0 || lx >= Proto.GRID || lz >= Proto.GRID) return farHeight(x, z);
		float v = mins[lz * Proto.GRID + lx];
		return Float.isNaN(v) ? Double.NaN : v;
	}

	/** Highest point of the original surface over this column, or NaN. */
	public static double maxHeight(int x, int z) {
		int lx = x - groundX, lz = z - groundZ;
		if (groundSeq == 0 || lx < 0 || lz < 0 || lx >= Proto.GRID || lz >= Proto.GRID) return farHeight(x, z);
		float v = maxs[lz * Proto.GRID + lx];
		return Float.isNaN(v) ? Double.NaN : v;
	}

	/** The original surface at any point (bilinear between block corners), or NaN. */
	public static double surfaceAt(double x, double z) {
		int bx = (int) Math.floor(x), bz = (int) Math.floor(z);
		int lx = bx - groundX, lz = bz - groundZ;
		if (groundSeq == 0 || lx < 0 || lz < 0 || lx >= Proto.GRID || lz >= Proto.GRID) return Double.NaN;
		int n = Proto.GRID + 1;
		float[] c = corners;
		double h00 = c[lz * n + lx], h10 = c[lz * n + lx + 1], h01 = c[(lz + 1) * n + lx], h11 = c[(lz + 1) * n + lx + 1];
		double fx = x - bx, fz = z - bz;
		return (h00 * (1 - fx) + h10 * fx) * (1 - fz) + (h01 * (1 - fx) + h11 * fx) * fz;
	}

	// ---- The Forest's water (lakes, sea) around the player (OFF_WATER) ----------------------

	private static final int OFF_WATER = 0xA34400;
	private static float[] waterTop = new float[0], waterBottom = new float[0];
	private static int waterX, waterZ, waterSeq;

	private static void refreshWater() {
		int seq = map.getInt(OFF_WATER);
		if ((seq & 1) != 0 || seq == 0 || seq == waterSeq) return;
		int ox = map.getInt(OFF_WATER + 4), oz = map.getInt(OFF_WATER + 8);
		if (map.getInt(OFF_WATER + 12) != Proto.GRID) return;
		int n = Proto.GRID * Proto.GRID;
		float[] top = new float[n], bottom = new float[n];
		int at = OFF_WATER + 16;
		for (int i = 0; i < n; i++) { top[i] = map.getFloat(at); at += 4; }
		for (int i = 0; i < n; i++) { bottom[i] = map.getFloat(at); at += 4; }
		if (map.getInt(OFF_WATER) != seq) return;
		boolean any = false;
		for (float t : top) if (!Float.isNaN(t)) { any = true; break; }
		waterTop = any ? top : new float[0];
		waterBottom = bottom;
		waterX = ox; waterZ = oz;
		waterSeq = seq;
	}

	/** How full of The Forest's water this block is (1 under the surface, less in the top one). */
	public static float waterFill(int x, int y, int z) {
		float[] top = waterTop;
		int lx = x - waterX, lz = z - waterZ;
		if (top.length == 0 || lx < 0 || lz < 0 || lx >= Proto.GRID || lz >= Proto.GRID) return 1f;
		float surface = top[lz * Proto.GRID + lx];
		return Float.isNaN(surface) ? 1f : Math.max(0f, Math.min(1f, surface - y));
	}

	public static boolean anyWaterIn(int x0, int y0, int z0, int x1, int y1, int z1) {
		if (waterTop.length == 0) return false;
		for (int x = x0; x <= x1; x++)
			for (int z = z0; z <= z1; z++)
				for (int y = y0; y <= y1; y++)
					if (waterAt(x, y, z)) return true;
		return false;
	}

	/**
	 * Water of The Forest in this block: under the surface (a block at least half under it),
	 * above the volume's bottom, and not inside the island's ground.
	 */
	public static boolean waterAt(int x, int y, int z) {
		float[] top = waterTop;
		if (top.length == 0) return false;
		int lx = x - waterX, lz = z - waterZ;
		if (lx < 0 || lz < 0 || lx >= Proto.GRID || lz >= Proto.GRID) return false;
		int i = lz * Proto.GRID + lx;
		float surface = top[i];
		if (Float.isNaN(surface) || y + 0.12 > surface) return false;
		// The Forest's water volumes are often shallow boxes under the surface: outside the caves
		// the water goes all the way down to the lake bed, or a swimmer sank out of it.
		if ((caveFlags & 3) != 0 && y + 1 <= waterBottom[i]) return false;
		if ((caveFlags & 3) == 0) {
			double hc = heightAt(x, z);
			if (!Double.isNaN(hc)) {
				if (hc >= surface) return false; // dry land (the sea's volume spans the island)
				if (y + 1 <= hc && !TerrainDig.isConverted(x, y, z)) return false; // the lake bed
			}
		}
		return true;
	}

	// ---- ropes and climbable walls of The Forest (OFF_ROPES): ladders for Minecraft ----------

	private static final int OFF_ROPES = 0xA36500;

	private static long ropeLoggedAt;

	/** Said in the log (at most every 5 s) when the player takes hold of a rope, and where. */
	public static void ropeHeld(double x, double y, double z) {
		long now = System.currentTimeMillis();
		if (now - ropeLoggedAt < 5000) return;
		ropeLoggedAt = now;
		LOG.info("holding a rope of The Forest at {} {} {}", Math.round(x * 10) / 10.0, Math.round(y * 10) / 10.0, Math.round(z * 10) / 10.0);
	}

	/** Within reach of one of The Forest's ropes: climbs like a ladder. */
	public static boolean ropeAt(double x, double y, double z) {
		return ropeAxis(x, y, z) != null;
	}

	/**
	 * The rope within reach at this height, as {x, z, t}: the point of the rope (from its bottom to
	 * its top, t from 0 to 1) level with the feet, or null. Entries: bottom (x, z, y) then top.
	 */
	public static double[] ropeAxis(double x, double y, double z) {
		if (map == null) return null;
		int n = map.getInt(OFF_ROPES);
		if (n <= 0 || n > 64) return null;
		for (int i = 0; i < n; i++) {
			int at = OFF_ROPES + 16 + i * 24;
			double y0 = map.getFloat(at + 8), y1 = map.getFloat(at + 20);
			if (y < y0 || y > y1) continue;
			double t = y1 - y0 < 1.0e-3 ? 0.0 : Math.max(0.0, Math.min(1.0, (y - y0) / (y1 - y0)));
			double ax = map.getFloat(at) + (map.getFloat(at + 12) - map.getFloat(at)) * t;
			double az = map.getFloat(at + 4) + (map.getFloat(at + 16) - map.getFloat(at + 4)) * t;
			double dx = ax - x, dz = az - z;
			if (dx * dx + dz * dz > 1.2 * 1.2) continue;
			return new double[] { ax, az, t };
		}
		return null;
	}
}
