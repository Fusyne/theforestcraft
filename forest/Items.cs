using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace ForestCraft
{
    // Dropped items as meshes in The Forest's scene: Minecraft publishes every frame the item
    // entities around the player (position, bob, spin); the models came through the mailbox.
    static class Items
    {
        const int OffItems = 0xA60000;
        const int Entry = 32;
        const int Max = 256;

        static readonly Dictionary<int, GameObject> live = new Dictionary<int, GameObject>();
        static readonly Dictionary<int, int> liveModel = new Dictionary<int, int>();
        static readonly HashSet<int> seen = new HashSet<int>();
        static readonly List<int> gone = new List<int>();
        static GameObject root;

        public static void Update(IntPtr view)
        {
            if (view == IntPtr.Zero) return;
            bool show = Link.Driving;
            seen.Clear();
            if (show)
            {
                int seq = Marshal.ReadInt32(view, OffItems);
                int count = Marshal.ReadInt32(view, OffItems + 4);
                if ((seq & 1) == 0 && count >= 0 && count <= Max)
                {
                    float k = Link.Scale;
                    for (int n = 0; n < count; n++)
                    {
                        int at = OffItems + 16 + n * Entry;
                        int id = Marshal.ReadInt32(view, at);
                        int model = Marshal.ReadInt32(view, at + 4);
                        float x = Link.ReadFloatAt(at + 8), y = Link.ReadFloatAt(at + 12), z = Link.ReadFloatAt(at + 16);
                        float bob = Link.ReadFloatAt(at + 20), spin = Link.ReadFloatAt(at + 24);
                        Mesh mesh; Material[] mats;
                        if (!Blocks.ItemModel(model, out mesh, out mats)) continue;
                        GameObject go = Get(id, model, mesh, mats);
                        go.transform.position = new Vector3(x * k, (y + bob) * k, -z * k);
                        go.transform.rotation = Quaternion.Euler(0f, -spin, 0f);
                        go.transform.localScale = new Vector3(k, k, k);
                        seen.Add(id);
                    }
                }
            }
            gone.Clear();
            foreach (var pair in live) if (!seen.Contains(pair.Key)) gone.Add(pair.Key);
            for (int i = 0; i < gone.Count; i++)
            {
                GameObject go;
                if (live.TryGetValue(gone[i], out go) && go != null) UnityEngine.Object.Destroy(go);
                live.Remove(gone[i]);
                liveModel.Remove(gone[i]);
            }
        }

        static GameObject Get(int id, int model, Mesh mesh, Material[] mats)
        {
            GameObject go;
            int had;
            if (live.TryGetValue(id, out go) && go != null && liveModel.TryGetValue(id, out had) && had == model)
            {
                MeshFilter f = go.GetComponent<MeshFilter>();
                if (f.sharedMesh != mesh) { f.sharedMesh = mesh; go.GetComponent<MeshRenderer>().sharedMaterials = mats; }
                return go;
            }
            if (go != null) UnityEngine.Object.Destroy(go);
            if (root == null)
            {
                root = new GameObject("ForestCraft Items");
                UnityEngine.Object.DontDestroyOnLoad(root);
            }
            go = new GameObject("item " + id);
            go.transform.SetParent(root.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterials = mats;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            live[id] = go;
            liveModel[id] = model;
            return go;
        }
    }

    // Minecraft's breaking cracks on the block being mined, in the scene (depth-tested, no lag).
    static class Cracks
    {
        static readonly Texture2D[] stages = new Texture2D[10];
        static readonly Material[] mats = new Material[10];
        static int stamp;
        static GameObject cube;

        public static void Load(IntPtr view)
        {
            int s = Marshal.ReadInt32(view, 0x200 + 152);
            if (s == 0 || s == stamp) return;
            stamp = s;
            Shader shader = Blocks.CutoutShader();
            if (shader == null) return;
            for (int i = 0; i < 10; i++)
            {
                stages[i] = Blocks.LoadTexture("crack" + i + ".bin", stages[i]);
                if (stages[i] == null) continue;
                stages[i].wrapMode = TextureWrapMode.Repeat;
                if (mats[i] == null) mats[i] = new Material(shader);
                mats[i].mainTexture = stages[i];
                mats[i].color = new Color(0.35f, 0.35f, 0.35f, 1f);
                if (mats[i].HasProperty("_Cutoff")) mats[i].SetFloat("_Cutoff", 0.1f);
                mats[i].renderQueue = 2460;
            }
            Plugin.Log.LogInfo("ForestCraft: breaking cracks loaded");
        }

        public static void Update(IntPtr view)
        {
            int stage = view == IntPtr.Zero || !Link.Driving ? -1 : Marshal.ReadInt32(view, 0x200 + 132);
            if (stage < 0 || stage > 9 || mats[stage] == null)
            {
                if (cube != null && cube.activeSelf) cube.SetActive(false);
                return;
            }
            if (cube == null) Build();
            float k = Link.Scale;
            int x = Link.ReadMcInt(136), y = Link.ReadMcInt(140), z = Link.ReadMcInt(144);
            bool ground = Link.ReadMcInt(184) == 1;
            if (ground)
            {
                // On The Forest's ground: the cracks follow the ground's shape in that cell.
                if (groundKey != Key(x, y, z) || groundMesh == null || groundScale != k)
                {
                    if (groundMesh != null) UnityEngine.Object.Destroy(groundMesh);
                    groundMesh = DigWorld.GroundCellMesh(x, y, z);
                    groundKey = Key(x, y, z);
                    groundScale = k;
                }
                if (groundMesh != null)
                {
                    cube.GetComponent<MeshFilter>().sharedMesh = groundMesh;
                    cube.transform.position = Vector3.zero;
                    cube.transform.localScale = Vector3.one;
                    cube.GetComponent<MeshRenderer>().sharedMaterial = mats[stage];
                    if (!cube.activeSelf) cube.SetActive(true);
                    return;
                }
            }
            cube.GetComponent<MeshFilter>().sharedMesh = cubeMesh;
            float e = 0.004f;
            cube.transform.position = new Vector3((x - e) * k, (y - e) * k, -(z + 1 + e) * k);
            cube.transform.localScale = Vector3.one * ((1f + 2f * e) * k);
            cube.GetComponent<MeshRenderer>().sharedMaterial = mats[stage];
            if (!cube.activeSelf) cube.SetActive(true);
        }

        static Mesh cubeMesh, groundMesh;
        static long groundKey = long.MinValue;
        static float groundScale;

        static long Key(int x, int y, int z)
        {
            return ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (long)(z & 0x1FFFFF);
        }

        static void Build()
        {
            cube = new GameObject("ForestCraft crack");
            UnityEngine.Object.DontDestroyOnLoad(cube);
            var mesh = new Mesh();
            var v = new List<Vector3>();
            var n = new List<Vector3>();
            var uv = new List<Vector2>();
            var t = new List<int>();
            // Unit cube 0..1, outward faces, clockwise seen from outside (Unity front faces).
            AddFace(v, n, uv, t, new Vector3(0, 0, 0), Vector3.right, Vector3.up, Vector3.back);    // z = 0 face
            AddFace(v, n, uv, t, new Vector3(1, 0, 1), Vector3.left, Vector3.up, Vector3.forward);  // z = 1 face
            AddFace(v, n, uv, t, new Vector3(0, 0, 1), Vector3.back, Vector3.up, Vector3.left);     // x = 0 face
            AddFace(v, n, uv, t, new Vector3(1, 0, 0), Vector3.forward, Vector3.up, Vector3.right); // x = 1 face
            AddFace(v, n, uv, t, new Vector3(0, 1, 0), Vector3.right, Vector3.forward, Vector3.up); // top
            AddFace(v, n, uv, t, new Vector3(0, 0, 1), Vector3.right, Vector3.back, Vector3.down);  // bottom
            mesh.SetVertices(v);
            mesh.SetNormals(n);
            mesh.SetUVs(0, uv);
            mesh.SetTriangles(t, 0);
            mesh.RecalculateBounds();
            cubeMesh = mesh;
            cube.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = cube.AddComponent<MeshRenderer>();
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        static void AddFace(List<Vector3> v, List<Vector3> n, List<Vector2> uv, List<int> t, Vector3 o, Vector3 right, Vector3 up, Vector3 normal)
        {
            int b = v.Count;
            v.Add(o); v.Add(o + up); v.Add(o + up + right); v.Add(o + right);
            for (int i = 0; i < 4; i++) n.Add(normal);
            uv.Add(new Vector2(0, 0)); uv.Add(new Vector2(0, 1)); uv.Add(new Vector2(1, 1)); uv.Add(new Vector2(1, 0));
            Vector3 face = Vector3.Cross(v[b + 1] - v[b], v[b + 2] - v[b]);
            if (Vector3.Dot(face, normal) >= 0f) { t.Add(b); t.Add(b + 1); t.Add(b + 2); t.Add(b); t.Add(b + 2); t.Add(b + 3); }
            else { t.Add(b); t.Add(b + 2); t.Add(b + 1); t.Add(b); t.Add(b + 3); t.Add(b + 2); }
        }
    }
}

namespace ForestCraft
{
    // Every living entity Minecraft draws (mobs, Steve in F5, their armor, elytra, held items),
    // as meshes in The Forest's scene: hidden by trees and blocks, lit like the island.
    static class Entities
    {
        const int OffEntities = 0xA90000;
        const int MaxEntities = 256;
        const int QuadsAt = 16 + MaxEntities * 32;
        const int Quad = 100;
        const int MaxQuads = (0x6F000 - QuadsAt) / Quad;

        static readonly List<GameObject> slots = new List<GameObject>();
        static readonly Dictionary<int, Texture2D> textures = new Dictionary<int, Texture2D>();
        static readonly HashSet<int> tried = new HashSet<int>();
        static readonly Dictionary<int, int> retries = new Dictionary<int, int>();
        static readonly Dictionary<long, Material> materials = new Dictionary<long, Material>();
        static readonly List<Vector3> verts = new List<Vector3>();
        static readonly List<Vector3> normals = new List<Vector3>();
        static readonly List<Vector2> uvs = new List<Vector2>();
        static readonly Dictionary<long, List<int>> groups = new Dictionary<long, List<int>>();
        static readonly List<int> slotHash = new List<int>();
        static byte[] buffer;
        static int lastSeq, shown;
        static GameObject root;

        public static void Update(IntPtr view, bool thirdPerson, Vector3 localFeet)
        {
            if (view == IntPtr.Zero) return;
            LoadTextures(view);
            if (!Link.Driving) { Hide(0); return; }
            int seq = Marshal.ReadInt32(view, OffEntities);
            if ((seq & 1) != 0 || seq == lastSeq)
            {
                Place(view, thirdPerson, localFeet);
                return;
            }
            int count = Marshal.ReadInt32(view, OffEntities + 4);
            int quads = Marshal.ReadInt32(view, OffEntities + 8);
            if (count < 0 || count > MaxEntities || quads < 0 || quads > MaxQuads) return;
            int bytes = QuadsAt + quads * Quad;
            if (buffer == null || buffer.Length < bytes) buffer = new byte[bytes];
            Marshal.Copy(new IntPtr(view.ToInt64() + OffEntities), buffer, 0, bytes);
            if (Marshal.ReadInt32(view, OffEntities) != seq) return;
            lastSeq = seq;
            for (int e = 0; e < count; e++) Build(e);
            shown = count;
            Hide(count);
            Place(view, thirdPerson, localFeet);
        }

        /// <summary>
        /// A copy of the small entity Minecraft shows nearest to this point (an arrow that just
        /// hit something), as it looks right now: same mesh, same materials. Null if none.
        /// </summary>
        public static GameObject CopyNear(Vector3 mcPos, float within)
        {
            if (buffer == null) return null;
            float best = within * within;
            int found = -1;
            for (int e = 0; e < shown && e < slots.Count; e++)
            {
                int at = 16 + e * 32;
                if ((BitConverter.ToInt32(buffer, at) & 1) != 0) continue; // Steve
                int count = BitConverter.ToInt32(buffer, at + 20);
                if (count <= 0 || count > 40) continue; // an arrow is a handful of quads
                float dx = BitConverter.ToSingle(buffer, at + 4) - mcPos.x;
                float dy = BitConverter.ToSingle(buffer, at + 8) - mcPos.y;
                float dz = BitConverter.ToSingle(buffer, at + 12) - mcPos.z;
                float d2 = dx * dx + dy * dy + dz * dz;
                if (d2 < best) { best = d2; found = e; }
            }
            if (found < 0) return null;
            GameObject src = slots[found];
            Mesh mesh = src.GetComponent<MeshFilter>().sharedMesh;
            if (mesh == null || mesh.vertexCount == 0) return null;
            var copy = new GameObject("ForestCraft planted arrow");
            copy.AddComponent<MeshFilter>().sharedMesh = UnityEngine.Object.Instantiate(mesh);
            var r = copy.AddComponent<MeshRenderer>();
            r.sharedMaterials = src.GetComponent<MeshRenderer>().sharedMaterials;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            copy.transform.localScale = Vector3.one * Link.Scale;
            return copy;
        }

        static void Place(IntPtr view, bool thirdPerson, Vector3 localFeet)
        {
            float k = Link.Scale;
            for (int e = 0; e < shown && e < slots.Count; e++)
            {
                int at = 16 + e * 32;
                bool local = (BitConverter.ToInt32(buffer, at) & 1) != 0;
                GameObject go = slots[e];
                if (local && !thirdPerson) { if (go.activeSelf) go.SetActive(false); continue; }
                // Steve stands where the camera is placed from; others where Minecraft has them.
                go.transform.position = local ? localFeet
                    : new Vector3(BitConverter.ToSingle(buffer, at + 4) * k, BitConverter.ToSingle(buffer, at + 8) * k, -BitConverter.ToSingle(buffer, at + 12) * k);
                go.transform.localScale = Vector3.one * k;
                if (!go.activeSelf) go.SetActive(true);
            }
        }

        static void Hide(int from)
        {
            for (int i = from; i < slots.Count; i++) if (slots[i] != null && slots[i].activeSelf) slots[i].SetActive(false);
            if (from == 0) shown = 0;
        }

        static void Build(int e)
        {
            while (slots.Count <= e)
            {
                if (root == null) { root = new GameObject("ForestCraft Entities"); UnityEngine.Object.DontDestroyOnLoad(root); }
                var go = new GameObject("entity " + slots.Count);
                go.transform.SetParent(root.transform, false);
                var mesh = new Mesh();
                mesh.MarkDynamic();
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                slots.Add(go);
            }
            int at = 16 + e * 32;
            int first = BitConverter.ToInt32(buffer, at + 16);
            int count = BitConverter.ToInt32(buffer, at + 20);
            // Same quads as last time (an entity standing still, a block entity): keep the mesh.
            int hash = count * 31 + first;
            int end = QuadsAt + (first + count) * Quad;
            for (int o = QuadsAt + first * Quad; o + 4 <= end; o += 4) hash = hash * 31 + BitConverter.ToInt32(buffer, o);
            while (slotHash.Count <= e) slotHash.Add(0);
            if (slotHash[e] == hash && hash != 0) return;
            slotHash[e] = hash;
            verts.Clear(); normals.Clear(); uvs.Clear();
            foreach (var list in groups.Values) list.Clear();
            for (int q = first; q < first + count; q++)
            {
                int o = QuadsAt + q * Quad;
                Vector3 n = new Vector3(BitConverter.ToSingle(buffer, o + 80), BitConverter.ToSingle(buffer, o + 84), -BitConverter.ToSingle(buffer, o + 88));
                int material = BitConverter.ToInt32(buffer, o + 92);
                int color = BitConverter.ToInt32(buffer, o + 96);
                long key = ((long)material << 32) | (uint)color;
                List<int> tris;
                if (!groups.TryGetValue(key, out tris)) { tris = new List<int>(); groups[key] = tris; }
                int b = verts.Count;
                // Both sides: Minecraft draws entities without back-face culling (arrow fins,
                // flat items, capes are single planes). The back side gets its own vertices with the
                // normal turned round: sharing the front's normal lit it as if it faced the other
                // way, and a thin turning plane (an arrow's fins) flashed light and dark.
                for (int side = 0; side < 2; side++)
                    for (int i = 0; i < 4; i++)
                    {
                        int v = o + i * 20;
                        verts.Add(new Vector3(BitConverter.ToSingle(buffer, v), BitConverter.ToSingle(buffer, v + 4), -BitConverter.ToSingle(buffer, v + 8)));
                        normals.Add(side == 0 ? n : -n);
                        uvs.Add(new Vector2(BitConverter.ToSingle(buffer, v + 12), 1f - BitConverter.ToSingle(buffer, v + 16)));
                    }
                // Each side wound so Unity sees it as facing the way its normal points (front faces:
                // Cross(v1 - v0, v2 - v0) along the normal). The flip of z between Minecraft and
                // Unity reverses Minecraft's winding: guessing it the other way showed every mob
                // from its inside-facing side (black in some qualities, striped shadows in others).
                Vector3 c = Vector3.Cross(verts[b + 1] - verts[b], verts[b + 2] - verts[b]);
                bool along = Vector3.Dot(c, n) >= 0f;
                int f = along ? b : b + 4, r = along ? b + 4 : b;
                tris.Add(f); tris.Add(f + 1); tris.Add(f + 2); tris.Add(f); tris.Add(f + 2); tris.Add(f + 3);
                tris.Add(r); tris.Add(r + 2); tris.Add(r + 1); tris.Add(r); tris.Add(r + 3); tris.Add(r + 2);
            }
            GameObject slot = slots[e];
            Mesh m = slot.GetComponent<MeshFilter>().sharedMesh;
            m.Clear();
            m.SetVertices(verts);
            m.SetNormals(normals);
            m.SetUVs(0, uvs);
            int used = 0;
            foreach (var pair in groups) if (pair.Value.Count > 0) used++;
            m.subMeshCount = used;
            var mats = new Material[used];
            int sub = 0;
            foreach (var pair in groups)
            {
                if (pair.Value.Count == 0) continue;
                m.SetTriangles(pair.Value, sub);
                mats[sub] = MaterialFor(pair.Key);
                sub++;
            }
            m.RecalculateBounds();
            var renderer = slot.GetComponent<MeshRenderer>();
            renderer.sharedMaterials = mats;
            // Small things (an arrow, a dropped item) cast no shadow: on thin parts the shadow
            // flickered on the thing itself.
            Vector3 size = m.bounds.size;
            renderer.shadowCastingMode = Mathf.Max(size.x, Mathf.Max(size.y, size.z)) < 0.7f
                ? UnityEngine.Rendering.ShadowCastingMode.Off : UnityEngine.Rendering.ShadowCastingMode.On;
        }

        internal static Material MaterialFor(long key)
        {
            int material = (int)(key >> 32);
            uint color = (uint)(key & 0xFFFFFFFF);
            // Items in hand use the block/item atlases (same materials as the placed blocks).
            if (material == -1 || material == -2) return Blocks.MaterialFor(((long)(1 | ((material == -2 ? 1 : 0) << 8)) << 32) | color);
            Material m;
            if (materials.TryGetValue(key, out m) && m != null)
            {
                Texture2D t;
                if (m.mainTexture == null && textures.TryGetValue(material, out t)) m.mainTexture = t;
                return m;
            }
            Shader shader = Blocks.CutoutShader();
            m = new Material(shader);
            Texture2D tex;
            if (textures.TryGetValue(material, out tex)) m.mainTexture = tex;
            if (m.HasProperty("_Cutoff")) m.SetFloat("_Cutoff", 0.1f);
            m.color = color == 0xFFFFFFFF ? Color.white : new Color(((color >> 16) & 255) / 255f, ((color >> 8) & 255) / 255f, (color & 255) / 255f, 1f);
            materials[key] = m;
            return m;
        }

        // Texture N (tex_N.bin), or the block (-1) / item (-2) atlas.
        public static Texture2D Texture(int id)
        {
            if (id == -1 || id == -2) return Blocks.Atlas(id == -2 ? 1 : 0);
            Texture2D t;
            return textures.TryGetValue(id, out t) ? t : null;
        }

        // Minecraft exports each entity texture once as tex_N.bin; OFF_MC+164 = how many exist.
        static int session;

        static void LoadTextures(IntPtr view)
        {
            int current = Marshal.ReadInt32(view, 0x200 + 176);
            if (current == 0) return; // Minecraft hasn't announced its session yet
            if (current != session)
            {
                // New Minecraft session: its tex_N numbering starts over. Drop everything loaded.
                session = current;
                tried.Clear();
                retries.Clear();
                foreach (var t in textures.Values) if (t != null) UnityEngine.Object.Destroy(t);
                textures.Clear();
                foreach (var m in materials.Values) if (m != null) m.mainTexture = null;
            }
            int ready = Marshal.ReadInt32(view, 0x200 + 164);
            for (int i = 0; i < ready && i < 4096; i++)
            {
                if (tried.Contains(i)) continue;
                Texture2D tex = Blocks.LoadTexture("tex_" + i + ".bin", null);
                if (tex == null)
                {
                    // Minecraft may still be closing the file: retry a few frames, then give up.
                    int n; retries.TryGetValue(i, out n); retries[i] = ++n;
                    if (n > 30) tried.Add(i);
                    continue;
                }
                tried.Add(i);
                textures[i] = tex;
                foreach (var pair in materials)
                    if ((int)(pair.Key >> 32) == i && pair.Value != null) pair.Value.mainTexture = tex;
            }
        }
    }
}
