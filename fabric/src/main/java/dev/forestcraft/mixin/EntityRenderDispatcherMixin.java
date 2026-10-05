package dev.forestcraft.mixin;

import com.mojang.blaze3d.vertex.PoseStack;
import dev.forestcraft.EntityExport;
import dev.forestcraft.ForestLink;
import net.minecraft.client.renderer.SubmitNodeCollector;
import net.minecraft.client.renderer.entity.EntityRenderDispatcher;
import net.minecraft.client.renderer.entity.state.EntityRenderState;
import net.minecraft.client.renderer.state.level.CameraRenderState;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.ModifyVariable;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/** Every entity goes to The Forest's scene (depth, light, no lag) instead of the overlay. */
@Mixin(EntityRenderDispatcher.class)
public abstract class EntityRenderDispatcherMixin {
	private static final String SUBMIT = "submit(Lnet/minecraft/client/renderer/entity/state/EntityRenderState;Lnet/minecraft/client/renderer/state/level/CameraRenderState;DDDLcom/mojang/blaze3d/vertex/PoseStack;Lnet/minecraft/client/renderer/SubmitNodeCollector;)V";

	@Inject(method = SUBMIT, at = @At("HEAD"))
	private void forestcraft$begin(EntityRenderState state, CameraRenderState camera, double x, double y, double z, PoseStack pose, SubmitNodeCollector collector, CallbackInfo ci) {
		if (EntityExport.capturing()) EntityExport.begin(state, camera);
	}

	@ModifyVariable(method = SUBMIT, at = @At("HEAD"), argsOnly = true)
	private SubmitNodeCollector forestcraft$capture(SubmitNodeCollector collector) {
		return EntityExport.capturing() ? EntityExport.collector() : collector;
	}

	@Inject(method = SUBMIT, at = @At("RETURN"))
	private void forestcraft$end(EntityRenderState state, CameraRenderState camera, double x, double y, double z, PoseStack pose, SubmitNodeCollector collector, CallbackInfo ci) {
		if (EntityExport.capturing()) EntityExport.end();
	}
}
