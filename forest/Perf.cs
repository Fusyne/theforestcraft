using System.Collections.Generic;
using System.Diagnostics;

namespace ForestCraft
{
    // Where a slow frame went: each part of ForestCraft's frame is timed, and a frame that takes
    // much longer than usual is logged with the breakdown of the frame just before it.
    static class Perf
    {
        static readonly Stopwatch watch = Stopwatch.StartNew();
        static readonly Dictionary<string, double> cur = new Dictionary<string, double>();
        static string last = "";
        static double lastTotal;
        static float nextLog;

        public static double Now() { return watch.Elapsed.TotalMilliseconds; }

        public static void Add(string part, double since)
        {
            double ms = Now() - since;
            double v;
            cur.TryGetValue(part, out v);
            cur[part] = v + ms;
        }

        // Once per frame, at the start of ForestCraft's update.
        public static void Frame()
        {
            float dt = UnityEngine.Time.unscaledDeltaTime * 1000f;
            if (dt > 60f && UnityEngine.Time.realtimeSinceStartup > nextLog)
            {
                nextLog = UnityEngine.Time.realtimeSinceStartup + 1f;
                Plugin.Log.LogInfo("ForestCraft: slow frame " + dt.ToString("0") + " ms (ForestCraft " + lastTotal.ToString("0.0") + " ms: " + last + ")");
            }
            var sb = new System.Text.StringBuilder();
            double total = 0;
            foreach (var pair in cur)
            {
                if (pair.Value >= 0.5) sb.Append(pair.Key).Append(' ').Append(pair.Value.ToString("0.0")).Append(", ");
                total += pair.Value;
            }
            last = sb.ToString();
            lastTotal = total;
            cur.Clear();
        }
    }
}
