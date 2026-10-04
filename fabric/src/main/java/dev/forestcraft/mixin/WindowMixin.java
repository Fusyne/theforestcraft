package dev.forestcraft.mixin;

import com.mojang.blaze3d.platform.Window;
import dev.forestcraft.ForestLink;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/** The Forest window is the real one. Minecraft keeps simulating as if it still had focus. */
@Mixin(Window.class)
public abstract class WindowMixin {
	@Inject(method = "isFocused", at = @At("HEAD"), cancellable = true)
	private void forestcraft$focused(CallbackInfoReturnable<Boolean> cir) {
		if (ForestLink.forestInGame()) cir.setReturnValue(true);
	}

	@Inject(method = "isIconified", at = @At("HEAD"), cancellable = true)
	private void forestcraft$shown(CallbackInfoReturnable<Boolean> cir) {
		if (ForestLink.forestInGame()) cir.setReturnValue(false);
	}
}
