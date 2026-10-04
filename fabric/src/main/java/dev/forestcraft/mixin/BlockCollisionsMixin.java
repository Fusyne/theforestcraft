package dev.forestcraft.mixin;

import com.llamalad7.mixinextras.injector.wrapoperation.Operation;
import com.llamalad7.mixinextras.injector.wrapoperation.WrapOperation;
import dev.forestcraft.ForestLink;
import net.minecraft.core.BlockPos;
import net.minecraft.world.level.BlockCollisions;
import net.minecraft.world.level.CollisionGetter;
import net.minecraft.world.level.block.state.BlockState;
import net.minecraft.world.phys.shapes.CollisionContext;
import net.minecraft.world.phys.shapes.Shapes;
import net.minecraft.world.phys.shapes.VoxelShape;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;

@Mixin(BlockCollisions.class)
public abstract class BlockCollisionsMixin {
	@WrapOperation(
		method = "computeNext",
		at = @At(
			value = "INVOKE",
			target = "Lnet/minecraft/world/phys/shapes/CollisionContext;getCollisionShape(Lnet/minecraft/world/level/block/state/BlockState;Lnet/minecraft/world/level/CollisionGetter;Lnet/minecraft/core/BlockPos;)Lnet/minecraft/world/phys/shapes/VoxelShape;"
		)
	)
	private VoxelShape forestcraft$surface(CollisionContext context, BlockState state, CollisionGetter level, BlockPos pos, Operation<VoxelShape> original) {
		VoxelShape block = original.call(context, state, level, pos);
		int x = pos.getX(), y = pos.getY(), z = pos.getZ();
		VoxelShape extra = null;
		double top = ForestLink.surfaceIn(x, y, z);
		if (!Double.isNaN(top)) extra = Shapes.box(0, 0, 0, 1, top, 1);
		VoxelShape solid = ForestLink.solidShape(x, y, z);
		if (solid != null) extra = extra == null ? solid : Shapes.or(extra, solid);
		if (extra == null) return block;
		return block.isEmpty() ? extra : Shapes.or(block, extra);
	}
}
