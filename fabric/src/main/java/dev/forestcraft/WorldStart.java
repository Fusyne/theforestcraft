package dev.forestcraft;

import net.minecraft.client.Minecraft;
import net.minecraft.client.gui.screens.Screen;
import net.minecraft.client.gui.screens.TitleScreen;
import net.minecraft.world.level.GameType;
import net.minecraft.world.level.LevelSettings;
import net.minecraft.world.level.WorldDataConfiguration;
import net.minecraft.world.level.levelgen.WorldOptions;
import net.minecraft.world.level.levelgen.presets.WorldPresets;

public final class WorldStart {
	private static boolean started;
	private static int wait;

	private WorldStart() {}

	public static void tick(Minecraft minecraft) {
		if (started || minecraft.level != null || !minecraft.isGameLoadFinished()) return;
		if (++wait < 20) return;
		onScreen(minecraft, new TitleScreen());
	}

	public static void onScreen(Minecraft minecraft, Screen screen) {
		if (started || minecraft.level != null || !(screen instanceof TitleScreen)) return;
		if (!minecraft.isGameLoadFinished()) return;
		started = true;
		try {
			var flows = minecraft.createWorldOpenFlows();
			if (minecraft.getLevelSource().levelExists("ForestCraft")) {
				ForestLink.LOG.info("opening world ForestCraft");
				flows.openWorld("ForestCraft", () -> {});
				return;
			}
			ForestLink.LOG.info("creating empty world ForestCraft");
			var settings = new LevelSettings(
				"ForestCraft",
				GameType.SURVIVAL,
				LevelSettings.DifficultySettings.DEFAULT,
				true,
				WorldDataConfiguration.DEFAULT
			);
			flows.createFreshLevel(
				"ForestCraft",
				settings,
				WorldOptions.defaultWithRandomSeed().withStructures(false),
				WorldPresets::createNormalWorldDimensions,
				screen
			);
		} catch (Exception e) {
			started = false;
			ForestLink.LOG.error("could not open the ForestCraft world", e);
		}
	}
}
