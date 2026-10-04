package dev.forestcraft.mixin;

import dev.forestcraft.ForestLink;
import dev.forestcraft.FrameCapture;
import dev.forestcraft.Proto;
import dev.forestcraft.WorldStart;
import net.minecraft.client.Minecraft;
import net.minecraft.client.gui.screens.Screen;
import org.lwjgl.glfw.GLFW;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.gen.Invoker;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

@Mixin(Minecraft.class)
public abstract class MinecraftMixin {
	private boolean forestcraft$hidden;
	private boolean forestcraft$stopping;
	private boolean forestcraft$attack;
	private boolean forestcraft$use;

	@Invoker("startAttack")
	protected abstract boolean forestcraft$startAttack();

	@Invoker("continueAttack")
	protected abstract void forestcraft$continueAttack(boolean leftClick);

	@Invoker("startUseItem")
	protected abstract void forestcraft$startUseItem();

	@Inject(method = "tick", at = @At("HEAD"))
	private void forestcraft$tick(CallbackInfo ci) {
		Minecraft minecraft = (Minecraft) (Object) this;
		minecraft.options.pauseOnLostFocus = false;
		// E is The Forest's interact key; Minecraft's inventory is I (also closes it from inside).
		var inventoryKey = com.mojang.blaze3d.platform.InputConstants.Type.KEYSYM.getOrCreate(org.lwjgl.glfw.GLFW.GLFW_KEY_I);
		if (!minecraft.options.keyInventory.matches(inventoryKey)) {
			minecraft.options.keyInventory.setKey(inventoryKey);
			net.minecraft.client.KeyMapping.resetMapping();
			minecraft.options.save();
		}
		// The window is hidden on purpose: the AFK limiter would drop the hand/HUD to a slideshow.
		if (minecraft.options.inactivityFpsLimit().get() != net.minecraft.client.InactivityFpsLimit.MINIMIZED)
			minecraft.options.inactivityFpsLimit().set(net.minecraft.client.InactivityFpsLimit.MINIMIZED);
		if (ForestLink.open()) ForestLink.heartbeat();
		if (!forestcraft$stopping && ForestLink.forestClosed()) {
			forestcraft$stopping = true;
			ForestLink.LOG.info("The Forest closed; stopping Minecraft");
			minecraft.stop();
			return;
		}
		WorldStart.tick(minecraft);
		ForestLink.ForestSnapshot forest = ForestLink.readForest();
		boolean playing = forest != null && (forest.flags() & Proto.IN_GAME) != 0 && (forest.flags() & Proto.MENU) == 0 && minecraft.player != null;
		if (!playing) {
			if (forestcraft$attack || forestcraft$use) {
				minecraft.options.keyAttack.setDown(false);
				minecraft.options.keyUse.setDown(false);
			}
			forestcraft$attack = false;
			forestcraft$use = false;
			return;
		}
		forestcraft$view(minecraft);
		dev.forestcraft.BlockExport.tick(minecraft);
		dev.forestcraft.TerrainDig.pump();
		if (!forestcraft$hidden) {
			GLFW.glfwHideWindow(minecraft.getWindow().handle());
			forestcraft$hidden = true;
			ForestLink.LOG.info("Minecraft window hidden; The Forest has the controls");
		}
		// Mouse buttons from The Forest drive Minecraft's own key mappings, so handleKeybinds does
		// the rest exactly like vanilla: hold to mine (no reset every tick), hold right click to
		// keep placing every 4 ticks, release to stop eating / drawing a bow.
		boolean attack = (forest.input() & 128) != 0;
		boolean use = (forest.input() & 256) != 0;
		minecraft.options.keyAttack.setDown(attack);
		minecraft.options.keyUse.setDown(use);
		if (attack && !forestcraft$attack) forestcraft$startAttack();
		forestcraft$forestAim(minecraft, attack);
		forestcraft$combat(minecraft);
		dev.forestcraft.TerrainDig.collectLogs(minecraft);
		dev.forestcraft.TerrainDig.collectGifts(minecraft);
		forestcraft$attack = attack;
		forestcraft$use = use;
	}

	private int forestcraft$frameW, forestcraft$frameH;
	private int forestcraft$damageSeq = Integer.MIN_VALUE;
	private float forestcraft$damageDone;

	/**
	 * Fighting The Forest's natives: publish what a hit would do (held item's attack damage and the
	 * cooldown bar) and take the damage they deal us (The Forest sends a running total).
	 */
	private void forestcraft$combat(Minecraft minecraft) {
		var map = ForestLink.buffer();
		if (map == null || minecraft.player == null) return;
		var player = minecraft.player;
		map.putFloat(Proto.OFF_MC + 168, (float) player.getAttributeValue(net.minecraft.world.entity.ai.attributes.Attributes.ATTACK_DAMAGE));
		map.putFloat(Proto.OFF_MC + 172, player.getAttackStrengthScale(0.5f));
		int seq = map.getInt(Proto.OFF_FOREST + 124);
		float total = map.getFloat(Proto.OFF_FOREST + 128);
		if (forestcraft$damageSeq == Integer.MIN_VALUE || total < forestcraft$damageDone) {
			forestcraft$damageSeq = seq;
			forestcraft$damageDone = total;
			return;
		}
		if (seq == forestcraft$damageSeq) return;
		forestcraft$damageSeq = seq;
		float amount = total - forestcraft$damageDone;
		forestcraft$damageDone = total;
		if (amount <= 0f) return;
		var server = minecraft.getSingleplayerServer();
		if (server == null) return;
		java.util.UUID id = player.getUUID();
		server.execute(() -> {
			var p = server.getPlayerList().getPlayer(id);
			if (p == null) return;
			var level = p.level();
			p.hurtServer(level, level.damageSources().generic(), amount);
		});
	}
	private int forestcraft$swing;

	/**
	 * Aiming at The Forest itself (an air block that only has a Forest shape): 1 = tree/rock/plane,
	 * 2 = the island's ground. The Forest chops the tree; the ground is converted to blocks.
	 */
	private void forestcraft$forestAim(Minecraft minecraft, boolean attack) {
		int aim = 0;
		net.minecraft.core.BlockPos pos = null;
		if (minecraft.level != null && minecraft.hitResult instanceof net.minecraft.world.phys.BlockHitResult hit
			&& hit.getType() == net.minecraft.world.phys.HitResult.Type.BLOCK) {
			pos = hit.getBlockPos();
			if (minecraft.level.getBlockState(pos).isAir()) {
				if (ForestLink.solidShape(pos.getX(), pos.getY(), pos.getZ()) != null) aim = 1;
				else if (ForestLink.aimShape(pos.getX(), pos.getY(), pos.getZ()) != null) aim = 2;
			}
		}
		if (ForestLink.buffer() != null) ForestLink.buffer().putInt(Proto.OFF_MC + 128, aim);
		if (!attack || aim != 2) dev.forestcraft.TerrainDig.stop(minecraft);
		if (!attack || aim == 0 || minecraft.player == null) { forestcraft$swing = 0; return; }
		if (forestcraft$swing++ % 5 == 0) minecraft.player.swing(net.minecraft.world.InteractionHand.MAIN_HAND);
		if (aim == 2) dev.forestcraft.TerrainDig.request(minecraft, pos);
	}

	/** F5 mode, frame size and hotbar slot come from The Forest's window. */
	private void forestcraft$view(Minecraft minecraft) {
		ForestLink.ForestView view = ForestLink.readView();
		if (view == null) return;
		net.minecraft.client.CameraType wanted = switch (view.cameraMode()) {
			case 1 -> net.minecraft.client.CameraType.THIRD_PERSON_BACK;
			case 2 -> net.minecraft.client.CameraType.THIRD_PERSON_FRONT;
			default -> net.minecraft.client.CameraType.FIRST_PERSON;
		};
		if (minecraft.options.getCameraType() != wanted) minecraft.options.setCameraType(wanted);
		if (minecraft.options.cloudStatus().get() != net.minecraft.client.CloudStatus.OFF)
			minecraft.options.cloudStatus().set(net.minecraft.client.CloudStatus.OFF);
		int w = view.frameW(), h = view.frameH();
		if (w >= 320 && h >= 240 && w <= 1920 && h <= 1080 && (w != forestcraft$frameW || h != forestcraft$frameH)) {
			forestcraft$frameW = w;
			forestcraft$frameH = h;
			GLFW.glfwSetWindowSize(minecraft.getWindow().handle(), w, h);
			ForestLink.LOG.info("Minecraft frame size {}x{}", w, h);
		}
		int slot = view.slot();
		if (minecraft.player != null && slot >= 0 && slot < 9 && minecraft.player.getInventory().getSelectedSlot() != slot)
			minecraft.player.getInventory().setSelectedSlot(slot);
	}

	@Inject(method = "renderFrame", at = @At("HEAD"))
	private void forestcraft$look(boolean advanceGameTime, CallbackInfo ci) {
		Minecraft minecraft = (Minecraft) (Object) this;
		ForestLink.ForestSnapshot forest = ForestLink.readForest();
		if (minecraft.player == null || forest == null || forest.teleportSeq() == 0) return;
		if ((forest.flags() & Proto.IN_GAME) == 0 || (forest.flags() & Proto.MENU) != 0) return;
		if (!dev.forestcraft.ScreenBridge.screenOpen(minecraft)) ForestLink.applyLook(minecraft.player, forest, true);
		dev.forestcraft.ScreenBridge.frame(minecraft);
		ForestLink.frameWalk = 0f;
		ForestLink.frameBob = 0f;
	}

	@Inject(method = "renderFrame", at = @At("TAIL"))
	private void forestcraft$frame(boolean advanceGameTime, CallbackInfo ci) {
		Minecraft minecraft = (Minecraft) (Object) this;
		if (minecraft.player != null) {
			ForestLink.publishView(minecraft.player.getEyeHeight(), minecraft.gameRenderer.mainCamera().getFov(),
				ForestLink.frameWalk, ForestLink.frameBob);
		}
		if (ForestLink.forestInGame()) {
			dev.forestcraft.BlockExport.publishHit(minecraft);
			dev.forestcraft.EntityExport.publish();
		}
		FrameCapture.capture(minecraft);
	}

	@Inject(method = "setScreenAndShow", at = @At("TAIL"))
	private void forestcraft$screen(Screen screen, CallbackInfo ci) {
		WorldStart.onScreen((Minecraft) (Object) this, screen);
	}
}
