package dev.forestcraft.mixin;

import com.mojang.blaze3d.platform.FramerateLimitTracker;
import dev.forestcraft.ForestLink;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/**
 * Minecraft's own frame limits don't apply while The Forest plays: its window never gets
 * keyboard or mouse events (they come through The Forest), so after a minute Minecraft thought
 * the player was away and dropped to 30, then 10 frames per second: the jerky hand. Minecraft
 * waits for each Forest frame instead (ForestLink.paceToForest).
 */
@Mixin(FramerateLimitTracker.class)
public abstract class FramerateLimitMixin {
	@Inject(method = "getFramerateLimit", at = @At("HEAD"), cancellable = true)
	private void forestcraft$paced(CallbackInfoReturnable<Integer> cir) {
		if (ForestLink.forestPlaying()) cir.setReturnValue(260);
	}

	@Inject(method = "isHeavilyThrottled", at = @At("HEAD"), cancellable = true)
	private void forestcraft$neverThrottled(CallbackInfoReturnable<Boolean> cir) {
		if (ForestLink.forestPlaying()) cir.setReturnValue(false);
	}
}
