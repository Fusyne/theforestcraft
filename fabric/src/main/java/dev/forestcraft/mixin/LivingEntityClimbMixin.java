package dev.forestcraft.mixin;

import dev.forestcraft.ForestLink;
import net.minecraft.world.entity.LivingEntity;
import net.minecraft.world.entity.player.Player;
import net.minecraft.world.phys.Vec3;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.Shadow;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/**
 * The Forest's ropes (into the caves) and climbable walls are ladders for the player. A rope
 * hangs in the open, with no wall to walk into (what makes a ladder climb in Minecraft), so on
 * one: forward or jump climbs, back goes down, sneak holds on, and the body is drawn to the rope
 * instead of drifting off it (except standing at its foot, to walk away, or above its top).
 * A rope is only taken hold of when the player is on it (off the ground within reach) or walks
 * up to it facing it: running past one must not slow down to a ladder's pace.
 */
@Mixin(LivingEntity.class)
public abstract class LivingEntityClimbMixin {
	@Shadow protected boolean jumping;

	@Inject(method = "onClimbable", at = @At("RETURN"), cancellable = true)
	private void forestcraft$rope(CallbackInfoReturnable<Boolean> cir) {
		if (cir.getReturnValueZ()) return;
		LivingEntity self = (LivingEntity) (Object) this;
		if (!(self instanceof Player) || self.isSpectator()) return;
		double[] rope = ForestLink.ropeAxis(self.getX(), self.getY(), self.getZ());
		if (rope != null && forestcraft$holds(self, rope)) cir.setReturnValue(true);
	}

	private static boolean forestcraft$holds(LivingEntity self, double[] rope) {
		if (!self.onGround()) return true;
		double dx = rope[0] - self.getX(), dz = rope[1] - self.getZ();
		double d = Math.sqrt(dx * dx + dz * dz);
		if (d < 0.3) return true;
		if (d > 0.9) return false;
		Vec3 look = self.getLookAngle();
		double l = Math.sqrt(look.x * look.x + look.z * look.z);
		return l > 1.0e-3 && (dx * look.x + dz * look.z) / (d * l) > 0.6;
	}

	@Inject(method = "handleRelativeFrictionAndCalculateMovement", at = @At("RETURN"), cancellable = true)
	private void forestcraft$ropeClimb(Vec3 input, float friction, CallbackInfoReturnable<Vec3> cir) {
		LivingEntity self = (LivingEntity) (Object) this;
		if (!(self instanceof Player player) || self.isSpectator() || player.getAbilities().flying) return;
		double[] rope = ForestLink.ropeAxis(self.getX(), self.getY(), self.getZ());
		if (rope == null || !forestcraft$holds(self, rope)) return;
		ForestLink.ropeHeld(self.getX(), self.getY(), self.getZ());
		Vec3 m = cir.getReturnValue();
		boolean atFoot = self.onGround() && self.zza <= 0.01f;
		double y = m.y;
		if (self.zza > 0.01f || this.jumping) y = 0.2;
		else if (self.zza < -0.01f) y = -0.15;
		else if (self.isShiftKeyDown()) y = Math.max(y, 0.0);
		double x = m.x, z = m.z;
		if (!atFoot && rope[2] < 0.97) {
			x = m.x * 0.3 + (rope[0] - self.getX()) * 0.25;
			z = m.z * 0.3 + (rope[1] - self.getZ()) * 0.25;
		}
		cir.setReturnValue(new Vec3(x, y, z));
	}
}
