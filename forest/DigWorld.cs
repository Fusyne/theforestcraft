using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace ForestCraft
{
    // Holes dug into the island, the SkyCraft way. Minecraft decides which cells (blocks) are dug
    // and sends them here (it keeps them with its world and sends them all again on every link).
    // Here, for each dug cell:
    //  - the visible terrain is pushed down under it (a heightmap can't have holes or walls);
    //  - the original surface around is drawn again by a "cap" mesh, cut exactly along the dug
    //    cells, with the terrain's own material (or Minecraft grass);
    //  - where a dug cell borders ground that is still The Forest's (the surface runs through
    //    it), the part of that ground under the surface is drawn as Minecraft dirt/stone faces,
    //    cut along the surface: the "half blocks" of SkyCraft. Ground wholly under the surface
    //    is turned into real Minecraft blocks by Minecraft itself.
    static class DigWorld
    {
        const int OffDig = 0xA20000;
        const int Ring = 1024;
        const int Entry = 16;
        const int OffSprites = 0xA34000;
        const int ChunkSize = 16;

        static readonly HashSet<long> cells = new HashSet<long>();
        static readonly Dictionary<long, List<long>> byChunk = new Dictionary<long, List<long>>();
        static readonly HashSet<long> dirty = new HashSet<long>();
        static readonly Dictionary<long, GameObject> built = new Dictionary<long, GameObject>();
        static GameObject root;
        static int applied;
        static bool relower;
        public static string CapMode = "Terrain";

        // Atlas rects from Minecraft (u0, v0, u1, v1, Minecraft's v: 0 = top of the atlas).
        static readonly float[] dirt = new float[4], stone = new float[4], grass = new float[4], sand = new float[4];
        static bool sandReady;
        static int grassTint = -1;
        static bool spritesReady;

        static long CellKey(int x, int y, int z)
        {
            return ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (long)(z & 0x1FFFFF);
        }

        static void Unpack(long key, out int x, out int y, out int z)
        {
            x = (int)((key >> 42) & 0x1FFFFF); if (x >= 0x100000) x -= 0x200000;
            y = (int)((key >> 21) & 0x1FFFFF); if (y >= 0x100000) y -= 0x200000;
            z = (int)(key & 0x1FFFFF); if (z >= 0x100000) z -= 0x200000;
        }

        static long ChunkKey(int cx, int cz)
        {
            return ((long)cx << 32) | (uint)cz;
        }

        public static bool IsDug(int x, int y, int z)
        {
            return cells.Contains(CellKey(x, y, z));
        }

        public static void Reset(IntPtr view)
        {
            Marshal.WriteInt32(view, OffDig, 0);
            Marshal.WriteInt32(view, OffDig + 4, 0);
            applied = 0;
        }

        // A new game: its holes come from Minecraft's world for it (sent again after the reset).
        public static void ClearAll(IntPtr view)
        {
            cells.Clear();
            byChunk.Clear();
            dirty.Clear();
            for (int i = 0; i < pending.Count; i++) if (pending[i].stand != null) UnityEngine.Object.Destroy(pending[i].stand);
            pending.Clear();
            rebuildNow.Clear();
            if (root != null) UnityEngine.Object.Destroy(root);
            root = null;
            built.Clear();
            for (int t = 0; t < Ground.Terrains.Count; t++)
            {
                GroundTerrain g = Ground.Terrains[t];
                if (g.lowered.Count == 0 || g.terrain == null) continue;
                // Put the visible terrain back as it was (a loaded save reloads it anyway).
                foreach (int idx in g.lowered.Keys)
                {
                    int i = idx % g.res, j = idx / g.res;
                    if (i < g.dirtyI0) g.dirtyI0 = i;
                    if (i > g.dirtyI1) g.dirtyI1 = i;
                    if (j < g.dirtyJ0) g.dirtyJ0 = j;
                    if (j > g.dirtyJ1) g.dirtyJ1 = j;
                }
                g.lowered.Clear();
            }
            Ground.Flush();
            if (view != IntPtr.Zero) Reset(view);
        }

        public static void TerrainsChanged()
        {
            relower = true;
        }

        public static void Update(IntPtr view)
        {
            if (view == IntPtr.Zero || !Ground.Ready) return;
            ReadSprites(view);
            if (relower)
            {
                relower = false;
                foreach (long key in cells)
                {
                    int x, y, z;
                    Unpack(key, out x, out y, out z);
                    for (int t = 0; t < Ground.Terrains.Count; t++) Ground.LowerUnder(Ground.Terrains[t], x, y, z);
                }
                foreach (long chunk in byChunk.Keys) dirty.Add(chunk);
            }
            int written = Marshal.ReadInt32(view, OffDig);
            if (written < applied) applied = 0;
            if (written - applied > Ring) applied = written - Ring;
            int n = 0;
            while (applied < written && n < 512)
            {
                int at = OffDig + 64 + (applied % Ring) * Entry;
                Add(Marshal.ReadInt32(view, at), Marshal.ReadInt32(view, at + 4), Marshal.ReadInt32(view, at + 8), Marshal.ReadInt32(view, at + 12));
                applied++;
                n++;
            }
            Marshal.WriteInt32(view, OffDig + 4, applied);
            OpenPending();
            Ground.Flush();
            if (dirty.Count > 0 && spritesReady) RebuildSome(1);
        }

        // A cell just dug: the island's ground sinks at once and the walls and cap around it are
        // rebuilt in that same frame (they used to come a few frames later). The blocks Minecraft
        // reveals around the hole (dirt, stone) take a few frames more to arrive: until they do,
        // stand-in cubes are drawn in their place, so you never see through the island.
        struct Pending { public GameObject stand; public float since; public long s0, s1, s2; public int t0, t1, t2; }
        static readonly List<Pending> pending = new List<Pending>();
        static readonly List<long> sectionScratch = new List<long>();
        static readonly List<long> rebuildNow = new List<long>();

        // How far (blocks) a dug cell can change the cap and walls around it (the cap reaches 3
        // terrain samples past the hole, a sample being up to ~1.2 block).
        const int Reach = 5;

        static long SectionKey(int x, int y, int z) { return ((long)((x >> 4) & 0x1FFFFF) << 42) | ((long)((y >> 4) & 0x1FFFFF) << 21) | (long)((z >> 4) & 0x1FFFFF); }

        static readonly int[] ndx = { 0, 0, -1, 1, 0, 0 }, ndy = { -1, 1, 0, 0, 0, 0 }, ndz = { 0, 0, 0, 0, -1, 1 };

        // flag: 0 = an old hole (loading a save), 1 = just dug, nothing revealed, 2+ = just dug, blocks revealed.
        static void Add(int x, int y, int z, int flag)
        {
            long key = CellKey(x, y, z);
            if (!cells.Add(key)) return;
            long chunk = ChunkKey(x >> 4, z >> 4);
            List<long> list;
            if (!byChunk.TryGetValue(chunk, out list)) { list = new List<long>(); byChunk[chunk] = list; }
            list.Add(key);
            Lower(x, y, z);
            if (flag <= 0 || flag > 1000) { MarkAround(x, z); return; }
            for (int cz = (z - Reach) >> 4; cz <= (z + Reach) >> 4; cz++)
                for (int cx = (x - Reach) >> 4; cx <= (x + Reach) >> 4; cx++)
                {
                    long c = ChunkKey(cx, cz);
                    if (!rebuildNow.Contains(c)) rebuildNow.Add(c);
                }
            if (flag < 2) return;
            GameObject stand = StandIns(x, y, z);
            if (stand == null) return;
            var p = new Pending { stand = stand, since = Time.realtimeSinceStartup };
            sectionScratch.Clear();
            for (int d = 0; d < 6; d++)
            {
                long sk = SectionKey(x + ndx[d], y + ndy[d], z + ndz[d]);
                if (!sectionScratch.Contains(sk)) sectionScratch.Add(sk);
            }
            p.s0 = sectionScratch.Count > 0 ? sectionScratch[0] : long.MinValue;
            p.s1 = sectionScratch.Count > 1 ? sectionScratch[1] : long.MinValue;
            p.s2 = sectionScratch.Count > 2 ? sectionScratch[2] : long.MinValue;
            p.t0 = StampOf(p.s0); p.t1 = StampOf(p.s1); p.t2 = StampOf(p.s2);
            pending.Add(p);
        }

        // Cubes where Minecraft is placing the revealed blocks (same rule as TerrainDig.reveal).
        static GameObject StandIns(int x, int y, int z)
        {
            if (!spritesReady) return null;
            var mb = new MeshBuild();
            float k = Link.Scale;
            const float e = 0.004f; // a hair inside, so the real block hides it when it arrives
            for (int d = 0; d < 6; d++)
            {
                int nx = x + ndx[d], ny = y + ndy[d], nz = z + ndz[d];
                if (IsDug(nx, ny, nz) || !Revealed(nx, ny, nz)) continue;
                float[] rect = MaterialOf(nx, ny, nz);
                for (int f = 0; f < 6; f++)
                {
                    // Face f of the cube: normal along axis f/2, side f%2.
                    int axis = f / 2; float side = f % 2 == 0 ? -1f : 1f;
                    Vector3 n = axis == 0 ? new Vector3(side, 0, 0) : axis == 1 ? new Vector3(0, side, 0) : new Vector3(0, 0, side);
                    Vector3 c = new Vector3(nx + 0.5f, ny + 0.5f, nz + 0.5f) + n * (0.5f - e);
                    Vector3 u = axis == 0 ? new Vector3(0, 0, 1) : new Vector3(1, 0, 0);
                    Vector3 v = axis == 1 ? new Vector3(0, 0, 1) : new Vector3(0, 1, 0);
                    u *= 0.5f - e; v *= 0.5f - e;
                    int b = mb.v.Count;
                    Vector3[] q = { c - u - v, c + u - v, c + u + v, c - u + v };
                    Vector2[] st = { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
                    Vector3 un = new Vector3(n.x, n.y, -n.z);
                    for (int i = 0; i < 4; i++)
                    {
                        mb.v.Add(new Vector3(q[i].x * k, q[i].y * k, -q[i].z * k));
                        mb.n.Add(un);
                        mb.uv.Add(AtlasUv(rect, st[i].x, st[i].y));
                    }
                    mb.tris.Add(b); mb.tris.Add(b + 1); mb.tris.Add(b + 2); mb.tris.Add(b); mb.tris.Add(b + 2); mb.tris.Add(b + 3);
                    mb.tris.Add(b); mb.tris.Add(b + 2); mb.tris.Add(b + 1); mb.tris.Add(b); mb.tris.Add(b + 3); mb.tris.Add(b + 2);
                }
            }
            if (mb.v.Count == 0) return null;
            var go = new GameObject("ForestCraft stand-in blocks");
            go.AddComponent<MeshFilter>().sharedMesh = mb.ToMesh("stand-in");
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = Blocks.MaterialFor(0xFFFFFFFF);
            return go;
        }

        static int StampOf(long sk)
        {
            if (sk == long.MinValue) return -1;
            int sx = (int)((sk >> 42) & 0x1FFFFF), sy = (int)((sk >> 21) & 0x1FFFFF), sz = (int)(sk & 0x1FFFFF);
            if (sx >= 0x100000) sx -= 0x200000;
            if (sy >= 0x100000) sy -= 0x200000;
            if (sz >= 0x100000) sz -= 0x200000;
            return Blocks.Stamp(sx, sy, sz);
        }

        static bool Changed(long sk, int before) { return sk != long.MinValue && StampOf(sk) != before; }

        static void Lower(int x, int y, int z)
        {
            for (int t = 0; t < Ground.Terrains.Count; t++) Ground.LowerUnder(Ground.Terrains[t], x, y, z);
        }

        // The cap and walls of every column within two blocks may change.
        static void MarkAround(int x, int z)
        {
            for (int cz = (z - Reach) >> 4; cz <= (z + Reach) >> 4; cz++)
                for (int cx = (x - Reach) >> 4; cx <= (x + Reach) >> 4; cx++)
                    dirty.Add(ChunkKey(cx, cz));
        }

        static void OpenPending()
        {
            // Walls and cap of what was just dug: now, in the frame the ground sinks.
            if (spritesReady)
            {
                for (int i = 0; i < rebuildNow.Count; i++)
                {
                    long c = rebuildNow[i];
                    dirty.Remove(c);
                    Build((int)(c >> 32), (int)(c & 0xFFFFFFFF));
                }
            }
            else for (int i = 0; i < rebuildNow.Count; i++) dirty.Add(rebuildNow[i]);
            rebuildNow.Clear();
            // Stand-ins go once Minecraft's blocks are there (their sections were sent again).
            float now = Time.realtimeSinceStartup;
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                Pending p = pending[i];
                float age = now - p.since;
                bool all = (p.s0 == long.MinValue || Changed(p.s0, p.t0)) && (p.s1 == long.MinValue || Changed(p.s1, p.t1)) && (p.s2 == long.MinValue || Changed(p.s2, p.t2));
                bool any = Changed(p.s0, p.t0) || Changed(p.s1, p.t1) || Changed(p.s2, p.t2);
                if (!(all || (any && age > 0.15f) || age > 1f)) continue;
                pending.RemoveAt(i);
                if (p.stand != null)
                {
                    MeshFilter mf = p.stand.GetComponent<MeshFilter>();
                    if (mf != null && mf.sharedMesh != null) UnityEngine.Object.Destroy(mf.sharedMesh);
                    UnityEngine.Object.Destroy(p.stand);
                }
            }
        }

        static void ReadSprites(IntPtr view)
        {
            if (Marshal.ReadInt32(view, OffSprites) == 0) return;
            for (int i = 0; i < 4; i++)
            {
                dirt[i] = Link.ReadFloatAt(OffSprites + 4 + i * 4);
                stone[i] = Link.ReadFloatAt(OffSprites + 20 + i * 4);
                grass[i] = Link.ReadFloatAt(OffSprites + 36 + i * 4);
            }
            grassTint = Marshal.ReadInt32(view, OffSprites + 52);
            // Sand (OFF_SPRITES+56), from Minecraft versions that send it.
            for (int i = 0; i < 4; i++) sand[i] = Link.ReadFloatAt(OffSprites + 56 + i * 4);
            sandReady = sand[2] > sand[0] && sand[3] > sand[1];
            if (!spritesReady) Plugin.Log.LogInfo("ForestCraft: dig textures ready");
            spritesReady = true;
        }

        static void RebuildSome(int max)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            // Nearest chunks first.
            float k = Link.Scale;
            Vector3 feet = Drive.Feet();
            int pcx = Mathf.FloorToInt(feet.x / k) >> 4, pcz = Mathf.FloorToInt(-feet.z / k) >> 4;
            for (int n = 0; n < max && dirty.Count > 0; n++)
            {
                long best = 0;
                long bestD = long.MaxValue;
                foreach (long c in dirty)
                {
                    int cx = (int)(c >> 32), cz = (int)(c & 0xFFFFFFFF);
                    long d = (long)(cx - pcx) * (cx - pcx) + (long)(cz - pcz) * (cz - pcz);
                    if (d < bestD) { bestD = d; best = c; }
                }
                dirty.Remove(best);
                Build((int)(best >> 32), (int)(best & 0xFFFFFFFF));
            }
            if (watch.ElapsedMilliseconds > 10) Plugin.Log.LogInfo("ForestCraft: dig meshes rebuilt in " + watch.ElapsedMilliseconds + " ms");
        }

        // ---------------------------------------------------------------- meshes

        // Reused for every chunk (ToMesh copies them into the mesh): no new lists per rebuild.
        static readonly MeshBuild wallsBuild = new MeshBuild(), capBuild = new MeshBuild();

        sealed class MeshBuild
        {
            public void Clear() { v.Clear(); n.Clear(); uv.Clear(); tan.Clear(); tris.Clear(); }

            public readonly List<Vector3> v = new List<Vector3>();
            public readonly List<Vector3> n = new List<Vector3>();
            public readonly List<Vector2> uv = new List<Vector2>();
            public readonly List<Vector4> tan = new List<Vector4>();
            public readonly List<int> tris = new List<int>();

            public Mesh ToMesh(string name)
            {
                if (tris.Count == 0) return null;
                var mesh = new Mesh();
                mesh.name = name;
                mesh.SetVertices(v);
                mesh.SetNormals(n);
                mesh.SetUVs(0, uv);
                if (tan.Count == v.Count)
                {
                    // Terrain-style vertex: same uv in the second channel, white colour.
                    mesh.SetTangents(tan);
                    mesh.SetUVs(1, uv);
                    var white = new List<Color>(v.Count);
                    for (int i = 0; i < v.Count; i++) white.Add(Color.white);
                    mesh.SetColors(white);
                }
                mesh.SetTriangles(tris, 0);
                mesh.RecalculateBounds();
                return mesh;
            }
        }

        static GameObject Root()
        {
            if (root == null)
            {
                root = new GameObject("ForestCraft Dug Ground");
                built.Clear();
            }
            return root;
        }

        static void Build(int cx, int cz)
        {
            long chunk = ChunkKey(cx, cz);
            GameObject old;
            if (built.TryGetValue(chunk, out old) && old != null)
            {
                foreach (MeshFilter mf in old.GetComponentsInChildren<MeshFilter>()) if (mf.sharedMesh != null) UnityEngine.Object.Destroy(mf.sharedMesh);
                UnityEngine.Object.Destroy(old);
            }
            built.Remove(chunk);
            var go = new GameObject("dug " + cx + "," + cz);
            go.transform.SetParent(Root().transform, false);
            bool any = false;

            var walls = wallsBuild; walls.Clear();
            List<long> list;
            if (byChunk.TryGetValue(chunk, out list)) for (int i = 0; i < list.Count; i++) Walls(list[i], walls);
            Mesh wallMesh = walls.ToMesh("dug walls " + cx + "," + cz);
            if (wallMesh != null)
            {
                var w = new GameObject("walls");
                w.transform.SetParent(go.transform, false);
                w.AddComponent<MeshFilter>().sharedMesh = wallMesh;
                var r = w.AddComponent<MeshRenderer>();
                r.sharedMaterial = Blocks.MaterialFor(0xFFFFFFFFL);
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                r.receiveShadows = true;
                any = true;
            }

            for (int t = 0; t < Ground.Terrains.Count; t++)
            {
                GroundTerrain g = Ground.Terrains[t];
                if (CapMode == "Off") continue;
                Material[] mats = CapMaterials(g);
                var cap = capBuild; cap.Clear();
                Cap(g, cx, cz, cap, mats == null);
                Mesh capMesh = cap.ToMesh("dug cap " + cx + "," + cz);
                if (capMesh == null) continue;
                var c = new GameObject("cap");
                c.transform.SetParent(go.transform, false);
                c.transform.position = mats == null ? Vector3.zero : g.pos;
                if (mats != null) c.layer = g.terrain.gameObject.layer;
                c.AddComponent<MeshFilter>().sharedMesh = capMesh;
                var r = c.AddComponent<MeshRenderer>();
                r.sharedMaterials = mats ?? new[] { Blocks.MaterialFor((uint)grassTint) };
                if (mats != null) r.SetPropertyBlock(TerrainBlock(g));
                // The cap is the island's surface drawn again over the sunk terrain, which still
                // casts its own shadow: a second caster at the same height only darkened the
                // ground around the hole in a band.
                r.shadowCastingMode = mats != null ? UnityEngine.Rendering.ShadowCastingMode.Off : UnityEngine.Rendering.ShadowCastingMode.On;
                r.receiveShadows = true;
                if (mats != null) MatchTerrainLighting(g, r);
                any = true;
            }
            if (!any) { UnityEngine.Object.Destroy(go); return; }
            built[chunk] = go;
        }

        // Unity draws this terrain with its base map shader, handing it every frame a base map it
        // composes itself (all the layers blended, at low resolution) as _MainTex: the colour under
        // the detail. Our copy didn't get it (no script can read Unity's), so the ground around a
        // hole came out pale. Compose an equivalent once: each layer's average colour, blended by
        // the terrain's own splat weights.
        static void MatchBaseMap(GroundTerrain g, Material m)
        {
            var info = new System.Text.StringBuilder("ForestCraft: cap material: keywords ");
            foreach (string kw in m.shaderKeywords) info.Append(kw).Append(' ');
            foreach (string kw in new[] { "SUNSHINE_DISABLED", "SUNSHINE_FILTER_HARD", "SUNSHINE_FILTER_PCF_2x2", "SUNSHINE_FILTER_PCF_3x3", "SUNSHINE_FILTER_PCF_4x4" })
                if (Shader.IsKeywordEnabled(kw)) info.Append("[global ").Append(kw).Append("] ");
            Texture main = m.HasProperty("_MainTex") ? m.GetTexture("_MainTex") : null;
            info.Append("| _MainTex ").Append(m.HasProperty("_MainTex") ? (main != null ? main.name : "empty") : "none");
            // Sunshine (The Forest's shadows) is on for the terrain at run time: not off for the cap.
            if (m.IsKeywordEnabled("SUNSHINE_DISABLED")) { m.DisableKeyword("SUNSHINE_DISABLED"); info.Append(" | Sunshine switched on"); }
            // The base map shader samples _MainTex without declaring it (HasProperty says no): Unity
            // hands it the composed base map on every terrain draw. Give the cap one too.
            if (main == null)
            {
                Texture2D baseMap = ComposeBaseMap(g);
                if (baseMap != null)
                {
                    g.baseMap = baseMap;
                    m.SetTexture("_MainTex", baseMap);
                    info.Append(" | base map composed " + baseMap.width + "x" + baseMap.height);
                }
            }
            Plugin.Log.LogInfo(info.ToString());
        }

        static Texture2D ComposeBaseMap(GroundTerrain g)
        {
            try
            {
                SplatPrototype[] splats = g.data.splatPrototypes;
                int layers = Mathf.Min(g.data.alphamapLayers, splats.Length);
                if (layers == 0) return null;
                var avg = new Color[layers];
                var rt = RenderTexture.GetTemporary(8, 8, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                var read = new Texture2D(8, 8, TextureFormat.RGBA32, false);
                RenderTexture before = RenderTexture.active;
                for (int l = 0; l < layers; l++)
                {
                    Texture2D t = splats[l].texture;
                    if (t == null) { avg[l] = Color.gray; continue; }
                    Graphics.Blit(t, rt);
                    RenderTexture.active = rt;
                    read.ReadPixels(new Rect(0, 0, 8, 8), 0, 0);
                    read.Apply();
                    Color32[] px = read.GetPixels32();
                    float r = 0, gg = 0, b = 0;
                    for (int i = 0; i < px.Length; i++) { r += px[i].r; gg += px[i].g; b += px[i].b; }
                    avg[l] = new Color(r / px.Length / 255f, gg / px.Length / 255f, b / px.Length / 255f, 1f);
                }
                RenderTexture.active = before;
                RenderTexture.ReleaseTemporary(rt);
                UnityEngine.Object.Destroy(read);
                int res = g.data.alphamapResolution;
                var colors = new Color32[res * res];
                const int Strip = 32;
                for (int z0 = 0; z0 < res; z0 += Strip)
                {
                    int rows = Mathf.Min(Strip, res - z0);
                    float[,,] a = g.data.GetAlphamaps(0, z0, res, rows);
                    for (int z = 0; z < rows; z++)
                        for (int x = 0; x < res; x++)
                        {
                            Color c = Color.black;
                            for (int l = 0; l < layers; l++) c += avg[l] * a[z, x, l];
                            colors[(z0 + z) * res + x] = new Color32((byte)Mathf.Clamp(c.r * 255f, 0, 255), (byte)Mathf.Clamp(c.g * 255f, 0, 255), (byte)Mathf.Clamp(c.b * 255f, 0, 255), 255);
                        }
                }
                var tex = new Texture2D(res, res, TextureFormat.RGBA32, true, false);
                tex.SetPixels32(colors);
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.Apply(true, true);
                return tex;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("ForestCraft: base map not composed: " + e.Message);
                return null;
            }
        }

        // What The Forest's scripts hand the terrain's material on every draw (atlas tilings, the
        // sand and layer settings...), read back from the terrain itself: without it the layers
        // past the first ones (the sand among them) came out quite unlike the ground around.
        static readonly MaterialPropertyBlock terrainBlock = new MaterialPropertyBlock();
        static bool blockLogged;
        static MaterialPropertyBlock TerrainBlock(GroundTerrain g)
        {
            terrainBlock.Clear();
            try { g.terrain.GetSplatMaterialPropertyBlock(terrainBlock); } catch (Exception) { }
            if (terrainBlock.isEmpty && g.capProps != null)
            {
                if (g.baseMap != null) g.capProps.SetTexture("_MainTex", g.baseMap);
                return g.capProps;
            }
            if (g.baseMap != null) terrainBlock.SetTexture("_MainTex", g.baseMap);
            if (!blockLogged)
            {
                blockLogged = true;
                Plugin.Log.LogInfo("ForestCraft: terrain's own material settings " + (terrainBlock.isEmpty ? "empty" : "found") + ", used for the dug ground cap");
            }
            return terrainBlock;
        }

        // Lit like the terrain it stands in for: same baked and realtime lightmaps (its UV1 is the
        // terrain's own 0..1 coordinate, as the terrain's lightmap expects), same probes.
        static bool lightLogged;
        static void MatchTerrainLighting(GroundTerrain g, MeshRenderer r)
        {
            Terrain t = g.terrain;
            if (t == null) return;
            try
            {
                if (t.lightmapIndex >= 0 && t.lightmapIndex < 0xFFFE)
                {
                    r.lightmapIndex = t.lightmapIndex;
                    r.lightmapScaleOffset = t.lightmapScaleOffset;
                }
                if (t.realtimeLightmapIndex >= 0 && t.realtimeLightmapIndex < 0xFFFE)
                {
                    r.realtimeLightmapIndex = t.realtimeLightmapIndex;
                    r.realtimeLightmapScaleOffset = t.realtimeLightmapScaleOffset;
                }
                r.reflectionProbeUsage = t.reflectionProbeUsage;
                r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
                if (!lightLogged)
                {
                    lightLogged = true;
                    Plugin.Log.LogInfo("ForestCraft: terrain lightmap " + t.lightmapIndex + " " + t.lightmapScaleOffset + ", realtime " + t.realtimeLightmapIndex
                        + ", probes " + t.reflectionProbeUsage + ": the cap uses the same");
                }
            }
            catch (Exception e)
            {
                if (!lightLogged) { lightLogged = true; Plugin.Log.LogWarning("ForestCraft: cap lighting: " + e.Message); }
            }
        }

        // The terrain's own material (Relief Terrain Pack), so the cap looks like the ground
        // around it. Its add pass too when The Forest draws more layers than the first pass holds.
        // F9 cycles how the ground around holes is drawn (to compare): Terrain, Grass, Off.
        public static void CycleCap()
        {
            CapMode = CapMode == "Terrain" ? "Grass" : CapMode == "Grass" ? "Off" : "Terrain";
            Plugin.Log.LogInfo("ForestCraft: dug ground cap mode " + CapMode);
            foreach (long chunk in byChunk.Keys)
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                        dirty.Add(ChunkKey((int)(chunk >> 32) + dx, (int)(chunk & 0xFFFFFFFF) + dz));
        }

        static Material[] CapMaterials(GroundTerrain g)
        {
            if (CapMode != "Terrain") return null;
            if (g.capChecked) return g.capMaterials;
            g.capChecked = true;
            Material first = g.terrain.materialTemplate;
            if (first == null)
            {
                Plugin.Log.LogInfo("ForestCraft: terrain has no material template, the dug ground cap uses Minecraft grass");
                return null;
            }
            // basemapDistance 0: Unity draws the whole terrain with the material's base map
            // shader, the first pass is never used (The Forest's draws plain red). Use the base one.
            if (g.terrain.basemapDistance < 1f)
            {
                Shader baseShader = null;
                foreach (string n in new[] { first.shader.name + " base", first.shader.name + "-Base", first.shader.name + " Base" })
                {
                    baseShader = Shader.Find(n);
                    if (baseShader != null) break;
                }
                if (baseShader != null)
                {
                    var b = new Material(baseShader);
                    b.CopyPropertiesFromMaterial(first);
                    b.shaderKeywords = first.shaderKeywords;
                    MatchBaseMap(g, b);
                    first = b;
                }
                else Plugin.Log.LogWarning("ForestCraft: terrain base map shader not found for " + first.shader.name);
            }
            var mats = new List<Material> { first };
            try
            {
                var manager = UnityEngine.Object.FindObjectOfType<RTP_LODmanager>();
                int perPass = manager != null && manager.RTP_4LAYERS_MODE ? 4 : 8;
                if (manager != null && manager.SHADER_USAGE_AddPass && g.data.splatPrototypes.Length > perPass)
                {
                    Shader add = Shader.Find("Relief Pack/ReliefTerrain-AddPass");
                    if (add != null)
                    {
                        var m = new Material(add);
                        m.CopyPropertiesFromMaterial(first);
                        mats.Add(m);
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("ForestCraft: RTP add pass not set up: " + e.Message);
            }
            g.capMaterials = mats.ToArray();
            g.capProps = TerrainProps(g, first);
            Plugin.Log.LogInfo("ForestCraft: dug ground cap drawn with " + first.shader.name + (g.capMaterials.Length > 1 ? " + add pass" : ""));
            return g.capMaterials;
        }

        // What Unity's terrain engine hands its material on every draw (splat map, layer
        // textures and tiling): a mesh drawn with the same material doesn't get them by itself,
        // hence the red cap (the default splat map) without this.
        static MaterialPropertyBlock TerrainProps(GroundTerrain g, Material mat)
        {
            var props = new MaterialPropertyBlock();
            try
            {
                Texture2D[] control = g.data.alphamapTextures;
                SplatPrototype[] splats = g.data.splatPrototypes;
                var have = new System.Text.StringBuilder();
                for (int c = 0; c < control.Length && c < 3; c++)
                {
                    string name = c == 0 ? "_Control" : "_Control" + (c + 1);
                    if (mat.HasProperty(name)) props.SetTexture(name, control[c]);
                    if (mat.HasProperty(name)) have.Append(name).Append(' ');
                }
                for (int i = 0; i < splats.Length && i < 12; i++)
                {
                    SplatPrototype sp = splats[i];
                    // Layers 0-3 are what Unity sets; further ones only if the shader has them and
                    // nothing (a script of The Forest) already filled them in.
                    string tex = "_Splat" + i;
                    if (!mat.HasProperty(tex) || (i >= 4 && mat.GetTexture(tex) != null)) continue;
                    if (mat.HasProperty(tex)) have.Append(tex).Append(' ');
                    if (sp.texture != null) props.SetTexture(tex, sp.texture);
                    if (sp.normalMap != null) props.SetTexture("_Normal" + i, sp.normalMap);
                    Vector2 tile = sp.tileSize;
                    if (tile.x <= 0f) tile.x = 1f;
                    if (tile.y <= 0f) tile.y = 1f;
                    props.SetVector(tex + "_ST", new Vector4(g.size.x / tile.x, g.size.z / tile.y, sp.tileOffset.x / tile.x, sp.tileOffset.y / tile.y));
                    props.SetFloat("_Metallic" + i, sp.metallic);
                    props.SetFloat("_Smoothness" + i, sp.smoothness);
                }
                Plugin.Log.LogInfo("ForestCraft: terrain material has " + have + "(" + control.Length + " splat maps, " + splats.Length + " layers)");
                var slots = new System.Text.StringBuilder();
                foreach (string n in new[] { "_Control0", "_Control1", "_Atlas0", "_Atlas1", "_N_Atlas0", "_CustomColorMap", "_Perlin", "_MainTex" })
                {
                    if (!mat.HasProperty(n)) continue;
                    Texture t = mat.GetTexture(n);
                    slots.Append(n).Append('=').Append(t == null ? "null" : t.name + " " + t.width + "x" + t.height).Append(' ');
                }
                Plugin.Log.LogInfo("ForestCraft: terrain material slots " + slots + "| keywords " + string.Join(" ", mat.shaderKeywords)
                    + " | heightmap drawn " + g.terrain.drawHeightmap + ", pixel error " + g.terrain.heightmapPixelError + ", basemap " + g.terrain.basemapDistance);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("ForestCraft: terrain textures for the cap: " + e.Message);
            }
            return props;
        }

        // ---- the cap: the island's original surface over the lowered quads, minus dug cells ----

        static readonly List<Vector3> polyA = new List<Vector3>(16), polyB = new List<Vector3>(16), polyC = new List<Vector3>(16);
        static readonly List<Vector3> tri = new List<Vector3>(3);

        static void Cap(GroundTerrain g, int cx, int cz, MeshBuild mb, bool grassStyle)
        {
            float k = Link.Scale;
            float ux0 = cx * ChunkSize * k, ux1 = (cx + 1) * ChunkSize * k;
            float uz0 = -(cz + 1) * ChunkSize * k, uz1 = -cz * ChunkSize * k;
            int qi0 = Mathf.Max(0, Mathf.FloorToInt((ux0 - g.pos.x) / g.stepX));
            int qi1 = Mathf.Min(g.res - 2, Mathf.FloorToInt((ux1 - g.pos.x) / g.stepX));
            int qj0 = Mathf.Max(0, Mathf.FloorToInt((uz0 - g.pos.z) / g.stepZ));
            int qj1 = Mathf.Min(g.res - 2, Mathf.FloorToInt((uz1 - g.pos.z) / g.stepZ));
            for (int qj = qj0; qj <= qj1; qj++)
            {
                for (int qi = qi0; qi <= qi1; qi++)
                {
                    if (!g.InZone(qi, qj)) continue;
                    Vector3 p00 = Corner(g, qi, qj), p10 = Corner(g, qi + 1, qj), p01 = Corner(g, qi, qj + 1), p11 = Corner(g, qi + 1, qj + 1);
                    CapTriangle(g, p00, p10, p11, cx, cz, mb, grassStyle);
                    CapTriangle(g, p00, p11, p01, cx, cz, mb, grassStyle);
                }
            }
        }

        static Vector3 Corner(GroundTerrain g, int i, int j)
        {
            return new Vector3(g.pos.x + i * g.stepX, g.Sample(i, j), g.pos.z + j * g.stepZ);
        }

        static void CapTriangle(GroundTerrain g, Vector3 a, Vector3 b, Vector3 c, int cx, int cz, MeshBuild mb, bool grassStyle)
        {
            float k = Link.Scale;
            float minX = Mathf.Min(a.x, Mathf.Min(b.x, c.x)), maxX = Mathf.Max(a.x, Mathf.Max(b.x, c.x));
            float minZ = Mathf.Min(a.z, Mathf.Min(b.z, c.z)), maxZ = Mathf.Max(a.z, Mathf.Max(b.z, c.z));
            // Minecraft columns this triangle covers, kept to this chunk.
            int x0 = Mathf.Max(cx * ChunkSize, Mathf.FloorToInt(minX / k)), x1 = Mathf.Min(cx * ChunkSize + ChunkSize - 1, Mathf.FloorToInt(maxX / k));
            int z0 = Mathf.Max(cz * ChunkSize, Mathf.FloorToInt(-maxZ / k)), z1 = Mathf.Min(cz * ChunkSize + ChunkSize - 1, Mathf.FloorToInt(-minZ / k));
            for (int z = z0; z <= z1; z++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    tri.Clear(); tri.Add(a); tri.Add(b); tri.Add(c);
                    Clip(tri, polyA, 0, x * k, true);
                    Clip(polyA, polyB, 0, (x + 1) * k, false);
                    Clip(polyB, polyA, 2, -(z + 1) * k, true);
                    Clip(polyA, polyB, 2, -z * k, false);
                    if (polyB.Count < 3) continue;
                    float lo = float.PositiveInfinity, hi = float.NegativeInfinity;
                    for (int i = 0; i < polyB.Count; i++) { lo = Mathf.Min(lo, polyB[i].y); hi = Mathf.Max(hi, polyB[i].y); }
                    int y0 = Mathf.FloorToInt(lo / k), y1 = Mathf.FloorToInt(hi / k);
                    for (int y = y0; y <= y1; y++)
                    {
                        if (IsDug(x, y, z)) continue;
                        List<Vector3> piece = polyB;
                        if (y0 != y1)
                        {
                            Clip(polyB, polyC, 1, y * k, true);
                            Clip(polyC, polyA, 1, (y + 1) * k, false);
                            piece = polyA;
                        }
                        if (piece.Count < 3) continue;
                        EmitCap(g, piece, mb, grassStyle, x, z);
                    }
                }
            }
        }

        static void EmitCap(GroundTerrain g, List<Vector3> poly, MeshBuild mb, bool grassStyle, int x, int z)
        {
            float k = Link.Scale;
            int b = mb.v.Count;
            for (int i = 0; i < poly.Count; i++)
            {
                Vector3 p = poly[i];
                Vector3 n = g.Normal(p.x, p.z);
                // Over the terrain where it still lies on the surface: 5 mm was too little far away
                // (the two fought, the dotted lines along the edges).
                p.y += CapLift;
                if (grassStyle)
                {
                    mb.v.Add(p);
                    float s = p.x / k - x, t = -p.z / k - z;
                    mb.uv.Add(AtlasUv(grass, s, t));
                }
                else
                {
                    Vector3 local = p - g.pos;
                    mb.v.Add(local);
                    mb.uv.Add(new Vector2(local.x / g.size.x, local.z / g.size.z));
                    Vector3 tg = Vector3.Cross(n, Vector3.forward).normalized;
                    mb.tan.Add(new Vector4(tg.x, tg.y, tg.z, -1f));
                }
                mb.n.Add(n);
            }
            Fan(mb, b, poly.Count, Vector3.up);
        }

        // Sutherland-Hodgman against one axis-aligned plane (keep >= value, or <= value).
        static void Clip(List<Vector3> src, List<Vector3> dst, int axis, float value, bool keepGreater)
        {
            dst.Clear();
            int count = src.Count;
            if (count == 0) return;
            for (int i = 0; i < count; i++)
            {
                Vector3 a = src[i], b = src[(i + 1) % count];
                float da = keepGreater ? a[axis] - value : value - a[axis];
                float db = keepGreater ? b[axis] - value : value - b[axis];
                if (da >= 0f) dst.Add(a);
                if ((da >= 0f) != (db >= 0f))
                {
                    float t = da / (da - db);
                    dst.Add(Vector3.Lerp(a, b, t));
                }
            }
        }

        // Triangles b..b+count-1 as a fan, wound to face 'facing' (Unity: clockwise from the front).
        static void Fan(MeshBuild mb, int b, int count, Vector3 facing)
        {
            Vector3 normal = Vector3.zero;
            for (int i = 0; i < count; i++)
            {
                Vector3 p = mb.v[b + i], q = mb.v[b + (i + 1) % count];
                normal.x += (p.y - q.y) * (p.z + q.z);
                normal.y += (p.z - q.z) * (p.x + q.x);
                normal.z += (p.x - q.x) * (p.y + q.y);
            }
            // Newell's normal points like Cross(v1 - v0, v2 - v0), which Unity's front faces share
            // (see Blocks): keep the order when it agrees with the facing, else reverse it.
            bool reverse = Vector3.Dot(normal, facing) < 0f;
            for (int i = 1; i + 1 < count; i++)
            {
                if (reverse) { mb.tris.Add(b); mb.tris.Add(b + i + 1); mb.tris.Add(b + i); }
                else { mb.tris.Add(b); mb.tris.Add(b + i); mb.tris.Add(b + i + 1); }
            }
        }

        static Vector2 AtlasUv(float[] rect, float s, float t)
        {
            s = Mathf.Clamp01(s);
            t = Mathf.Clamp01(t);
            float u = rect[0] + s * (rect[2] - rect[0]);
            float v = rect[1] + (1f - t) * (rect[3] - rect[1]);
            return new Vector2(u, 1f - v);
        }

        // ---- walls: ground still The Forest's, seen from a dug cell, cut along the surface ----

        static readonly List<float> ts = new List<float>(8);
        static readonly List<Vector3> facePoly = new List<Vector3>(16);

        static void Walls(long key, MeshBuild mb)
        {
            int x, y, z;
            Unpack(key, out x, out y, out z);
            float k = Link.Scale;
            GroundTerrain g = Ground.At((x + 0.5f) * k, -(z + 0.5f) * k);
            if (g == null) return;
            // Sides: +X, -X, +Z, -Z (Minecraft directions). A neighbour wholly under the surface
            // is a real Minecraft block by now (Minecraft reveals it), which draws its own face.
            if (Open(x + 1, y, z)) Side(g, x + 1, z, x + 1, z + 1, y, new Vector3(-1, 0, 0), x + 1, y, z, mb);
            if (Open(x - 1, y, z)) Side(g, x, z + 1, x, z, y, new Vector3(1, 0, 0), x - 1, y, z, mb);
            if (Open(x, y, z + 1)) Side(g, x + 1, z + 1, x, z + 1, y, new Vector3(0, 0, -1), x, y, z + 1, mb);
            if (Open(x, y, z - 1)) Side(g, x, z, x + 1, z, y, new Vector3(0, 0, 1), x, y, z - 1, mb);
            // Floor (ground below the cell reaching up to its bottom) and ceiling.
            if (Open(x, y - 1, z)) Flat(g, x, y, z, y, true, x, y - 1, z, mb);
            if (Open(x, y + 1, z)) Flat(g, x, y, z, y + 1, false, x, y + 1, z, mb);
        }

        // A neighbour cell whose ground is still drawn by The Forest: not dug, not wholly inside.
        static bool Open(int x, int y, int z)
        {
            return !IsDug(x, y, z) && !Revealed(x, y, z);
        }

        // Wholly under the original surface: Minecraft turns such a cell into a block when a dug
        // cell next to it opens it up (same rule and same numbers as TerrainDig.java).
        public static bool Revealed(int x, int y, int z)
        {
            float min = MinOf(x, z);
            return !float.IsNaN(min) && y + 1 <= min - 0.02f;
        }

        public static float MinOf(int x, int z)
        {
            float min, max;
            Ground.MinMaxMc(x, z, out min, out max);
            return min;
        }

        static float[] rectOverride;
        static readonly float[] unitRect = { 0f, 0f, 1f, 1f };

        // The Forest's ground inside one cell (its surface piece, its sides and floor cut along
        // the surface), uv 0..1 per face, pushed out a hair: what breaking cracks are drawn on.
        public static Mesh GroundCellMesh(int x, int y, int z)
        {
            float k = Link.Scale;
            GroundTerrain g = Ground.At((x + 0.5f) * k, -(z + 0.5f) * k);
            if (g == null) return null;
            var mb = new MeshBuild();
            rectOverride = unitRect;
            try
            {
                // Surface piece in this cell.
                float a0 = (x * k - g.pos.x) / g.stepX, a1 = ((x + 1) * k - g.pos.x) / g.stepX;
                float b0 = (-(z + 1) * k - g.pos.z) / g.stepZ, b1 = (-z * k - g.pos.z) / g.stepZ;
                int qi0 = Mathf.Max(0, Mathf.FloorToInt(a0)), qi1 = Mathf.Min(g.res - 2, Mathf.FloorToInt(a1));
                int qj0 = Mathf.Max(0, Mathf.FloorToInt(b0)), qj1 = Mathf.Min(g.res - 2, Mathf.FloorToInt(b1));
                for (int qj = qj0; qj <= qj1; qj++)
                {
                    for (int qi = qi0; qi <= qi1; qi++)
                    {
                        Vector3 p00 = Corner(g, qi, qj), p10 = Corner(g, qi + 1, qj), p01 = Corner(g, qi, qj + 1), p11 = Corner(g, qi + 1, qj + 1);
                        CellSurface(p00, p10, p11, x, y, z, mb);
                        CellSurface(p00, p11, p01, x, y, z, mb);
                    }
                }
                // Sides facing out of the cell, its underside, and its top where the ground fills it.
                Side(g, x + 1, z, x + 1, z + 1, y, new Vector3(1, 0, 0), x, y, z, mb);
                Side(g, x, z + 1, x, z, y, new Vector3(-1, 0, 0), x, y, z, mb);
                Side(g, x + 1, z + 1, x, z + 1, y, new Vector3(0, 0, 1), x, y, z, mb);
                Side(g, x, z, x + 1, z, y, new Vector3(0, 0, -1), x, y, z, mb);
                Flat(g, x, y, z, y, false, x, y, z, mb);
                Flat(g, x, y, z, y + 1, true, x, y, z, mb);
            }
            finally
            {
                rectOverride = null;
            }
            float e = 0.006f * k;
            for (int i = 0; i < mb.v.Count; i++) mb.v[i] += mb.n[i] * e;
            return mb.ToMesh("ground crack");
        }

        static void CellSurface(Vector3 a, Vector3 b, Vector3 c, int x, int y, int z, MeshBuild mb)
        {
            float k = Link.Scale;
            tri.Clear(); tri.Add(a); tri.Add(b); tri.Add(c);
            Clip(tri, polyA, 0, x * k, true);
            Clip(polyA, polyB, 0, (x + 1) * k, false);
            Clip(polyB, polyA, 2, -(z + 1) * k, true);
            Clip(polyA, polyB, 2, -z * k, false);
            Clip(polyB, polyC, 1, y * k, true);
            Clip(polyC, polyA, 1, (y + 1) * k, false);
            if (polyA.Count < 3) return;
            Vector3 n = Vector3.Cross(b - a, c - a).normalized;
            if (n.y < 0f) n = -n;
            int start = mb.v.Count;
            for (int i = 0; i < polyA.Count; i++)
            {
                Vector3 p = polyA[i];
                mb.v.Add(p);
                mb.n.Add(n);
                mb.uv.Add(new Vector2(p.x / k - x, -p.z / k - z));
            }
            Fan(mb, start, polyA.Count, n);
        }

        static float[] MaterialOf(int nx, int ny, int nz)
        {
            if (rectOverride != null) return rectOverride;
            float hc = Ground.HeightMc(nx + 0.5f, nz + 0.5f);
            if (!float.IsNaN(hc) && ny + 1 <= hc - 3f) return stone;
            return sandReady && Ground.SandMc(nx + 0.5f, nz + 0.5f) ? sand : dirt;
        }

        // The face between a dug cell and its neighbour, on the vertical plane through the
        // Minecraft segment (xa,za)-(xb,zb), from y up to the surface along that segment.
        static void Side(GroundTerrain g, float xa, float za, float xb, float zb, int y, Vector3 mcNormal, int nx, int ny, int nz, MeshBuild mb)
        {
            float k = Link.Scale;
            Ground.Breaks(g, xa, za, xb, zb, ts);
            float[] rect = MaterialOf(nx, ny, nz);
            float y0 = y, y1 = y + 1;
            float prevT = ts[0];
            float prevH = g.Height((xa + (xb - xa) * prevT) * k, -(za + (zb - za) * prevT) * k) / k;
            for (int n = 1; n < ts.Count; n++)
            {
                float t = ts[n];
                float h = g.Height((xa + (xb - xa) * t) * k, -(za + (zb - za) * t) * k) / k;
                Strip(prevT, prevH, t, h, y0, y1, xa, za, xb, zb, mcNormal, rect, mb);
                prevT = t;
                prevH = h;
            }
        }

        // Region t in [ta, tb], y0 <= y <= min(y1, surface line): a convex polygon.
        const float CapLift = 0.02f; // units the cap is drawn above the island's surface

        static void Strip(float ta, float ha, float tb, float hb, float y0, float y1,
            float xa, float za, float xb, float zb, Vector3 mcNormal, float[] rect, MeshBuild mb)
        {
            if (tb - ta < 1e-5f || (ha <= y0 && hb <= y0)) return;
            facePoly.Clear();
            // Walk the boundary: bottom edge left to right, then the top back.
            // Points as (t, y) packed in Vector3 (x = t, y = y).
            // Top of the region along t: min(y1, line), only where the line is above y0.
            float tStart = ta, tEnd = tb;
            if (ha < y0) tStart = ta + (y0 - ha) / (hb - ha) * (tb - ta);
            if (hb < y0) tEnd = ta + (y0 - ha) / (hb - ha) * (tb - ta);
            facePoly.Add(new Vector3(tStart, y0, 0));
            facePoly.Add(new Vector3(tEnd, y0, 0));
            // Top from tEnd back to tStart, with a knee where the line crosses y1.
            AddTop(tEnd, ta, ha, tb, hb, y0, y1, facePoly);
            float cross = (ha - y1) * (hb - y1) < 0f ? ta + (y1 - ha) / (hb - ha) * (tb - ta) : float.NaN;
            if (!float.IsNaN(cross) && cross > tStart && cross < tEnd) facePoly.Add(new Vector3(cross, y1, 0));
            AddTop(tStart, ta, ha, tb, hb, y0, y1, facePoly);
            // Drop repeated points (a triangle when the line starts on y0).
            for (int i = facePoly.Count - 1; i > 0; i--) if ((facePoly[i] - facePoly[i - 1]).sqrMagnitude < 1e-10f) facePoly.RemoveAt(i);
            if (facePoly.Count > 1 && (facePoly[0] - facePoly[facePoly.Count - 1]).sqrMagnitude < 1e-10f) facePoly.RemoveAt(facePoly.Count - 1);
            if (facePoly.Count < 3) return;
            float k = Link.Scale;
            int b = mb.v.Count;
            Vector3 normal = new Vector3(mcNormal.x, mcNormal.y, -mcNormal.z);
            for (int i = 0; i < facePoly.Count; i++)
            {
                float t = facePoly[i].x, yy = facePoly[i].y;
                float mx = xa + (xb - xa) * t, mz = za + (zb - za) * t;
                float uy = yy * k;
                // Where the wall meets the surface, up to the cap (drawn CapLift above it): the
                // 2 cm between them was a thin slit of light along the top of every hole.
                float line = ha + (hb - ha) * ((t - ta) / (tb - ta));
                if (yy > y0 + 1e-4f && yy < y1 - 1e-4f && Mathf.Abs(yy - line) < 1e-3f) uy += CapLift;
                // And a hair below its bottom edge, under the block or wall it sits on: no
                // hairline between the two.
                else if (Mathf.Abs(yy - y0) < 1e-5f) uy -= 0.004f;
                mb.v.Add(new Vector3(mx * k, uy, -mz * k));
                mb.n.Add(normal);
                mb.uv.Add(AtlasUv(rect, t, yy - y0));
            }
            Fan(mb, b, facePoly.Count, normal);
        }

        static void AddTop(float t, float ta, float ha, float tb, float hb, float y0, float y1, List<Vector3> poly)
        {
            float h = ha + (hb - ha) * ((t - ta) / (tb - ta));
            poly.Add(new Vector3(t, Mathf.Clamp(h, y0, y1), 0));
        }

        // The flat face at height 'level' over the cell's footprint where the surface is above it:
        // the top of the ground under a dug cell (floor) or its underside above one (ceiling).
        static void Flat(GroundTerrain g, int x, int y, int z, int level, bool floor, int nx, int ny, int nz, MeshBuild mb)
        {
            float k = Link.Scale;
            float[] rect = MaterialOf(nx, ny, nz);
            float a0 = (x * k - g.pos.x) / g.stepX, a1 = ((x + 1) * k - g.pos.x) / g.stepX;
            float b0 = (-(z + 1) * k - g.pos.z) / g.stepZ, b1 = (-z * k - g.pos.z) / g.stepZ;
            int qi0 = Mathf.Max(0, Mathf.FloorToInt(a0)), qi1 = Mathf.Min(g.res - 2, Mathf.FloorToInt(a1));
            int qj0 = Mathf.Max(0, Mathf.FloorToInt(b0)), qj1 = Mathf.Min(g.res - 2, Mathf.FloorToInt(b1));
            Vector3 normal = floor ? Vector3.up : Vector3.down;
            for (int qj = qj0; qj <= qj1; qj++)
            {
                for (int qi = qi0; qi <= qi1; qi++)
                {
                    Vector3 p00 = Corner(g, qi, qj), p10 = Corner(g, qi + 1, qj), p01 = Corner(g, qi, qj + 1), p11 = Corner(g, qi + 1, qj + 1);
                    FlatTriangle(p00, p10, p11, x, z, level, normal, rect, mb);
                    FlatTriangle(p00, p11, p01, x, z, level, normal, rect, mb);
                }
            }
        }

        static void FlatTriangle(Vector3 a, Vector3 b, Vector3 c, int x, int z, int level, Vector3 normal, float[] rect, MeshBuild mb)
        {
            float k = Link.Scale;
            tri.Clear(); tri.Add(a); tri.Add(b); tri.Add(c);
            Clip(tri, polyA, 0, x * k, true);
            Clip(polyA, polyB, 0, (x + 1) * k, false);
            Clip(polyB, polyA, 2, -(z + 1) * k, true);
            Clip(polyA, polyB, 2, -z * k, false);
            Clip(polyB, polyA, 1, level * k, true); // where the surface is above the plane
            if (polyA.Count < 3) return;
            int start = mb.v.Count;
            for (int i = 0; i < polyA.Count; i++)
            {
                Vector3 p = polyA[i];
                p.y = level * k;
                mb.v.Add(p);
                mb.n.Add(normal);
                mb.uv.Add(AtlasUv(rect, p.x / k - x, -p.z / k - z));
            }
            Fan(mb, start, polyA.Count, normal);
        }
    }
}
