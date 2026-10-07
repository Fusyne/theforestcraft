package dev.forestcraft;

import java.nio.MappedByteBuffer;
import net.minecraft.client.Minecraft;
import net.minecraft.core.BlockPos;
import net.minecraft.tags.FluidTags;
import net.minecraft.world.item.Items;
import net.minecraft.world.level.block.BaseFireBlock;
import net.minecraft.world.level.block.Blocks;
import net.minecraft.world.level.block.CampfireBlock;
import net.minecraft.world.level.block.state.BlockState;
import net.minecraft.world.phys.Vec3;

/**
 * Minecraft things that hurt The Forest's creatures.
 *  - Fire, lava and water blocks around the player (client, every 5 ticks) at OFF_HAZARDS:
 *    seq, count, then (x, y, z, kind) ints; kind 1 fire, 2 lava, 3 water. A cannibal walking
 *    through Steve's fire catches fire, lava burns him badly, water puts him out.
 *  - Explosions (TNT, creepers, fire charges...) at OFF_BLASTS: count, then a ring of
 *    (x, y, z, radius) floats. The Forest blows its creatures up the way its own bombs do.
 *  - What Steve holds, at OFF_MC+200: 1 flint and steel, 2 fire charge, 3 lava bucket,
 *    4 water bucket, 5 lead (The Forest acts on the creature he right-clicks with it).
 */
public final class Hazards {
	public static final int OFF_HAZARDS = 0xA1A000;
	private static final int MAX = 512;
	public static final int OFF_BLASTS = 0xA1E000;
	private static final int BLAST_RING = 32;
	private static int ticks, blasts = -1;

	private Hazards() {}

	public static int heldKind(Minecraft minecraft) {
		if (minecraft.player == null) return 0;
		var item = minecraft.player.getMainHandItem().getItem();
		if (item == Items.FLINT_AND_STEEL) return 1;
		if (item == Items.FIRE_CHARGE) return 2;
		if (item == Items.LAVA_BUCKET) return 3;
		if (item == Items.WATER_BUCKET) return 4;
		if (item == Items.LEAD) return 5;
		return 0;
	}

	/** Client tick. */
	public static void tick(Minecraft minecraft) {
		MappedByteBuffer map = ForestLink.buffer();
		if (map == null || minecraft.player == null || minecraft.level == null) return;
		map.putInt(Proto.OFF_MC + 200, heldKind(minecraft));
		if (ticks++ % 5 != 0) return;
		var level = minecraft.level;
		BlockPos at = minecraft.player.blockPosition();
		BlockPos.MutableBlockPos p = new BlockPos.MutableBlockPos();
		int n = 0;
		for (int dy = -6; dy <= 6 && n < MAX; dy++)
			for (int dx = -12; dx <= 12 && n < MAX; dx++)
				for (int dz = -12; dz <= 12 && n < MAX; dz++) {
					p.set(at.getX() + dx, at.getY() + dy, at.getZ() + dz);
					BlockState state = level.getBlockState(p);
					if (state.isAir()) continue;
					int kind = 0;
					if (state.getBlock() instanceof BaseFireBlock) kind = 1;
					else if (state.getBlock() instanceof CampfireBlock && state.getValue(CampfireBlock.LIT)) kind = 1;
					else if (state.is(Blocks.MAGMA_BLOCK)) kind = 1;
					else if (state.getFluidState().is(FluidTags.LAVA)) kind = 2;
					else if (state.is(Blocks.WATER)) kind = 3;
					if (kind == 0) continue;
					int o = OFF_HAZARDS + 16 + n * 16;
					map.putInt(o, p.getX());
					map.putInt(o + 4, p.getY());
					map.putInt(o + 8, p.getZ());
					map.putInt(o + 12, kind);
					n++;
				}
		map.putInt(OFF_HAZARDS + 4, n);
		map.putInt(OFF_HAZARDS, map.getInt(OFF_HAZARDS) + 1);
	}

	/** Server thread: an explosion is about to happen. */
	public static synchronized void explosion(Vec3 center, float radius) {
		MappedByteBuffer map = ForestLink.buffer();
		if (map == null || center == null) return;
		if (blasts < 0) blasts = Math.max(0, map.getInt(OFF_BLASTS));
		int o = OFF_BLASTS + 16 + (blasts % BLAST_RING) * 16;
		map.putFloat(o, (float) center.x);
		map.putFloat(o + 4, (float) center.y);
		map.putFloat(o + 8, (float) center.z);
		map.putFloat(o + 12, radius);
		blasts++;
		map.putInt(OFF_BLASTS, blasts);
	}
}
