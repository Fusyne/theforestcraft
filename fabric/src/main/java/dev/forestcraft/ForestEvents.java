package dev.forestcraft;

import java.nio.MappedByteBuffer;
import net.minecraft.client.Minecraft;
import net.minecraft.client.particle.TerrainParticle;
import net.minecraft.core.BlockPos;
import net.minecraft.core.particles.ParticleTypes;
import net.minecraft.sounds.SoundEvent;
import net.minecraft.sounds.SoundEvents;
import net.minecraft.sounds.SoundSource;
import net.minecraft.util.RandomSource;
import net.minecraft.world.level.block.Blocks;
import net.minecraft.world.level.block.SoundType;
import net.minecraft.world.level.block.state.BlockState;

/**
 * What happens in The Forest under Minecraft's hands, heard and seen the Minecraft way: the axe
 * in a tree (wood knocks and chips), a tree coming down, a blow or an arrow landing in a
 * creature, a bush or a rock hit. The Forest writes them at 0xA18000 (ForestEvents.cs).
 */
public final class ForestEvents {
	private static final int OFF = 0xA18000;
	private static final int RING = 64;
	private static int read = Integer.MIN_VALUE;

	private ForestEvents() {}

	/** Client tick. */
	public static void poll(Minecraft minecraft) {
		MappedByteBuffer map = ForestLink.buffer();
		var level = minecraft.level;
		if (map == null || level == null) return;
		int total = map.getInt(OFF);
		if (read == Integer.MIN_VALUE || total < read) { read = total; return; }
		if (total - read > RING) read = total - RING;
		for (; read < total; read++) {
			int at = OFF + 16 + (read % RING) * 16;
			int kind = map.getInt(at);
			double x = map.getFloat(at + 4), y = map.getFloat(at + 8), z = map.getFloat(at + 12);
			play(minecraft, kind, x, y, z);
		}
	}

	private static void play(Minecraft minecraft, int kind, double x, double y, double z) {
		var level = minecraft.level;
		RandomSource random = level.getRandom();
		switch (kind) {
			case 1 -> hit(minecraft, Blocks.OAK_LOG.defaultBlockState(), x, y, z, false);
			case 2 -> hit(minecraft, Blocks.OAK_LOG.defaultBlockState(), x, y, z, true);
			case 3, 7 -> {
				SoundEvent s = kind == 7 ? SoundEvents.ARROW_HIT : SoundEvents.PLAYER_ATTACK_STRONG;
				level.playLocalSound(x, y, z, s, SoundSource.PLAYERS, 1.0f, 0.9f + random.nextFloat() * 0.2f, false);
				for (int i = 0; i < 6; i++)
					level.addParticle(ParticleTypes.CRIT, x, y, z, (random.nextDouble() - 0.5) * 0.6, random.nextDouble() * 0.4, (random.nextDouble() - 0.5) * 0.6);
			}
			case 4 -> hit(minecraft, Blocks.OAK_LEAVES.defaultBlockState(), x, y, z, false);
			case 5 -> hit(minecraft, Blocks.STONE.defaultBlockState(), x, y, z, false);
			default -> hit(minecraft, Blocks.IRON_BLOCK.defaultBlockState(), x, y, z, false);
		}
	}

	/** A block-style hit (or break) at a point: its sound and chips flying off. */
	private static void hit(Minecraft minecraft, BlockState state, double x, double y, double z, boolean broke) {
		var level = minecraft.level;
		RandomSource random = level.getRandom();
		SoundType sound = state.getSoundType();
		BlockPos pos = BlockPos.containing(x, y, z);
		if (broke) level.playLocalSound(x, y, z, sound.getBreakSound(), SoundSource.BLOCKS, (sound.getVolume() + 1f) / 2f, sound.getPitch() * 0.8f, false);
		else level.playLocalSound(x, y, z, sound.getHitSound(), SoundSource.BLOCKS, (sound.getVolume() + 1f) / 2f, sound.getPitch() * 0.8f, false);
		int n = broke ? 24 : 6;
		for (int i = 0; i < n; i++) {
			double vx = (random.nextDouble() - 0.5) * (broke ? 0.5 : 0.2);
			double vy = random.nextDouble() * (broke ? 0.4 : 0.15);
			double vz = (random.nextDouble() - 0.5) * (broke ? 0.5 : 0.2);
			double px = x + (random.nextDouble() - 0.5) * (broke ? 1.0 : 0.2);
			double py = y + (random.nextDouble() - 0.5) * (broke ? 1.5 : 0.2);
			double pz = z + (random.nextDouble() - 0.5) * (broke ? 1.0 : 0.2);
			minecraft.particleEngine.add(new TerrainParticle(level, px, py, pz, vx, vy, vz, state, pos).scale(0.7f));
		}
	}
}
