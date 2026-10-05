package dev.forestcraft.mixin;

import dev.forestcraft.ForestLink;
import net.minecraft.core.BlockPos;
import net.minecraft.world.entity.ai.navigation.GroundPathNavigation;
import net.minecraft.world.level.chunk.LevelChunk;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/**
 * Where a mob should walk to in a column: Minecraft looks down the column for a block, finds
 * only air (the island is The Forest's), and sent mobs to the sky: no path, so they never moved.
 * Over The Forest's ground the answer is the first open cell above it (or above a Minecraft
 * block standing on it).
 */
@Mixin(GroundPathNavigation.class)
public abstract class GroundNavigationMixin {
	@Inject(method = "findSurfacePosition", at = @At("HEAD"), cancellable = true)
	private void forestcraft$forestSurface(LevelChunk chunk, BlockPos pos, int reachRange, CallbackInfoReturnable<BlockPos> cir) {
		if (ForestLink.buffer() == null) return;
		int top = ForestLink.groundTopAny(pos.getX(), pos.getZ());
		if (top == Integer.MIN_VALUE) return;
		BlockPos.MutableBlockPos p = pos.mutable();
		if (p.getY() <= top) p.setY(top + 1);
		// Down through air to the ground (or a block on it), then up out of anything solid.
		while (p.getY() > top + 1 && chunk.getBlockState(p).isAir() && chunk.getBlockState(p.below()).isAir()) p.move(0, -1, 0);
		while (p.getY() < chunk.getMaxY() && !chunk.getBlockState(p).isAir() && chunk.getBlockState(p).isSolid()) p.move(0, 1, 0);
		cir.setReturnValue(p.immutable());
	}
}
