package dev.forestcraft.mixin;

import dev.forestcraft.ForestLink;
import net.minecraft.core.BlockPos;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.util.RandomSource;
import net.minecraft.world.level.Level;
import net.minecraft.world.level.block.FallingBlock;
import net.minecraft.world.level.block.state.BlockState;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/**
 * Sand and gravel resting on The Forest's ground (or a rock, a tree...) stay put: for Minecraft
 * the block under them is air. Dig the ground out from under them and they fall as usual.
 */
@Mixin(FallingBlock.class)
public abstract class FallingBlockMixin {
	private static boolean forestcraft$supported(Level level, BlockPos pos) {
		if (ForestLink.buffer() == null || level.dimension() != Level.OVERWORLD) return false;
		if (!level.getBlockState(pos.below()).isAir()) return false;
		return !Double.isNaN(ForestLink.forestTop(pos.getX(), pos.getY() - 1, pos.getZ()));
	}

	@Inject(method = "tick", at = @At("HEAD"), cancellable = true)
	private void forestcraft$stayOnGround(BlockState state, ServerLevel level, BlockPos pos, RandomSource random, CallbackInfo ci) {
		if (forestcraft$supported(level, pos)) ci.cancel();
	}

	@Inject(method = "animateTick", at = @At("HEAD"), cancellable = true)
	private void forestcraft$noDust(BlockState state, Level level, BlockPos pos, RandomSource random, CallbackInfo ci) {
		if (forestcraft$supported(level, pos)) ci.cancel();
	}
}
