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
		// xo/yo/zo is where this tick started: The Forest lerps from there to here, like MC's own renderer.
		ForestLink.publishPlayer(player.xo, player.yo, player.zo, player.getX(), player.getY(), player.getZ(),
			player.getYRot(), player.getXRot(), player.onGround(), ForestLink.teleportAck());
	}
}
