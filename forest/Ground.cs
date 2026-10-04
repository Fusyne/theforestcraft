using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace ForestCraft
{
    // One of The Forest's terrains, as it was before any digging. Every height Minecraft or the
    // dig geometry needs comes from this snapshot, never from the (lowered) visible terrain, so
    // collisions, walls and the ground cap all agree with each other.
    sealed class GroundTerrain
    {
        public Terrain terrain;
        public TerrainData data;
        public int id;
        public Vector3 pos, size;
        public int res;
        public float stepX, stepZ;
        public float[,] orig; // normalized heights, [z, x]
        // Samples pushed down under dug cells (index z * res + x -> normalized height).
        public readonly Dictionary<int, float> lowered = new Dictionary<int, float>();
        public int dirtyI0 = int.MaxValue, dirtyI1 = -1, dirtyJ0 = int.MaxValue, dirtyJ1 = -1;
        public Material[] capMaterials;
        public MaterialPropertyBlock capProps;
        public bool capChecked;
        public bool lodPending;

        public bool Contains(float ux, float uz)
        {
            return ux >= pos.x && uz >= pos.z && ux <= pos.x + size.x && uz <= pos.z + size.z;
        }

        public float Sample(int i, int j)
        {
            if (i < 0) i = 0; else if (i >= res) i = res - 1;
            if (j < 0) j = 0; else if (j >= res) j = res - 1;
            return pos.y + orig[j, i] * size.y;
        }

        // Unity units in, Unity units out. Each heightmap quad is two triangles split along
        // (i, j)-(i+1, j+1); walls, caps and Minecraft's ground all use this same surface.
        public float Height(float ux, float uz)
        {
            float fx = (ux - pos.x) / stepX, fz = (uz - pos.z) / stepZ;
            int i = Mathf.Clamp(Mathf.FloorToInt(fx), 0, res - 2);
            int j = Mathf.Clamp(Mathf.FloorToInt(fz), 0, res - 2);
            float dx = Mathf.Clamp01(fx - i), dz = Mathf.Clamp01(fz - j);
            float h00 = orig[j, i], h10 = orig[j, i + 1], h01 = orig[j + 1, i], h11 = orig[j + 1, i + 1];
            float h = dx >= dz ? h00 + dx * (h10 - h00) + dz * (h11 - h10) : h00 + dz * (h01 - h00) + dx * (h11 - h01);
            return pos.y + h * size.y;
        }

        Vector3 SampleNormal(int i, int j)
        {
            float l = Sample(i - 1, j), r = Sample(i + 1, j), d = Sample(i, j - 1), u = Sample(i, j + 1);
            return new Vector3((l - r) / (2f * stepX), 1f, (d - u) / (2f * stepZ)).normalized;
        }

        public Vector3 Normal(float ux, float uz)
        {
            float fx = (ux - pos.x) / stepX, fz = (uz - pos.z) / stepZ;
            int i = Mathf.Clamp(Mathf.FloorToInt(fx), 0, res - 2);
            int j = Mathf.Clamp(Mathf.FloorToInt(fz), 0, res - 2);
            float dx = Mathf.Clamp01(fx - i), dz = Mathf.Clamp01(fz - j);
            Vector3 n = Vector3.Lerp(Vector3.Lerp(SampleNormal(i, j), SampleNormal(i + 1, j), dx),
                                     Vector3.Lerp(SampleNormal(i, j + 1), SampleNormal(i + 1, j + 1), dx), dz);
            return n.normalized;
        }

        public bool IsLowered(int i, int j)
        {
            return i >= 0 && j >= 0 && i < res && j < res && lowered.ContainsKey(j * res + i);
        }

        // A heightmap quad any corner of which was pushed down: the visible terrain there is no
        // longer the island's surface, the ground cap draws it instead.
        public bool InZone(int qi, int qj)
        {
            return IsLowered(qi, qj) || IsLowered(qi + 1, qj) || IsLowered(qi, qj + 1) || IsLowered(qi + 1, qj + 1);
        }
    }

    static class Ground
    {
        public static readonly List<GroundTerrain> Terrains = new List<GroundTerrain>();
        static float nextScan;

        public static bool Ready { get { return Terrains.Count > 0; } }

        // New terrains (a save was loaded) are snapshotted; gone ones forgotten.
        public static void Update()
        {
            for (int t = Terrains.Count - 1; t >= 0; t--)
                if (Terrains[t].terrain == null) { Terrains.RemoveAt(t); DigWorld.TerrainsChanged(); }
            if (Time.realtimeSinceStartup < nextScan && Terrains.Count > 0) return;
            nextScan = Time.realtimeSinceStartup + 2f;
            Terrain[] active = Terrain.activeTerrains;
            if (active == null) return;
            for (int t = 0; t < active.Length; t++)
            {
                Terrain terrain = active[t];
                if (terrain == null || terrain.terrainData == null) continue;
                int id = terrain.GetInstanceID();
                bool known = false;
                for (int k = 0; k < Terrains.Count; k++) if (Terrains[k].id == id) { known = true; break; }
                if (known) continue;
                Add(terrain);
            }
        }

        static void Add(Terrain terrain)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            TerrainData data = terrain.terrainData;
            var g = new GroundTerrain();
            g.terrain = terrain;
            g.data = data;
            g.id = terrain.GetInstanceID();
            g.pos = terrain.transform.position;
            g.size = data.size;
            g.res = data.heightmapResolution;
            g.stepX = g.size.x / (g.res - 1);
            g.stepZ = g.size.z / (g.res - 1);
            g.orig = data.GetHeights(0, 0, g.res, g.res);
            // Every SetHeights also rebuilds the TerrainCollider (PhysX re-cooks the whole island).
            // The Forest's physics doesn't need the holes (Minecraft owns the body), so the
            // collider keeps its own copy of the original heights and only the visible terrain sinks.
            TerrainCollider collider = terrain.GetComponent<TerrainCollider>();
            if (collider != null && collider.terrainData == data)
            {
                var copy = new TerrainData();
                copy.heightmapResolution = g.res;
                copy.size = g.size;
                copy.SetHeights(0, 0, g.orig);
                collider.terrainData = copy;
            }
            Terrains.Add(g);
            Plugin.Log.LogInfo("ForestCraft: ground " + terrain.name + " " + g.res + " samples, spacing " + g.stepX + " units = "
                + (g.stepX / Link.Scale) + " blocks, snapshot " + watch.ElapsedMilliseconds + " ms");
            LogMaterial(g);
            DigWorld.TerrainsChanged();
        }

        static void LogMaterial(GroundTerrain g)
        {
            try
            {
                Material m = g.terrain.materialTemplate;
                string lod = "";
                var manager = UnityEngine.Object.FindObjectOfType<RTP_LODmanager>();
                if (manager != null) lod = ", RTP layers " + manager.numLayers + (manager.RTP_4LAYERS_MODE ? " (4 layers mode)" : "") + (manager.SHADER_USAGE_AddPass ? ", add pass" : "");
                Plugin.Log.LogInfo("ForestCraft: terrain material " + (m != null ? m.name + " / " + m.shader.name : "none") + ", "
                    + g.data.splatPrototypes.Length + " splats" + lod);
            }
            catch (Exception e)
            {
                Plugin.Log.LogInfo("ForestCraft: terrain material unknown: " + e.Message);
            }
        }

        public static GroundTerrain At(float ux, float uz)
        {
            GroundTerrain best = null;
            float top = float.NegativeInfinity;
            for (int t = 0; t < Terrains.Count; t++)
            {
                GroundTerrain g = Terrains[t];
                if (!g.Contains(ux, uz)) continue;
                float h = g.Height(ux, uz);
                if (h > top) { top = h; best = g; }
            }
            return best;
        }

        // The original surface (Unity units), NaN off the island.
        public static float Height(float ux, float uz)
        {
            GroundTerrain g = At(ux, uz);
            return g == null ? float.NaN : g.Height(ux, uz);
        }

        // Same in Minecraft block coordinates.
        public static float HeightMc(float x, float z)
        {
            float k = Link.Scale;
            float h = Height(x * k, -z * k);
            return float.IsNaN(h) ? float.NaN : h / k;
        }

        // Parameters (0..1) where the Minecraft segment (x0,z0)-(x1,z1) crosses the heightmap's
        // triangle edges: between two of them the surface along it is a straight line.
        public static void Breaks(GroundTerrain g, float x0, float z0, float x1, float z1, List<float> ts)
        {
            ts.Clear();
            ts.Add(0f);
            float k = Link.Scale;
            float fx0 = (x0 * k - g.pos.x) / g.stepX, fx1 = (x1 * k - g.pos.x) / g.stepX;
            float fz0 = (-z0 * k - g.pos.z) / g.stepZ, fz1 = (-z1 * k - g.pos.z) / g.stepZ;
            AddCrossings(fx0, fx1, ts);
            AddCrossings(fz0, fz1, ts);
            AddCrossings(fx0 - fz0, fx1 - fz1, ts);
            ts.Add(1f);
            ts.Sort();
        }

        static void AddCrossings(float a, float b, List<float> ts)
        {
            if (Mathf.Abs(b - a) < 1e-6f) return;
            float lo = Mathf.Min(a, b), hi = Mathf.Max(a, b);
            for (int n = Mathf.CeilToInt(lo); n <= Mathf.FloorToInt(hi); n++)
            {
                float t = (n - a) / (b - a);
                if (t > 1e-4f && t < 1f - 1e-4f) ts.Add(t);
            }
        }

        static readonly List<float> breaks = new List<float>();

        // Lowest and highest point of the original surface over the column's footprint
        // (Minecraft units). Exact: a piecewise-linear surface peaks at its vertices or edges.
        static readonly Dictionary<long, Vector2> extremes = new Dictionary<long, Vector2>();
        static float extremesScale;
        static int extremesTerrains;

        public static bool MinMaxMc(int x, int z, out float min, out float max)
        {
            if (extremesScale != Link.Scale || extremesTerrains != Terrains.Count)
            {
                extremes.Clear();
                extremesScale = Link.Scale;
                extremesTerrains = Terrains.Count;
            }
            long key = ((long)x << 32) | (uint)z;
            Vector2 e;
            if (extremes.TryGetValue(key, out e)) { min = e.x; max = e.y; return !float.IsNaN(min); }
            bool ok = MinMaxExact(x, z, out min, out max);
            if (extremes.Count > 200000) extremes.Clear();
            extremes[key] = new Vector2(min, max);
            return ok;
        }

        static bool MinMaxExact(int x, int z, out float min, out float max)
        {
            min = float.PositiveInfinity;
            max = float.NegativeInfinity;
            float k = Link.Scale;
            GroundTerrain g = At((x + 0.5f) * k, -(z + 0.5f) * k);
            if (g == null) { min = max = float.NaN; return false; }
            Edge(g, x, z, x + 1, z, ref min, ref max);
            Edge(g, x + 1, z, x + 1, z + 1, ref min, ref max);
            Edge(g, x + 1, z + 1, x, z + 1, ref min, ref max);
            Edge(g, x, z + 1, x, z, ref min, ref max);
            float a0 = (x * k - g.pos.x) / g.stepX, a1 = ((x + 1) * k - g.pos.x) / g.stepX;
            float b0 = (-(z + 1) * k - g.pos.z) / g.stepZ, b1 = (-z * k - g.pos.z) / g.stepZ;
            for (int j = Mathf.CeilToInt(b0); j <= Mathf.FloorToInt(b1); j++)
                for (int i = Mathf.CeilToInt(a0); i <= Mathf.FloorToInt(a1); i++)
                {
                    float h = g.Sample(i, j) / k;
                    if (h < min) min = h;
                    if (h > max) max = h;
                }
            return true;
        }

        static void Edge(GroundTerrain g, float x0, float z0, float x1, float z1, ref float min, ref float max)
        {
            float k = Link.Scale;
            Breaks(g, x0, z0, x1, z1, breaks);
            for (int n = 0; n < breaks.Count; n++)
            {
                float t = breaks[n];
                float h = g.Height((x0 + (x1 - x0) * t) * k, -(z0 + (z1 - z0) * t) * k) / k;
                if (h < min) min = h;
                if (h > max) max = h;
            }
        }

        // ---- lowering the visible terrain under dug cells ----

        // Every sample that is a corner of a heightmap quad overlapping the cell's footprint goes
        // just under the cell's floor: nothing of the visible terrain is left inside the cell.
        public static void LowerUnder(GroundTerrain g, int x, int y, int z)
        {
            float k = Link.Scale;
            float a0 = (x * k - g.pos.x) / g.stepX, a1 = ((x + 1) * k - g.pos.x) / g.stepX;
            float b0 = (-(z + 1) * k - g.pos.z) / g.stepZ, b1 = (-z * k - g.pos.z) / g.stepZ;
            int i0 = Mathf.Clamp(Mathf.FloorToInt(a0), 0, g.res - 1), i1 = Mathf.Clamp(Mathf.CeilToInt(a1), 0, g.res - 1);
            int j0 = Mathf.Clamp(Mathf.FloorToInt(b0), 0, g.res - 1), j1 = Mathf.Clamp(Mathf.CeilToInt(b1), 0, g.res - 1);
            if (a1 < 0 || b1 < 0 || a0 > g.res - 1 || b0 > g.res - 1) return;
            float target = Mathf.Max(0f, (y * k - 0.05f - g.pos.y) / g.size.y);
            for (int j = j0; j <= j1; j++)
            {
                for (int i = i0; i <= i1; i++)
                {
                    int idx = j * g.res + i;
                    float cur;
                    if (!g.lowered.TryGetValue(idx, out cur)) cur = g.orig[j, i];
                    if (target >= cur) continue;
                    g.lowered[idx] = target;
                    if (i < g.dirtyI0) g.dirtyI0 = i;
                    if (i > g.dirtyI1) g.dirtyI1 = i;
                    if (j < g.dirtyJ0) g.dirtyJ0 = j;
                    if (j > g.dirtyJ1) g.dirtyJ1 = j;
                }
            }
        }

        static float lastLowered;

        // One SetHeights per terrain per frame, over everything lowered since the last one.
        public static void Flush()
        {
            if (Time.realtimeSinceStartup - lastLowered > 2.5f)
            {
                for (int t = 0; t < Terrains.Count; t++)
                {
                    GroundTerrain p = Terrains[t];
                    if (!p.lodPending || p.terrain == null) continue;
                    p.lodPending = false;
                    var lod = System.Diagnostics.Stopwatch.StartNew();
                    p.terrain.ApplyDelayedHeightmapModification();
                    Plugin.Log.LogInfo("ForestCraft: terrain LOD caught up in " + lod.ElapsedMilliseconds + " ms");
                }
            }
            for (int t = 0; t < Terrains.Count; t++)
            {
                GroundTerrain g = Terrains[t];
                if (g.dirtyI1 < g.dirtyI0 || g.dirtyJ1 < g.dirtyJ0 || g.terrain == null) continue;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                int w = g.dirtyI1 - g.dirtyI0 + 1, h = g.dirtyJ1 - g.dirtyJ0 + 1;
                var heights = new float[h, w];
                for (int j = 0; j < h; j++)
                {
                    for (int i = 0; i < w; i++)
                    {
                        int si = g.dirtyI0 + i, sj = g.dirtyJ0 + j;
                        float v;
                        heights[j, i] = g.lowered.TryGetValue(sj * g.res + si, out v) ? v : g.orig[sj, si];
                    }
                }
                // SetHeights costs ~10 ms however small the patch (Unity redoes the whole terrain's
                // LOD and trees): that was the hitch on every dig. The heights go in right away
                // without it; the LOD is caught up once the digging stops for a moment.
                g.data.SetHeightsDelayLOD(g.dirtyI0, g.dirtyJ0, heights);
                g.lodPending = true;
                lastLowered = Time.realtimeSinceStartup;
                if (watch.ElapsedMilliseconds > 8) Plugin.Log.LogInfo("ForestCraft: terrain lowered " + w + "x" + h + " samples in " + watch.ElapsedMilliseconds + " ms");
                g.dirtyI0 = g.dirtyJ0 = int.MaxValue;
                g.dirtyI1 = g.dirtyJ1 = -1;
            }
        }
    }
}
