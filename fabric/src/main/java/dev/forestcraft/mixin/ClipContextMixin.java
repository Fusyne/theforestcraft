package dev.forestcraft.mixin;

import dev.forestcraft.ForestLink;
import net.minecraft.core.BlockPos;
import net.minecraft.world.level.BlockGetter;
import net.minecraft.world.level.ClipContext;
import net.minecraft.world.level.block.state.BlockState;
import net.minecraft.world.phys.shapes.Shapes;
import net.minecraft.world.phys.shapes.VoxelShape;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/**
 * Rays (the crosshair, arrows) hit The Forest too: the island and its trees/rocks become
 * aimable, so a block can be placed against the ground or a tree like against any block.
 */
@Mixin(ClipContext.class)
public abstract class ClipContextMixin {
	@Inject(method = "getBlockShape", at = @At("RETURN"), cancellable = true)
	private void forestcraft$forest(BlockState state, BlockGetter level, BlockPos pos, CallbackInfoReturnable<VoxelShape> cir) {
		VoxelShape forest = ForestLink.aimShape(pos.getX(), pos.getY(), pos.getZ());
		if (forest == null) return;
		VoxelShape own = cir.getReturnValue();
		cir.setReturnValue(own.isEmpty() ? forest : Shapes.or(own, forest));
	}
}
