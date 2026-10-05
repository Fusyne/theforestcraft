package dev.forestcraft.mixin;

import com.mojang.blaze3d.vertex.PoseStack;
import dev.forestcraft.EntityExport;
import dev.forestcraft.ForestLink;
import net.minecraft.client.renderer.SubmitNodeCollector;
import net.minecraft.client.renderer.entity.LivingEntityRenderer;
import net.minecraft.client.renderer.entity.state.LivingEntityRenderState;
import net.minecraft.client.renderer.state.level.CameraRenderState;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.ModifyVariable;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/**
 * Living entities (mobs, Steve in F5) go to The Forest's scene instead of the overlay, where
 * they had no depth and showed through blocks and trees. Their whole submit (body, armor,
 * elytra, held items...) renders into EntityExport's capturing collector.
 */
@Mixin(LivingEntityRenderer.class)
public abstract class LivingEntityRendererMixin {
	private static final String SUBMIT = "submit(Lnet/minecraft/client/renderer/entity/state/LivingEntityRenderState;Lcom/mojang/blaze3d/vertex/PoseStack;Lnet/minecraft/client/renderer/SubmitNodeCollector;Lnet/minecraft/client/renderer/state/level/CameraRenderState;)V";

	@Inject(method = SUBMIT, at = @At("HEAD"))
	private void forestcraft$begin(LivingEntityRenderState state, PoseStack pose, SubmitNodeCollector collector, CameraRenderState camera, CallbackInfo ci) {
		if (EntityExport.capturing()) EntityExport.begin(state, camera);
	}

	@ModifyVariable(method = SUBMIT, at = @At("HEAD"), argsOnly = true)
	private SubmitNodeCollector forestcraft$capture(SubmitNodeCollector collector) {
		return EntityExport.capturing() ? EntityExport.collector() : collector;
	}

	@Inject(method = SUBMIT, at = @At("TAIL"))
	private void forestcraft$end(LivingEntityRenderState state, PoseStack pose, SubmitNodeCollector collector, CameraRenderState camera, CallbackInfo ci) {
		if (EntityExport.capturing()) EntityExport.end();
	}
}
