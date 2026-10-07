package dev.forestcraft.mixin;

import com.mojang.blaze3d.vertex.PoseStack;
import dev.forestcraft.ForestLink;
import net.minecraft.client.renderer.GameRenderer;
import net.minecraft.client.renderer.state.level.CameraRenderState;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/** Hands Minecraft's view bobbing to The Forest's camera (only called when bobbing is on). */
@Mixin(GameRenderer.class)
public abstract class GameRendererMixin {
	@Inject(method = "bobView", at = @At("HEAD"))
	private void forestcraft$bob(CameraRenderState state, PoseStack pose, CallbackInfo ci) {
		if (state.entityRenderState == null || !state.entityRenderState.isPlayer) return;
		ForestLink.frameWalk = state.entityRenderState.backwardsInterpolatedWalkDistance;
		ForestLink.frameBob = state.entityRenderState.bob;
	}

	@Inject(method = "renderItemInHand", at = @At("HEAD"))
	private void forestcraft$handView(net.minecraft.client.renderer.state.level.CameraRenderState camera, float partial, org.joml.Matrix4fc modelView, CallbackInfo ci) {
		dev.forestcraft.HandView.MATRIX.set(modelView);
	}
}
