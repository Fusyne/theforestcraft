package dev.forestcraft.mixin;

import dev.forestcraft.ForestLink;
import net.minecraft.world.entity.Avatar;
import net.minecraft.world.entity.EntityDimensions;
import net.minecraft.world.entity.Pose;
import net.minecraft.world.entity.player.Player;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/**
 * A bigger Steve (blocks bigger than 1.5 Forest units) gets a narrower hitbox, so that in The
 * Forest he is as wide as before: he still fits the plane's door, the cabin, cave passages.
 * His height (1.8 blocks, under 2 for Minecraft's own doorways) doesn't change.
 */
@Mixin(Avatar.class)
public abstract class AvatarSizeMixin {
	@Inject(method = "getDefaultDimensions", at = @At("RETURN"), cancellable = true)
	private void forestcraft$narrower(Pose pose, CallbackInfoReturnable<EntityDimensions> cir) {
		if (!((Object) this instanceof Player) || ForestLink.buffer() == null) return;
		float f = ForestLink.hitboxWidthFactor();
		if (f >= 0.999f) return;
		cir.setReturnValue(cir.getReturnValue().scale(f, 1f));
	}
}
