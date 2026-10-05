package dev.forestcraft.mixin;

import dev.forestcraft.ForestLink;
import net.minecraft.core.BlockPos;
import net.minecraft.world.entity.ai.navigation.PathNavigation;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/** A place standing on The Forest's ground is a fine destination for a wandering mob. */
@Mixin(PathNavigation.class)
public abstract class MobNavigationMixin {
	@Inject(method = "isStableDestination", at = @At("HEAD"), cancellable = true)
	private void forestcraft$forestFloor(BlockPos pos, CallbackInfoReturnable<Boolean> cir) {
		if (ForestLink.buffer() == null) return;
		if (!Double.isNaN(ForestLink.forestTop(pos.getX(), pos.getY() - 1, pos.getZ()))) cir.setReturnValue(true);
	}
}
