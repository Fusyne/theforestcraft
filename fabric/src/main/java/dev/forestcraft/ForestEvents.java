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
			case 8 -> {
				// A critical blow (jump attack) on one of The Forest's creatures.
				level.playLocalSound(x, y, z, SoundEvents.PLAYER_ATTACK_CRIT, SoundSource.PLAYERS, 1.0f, 0.9f + random.nextFloat() * 0.2f, false);
				level.playLocalSound(x, y, z, SoundEvents.PLAYER_ATTACK_KNOCKBACK, SoundSource.PLAYERS, 0.8f, 1.0f, false);
				for (int i = 0; i < 18; i++)
					level.addParticle(ParticleTypes.CRIT, x, y, z, (random.nextDouble() - 0.5) * 1.0, random.nextDouble() * 0.6, (random.nextDouble() - 0.5) * 1.0);
			}
			case 9 -> {
				// Flint and steel / fire charge set a creature alight.
				level.playLocalSound(x, y, z, SoundEvents.FLINTANDSTEEL_USE, SoundSource.PLAYERS, 1.0f, 0.9f + random.nextFloat() * 0.2f, false);
				for (int i = 0; i < 10; i++)
					level.addParticle(ParticleTypes.FLAME, x + (random.nextDouble() - 0.5) * 0.6, y + random.nextDouble() * 0.8, z + (random.nextDouble() - 0.5) * 0.6, 0, 0.03, 0);
			}
			case 10 -> {
				// A bucket of lava poured on a creature.
				level.playLocalSound(x, y, z, SoundEvents.BUCKET_EMPTY_LAVA, SoundSource.PLAYERS, 1.0f, 1.0f, false);
				level.playLocalSound(x, y, z, SoundEvents.LAVA_POP, SoundSource.BLOCKS, 1.0f, 1.0f, false);
				for (int i = 0; i < 16; i++)
					level.addParticle(ParticleTypes.LAVA, x, y + 0.5, z, 0, 0, 0);
			}
			case 11 -> {
				// Water thrown on a burning creature.
				level.playLocalSound(x, y, z, SoundEvents.BUCKET_EMPTY, SoundSource.PLAYERS, 1.0f, 1.0f, false);
				level.playLocalSound(x, y, z, SoundEvents.FIRE_EXTINGUISH, SoundSource.BLOCKS, 0.8f, 1.0f, false);
				for (int i = 0; i < 16; i++)
					level.addParticle(ParticleTypes.SPLASH, x + (random.nextDouble() - 0.5), y + random.nextDouble(), z + (random.nextDouble() - 0.5), 0, 0.1, 0);
				for (int i = 0; i < 6; i++)
					level.addParticle(ParticleTypes.LARGE_SMOKE, x, y + 0.8, z, 0, 0.05, 0);
			}
			case 12 -> level.playLocalSound(x, y, z, SoundEvents.LEAD_TIED, SoundSource.NEUTRAL, 1.0f, 1.0f, false);
			case 13 -> level.playLocalSound(x, y, z, SoundEvents.LEAD_BREAK, SoundSource.NEUTRAL, 1.0f, 1.0f, false);
			case 14 -> level.playLocalSound(x, y, z, SoundEvents.LEAD_UNTIED, SoundSource.NEUTRAL, 1.0f, 1.0f, false);
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
