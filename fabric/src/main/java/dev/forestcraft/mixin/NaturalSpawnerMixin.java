package dev.forestcraft.mixin;

import net.minecraft.server.level.ServerLevel;
import net.minecraft.world.entity.MobCategory;
import net.minecraft.world.level.NaturalSpawner;
import net.minecraft.world.level.chunk.LevelChunk;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/**
 * The Forest's lakes, sea and cave pools are water for Minecraft, so Minecraft filled them with
 * its own fish, squids and axolotls. The Forest has its own fish: no Minecraft water mobs.
 */
@Mixin(NaturalSpawner.class)
public abstract class NaturalSpawnerMixin {
	@Inject(method = "spawnCategoryForChunk", at = @At("HEAD"), cancellable = true)
	private static void forestcraft$noWaterMobs(MobCategory category, ServerLevel level, LevelChunk chunk,
			NaturalSpawner.SpawnPredicate extraTest, NaturalSpawner.AfterSpawnCallback spawnCallback, CallbackInfo ci) {
		if (category == MobCategory.WATER_CREATURE || category == MobCategory.WATER_AMBIENT
				|| category == MobCategory.UNDERGROUND_WATER_CREATURE || category == MobCategory.AXOLOTLS) ci.cancel();
	}
}
