using System.Diagnostics;

namespace ForestCraft
{
    // Where a slow frame went: each part of ForestCraft's frame is timed, and a frame that takes
    // much longer than usual is logged with the breakdown of the frame just before it, and
    // whether Mono's garbage collector ran (on The Forest's big heap one collection is ~100 ms).
    // Nothing is allocated per frame here: the log string is only built when a frame was slow.
    static class Perf
    {
        static readonly Stopwatch watch = Stopwatch.StartNew();
        static readonly string[] names = new string[24];
        static readonly double[] cur = new double[24];
        static readonly double[] last = new double[24];
        static int parts;
        static float nextLog;
        static int lastGc = -1;
        static int slow, slowGc;
        static float windowStart;

        // The 30 s summary: every frame counts, not only the slow ones (a steady 25 fps never
        // makes a "slow frame" but is what "it lags" usually means).
        static int frames, over33, over50;
        static double frameMs, worstMs, oursMs, oursWorst;
        static readonly double[] sumPart = new double[24];
        static double oursStart;

        // Memory at the last Now(): Add() also counts what the part allocated.
        static long nowMem;
        static readonly long[] partBytes = new long[24];

        public static double Now()
        {
            nowMem = System.GC.GetTotalMemory(false);
            return watch.Elapsed.TotalMilliseconds;
        }

        /// <summary>Times a part and counts its allocations (sequential parts, not nested).</summary>
        public static void Mark(string part, double since)
        {
            Add(part, since);
        }

        // Who fills the heap (and so how often the ~100 ms collections come): bytes allocated
        // during ForestCraft's LateUpdate versus during the whole frame. A drop in the heap size
        // between two readings is a collection, not a negative allocation.
        static long memFrameStart = -1, memOursStart, oursBytes, allBytes;

        static void Count(ref long total, long from, long to)
        {
            if (from >= 0 && to > from) total += to - from;
        }

        /// <summary>End of ForestCraft's LateUpdate.</summary>
        public static void EndOfOurs()
        {
            double ours = watch.Elapsed.TotalMilliseconds - oursStart;
            oursMs += ours;
            if (ours > oursWorst) oursWorst = ours;
            Count(ref oursBytes, memOursStart, System.GC.GetTotalMemory(false));
        }

        public static void Add(string part, double since)
        {
            long mem = System.GC.GetTotalMemory(false);
            long bytes = mem > nowMem ? mem - nowMem : 0;
            double ms = watch.Elapsed.TotalMilliseconds - since;
            for (int i = 0; i < parts; i++)
            {
                if ((object)names[i] == (object)part) { cur[i] += ms; partBytes[i] += bytes; return; }
            }
            if (parts < names.Length) { names[parts] = part; cur[parts] = ms; partBytes[parts] = bytes; parts++; }
        }

        // Once per frame, at the start of ForestCraft's update.
        public static void Frame()
        {
            long mem = System.GC.GetTotalMemory(false);
            Count(ref allBytes, memFrameStart, mem);
            memFrameStart = mem;
            memOursStart = mem;
            oursStart = watch.Elapsed.TotalMilliseconds;
            float dt = UnityEngine.Time.unscaledDeltaTime * 1000f;
            frames++;
            frameMs += dt;
            if (dt > worstMs) worstMs = dt;
            if (dt > 33.4f) over33++;
            if (dt > 50f) over50++;
            int gc = System.GC.CollectionCount(0);
            bool collected = lastGc >= 0 && gc != lastGc;
            lastGc = gc;
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (dt > 60f)
            {
                slow++;
                if (collected) slowGc++;
                if (now > nextLog)
                {
                    nextLog = now + 1f;
                    var sb = new System.Text.StringBuilder();
                    double total = 0;
                    for (int i = 0; i < parts; i++)
                    {
                        total += last[i];
                        if (last[i] >= 0.5) sb.Append(names[i]).Append(' ').Append(last[i].ToString("0.0")).Append(", ");
                    }
                    Plugin.Log.LogInfo("ForestCraft: slow frame " + dt.ToString("0") + " ms" + (collected ? " [garbage collection]" : "")
                        + " (ForestCraft " + total.ToString("0.0") + " ms: " + sb + ")");
                }
            }
            if (now - windowStart > 30f)
            {
                if (frames > 0)
                {
                    var sb3 = new System.Text.StringBuilder("ForestCraft: perf last 30 s: ");
                    sb3.Append((1000.0 * frames / System.Math.Max(1.0, frameMs)).ToString("0.0")).Append(" fps, worst ").Append(worstMs.ToString("0"))
                        .Append(" ms, ").Append(over33).Append(" frames > 33 ms, ").Append(over50).Append(" > 50 ms; ForestCraft ")
                        .Append((oursMs / frames).ToString("0.00")).Append(" ms/frame (worst ").Append(oursWorst.ToString("0.0")).Append("): ");
                    for (int i = 0; i < parts; i++)
                    {
                        double avg = sumPart[i] / frames;
                        if (avg >= 0.05) sb3.Append(names[i]).Append(' ').Append(avg.ToString("0.00")).Append(", ");
                    }
                    sb3.Append("| ").Append(Plugin.Context());
                    Plugin.Log.LogInfo(sb3.ToString());
                }
                frames = over33 = over50 = 0;
                frameMs = worstMs = oursMs = oursWorst = 0;
                for (int i = 0; i < sumPart.Length; i++) sumPart[i] = 0;
                if (slow > 0) Plugin.Log.LogInfo("ForestCraft: last 30 s: " + slow + " slow frames, " + slowGc + " of them garbage collections"
                    + "; allocated " + (allBytes >> 20) + " MB in all, " + (oursBytes >> 10) + " KB by ForestCraft's update");
                if (allBytes > (8L << 20))
                {
                    var sb2 = new System.Text.StringBuilder("ForestCraft: allocations by part (KB in 30 s): ");
                    for (int i = 0; i < parts; i++) if (partBytes[i] >= 64 * 1024) sb2.Append(names[i]).Append(' ').Append(partBytes[i] >> 10).Append(", ");
                    Plugin.Log.LogInfo(sb2.ToString());
                }
                for (int i = 0; i < partBytes.Length; i++) partBytes[i] = 0;
                oursBytes = allBytes = 0;
                windowStart = now;
                slow = slowGc = 0;
            }
            for (int i = 0; i < parts; i++) { sumPart[i] += cur[i]; last[i] = cur[i]; cur[i] = 0; }
        }
    }
}
