package dev.forestcraft.mixin;

import net.minecraft.server.level.ServerLevel;
import net.minecraft.world.level.levelgen.PhantomSpawner;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/**
 * Minecraft's night follows The Forest's, and nobody sleeps in a Minecraft bed here: after an
 * hour of play, every night would bring phantoms diving from The Forest's sky. Not in this world.
 */
@Mixin(PhantomSpawner.class)
public abstract class PhantomSpawnerMixin {
	@Inject(method = "tick", at = @At("HEAD"), cancellable = true)
	private void forestcraft$noPhantoms(ServerLevel level, boolean spawnEnemies, CallbackInfo ci) {
		ci.cancel();
	}
}
