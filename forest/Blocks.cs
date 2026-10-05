using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace ForestCraft
{
    // Minecraft's placed blocks as real meshes in The Forest's scene (SkyCraft composites a
    // depth-tested layer; Unity 5.6 can't share a depth buffer with an OpenGL process, so the
    // geometry itself comes over). Trees hide them, The Forest's sun and night light them,
    // and they stay put when the camera turns. One 16^3 section per mailbox message.
    static class Blocks
    {
        const int OffMesh = 0xB00000;
        const int Head = 64;
        const int Quad = 100;
        const int MaxQuadsPerMesh = 16000; // 64k vertex limit of Unity 5.6 meshes

        static readonly Dictionary<long, GameObject> sections = new Dictionary<long, GameObject>();
        static readonly Dictionary<long, Material> materials = new Dictionary<long, Material>();
        static readonly Dictionary<Material, int> materialAtlas = new Dictionary<Material, int>();
        static readonly Texture2D[] atlases = new Texture2D[2];
        static readonly int[] atlasStamps = new int[2];
        static readonly Dictionary<int, Mesh> itemMeshes = new Dictionary<int, Mesh>();
        static readonly Dictionary<int, Material[]> itemMaterials = new Dictionary<int, Material[]>();
        static GameObject root;
        static Shader solidShader, cutoutShader, fadeShader;
        static bool shadersLogged;
        static byte[] buffer;

        // A new game: every block section of the previous one goes (Minecraft sends them again).
        public static void ClearAll()
        {
            foreach (var go in sections.Values) if (go != null) UnityEngine.Object.Destroy(go);
            sections.Clear();
            lights.Clear();
        }

        public static void Reset(IntPtr view)
        {
            Marshal.WriteInt32(view, OffMesh, 0);
            Marshal.WriteInt32(view, OffMesh + 4, 0);
        }

        public static void Update(IntPtr view)
        {
            if (view == IntPtr.Zero) return;
            LoadAtlas(view, 0, 92, "atlas.bin");
            LoadAtlas(view, 1, 148, "atlas_items.bin");
            Cracks.Load(view);
            PickLights();
            int msg = Marshal.ReadInt32(view, OffMesh);
            int ack = Marshal.ReadInt32(view, OffMesh + 4);
            if (msg == ack) return;
            try
            {
                Receive(view);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("ForestCraft: block mesh failed: " + e);
            }
            Marshal.WriteInt32(view, OffMesh + 4, msg);
        }

        // How many times each section was received: the dug ground waits for the blocks it reveals.
        static readonly Dictionary<long, int> stamps = new Dictionary<long, int>();

        public static int Stamp(int sx, int sy, int sz)
        {
            long key = ((long)(sx & 0x3FFFFF) << 42) | ((long)(sy & 0xFFFFF) << 22) | (long)(sz & 0x3FFFFF);
            int st;
            stamps.TryGetValue(key, out st);
            return st;
        }

        static void Receive(IntPtr view)
        {
            int sx = Marshal.ReadInt32(view, OffMesh + 8);
            int sy = Marshal.ReadInt32(view, OffMesh + 12);
            int sz = Marshal.ReadInt32(view, OffMesh + 16);
            int count = Marshal.ReadInt32(view, OffMesh + 20);
            bool item = Marshal.ReadInt32(view, OffMesh + 24) == 1;
            long key = ((long)(sx & 0x3FFFFF) << 42) | ((long)(sy & 0xFFFFF) << 22) | (long)(sz & 0x3FFFFF);
            if (!item) { int st; stamps.TryGetValue(key, out st); stamps[key] = st + 1; }
            GameObject old;
            if (!item && sections.TryGetValue(key, out old))
            {
                if (old != null) UnityEngine.Object.Destroy(old);
                sections.Remove(key);
            }
            if (count <= 0) return;
            int sentQuads = count; // the light list follows all the quads Minecraft wrote
            if (count > MaxQuadsPerMesh) count = MaxQuadsPerMesh;
            int bytes = count * Quad;
            if (buffer == null || buffer.Length < bytes) buffer = new byte[bytes];
            Marshal.Copy(new IntPtr(view.ToInt64() + OffMesh + Head), buffer, 0, bytes);

            // Item models are built in block units and scaled by their GameObject.
            float k = item ? 1f : Link.Scale;
            // Group quads by material (layer + tint colour) into submeshes.
            var groups = new Dictionary<long, List<int>>();
            var verts = new List<Vector3>(count * 4);
            var normals = new List<Vector3>(count * 4);
            var uvs = new List<Vector2>(count * 4);
            for (int q = 0; q < count; q++)
            {
                int o = q * Quad;
                int color = BitConverter.ToInt32(buffer, o + 80);
                int layer = BitConverter.ToInt32(buffer, o + 84);
                Vector3 n = new Vector3(BitConverter.ToSingle(buffer, o + 88), BitConverter.ToSingle(buffer, o + 92), -BitConverter.ToSingle(buffer, o + 96));
                int b = verts.Count;
                for (int i = 0; i < 4; i++)
                {
                    int v = o + i * 20;
                    float x = BitConverter.ToSingle(buffer, v);
                    float y = BitConverter.ToSingle(buffer, v + 4);
                    float z = BitConverter.ToSingle(buffer, v + 8);
                    verts.Add(new Vector3(x * k, y * k, -z * k));
                    normals.Add(n);
                    // GPU row 0 is the atlas' top row; Unity's row 0 is the bottom.
                    uvs.Add(new Vector2(BitConverter.ToSingle(buffer, v + 12), 1f - BitConverter.ToSingle(buffer, v + 16)));
                }
                long mat = ((long)layer << 32) | (uint)color;
                List<int> tris;
                if (!groups.TryGetValue(mat, out tris)) { tris = new List<int>(); groups[mat] = tris; }
                // Z is mirrored on the way in, so check the winding against the face normal
                // instead of trusting Minecraft's order.
                Vector3 face = Vector3.Cross(verts[b + 1] - verts[b], verts[b + 2] - verts[b]);
                if (Vector3.Dot(face, n) >= 0f)
                {
                    tris.Add(b); tris.Add(b + 1); tris.Add(b + 2);
                    tris.Add(b); tris.Add(b + 2); tris.Add(b + 3);
                }
                else
                {
                    tris.Add(b); tris.Add(b + 2); tris.Add(b + 1);
                    tris.Add(b); tris.Add(b + 3); tris.Add(b + 2);
                }
            }

            var mesh = new Mesh();
            mesh.name = "ForestCraft section " + sx + "," + sy + "," + sz;
            mesh.SetVertices(verts);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.subMeshCount = groups.Count;
            var mats = new Material[groups.Count];
            int sub = 0;
            foreach (var pair in groups)
            {
                mesh.SetTriangles(pair.Value, sub);
                mats[sub] = MaterialFor(pair.Key);
                sub++;
            }
            mesh.RecalculateBounds();

            if (item)
            {
                Mesh previous;
                if (itemMeshes.TryGetValue(sx, out previous) && previous != null) UnityEngine.Object.Destroy(previous);
                itemMeshes[sx] = mesh;
                itemMaterials[sx] = mats;
                return;
            }

            if (root == null)
            {
                root = new GameObject("ForestCraft Blocks");
                UnityEngine.Object.DontDestroyOnLoad(root);
            }
            var go = new GameObject(mesh.name);
            go.transform.SetParent(root.transform, false);
            go.transform.position = new Vector3(sx * 16 * k, sy * 16 * k, -sz * 16 * k);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = mats;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            renderer.receiveShadows = true;
            sections[key] = go;
            AddLights(view, go, sentQuads, k);
            AddColliders(view, go, sentQuads, k);
        }

        // Torches, lanterns, lava, glowstone... light The Forest's night: one point light per
        // source block, range like Minecraft's light level, warm colour, no shadows.
        static void AddLights(IntPtr view, GameObject section, int quadCount, float k)
        {
            int n = Marshal.ReadInt32(view, OffMesh + 28);
            if (n <= 0 || n > 256) return;
            int at = OffMesh + Head + quadCount * Quad + 4;
            for (int i = 0; i < n; i++)
            {
                float x = Link.ReadFloatAt(at + i * 16), y = Link.ReadFloatAt(at + i * 16 + 4), z = Link.ReadFloatAt(at + i * 16 + 8);
                float level = Link.ReadFloatAt(at + i * 16 + 12);
                var lo = new GameObject("light");
                lo.transform.SetParent(section.transform, false);
                lo.transform.localPosition = new Vector3(x * k, y * k, -z * k);
                var light = lo.AddComponent<Light>();
                light.type = LightType.Point;
                light.range = Mathf.Max(2f, level * 0.75f) * k;
                light.intensity = 0.3f + level / 15f * 0.6f;
                light.color = new Color(1f, 0.78f, 0.5f);
                light.shadows = LightShadows.None;
                light.renderMode = LightRenderMode.Auto;
                lights.Add(light);
            }
        }

        // Minecraft's collision boxes for the section (merged walls, slabs, fences...) as real
        // colliders on the layer of The Forest's cliffs: cannibals, animals, logs and dropped
        // things stop at Minecraft's walls. (Solids skips them: Minecraft knows its own blocks.)
        public const int ColliderLayer = 21;

        static void AddColliders(IntPtr view, GameObject section, int quadCount, float k)
        {
            int lights = Marshal.ReadInt32(view, OffMesh + 28);
            if (lights < 0 || lights > 256) return;
            int at = OffMesh + Head + quadCount * Quad + 4 + lights * 16;
            int n = Marshal.ReadInt32(view, at);
            if (n <= 0 || n > 4096 || n != Marshal.ReadInt32(view, OffMesh + 32)) return;
            var holder = new GameObject("collision");
            holder.layer = ColliderLayer;
            holder.transform.SetParent(section.transform, false);
            at += 4;
            for (int i = 0; i < n; i++)
            {
                float x0 = Link.ReadFloatAt(at), y0 = Link.ReadFloatAt(at + 4), z0 = Link.ReadFloatAt(at + 8);
                float x1 = Link.ReadFloatAt(at + 12), y1 = Link.ReadFloatAt(at + 16), z1 = Link.ReadFloatAt(at + 20);
                at += 24;
                var box = holder.AddComponent<BoxCollider>();
                box.center = new Vector3((x0 + x1) * 0.5f * k, (y0 + y1) * 0.5f * k, -(z0 + z1) * 0.5f * k);
                box.size = new Vector3((x1 - x0) * k, (y1 - y0) * k, (z1 - z0) * k);
            }
        }

        public static bool IsOurs(Transform t)
        {
            return root != null && t != null && t.IsChildOf(root.transform);
        }

        // Every torch is a point light; dozens of them (a lit base at night) cost a lot to draw.
        // Only the nearest ones stay on, re-chosen twice a second.
        const int MaxLights = 12;
        static readonly List<Light> lights = new List<Light>();
        static readonly List<KeyValuePair<float, Light>> byDistance = new List<KeyValuePair<float, Light>>();
        static float nextLightPick;

        public static void PickLights()
        {
            if (Time.realtimeSinceStartup < nextLightPick) return;
            nextLightPick = Time.realtimeSinceStartup + 0.5f;
            lights.RemoveAll(l => l == null);
            if (lights.Count <= MaxLights)
            {
                for (int i = 0; i < lights.Count; i++) if (!lights[i].enabled) lights[i].enabled = true;
                return;
            }
            Camera cam = LocalPlayerSafe.Camera();
            Vector3 eye = cam != null ? cam.transform.position : Vector3.zero;
            byDistance.Clear();
            for (int i = 0; i < lights.Count; i++) byDistance.Add(new KeyValuePair<float, Light>((lights[i].transform.position - eye).sqrMagnitude, lights[i]));
            byDistance.Sort((a, b) => a.Key.CompareTo(b.Key));
            for (int i = 0; i < byDistance.Count; i++)
            {
                bool on = i < MaxLights;
                if (byDistance[i].Value.enabled != on) byDistance[i].Value.enabled = on;
            }
        }

        public static Material MaterialFor(long key)
        {
            Material m;
            if (materials.TryGetValue(key, out m) && m != null) return m;
            FindShaders();
            int packed = (int)(key >> 32);
            int layer = packed & 0xFF;
            int atlasId = (packed >> 8) & 0xFF;
            if (atlasId > 1) atlasId = 0;
            uint argb = (uint)(key & 0xFFFFFFFF);
            Shader shader = layer == 0 ? solidShader : layer == 2 && fadeShader != null ? fadeShader : cutoutShader;
            m = new Material(shader);
            m.mainTexture = atlases[atlasId];
            Color tint = argb == 0xFFFFFFFF ? Color.white : new Color(((argb >> 16) & 255) / 255f, ((argb >> 8) & 255) / 255f, (argb & 255) / 255f, 1f);
            m.color = tint;
            if (m.HasProperty("_Cutoff")) m.SetFloat("_Cutoff", 0.5f);
            if (shader.name == "Standard")
            {
                m.SetFloat("_Glossiness", 0f);
                m.SetFloat("_Metallic", 0f);
                if (layer != 0)
                {
                    m.SetFloat("_Mode", 1f);
                    m.EnableKeyword("_ALPHATEST_ON");
                    m.renderQueue = 2450;
                }
            }
            materials[key] = m;
            materialAtlas[m] = atlasId;
            return m;
        }

        static Shader First(params string[] names)
        {
            for (int i = 0; i < names.Length; i++)
            {
                Shader s = Shader.Find(names[i]);
                if (s != null) return s;
            }
            return null;
        }

        static void FindShaders()
        {
            if (solidShader != null) return;
            solidShader = First("Legacy Shaders/Diffuse", "Diffuse", "Standard", "Mobile/Diffuse");
            cutoutShader = First("Legacy Shaders/Transparent/Cutout/Diffuse", "Transparent/Cutout/Diffuse", "Standard", "Unlit/Transparent Cutout");
            // Translucent layer (water, stained glass, ice): real alpha blending when available.
            fadeShader = First("Legacy Shaders/Transparent/Diffuse", "Transparent/Diffuse", "Legacy Shaders/Transparent/Cutout/Diffuse", "Transparent/Cutout/Diffuse");
            if (solidShader == null) solidShader = cutoutShader;
            if (cutoutShader == null) cutoutShader = solidShader;
            if (!shadersLogged)
            {
                shadersLogged = true;
                Plugin.Log.LogInfo("ForestCraft: block shaders " + (solidShader != null ? solidShader.name : "none") + " / " + (cutoutShader != null ? cutoutShader.name : "none"));
            }
        }

        // Minecraft writes %LOCALAPPDATA%\ForestCraft\<file> and stamps OFF_MC+<stamp> when done.
        static void LoadAtlas(IntPtr view, int id, int stampOffset, string file)
        {
            int stamp = Marshal.ReadInt32(view, 0x200 + stampOffset);
            if (stamp == 0 || stamp == atlasStamps[id]) return;
            atlasStamps[id] = stamp;
            Texture2D tex = LoadTexture(file, atlases[id]);
            if (tex == null) { atlasStamps[id] = 0; return; }
            atlases[id] = tex;
            foreach (var pair in materialAtlas) if (pair.Key != null && pair.Value == id) pair.Key.mainTexture = tex;
            Plugin.Log.LogInfo("ForestCraft: " + file + " " + tex.width + "x" + tex.height + " loaded");
        }

        public static Texture2D LoadTexture(string file, Texture2D reuse)
        {
            string path = Path.Combine(Path.GetDirectoryName(Link.FilePath), file);
            try
            {
                byte[] all = File.ReadAllBytes(path);
                int w = BitConverter.ToInt32(all, 0);
                int h = BitConverter.ToInt32(all, 4);
                if (w <= 0 || h <= 0 || all.Length < 8 + w * h * 4) return null;
                Texture2D tex = reuse;
                if (tex == null || tex.width != w || tex.height != h)
                {
                    tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                    tex.filterMode = FilterMode.Point;
                    tex.wrapMode = TextureWrapMode.Clamp;
                    tex.anisoLevel = 0;
                }
                // Flip rows: Minecraft's first row is the top of the image.
                byte[] flipped = new byte[w * h * 4];
                int row = w * 4;
                for (int y = 0; y < h; y++) Buffer.BlockCopy(all, 8 + y * row, flipped, (h - 1 - y) * row, row);
                tex.LoadRawTextureData(flipped);
                tex.Apply(false);
                return tex;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("ForestCraft: " + file + " not readable yet: " + e.Message);
                return null;
            }
        }

        public static bool ItemModel(int id, out Mesh mesh, out Material[] mats)
        {
            mats = null;
            return itemMeshes.TryGetValue(id, out mesh) && mesh != null && itemMaterials.TryGetValue(id, out mats);
        }

        public static Texture2D Atlas(int id)
        {
            return id >= 0 && id < atlases.Length ? atlases[id] : null;
        }

        public static Shader CutoutShader()
        {
            FindShaders();
            return cutoutShader;
        }

        // ---- outline of the block Minecraft is aiming at, drawn in the scene (depth-tested) ----
        static Material lineMat;
        static bool outlineHooked;
        static bool hasHit;
        static Vector3 hitMin, hitMax;

        public static void ReadHit(IntPtr view)
        {
            hasHit = view != IntPtr.Zero && Link.Driving && Marshal.ReadInt32(view, 0x200 + 96) == 1;
            if (!hasHit) return;
            float k = Link.Scale;
            float x0 = Link.ReadMcFloat(100), y0 = Link.ReadMcFloat(104), z0 = Link.ReadMcFloat(108);
            float x1 = Link.ReadMcFloat(112), y1 = Link.ReadMcFloat(116), z1 = Link.ReadMcFloat(120);
            float e = 0.003f;
            hitMin = new Vector3((x0 - e) * k, (y0 - e) * k, -(z1 + e) * k);
            hitMax = new Vector3((x1 + e) * k, (y1 + e) * k, -(z0 - e) * k);
            if (!outlineHooked)
            {
                Camera.onPostRender += DrawOutline;
                outlineHooked = true;
            }
        }

        static void DrawOutline(Camera cam)
        {
            if (!hasHit || cam != LocalPlayerSafe.Camera()) return;
            if (lineMat == null)
            {
                Shader s = Shader.Find("Hidden/Internal-Colored");
                if (s == null) return;
                lineMat = new Material(s);
                lineMat.hideFlags = HideFlags.HideAndDontSave;
                lineMat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                lineMat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                lineMat.SetInt("_Cull", 0);
                lineMat.SetInt("_ZWrite", 0);
                lineMat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.LessEqual);
            }
            lineMat.SetPass(0);
            GL.PushMatrix();
            GL.Begin(GL.LINES);
            GL.Color(new Color(0f, 0f, 0f, 0.45f));
            Vector3 a = hitMin, b = hitMax;
            Vector3[] c =
            {
                new Vector3(a.x, a.y, a.z), new Vector3(b.x, a.y, a.z), new Vector3(b.x, a.y, b.z), new Vector3(a.x, a.y, b.z),
                new Vector3(a.x, b.y, a.z), new Vector3(b.x, b.y, a.z), new Vector3(b.x, b.y, b.z), new Vector3(a.x, b.y, b.z)
            };
            int[] edges = { 0,1, 1,2, 2,3, 3,0, 4,5, 5,6, 6,7, 7,4, 0,4, 1,5, 2,6, 3,7 };
            for (int i = 0; i < edges.Length; i++) GL.Vertex(c[edges[i]]);
            GL.End();
            GL.PopMatrix();
        }
    }
}
