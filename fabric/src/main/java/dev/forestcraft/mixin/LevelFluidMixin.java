package dev.forestcraft.mixin;

import dev.forestcraft.ForestLink;
import net.minecraft.core.BlockPos;
import net.minecraft.world.level.Level;
import net.minecraft.world.level.material.FluidState;
import net.minecraft.world.level.material.Fluids;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/** The Forest's lakes and sea are water for Minecraft: swimming, sinking, drowning, buckets. */
@Mixin(Level.class)
public abstract class LevelFluidMixin {
	@Inject(method = "getFluidState", at = @At("RETURN"), cancellable = true)
	private void forestcraft$forestWater(BlockPos pos, CallbackInfoReturnable<FluidState> cir) {
		if (!cir.getReturnValue().isEmpty()) return;
		if (((Level) (Object) this).dimension() != Level.OVERWORLD) return;
		if (!ForestLink.waterAt(pos.getX(), pos.getY(), pos.getZ())) return;
		// The top block holds only as much water as The Forest's surface leaves in it, so
		// Minecraft's water line (swimming, floating, the eyes going under) is The Forest's.
		float fill = ForestLink.waterFill(pos.getX(), pos.getY(), pos.getZ());
		if (fill >= 8f / 9f) cir.setReturnValue(Fluids.WATER.getSource(false));
		else cir.setReturnValue(Fluids.FLOWING_WATER.getFlowing(Math.max(1, Math.min(7, Math.round(fill * 9f))), false));
	}
}
