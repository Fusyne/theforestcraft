package dev.forestcraft.mixin;

import dev.forestcraft.Hazards;
import dev.forestcraft.TerrainDig;
import net.minecraft.world.level.ServerExplosion;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/**
 * TNT, creepers, fire charges...: The Forest's creatures around it are blown up too, and when
 * the blast may break blocks, a small crater opens in The Forest's ground.
 */
@Mixin(ServerExplosion.class)
public abstract class ServerExplosionMixin {
	@Inject(method = "explode", at = @At("HEAD"))
	private void forestcraft$blast(CallbackInfoReturnable<Integer> cir) {
		ServerExplosion self = (ServerExplosion) (Object) this;
		Hazards.explosion(self.center(), self.radius());
	}

	@Inject(method = "explode", at = @At("RETURN"))
	private void forestcraft$crater(CallbackInfoReturnable<Integer> cir) {
		ServerExplosion self = (ServerExplosion) (Object) this;
		if (self.getBlockInteraction() == net.minecraft.world.level.Explosion.BlockInteraction.KEEP) return;
		TerrainDig.blast(self.level(), self.center(), self.radius());
	}
}
