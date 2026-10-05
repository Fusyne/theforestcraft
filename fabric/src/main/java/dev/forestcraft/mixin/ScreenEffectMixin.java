package dev.forestcraft.mixin;

import dev.forestcraft.ForestLink;
import net.minecraft.client.renderer.ScreenEffectRenderer;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/** Under water The Forest draws its own underwater look: Minecraft's blue overlay would stack on it. */
@Mixin(ScreenEffectRenderer.class)
public abstract class ScreenEffectMixin {
	@Inject(method = "submitWater", at = @At("HEAD"), cancellable = true)
	private static void forestcraft$noWaterOverlay(CallbackInfo ci) {
		if (ForestLink.forestInGame()) ci.cancel();
	}
}
