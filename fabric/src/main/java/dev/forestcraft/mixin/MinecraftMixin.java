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

	/** One Minecraft frame per Forest frame (ForestLink.paceToForest). */
	@Inject(method = "runTick", at = @At("HEAD"))
	private void forestcraft$pace(boolean advanceGameTime, CallbackInfo ci) {
		ForestLink.paceToForest();
	}

	private long forestcraft$tickStart, forestcraft$renderStart;

	@Inject(method = "tick", at = @At("RETURN"))
	private void forestcraft$tickEnd(CallbackInfo ci) {
		if (forestcraft$tickStart != 0) dev.forestcraft.McPerf.add(dev.forestcraft.McPerf.TICK, forestcraft$tickStart);
		forestcraft$tickStart = 0;
	}

	@Inject(method = "tick", at = @At("HEAD"))
	private void forestcraft$tick(CallbackInfo ci) {
		forestcraft$tickStart = System.nanoTime();
		Minecraft minecraft = (Minecraft) (Object) this;
		dev.forestcraft.ForestEvents.poll(minecraft);
		ForestLink.perfLog(minecraft);
		minecraft.options.pauseOnLostFocus = false;
		// Minecraft's pause menu (Esc or a lost focus while its window was still showing): the
		// integrated server stops ticking behind it, so the body was never handed over and the
		// player had no character at all. The Forest has its own pause menu: close Minecraft's.
		if (minecraft.gui.screen() instanceof net.minecraft.client.gui.screens.PauseScreen && ForestLink.forestPlaying()) {
			minecraft.gui.setScreen(null);
			ForestLink.LOG.info("Minecraft's pause menu closed: The Forest has the controls");
		}
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
		long forestcraft$b = System.nanoTime();
		dev.forestcraft.BlockExport.tick(minecraft);
		dev.forestcraft.TerrainDig.pump();
		dev.forestcraft.McPerf.add(dev.forestcraft.McPerf.BLOCKS, forestcraft$b);
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
		forestcraft$heldLight(minecraft);
		dev.forestcraft.Hazards.tick(minecraft);
		dev.forestcraft.Fighters.tick(minecraft);
		dev.forestcraft.TerrainDig.collectLogs(minecraft);
		dev.forestcraft.TerrainDig.collectGifts(minecraft);
		forestcraft$attack = attack;
		forestcraft$use = use;
	}

	private int forestcraft$frameW, forestcraft$frameH, forestcraft$resizeTicks;
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

	/** Light level of what Steve holds (torch 14, lantern 15...): The Forest lights around him. */
	private void forestcraft$heldLight(Minecraft minecraft) {
		var map = ForestLink.buffer();
		if (map == null || minecraft.player == null) return;
		int level = 0;
		for (var stack : new net.minecraft.world.item.ItemStack[] { minecraft.player.getMainHandItem(), minecraft.player.getOffhandItem() }) {
			if (stack.getItem() instanceof net.minecraft.world.item.BlockItem block)
				level = Math.max(level, block.getBlock().defaultBlockState().getLightEmission());
		}
		map.putInt(Proto.OFF_MC + 192, level);
	}

	/** F5 mode, frame size and hotbar slot come from The Forest's window. */
	/**
	 * Minecraft only draws the hand, the HUD, screens and what The Forest copies (entities,
	 * particles): it shares the GPU with The Forest, so it runs as light as it can. Nothing here
	 * changes what is seen. Not touched: mipmaps (a resource reload would move the block atlas
	 * The Forest already has).
	 */
	private static boolean forestcraft$lightened;

	private void forestcraft$lightweight(Minecraft minecraft) {
		if (forestcraft$lightened) return; // once per launch: a value the game clamps is not retried every tick
		forestcraft$lightened = true;
		var o = minecraft.options;
		boolean changed = false;
		changed |= forestcraft$set(o.renderDistance(), 10);          // blocks sent to The Forest: 128 around
		changed |= forestcraft$set(o.simulationDistance(), 6);
		changed |= forestcraft$set(o.framerateLimit(), 60);          // The Forest copies at most this many frames
		changed |= forestcraft$set(o.enableVsync(), false);          // a hidden window has no screen to wait for
		changed |= forestcraft$set(o.ambientOcclusion(), false);     // Minecraft's terrain is never drawn
		changed |= forestcraft$set(o.biomeBlendRadius(), 0);
		changed |= forestcraft$set(o.chunkSectionFadeInTime(), 0.0);
		changed |= forestcraft$set(o.entityShadows(), false);
		changed |= forestcraft$set(o.menuBackgroundBlurriness(), 0); // blur pass behind the inventory
		changed |= forestcraft$set(o.weatherRadius(), 3);
		if (changed) {
			o.save();
			ForestLink.LOG.info("Minecraft settings lightened for ForestCraft");
		}
	}

	private int forestcraft$fpsTicks;
	private int forestcraft$fpsTries;

	/**
	 * Every Minecraft frame is copied to The Forest, which shows at most its own frame rate:
	 * Minecraft renders just above it (never under 30, never over 60). Not saved to options.
	 */
	private void forestcraft$matchFps(Minecraft minecraft) {
		if (++forestcraft$fpsTicks % 10 != 0 || ForestLink.buffer() == null) return;
		float fps = ForestLink.buffer().getFloat(Proto.OFF_FOREST + 200);
		if (!(fps > 1f) || fps > 1000f) return;
		// The option takes steps of 10: the next step above The Forest's rate.
		int target = Math.max(30, Math.min(60, ((int) Math.ceil(fps / 10f) + 1) * 10));
		if (minecraft.gui.screen() != null) target = 60; // inventory/chat: full rate for the cursor
		var limit = minecraft.options.framerateLimit();
		if (limit.get() != target && forestcraft$fpsTries < 20) {
			forestcraft$fpsTries++;
			try { limit.set(target); } catch (RuntimeException e) { /* keep */ }
			if (limit.get() == target) forestcraft$fpsTries = 0;
		}
	}

	private static <T> boolean forestcraft$set(net.minecraft.client.OptionInstance<T> option, T value) {
		if (java.util.Objects.equals(option.get(), value)) return false;
		try {
			option.set(value);
			return true;
		} catch (RuntimeException e) {
			return false; // a value this version doesn't accept: leave it
		}
	}

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
		forestcraft$lightweight(minecraft);
		forestcraft$matchFps(minecraft);
		int w = view.frameW(), h = view.frameH();
		if (w >= 320 && h >= 240 && w <= 1920 && h <= 1080 && (w != forestcraft$frameW || h != forestcraft$frameH)) {
			forestcraft$frameW = w;
			forestcraft$frameH = h;
			forestcraft$resizeTicks = 0;
			GLFW.glfwSetWindowSize(minecraft.getWindow().handle(), w, h);
			ForestLink.LOG.info("Minecraft frame size {}x{}", w, h);
		}
		// A hidden window doesn't always get its resize events: then the HUD and screens stayed
		// at 854x480 (blurry, stretched) with the cursor off. Apply the size by hand.
		var window = minecraft.getWindow();
		if (forestcraft$frameW > 0 && (window.getWidth() != forestcraft$frameW || window.getHeight() != forestcraft$frameH)
			&& ++forestcraft$resizeTicks == 10) {
			WindowInvoker invoker = (WindowInvoker) (Object) window;
			invoker.forestcraft$onResize(window.handle(), forestcraft$frameW, forestcraft$frameH);
			invoker.forestcraft$onFramebufferResize(window.handle(), forestcraft$frameW, forestcraft$frameH);
			ForestLink.LOG.info("Minecraft frame resized by hand to {}x{}", window.getWidth(), window.getHeight());
		}
		int slot = view.slot();
		if (minecraft.player != null && slot >= 0 && slot < 9 && minecraft.player.getInventory().getSelectedSlot() != slot)
			minecraft.player.getInventory().setSelectedSlot(slot);
	}

	@Inject(method = "renderFrame", at = @At("HEAD"))
	private void forestcraft$look(boolean advanceGameTime, CallbackInfo ci) {
		forestcraft$renderStart = System.nanoTime();
		Minecraft minecraft = (Minecraft) (Object) this;
		ForestLink.ForestSnapshot forest = ForestLink.readForest();
		if (minecraft.player == null || forest == null || forest.teleportSeq() == 0) return;
		if ((forest.flags() & Proto.IN_GAME) == 0 || (forest.flags() & Proto.MENU) != 0) return;
		if (!dev.forestcraft.ScreenBridge.screenOpen(minecraft)) ForestLink.applyLook(minecraft.player, forest, true);
		dev.forestcraft.ScreenBridge.frame(minecraft);
		ForestLink.frameWalk = 0f;
		ForestLink.frameBob = 0f;
	}

	private long forestcraft$part;

	@Inject(method = "renderFrame", at = @At(value = "INVOKE", target = "Lnet/minecraft/client/renderer/GameRenderer;render(Lnet/minecraft/client/DeltaTracker;Z)V"))
	private void forestcraft$drawStart(boolean advanceGameTime, CallbackInfo ci) {
		if (forestcraft$renderStart != 0) dev.forestcraft.McPerf.add(dev.forestcraft.McPerf.EXTRACT, forestcraft$renderStart);
		forestcraft$part = System.nanoTime();
	}

	@Inject(method = "renderFrame", at = @At(value = "INVOKE", target = "Lnet/minecraft/client/renderer/GameRenderer;render(Lnet/minecraft/client/DeltaTracker;Z)V", shift = At.Shift.AFTER))
	private void forestcraft$drawEnd(boolean advanceGameTime, CallbackInfo ci) {
		dev.forestcraft.McPerf.add(dev.forestcraft.McPerf.DRAW, forestcraft$part);
	}

	@Inject(method = "renderFrame", at = @At(value = "INVOKE", target = "Lcom/mojang/blaze3d/systems/GpuSurface;present()V"))
	private void forestcraft$presentStart(boolean advanceGameTime, CallbackInfo ci) {
		forestcraft$part = System.nanoTime();
	}

	@Inject(method = "renderFrame", at = @At(value = "INVOKE", target = "Lcom/mojang/blaze3d/systems/GpuSurface;present()V", shift = At.Shift.AFTER))
	private void forestcraft$presentEnd(boolean advanceGameTime, CallbackInfo ci) {
		dev.forestcraft.McPerf.add(dev.forestcraft.McPerf.PRESENT, forestcraft$part);
	}

	@Inject(method = "renderFrame", at = @At("TAIL"))
	private void forestcraft$frame(boolean advanceGameTime, CallbackInfo ci) {
		if (forestcraft$renderStart != 0) dev.forestcraft.McPerf.add(dev.forestcraft.McPerf.RENDER, forestcraft$renderStart);
		long forestcraft$e = System.nanoTime();
		Minecraft minecraft = (Minecraft) (Object) this;
		if (minecraft.player != null) {
			ForestLink.publishView(minecraft.player.getEyeHeight(), minecraft.gameRenderer.mainCamera().getFov(),
				ForestLink.frameWalk, ForestLink.frameBob);
		}
		if (ForestLink.forestInGame()) {
			dev.forestcraft.BlockExport.publishHit(minecraft);
			dev.forestcraft.EntityExport.publish();
			dev.forestcraft.ParticleExport.publish();
		}
		dev.forestcraft.McPerf.add(dev.forestcraft.McPerf.EXPORT, forestcraft$e);
		long forestcraft$c = System.nanoTime();
		FrameCapture.capture(minecraft);
		dev.forestcraft.McPerf.add(dev.forestcraft.McPerf.CAPTURE, forestcraft$c);
	}

	@Inject(method = "setScreenAndShow", at = @At("TAIL"))
	private void forestcraft$screen(Screen screen, CallbackInfo ci) {
		WorldStart.onScreen((Minecraft) (Object) this, screen);
	}
}
