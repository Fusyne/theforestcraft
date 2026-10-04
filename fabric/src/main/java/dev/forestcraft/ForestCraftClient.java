package dev.forestcraft;

import net.fabricmc.api.ClientModInitializer;

public final class ForestCraftClient implements ClientModInitializer {
	@Override
	public void onInitializeClient() {
		ForestLink.LOG.info("ForestCraft client loaded");
	}
}
