package dev.forestcraft.mixin;

import java.util.concurrent.CompletableFuture;
import net.minecraft.world.level.StructureManager;
import net.minecraft.world.level.chunk.ChunkAccess;
import net.minecraft.world.level.levelgen.NoiseBasedChunkGenerator;
import net.minecraft.world.level.levelgen.RandomState;
import net.minecraft.world.level.levelgen.blending.Blender;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/** The mirror world has no Minecraft terrain. The Forest's surface is collision only. */
@Mixin(NoiseBasedChunkGenerator.class)
public abstract class VoidChunkMixin {
	@Inject(method = "fillFromNoise", at = @At("HEAD"), cancellable = true)
	private void forestcraft$empty(Blender blender, RandomState state, StructureManager structures, ChunkAccess chunk, CallbackInfoReturnable<CompletableFuture<ChunkAccess>> cir) {
		cir.setReturnValue(CompletableFuture.completedFuture(chunk));
	}

	@Inject(method = "buildSurface(Lnet/minecraft/server/level/WorldGenRegion;Lnet/minecraft/world/level/StructureManager;Lnet/minecraft/world/level/levelgen/RandomState;Lnet/minecraft/world/level/chunk/ChunkAccess;)V", at = @At("HEAD"), cancellable = true)
	private void forestcraft$noSurface(CallbackInfo ci) {
		ci.cancel();
	}

	@Inject(method = "applyCarvers", at = @At("HEAD"), cancellable = true)
	private void forestcraft$noCarvers(CallbackInfo ci) {
		ci.cancel();
	}
}
