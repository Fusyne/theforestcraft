package dev.forestcraft;

import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardCopyOption;
import java.util.Comparator;
import java.util.stream.Stream;
import net.minecraft.client.Minecraft;
import net.minecraft.client.gui.screens.Screen;
import net.minecraft.world.level.GameType;
import net.minecraft.world.level.LevelSettings;
import net.minecraft.world.level.WorldDataConfiguration;
import net.minecraft.world.level.levelgen.WorldOptions;
import net.minecraft.world.level.levelgen.presets.WorldPresets;

/**
 * One Minecraft world per The Forest save, saved when The Forest saves.
 * <ul>
 * <li>Minecraft always plays in "ForestCraft_Play".</li>
 * <li>The Forest starts a game (OFF_FOREST+220 = session counter, +216 = 1..5 the save slot it
 * loaded, 9 a new game): Minecraft leaves its world if it is in one, then rebuilds the play world
 * from "ForestCraft_SlotN" (a loaded save) or from nothing (a new game), and opens it.</li>
 * <li>The Forest writes a save into slot N (+224 save counter, +228 slot): Minecraft saves and
 * copies the play world to "ForestCraft_SlotN".</li>
 * </ul>
 * A save made before this existed (the single "ForestCraft" world) is taken over by the first
 * save slot loaded.
 */
public final class WorldStart {
	private static final String PLAY = "ForestCraft_Play";
	private static final String LEGACY = "ForestCraft";

	private static int openedSession;
	private static int lastSaves = Integer.MIN_VALUE;
	private static boolean leaving;

	private WorldStart() {}

	private static final String SCALE_FILE = "forestcraft_scale.txt";
	private static Path scalePending;
	private static float scalePendingValue;

	public static void tick(Minecraft minecraft) {
		var map = ForestLink.buffer();
		if (map == null || !minecraft.isGameLoadFinished()) return;
		// Keep telling The Forest this world's size (it may have restarted).
		map.putFloat(Proto.OFF_MC + 196, openedSession != 0 ? scalePendingValue : 0f);
		if (scalePending != null && Files.isDirectory(scalePending.getParent())) {
			try {
				Files.writeString(scalePending, Float.toString(scalePendingValue));
				ForestLink.LOG.info("world size {} saved with the world", scalePendingValue);
			} catch (IOException e) {
				ForestLink.LOG.warn("world size not saved: {}", e.toString());
			}
			scalePending = null;
		}
		int session = map.getInt(Proto.OFF_FOREST + 220);
		int kind = map.getInt(Proto.OFF_FOREST + 216);
		int saves = map.getInt(Proto.OFF_FOREST + 224);
		if (lastSaves == Integer.MIN_VALUE) lastSaves = saves;
		if (saves != lastSaves) {
			lastSaves = saves;
			int slot = map.getInt(Proto.OFF_FOREST + 228);
			if (openedSession != 0 && slot >= 1 && slot <= 5) saveTo(minecraft, slot);
		}
		if (session == 0 || session == openedSession) return; // The Forest is still on its title screen
		if (minecraft.level != null || minecraft.getSingleplayerServer() != null) {
			// Another game was started in The Forest: leave this world first.
			if (!leaving) {
				leaving = true;
				ForestLink.LOG.info("The Forest started another game: leaving the Minecraft world");
				minecraft.disconnectWithSavingScreen();
			}
			return;
		}
		leaving = false;
		openedSession = session;
		open(minecraft, kind);
	}

	/** Kept for the title-screen hook: everything happens in tick now. */
	public static void onScreen(Minecraft minecraft, Screen screen) {
	}

	private static void open(Minecraft minecraft, int kind) {
		try {
			Path saves = minecraft.getLevelSource().getBaseDir();
			Path play = saves.resolve(PLAY);
			deleteTree(play);
			if (kind >= 1 && kind <= 5) {
				Path slot = saves.resolve("ForestCraft_Slot" + kind);
				if (Files.isDirectory(slot)) {
					copyTree(slot, play);
					ForestLink.LOG.info("save slot {}: Minecraft world restored from {}", kind, slot.getFileName());
				} else if (Files.isDirectory(saves.resolve(LEGACY)) && !anySlotWorld(saves)) {
					copyTree(saves.resolve(LEGACY), play);
					ForestLink.LOG.info("save slot {}: the old ForestCraft world carried over", kind);
				} else {
					ForestLink.LOG.info("save slot {}: no Minecraft world saved with it yet, starting fresh", kind);
				}
			} else {
				ForestLink.LOG.info("new game in The Forest: fresh Minecraft world");
			}
		} catch (IOException e) {
			ForestLink.LOG.warn("preparing the Minecraft world failed: {}", e.toString());
		}
		// The world's size: kept from its file, 1.5 for worlds made before sizes were saved,
		// The Forest's wish for a new one (written into the world once it exists).
		Path play0 = minecraft.getLevelSource().getBaseDir().resolve(PLAY);
		Path scaleFile = play0.resolve(SCALE_FILE);
		float scale = 1.5f;
		scalePending = null;
		try {
			if (Files.isRegularFile(scaleFile)) scale = Float.parseFloat(Files.readString(scaleFile).trim());
			else if (Files.isDirectory(play0)) { scale = 1.5f; scalePending = scaleFile; }
			else { scale = ForestLink.wantedScale(); scalePending = scaleFile; }
		} catch (IOException | NumberFormatException e) {
			ForestLink.LOG.warn("world size unreadable, using 1.5: {}", e.toString());
			scale = 1.5f;
		}
		if (!(scale >= 0.5f && scale <= 3f)) scale = 1.5f;
		scalePendingValue = scale;
		ForestLink.setWorldScale(scale);
		ForestLink.LOG.info("world size: {} Forest units per block", scale);
		ForestLink.resetWorldState();
		BlockExport.resetSent();
		try {
			var flows = minecraft.createWorldOpenFlows();
			if (minecraft.getLevelSource().levelExists(PLAY)) {
				ForestLink.LOG.info("opening world {}", PLAY);
				flows.openWorld(PLAY, () -> {});
				return;
			}
			ForestLink.LOG.info("creating empty world {}", PLAY);
			var settings = new LevelSettings(PLAY, GameType.SURVIVAL, LevelSettings.DifficultySettings.DEFAULT, true, WorldDataConfiguration.DEFAULT);
			flows.createFreshLevel(PLAY, settings, WorldOptions.defaultWithRandomSeed().withStructures(false),
				WorldPresets::createNormalWorldDimensions, new net.minecraft.client.gui.screens.TitleScreen());
		} catch (Exception e) {
			openedSession = 0;
			ForestLink.LOG.error("could not open the ForestCraft world", e);
		}
	}

	/** The Forest saved into slot N: same moment, same slot for Minecraft. */
	private static void saveTo(Minecraft minecraft, int slot) {
		var server = minecraft.getSingleplayerServer();
		if (server == null) return;
		server.execute(() -> {
			try {
				server.saveEverything(true, true, true);
				Path saves = minecraft.getLevelSource().getBaseDir();
				Path target = saves.resolve("ForestCraft_Slot" + slot);
				Path temp = saves.resolve("ForestCraft_Slot" + slot + "_saving");
				deleteTree(temp);
				copyTree(saves.resolve(PLAY), temp);
				deleteTree(target);
				Files.move(temp, target);
				ForestLink.LOG.info("The Forest saved in slot {}: Minecraft world saved with it", slot);
			} catch (Exception e) {
				ForestLink.LOG.warn("Minecraft world not saved with slot {}: {}", slot, e.toString());
			}
		});
	}

	private static boolean anySlotWorld(Path saves) {
		for (int i = 1; i <= 5; i++) if (Files.isDirectory(saves.resolve("ForestCraft_Slot" + i))) return true;
		return false;
	}

	private static void deleteTree(Path dir) throws IOException {
		if (!Files.exists(dir)) return;
		try (Stream<Path> walk = Files.walk(dir)) {
			for (Path p : walk.sorted(Comparator.reverseOrder()).toList()) Files.deleteIfExists(p);
		}
	}

	private static void copyTree(Path from, Path to) throws IOException {
		try (Stream<Path> walk = Files.walk(from)) {
			for (Path p : walk.toList()) {
				if (p.getFileName() != null && p.getFileName().toString().equals("session.lock")) continue;
				Path q = to.resolve(from.relativize(p).toString());
				if (Files.isDirectory(p)) Files.createDirectories(q);
				else Files.copy(p, q, StandardCopyOption.REPLACE_EXISTING, StandardCopyOption.COPY_ATTRIBUTES);
			}
		}
	}
}
