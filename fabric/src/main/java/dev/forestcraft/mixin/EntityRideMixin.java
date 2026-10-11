package dev.forestcraft.mixin;

import dev.forestcraft.ForestLink;
import net.minecraft.client.player.LocalPlayer;
import net.minecraft.world.entity.Entity;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/**
 * In a boat (or on a horse, in a minecart), the player's tick runs before the vehicle places him:
 * published from there, every tick started and ended on the same spot, so The Forest's camera had
 * nothing to interpolate and moved in 20 steps a second. Published once he is in his seat.
 */
@Mixin(Entity.class)
public abstract class EntityRideMixin {
	@Inject(method = "rideTick", at = @At("TAIL"))
	private void forestcraft$seated(CallbackInfo ci) {
		if ((Object) this instanceof LocalPlayer player && player.isPassenger()) ForestLink.publishPlayer(player);
	}
}
