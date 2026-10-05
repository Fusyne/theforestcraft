package dev.forestcraft.mixin;

import dev.forestcraft.Shots;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.util.Mth;
import net.minecraft.world.entity.projectile.arrow.AbstractArrow;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.Shadow;
import org.spongepowered.asm.mixin.Unique;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/** Each tick of a flying arrow goes to The Forest, which checks it against its creatures (Shots). */
@Mixin(AbstractArrow.class)
public abstract class AbstractArrowMixin {
	@Shadow private double baseDamage;
	@Unique private double forestcraft$x, forestcraft$y, forestcraft$z;

	@Inject(method = "tick", at = @At("HEAD"))
	private void forestcraft$from(CallbackInfo ci) {
		AbstractArrow self = (AbstractArrow) (Object) this;
		forestcraft$x = self.getX();
		forestcraft$y = self.getY();
		forestcraft$z = self.getZ();
	}

	@Inject(method = "tick", at = @At("TAIL"))
	private void forestcraft$to(CallbackInfo ci) {
		AbstractArrow self = (AbstractArrow) (Object) this;
		if (!(self.level() instanceof ServerLevel) || self.isRemoved()) return;
		double dx = self.getX() - forestcraft$x, dy = self.getY() - forestcraft$y, dz = self.getZ() - forestcraft$z;
		double len = Math.sqrt(dx * dx + dy * dy + dz * dz);
		if (len < 0.05) return; // resting (stuck in something)
		// Minecraft's own arrow damage: speed (blocks per tick) times base damage.
		float damage = (float) Mth.ceil(Mth.clamp(len * baseDamage, 0.0, 1000.0));
		Shots.flew(self.getId(), forestcraft$x, forestcraft$y, forestcraft$z, self.getX(), self.getY(), self.getZ(), damage);
	}
}
