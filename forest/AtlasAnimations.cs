using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace ForestCraft
{
    // Water, lava, fire, portals, sea lanterns...: the animated sprites of Minecraft's blocks
    // atlas. Minecraft re-uploads their frames into its own atlas every tick; our copy of the
    // atlas was frozen on the first frame. Minecraft exports each one's frames and timing once
    // (atlas_anim.bin, next to atlas.bin) and we play them here: each frame change is a small
    // GPU copy (Graphics.CopyTexture) of the frame into its place in the atlas, so everything
    // using the atlas (placed blocks, water, a burning mob's fire) moves with no re-upload.
    static class AtlasAnimations
    {
        class Anim
        {
            public Texture2D frames;
            public int dstX, dstY, w, h, row, imageH;
            public int[] index, time;
            public int total, shown = -1;
        }

        static readonly List<Anim> anims = new List<Anim>();
        static Texture2D atlas;
        static bool gpuCopy;
        static bool dirty;
        static float nextCpuApply;

        public static void Load(Texture2D target)
        {
            foreach (var a in anims) if (a.frames != null) UnityEngine.Object.Destroy(a.frames);
            anims.Clear();
            atlas = target;
            if (atlas == null) return;
            gpuCopy = SystemInfo.copyTextureSupport != UnityEngine.Rendering.CopyTextureSupport.None;
            string path = Path.Combine(Path.GetDirectoryName(Link.FilePath), "atlas_anim.bin");
            try
            {
                if (!File.Exists(path)) { Plugin.Log.LogInfo("ForestCraft: no animated block textures yet"); return; }
                byte[] all = File.ReadAllBytes(path);
                int at = 0;
                int count = BitConverter.ToInt32(all, at); at += 4;
                int W = atlas.width, H = atlas.height;
                for (int n = 0; n < count && at < all.Length; n++)
                {
                    float u0 = BitConverter.ToSingle(all, at), v0 = BitConverter.ToSingle(all, at + 4);
                    int w = BitConverter.ToInt32(all, at + 16), h = BitConverter.ToInt32(all, at + 20);
                    int iw = BitConverter.ToInt32(all, at + 24), ih = BitConverter.ToInt32(all, at + 28);
                    int row = BitConverter.ToInt32(all, at + 32), frameCount = BitConverter.ToInt32(all, at + 36);
                    at += 40;
                    var a = new Anim { w = w, h = h, row = row, imageH = ih, index = new int[frameCount], time = new int[frameCount] };
                    for (int i = 0; i < frameCount; i++)
                    {
                        a.index[i] = BitConverter.ToInt32(all, at);
                        a.time[i] = Math.Max(1, BitConverter.ToInt32(all, at + 4));
                        a.total += a.time[i];
                        at += 8;
                    }
                    int bytes = iw * ih * 4;
                    if (w <= 0 || h <= 0 || iw < w || ih < h || at + bytes > all.Length) break;
                    // Rows flipped like the atlas: Minecraft's first row is the top of the image.
                    byte[] flipped = new byte[bytes];
                    int stride = iw * 4;
                    for (int y = 0; y < ih; y++) Buffer.BlockCopy(all, at + y * stride, flipped, (ih - 1 - y) * stride, stride);
                    at += bytes;
                    a.dstX = Mathf.RoundToInt(u0 * W);
                    a.dstY = H - Mathf.RoundToInt(v0 * H) - h;
                    if (a.dstX < 0 || a.dstY < 0 || a.dstX + w > W || a.dstY + h > H) continue;
                    a.frames = new Texture2D(iw, ih, TextureFormat.RGBA32, false);
                    a.frames.filterMode = FilterMode.Point;
                    a.frames.wrapMode = TextureWrapMode.Clamp;
                    a.frames.LoadRawTextureData(flipped);
                    a.frames.Apply(false);
                    anims.Add(a);
                }
                Plugin.Log.LogInfo("ForestCraft: " + anims.Count + " animated block textures (" + (gpuCopy ? "GPU copy" : "CPU") + ")");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("ForestCraft: animated block textures: " + e.Message);
                anims.Clear();
            }
        }

        public static void Update()
        {
            if (atlas == null || anims.Count == 0) return;
            int tick = (int)(Time.time * 20f); // Minecraft's frame times are in ticks
            for (int n = 0; n < anims.Count; n++)
            {
                Anim a = anims[n];
                if (a.total <= 0 || a.frames == null) continue;
                int t = tick % a.total, i = 0;
                while (i < a.time.Length - 1 && t >= a.time[i]) { t -= a.time[i]; i++; }
                int idx = a.index[i];
                if (idx == a.shown) continue;
                a.shown = idx;
                int srcX = (idx % a.row) * a.w;
                int srcY = a.imageH - (idx / a.row) * a.h - a.h;
                if (srcX < 0 || srcY < 0 || srcX + a.w > a.frames.width) continue;
                try
                {
                    if (gpuCopy) Graphics.CopyTexture(a.frames, 0, 0, srcX, srcY, a.w, a.h, atlas, 0, 0, a.dstX, a.dstY);
                    else { atlas.SetPixels(a.dstX, a.dstY, a.w, a.h, a.frames.GetPixels(srcX, srcY, a.w, a.h)); dirty = true; }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning("ForestCraft: animated texture copy failed, falling back: " + e.Message);
                    gpuCopy = false;
                }
            }
            // Without GPU copies, the whole atlas is re-uploaded: at most 10 times a second.
            if (dirty && Time.realtimeSinceStartup >= nextCpuApply)
            {
                dirty = false;
                nextCpuApply = Time.realtimeSinceStartup + 0.1f;
                atlas.Apply(false);
            }
        }
    }
}
