package dev.forestcraft;

import java.io.IOException;
import java.nio.MappedByteBuffer;
import java.nio.channels.FileChannel;
import java.nio.file.Path;
import java.nio.file.StandardOpenOption;

/** Byte layout shared with forest/Link.cs. Little-endian. */
public final class Proto {
	public static final int MAGIC = 0x46524346;
	public static final int VERSION = 1;
	public static final int GRID = 32;
	public static final int IN_GAME = 1;
	public static final int MENU = 2;
	public static final int MC_IN_WORLD = 1;
	public static final int MC_ON_GROUND = 2;
	/** Set once Minecraft is standing on the island. Stays set in the air, so a jump is not a hand-back. */
	public static final int MC_SETTLED = 4;
	public static final int MAP_BYTES = 16 << 20;
	public static final int OFF_FRAME = 0x200000;
	/** The frame must end before the far ground grid (0x9F0000): 1920x1080 fits, bigger doesn't. */
	public static final int FRAME_END = 0x9F0000;
	public static final int OFF_HEADER = 0x000;
	public static final int OFF_FOREST = 0x100;
	public static final int OFF_MC = 0x200;
	public static final int OFF_GRID = 0x300;
	/** Solid cells (trees, the plane, rocks, cabins) voxelized by The Forest around the player. */
	public static final int OFF_SOLIDS = 0x2000;
	public static final int SOLIDS_MAX = 0x8000;

	private Proto() {}

	public static Path linkFile() {
		String base = System.getenv("LOCALAPPDATA");
		if (base == null || base.isBlank()) base = System.getProperty("user.home");
		return Path.of(base, "ForestCraft", "link.bin");
	}

	public static MappedByteBuffer open() throws IOException {
		Path path = linkFile();
		FileChannel channel = FileChannel.open(path, StandardOpenOption.READ, StandardOpenOption.WRITE);
		MappedByteBuffer buffer = channel.map(FileChannel.MapMode.READ_WRITE, 0, MAP_BYTES);
		buffer.order(java.nio.ByteOrder.LITTLE_ENDIAN);
		return buffer;
	}
}
