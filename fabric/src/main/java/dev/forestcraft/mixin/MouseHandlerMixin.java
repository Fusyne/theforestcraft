package dev.forestcraft.mixin;

import dev.forestcraft.ForestLink;
import net.minecraft.client.MouseHandler;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/**
 * The hidden window never grabs the real cursor (The Forest has it), but Minecraft must believe
 * it does: handleKeybinds only keeps mining while the mouse is "grabbed".
 */
@Mixin(MouseHandler.class)
public abstract class MouseHandlerMixin {
	@Inject(method = "grabMouse", at = @At("HEAD"), cancellable = true)
	private void forestcraft$noGrab(CallbackInfo ci) {
		if (ForestLink.forestInGame()) ci.cancel();
	}

	@Inject(method = "isMouseGrabbed", at = @At("HEAD"), cancellable = true)
	private void forestcraft$grabbed(CallbackInfoReturnable<Boolean> cir) {
		// "Grabbed" (so mining keeps going) only while playing; with a Minecraft screen open the
		// cursor must move freely over it.
		if (ForestLink.forestInGame() && !dev.forestcraft.ScreenBridge.screenOpen(net.minecraft.client.Minecraft.getInstance())) cir.setReturnValue(true);
	}
}
