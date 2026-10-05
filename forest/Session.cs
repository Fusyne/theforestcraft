using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using TheForest.Utils;

namespace ForestCraft
{
    // Which The Forest game is being played, and when it is saved, so Minecraft keeps one world
    // per save slot (WorldStart.java). A game starts when the player object appears; a save is
    // a newer file in one of the five slot folders.
    // OFF_FOREST: +216 kind (1..5 loaded slot, 9 new game), +220 session counter,
    //             +224 save counter, +228 slot saved.
    static class Session
    {
        const int Base = 0x100;
        static bool hadPlayer;
        static int sessions, saves;
        static float nextScan;
        static readonly long[] slotTimes = new long[6];
        public static bool JustStarted;

        public static void Update(IntPtr view)
        {
            JustStarted = false;
            if (view == IntPtr.Zero) return;
            bool has = false;
            try { has = LocalPlayer.Transform != null; } catch { }
            if (has && !hadPlayer)
            {
                int kind = 9;
                try { if (GameSetup.IsSavedGame) kind = Mathf.Clamp((int)GameSetup.Slot, 1, 5); } catch { }
                sessions++;
                Marshal.WriteInt32(view, Base + 216, kind);
                Marshal.WriteInt32(view, Base + 220, sessions);
                for (int s = 1; s <= 5; s++) slotTimes[s] = Newest(s); // loading isn't saving
                JustStarted = true;
                Plugin.Log.LogInfo("ForestCraft: game started (" + (kind == 9 ? "new game" : "save slot " + kind) + ")");
            }
            hadPlayer = has;
            if (!has || Time.realtimeSinceStartup < nextScan) return;
            nextScan = Time.realtimeSinceStartup + 2f;
            for (int s = 1; s <= 5; s++)
            {
                long t = Newest(s);
                if (t <= slotTimes[s]) continue;
                slotTimes[s] = t;
                saves++;
                Marshal.WriteInt32(view, Base + 228, s);
                Marshal.WriteInt32(view, Base + 224, saves);
                Plugin.Log.LogInfo("ForestCraft: The Forest saved in slot " + s);
            }
        }

        // Newest write time of the files in a single-player save slot, 0 if none.
        static long Newest(int slot)
        {
            try
            {
                string dir = SaveSlotUtils.GetLocalSlotPath((TheForest.Commons.Enums.PlayerModes)0, (TheForest.Commons.Enums.Slots)slot);
                if (!Directory.Exists(dir)) return 0;
                long best = 0;
                foreach (string f in Directory.GetFiles(dir))
                {
                    long t = File.GetLastWriteTimeUtc(f).Ticks;
                    if (t > best) best = t;
                }
                return best;
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }
}
