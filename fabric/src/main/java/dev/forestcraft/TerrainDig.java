package dev.forestcraft;

import java.nio.MappedByteBuffer;
import net.minecraft.client.Minecraft;
import net.minecraft.core.BlockPos;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.world.item.ItemStack;
import net.minecraft.world.item.Items;
import net.minecraft.world.level.block.Blocks;
import net.minecraft.world.level.block.state.BlockState;

/**
 * Digging into the island, the SkyCraft way. The Forest's ground is a heightmap, not blocks;
 * cells (blocks) of it are taken out one at a time:
 * <ul>
 * <li><b>dug</b>: mining the ground takes out the cell the crosshair is on. Nothing of The
 * Forest's ground is left in it (no collision here, The Forest pushes its terrain under it and
 * draws the surface around cut exactly along the cell).</li>
 * <li><b>revealed</b>: the cells next to a dug one that are wholly under the surface become real
 * blocks (dirt, stone deeper), so digging on goes on as ordinary Minecraft mining. Cells only
 * partly under the surface stay The Forest's; it draws their part under the surface as dirt
 * faces cut along the surface ("half blocks"), so a hole is always closed.</li>
 * </ul>
 * A revealed block that is broken (by anything) becomes a dug cell too. Every converted cell is
 * kept with the world (forestcraft_ground.txt) and the dug ones are sent to The Forest again on
 * every link.
 */
public final class TerrainDig {
	public static final int OFF_DIG = 0xA20000;
	public static final int RING = 1024;
	public static final int ENTRY = 16;

	/** Cells no longer The Forest's ground (dug or revealed). Read by collisions on both threads. */
	private static final java.util.Set<Long> converted = java.util.concurrent.ConcurrentHashMap.newKeySet();
	/** Cells emptied of The Forest's ground: sent to The Forest. */
	private static final java.util.Set<Long> dug = java.util.concurrent.ConcurrentHashMap.newKeySet();
	private static final java.util.concurrent.ConcurrentLinkedQueue<Long> toForest = new java.util.concurrent.ConcurrentLinkedQueue<>();
	private static final java.util.concurrent.ConcurrentLinkedQueue<Long> emptied = new java.util.concurrent.ConcurrentLinkedQueue<>();
	private static final java.util.concurrent.ConcurrentLinkedQueue<Long> filled = new java.util.concurrent.ConcurrentLinkedQueue<>();
	private static final StringBuilder unsaved = new StringBuilder();
	private static Object loadedFor;
	private static java.nio.file.Path saveFile;
	private static int written = Integer.MIN_VALUE;

	private static long digKey = Long.MIN_VALUE;
	private static int digTicks;
	private static int lastLogSeq = Integer.MIN_VALUE;

	private TerrainDig() {}

	public static boolean isConverted(int x, int y, int z) {
		return !converted.isEmpty() && converted.contains(BlockPos.asLong(x, y, z));
	}

	/** Wholly under the original surface (same rule and numbers as DigWorld.Revealed in The Forest). */
	public static boolean whollyInside(int x, int y, int z) {
		double min = ForestLink.minHeight(x, z);
		return !Double.isNaN(min) && y + 1 <= min - 0.02;
	}

	/** Deep enough for stone (same rule as DigWorld.MaterialOf in The Forest). */
	private static boolean deep(int x, int y, int z) {
		double hc = ForestLink.heightAt(x, z);
		return !Double.isNaN(hc) && y + 1 <= hc - 3.0;
	}

	/** What the island is made of in this cell: stone deep down, sand on beaches, dirt elsewhere. */
	public static BlockState groundBlock(int x, int y, int z) {
		if (deep(x, y, z)) return Blocks.STONE.defaultBlockState();
		return ForestLink.sandAt(x, z) ? Blocks.SAND.defaultBlockState() : Blocks.DIRT.defaultBlockState();
	}

	/** What digging that cell gives: cobblestone for stone, the block itself otherwise. */
	private static ItemStack dropOf(BlockState state) {
		if (state.is(Blocks.STONE)) return new ItemStack(Items.COBBLESTONE, 1);
		return new ItemStack(state.getBlock().asItem(), 1);
	}

	/** Attack released or aimed elsewhere: the cracks go too. */
	public static void stop(Minecraft minecraft) {
		if (digKey != Long.MIN_VALUE && minecraft.level != null && minecraft.player != null)
			minecraft.level.destroyBlockProgress(minecraft.player.getId(), BlockPos.of(digKey), -1);
		digKey = Long.MIN_VALUE;
		digTicks = 0;
	}

	/** Client tick while attack is held on the island's ground at pos: mine it like dirt. */
	public static void request(Minecraft minecraft, BlockPos pos) {
		long key = pos.asLong();
		if (key != digKey) { stop(minecraft); digKey = key; digTicks = 0; }
		if (digTicks < 0) return;
		digTicks++;
		if (minecraft.player == null || minecraft.level == null) return;
		// Mined like the block it is (dirt, stone deeper): vanilla's speed rules, tool and all.
		BlockState state = groundBlock(pos.getX(), pos.getY(), pos.getZ());
		ItemStack tool = minecraft.player.getMainHandItem();
		boolean correct = !state.requiresCorrectToolForDrops() || tool.isCorrectToolForDrops(state);
		float speed = Math.max(0.1f, tool.getDestroySpeed(state));
		float hardness = state.getDestroySpeed(minecraft.level, pos);
		int needed = Math.max(1, (int) Math.ceil(hardness * (correct ? 30f : 100f) / speed));
		if (minecraft.level != null) minecraft.level.destroyBlockProgress(minecraft.player.getId(), pos, Math.min(9, digTicks * 10 / needed));
		if (minecraft.hitResult instanceof net.minecraft.world.phys.BlockHitResult hit) crack(minecraft, pos, state, hit.getDirection());
		// The knock of each blow, every 4 ticks as Minecraft does while mining its own blocks.
		if (digTicks % 4 == 1) {
			var sound = state.getSoundType();
			minecraft.level.playLocalSound(pos.getX() + 0.5, pos.getY() + 0.5, pos.getZ() + 0.5, sound.getHitSound(),
				net.minecraft.sounds.SoundSource.BLOCKS, (sound.getVolume() + 1f) / 8f, sound.getPitch() * 0.5f, false);
		}
		if (digTicks < needed) return;
		digTicks = -1; // done with this cell until the aim moves
		if (minecraft.level != null) minecraft.level.destroyBlockProgress(minecraft.player.getId(), pos, -1);
		var server = minecraft.getSingleplayerServer();
		if (server == null || minecraft.player == null) return;
		BlockPos cell = pos.immutable();
		// Break burst and sound right away, here (the server sends them to everyone else).
		minecraft.level.levelEvent(minecraft.player, 2001, cell, net.minecraft.world.level.block.Block.getId(state));
		java.util.UUID id = minecraft.player.getUUID();
		server.execute(() -> dig(server, cell, id, correct));
	}

	/**
	 * The little chips flying off the face being mined, every tick, as Minecraft does for its
	 * own blocks (ClientLevel.addBreakingBlockEffect, which can't: the block there is air).
	 */
	private static void crack(Minecraft minecraft, BlockPos pos, BlockState state, net.minecraft.core.Direction face) {
		var level = minecraft.level;
		if (level == null) return;
		net.minecraft.world.phys.shapes.VoxelShape shape = ForestLink.aimShape(pos.getX(), pos.getY(), pos.getZ());
		net.minecraft.world.phys.AABB b = shape == null || shape.isEmpty() ? new net.minecraft.world.phys.AABB(0, 0, 0, 1, 1, 1) : shape.bounds();
		var random = level.getRandom();
		double x = pos.getX() + random.nextDouble() * Math.max(0, b.maxX - b.minX - 0.2) + 0.1 + b.minX;
		double y = pos.getY() + random.nextDouble() * Math.max(0, b.maxY - b.minY - 0.2) + 0.1 + b.minY;
		double z = pos.getZ() + random.nextDouble() * Math.max(0, b.maxZ - b.minZ - 0.2) + 0.1 + b.minZ;
		switch (face) {
			case DOWN -> y = pos.getY() + b.minY - 0.1;
			case UP -> y = pos.getY() + b.maxY + 0.1;
			case NORTH -> z = pos.getZ() + b.minZ - 0.1;
			case SOUTH -> z = pos.getZ() + b.maxZ + 0.1;
			case WEST -> x = pos.getX() + b.minX - 0.1;
			case EAST -> x = pos.getX() + b.maxX + 0.1;
		}
		minecraft.particleEngine.add(new net.minecraft.client.particle.TerrainParticle(level, x, y, z, 0, 0, 0, state, pos).setPower(0.2f).scale(0.6f));
	}

	/** Server thread: the cell's ground is gone; its neighbours wholly under the surface become blocks. */
	private static void dig(net.minecraft.server.MinecraftServer server, BlockPos cell, java.util.UUID id, boolean harvest) {
		ensureLoaded(server);
		var player = server.getPlayerList().getPlayer(id);
		if (player == null) return;
		ServerLevel level = player.level();
		if (!level.getBlockState(cell).isAir()) return;
		long key = cell.asLong();
		if (!converted.add(key)) return;
		BlockState was = groundBlock(cell.getX(), cell.getY(), cell.getZ());
		level.levelEvent(player, 2001, cell, net.minecraft.world.level.block.Block.getId(was)); // particles + sound for the others
		level.getPathTypeCache().invalidate(cell); // mobs: no ground here any more
		if (harvest) {
			ItemStack drop = dropOf(was);
			if (!player.getInventory().add(drop)) player.drop(drop, false);
		}
		int revealed = reveal(level, cell);
		// The Forest sinks its ground there once these blocks have reached it (no see-through).
		fresh.put(key, 1 + revealed);
		markDug(key, cell);
		// Sand or gravel sitting on this ground has nothing under it now: let it fall.
		BlockPos over = cell.above();
		BlockState onTop = level.getBlockState(over);
		if (onTop.getBlock() instanceof net.minecraft.world.level.block.FallingBlock) level.scheduleTick(over, onTop.getBlock(), 2);
		// The ground's surface may just enter the cell above: a thin sliver left floating over
		// the hole. Half a block of ground or less there goes with the cell under it.
		BlockPos above = cell.above();
		double top = ForestLink.maxHeight(above.getX(), above.getZ());
		long up = above.asLong();
		if (!Double.isNaN(top) && top > above.getY() && top < above.getY() + 0.5 && !converted.contains(up) && level.getBlockState(above).isAir()) {
			converted.add(up);
			int more = reveal(level, above);
			fresh.put(up, 1 + Math.max(revealed, more));
			markDug(up, above);
		}
	}

	private static void markDug(long key, BlockPos cell) {
		if (!dug.add(key)) return;
		toForest.add(key);
		save("d", cell);
	}

	/** Cells dug just now (not loaded from the save): how many blocks each revealed, plus one. */
	private static final java.util.concurrent.ConcurrentHashMap<Long, Integer> fresh = new java.util.concurrent.ConcurrentHashMap<>();

	private static int reveal(ServerLevel level, BlockPos cell) {
		int revealed = 0;
		for (net.minecraft.core.Direction d : net.minecraft.core.Direction.values()) {
			BlockPos n = cell.relative(d);
			long key = n.asLong();
			if (converted.contains(key) || !whollyInside(n.getX(), n.getY(), n.getZ())) continue;
			converted.add(key);
			save("r", n);
			if (!level.getBlockState(n).isAir()) continue; // something is already there
			BlockState state = groundBlock(n.getX(), n.getY(), n.getZ());
			level.setBlock(n, state, 2 | 16);
			revealed++;
		}
		ForestLink.LOG.info("dug {} {} {}: {} blocks revealed", cell.getX(), cell.getY(), cell.getZ(), revealed);
		return revealed;
	}

	/** Server: a block changed (ServerLevel mixin). A revealed block gone means its cell is open now. */
	public static void blockChanged(BlockPos pos, BlockState old, BlockState now) {
		// A block placed into a half block of ground: that ground goes, like dug out.
		if (old.isAir() && !now.isAir() && now.getFluidState().isEmpty() && !(now.getBlock() instanceof net.minecraft.world.level.block.BaseFireBlock)
			&& ForestLink.isPartialGround(pos.getX(), pos.getY(), pos.getZ())) {
			filled.add(pos.asLong());
			return;
		}
		if (old.isAir() || !now.isAir() || converted.isEmpty()) return;
		long key = pos.asLong();
		if (converted.contains(key) && !dug.contains(key)) emptied.add(key);
	}

	/** Server tick: open what was emptied, save what changed. */
	public static void serverTick(ServerLevel level) {
		ensureLoaded(level.getServer());
		Long key;
		while ((key = filled.poll()) != null) {
			BlockPos pos = BlockPos.of(key);
			if (level.getBlockState(pos).isAir() || !converted.add(key)) continue;
			markDug(key, pos);
			reveal(level, pos);
		}
		while ((key = emptied.poll()) != null) {
			BlockPos pos = BlockPos.of(key);
			if (dug.contains(key) || !level.getBlockState(pos).isAir()) continue;
			markDug(key, pos);
			reveal(level, pos);
		}
		flush();
	}

	// ---- with the world ------------------------------------------------------------------------

	private static synchronized void ensureLoaded(net.minecraft.server.MinecraftServer server) {
		if (server == null || loadedFor == server) return;
		loadedFor = server;
		converted.clear();
		dug.clear();
		synchronized (unsaved) { unsaved.setLength(0); }
		saveFile = server.getWorldPath(net.minecraft.world.level.storage.LevelResource.ROOT).resolve("forestcraft_ground.txt");
		int count = 0;
		try {
			if (java.nio.file.Files.isRegularFile(saveFile)) {
				for (String line : java.nio.file.Files.readAllLines(saveFile)) count += parse(line, false);
			}
		} catch (Exception e) {
			ForestLink.LOG.warn("dug ground not loaded: {}", e.toString());
		}
		toForest.clear();
		toForest.addAll(dug);
		ForestLink.LOG.info("dug ground: {} cells converted, {} dug", converted.size(), dug.size());
	}

	private static int parse(String line, boolean save) {
		String[] p = line.trim().split(" ");
		if (p.length != 4) return 0;
		try {
			BlockPos pos = new BlockPos(Integer.parseInt(p[1]), Integer.parseInt(p[2]), Integer.parseInt(p[3]));
			converted.add(pos.asLong());
			if (p[0].equals("d")) dug.add(pos.asLong());
			if (save) save(p[0], pos);
			return 1;
		} catch (NumberFormatException e) {
			return 0;
		}
	}

	private static void save(String kind, BlockPos pos) {
		synchronized (unsaved) {
			unsaved.append(kind).append(' ').append(pos.getX()).append(' ').append(pos.getY()).append(' ').append(pos.getZ()).append('\n');
		}
	}

	private static void flush() {
		String text;
		synchronized (unsaved) {
			if (unsaved.length() == 0 || saveFile == null) return;
			text = unsaved.toString();
			unsaved.setLength(0);
		}
		try {
			java.nio.file.Files.writeString(saveFile, text, java.nio.file.StandardOpenOption.CREATE, java.nio.file.StandardOpenOption.APPEND);
		} catch (Exception e) {
			ForestLink.LOG.warn("dug ground not saved: {}", e.toString());
		}
	}

	// ---- to The Forest -------------------------------------------------------------------------

	/**
	 * Client tick: dug cells go to The Forest through a ring (OFF_DIG = cells written, +4 = cells
	 * The Forest applied). The Forest zeroes both when it (re)creates the link: everything is sent
	 * again then, and on Minecraft's first tick too.
	 */
	public static void pump() {
		MappedByteBuffer map = ForestLink.buffer();
		if (map == null) return;
		int cur = map.getInt(OFF_DIG);
		if (cur != written) {
			written = cur;
			toForest.clear();
			toForest.addAll(dug);
		}
		int applied = map.getInt(OFF_DIG + 4);
		if (applied > written) applied = written;
		Long key;
		while (written - applied < RING - 8 && (key = toForest.poll()) != null) {
			BlockPos cell = BlockPos.of(key);
			int at = OFF_DIG + 64 + Math.floorMod(written, RING) * ENTRY;
			map.putInt(at, cell.getX());
			map.putInt(at + 4, cell.getY());
			map.putInt(at + 8, cell.getZ());
			Integer flag = fresh.remove(key);
			map.putInt(at + 12, flag == null ? 0 : flag);
			written++;
			map.putInt(OFF_DIG, written);
		}
	}

	private static int givesRead = Integer.MIN_VALUE;

	/**
	 * Client tick: things picked up in The Forest arrive as (Minecraft item id, count) in a ring
	 * at 0xA88000; they go into the inventory (or drop at the feet if it is full).
	 */
	public static void collectGifts(Minecraft minecraft) {
		MappedByteBuffer map = ForestLink.buffer();
		if (map == null || minecraft.player == null) return;
		int written = map.getInt(0xA88000);
		if (givesRead == Integer.MIN_VALUE || written < givesRead) givesRead = written;
		if (written - givesRead > 64) givesRead = written - 64;
		var server = minecraft.getSingleplayerServer();
		java.util.UUID player = minecraft.player.getUUID();
		while (givesRead < written) {
			int at = 0xA88000 + 16 + Math.floorMod(givesRead, 64) * 64;
			givesRead++;
			int count = map.getInt(at);
			int len = Math.min(56, Math.max(0, map.getInt(at + 4)));
			byte[] raw = new byte[len];
			map.get(at + 8, raw);
			String id = new String(raw, java.nio.charset.StandardCharsets.US_ASCII);
			if (server == null || count <= 0) continue;
			server.execute(() -> {
				var p = server.getPlayerList().getPlayer(player);
				if (p == null) return;
				var key = net.minecraft.resources.Identifier.tryParse(id);
				var item = key == null ? null : net.minecraft.core.registries.BuiltInRegistries.ITEM.getValue(key);
				if (item == null || item == Items.AIR) { ForestLink.LOG.warn("no Minecraft item {}", id); return; }
				int left = count;
				while (left > 0) {
					int n = Math.min(left, item.getDefaultMaxStackSize());
					ItemStack stack = new ItemStack(item, n);
					if (!p.getInventory().add(stack)) p.drop(stack, false);
					left -= n;
				}
				ForestLink.LOG.info("from The Forest: {} x{}", id, count);
			});
		}
	}

	/** Client tick: The Forest felled a tree for us -> logs in the inventory, like breaking a log. */
	public static void collectLogs(Minecraft minecraft) {
		MappedByteBuffer map = ForestLink.buffer();
		if (map == null || minecraft.player == null) return;
		int seq = map.getInt(Proto.OFF_FOREST + 84);
		if (lastLogSeq == Integer.MIN_VALUE) { lastLogSeq = seq; return; }
		if (seq == lastLogSeq) return;
		lastLogSeq = seq;
		int count = Math.max(1, Math.min(64, map.getInt(Proto.OFF_FOREST + 88)));
		var server = minecraft.getSingleplayerServer();
		if (server == null) return;
		java.util.UUID id = minecraft.player.getUUID();
		server.execute(() -> {
			var player = server.getPlayerList().getPlayer(id);
			if (player == null) return;
			ItemStack logs = new ItemStack(Items.OAK_LOG, count);
			if (!player.getInventory().add(logs)) player.drop(logs, false);
			ForestLink.LOG.info("tree felled: {} logs", count);
		});
	}
}
