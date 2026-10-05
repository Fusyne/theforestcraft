package dev.forestcraft;

import dev.forestcraft.mixin.MouseHandlerInvoker;
import java.nio.MappedByteBuffer;
import net.minecraft.client.Minecraft;
import net.minecraft.client.gui.screens.ChatScreen;
import net.minecraft.client.gui.screens.inventory.InventoryScreen;
import net.minecraft.client.input.CharacterEvent;
import net.minecraft.client.input.KeyEvent;
import net.minecraft.client.input.MouseButtonEvent;
import net.minecraft.client.input.MouseButtonInfo;

/**
 * Minecraft's screens (inventory E, chat T, command /) inside The Forest's window. The Forest
 * asks for a screen, then, while one is open, sends its cursor, mouse buttons, wheel, keys and
 * typed characters; they are fed to Minecraft's own GLFW callbacks, so every vanilla screen
 * (crafting, chests, chat history, tab completion...) works as usual. Minecraft reports back
 * whether a screen is open so The Forest frees its cursor and mutes its own controls.
 *
 * Forest block: +96 request seq, +100 request kind (1 inventory, 2 chat, 3 command),
 * +104/+108 cursor (fraction of the screen, top-left), +112 buttons, +116 wheel total, +120 keys written.
 * Key ring at OFF_KEYS: [type (0 key, 1 char), code (GLFW key or codepoint), action (1 press, 0 release)].
 * Minecraft block: +160 screen open.
 */
public final class ScreenBridge {
	public static final int OFF_KEYS = 0xA80000;
	public static final int KEY_RING = 256;

	private static int lastRequest = Integer.MIN_VALUE;
	private static int keysRead = Integer.MIN_VALUE;
	private static int lastButtons;
	private static float lastWheel = Float.NaN;
	private static double lastX = -1, lastY = -1, lastGx, lastGy;
	private static int lastClickButton = -1;
	private static long lastClickTime;
	private static int mods;

	private ScreenBridge() {}

	/** Render thread, every frame while The Forest is in game. */
	public static void frame(Minecraft minecraft) {
		MappedByteBuffer map = ForestLink.buffer();
		if (map == null || minecraft.player == null) return;
		long window = minecraft.getWindow().handle();
		int request = map.getInt(Proto.OFF_FOREST + 96);
		int written = map.getInt(Proto.OFF_FOREST + 120);
		if (lastRequest == Integer.MIN_VALUE) { lastRequest = request; keysRead = written; }
		if (request != lastRequest) {
			lastRequest = request;
			int kind = map.getInt(Proto.OFF_FOREST + 100);
			if (minecraft.gui.screen() == null) {
				if (kind == 1) minecraft.setScreenAndShow(new InventoryScreen(minecraft.player));
				else if (kind == 2) minecraft.setScreenAndShow(new ChatScreen("", false));
				else if (kind == 3) minecraft.setScreenAndShow(new ChatScreen("/", false));
				lastButtons = 0;
				lastDowns = Integer.MIN_VALUE;
				lastWheel = Float.NaN;
				keysRead = written;
				mods = 0;
			}
		}
		boolean open = minecraft.gui.screen() != null;
		map.putInt(Proto.OFF_MC + 160, open ? 1 : 0);
		if (!open) { keysRead = written; lastDowns = Integer.MIN_VALUE; return; }

		// Cursor arrives as a fraction of The Forest's screen (0..1 from top-left): no guessing
		// about the hidden window's real size. The mouse handler gets it in window pixels (hover
		// rendering), the screen gets clicks/drags/wheel/keys directly, in GUI coordinates.
		var win = minecraft.getWindow();
		double nx = map.getFloat(Proto.OFF_FOREST + 104), ny = map.getFloat(Proto.OFF_FOREST + 108);
		double gx = nx * win.getGuiScaledWidth(), gy = ny * win.getGuiScaledHeight();
		MouseHandlerInvoker mouse = (MouseHandlerInvoker) minecraft.mouseHandler;
		var screen = minecraft.gui.screen();
		if (nx != lastX || ny != lastY) {
			mouse.forestcraft$onMove(window, nx * win.getScreenWidth(), ny * win.getScreenHeight());
			double dx = gx - lastGx, dy = gy - lastGy;
			screen.mouseMoved(gx, gy);
			for (int b = 0; b < 3; b++)
				if ((lastButtons & (1 << b)) != 0) screen.mouseDragged(new MouseButtonEvent(gx, gy, new MouseButtonInfo(b, mods)), dx, dy);
			lastX = nx; lastY = ny;
		}
		lastGx = gx; lastGy = gy;
		int buttons = map.getInt(Proto.OFF_FOREST + 112);
		int downs = map.getInt(Proto.OFF_FOREST + 204), ups = map.getInt(Proto.OFF_FOREST + 208);
		if (lastDowns == Integer.MIN_VALUE) { lastDowns = downs; lastUps = ups; }
		int state = lastButtons;
		for (int b = 0; b < 3; b++) {
			// Every press and release The Forest saw since last frame, in order: a quick click
			// between two Minecraft frames still clicks. Then the held state wins.
			int d = ((downs >>> (b * 8)) - (lastDowns >>> (b * 8))) & 255;
			int u = ((ups >>> (b * 8)) - (lastUps >>> (b * 8))) & 255;
			if (d > 4 || u > 4) { d = 0; u = 0; } // out of step: trust the state only
			boolean down = (state & (1 << b)) != 0;
			for (int n = 0; n < 8 && (d > 0 || u > 0); n++) {
				if (minecraft.gui.screen() != screen) break;
				if (!down && d > 0) { press(screen, b, gx, gy); d--; down = true; }
				else if (down && u > 0) { screen.mouseReleased(new MouseButtonEvent(gx, gy, new MouseButtonInfo(b, mods))); u--; down = false; }
				else break;
			}
			boolean now = (buttons & (1 << b)) != 0;
			if (down != now && minecraft.gui.screen() == screen) {
				if (now) press(screen, b, gx, gy);
				else screen.mouseReleased(new MouseButtonEvent(gx, gy, new MouseButtonInfo(b, mods)));
			}
		}
		lastDowns = downs;
		lastUps = ups;
		lastButtons = buttons;
		float wheel = map.getFloat(Proto.OFF_FOREST + 116);
		if (!Float.isNaN(lastWheel) && wheel != lastWheel && minecraft.gui.screen() == screen) screen.mouseScrolled(gx, gy, 0, wheel - lastWheel);
		lastWheel = wheel;

		if (written - keysRead > KEY_RING) keysRead = written - KEY_RING;
		while (keysRead < written) {
			int at = OFF_KEYS + 16 + Math.floorMod(keysRead, KEY_RING) * 12;
			keysRead++;
			int type = map.getInt(at), code = map.getInt(at + 4), action = map.getInt(at + 8);
			var current = minecraft.gui.screen();
			if (current == null) continue;
			if (type == 1) {
				current.charTyped(new CharacterEvent(code));
				continue;
			}
			int bit = code == 340 || code == 344 ? 1 : code == 341 || code == 345 ? 2 : code == 342 || code == 346 ? 4 : 0;
			if (bit != 0) mods = action != 0 ? mods | bit : mods & ~bit;
			if (action != 0) current.keyPressed(new KeyEvent(code, 0, mods));
			else current.keyReleased(new KeyEvent(code, 0, mods));
		}
	}

	private static int lastDowns = Integer.MIN_VALUE, lastUps;

	private static void press(net.minecraft.client.gui.screens.Screen screen, int b, double gx, double gy) {
		long t = System.currentTimeMillis();
		boolean doubleClick = b == lastClickButton && t - lastClickTime < 250;
		lastClickButton = b;
		lastClickTime = t;
		screen.mouseClicked(new MouseButtonEvent(gx, gy, new MouseButtonInfo(b, mods)), doubleClick);
	}

	/** True while a Minecraft screen is open (mouse must not turn the player then). */
	public static boolean screenOpen(Minecraft minecraft) {
		return minecraft.gui != null && minecraft.gui.screen() != null;
	}
}
