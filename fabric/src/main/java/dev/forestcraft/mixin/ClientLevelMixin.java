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
 * setSectionDirtyWithNeighbors is only for whole chunk loads, so hooking it alone meant a
 * section was sent once and never updated (second block invisible, mined block still there).
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

	@Inject(method = "setSectionDirtyWithNeighbors", at = @At("HEAD"))
	private void forestcraft$sectionDirty(int x, int y, int z, CallbackInfo ci) {
		BlockExport.markDirty(x, y, z);
	}

	@Inject(method = "destroyBlockProgress", at = @At("HEAD"))
	private void forestcraft$crack(int breaker, BlockPos pos, int progress, CallbackInfo ci) {
		BlockExport.crack(breaker, pos, progress);
	}
}
