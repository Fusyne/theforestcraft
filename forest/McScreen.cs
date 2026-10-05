using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using HarmonyLib;
using UnityEngine;

namespace ForestCraft
{
    // Minecraft's inventory (E), chat (T) and command line (/) inside The Forest's window.
    // While one is open: The Forest's cursor is freed, its own controls are muted (pause menu,
    // inventory, interactions all go through TheForest.Utils.Input, patched below), and the
    // cursor, buttons, wheel, keys and typed text are forwarded to Minecraft's own input callbacks.
    static class McScreen
    {
        const int OffKeys = 0xA80000;
        const int KeyRing = 256;

        public static bool Open;
        static bool wasOpen;
        public static float CursorX, CursorY; // GUI pixels, top-left origin
        static Texture2D arrow;

        // A Minecraft-style arrow, drawn over the overlay while a screen is open.
        public static void DrawCursor()
        {
            if (!Open) return;
            if (arrow == null) arrow = MakeArrow();
            float size = Mathf.Max(16f, Screen.height / 45f);
            GUI.DrawTexture(new Rect(CursorX, CursorY, size, size), arrow);
        }

        // Pointer shape, row y from the top: a slanted triangle with a short stem.
        static bool Inside(int x, int y)
        {
            if (x < 0 || y < 0 || x >= 16 || y >= 16) return false;
            bool head = x <= y * 0.62f && y <= 11;
            bool stem = y >= 8 && y <= 14 && x >= 3 + (y - 8) / 3 && x <= 5 + (y - 8) / 3;
            return head || stem;
        }

        static Texture2D MakeArrow()
        {
            const int n = 16;
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false);
            t.filterMode = FilterMode.Point;
            t.wrapMode = TextureWrapMode.Clamp;
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    bool inside = Inside(x, y);
                    bool edge = !inside && (Inside(x - 1, y) || Inside(x + 1, y) || Inside(x, y - 1) || Inside(x, y + 1));
                    // Texture rows go bottom-up.
                    px[(n - 1 - y) * n + x] = inside ? new Color32(255, 255, 255, 255) : edge ? new Color32(0, 0, 0, 255) : new Color32(0, 0, 0, 0);
                }
            }
            t.SetPixels32(px);
            t.Apply(false);
            return t;
        }

        static int requestSeq, keysWritten;
        static readonly int[] downs = new int[3], ups = new int[3];
        static float wheel;
        static readonly Dictionary<KeyCode, int> glfw = BuildKeys();

        public static void Reset(IntPtr view)
        {
            Marshal.WriteInt32(view, 0x100 + 96, 0);
            Marshal.WriteInt32(view, 0x100 + 120, 0);
            requestSeq = 0;
            keysWritten = 0;
        }

        public static void Update(IntPtr view, bool driving, int frameW, int frameH)
        {
            if (view == IntPtr.Zero) return;
            bool open = driving && Link.ReadMcInt(160) == 1;
            wasOpen = Open;
            Open = open;
            if (!driving) return;
            if (!Open)
            {
                int kind = 0;
                if (UnityEngine.Input.GetKeyDown(KeyCode.I)) kind = 1; // E stays The Forest's "take"
                else if (UnityEngine.Input.GetKeyDown(KeyCode.T)) kind = 2;
                else if (UnityEngine.Input.GetKeyDown(KeyCode.Slash) || UnityEngine.Input.GetKeyDown(KeyCode.KeypadDivide)) kind = 3;
                if (kind != 0)
                {
                    Marshal.WriteInt32(view, 0x100 + 100, kind);
                    Marshal.WriteInt32(view, 0x100 + 96, ++requestSeq);
                }
                return;
            }

            // The Forest keeps relocking the real cursor, so ours is virtual: it moves with the
            // mouse deltas (which still arrive while locked) and Overlay draws it.
            if (!wasOpen) { CursorX = Screen.width * 0.5f; CursorY = Screen.height * 0.5f; }
            float speed = Plugin.CursorSpeed * Screen.height / 1080f;
            CursorX = Mathf.Clamp(CursorX + UnityEngine.Input.GetAxisRaw("Mouse X") * speed, 0f, Screen.width - 1);
            CursorY = Mathf.Clamp(CursorY - UnityEngine.Input.GetAxisRaw("Mouse Y") * speed, 0f, Screen.height - 1);
            // Fraction of the screen, top-left origin: Minecraft scales it to its own GUI.
            float x = CursorX / Mathf.Max(1, Screen.width);
            float y = CursorY / Mathf.Max(1, Screen.height);
            Link.WriteFloatAt(0x100 + 104, x);
            Link.WriteFloatAt(0x100 + 108, y);
            int buttons = 0;
            if (UnityEngine.Input.GetMouseButton(0)) buttons |= 1;
            if (UnityEngine.Input.GetMouseButton(1)) buttons |= 2;
            if (UnityEngine.Input.GetMouseButton(2)) buttons |= 4;
            Marshal.WriteInt32(view, 0x100 + 112, buttons);
            // Presses and releases counted too (one byte per button): a quick click that starts
            // and ends between two Minecraft frames is not lost.
            for (int b = 0; b < 3; b++)
            {
                if (UnityEngine.Input.GetMouseButtonDown(b)) downs[b]++;
                if (UnityEngine.Input.GetMouseButtonUp(b)) ups[b]++;
            }
            Marshal.WriteInt32(view, 0x100 + 204, (downs[0] & 255) | ((downs[1] & 255) << 8) | ((downs[2] & 255) << 16));
            Marshal.WriteInt32(view, 0x100 + 208, (ups[0] & 255) | ((ups[1] & 255) << 8) | ((ups[2] & 255) << 16));
            wheel += UnityEngine.Input.mouseScrollDelta.y;
            Link.WriteFloatAt(0x100 + 116, wheel);

            foreach (var pair in glfw)
            {
                if (UnityEngine.Input.GetKeyDown(pair.Key)) Push(view, 0, pair.Value, 1);
                if (UnityEngine.Input.GetKeyUp(pair.Key)) Push(view, 0, pair.Value, 0);
            }
            string typed = UnityEngine.Input.inputString;
            for (int i = 0; i < typed.Length; i++)
            {
                char c = typed[i];
                if (c < 32 || c == 127) continue; // Backspace/Enter/Escape go as keys
                Push(view, 1, c, 1);
            }
            Marshal.WriteInt32(view, 0x100 + 120, keysWritten);
        }

        static void Push(IntPtr view, int type, int code, int action)
        {
            int at = OffKeys + 16 + (keysWritten % KeyRing) * 12;
            Marshal.WriteInt32(view, at, type);
            Marshal.WriteInt32(view, at + 4, code);
            Marshal.WriteInt32(view, at + 8, action);
            keysWritten++;
        }

        // Unity KeyCode -> GLFW key code (physical US layout, which is what Minecraft expects).
        static Dictionary<KeyCode, int> BuildKeys()
        {
            var d = new Dictionary<KeyCode, int>();
            for (int i = 0; i < 26; i++) d[KeyCode.A + i] = 65 + i;
            for (int i = 0; i < 10; i++) d[KeyCode.Alpha0 + i] = 48 + i;
            for (int i = 0; i < 10; i++) d[KeyCode.Keypad0 + i] = 320 + i;
            for (int i = 0; i < 12; i++) d[KeyCode.F1 + i] = 290 + i;
            d[KeyCode.Space] = 32; d[KeyCode.Escape] = 256; d[KeyCode.Return] = 257; d[KeyCode.Tab] = 258;
            d[KeyCode.Backspace] = 259; d[KeyCode.Insert] = 260; d[KeyCode.Delete] = 261;
            d[KeyCode.RightArrow] = 262; d[KeyCode.LeftArrow] = 263; d[KeyCode.DownArrow] = 264; d[KeyCode.UpArrow] = 265;
            d[KeyCode.PageUp] = 266; d[KeyCode.PageDown] = 267; d[KeyCode.Home] = 268; d[KeyCode.End] = 269;
            d[KeyCode.KeypadEnter] = 335; d[KeyCode.LeftShift] = 340; d[KeyCode.LeftControl] = 341; d[KeyCode.LeftAlt] = 342;
            d[KeyCode.RightShift] = 344; d[KeyCode.RightControl] = 345; d[KeyCode.RightAlt] = 346;
            return d;
        }
    }

    // While a Minecraft screen is open, The Forest hears nothing: no pause menu on Escape,
    // no survival book/inventory, no "take" on E, and it does not relock the mouse.
    static class ForestInputTargets
    {
        static readonly string[] Names = { "GetButtonDown", "GetButtonUp", "GetButton", "GetButtonPress", "GetKeyDown", "GetKeyUp", "GetKey", "GetMouseButtonDown", "GetMouseButtonUp", "GetMouseButton", "GetAxis", "GetAxisDown", "LockMouse", "get_anyKeyDown" };

        public static IEnumerable<System.Reflection.MethodBase> Of(Type returns)
        {
            foreach (var method in typeof(TheForest.Utils.Input).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            {
                if (Array.IndexOf(Names, method.Name) >= 0 && method.ReturnType == returns) yield return method;
            }
        }
    }

    [HarmonyPatch]
    static class MuteForestButtons
    {
        static IEnumerable<System.Reflection.MethodBase> TargetMethods() { return ForestInputTargets.Of(typeof(bool)); }
        static bool Prefix(ref bool __result)
        {
            if (!McScreen.Open) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch]
    static class MuteForestAxes
    {
        static IEnumerable<System.Reflection.MethodBase> TargetMethods() { return ForestInputTargets.Of(typeof(float)); }
        static bool Prefix(ref float __result)
        {
            if (!McScreen.Open) return true;
            __result = 0f;
            return false;
        }
    }

    [HarmonyPatch]
    static class MuteForestMouseLock
    {
        static IEnumerable<System.Reflection.MethodBase> TargetMethods() { return ForestInputTargets.Of(typeof(void)); }
        static bool Prefix() { return !McScreen.Open; }
    }
}
