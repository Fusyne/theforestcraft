using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ForestCraft
{
    [BepInPlugin("dev.forestcraft", "ForestCraft", "0.2.0")]
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
        BepInEx.Configuration.ConfigEntry<float> cursorSpeed;
        BepInEx.Configuration.ConfigEntry<string> digCap;
        public static float CursorSpeed = 10f;
        float lookYaw, lookPitch;
        int cameraMode;   // 0 first person, 1 behind, 2 in front (F5, like Minecraft)
        int slot;

        void Awake()
        {
            Log = Logger;
            sensitivity = Config.Bind("Controls", "MouseSensitivity", 0.75f, "Degrees per unit of Unity mouse axis while Minecraft has the body.");
            invertY = Config.Bind("Controls", "InvertY", false, "Invert vertical mouse look.");
            cursorSpeed = Config.Bind("Controls", "CursorSpeed", 10f, "Speed of the cursor in Minecraft's inventory and chat (pixels per unit of mouse axis at 1080p).");
            CursorSpeed = cursorSpeed.Value;
            worldScale = Config.Bind("World", "Scale", 0f, "Forest units per Minecraft block. 0 = auto (The Forest eye height / Steve's 1.62, kept between 1 and 1.35). Bigger = you are bigger, but above ~1.35 the plane's door gets too tight.");
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
            if (!Link.Create()) return;
            Link.Heartbeat();
            Launcher.Watch();
            Vector3 feet = Drive.Feet();
            double x, y, z;
            Link.UnityToMc(feet, out x, out y, out z);
            bool inWorld = Link.ForestInWorld();
            bool scripted = LocalPlayerSafe.Scripted();
            if (teleportSeq == 0 && inWorld && !scripted) ChooseScale();
            Link.UnityToMc(feet, out x, out y, out z);
            // Hand Minecraft the beach, not the plane. A new seq is the only time it may move us.
            // Only the end of a cutscene (or the first arrival) counts: closing the pause menu
            // or the inventory used to bump the seq and yank the body back to The Forest.
            if (inWorld && !scripted && (wasScripted || teleportSeq == 0) && !Solids.NearReady)
            {
                // Scan The Forest's colliders around the feet first (see Solids.NearReady).
                Solids.Step(x, y, z);
                if (!waitLogged) { waitLogged = true; Log.LogInfo("ForestCraft: waiting for the collisions around the player"); }
            }
            if (inWorld && !scripted && (wasScripted || teleportSeq == 0) && Solids.NearReady)
            {
                teleportSeq++;
                if (teleportSeq == 0) teleportSeq = 1;
                Log.LogInfo("ForestCraft: handing the body to Minecraft, seq " + teleportSeq);
            }
            if (inWorld) wasScripted = scripted;
            wasInWorld = inWorld;

            bool driving = Link.Driving;
            int fw, fh;
            FrameSize(out fw, out fh);
            McScreen.Update(Link.View, driving, fw, fh);
            float yaw = 0f, pitch = 0f;
            Camera cam = LocalPlayerSafe.Camera();
            if (driving && McScreen.Open)
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
            int input = inWorld && !McScreen.Open ? ReadInput() : 0;
            if (driving && !McScreen.Open) ReadHotbarAndF5();
            Link.WriteForest(x, y, z, yaw, pitch, teleportSeq, input, 0f, 0f, 0);
            Link.WriteView(driving ? cameraMode : 0, fw, fh, slot, Drive.ThirdPersonDistance);
            if (inWorld && !scripted)
            {
                Ground.Update();
                SampleGrid((float)x, (float)z);
                double ps = Perf.Now();
                Solids.Step(x, y, z);
                Perf.Add("solids", ps);
            }
            Link.WriteSun(SunElevation());
            double pb = Perf.Now();
            Blocks.Update(Link.View);
            Perf.Add("blocks", pb);
            double pd = Perf.Now();
            DigWorld.Update(Link.View);
            Perf.Add("dig", pd);
            Trees.Update(input);
            Combat.Update(input);
            // Minecraft holds the player after a respawn until the colliders around it are known.
            Link.WriteIntAt(0x100 + 132, Solids.NearReady ? 1 : 0);
            Blocks.ReadHit(Link.View);
            // Dropped items now come with the other entities (Entities), stacks and all.
            Cracks.Update(Link.View);
            if (driving && !wasDriving) { Log.LogInfo("ForestCraft: Minecraft has the body"); Loot.DumpNames(); }
            wasDriving = driving;
            if (driving) Drive.Apply(yaw, pitch, cameraMode);
            else Drive.Release();
            // After Apply: Steve stands exactly where the camera was just placed from.
            double pe = Perf.Now();
            Entities.Update(Link.View, cameraMode != 0, Drive.LastFeet);
            Perf.Add("entities", pe);
        }

        static Light sun;
        static float sunSearchAt;

        static float SunElevation()
        {
            if ((sun == null || !sun.isActiveAndEnabled) && Time.realtimeSinceStartup >= sunSearchAt)
            {
                sunSearchAt = Time.realtimeSinceStartup + 5f;
                sun = RenderSettings.sun;
                if (sun == null)
                {
                    Light[] lights = Object.FindObjectsOfType<Light>();
                    for (int i = 0; i < lights.Length; i++)
                    {
                        if (lights[i].type == LightType.Directional && (sun == null || lights[i].intensity > sun.intensity)) sun = lights[i];
                    }
                }
            }
            if (sun == null) return float.NaN;
            return Mathf.Asin(Mathf.Clamp(-sun.transform.forward.y, -1f, 1f)) * Mathf.Rad2Deg;
        }

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
                s = eye > 0.5f ? eye / 1.62f : 1.2f;
                // Kept modest: a bigger Steve stops fitting through the plane's door and cabin.
                s = Mathf.Clamp(s, 1f, 1.5f);
            }
            s = Mathf.Clamp(s, 0.5f, 3f);
            Link.Scale = s;
            Log.LogInfo("ForestCraft: Forest eye height " + eye + " units, capsule " + Drive.ForestHeight() + ", scale " + s + " units per block");
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

        void OnApplicationQuit()
        {
            Link.RequestQuit();
        }

        void OnGUI()
        {
            double po = Perf.Now();
            Overlay.Draw();
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
                    }
                }
                Link.PublishGround(ox, oz, corners, mins, maxs);
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
