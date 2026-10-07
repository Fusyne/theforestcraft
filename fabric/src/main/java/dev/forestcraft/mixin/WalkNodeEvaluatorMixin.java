package dev.forestcraft.mixin;

import dev.forestcraft.ForestLink;
import net.minecraft.core.BlockPos;
import net.minecraft.world.level.BlockGetter;
import net.minecraft.world.level.pathfinder.PathType;
import net.minecraft.world.level.pathfinder.WalkNodeEvaluator;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/**
 * Mobs find their way over The Forest: its ground (and trees, rocks) is solid for the path
 * finder, which then walks on top of it at the surface's real height. Otherwise the world is
 * void to them and they stand still, or walk off into nothing.
 */
@Mixin(WalkNodeEvaluator.class)
public abstract class WalkNodeEvaluatorMixin {
	@Inject(method = "getPathTypeFromState(Lnet/minecraft/world/level/BlockGetter;Lnet/minecraft/core/BlockPos;)Lnet/minecraft/world/level/pathfinder/PathType;",
		at = @At("HEAD"), cancellable = true)
	private static void forestcraft$forestGround(BlockGetter level, BlockPos pos, CallbackInfoReturnable<PathType> cir) {
		if (ForestLink.buffer() == null) return;
		if (!level.getBlockState(pos).isAir()) return;
		// The Forest's lakes are water for the path finder too (the path region only knows real
		// blocks): without it a villager floating on a lake planned its way along the lake bed
		// under it and spun round on the spot, unable to reach it.
		if (ForestLink.waterAt(pos.getX(), pos.getY(), pos.getZ())) { cir.setReturnValue(PathType.WATER); return; }
		if (!Double.isNaN(ForestLink.forestTop(pos.getX(), pos.getY(), pos.getZ()))) cir.setReturnValue(PathType.BLOCKED);
	}

	@Inject(method = "getFloorLevel(Lnet/minecraft/world/level/BlockGetter;Lnet/minecraft/core/BlockPos;)D",
		at = @At("RETURN"), cancellable = true)
	private static void forestcraft$forestFloor(BlockGetter level, BlockPos pos, CallbackInfoReturnable<Double> cir) {
		if (ForestLink.buffer() == null) return;
		int y = pos.getY() - 1;
		double top = ForestLink.forestTop(pos.getX(), y, pos.getZ());
		if (Double.isNaN(top)) return;
		double floor = y + top;
		if (floor > cir.getReturnValueD()) cir.setReturnValue(floor);
	}

	/** A swimmer's path on The Forest's water runs on its surface, like on Minecraft's. */
	@Inject(method = "getFloorLevel(Lnet/minecraft/core/BlockPos;)D", at = @At("HEAD"), cancellable = true)
	private void forestcraft$floatOnForestWater(BlockPos pos, CallbackInfoReturnable<Double> cir) {
		if (ForestLink.buffer() == null) return;
		if (!((WalkNodeEvaluator) (Object) this).canFloat()) return;
		if (ForestLink.waterAt(pos.getX(), pos.getY(), pos.getZ())) cir.setReturnValue(pos.getY() + 0.5);
	}

	/** Where a path starts for a mob floating on The Forest's water: up to its surface. */
	@com.llamalad7.mixinextras.injector.wrapoperation.WrapOperation(method = "getStart", at = @At(value = "INVOKE",
		target = "Lnet/minecraft/world/level/pathfinder/PathfindingContext;getBlockState(Lnet/minecraft/core/BlockPos;)Lnet/minecraft/world/level/block/state/BlockState;"))
	private net.minecraft.world.level.block.state.BlockState forestcraft$startOnForestWater(net.minecraft.world.level.pathfinder.PathfindingContext context, BlockPos pos,
			com.llamalad7.mixinextras.injector.wrapoperation.Operation<net.minecraft.world.level.block.state.BlockState> original) {
		net.minecraft.world.level.block.state.BlockState state = original.call(context, pos);
		if (state.isAir() && ForestLink.buffer() != null && ForestLink.waterAt(pos.getX(), pos.getY(), pos.getZ()))
			return net.minecraft.world.level.block.Blocks.WATER.defaultBlockState();
		return state;
	}
}
