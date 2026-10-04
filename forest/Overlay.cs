using UnityEngine;

namespace ForestCraft
{
    // Draws Minecraft's latest frame (hand, HUD, screens) over The Forest, using its own alpha.
    static class Overlay
    {
        static Texture2D tex;
        static Material premulMat;
        static bool premulTried;

        // Minecraft's frame is premultiplied (colour already x alpha: its GUI blends onto a
        // transparent target). Drawn with ordinary alpha blending, every translucent pixel was
        // multiplied twice: hotbar, hearts' backgrounds, screens all came out much too dark.
        // Blend One / OneMinusSrcAlpha is the right operator. Unity 5.6 can still build a
        // fixed-function shader from source at runtime; a built-in premultiplied particle
        // shader is the second choice; plain GUI drawing the last resort.
        static Material Premultiplied()
        {
            if (premulTried) return premulMat;
            premulTried = true;
            const string source =
                "Shader \"Hidden/ForestCraftPremultiplied\" {" +
                " Properties { _MainTex (\"Frame\", 2D) = \"white\" {} }" +
                " SubShader { Tags { \"Queue\"=\"Overlay\" }" +
                "  Pass { ZTest Always ZWrite Off Cull Off Lighting Off Fog { Mode Off }" +
                "   Blend One OneMinusSrcAlpha" +
                "   SetTexture [_MainTex] { combine texture } } } }";
            try
            {
                var ctor = typeof(Material).GetConstructor(new[] { typeof(string) });
                if (ctor != null)
                {
                    var m = (Material)ctor.Invoke(new object[] { source });
                    if (m != null && m.shader != null && m.shader.isSupported) premulMat = m;
                }
            }
            catch (System.Exception) { }
            if (premulMat == null)
            {
                Shader s = Shader.Find("Particles/Alpha Blended Premultiply");
                if (s == null) s = Shader.Find("Legacy Shaders/Particles/Alpha Blended Premultiply");
                if (s != null) premulMat = new Material(s);
            }
            if (premulMat != null) premulMat.hideFlags = HideFlags.HideAndDontSave;
            Plugin.Log.LogInfo("ForestCraft: overlay blending " + (premulMat != null ? premulMat.shader.name : "plain GUI (no premultiplied shader)"));
            return premulMat;
        }
        static int lastSeq = -1;

        public static void Draw()
        {
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            if (!Link.Driving) return;
            int w, h;
            byte[] raw;
            bool keyed;
            Color32[] pixels;
            if (Link.ReadFrameRaw(ref lastSeq, out raw, out pixels, out keyed, out w, out h))
            {
                if (tex == null || tex.width != w || tex.height != h)
                {
                    if (tex != null) Object.Destroy(tex);
                    // linear: true -> no sRGB decode on sampling. In a linear-space project an
                    // sRGB texture is darkened when sampled and IMGUI writes it out as is: that was
                    // the "too dark" HUD. Minecraft's bytes are display-ready; keep them as they are.
                    tex = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
                    Plugin.Log.LogInfo("ForestCraft: overlay " + w + "x" + h + ", colour space " + QualitySettings.activeColorSpace);
                    tex.wrapMode = TextureWrapMode.Clamp;
                    tex.filterMode = FilterMode.Point;
                }
                // Minecraft clears to transparent: its own alpha is the mask, no per-pixel work.
                if (!keyed) tex.LoadRawTextureData(raw);
                else tex.SetPixels32(pixels);
                tex.Apply(false);
            }
            if (tex != null)
            {
                Material premul = null; // Minecraft un-premultiplies its frame before sending it: plain alpha is right now
                // White vertex colour: the fixed-function pass ignores it, the particle shader
                // multiplies by it (and by its alpha), so white keeps the frame untouched.
                if (premul != null)
                {
                    // The particle shader takes The Forest's fog: switch fog off for the overlay,
                    // or the whole HUD comes out veiled.
                    bool fog = RenderSettings.fog;
                    RenderSettings.fog = false;
                    Graphics.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), tex, new Rect(0f, 0f, 1f, 1f), 0, 0, 0, 0, Color.white, premul);
                    RenderSettings.fog = fog;
                }
                else GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), tex, ScaleMode.StretchToFill, true);
            }
            McScreen.DrawCursor();
        }
    }
}
