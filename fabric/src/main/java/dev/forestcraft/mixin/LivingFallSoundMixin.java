package dev.forestcraft.mixin;

import com.llamalad7.mixinextras.injector.wrapoperation.Operation;
import com.llamalad7.mixinextras.injector.wrapoperation.WrapOperation;
import dev.forestcraft.ForestLink;
import net.minecraft.core.BlockPos;
import net.minecraft.world.entity.LivingEntity;
import net.minecraft.world.level.Level;
import net.minecraft.world.level.block.state.BlockState;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;

/** The thud of a fall onto The Forest's ground. */
@Mixin(LivingEntity.class)
public abstract class LivingFallSoundMixin {
	@WrapOperation(method = "playBlockFallSound", at = @At(value = "INVOKE",
		target = "Lnet/minecraft/world/level/Level;getBlockState(Lnet/minecraft/core/BlockPos;)Lnet/minecraft/world/level/block/state/BlockState;"))
	private BlockState forestcraft$landedOn(Level level, BlockPos pos, Operation<BlockState> original) {
		BlockState state = original.call(level, pos);
		if (!state.isAir() || level.dimension() != Level.OVERWORLD) return state;
		BlockState forest = ForestLink.groundState(pos.getX(), pos.getY(), pos.getZ());
		return forest != null ? forest : state;
	}
}
