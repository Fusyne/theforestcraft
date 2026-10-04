package dev.forestcraft.mixin;

import net.minecraft.client.renderer.SkyRenderer;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/** The Forest draws the world. Minecraft's sky would cover it. */
@Mixin(SkyRenderer.class)
public abstract class SkyMixin {
	@Inject(method = "renderSkyDisc", at = @At("HEAD"), cancellable = true)
	private void forestcraft$disc(CallbackInfo ci) { ci.cancel(); }

	@Inject(method = "renderDarkDisc", at = @At("HEAD"), cancellable = true)
	private void forestcraft$dark(CallbackInfo ci) { ci.cancel(); }

	@Inject(method = "renderSunMoonAndStars", at = @At("HEAD"), cancellable = true)
	private void forestcraft$sun(CallbackInfo ci) { ci.cancel(); }

	@Inject(method = "renderSunriseAndSunset", at = @At("HEAD"), cancellable = true)
	private void forestcraft$sunset(CallbackInfo ci) { ci.cancel(); }

	@Inject(method = "renderEndSky", at = @At("HEAD"), cancellable = true)
	private void forestcraft$end(CallbackInfo ci) { ci.cancel(); }
}
