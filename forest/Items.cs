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
            float e = 0.004f;
            cube.transform.position = new Vector3((x - e) * k, (y - e) * k, -(z + 1 + e) * k);
            cube.transform.localScale = Vector3.one * ((1f + 2f * e) * k);
            cube.GetComponent<MeshRenderer>().sharedMaterial = mats[stage];
            if (!cube.activeSelf) cube.SetActive(true);
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
            verts.Clear(); normals.Clear(); uvs.Clear();
            var groups = new Dictionary<long, List<int>>();
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
                for (int i = 0; i < 4; i++)
                {
                    int v = o + i * 20;
                    verts.Add(new Vector3(BitConverter.ToSingle(buffer, v), BitConverter.ToSingle(buffer, v + 4), -BitConverter.ToSingle(buffer, v + 8)));
                    normals.Add(n);
                    uvs.Add(new Vector2(BitConverter.ToSingle(buffer, v + 12), 1f - BitConverter.ToSingle(buffer, v + 16)));
                }
                // Both windings: Minecraft draws entities without back-face culling (arrow fins,
                // flat items, capes are single planes), a closed box just hides its inner side.
                tris.Add(b); tris.Add(b + 1); tris.Add(b + 2); tris.Add(b); tris.Add(b + 2); tris.Add(b + 3);
                tris.Add(b); tris.Add(b + 2); tris.Add(b + 1); tris.Add(b); tris.Add(b + 3); tris.Add(b + 2);
            }
            GameObject slot = slots[e];
            Mesh m = slot.GetComponent<MeshFilter>().sharedMesh;
            m.Clear();
            m.SetVertices(verts);
            m.SetNormals(normals);
            m.SetUVs(0, uvs);
            m.subMeshCount = groups.Count;
            var mats = new Material[groups.Count];
            int sub = 0;
            foreach (var pair in groups)
            {
                m.SetTriangles(pair.Value, sub);
                mats[sub] = MaterialFor(pair.Key);
                sub++;
            }
            m.RecalculateBounds();
            slot.GetComponent<MeshRenderer>().sharedMaterials = mats;
        }

        static Material MaterialFor(long key)
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
