package dev.forestcraft;

import java.lang.management.GarbageCollectorMXBean;
import java.lang.management.ManagementFactory;
import java.util.List;
import java.util.Locale;

/**
 * Where a Minecraft frame goes. Each part is timed every frame; a frame that takes much longer
 * than usual is logged with its breakdown (and Java's garbage collector time in it), and the
 * 30 s perf line gets the averages. Nothing is allocated per frame.
 */
public final class McPerf {
	// The first five add up to the frame; the others are parts of "render" (extract, draw,
	// acquire, present) or of "capture" (copy).
	public static final int TICK = 0, RENDER = 1, EXPORT = 2, CAPTURE = 3, BLOCKS = 4, SHIP = 5,
			EXTRACT = 6, DRAW = 7, ACQUIRE = 8, PRESENT = 9;
	private static final String[] NAMES = { "tick", "render", "export", "capture", "blocks", "copy",
			"(extract", "draw", "acquire", "present)" };
	private static final long[] cur = new long[NAMES.length];
	private static final long[] sum = new long[NAMES.length];
	private static long gcSum, gcLast = -1, nextLog;
	private static int frames;
	private static List<GarbageCollectorMXBean> gcs;

	private McPerf() {}

	public static void add(int part, long since) {
		cur[part] += System.nanoTime() - since;
	}

	private static long gcMillis() {
		if (gcs == null) gcs = ManagementFactory.getGarbageCollectorMXBeans();
		long t = 0;
		for (int i = 0; i < gcs.size(); i++) {
			long c = gcs.get(i).getCollectionTime();
			if (c > 0) t += c;
		}
		return t;
	}

	/** End of one Minecraft frame (called where Minecraft starts waiting for The Forest). */
	public static void endFrame(long workNanos) {
		long gc = gcMillis();
		long gcFrame = gcLast >= 0 ? gc - gcLast : 0;
		gcLast = gc;
		gcSum += gcFrame;
		frames++;
		if (workNanos > 45_000_000L) {
			long now = System.currentTimeMillis();
			if (now >= nextLog) {
				nextLog = now + 1000;
				StringBuilder sb = new StringBuilder("slow Minecraft frame ").append(workNanos / 1_000_000).append(" ms: ");
				long known = 0;
				for (int i = 0; i < NAMES.length; i++) {
					if (i <= BLOCKS) known += cur[i];
					sb.append(NAMES[i]).append(' ').append(cur[i] / 1_000_000).append(", ");
				}
				sb.append("GC ").append(gcFrame).append(", other ").append(Math.max(0, workNanos - known) / 1_000_000);
				ForestLink.LOG.info(sb.toString());
			}
		}
		for (int i = 0; i < NAMES.length; i++) { sum[i] += cur[i]; cur[i] = 0; }
	}

	/** Averages for the 30 s perf line, then starts a new window. */
	public static String summary() {
		StringBuilder sb = new StringBuilder("; per frame: ");
		int n = Math.max(1, frames);
		for (int i = 0; i < NAMES.length; i++) {
			sb.append(NAMES[i]).append(' ').append(String.format(Locale.ROOT, "%.2f", sum[i] / 1e6 / n)).append(", ");
			sum[i] = 0;
		}
		sb.append("GC ").append(gcSum).append(" ms in all");
		gcSum = 0;
		frames = 0;
		return sb.toString();
	}
}
