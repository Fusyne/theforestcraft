package dev.forestcraft.mixin;

import dev.forestcraft.ForestLink;
import net.minecraft.client.player.LocalPlayer;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

@Mixin(LocalPlayer.class)
public abstract class LocalPlayerMixin {
	@Inject(method = "tick", at = @At("TAIL"))
	private void forestcraft$publish(CallbackInfo ci) {
		LocalPlayer player = (LocalPlayer) (Object) this;
		ForestLink.refreshGrid();
		ForestLink.stepUp(player);
		// Riding (a boat...): the vehicle places the player only after this tick, in rideTick.
		if (player.isPassenger()) return;
		ForestLink.publishPlayer(player);
	}
}
