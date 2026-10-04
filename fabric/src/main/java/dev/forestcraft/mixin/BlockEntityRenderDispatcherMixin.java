package dev.forestcraft.mixin;

import com.mojang.blaze3d.vertex.PoseStack;
import dev.forestcraft.EntityExport;
import dev.forestcraft.ForestLink;
import net.minecraft.client.renderer.SubmitNodeCollector;
import net.minecraft.client.renderer.blockentity.BlockEntityRenderDispatcher;
import net.minecraft.client.renderer.blockentity.state.BlockEntityRenderState;
import net.minecraft.client.renderer.state.level.CameraRenderState;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.ModifyVariable;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/** Chests, signs, beds, banners, skulls...: drawn by The Forest like the entities. */
@Mixin(BlockEntityRenderDispatcher.class)
public abstract class BlockEntityRenderDispatcherMixin {
	private static final String SUBMIT = "submit(Lnet/minecraft/client/renderer/blockentity/state/BlockEntityRenderState;Lcom/mojang/blaze3d/vertex/PoseStack;Lnet/minecraft/client/renderer/SubmitNodeCollector;Lnet/minecraft/client/renderer/state/level/CameraRenderState;)V";

	@Inject(method = SUBMIT, at = @At("HEAD"))
	private void forestcraft$begin(BlockEntityRenderState state, PoseStack pose, SubmitNodeCollector collector, CameraRenderState camera, CallbackInfo ci) {
		if (ForestLink.forestInGame() && state.blockPos != null)
			EntityExport.begin(state.blockPos.getX(), state.blockPos.getY(), state.blockPos.getZ(), camera, 2);
	}

	@ModifyVariable(method = SUBMIT, at = @At("HEAD"), argsOnly = true)
	private SubmitNodeCollector forestcraft$capture(SubmitNodeCollector collector) {
		return ForestLink.forestInGame() ? EntityExport.collector() : collector;
	}

	@Inject(method = SUBMIT, at = @At("RETURN"))
	private void forestcraft$end(BlockEntityRenderState state, PoseStack pose, SubmitNodeCollector collector, CameraRenderState camera, CallbackInfo ci) {
		if (ForestLink.forestInGame() && state.blockPos != null) EntityExport.end();
	}
}
