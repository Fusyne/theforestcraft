package dev.forestcraft.mixin;

import net.minecraft.world.level.StructureManager;
import net.minecraft.world.level.WorldGenLevel;
import net.minecraft.world.level.chunk.ChunkAccess;
import net.minecraft.world.level.chunk.ChunkGenerator;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/**
 * No Minecraft decorations either: the terrain was already empty, but each biome's features
 * (icebergs in a frozen ocean, springs, ice spikes...) were still placed and hung in the air
 * over The Forest's island.
 */
@Mixin(ChunkGenerator.class)
public abstract class NoDecorationMixin {
	@Inject(method = "applyBiomeDecoration", at = @At("HEAD"), cancellable = true)
	private void forestcraft$noFeatures(WorldGenLevel level, ChunkAccess chunk, StructureManager structures, CallbackInfo ci) {
		ci.cancel();
	}
}
