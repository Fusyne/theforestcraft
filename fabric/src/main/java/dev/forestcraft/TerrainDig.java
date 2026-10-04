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
 * Digging the island one block at a time, like dirt. Unity 5 terrain has no holes: The Forest
 * lowers its heightmap under the dug cell, then the columns around whose ground actually sank
 * get just enough blocks (grass on top, dirt under) to rebuild the surface they had, so the
 * result is a one-block hole with Minecraft walls, not a patch of converted ground.
 */
public final class TerrainDig {
	public static final int OFF_DIG = 0xA20000;
	public static final int RING = 1024;
	public static final int ENTRY = 16;
	private static final int R = 3;
	private static final int SIDE = R * 2 + 1;

	private record Pending(BlockPos cell, double[] before, int[] delay) {}
	private static final java.util.List<Pending> pending = new java.util.ArrayList<>();

	private static long digKey = Long.MIN_VALUE;
	private static int digTicks;
	private static int lastLogSeq = Integer.MIN_VALUE;

	private TerrainDig() {}

	public static void stop() {
		digKey = Long.MIN_VALUE;
		digTicks = 0;
	}

	/** Client tick while attack is held on the island's ground at pos: mine it like dirt. */
	public static void request(Minecraft minecraft, BlockPos pos) {
		long key = pos.asLong();
		if (key != digKey) { digKey = key; digTicks = 0; }
		if (digTicks < 0) return;
		digTicks++;
		boolean shovel = minecraft.player != null && minecraft.player.getMainHandItem().getItem() instanceof net.minecraft.world.item.ShovelItem;
		int needed = shovel ? 4 : 15; // dirt: 0.75 s by hand, ~0.2 s with an iron shovel
		if (minecraft.level != null) minecraft.level.destroyBlockProgress(minecraft.player.getId(), pos, Math.min(9, digTicks * 10 / needed));
		if (digTicks < needed) return;
		digTicks = -1; // done with this cell until the aim moves
		if (minecraft.level != null) minecraft.level.destroyBlockProgress(minecraft.player.getId(), pos, -1);
		var server = minecraft.getSingleplayerServer();
		if (server == null || minecraft.player == null) return;
		// The ground block is the one whose top is the rounded surface (same rule as placing):
		// digging it takes the ground down a whole block, never just a sliver.
		// The aimed block is the dug block (aimShape uses the same rounded grid).
		BlockPos cell = pos.immutable();
		java.util.UUID id = minecraft.player.getUUID();
		server.execute(() -> dig(server, cell, id));
	}

	/** Server thread: ask The Forest to lower the ground under the cell, remember the surface around it. */
	private static void dig(net.minecraft.server.MinecraftServer server, BlockPos cell, java.util.UUID player) {
		ForestLink.refreshGrid();
		double[] before = new double[SIDE * SIDE];
		for (int dz = -R; dz <= R; dz++)
			for (int dx = -R; dx <= R; dx++)
				before[(dz + R) * SIDE + dx + R] = ForestLink.heightAt(cell.getX() + dx, cell.getZ() + dz);
		publish(cell);
		synchronized (pending) { pending.add(new Pending(cell, before, new int[] {6})); }
		var p = server.getPlayerList().getPlayer(player);
		if (p != null) {
			ItemStack dirt = new ItemStack(Items.DIRT, 1);
			if (!p.getInventory().add(dirt)) p.drop(dirt, false);
		}
	}

	/**
	 * Server tick, a few ticks after the dig (The Forest has lowered its terrain by then).
	 * The ground that was there before and is now exposed becomes blocks, so the hole is a cube:
	 *  - the dug column gets a block floor right under the dug cell;
	 *  - every column whose ground sank gets blocks back up to the surface it had (grass on top),
	 *    which are the hole's walls. Only cells that were under the ground before are filled, so a
	 *    block the player already mined out is never put back.
	 */
	public static void serverTick(ServerLevel level) {
		Pending[] work;
		synchronized (pending) {
			if (pending.isEmpty()) return;
			work = pending.toArray(new Pending[0]);
		}
		for (Pending job : work) {
			if (--job.delay()[0] > 0) continue;
			synchronized (pending) { pending.remove(job); }
			long started = System.nanoTime();
			ForestLink.refreshGrid();
			BlockPos cell = job.cell();
			BlockPos.MutableBlockPos p = new BlockPos.MutableBlockPos();
			int filled = 0;
			// Only what can be seen: the hole's floor, its four-plus-four walls (the ring next to the
			// hole, from the dug level up to their surface), and one surface block on the next ring
			// where the ground visibly sank. Placed without neighbour updates (flags 2|16): before,
			// whole columns 7 wide were refilled with full updates and the game hitched.
			for (int dz = -2; dz <= 2; dz++) {
				for (int dx = -2; dx <= 2; dx++) {
					int x = cell.getX() + dx, z = cell.getZ() + dz;
					double was = job.before()[(dz + R) * SIDE + dx + R];
					double now = ForestLink.heightAt(x, z);
					if (Double.isNaN(was) || Double.isNaN(now)) continue;
					int ring = Math.max(Math.abs(dx), Math.abs(dz));
					int surface = (int) Math.round(was) - 1;
					int from, to;
					if (ring == 0) { from = cell.getY() - 1; to = cell.getY() - 1; }           // floor
					else if (ring == 1) { if (now > was - 0.02) continue; from = Math.max((int) Math.floor(now), cell.getY() - 1); to = surface; } // walls
					else { if (now > was - 0.25) continue; from = surface; to = surface; }    // lip
					for (int y = from; y <= to; y++) {
						p.set(x, y, z);
						if (!level.getBlockState(p).isAir()) continue;
						boolean top = y == surface && ring > 0 && level.getBlockState(p.above()).isAir();
						BlockState state = top ? Blocks.GRASS_BLOCK.defaultBlockState()
							: y < surface - 2 ? Blocks.STONE.defaultBlockState() : Blocks.DIRT.defaultBlockState();
						level.setBlock(p, state, 2 | 16);
						filled++;
					}
				}
			}
			ForestLink.LOG.info("dug {} {} {}: {} blocks of ground around, {} ms", cell.getX(), cell.getY(), cell.getZ(), filled, (System.nanoTime() - started) / 1000000);
		}
	}

	private static synchronized void publish(BlockPos cell) {
		MappedByteBuffer map = ForestLink.buffer();
		if (map == null) return;
		int written = map.getInt(OFF_DIG);
		int at = OFF_DIG + 64 + Math.floorMod(written, RING) * ENTRY;
		map.putInt(at, cell.getX());
		map.putInt(at + 4, cell.getY());
		map.putInt(at + 8, cell.getZ());
		map.putInt(OFF_DIG, written + 1);
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
