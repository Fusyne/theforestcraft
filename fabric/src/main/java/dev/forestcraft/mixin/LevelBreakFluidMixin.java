package dev.forestcraft.mixin;

import com.llamalad7.mixinextras.injector.wrapoperation.Operation;
import com.llamalad7.mixinextras.injector.wrapoperation.WrapOperation;
import net.minecraft.core.BlockPos;
import net.minecraft.world.level.Level;
import net.minecraft.world.level.material.FluidState;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;

/**
 * A block broken where The Forest has water: Minecraft leaves behind "the fluid at that spot",
 * and The Forest's lake answered water (LevelFluidMixin), so a real water source block was
 * placed. What a broken block leaves is only its own fluid (a waterlogged slab's water), not
 * The Forest's lake, which is still there for swimming anyway.
 */
@Mixin(Level.class)
public abstract class LevelBreakFluidMixin {
	@WrapOperation(method = { "removeBlock", "destroyBlock(Lnet/minecraft/core/BlockPos;ZLnet/minecraft/world/entity/Entity;I)Z" },
		at = @At(value = "INVOKE", target = "Lnet/minecraft/world/level/Level;getFluidState(Lnet/minecraft/core/BlockPos;)Lnet/minecraft/world/level/material/FluidState;"))
	private FluidState forestcraft$ownFluidOnly(Level level, BlockPos pos, Operation<FluidState> original) {
		return level.getBlockState(pos).getFluidState();
	}
}
