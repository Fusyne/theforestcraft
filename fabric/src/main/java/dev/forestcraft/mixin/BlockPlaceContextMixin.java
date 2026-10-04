package dev.forestcraft.mixin;

import dev.forestcraft.ForestLink;
import net.minecraft.core.BlockPos;
import net.minecraft.core.Direction;
import net.minecraft.world.InteractionHand;
import net.minecraft.world.entity.player.Player;
import net.minecraft.world.item.ItemStack;
import net.minecraft.world.item.context.BlockPlaceContext;
import net.minecraft.world.level.Level;
import net.minecraft.world.phys.BlockHitResult;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.Shadow;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/**
 * Clicking The Forest hits an air block that only has a Forest shape (the island's top block, a
 * tree...). Vanilla would place the block right there (air is replaceable), i.e. inside the
 * ground or the tree. Place it against the face instead, like on a real block.
 */
@Mixin(BlockPlaceContext.class)
public abstract class BlockPlaceContextMixin {
	@Shadow protected boolean replaceClicked;

	@Inject(
		method = "<init>(Lnet/minecraft/world/level/Level;Lnet/minecraft/world/entity/player/Player;Lnet/minecraft/world/InteractionHand;Lnet/minecraft/world/item/ItemStack;Lnet/minecraft/world/phys/BlockHitResult;)V",
		at = @At("TAIL")
	)
	private void forestcraft$againstForest(Level level, Player player, InteractionHand hand, ItemStack stack, BlockHitResult hit, CallbackInfo ci) {
		if (!replaceClicked) return;
		BlockPos pos = hit.getBlockPos();
		if (!level.getBlockState(pos).isAir()) return;
		if (ForestLink.aimShape(pos.getX(), pos.getY(), pos.getZ()) == null) return;
		replaceClicked = false;
	}
}
