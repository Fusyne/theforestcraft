package dev.forestcraft.mixin;

import dev.forestcraft.ForestLink;
import net.minecraft.util.Mth;
import net.minecraft.world.entity.LivingEntity;
import net.minecraft.world.entity.player.Player;
import net.minecraft.world.level.Level;
import net.minecraft.world.phys.Vec3;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/**
 * Animals and monsters on The Forest's island: they climb its slopes like the player does (a
 * full block step), and where Minecraft doesn't know the ground yet (too far from the player)
 * they wait instead of falling into the void under the island.
 */
@Mixin(LivingEntity.class)
public abstract class MobGroundMixin {
	private boolean forestcraft$onIsland() {
		LivingEntity self = (LivingEntity) (Object) this;
		if (self instanceof Player || ForestLink.buffer() == null) return false;
		Level level = self.level();
		return level.dimension() == Level.OVERWORLD;
	}

	@Inject(method = "travel", at = @At("HEAD"), cancellable = true)
	private void forestcraft$waitForGround(Vec3 input, CallbackInfo ci) {
		if (!forestcraft$onIsland()) return;
		LivingEntity self = (LivingEntity) (Object) this;
		if (self.isPassenger() || self.isNoGravity()) return;
		if (ForestLink.knownGround(Mth.floor(self.getX()), Mth.floor(self.getZ()))) return;
		self.setDeltaMovement(Vec3.ZERO);
		self.resetFallDistance();
		ci.cancel();
	}

	@Inject(method = "maxUpStep", at = @At("RETURN"), cancellable = true)
	private void forestcraft$islandStep(CallbackInfoReturnable<Float> cir) {
		if (cir.getReturnValueF() < 1.0f && forestcraft$onIsland()) cir.setReturnValue(1.0f);
	}
}
