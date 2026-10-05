package dev.forestcraft.mixin;

import com.llamalad7.mixinextras.injector.wrapoperation.Operation;
import com.llamalad7.mixinextras.injector.wrapoperation.WrapOperation;
import dev.forestcraft.ForestLink;
import net.minecraft.client.renderer.LevelRenderer;
import net.minecraft.client.renderer.extract.LevelExtractor;
import net.minecraft.core.BlockPos;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;

/**
 * Minecraft only draws an entity whose chunk section is "visible" in its own occlusion graph.
 * Its terrain isn't drawn here (The Forest's is), so that graph lags behind: a flying arrow kept
 * entering sections not marked yet and vanished every few frames (the blinking). The Forest
 * hides what is behind its own trees and walls; Minecraft only checks the view cone.
 */
@Mixin(LevelExtractor.class)
public abstract class EntityVisibleMixin {
	@WrapOperation(method = "isEntityVisible", at = @At(value = "INVOKE",
		target = "Lnet/minecraft/client/renderer/LevelRenderer;isSectionCompiledAndVisible(Lnet/minecraft/core/BlockPos;)Z"))
	private boolean forestcraft$anySection(LevelRenderer renderer, BlockPos pos, Operation<Boolean> original) {
		return ForestLink.forestInGame() || original.call(renderer, pos);
	}
}
