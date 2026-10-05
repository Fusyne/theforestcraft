package dev.forestcraft;

import java.nio.MappedByteBuffer;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.world.entity.Entity;

/**
 * Minecraft's arrows against The Forest's creatures. Every server tick each flying arrow's path
 * (from, to, damage) goes into a ring at OFF_SHOTS; The Forest looks along it for a cannibal or
 * an animal and, on a hit, applies the damage and hands the arrow's id back (OFF_SHOTS+0x4000),
 * and the arrow is removed here, as if stuck in the creature.
 */
public final class Shots {
	public static final int OFF_SHOTS = 0xA10000;
	private static final int RING = 128;
	private static final int ENTRY = 32;
	private static final int OFF_HITS = OFF_SHOTS + 0x4000;
	private static final int HIT_RING = 64;
	private static int written;
	private static int hitsRead = Integer.MIN_VALUE;

	private Shots() {}

	/** Server thread, end of an arrow's tick: it flew from (x0,y0,z0) to (x1,y1,z1). */
	public static synchronized void flew(int id, double x0, double y0, double z0, double x1, double y1, double z1, float damage) {
		MappedByteBuffer map = ForestLink.buffer();
		if (map == null) return;
		if (written == 0) written = Math.max(0, map.getInt(OFF_SHOTS));
		int at = OFF_SHOTS + 16 + (written % RING) * ENTRY;
		map.putInt(at, id);
		map.putFloat(at + 4, (float) x0);
		map.putFloat(at + 8, (float) y0);
		map.putFloat(at + 12, (float) z0);
		map.putFloat(at + 16, (float) x1);
		map.putFloat(at + 20, (float) y1);
		map.putFloat(at + 24, (float) z1);
		map.putFloat(at + 28, damage);
		written++;
		map.putInt(OFF_SHOTS, written);
	}

	/** Server tick: arrows The Forest says hit one of its creatures are gone. */
	public static void collectHits(ServerLevel level) {
		MappedByteBuffer map = ForestLink.buffer();
		if (map == null) return;
		int total = map.getInt(OFF_HITS);
		if (hitsRead == Integer.MIN_VALUE || total < hitsRead) { hitsRead = total; return; }
		if (total - hitsRead > HIT_RING) hitsRead = total - HIT_RING;
		for (; hitsRead < total; hitsRead++) {
			int id = map.getInt(OFF_HITS + 16 + (hitsRead % HIT_RING) * 4);
			Entity e = level.getEntity(id);
			if (e instanceof net.minecraft.world.entity.projectile.arrow.AbstractArrow) e.discard();
		}
	}
}
