package dev.forestcraft.mixin;

import dev.forestcraft.ForestLink;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.world.damagesource.DamageSource;
import net.minecraft.world.damagesource.DamageTypes;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

@Mixin(ServerPlayer.class)
public abstract class ServerPlayerMixin {
	@Inject(method = "tick", at = @At("HEAD"))
	private void forestcraft$tick(CallbackInfo ci) {
		ServerPlayer player = (ServerPlayer) (Object) this;
		ForestLink.rescue(player);
		ForestLink.syncWorld(player);
		dev.forestcraft.TerrainDig.serverTick(player.level());
	}

	@Inject(method = "hurtServer", at = @At("HEAD"), cancellable = true)
	private void forestcraft$noVoid(ServerLevel level, DamageSource source, float amount, CallbackInfoReturnable<Boolean> cir) {
		if (source.is(DamageTypes.FELL_OUT_OF_WORLD)) cir.setReturnValue(false);
	}

	@Inject(method = "die", at = @At("HEAD"), cancellable = true)
	private void forestcraft$noDeath(DamageSource source, CallbackInfo ci) {
		if (!source.is(DamageTypes.FELL_OUT_OF_WORLD)) return;
		ci.cancel();
		ServerPlayer self = (ServerPlayer) (Object) this;
		self.setHealth(self.getMaxHealth());
		ForestLink.rescue(self);
	}
}
