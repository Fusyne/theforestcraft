package dev.forestcraft.mixin;

import dev.forestcraft.ForestLink;
import net.minecraft.world.entity.EntityFluidInteraction;
import net.minecraft.world.level.Level;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/**
 * Entities only look for fluids where their chunk sections say they hold some: The Forest's water
 * isn't in any section, so swimming never started. Say yes where The Forest has water.
 */
@Mixin(EntityFluidInteraction.class)
public abstract class EntityFluidMixin {
	@Inject(method = "hasFluidAndLoaded", at = @At("HEAD"), cancellable = true)
	private static void forestcraft$forestWater(Level level, int x0, int y0, int z0, int x1, int y1, int z1, CallbackInfoReturnable<Boolean> cir) {
		if (level.dimension() == Level.OVERWORLD && ForestLink.anyWaterIn(x0, y0, z0, x1, y1, z1)) cir.setReturnValue(true);
	}
}
