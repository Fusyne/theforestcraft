package dev.forestcraft.mixin;

import dev.forestcraft.BlockExport;
import net.minecraft.client.multiplayer.ClientLevel;
import net.minecraft.core.BlockPos;
import net.minecraft.world.level.block.state.BlockState;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/**
 * A block changed: its section (and neighbours, for face culling) is re-sent to The Forest.
 * In 26.2 a placed or broken block goes through sendBlockUpdated -> LevelExtractor.blockChanged;
 * setSectionDirtyWithNeighbors only comes with light updates (Minecraft's light is not used by
 * The Forest's meshes): hooking it re-sent 27 sections per light change, the lag when digging.
 */
@Mixin(ClientLevel.class)
public abstract class ClientLevelMixin {
	@Inject(method = "sendBlockUpdated", at = @At("HEAD"))
	private void forestcraft$changed(BlockPos pos, BlockState oldState, BlockState newState, int flags, CallbackInfo ci) {
		BlockExport.markBlock(pos.getX(), pos.getY(), pos.getZ());
	}

	@Inject(method = "setBlocksDirty", at = @At("HEAD"))
	private void forestcraft$blocksDirty(BlockPos pos, BlockState oldState, BlockState newState, CallbackInfo ci) {
		BlockExport.markBlock(pos.getX(), pos.getY(), pos.getZ());
	}

	@Inject(method = "destroyBlockProgress", at = @At("HEAD"))
	private void forestcraft$crack(int breaker, BlockPos pos, int progress, CallbackInfo ci) {
		BlockExport.crack(breaker, pos, progress);
	}
}
