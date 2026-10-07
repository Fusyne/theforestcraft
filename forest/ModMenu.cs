using System;
using System.Collections.Generic;
using UnityEngine;

namespace ForestCraft
{
    // F8: a clickable test menu (spawn cannibals, animals, Minecraft mobs, give items, toggle
    // god mode...). The Forest's commands go through its developer console, Minecraft's through
    // the command block channel (DevConsole.SendToMinecraft). While it is open The Forest and
    // Minecraft hear nothing, the view stays put and the mouse moves a virtual cursor (The Forest
    // keeps relocking the real one), like Minecraft's own screens.
    static class ModMenu
    {
        public static bool Enabled = true;
        public static bool Open;

        class Entry
        {
            public string Label;
            public string[] Forest, Minecraft;
            public Func<bool> State;           // toggles: current state
            public string[] ForestOff, MinecraftOff;
            public string Key;                 // toggles whose state we keep ourselves
            public Action Do;                  // done here, not through a console
            public bool Radio;                 // one of a set: clicking it always selects it
        }

        class Page
        {
            public string Name;
            public readonly List<Entry> Entries = new List<Entry>();
        }

        static List<Page> pages;
        static readonly Dictionary<string, bool> flags = new Dictionary<string, bool>();
        static int page;
        static float cursorX, cursorY;
        static bool clicked;
        static Entry pending;
        static string status = "";
        static float statusTime = -100f;
        static bool listening;

        // ---------------------------------------------------------------- pages

        static void Build()
        {
            pages = new List<Page>();

            Page p = Add("Cannibals");
            F(p, "Male cannibal", "spawnenemy male");
            F(p, "Female cannibal", "spawnenemy female");
            F(p, "Skinny (male)", "spawnenemy male_skinny");
            F(p, "Skinny (female)", "spawnenemy female_skinny");
            F(p, "Pale", "spawnenemy pale");
            F(p, "Skinny pale", "spawnenemy skinny_pale");
            F(p, "Fireman", "spawnenemy fireman");
            F(p, "Dynamite man", "spawnenemy dynamiteman");
            F(p, "Virginia", "spawnenemy vags");
            F(p, "Armsy", "spawnenemy armsy");
            F(p, "Mutant baby", "spawnenemy baby");
            F(p, "Cowman", "spawnenemy fat");
            F(p, "Group of 5", "spawnenemy male --3", "spawnenemy female --2");
            F(p, "Kill the closest", "killclosestenemy");
            F(p, "Knock down the closest", "knockdownclosestenemy");
            F(p, "Kill all enemies", "killallenemies");
            Toggle(p, "Enemies active", "enemies", true, new[] { "enemies on" }, null, new[] { "enemies off" }, null);

            p = Add("Animals");
            F(p, "Rabbit", "spawnanimal rabbit");
            F(p, "Lizard", "spawnanimal lizard");
            F(p, "Deer", "spawnanimal deer");
            F(p, "Boar", "spawnanimal boar");
            F(p, "Raccoon", "spawnanimal raccoon");
            F(p, "Squirrel", "spawnanimal squirrel");
            F(p, "Sea turtle", "spawnanimal turtle");
            F(p, "Tortoise", "spawnanimal tortoise");
            F(p, "Crocodile", "spawnanimal crocodile");
            F(p, "Kill the closest", "killclosestanimal");
            F(p, "Kill all animals", "killallanimals");
            Toggle(p, "Animals active", "animals", true, new[] { "animals on" }, null, new[] { "animals off" }, null);
            Toggle(p, "Birds active", "birds", true, new[] { "birds on" }, null, new[] { "birds off" }, null);

            p = Add("Minecraft mobs");
            Summon(p, "Zombie", "zombie");
            Summon(p, "Skeleton", "skeleton");
            Summon(p, "Creeper", "creeper");
            Summon(p, "Spider", "spider");
            Summon(p, "Enderman", "enderman");
            Summon(p, "Witch", "witch");
            Summon(p, "Cow", "cow");
            Summon(p, "Pig", "pig");
            Summon(p, "Sheep", "sheep");
            Summon(p, "Chicken", "chicken");
            Summon(p, "Wolf", "wolf");
            Summon(p, "Fox", "fox");
            Summon(p, "Horse", "horse");
            Summon(p, "Villager", "villager");
            Summon(p, "Iron golem", "iron_golem");
            M(p, "Kill nearby mobs", "kill @e[type=!minecraft:player,distance=..64]");

            p = Add("Player");
            Toggle(p, "Invincible", "god", false,
                new[] { "godmode on" },
                new[] { "effect give @s minecraft:resistance infinite 4 true", "effect give @s minecraft:saturation infinite 0 true" },
                new[] { "godmode off" },
                new[] { "effect clear @s minecraft:resistance", "effect clear @s minecraft:saturation" });
            Both(p, "Heal fully", new[] { "setstat full" },
                new[] { "effect give @s minecraft:instant_health 1 5 true", "effect give @s minecraft:saturation 1 20 true" });
            Toggle(p, "Creative mode", "creative", false, null, new[] { "gamemode creative" }, null, new[] { "gamemode survival" });
            Toggle(p, "Night vision", "night", false, null, new[] { "effect give @s minecraft:night_vision infinite 0 true" },
                null, new[] { "effect clear @s minecraft:night_vision" });
            M(p, "Speed (1 min)", "effect give @s minecraft:speed 60 2 true");
            M(p, "+30 XP levels", "xp add @s 30 levels");
            p.Entries.Add(new Entry
            {
                Label = "Invisible (to enemies)",
                State = () => Stealth.On,
                Do = () => Stealth.Set(!Stealth.On),
                Forest = new[] { "invisible on" },
                ForestOff = new[] { "invisible off" },
            });
            ToggleState(p, "Infinite energy", () => Cheats.InfiniteEnergy, "energyhack on", "energyhack off");
            Act(p, "Unstuck (back to the surface)", Caves.Unstick);
            M(p, "Die (Minecraft)", "kill @s");
            F(p, "Die (The Forest)", "killlocalplayer");

            p = Add("Items");
            M(p, "Diamond sword", "give @s minecraft:diamond_sword");
            M(p, "Bow + 64 arrows", "give @s minecraft:bow", "give @s minecraft:arrow 64");
            M(p, "Diamond tools", "give @s minecraft:diamond_pickaxe", "give @s minecraft:diamond_axe", "give @s minecraft:diamond_shovel");
            M(p, "Iron armor", "give @s minecraft:iron_helmet", "give @s minecraft:iron_chestplate", "give @s minecraft:iron_leggings", "give @s minecraft:iron_boots");
            M(p, "Diamond armor", "give @s minecraft:diamond_helmet", "give @s minecraft:diamond_chestplate", "give @s minecraft:diamond_leggings", "give @s minecraft:diamond_boots");
            M(p, "64 torches", "give @s minecraft:torch 64");
            M(p, "32 cooked steaks", "give @s minecraft:cooked_beef 32");
            M(p, "8 golden apples", "give @s minecraft:golden_apple 8");
            M(p, "64 planks", "give @s minecraft:oak_planks 64");
            M(p, "64 cobblestone", "give @s minecraft:cobblestone 64");
            M(p, "Crafting table + furnace", "give @s minecraft:crafting_table", "give @s minecraft:furnace");
            M(p, "Water bucket", "give @s minecraft:water_bucket");
            M(p, "Clear inventory", "clear @s");
            F(p, "All items (The Forest)", "addallitems");
            Toggle(p, "Infinite items (The Forest)", "itemhack", false, new[] { "itemhack on" }, null, new[] { "itemhack off" }, null);

            p = Add("World");
            Act(p, "Full day (noon)", () => SetTime(358f));
            Act(p, "Night (midnight)", () => SetTime(180f));
            Act(p, "Sunset", () => SetTime(75f));
            F(p, "Heavy rain", "forcerain heavy");
            F(p, "Clear weather", "forcerain sunny");
            F(p, "Map (M) + caves revealed", "additem MapFull", "additem CaveMap", "revealcavemap");
            F(p, "Go to the plane wreck", "goto Hull");
            ToggleState(p, "Instant building", () => Cheats.Creative, "buildhack on", "buildhack off");
            F(p, "Save", "save");

            p = Add("Perf");
            foreach (int fps in new[] { 60, 90, 120, 144, 0 })
            {
                int f = fps;
                p.Entries.Add(new Entry
                {
                    Label = f == 0 ? "Unlimited FPS" : "Cap at " + f + " FPS",
                    State = () => Plugin.MaxFps != null && Plugin.MaxFps.Value == f,
                    Do = () => { if (Plugin.MaxFps != null) Plugin.MaxFps.Value = f; },
                    Radio = true,
                });
            }
        }

        static Page Add(string name)
        {
            var p = new Page { Name = name };
            pages.Add(p);
            return p;
        }

        static void Act(Page p, string label, Action action) { p.Entries.Add(new Entry { Label = label, Do = action }); }

        // The Forest's clock: dark from 88 to 270, the sun highest around 358 (TheForestAtmosphere).
        // Its own "advanceday" jumps to dusk or to dawn, never to noon or midnight.
        static void SetTime(float timeOfDay)
        {
            var atmo = TheForest.Utils.Scene.Atmosphere;
            if (atmo == null) throw new Exception("no atmosphere (not in a game?)");
            atmo.TimeOfDay = timeOfDay;
            atmo.ForceSunRotationUpdate = true;
        }

        static void F(Page p, string label, params string[] forest) { p.Entries.Add(new Entry { Label = label, Forest = forest }); }
        static void M(Page p, string label, params string[] mc) { p.Entries.Add(new Entry { Label = label, Minecraft = mc }); }
        static void Both(Page p, string label, string[] forest, string[] mc) { p.Entries.Add(new Entry { Label = label, Forest = forest, Minecraft = mc }); }

        // Minecraft mob 3 blocks ahead, at the feet's height whatever the pitch.
        static void Summon(Page p, string label, string id)
        {
            M(p, label, "execute rotated ~ 0 run summon minecraft:" + id + " ^ ^ ^3");
        }

        // A toggle whose state we keep ourselves (neither game tells us).
        static void Toggle(Page p, string label, string key, bool initial, string[] forestOn, string[] mcOn, string[] forestOff, string[] mcOff)
        {
            flags[key] = initial;
            p.Entries.Add(new Entry
            {
                Label = label, Key = key, State = () => flags[key],
                Forest = forestOn, Minecraft = mcOn, ForestOff = forestOff, MinecraftOff = mcOff,
            });
        }

        // A toggle whose state The Forest gives us.
        static void ToggleState(Page p, string label, Func<bool> state, string on, string off)
        {
            p.Entries.Add(new Entry
            {
                Label = label,
                State = () => { try { return state(); } catch { return false; } },
                Forest = new[] { on }, ForestOff = new[] { off },
            });
        }

        // ---------------------------------------------------------------- input

        public static void Update(bool inWorld)
        {
            if (!Enabled) { Open = false; return; }
            if (pages == null) Build();
            if (!listening)
            {
                listening = true;
                Application.logMessageReceived += OnLog;
            }
            if (UnityEngine.Input.GetKeyDown(KeyCode.F8))
            {
                if (Open) Open = false;
                else if (inWorld && !McScreen.Open && !DevConsole.Open)
                {
                    Open = true;
                    cursorX = Screen.width * 0.5f;
                    cursorY = Screen.height * 0.5f;
                    clicked = false;
                }
            }
            if (Open && (!inWorld || McScreen.Open)) Open = false;
            if (!Open)
            {
                clicked = false;
                pending = null;
                return;
            }
            if (UnityEngine.Input.GetKeyDown(KeyCode.Escape)) { Open = false; return; }
            if (UnityEngine.Input.GetKeyDown(KeyCode.Tab) || UnityEngine.Input.GetKeyDown(KeyCode.RightArrow)) page = (page + 1) % pages.Count;
            if (UnityEngine.Input.GetKeyDown(KeyCode.LeftArrow)) page = (page + pages.Count - 1) % pages.Count;

            float speed = Plugin.CursorSpeed * Screen.height / 1080f;
            cursorX = Mathf.Clamp(cursorX + UnityEngine.Input.GetAxisRaw("Mouse X") * speed, 0f, Screen.width - 1);
            cursorY = Mathf.Clamp(cursorY - UnityEngine.Input.GetAxisRaw("Mouse Y") * speed, 0f, Screen.height - 1);
            if (UnityEngine.Input.GetMouseButtonDown(0)) clicked = true;

            if (pending != null)
            {
                Entry e = pending;
                pending = null;
                Run(e);
            }
        }

        static void Run(Entry e)
        {
            bool off = e.State != null && !e.Radio && e.State();
            string[] forest = off ? e.ForestOff : e.Forest;
            string[] mc = off ? e.MinecraftOff : e.Minecraft;
            try
            {
                if (e.Do != null) e.Do();
                if (forest != null)
                {
                    foreach (string c in forest)
                    {
                        if (!DevConsole.RunForest(c)) { Say("The Forest's console isn't available"); return; }
                    }
                }
                if (mc != null)
                {
                    if (Link.View == IntPtr.Zero || !Link.Driving) { Say("Minecraft doesn't have the body yet: try again in a moment"); return; }
                    foreach (string c in mc) DevConsole.SendToMinecraft(c);
                }
                if (e.Key != null) flags[e.Key] = !off;
                if (e.State != null) Say(e.Label + (off ? ": off" : ": on"));
                else Say(e.Label + ": OK");
                Plugin.Log.LogInfo("ForestCraft: mod menu -> " + e.Label);
            }
            catch (Exception ex)
            {
                Say("Error: " + ex.Message);
                Plugin.Log.LogWarning("ForestCraft: mod menu '" + e.Label + "' failed: " + ex);
            }
        }

        static void Say(string text)
        {
            status = text;
            statusTime = Time.realtimeSinceStartup;
        }

        // The Forest's console answers with "$> ..." log lines: show the latest one.
        static void OnLog(string message, string stack, LogType type)
        {
            if (!Open || message == null || !message.StartsWith("$> ")) return;
            Say(message.Substring(3));
        }

        // ---------------------------------------------------------------- drawing

        static GUIStyle panelStyle, titleStyle, tabStyle, tabOnStyle, buttonStyle, buttonHoverStyle, buttonOnStyle, buttonOnHoverStyle, statusStyle, hintStyle;
        static Texture2D cursorTex;

        static Texture2D Solid(Color c)
        {
            var t = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            t.SetPixel(0, 0, c);
            t.Apply(false);
            t.hideFlags = HideFlags.DontSave;
            return t;
        }

        static GUIStyle Style(Color back, Color text, TextAnchor anchor, FontStyle font)
        {
            var s = new GUIStyle();
            if (back.a > 0f) s.normal.background = Solid(back);
            s.normal.textColor = text;
            s.alignment = anchor;
            s.fontStyle = font;
            s.wordWrap = true;
            s.clipping = TextClipping.Clip;
            s.padding = new RectOffset(6, 6, 2, 2);
            return s;
        }

        static void Styles()
        {
            if (panelStyle != null) return;
            panelStyle = Style(new Color(0.07f, 0.08f, 0.07f, 0.92f), Color.white, TextAnchor.UpperLeft, FontStyle.Normal);
            titleStyle = Style(Color.clear, new Color(0.85f, 0.95f, 0.75f), TextAnchor.MiddleLeft, FontStyle.Bold);
            tabStyle = Style(new Color(0.18f, 0.2f, 0.17f, 1f), new Color(0.8f, 0.8f, 0.8f), TextAnchor.MiddleCenter, FontStyle.Normal);
            tabOnStyle = Style(new Color(0.33f, 0.5f, 0.25f, 1f), Color.white, TextAnchor.MiddleCenter, FontStyle.Bold);
            buttonStyle = Style(new Color(0.26f, 0.27f, 0.25f, 1f), Color.white, TextAnchor.MiddleCenter, FontStyle.Normal);
            buttonHoverStyle = Style(new Color(0.4f, 0.42f, 0.38f, 1f), Color.white, TextAnchor.MiddleCenter, FontStyle.Normal);
            buttonOnStyle = Style(new Color(0.25f, 0.45f, 0.2f, 1f), Color.white, TextAnchor.MiddleCenter, FontStyle.Bold);
            buttonOnHoverStyle = Style(new Color(0.35f, 0.6f, 0.28f, 1f), Color.white, TextAnchor.MiddleCenter, FontStyle.Bold);
            statusStyle = Style(Color.clear, new Color(1f, 0.92f, 0.6f), TextAnchor.MiddleLeft, FontStyle.Normal);
            hintStyle = Style(Color.clear, new Color(0.65f, 0.65f, 0.65f), TextAnchor.MiddleRight, FontStyle.Normal);
        }

        public static void Draw()
        {
            if (!Open || pages == null) return;
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            Styles();
            float s = Mathf.Clamp(Screen.height / 1080f, 0.6f, 2.5f);
            int font = Mathf.RoundToInt(17 * s);
            buttonStyle.fontSize = buttonHoverStyle.fontSize = buttonOnStyle.fontSize = buttonOnHoverStyle.fontSize = font;
            tabStyle.fontSize = tabOnStyle.fontSize = font;
            titleStyle.fontSize = Mathf.RoundToInt(22 * s);
            statusStyle.fontSize = hintStyle.fontSize = Mathf.RoundToInt(15 * s);

            Vector2 mouse = new Vector2(cursorX, cursorY);
            bool click = clicked;
            clicked = false;

            float w = Mathf.Min(900f * s, Screen.width - 20f);
            float h = Mathf.Min(640f * s, Screen.height - 20f);
            Rect panel = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
            GUI.Box(panel, GUIContent.none, panelStyle);
            float pad = 14f * s;
            float x0 = panel.x + pad, y = panel.y + pad, inner = w - 2f * pad;

            GUI.Label(new Rect(x0, y, inner, 30f * s), "ForestCraft: test menu", titleStyle);
            y += 38f * s;

            // Tabs
            float gap = 6f * s;
            float tw = (inner - gap * (pages.Count - 1)) / pages.Count;
            float th = 36f * s;
            for (int i = 0; i < pages.Count; i++)
            {
                Rect r = new Rect(x0 + i * (tw + gap), y, tw, th);
                bool hover = r.Contains(mouse);
                if (hover && click) { page = i; click = false; }
                GUI.Box(r, pages[i].Name, i == page ? tabOnStyle : hover ? buttonHoverStyle : tabStyle);
            }
            y += th + 14f * s;

            // Buttons, 3 columns
            Page cur = pages[page];
            const int cols = 3;
            float bh = 44f * s;
            float bw = (inner - gap * (cols - 1)) / cols;
            for (int i = 0; i < cur.Entries.Count; i++)
            {
                Entry e = cur.Entries[i];
                Rect r = new Rect(x0 + (i % cols) * (bw + gap), y + (i / cols) * (bh + gap), bw, bh);
                bool hover = r.Contains(mouse);
                bool on = false;
                if (e.State != null) { try { on = e.State(); } catch { } }
                if (hover && click) { pending = e; click = false; }
                string label = e.State != null ? e.Label + (on ? "  [ON]" : "  [OFF]") : e.Label;
                GUI.Box(r, label, on ? (hover ? buttonOnHoverStyle : buttonOnStyle) : (hover ? buttonHoverStyle : buttonStyle));
            }

            // Status and hint
            float by = panel.yMax - pad - 26f * s;
            if (Time.realtimeSinceStartup - statusTime < 6f)
                GUI.Label(new Rect(x0, by, inner * 0.62f, 26f * s), status, statusStyle);
            GUI.Label(new Rect(x0 + inner * 0.62f, by, inner * 0.38f, 26f * s), "F8 / Esc: close   Tab: next page", hintStyle);

            DrawCursor();
        }

        static void DrawCursor()
        {
            if (cursorTex == null) cursorTex = McScreen.Arrow();
            float size = Mathf.Max(16f, Screen.height / 45f);
            GUI.DrawTexture(new Rect(cursorX, cursorY, size, size), cursorTex);
        }
    }
}
