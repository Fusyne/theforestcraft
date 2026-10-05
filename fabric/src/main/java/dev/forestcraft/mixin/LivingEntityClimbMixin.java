package dev.forestcraft.mixin;

import dev.forestcraft.ForestLink;
import net.minecraft.world.entity.LivingEntity;
import net.minecraft.world.entity.player.Player;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/** The Forest's ropes (into the caves) and climbable walls are ladders for the player. */
@Mixin(LivingEntity.class)
public abstract class LivingEntityClimbMixin {
	@Inject(method = "onClimbable", at = @At("RETURN"), cancellable = true)
	private void forestcraft$rope(CallbackInfoReturnable<Boolean> cir) {
		if (cir.getReturnValueZ()) return;
		LivingEntity self = (LivingEntity) (Object) this;
		if (!(self instanceof Player) || self.isSpectator()) return;
		if (ForestLink.ropeAt(self.getX(), self.getY(), self.getZ())) cir.setReturnValue(true);
	}
}
