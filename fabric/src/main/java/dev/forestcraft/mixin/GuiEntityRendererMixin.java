package dev.forestcraft.mixin;

import com.mojang.blaze3d.vertex.PoseStack;
import dev.forestcraft.EntityExport;
import net.minecraft.client.gui.render.pip.GuiEntityRenderer;
import net.minecraft.client.renderer.SubmitNodeCollector;
import net.minecraft.client.renderer.state.gui.pip.GuiEntityRenderState;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/** Steve in the inventory is drawn by Minecraft itself, not sent to The Forest's scene. */
@Mixin(GuiEntityRenderer.class)
public abstract class GuiEntityRendererMixin {
	private static final String RENDER = "renderToTexture(Lnet/minecraft/client/renderer/state/gui/pip/GuiEntityRenderState;Lcom/mojang/blaze3d/vertex/PoseStack;Lnet/minecraft/client/renderer/SubmitNodeCollector;)V";

	@Inject(method = RENDER, at = @At("HEAD"))
	private void forestcraft$guiStart(GuiEntityRenderState state, PoseStack pose, SubmitNodeCollector collector, CallbackInfo ci) {
		EntityExport.inGui = true;
	}

	@Inject(method = RENDER, at = @At("RETURN"))
	private void forestcraft$guiEnd(GuiEntityRenderState state, PoseStack pose, SubmitNodeCollector collector, CallbackInfo ci) {
		EntityExport.inGui = false;
	}
}
