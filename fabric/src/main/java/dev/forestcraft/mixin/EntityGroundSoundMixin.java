package dev.forestcraft.mixin;

import com.llamalad7.mixinextras.injector.wrapoperation.Operation;
import com.llamalad7.mixinextras.injector.wrapoperation.WrapOperation;
import dev.forestcraft.ForestLink;
import net.minecraft.core.BlockPos;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.level.Level;
import net.minecraft.world.level.block.state.BlockState;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;

/**
 * Footsteps, landings and sprint dust on The Forest's ground: Minecraft looked at the block
 * under the feet, found air (the island isn't made of blocks) and stayed silent.
 */
@Mixin(Entity.class)
public abstract class EntityGroundSoundMixin {
	@WrapOperation(method = {"move", "spawnSprintParticle"}, at = @At(value = "INVOKE",
		target = "Lnet/minecraft/world/level/Level;getBlockState(Lnet/minecraft/core/BlockPos;)Lnet/minecraft/world/level/block/state/BlockState;"))
	private BlockState forestcraft$groundUnderFeet(Level level, BlockPos pos, Operation<BlockState> original) {
		BlockState state = original.call(level, pos);
		if (!state.isAir() || level.dimension() != Level.OVERWORLD) return state;
		BlockState forest = ForestLink.groundState(pos.getX(), pos.getY(), pos.getZ());
		return forest != null ? forest : state;
	}
}
