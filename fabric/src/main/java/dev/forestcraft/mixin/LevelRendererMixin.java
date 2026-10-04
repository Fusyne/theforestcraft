package dev.forestcraft.mixin;

import dev.forestcraft.ForestLink;
import net.minecraft.client.renderer.LevelRenderer;
import org.joml.Vector4f;
import org.joml.Vector4fc;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.ModifyArg;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/**
 * The Forest draws the island. Minecraft draws its own level (placed blocks, block outline,
 * break particles, the player model in F5) as a transparent layer: every clear is transparent,
 * the sky is cut (SkyMixin), clouds are off, and the world is otherwise void.
 */
@Mixin(LevelRenderer.class)
public abstract class LevelRendererMixin {
	private static final Vector4f FORESTCRAFT$CLEAR = new Vector4f(0f, 0f, 0f, 0f);

	@ModifyArg(
		method = {"lambda$render$0", "lambda$addMainPass$0"},
		at = @At(
			value = "INVOKE",
			target = "Lcom/mojang/blaze3d/systems/CommandEncoder;clearColorAndDepthTextures(Lcom/mojang/blaze3d/textures/GpuTexture;Lorg/joml/Vector4fc;Lcom/mojang/blaze3d/textures/GpuTexture;D)V"
		),
		index = 1,
		require = 1
	)
	private Vector4fc forestcraft$transparent(Vector4fc color) {
		return ForestLink.forestInGame() ? FORESTCRAFT$CLEAR : color;
	}

	/** The Forest draws the outline itself, depth-tested and in place. */
	@Inject(method = "submitBlockOutline", at = @At("HEAD"), cancellable = true)
	private void forestcraft$noOutline(CallbackInfo ci) {
		if (ForestLink.forestInGame()) ci.cancel();
	}

	/** Cracks are drawn by The Forest on the real block (depth, no lag). */
	@Inject(method = "submitBlockDestroyAnimation", at = @At("HEAD"), cancellable = true)
	private void forestcraft$noCracks(CallbackInfo ci) {
		if (ForestLink.forestInGame()) ci.cancel();
	}
}
