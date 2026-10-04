package dev.forestcraft.mixin;

import net.minecraft.core.BlockPos;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.world.level.block.state.BlockState;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/** Every block change on the server: a revealed block broken (by anyone, by anything) opens its cell. */
@Mixin(ServerLevel.class)
public abstract class ServerLevelMixin {
	@Inject(method = "updatePOIOnBlockStateChange", at = @At("HEAD"))
	private void forestcraft$changed(BlockPos pos, BlockState old, BlockState now, CallbackInfo ci) {
		dev.forestcraft.TerrainDig.blockChanged(pos, old, now);
	}
}
