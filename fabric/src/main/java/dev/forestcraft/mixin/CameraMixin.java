package dev.forestcraft.mixin;

import dev.forestcraft.ForestLink;
import net.minecraft.client.Camera;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/** F5: Minecraft's camera stops where The Forest's does (trees, rocks), so the player model lines up. */
@Mixin(Camera.class)
public abstract class CameraMixin {
	@Inject(method = "getMaxZoom", at = @At("RETURN"), cancellable = true)
	private void forestcraft$zoom(float max, CallbackInfoReturnable<Float> cir) {
		if (!ForestLink.forestInGame()) return;
		ForestLink.ForestView view = ForestLink.readView();
		if (view == null || !(view.camDist() > 0f)) return;
		if (view.camDist() < cir.getReturnValue()) cir.setReturnValue(view.camDist());
	}
}
