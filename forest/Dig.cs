using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using TheForest.Utils;

namespace ForestCraft
{
    // Digging the island one block at a time. Minecraft asks for a cell (x, y, z): The Forest
    // lowers the heightmap samples under that cell's footprint (the nearest one if the cell is
    // smaller than the sample spacing) down to the cell's floor. Minecraft then rebuilds, with
    // blocks, only the neighbour columns whose ground actually sank. Every dug cell goes to
    // dig.txt and is replayed on the next launch (TerrainData resets when the game starts).
    static class Dig
    {
        const int OffDig = 0xA20000;
        const int Ring = 1024;
        const int Entry = 16;

        static int applied;
        static bool replayed, spacingLogged;

        static string SavePath
        {
            get { return Path.Combine(Path.GetDirectoryName(Link.FilePath), "dig.txt"); }
        }

        public static void Reset(IntPtr view)
        {
            Marshal.WriteInt32(view, OffDig, 0);
            applied = 0;
        }

        public static void Update(IntPtr view)
        {
            if (view == IntPtr.Zero) return;
            Terrain[] terrains = Terrain.activeTerrains;
            if (terrains == null || terrains.Length == 0) return;
            DetachColliders(terrains);
            if (!replayed) Replay();
            int written = Marshal.ReadInt32(view, OffDig);
            if (written - applied > Ring) applied = written - Ring;
            while (applied < written)
            {
                int at = OffDig + 64 + (applied % Ring) * Entry;
                int x = Marshal.ReadInt32(view, at);
                int y = Marshal.ReadInt32(view, at + 4);
                int z = Marshal.ReadInt32(view, at + 8);
                applied++;
                LowerCell(x, y, z, Link.Scale);
                try
                {
                    File.AppendAllText(SavePath, "c " + x + " " + y + " " + z + " " + Link.Scale.ToString(CultureInfo.InvariantCulture) + "\n");
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning("ForestCraft: dig not saved: " + e.Message);
                }
            }
        }

        // Every SetHeights also rebuilds the TerrainCollider: PhysX re-cooks the whole island
        // (2049 x 2049 samples), hence the hitch on each dig. The Forest's physics doesn't need
        // the holes (Minecraft owns the body and collides with the sampled heights), so the
        // collider gets its own copy of the original heights, once, and the visible terrain is
        // the only thing lowered afterwards.
        static readonly HashSet<int> detached = new HashSet<int>();

        static void DetachColliders(Terrain[] terrains)
        {
            for (int t = 0; t < terrains.Length; t++)
            {
                Terrain terrain = terrains[t];
                if (terrain == null || terrain.terrainData == null || detached.Contains(terrain.GetInstanceID())) continue;
                detached.Add(terrain.GetInstanceID());
                TerrainCollider collider = terrain.GetComponent<TerrainCollider>();
                if (collider == null || collider.terrainData != terrain.terrainData) continue;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                TerrainData src = terrain.terrainData;
                int res = src.heightmapResolution;
                var copy = new TerrainData();
                copy.heightmapResolution = res;
                copy.size = src.size;
                copy.SetHeights(0, 0, src.GetHeights(0, 0, res, res));
                collider.terrainData = copy;
                Plugin.Log.LogInfo("ForestCraft: terrain collider detached from the visible terrain (" + watch.ElapsedMilliseconds + " ms)");
            }
        }

        static void LowerCell(int x, int y, int z, float k)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            LowerCellNow(x, y, z, k);
            if (watch.ElapsedMilliseconds > 15) Plugin.Log.LogInfo("ForestCraft: dig took " + watch.ElapsedMilliseconds + " ms in The Forest");
        }

        static void LowerCellNow(int x, int y, int z, float k)
        {
            Terrain[] terrains = Terrain.activeTerrains;
            for (int t = 0; t < terrains.Length; t++)
            {
                Terrain terrain = terrains[t];
                if (terrain == null || terrain.terrainData == null) continue;
                TerrainData data = terrain.terrainData;
                Vector3 pos = terrain.transform.position;
                Vector3 size = data.size;
                int res = data.heightmapResolution;
                float stepX = size.x / (res - 1);
                float stepZ = size.z / (res - 1);
                if (!spacingLogged)
                {
                    spacingLogged = true;
                    Plugin.Log.LogInfo("ForestCraft: terrain heightmap " + res + " samples, spacing " + stepX + " units = " + (stepX / k) + " blocks");
                }
                // Every heightmap sample that shapes the terrain inside the dug cell: the cell's
                // footprint plus one sample spacing. Lowering them leaves the cell flat and empty;
                // the dip around it is hidden by the ring of columns Minecraft rebuilds as walls.
                float ux0 = x * k - stepX, ux1 = (x + 1) * k + stepX;
                float uz0 = -(z + 1) * k - stepZ, uz1 = -z * k + stepZ;
                if (ux1 < pos.x || ux0 > pos.x + size.x || uz1 < pos.z || uz0 > pos.z + size.z) continue;
                int i0 = Mathf.Clamp(Mathf.CeilToInt((ux0 - pos.x) / stepX), 0, res - 1);
                int i1 = Mathf.Clamp(Mathf.FloorToInt((ux1 - pos.x) / stepX), 0, res - 1);
                int j0 = Mathf.Clamp(Mathf.CeilToInt((uz0 - pos.z) / stepZ), 0, res - 1);
                int j1 = Mathf.Clamp(Mathf.FloorToInt((uz1 - pos.z) / stepZ), 0, res - 1);
                if (i1 < i0 || j1 < j0) continue;
                int cw = i1 - i0 + 1, ch = j1 - j0 + 1;
                float[,] heights = data.GetHeights(i0, j0, cw, ch);
                float target = Mathf.Max(0f, (y * k - 0.03f - pos.y) / size.y);
                bool changed = false;
                for (int j = 0; j < ch; j++)
                    for (int i = 0; i < cw; i++)
                        if (target < heights[j, i]) { heights[j, i] = target; changed = true; }
                if (changed) data.SetHeights(i0, j0, heights);
            }
        }

        static void Lower(int x0, int z0, int w, int d, float[] bottoms, float k)
        {
            Terrain[] terrains = Terrain.activeTerrains;
            for (int t = 0; t < terrains.Length; t++)
            {
                Terrain terrain = terrains[t];
                if (terrain == null || terrain.terrainData == null) continue;
                TerrainData data = terrain.terrainData;
                Vector3 pos = terrain.transform.position;
                Vector3 size = data.size;
                int res = data.heightmapResolution;
                float stepX = size.x / (res - 1);
                float stepZ = size.z / (res - 1);
                // Patch in Unity units: x in [x0, x0+w) * k, z in (-(z0+d), -z0] * k.
                float ux0 = x0 * k, ux1 = (x0 + w) * k;
                float uz0 = -(z0 + d) * k, uz1 = -z0 * k;
                int i0 = Mathf.Max(0, Mathf.CeilToInt((ux0 - pos.x) / stepX));
                int i1 = Mathf.Min(res - 1, Mathf.FloorToInt((ux1 - pos.x) / stepX));
                int j0 = Mathf.Max(0, Mathf.CeilToInt((uz0 - pos.z) / stepZ));
                int j1 = Mathf.Min(res - 1, Mathf.FloorToInt((uz1 - pos.z) / stepZ));
                if (i1 < i0 || j1 < j0) continue;
                int cw = i1 - i0 + 1, ch = j1 - j0 + 1;
                float[,] heights = data.GetHeights(i0, j0, cw, ch);
                bool changed = false;
                for (int j = 0; j < ch; j++)
                {
                    for (int i = 0; i < cw; i++)
                    {
                        float ux = pos.x + (i0 + i) * stepX;
                        float uz = pos.z + (j0 + j) * stepZ;
                        // The sample and its four neighbours must all be under converted columns.
                        float bottom = float.PositiveInfinity;
                        bool inside = true;
                        for (int n = 0; n < 5 && inside; n++)
                        {
                            float sx = ux + (n == 1 ? stepX : n == 2 ? -stepX : 0f);
                            float sz = uz + (n == 3 ? stepZ : n == 4 ? -stepZ : 0f);
                            int cx = Mathf.FloorToInt(sx / k) - x0;
                            int cz = Mathf.FloorToInt(-sz / k) - z0;
                            if (cx < 0 || cz < 0 || cx >= w || cz >= d) { inside = false; break; }
                            float b = bottoms[cz * w + cx];
                            if (float.IsNaN(b)) { inside = false; break; }
                            bottom = Mathf.Min(bottom, b);
                        }
                        if (!inside) continue;
                        float target = (bottom * k - 0.05f - pos.y) / size.y;
                        if (target < heights[j, i])
                        {
                            heights[j, i] = Mathf.Max(0f, target);
                            changed = true;
                        }
                    }
                }
                if (changed) data.SetHeights(i0, j0, heights);
            }
        }

        static void Save(int x0, int z0, int w, int d, float[] bottoms, float k)
        {
            try
            {
                var line = new System.Text.StringBuilder();
                line.Append(x0).Append(' ').Append(z0).Append(' ').Append(w).Append(' ').Append(d).Append(' ').Append(k.ToString(CultureInfo.InvariantCulture));
                for (int i = 0; i < bottoms.Length; i++) line.Append(' ').Append(bottoms[i].ToString(CultureInfo.InvariantCulture));
                File.AppendAllText(SavePath, line.ToString() + "\n");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("ForestCraft: dig not saved: " + e.Message);
            }
        }

        static void Replay()
        {
            replayed = true;
            if (!File.Exists(SavePath)) return;
            int count = 0;
            try
            {
                foreach (string raw in File.ReadAllLines(SavePath))
                {
                    string[] p = raw.Split(' ');
                    if (p.Length == 5 && p[0] == "c")
                    {
                        LowerCell(int.Parse(p[1]), int.Parse(p[2]), int.Parse(p[3]), float.Parse(p[4], CultureInfo.InvariantCulture));
                        count++;
                        continue;
                    }
                    if (p.Length < 5) continue;
                    int x0 = int.Parse(p[0]), z0 = int.Parse(p[1]), w = int.Parse(p[2]), d = int.Parse(p[3]);
                    float k = float.Parse(p[4], CultureInfo.InvariantCulture);
                    if (p.Length < 5 + w * d) continue;
                    float[] b = new float[w * d];
                    for (int i = 0; i < b.Length; i++) b[i] = float.Parse(p[5 + i], CultureInfo.InvariantCulture);
                    Lower(x0, z0, w, d, b, k);
                    count++;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("ForestCraft: dig replay stopped: " + e.Message);
            }
            if (count > 0) Plugin.Log.LogInfo("ForestCraft: " + count + " dug patches restored");
        }
    }

    // Holding attack on one of The Forest's trees chops it with The Forest's own TreeHealth
    // (one DamageTree per swing). When it falls, Minecraft gets oak logs, like breaking a log.
    static class Trees
    {
        static float nextHit;
        static TreeHealth target;
        static int logSeq;
        static bool warned;
        static string lastName = "?";
        static readonly RaycastHit[] hits = new RaycastHit[16];

        public static void Update(int input)
        {
            bool attack = (input & 128) != 0 && Link.Driving && Link.ReadMcInt(128) == 1;
            if (!attack) { target = null; return; }
            Camera cam = LocalPlayerSafe.Camera();
            if (cam == null) return;
            TreeHealth tree = Aimed(cam.transform.position, cam.transform.forward, 5.5f * Link.Scale);
            if (tree == null)
            {
                if (target != null || !warned) { warned = true; Plugin.Log.LogInfo("ForestCraft: aiming at a Forest object without TreeHealth: " + lastName); }
                target = null;
                return;
            }
            float now = Time.time;
            if (tree != target)
            {
                target = tree;
                nextHit = now + 0.15f;
                Plugin.Log.LogInfo("ForestCraft: chopping " + tree.name + " (health " + tree.Health + ")");
                return;
            }
            if (now < nextHit) return;
            nextHit = now + 0.2f;
            if (tree.Health <= 0) return;
            tree.DamageTree();
            if (tree.Health <= 0)
            {
                logSeq++;
                Link.WriteLogs(logSeq, 4);
                Solids.ForgetAll();
                Plugin.Log.LogInfo("ForestCraft: tree felled: " + tree.name + ", spawned cut tree " + tree.SpawnedCutTree);
                target = null;
            }
        }

        static TreeHealth Aimed(Vector3 from, Vector3 dir, float reach)
        {
            Transform player = LocalPlayer.Transform;
            int n = Physics.RaycastNonAlloc(from, dir, hits, reach, ~0, QueryTriggerInteraction.Collide);
            float best = float.MaxValue;
            TreeHealth found = null;
            for (int i = 0; i < n; i++)
            {
                Collider c = hits[i].collider;
                if (c == null) continue;
                if (player != null && c.transform.IsChildOf(player)) continue;
                if (hits[i].distance < best) lastName = (c.transform.parent != null ? c.transform.parent.name + "/" : "") + c.name;
                TreeHealth th = c.GetComponentInParent<TreeHealth>();
                if (th == null) continue;
                if (hits[i].distance < best) { best = hits[i].distance; found = th; }
            }
            return found;
        }
    }
}
