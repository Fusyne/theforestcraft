using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace ForestCraft
{
    // Minecraft's particles (splashes, drips, block dust, smoke...) in The Forest's scene: one
    // mesh rebuilt each Minecraft frame, depth-tested, so trees, walls and the ground hide them.
    // Lit like Minecraft does it: the brighter of the block light and the sky light (scaled by
    // The Forest's daylight), carried in the vertex colour.
    static class Particles
    {
        const int Off = 0xA38000;
        const int Quad = 8 + 4 * 28;
        const int MaxQuads = (0x28000 - 16) / Quad;

        static GameObject go;
        static Mesh mesh;
        static int lastSeq;
        static byte[] buffer;
        static Shader shader;
        static readonly Dictionary<int, Material> materials = new Dictionary<int, Material>();
        static readonly List<Vector3> verts = new List<Vector3>();
        static readonly List<Vector2> uvs = new List<Vector2>();
        static readonly List<Color32> colors = new List<Color32>();
        static readonly Dictionary<int, List<int>> groups = new Dictionary<int, List<int>>();
        static readonly List<int> used = new List<int>();
        static Material[] mats;

        public static void Update(IntPtr view)
        {
            if (view == IntPtr.Zero) return;
            if (!Link.Driving) { if (go != null && go.activeSelf) go.SetActive(false); return; }
            int seq = Marshal.ReadInt32(view, Off);
            if ((seq & 1) != 0 || seq == lastSeq) return;
            int count = Marshal.ReadInt32(view, Off + 4);
            if (count < 0 || count > MaxQuads) return;
            int bytes = 16 + count * Quad;
            if (buffer == null || buffer.Length < bytes) buffer = new byte[Math.Max(bytes, 4096)];
            Marshal.Copy(new IntPtr(view.ToInt64() + Off), buffer, 0, bytes);
            if (Marshal.ReadInt32(view, Off) != seq) return;
            lastSeq = seq;
            Build(count);
        }

        static readonly List<Vector3> normals = new List<Vector3>();
        static readonly Dictionary<long, List<int>> keyed = new Dictionary<long, List<int>>();
        static readonly List<long> usedKeys = new List<long>();

        // Drawn with the same materials as Minecraft's entities and blocks in The Forest (the
        // cutout diffuse ones that are known to render here, lit by The Forest's sun and lights).
        // The particle shaders found by name drew nothing in this game's build.
        static void Build(int count)
        {
            if (go == null)
            {
                go = new GameObject("ForestCraft Particles");
                UnityEngine.Object.DontDestroyOnLoad(go);
                mesh = new Mesh();
                mesh.MarkDynamic();
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var r = go.AddComponent<MeshRenderer>();
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
            }
            float k = Link.Scale;
            Camera cam = LocalPlayerSafe.Camera();
            Vector3 eye = cam != null ? cam.transform.position : Vector3.zero;
            verts.Clear(); uvs.Clear(); normals.Clear();
            foreach (var list in keyed.Values) list.Clear();
            for (int q = 0; q < count; q++)
            {
                int o = 16 + q * Quad;
                int material = BitConverter.ToInt32(buffer, o);
                int argb = BitConverter.ToInt32(buffer, o + 8 + 20);
                if (((argb >> 24) & 255) < 24) continue; // faded out
                // Colour, rounded so a handful of materials cover every particle.
                uint color = Round((argb >> 16) & 255) << 16 | Round((argb >> 8) & 255) << 8 | Round(argb & 255);
                color |= 0xFF000000u;
                long key = ((long)material << 32) | color;
                List<int> tris;
                if (!keyed.TryGetValue(key, out tris)) { tris = new List<int>(); keyed[key] = tris; }
                int b = verts.Count;
                for (int i = 0; i < 4; i++)
                {
                    int v = o + 8 + i * 28;
                    float x = BitConverter.ToSingle(buffer, v), y = BitConverter.ToSingle(buffer, v + 4), z = BitConverter.ToSingle(buffer, v + 8);
                    verts.Add(new Vector3(x * k, y * k, -z * k));
                    uvs.Add(new Vector2(BitConverter.ToSingle(buffer, v + 12), 1f - BitConverter.ToSingle(buffer, v + 16)));
                }
                Vector3 c = (verts[b] + verts[b + 2]) * 0.5f;
                Vector3 n = eye - c;
                n = n.sqrMagnitude > 1e-6f ? n.normalized : Vector3.up;
                for (int i = 0; i < 4; i++) normals.Add(n);
                // Both windings: a billboard is seen from the front whatever way it was wound.
                tris.Add(b); tris.Add(b + 1); tris.Add(b + 2); tris.Add(b); tris.Add(b + 2); tris.Add(b + 3);
                tris.Add(b); tris.Add(b + 2); tris.Add(b + 1); tris.Add(b); tris.Add(b + 3); tris.Add(b + 2);
            }
            mesh.Clear();
            if (verts.Count == 0) { if (go.activeSelf) go.SetActive(false); return; }
            mesh.SetVertices(verts);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            usedKeys.Clear();
            foreach (var pair in keyed) if (pair.Value.Count > 0) usedKeys.Add(pair.Key);
            mesh.subMeshCount = usedKeys.Count;
            if (mats == null || mats.Length != usedKeys.Count) mats = new Material[usedKeys.Count];
            for (int i = 0; i < usedKeys.Count; i++)
            {
                mesh.SetTriangles(keyed[usedKeys[i]], i);
                mats[i] = Entities.MaterialFor(usedKeys[i]);
            }
            // Positions are world coordinates: bounds big enough never to be culled wrongly.
            mesh.bounds = new Bounds(eye, Vector3.one * 2000f);
            go.GetComponent<MeshRenderer>().sharedMaterials = mats;
            if (!go.activeSelf) go.SetActive(true);
            if (!logged) { logged = true; Plugin.Log.LogInfo("ForestCraft: particles drawn in The Forest (" + verts.Count / 4 + " quads, " + usedKeys.Count + " materials)"); }
        }

        static bool logged;

        static uint Round(int c)
        {
            int r = ((c + 16) >> 5) << 5;
            return (uint)(r > 255 ? 255 : r);
        }

        static Material MaterialFor(int texture)
        {
            Material m;
            if (!materials.TryGetValue(texture, out m) || m == null)
            {
                if (shader == null)
                {
                    // Alpha blended, vertex coloured, both sides, no depth write: like Minecraft's.
                    foreach (string n in new[] { "Legacy Shaders/Particles/Alpha Blended", "Particles/Alpha Blended", "Mobile/Particles/Alpha Blended", "Sprites/Default" })
                    {
                        shader = Shader.Find(n);
                        if (shader != null) break;
                    }
                    Plugin.Log.LogInfo("ForestCraft: particle shader " + (shader != null ? shader.name : "none"));
                    if (shader == null) shader = Blocks.CutoutShader();
                }
                m = new Material(shader);
                if (m.HasProperty("_TintColor")) m.SetColor("_TintColor", new Color(0.5f, 0.5f, 0.5f, 0.5f));
                // Soft particles (on in high quality) fade a particle out as it nears a surface,
                // over a whole unit: block dust is born right at the ground and vanished. Keep the
                // fade to a hair, so particles only blend where they really touch.
                if (m.HasProperty("_InvFade")) m.SetFloat("_InvFade", 100f);
                materials[texture] = m;
            }
            if (m.mainTexture == null) m.mainTexture = Entities.Texture(texture);
            return m;
        }
    }
}
