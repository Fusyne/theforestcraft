package dev.forestcraft.mixin;

import dev.forestcraft.ForestLink;
import net.minecraft.client.gui.GuiGraphicsExtractor;
import net.minecraft.client.gui.Hud;
import net.minecraft.world.entity.Entity;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/** The vignette is a full-screen pass: over The Forest it would darken (and un-mask) the whole frame. */
@Mixin(Hud.class)
public abstract class HudMixin {
	@Inject(method = "extractVignette", at = @At("HEAD"), cancellable = true)
	private void forestcraft$noVignette(GuiGraphicsExtractor graphics, Entity entity, CallbackInfo ci) {
		if (ForestLink.forestInGame()) ci.cancel();
	}
}
