package dev.forestcraft.mixin;

import dev.forestcraft.ForestLink;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/**
 * "Loading terrain" (after a respawn, a dimension change...) waits for the player's chunk section
 * to be compiled for rendering. Minecraft's terrain is never drawn here (The Forest draws the
 * world): don't wait for it, or the screen never goes away. Same as SkyCraft.
 */
@Mixin(targets = "net.minecraft.client.multiplayer.LevelLoadTracker$WaitingForPlayerChunk")
public abstract class WaitingForPlayerChunkMixin {
	@Inject(method = "isReady", at = @At("HEAD"), cancellable = true)
	private void forestcraft$ready(CallbackInfoReturnable<Boolean> cir) {
		if (ForestLink.buffer() != null) cir.setReturnValue(true);
	}
}
