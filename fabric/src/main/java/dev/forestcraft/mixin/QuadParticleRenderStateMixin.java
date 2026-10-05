package dev.forestcraft.mixin;

import dev.forestcraft.ForestLink;
import dev.forestcraft.ParticleExport;
import net.minecraft.client.renderer.SubmitNodeCollector;
import net.minecraft.client.renderer.state.level.CameraRenderState;
import net.minecraft.client.renderer.state.level.QuadParticleRenderState;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/** Particles go to The Forest (drawn in its scene, with depth) instead of Minecraft's overlay. */
@Mixin(QuadParticleRenderState.class)
public abstract class QuadParticleRenderStateMixin {
	@Inject(method = "submit", at = @At("HEAD"), cancellable = true)
	private void forestcraft$toForest(SubmitNodeCollector collector, CameraRenderState camera, CallbackInfo ci) {
		if (ForestLink.buffer() == null) return;
		ParticleExport.capture((QuadParticleRenderState) (Object) this, camera);
		ci.cancel();
	}
}
