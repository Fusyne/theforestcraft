package dev.forestcraft.mixin;

import com.llamalad7.mixinextras.injector.wrapoperation.Operation;
import com.llamalad7.mixinextras.injector.wrapoperation.WrapOperation;
import com.mojang.blaze3d.systems.GpuSurface;
import dev.forestcraft.ForestLink;
import net.minecraft.client.Minecraft;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;

/**
 * Minecraft's window is hidden while The Forest plays: nobody sees what it presents. Yet each
 * frame it still took a window image, copied the frame into it and presented it, and with The
 * Forest keeping the graphics card busy (a crowd of cannibals) that present waited 150-200 ms:
 * Minecraft fell to ~20 frames a second, the hand and HUD stuttered. The frame The Forest shows
 * is copied from Minecraft's own render target (FrameCapture), so the window image is skipped:
 * not acquired, so neither filled nor presented.
 */
@Mixin(Minecraft.class)
public abstract class HiddenWindowMixin {
	@WrapOperation(method = "renderFrame", at = @At(value = "INVOKE", target = "Lcom/mojang/blaze3d/systems/GpuSurface;acquireNextTexture()V"))
	private void forestcraft$noWindowImage(GpuSurface surface, Operation<Void> original) {
		if (ForestLink.forestPlaying()) return;
		original.call(surface);
	}
}
