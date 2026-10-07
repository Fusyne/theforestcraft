using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using HarmonyLib;
using UnityEngine;

namespace ForestCraft
{
    // The Forest's own developer console (the one "developermodeon" unlocks), switched on by the
    // mod: F1 opens it, Enter runs a command. Everything it has works: spawnmutant <type>,
    // spawnanimal, spawnitem, additem, godmode, killallenemies, goto, setCurrentDay... plus
    // "mc <command>" (or "/<command>"), which runs a Minecraft command (summon, give, time,
    // tp, gamemode...). While the console is open nothing typed reaches Minecraft.
    static class DevConsole
    {
        const int OffCommand = 0xA19000; // seq, length, then the command (UTF-8, up to 1000 bytes)
        static bool announced, triedCreate;
        public static bool Enabled = true;

        public static void Ensure()
        {
            if (!Enabled && !ModMenu.Enabled) return;
            try
            {
                if (!Cheats.DebugConsole) Cheats.DebugConsole = true;
                if (TheForest.DebugConsole.GetInstance() == null && !triedCreate)
                {
                    triedCreate = true;
                    var go = new GameObject("DebugConsole");
                    go.AddComponent<TheForest.DebugConsole>();
                    UnityEngine.Object.DontDestroyOnLoad(go);
                }
                if (!announced && TheForest.DebugConsole.GetInstance() != null)
                {
                    announced = true;
                    Plugin.Log.LogInfo("ForestCraft: The Forest's developer console is on (F1). 'mc <command>' runs a Minecraft command.");
                }
            }
            catch (Exception e)
            {
                if (!announced) { announced = true; Plugin.Log.LogWarning("ForestCraft: developer console not available: " + e.Message); }
            }
        }

        public static bool Open
        {
            get
            {
                try
                {
                    TheForest.DebugConsole c = TheForest.DebugConsole.GetInstance();
                    return c != null && c._showConsole;
                }
                catch { return false; }
            }
        }

        // The Forest's console, created if needed (the mod menu runs its commands through it).
        public static TheForest.DebugConsole Instance()
        {
            Ensure();
            try { return TheForest.DebugConsole.GetInstance(); }
            catch { return null; }
        }

        // Runs one of The Forest's console commands; false when the console isn't there.
        public static bool RunForest(string command)
        {
            TheForest.DebugConsole c = Instance();
            if (c == null) return false;
            c.HandleConsoleInput(command);
            return true;
        }

        // Commands wait here until Minecraft has taken the previous block (it acknowledges at
        // +1016): Minecraft reads at 20 ticks a second, The Forest clicks faster than that.
        // A block holds several commands, one per line.
        const int AckAt = 1016;
        static readonly List<string> queue = new List<string>();
        static readonly System.Text.StringBuilder block = new System.Text.StringBuilder();

        public static void SendToMinecraft(string command)
        {
            if (string.IsNullOrEmpty(command)) return;
            if (queue.Count < 64) queue.Add(command);
            Flush();
        }

        public static void Flush()
        {
            if (queue.Count == 0) return;
            IntPtr view = Link.View;
            if (view == IntPtr.Zero) return;
            int seq = Marshal.ReadInt32(view, OffCommand);
            if (Marshal.ReadInt32(view, OffCommand + AckAt) != seq) return; // not taken yet
            block.Length = 0;
            int taken = 0, size = 0;
            while (taken < queue.Count)
            {
                int n = System.Text.Encoding.UTF8.GetByteCount(queue[taken]) + 1;
                if (taken > 0 && size + n > 1000) break;
                if (taken > 0) block.Append('\n');
                block.Append(queue[taken]);
                size += n;
                taken++;
            }
            queue.RemoveRange(0, taken);
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(block.ToString());
            int len = Math.Min(bytes.Length, 1000);
            Marshal.Copy(bytes, 0, new IntPtr(view.ToInt64() + OffCommand + 8), len);
            Marshal.WriteInt32(view, OffCommand + 4, len);
            Marshal.WriteInt32(view, OffCommand, seq + 1);
        }
    }

    [HarmonyPatch(typeof(TheForest.DebugConsole), "HandleConsoleInput")]
    static class DevConsoleMinecraftCommand
    {
        static bool Prefix(string consoleInput)
        {
            if (string.IsNullOrEmpty(consoleInput)) return true;
            string s = consoleInput.Trim();
            string cmd = null;
            if (s.StartsWith("/")) cmd = s.Substring(1);
            else if (s.Length > 3 && s.Substring(0, 3).ToLowerInvariant() == "mc ") cmd = s.Substring(3).Trim();
            if (cmd == null) return true;
            if (cmd.StartsWith("/")) cmd = cmd.Substring(1);
            DevConsole.SendToMinecraft(cmd);
            Debug.Log("$> Minecraft: /" + cmd + " (its answer shows in Minecraft's chat)");
            return false;
        }
    }
}
