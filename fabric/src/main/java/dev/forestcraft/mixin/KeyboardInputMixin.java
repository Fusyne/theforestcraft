package dev.forestcraft.mixin;

import dev.forestcraft.ForestLink;
import dev.forestcraft.Proto;
import net.minecraft.client.Minecraft;
import net.minecraft.client.player.ClientInput;
import net.minecraft.client.player.KeyboardInput;
import net.minecraft.world.entity.player.Input;
import net.minecraft.world.phys.Vec2;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/**
 * Movement keys come from The Forest's window, not from Minecraft's.
 * KeyboardInput.tick builds moveVector from the hidden window's keys (always zero),
 * and LocalPlayer walks with moveVector, so both keyPresses and moveVector are replaced.
 * The look direction is The Forest's (mouse handled there every frame); it is applied
 * here, right before LocalPlayer moves, so W walks where the camera looks.
 */
@Mixin(KeyboardInput.class)
public abstract class KeyboardInputMixin extends ClientInput {
	@Inject(method = "tick", at = @At("TAIL"))
	private void forestcraft$keys(CallbackInfo ci) {
		ForestLink.ForestSnapshot forest = ForestLink.readForest();
		if (forest == null || (forest.flags() & Proto.IN_GAME) == 0 || (forest.flags() & Proto.MENU) != 0) return;
		int bits = forest.input();
		boolean forward = (bits & 1) != 0;
		boolean backward = (bits & 2) != 0;
		boolean left = (bits & 4) != 0;
		boolean right = (bits & 8) != 0;
		this.keyPresses = new Input(forward, backward, left, right, (bits & 16) != 0, (bits & 32) != 0, (bits & 64) != 0);
		float z = forward == backward ? 0f : (forward ? 1f : -1f);
		float x = left == right ? 0f : (left ? 1f : -1f);
		this.moveVector = new Vec2(x, z).normalized();
		if (forest.teleportSeq() != 0 && !dev.forestcraft.ScreenBridge.screenOpen(Minecraft.getInstance())) ForestLink.applyLook(Minecraft.getInstance().player, forest, false);
	}
}
