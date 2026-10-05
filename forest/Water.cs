using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace ForestCraft
{
    // The Forest's lakes, rivers, ponds and sea are trigger volumes tagged "Water" (the surface
    // is the top of the volume, like The Forest's own Buoyancy reads it). Minecraft gets, for each
    // column around the player, the water surface and bottom: it swims, sinks, drowns and fills
    // buckets there like in its own water. In the caves the sea is ignored (as The Forest does).
    // OFF_WATER: seq, ox, oz, size, then size^2 surfaces, size^2 bottoms (Minecraft units, NaN = none).
    static class Water
    {
        const int Off = 0xA34400;
        static readonly List<Collider> volumes = new List<Collider>();
        static readonly List<Collider> near = new List<Collider>();
        static readonly float[] surfaces = new float[Link.Grid * Link.Grid];
        static readonly float[] bottoms = new float[Link.Grid * Link.Grid];
        static float nextScan, nextPublish;
        static int lastOx = int.MinValue, lastOz = int.MinValue, lastCaves = -1;
        static int lastCount = -1;

        public static void Update(int ox, int oz)
        {
            System.IntPtr view = Link.View;
            if (view == System.IntPtr.Zero) return;
            float now = Time.realtimeSinceStartup;
            if (now >= nextScan)
            {
                // Every water trigger around the window (tag "Water", as The Forest's Buoyancy
                // checks it, or the Water layer), found by the physics engine itself.
                nextScan = now + 2f;
                volumes.Clear();
                float kk = Link.Scale;
                Vector3 center = new Vector3((ox + Link.Grid * 0.5f) * kk, 0f, -(oz + Link.Grid * 0.5f) * kk);
                Vector3 half = new Vector3(Link.Grid * 0.5f * kk + 4f, 2000f, Link.Grid * 0.5f * kk + 4f);
                Collider[] found = Physics.OverlapBox(center, half, Quaternion.identity, ~0, QueryTriggerInteraction.Collide);
                for (int i = 0; i < found.Length; i++)
                {
                    Collider c = found[i];
                    if (c == null || !c.enabled) continue;
                    bool water = c.gameObject.layer == 4;
                    try { water = water || c.CompareTag("Water"); } catch { }
                    if (water && !volumes.Contains(c)) volumes.Add(c);
                }
                if (volumes.Count != lastCount) { lastCount = volumes.Count; Plugin.Log.LogInfo("ForestCraft: " + volumes.Count + " water volumes around"); }
            }
            int caves = Caves.Flags & 1;
            if (ox == lastOx && oz == lastOz && caves == lastCaves && now < nextPublish) return;
            lastOx = ox; lastOz = oz; lastCaves = caves;
            nextPublish = now + 0.5f;
            float k = Link.Scale;
            // Only the volumes over this window.
            float wx0 = ox * k, wx1 = (ox + Link.Grid) * k, wz0 = -(oz + Link.Grid) * k, wz1 = -oz * k;
            near.Clear();
            for (int i = 0; i < volumes.Count; i++)
            {
                Collider c = volumes[i];
                if (c == null || !c.enabled || !c.gameObject.activeInHierarchy) continue;
                if (caves != 0 && c.GetComponent("IsOcean") != null) continue;
                Bounds b = c.bounds;
                if (b.max.x < wx0 || b.min.x > wx1 || b.max.z < wz0 || b.min.z > wz1) continue;
                near.Add(c);
            }
            for (int gz = 0; gz < Link.Grid; gz++)
            {
                for (int gx = 0; gx < Link.Grid; gx++)
                {
                    float ux = (ox + gx + 0.5f) * k, uz = -(oz + gz + 0.5f) * k;
                    float top = float.NaN, bottom = float.NaN;
                    for (int i = 0; i < near.Count; i++)
                    {
                        Bounds b = near[i].bounds;
                        if (ux < b.min.x || ux > b.max.x || uz < b.min.z || uz > b.max.z) continue;
                        if (float.IsNaN(top) || b.max.y > top * k) { top = b.max.y / k; bottom = b.min.y / k; }
                    }
                    surfaces[gz * Link.Grid + gx] = top;
                    bottoms[gz * Link.Grid + gx] = bottom;
                }
            }
            int seq = Marshal.ReadInt32(view, Off) + 1;
            if ((seq & 1) == 0) seq++;
            Marshal.WriteInt32(view, Off, seq);
            Marshal.WriteInt32(view, Off + 4, ox);
            Marshal.WriteInt32(view, Off + 8, oz);
            Marshal.WriteInt32(view, Off + 12, Link.Grid);
            Marshal.Copy(surfaces, 0, new System.IntPtr(view.ToInt64() + Off + 16), surfaces.Length);
            Marshal.Copy(bottoms, 0, new System.IntPtr(view.ToInt64() + Off + 16 + surfaces.Length * 4), bottoms.Length);
            Marshal.WriteInt32(view, Off, seq + 1);
        }
    }
}
