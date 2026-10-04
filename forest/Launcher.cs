using System.Diagnostics;
using System.IO;

namespace ForestCraft
{
    static class Launcher
    {
        static bool started;
        static float nextCheck = 60f;

        // If Minecraft's process is gone (closed by hand, crashed, or an old build that quit during
        // a long save load), start it again: the link and the world come back on their own.
        public static void Watch()
        {
            if (UnityEngine.Time.realtimeSinceStartup < nextCheck) return;
            nextCheck = UnityEngine.Time.realtimeSinceStartup + 15f;
            if (Link.McAlive()) return;
            int pid = Link.McPid();
            if (pid != 0)
            {
                try
                {
                    var p = Process.GetProcessById(pid);
                    if (p != null && !p.HasExited) return; // alive, just busy
                }
                catch (System.Exception) { }
            }
            Plugin.Log.LogWarning("ForestCraft: Minecraft is not running, starting it again");
            started = false;
            Start();
            nextCheck = UnityEngine.Time.realtimeSinceStartup + 90f; // give it time to boot
        }

        // The installer writes %LOCALAPPDATA%\ForestCraft\launcher.txt:
        //   line 1 = full path of prismlauncher.exe, line 2 = instance id.
        // Without it, the usual Prism install folders are tried.
        static void Resolve(out string prism, out string instance)
        {
            prism = null;
            instance = "ForestCraft";
            string local = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
            try
            {
                string cfg = Path.Combine(Path.Combine(local, "ForestCraft"), "launcher.txt");
                if (File.Exists(cfg))
                {
                    string[] lines = File.ReadAllLines(cfg);
                    if (lines.Length > 0 && File.Exists(lines[0].Trim())) prism = lines[0].Trim();
                    if (lines.Length > 1 && lines[1].Trim().Length > 0) instance = lines[1].Trim();
                }
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("ForestCraft: launcher.txt unreadable: " + e.Message); }
            if (prism != null) return;
            string pf = System.Environment.GetEnvironmentVariable("ProgramFiles") ?? "C:\\Program Files";
            string home = System.Environment.GetEnvironmentVariable("USERPROFILE") ?? "";
            string[] candidates =
            {
                Path.Combine(Path.Combine(Path.Combine(local, "Programs"), "PrismLauncher"), "prismlauncher.exe"),
                Path.Combine(Path.Combine(pf, "PrismLauncher"), "prismlauncher.exe"),
                Path.Combine(Path.Combine(Path.Combine(Path.Combine(Path.Combine(home, "scoop"), "apps"), "prismlauncher"), "current"), "prismlauncher.exe"),
            };
            foreach (string c in candidates)
                if (File.Exists(c)) { prism = c; return; }
        }

        public static void Start()
        {
            if (started) return;
            started = true;
            string prism, instance;
            Resolve(out prism, out instance);
            if (prism == null)
            {
                Plugin.Log.LogError("ForestCraft: Prism Launcher not found - run install.bat again");
                return;
            }
            var info = new ProcessStartInfo();
            info.FileName = prism;
            info.Arguments = "--launch \"" + instance + "\"";
            info.WorkingDirectory = Path.GetDirectoryName(prism);
            info.UseShellExecute = true;
            Process.Start(info);
            Plugin.Log.LogInfo("ForestCraft: started " + prism + " --launch " + instance);
        }
    }
}
