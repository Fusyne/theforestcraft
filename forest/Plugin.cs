using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ForestCraft
{
    [BepInPlugin("dev.forestcraft", "ForestCraft", "0.4.0")]
    public class Plugin : BaseUnityPlugin
    {
        public static ManualLogSource Log;
        int teleportSeq;
        bool wasInWorld;
        bool wasScripted;
        bool wasDriving;
        bool waitLogged;
        bool warnedTerrain;
        readonly float[] heights = new float[Link.Grid * Link.Grid];
        int originX, originZ;

        BepInEx.Configuration.ConfigEntry<float> sensitivity;
        BepInEx.Configuration.ConfigEntry<bool> invertY;
        BepInEx.Configuration.ConfigEntry<float> worldScale;
        BepInEx.Configuration.ConfigEntry<bool> devConsole;
        BepInEx.Configuration.ConfigEntry<bool> modMenu;
        public static BepInEx.Configuration.ConfigEntry<int> MaxFps;
        BepInEx.Configuration.ConfigEntry<float> cursorSpeed;
        BepInEx.Configuration.ConfigEntry<string> digCap;
        public static float CursorSpeed = 10f;
        float lookYaw, lookPitch;
        int cameraMode;   // 0 first person, 1 behind, 2 in front (F5, like Minecraft)
        int slot;

        void Awake()
        {
            Log = Logger;
            // No GUILayout here: skipping Unity's layout pass saves an OnGUI call and its garbage every frame.
            useGUILayout = false;
            sensitivity = Config.Bind("Controls", "MouseSensitivity", 0.75f, "Degrees per unit of Unity mouse axis while Minecraft has the body.");
            invertY = Config.Bind("Controls", "InvertY", false, "Invert vertical mouse look.");
            cursorSpeed = Config.Bind("Controls", "CursorSpeed", 10f, "Speed of the cursor in Minecraft's inventory and chat (pixels per unit of mouse axis at 1080p).");
            CursorSpeed = cursorSpeed.Value;
            worldScale = Config.Bind("World", "Scale", 0f, "Forest units per Minecraft block (how big Steve and the blocks are). 0 = auto (The Forest eye height / Steve's 1.62, at most 1.8). Only used for new Minecraft worlds: each world keeps the size it was made with.");
            devConsole = Config.Bind("Debug", "DeveloperConsole", true, "The Forest's developer console on F1 (spawnmutant, spawnitem, godmode, goto...); 'mc <command>' in it runs a Minecraft command.");
            MaxFps = Config.Bind("Performance", "MaxFps", 120, "Cap on The Forest's frame rate (0 = no cap; 120 by default). Minecraft shares the graphics card: a Forest running flat out leaves it too little, and the hand/HUD stutter. Also in the F8 menu.");
            modMenu = Config.Bind("Debug", "ModMenu", true, "Clickable test menu on F8: spawn cannibals, animals and Minecraft mobs, give items, god mode, weather...");
            digCap = Config.Bind("World", "DugGroundLook", "Terrain", "How the ground around holes is drawn: Terrain (The Forest's own ground material) or Grass (Minecraft grass).");
            DigWorld.CapMode = digCap.Value == "Grass" ? "Grass" : "Terrain";
            if (!Link.Create())
            {
                Log.LogError("ForestCraft: shared link was not created");
                return;
            }
            new Harmony("dev.forestcraft").PatchAll(typeof(Plugin).Assembly);
            Launcher.Start();
            Log.LogInfo("ForestCraft: link ready, Prism instance ForestCraft");
        }

        void LateUpdate()
        {
            Perf.Frame();
            try { LateUpdateInner(); }
            finally { Perf.EndOfOurs(); }
        }

        float hopLoggedAt;

        void LateUpdateInner()
        {
            DevConsole.Enabled = devConsole.Value;
            ModMenu.Enabled = modMenu.Value;
            DevConsole.Ensure();
            DevConsole.Flush();
            ApplyFpsCap();
            StandUpFast.Update();
            if (!Link.Create()) return;
            // The Forest moved its player far by itself while Minecraft had the body (console goto,
            // the mod menu's trips): send Minecraft there too, the same way the body is first handed
            // over. A short hop is a cave door (CaveTriggers.CaveDoorRoutine puts the player a few
            // units across the doorway after a fade): Minecraft walks through that doorway itself,
            // and handing the body back and forth there threw the player out of the cave again.
            if (Drive.HasLastRoot && !Link.ForestMoved)
            {
                Transform root = TheForest.Utils.LocalPlayer.Transform;
                float hop = root != null ? (root.position - Drive.LastRootSet).magnitude : 0f;
                if (hop > 2f && hop <= 12f && Time.realtimeSinceStartup >= hopLoggedAt)
                {
                    hopLoggedAt = Time.realtimeSinceStartup + 2f;
                    Log.LogInfo("ForestCraft: The Forest moved the player " + hop.ToString("0.0") + " units (a cave door): Minecraft keeps the body");
                }
                if (root != null && hop > 12f)
                {
                    Link.ForestMoved = true;
                    Drive.HasLastRoot = false;
                    waitLogged = false;
                    Log.LogInfo("ForestCraft: The Forest moved the player " + (root.position - Drive.LastRootSet).magnitude.ToString("0.0") + " units: Minecraft follows");
                }
            }
            { double pq = Perf.Now(); Session.Update(Link.View); Perf.Add("session", pq); }
            if (Session.JustStarted)
            {
                // Another game (new or loaded): Minecraft opens its own world for it and hands
                // the body over again; nothing of the previous game's blocks or holes stays.
                teleportSeq = 0;
                Link.ForestMoved = false;
                waitLogged = false;
                Blocks.ClearAll();
                DigWorld.ClearAll(Link.View);
                Caves.TerrainIgnored = false;
            }
            // The Forest's frame rate, smoothed: Minecraft renders no more frames than are shown.
            float dt = Time.unscaledDeltaTime;
            if (dt > 0f && dt < 1f) forestFps = Mathf.Lerp(forestFps, 1f / dt, 0.05f);
            Link.WriteFloatAt(0x100 + 200, forestFps);
            Link.Heartbeat();
            { double pq = Perf.Now(); Launcher.Watch(); Perf.Add("launcher", pq); }
            Vector3 feet = Drive.Feet();
            double x, y, z;
            Link.UnityToMc(feet, out x, out y, out z);
            bool inWorld = Link.ForestInWorld();
            bool scripted = LocalPlayerSafe.Scripted();
            if (inWorld) ChooseScale();
            Link.UnityToMc(feet, out x, out y, out z);
            // Hand Minecraft the beach, not the plane. A new seq is the only time it may move us.
            // Only the end of a cutscene (or the first arrival) counts: closing the pause menu
            // or the inventory used to bump the seq and yank the body back to The Forest.
            if (inWorld && !scripted && (wasScripted || teleportSeq == 0 || Link.ForestMoved) && !Solids.NearReady)
            {
                // Scan The Forest's colliders around the feet first (see Solids.NearReady).
                Solids.Step(x, y, z);
                if (!waitLogged) { waitLogged = true; Log.LogInfo("ForestCraft: waiting for the collisions around the player"); }
            }
            if (inWorld && !scripted && (wasScripted || teleportSeq == 0 || Link.ForestMoved) && Solids.NearReady && StandUpFast.Ready)
            {
                Link.ForestMoved = false;
                teleportSeq++;
                if (teleportSeq == 0) teleportSeq = 1;
                Log.LogInfo("ForestCraft: handing the body to Minecraft, seq " + teleportSeq);
            }
            if (inWorld) wasScripted = scripted;
            wasInWorld = inWorld;

            bool driving = Link.Driving;
            int fw, fh;
            FrameSize(out fw, out fh);
            ModMenu.Update(inWorld && !scripted);
            McScreen.Suppress = DevConsole.Open || ModMenu.Open;
            { double pq = Perf.Now(); McScreen.Update(Link.View, driving, fw, fh); Perf.Add("mcscreen", pq); }
            float yaw = 0f, pitch = 0f;
            Camera cam = LocalPlayerSafe.Camera();
            if (driving && (McScreen.Open || DevConsole.Open || ModMenu.Open))
            {
                // A Minecraft screen is open: the mouse is a cursor, the view stays put.
                yaw = lookYaw;
                pitch = lookPitch;
            }
            else if (driving)
            {
                // The mouse is read here, every Forest frame, and Minecraft follows.
                // Turning through a 20 Hz round trip is what made looking around feel sticky.
                if (!wasDriving && cam != null) CameraAngles(cam, out lookYaw, out lookPitch);
                float k = sensitivity.Value;
                lookYaw = Mathf.Repeat(lookYaw + Input.GetAxis("Mouse X") * k, 360f);
                float dy = Input.GetAxis("Mouse Y") * k;
                lookPitch = Mathf.Clamp(lookPitch + (invertY.Value ? dy : -dy), -89.9f, 89.9f);
                yaw = lookYaw;
                pitch = lookPitch;
            }
            else if (cam != null)
            {
                CameraAngles(cam, out yaw, out pitch);
            }
            bool console = DevConsole.Open || ModMenu.Open;
            int input = inWorld && !McScreen.Open && !console ? ReadInput() : 0;
            if (driving && !McScreen.Open && !console) ReadHotbarAndF5();
            Link.WriteForest(x, y, z, yaw, pitch, teleportSeq, input, 0f, 0f, 0);
            Link.WriteView(driving ? cameraMode : 0, fw, fh, slot, Drive.ThirdPersonDistance);
            if (inWorld && !scripted)
            {
                { double pq = Perf.Now(); Ground.Update(); Perf.Add("ground", pq); }
                { double pq = Perf.Now(); Caves.Update(feet, input); Perf.Add("caves", pq); }
                { double pq = Perf.Now(); SampleGrid((float)x, (float)z); Perf.Add("grid", pq); }
                { double pq = Perf.Now(); FarGround.Update((float)x, (float)z); Perf.Add("far", pq); }
                double ps = Perf.Now();
                Solids.Step(x, y, z);
                Perf.Add("solids", ps);
            }
            { double pq = Perf.Now(); float sunNow = SunElevation(); Link.WriteSun(sunNow); Blocks.Daylight(sunNow, (Caves.Flags & 1) != 0); Perf.Add("sun", pq); }
            double pb = Perf.Now();
            Blocks.Update(Link.View);
            Perf.Add("blocks", pb);
            double pd = Perf.Now();
            DigWorld.Update(Link.View);
            Perf.Add("dig", pd);
            { double pq = Perf.Now(); Trees.Update(input); Perf.Add("trees", pq); }
            { double pq = Perf.Now(); Combat.Update(input); Combat.Arrows(); Combat.UpdatePushes(); Perf.Add("combat", pq); }
            { double pq = Perf.Now(); Tools.Update(input); Perf.Add("tools", pq); }
            { double pq = Perf.Now(); MobFights.Update(); Perf.Add("mobfights", pq); }
            // Minecraft holds the player after a respawn until the colliders around it are known.
            Link.WriteIntAt(0x100 + 132, Solids.NearReady ? 1 : 0);
            { double pq = Perf.Now(); Blocks.ReadHit(Link.View); Perf.Add("hit", pq); }
            // Dropped items now come with the other entities (Entities), stacks and all.
            { double pq = Perf.Now(); Cracks.Update(Link.View); Perf.Add("cracks", pq); }
            if (driving && !wasDriving) { Log.LogInfo("ForestCraft: Minecraft has the body"); Loot.DumpNames(); }
            wasDriving = driving;
            { double pq = Perf.Now(); if (driving) Drive.Apply(yaw, pitch, cameraMode); else Drive.Release(); Perf.Add("drive", pq); }
            // After Drive: the camera is where Minecraft looks only from here on (before, it hung off
            // The Forest's animated head, and the map was placed for that view).
            { double pq = Perf.Now(); HeldMap.Update(); Perf.Add("map", pq); }
            // After Apply: Steve stands exactly where the camera was just placed from.
            double pe = Perf.Now();
            Entities.Update(Link.View, cameraMode != 0, Drive.LastFeet);
            Perf.Add("entities", pe);
            pe = Perf.Now();
            Particles.Update(Link.View);
            Perf.Add("particles", pe);
            pe = Perf.Now();
            HeldLight.Update(cameraMode != 0, Drive.LastFeet);
            Perf.Add("light", pe);
        }

        static Light sun;
        static float sunSearchAt;

        public static float LastSunElevation = 45f;
        static float forestFps = 60f;

        static float SunElevation()
        {
            float e = SunElevationNow();
            if (!float.IsNaN(e)) LastSunElevation = e;
            return e;
        }

        // The Forest's sun keeps turning at night, when it is switched off and the moon lights the
        // scene (TheForestAtmosphere.Sun / Moon): read its direction whatever its state. Searching
        // the scene for "the" directional light found nothing enabled at night and searched again
        // every 5 s, all night long.
        static float SunElevationNow()
        {
            Transform t = null;
            try
            {
                var atmo = TheForest.Utils.Scene.Atmosphere;
                if (atmo != null && atmo.Sun != null) t = atmo.Sun.transform;
            }
            catch { }
            if (t == null)
            {
                if (sun == null && Time.realtimeSinceStartup >= sunSearchAt)
                {
                    sunSearchAt = Time.realtimeSinceStartup + 30f;
                    sun = RenderSettings.sun;
                }
                if (sun != null) t = sun.transform;
            }
            if (t == null) return float.NaN;
            return Mathf.Asin(Mathf.Clamp(-t.forward.y, -1f, 1f)) * Mathf.Rad2Deg;
        }

        // What the 30 s perf line also says about the scene (night, quality, how many enemies).
        public static string Context()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("sun ").Append(LastSunElevation.ToString("0")).Append("°");
            try { sb.Append(Clock.Dark ? " night" : " day"); } catch { }
            try { sb.Append(", in caves ").Append(TheForest.Utils.LocalPlayer.IsInCaves); } catch { }
            try { sb.Append(", quality ").Append(QualitySettings.names[QualitySettings.GetQualityLevel()]); } catch { }
            try
            {
                Camera cam = LocalPlayerSafe.Camera();
                if (cam != null) sb.Append(", path ").Append(cam.actualRenderingPath).Append(", far ").Append(cam.farClipPlane.ToString("0"));
            }
            catch { }
            try
            {
                var mc = TheForest.Utils.Scene.MutantControler;
                if (mc != null) sb.Append(", cannibals ").Append(mc.activeCannibals.Count + mc.activeInstantSpawnedCannibals.Count);
            }
            catch { }
            sb.Append(", ").Append(Blocks.LightsInfo());
            sb.Append(", ").Append(Corpses.Info());
            sb.Append(", held light ").Append(HeldLight.On ? "on" : "off");
            sb.Append(", res ").Append(Screen.width).Append('x').Append(Screen.height);
            return sb.ToString();
        }

        float wantedScale = 1.8f;

        // Forest units per Minecraft block. Each Minecraft world keeps the size it was made with
        // (its blocks and holes are placed in blocks: another size would move them all), so the
        // size wanted here (config, or auto) is only offered to Minecraft for a new world; the
        // world's own size, published by Minecraft at OFF_MC+196, is the one used.
        void ChooseScale()
        {
            float s = worldScale.Value;
            // The Forest's capsule is not the body (it measured 4.7 units), so size Steve from
            // where The Forest puts the eyes instead: Forest eye height / Steve's 1.62.
            float eye = 0f;
            Camera cam = LocalPlayerSafe.Camera();
            if (cam != null) eye = cam.transform.position.y - Drive.Feet().y;
            if (s <= 0f)
            {
                s = eye > 0.5f ? eye / 1.62f : 1.8f;
                // Kept under The Forest's own size (2.7): Minecraft makes Steve's hitbox narrower
                // as he grows (same width in The Forest as at 1.5), so doors and the cabin still
                // fit; his height (1.8 blocks) stays under The Forest's 4.7-unit capsule.
                s = Mathf.Clamp(s, 1f, 1.8f);
            }
            wantedScale = Mathf.Clamp(s, 0.5f, 3f);
            Link.WriteFloatAt(0x100 + 232, wantedScale);
            float world = Link.ReadFloatAt(0x200 + 196);
            float use = world >= 0.5f && world <= 3f ? world : wantedScale;
            if (Mathf.Abs(use - Link.Scale) > 1e-4f)
            {
                Link.Scale = use;
                // Everything cached in blocks was measured at the old size.
                Solids.ForgetAll();
                FarGround.Reset();
                DigWorld.TerrainsChanged();
                Log.LogInfo("ForestCraft: Forest eye height " + eye + " units, capsule " + Drive.ForestHeight() + ", scale " + use
                    + " units per block" + (world >= 0.5f ? " (this Minecraft world's size)" : " (wanted " + wantedScale + ")"));
            }
        }

        void ReadHotbarAndF5()
        {
            if (Input.GetKeyDown(KeyCode.F5)) cameraMode = (cameraMode + 1) % 3;
            if (Input.GetKeyDown(KeyCode.F9)) DigWorld.CycleCap();
            for (int i = 0; i < 9; i++)
            {
                if (Input.GetKeyDown(KeyCode.Alpha1 + i) || Input.GetKeyDown(KeyCode.Keypad1 + i)) slot = i;
            }
            float wheel = Input.mouseScrollDelta.y;
            if (wheel > 0.01f) slot = (slot + 8) % 9;      // wheel up: previous slot, like Minecraft
            else if (wheel < -0.01f) slot = (slot + 1) % 9;
        }

        // Minecraft renders at The Forest's resolution (capped at 1920x1080 for the copy), so the
        // hotbar has Minecraft's own size and pixels instead of a stretched 854x480 frame.
        static void FrameSize(out int w, out int h)
        {
            w = Screen.width;
            h = Screen.height;
            float k = Mathf.Min(1f, Mathf.Min(1920f / Mathf.Max(1, w), 1080f / Mathf.Max(1, h)));
            w = Mathf.Max(320, Mathf.RoundToInt(w * k));
            h = Mathf.Max(240, Mathf.RoundToInt(h * k));
        }

        static void CameraAngles(Camera cam, out float yaw, out float pitch)
        {
            Vector3 e = cam.transform.eulerAngles;
            yaw = e.y;
            pitch = e.x > 180f ? e.x - 360f : e.x;
        }

        // Re-applied every second: The Forest may set its own target frame rate (menus, loading).
        static float nextFpsCheck;
        static void ApplyFpsCap()
        {
            if (Time.realtimeSinceStartup < nextFpsCheck) return;
            nextFpsCheck = Time.realtimeSinceStartup + 1f;
            int cap = MaxFps != null ? MaxFps.Value : 0;
            int want = cap > 0 ? cap : -1;
            if (cap > 0 && QualitySettings.vSyncCount != 0)
            {
                // V-sync caps at the screen's rate, and Unity ignores the cap while it is on: on a
                // 144 Hz screen The Forest ran at 144 FPS whatever the cap said, and Minecraft
                // (sharing the graphics card) stuttered. Under the cap, v-sync does the job;
                // above it, v-sync goes off and the cap takes over.
                int hz = Screen.currentResolution.refreshRate;
                if (hz > 0 && hz <= cap + 2) return;
                QualitySettings.vSyncCount = 0;
                Log.LogInfo("ForestCraft: v-sync off (" + hz + " Hz screen): FPS capped at " + cap + " instead");
            }
            if (Application.targetFrameRate != want) Application.targetFrameRate = want;
        }

        void OnApplicationQuit()
        {
            Link.RequestQuit();
        }

        void OnGUI()
        {
            double po = Perf.Now();
            Overlay.Draw();
            ModMenu.Draw();
            Perf.Add("overlay", po);
        }

        static int ReadInput()
        {
            int bits = 0;
            if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.Z) || Input.GetAxisRaw("Vertical") > 0.5f) bits |= 1;
            if (Input.GetKey(KeyCode.S) || Input.GetAxisRaw("Vertical") < -0.5f) bits |= 2;
            if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.Q) || Input.GetAxisRaw("Horizontal") < -0.5f) bits |= 4;
            if (Input.GetKey(KeyCode.D) || Input.GetAxisRaw("Horizontal") > 0.5f) bits |= 8;
            if (Input.GetKey(KeyCode.Space)) bits |= 16;
            // Minecraft layout: Shift sneaks, Ctrl sprints (double-tap W works too, Minecraft handles it).
            if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.C)) bits |= 32;
            if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) bits |= 64;
            if (Input.GetMouseButton(0)) bits |= 128;
            if (Input.GetMouseButton(1)) bits |= 256;
            return bits;
        }

        // The island's heightmap, not a ray through the player. RaycastAll was hitting the
        // capsule and every tree, which both punched a hole under the feet and stalled the frame.
        readonly float[] corners = new float[(Link.Grid + 1) * (Link.Grid + 1)];
        readonly float[] mins = new float[Link.Grid * Link.Grid];
        readonly float[] maxs = new float[Link.Grid * Link.Grid];
        readonly byte[] kinds = new byte[Link.Grid * Link.Grid];
        int groundX = int.MinValue, groundZ = int.MinValue, groundTerrains;

        void SampleGrid(float mcX, float mcZ)
        {
            int ox = Mathf.FloorToInt(mcX) - Link.Grid / 2;
            int oz = Mathf.FloorToInt(mcZ) - Link.Grid / 2;
            bool moved = ox != originX || oz != originZ;
            originX = ox;
            originZ = oz;
            bool any = false;
            // The island as it was before any digging: holes are Minecraft's business (dug cells).
            for (int gz = 0; gz < Link.Grid; gz++)
            {
                for (int gx = 0; gx < Link.Grid; gx++)
                {
                    float h = Ground.Ready ? Ground.HeightMc(ox + gx + 0.5f, oz + gz + 0.5f)
                        : TerrainHeight((ox + gx + 0.5f) * Link.Scale, -(oz + gz + 0.5f) * Link.Scale) / Link.Scale;
                    heights[gz * Link.Grid + gx] = h;
                    if (!float.IsNaN(h)) any = true;
                }
            }
            if (!any && !warnedTerrain)
            {
                warnedTerrain = true;
                Log.LogWarning("ForestCraft: no ground under the player; Minecraft will not take the body");
            }
            Link.PublishGrid(originX, originZ, heights);
            Water.Update(ox, oz);
            // Corners and per-column extremes: only when the window moves (the original surface never changes).
            if (Ground.Ready && (moved || ox != groundX || oz != groundZ || groundTerrains != Ground.Terrains.Count))
            {
                groundX = ox; groundZ = oz; groundTerrains = Ground.Terrains.Count;
                int n = Link.Grid + 1;
                for (int gz = 0; gz < n; gz++)
                    for (int gx = 0; gx < n; gx++)
                        corners[gz * n + gx] = Ground.HeightMc(ox + gx, oz + gz);
                for (int gz = 0; gz < Link.Grid; gz++)
                {
                    for (int gx = 0; gx < Link.Grid; gx++)
                    {
                        float lo, hi;
                        Ground.MinMaxMc(ox + gx, oz + gz, out lo, out hi);
                        mins[gz * Link.Grid + gx] = lo;
                        maxs[gz * Link.Grid + gx] = hi;
                        kinds[gz * Link.Grid + gx] = (byte)(Ground.SandMc(ox + gx + 0.5f, oz + gz + 0.5f) ? 1 : 0);
                    }
                }
                Link.PublishGround(ox, oz, corners, mins, maxs, kinds);
            }
        }

        static float TerrainHeight(float x, float z)
        {
            Terrain[] terrains = Terrain.activeTerrains;
            if (terrains == null || terrains.Length == 0) return float.NaN;
            float best = float.NaN;
            for (int i = 0; i < terrains.Length; i++)
            {
                Terrain terrain = terrains[i];
                if (terrain == null || terrain.terrainData == null) continue;
                Vector3 origin = terrain.transform.position;
                Vector3 size = terrain.terrainData.size;
                if (x < origin.x || z < origin.z || x > origin.x + size.x || z > origin.z + size.z) continue;
                float h = terrain.SampleHeight(new Vector3(x, 0f, z)) + origin.y;
                if (float.IsNaN(best) || h > best) best = h;
            }
            return best;
        }
    }
}
