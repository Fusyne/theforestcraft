using System.Collections.Generic;
using UnityEngine;
using TheForest.Utils;

namespace ForestCraft
{
    // Trees, the plane, rocks, cabins: everything the heightmap does not know about.
    // Each Minecraft block around the player is split into 4x4x4 quarter cells (64 bits).
    // A cell is solid when one of The Forest's colliders crosses it. Scanned as an octree
    // (empty block = 1 query), cached because the island does not move, nearest blocks first,
    // with a per-frame query budget so nothing hitches. Same idea as SkyCraft's 1/8-block field.
    static class Solids
    {
        const int Radius = 8;
        const int Below = 3;
        const int Above = 7;
        public const int SX = Radius * 2 + 1;
        public const int SY = Below + Above + 1;
        public const int SZ = Radius * 2 + 1;
        const int Budget = 1500;         // OverlapBox calls per frame
        const float Stale = 6f;          // rescan near blocks (doors, new walls) after this
        const float PublishEvery = 0.05f;

        public const int Words = 8;      // 8x8x8 eighth-cells = 512 bits per block, one ulong per Y layer
        struct Entry { public ulong[] mask; public float at; }

        static readonly Dictionary<long, Entry> cache = new Dictionary<long, Entry>();
        static readonly ulong[] region = new ulong[SX * SY * SZ * Words];
        static readonly Collider[] hits = new Collider[32];
        static readonly Dictionary<int, bool> verdict = new Dictionary<int, bool>();
        static int[] order;
        static float publishAt, forgetAt;
        static Transform player;
        static int queries;
        static int playerLayer = -1;

        // A tree fell or something moved: rescan everything around the player.
        // The ~300 blocks closest to the player (about 4 blocks around) are all scanned.
        // Minecraft only gets the body once this is true: otherwise it is dropped onto a floor
        // (the plane's) that does not exist for it yet, and falls through to the ground below.
        const int NearCount = 300;
        public static bool NearReady;

        static int missingNear(int ox, int oy, int oz)
        {
            int missing = 0;
            for (int n = 0; n < NearCount && n < order.Length; n++)
            {
                int i = order[n];
                if (!cache.ContainsKey(Key(ox + i % SX, oy + i / (SX * SZ), oz + (i / SX) % SZ))) missing++;
            }
            return missing;
        }

        public static void ForgetAll()
        {
            cache.Clear();
            verdict.Clear();
        }

        static void BuildOrder()
        {
            var list = new List<int>();
            for (int i = 0; i < SX * SY * SZ; i++) list.Add(i);
            list.Sort((a, b) => Dist(a).CompareTo(Dist(b)));
            order = list.ToArray();
        }

        static int Dist(int i)
        {
            int lx = i % SX - Radius;
            int lz = (i / SX) % SZ - Radius;
            int ly = i / (SX * SZ) - Below;
            return lx * lx + lz * lz + ly * ly * 2;
        }

        static long Key(int x, int y, int z)
        {
            return ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (long)(z & 0x1FFFFF);
        }

        public static void Step(double mcX, double mcY, double mcZ)
        {
            if (order == null) BuildOrder();
            float now = Time.realtimeSinceStartup;
            if (now > forgetAt)
            {
                verdict.Clear();
                forgetAt = now + 10f;
                if (cache.Count > 200000) cache.Clear();
            }
            player = LocalPlayer.Transform;
            if (playerLayer < 0)
            {
                FirstPersonCharacter fpc = LocalPlayer.FpCharacter;
                if (fpc != null && fpc.capsule != null) playerLayer = fpc.capsule.gameObject.layer;
                else if (player != null) playerLayer = player.gameObject.layer;
                if (playerLayer >= 0) Plugin.Log.LogInfo("ForestCraft: player layer " + playerLayer);
            }
            int ox = Mathf.FloorToInt((float)mcX) - Radius;
            int oy = Mathf.FloorToInt((float)mcY) - Below;
            int oz = Mathf.FloorToInt((float)mcZ) - Radius;
            queries = 0;
            // Before Minecraft has the body, scan much faster: the handover waits for this.
            // Before Minecraft has the body, or after a jump (respawn) until the new place is
            // known, scan much faster: Minecraft holds the player still meanwhile.
            int budget = Link.Driving && NearReady ? Budget : Budget * 8;
            for (int n = 0; n < order.Length; n++)
            {
                int i = order[n];
                int bx = ox + i % SX;
                int bz = oz + (i / SX) % SZ;
                int by = oy + i / (SX * SZ);
                long key = Key(bx, by, bz);
                Entry e;
                bool have = cache.TryGetValue(key, out e);
                bool near = n < 300;
                if (!have || (near && now - e.at > Stale))
                {
                    if (queries >= budget) { Put(i, have ? e.mask : null); continue; }
                    e.mask = Scan(bx, by, bz);
                    e.at = now;
                    cache[key] = e;
                }
                Put(i, e.mask);
            }
            NearReady = missingNear(ox, oy, oz) == 0;
            if (now >= publishAt)
            {
                publishAt = now + PublishEvery;
                Link.PublishSolids(ox, oy, oz, SX, SY, SZ, region);
            }
        }

        static void Put(int i, ulong[] mask)
        {
            int at = i * Words;
            for (int w = 0; w < Words; w++) region[at + w] = mask == null ? 0UL : mask[w];
        }

        static ulong[] scratch;

        // Minecraft block -> 512-bit mask (bit = y*64 + z*8 + x, in eighths), or null if empty.
        // Octree: an empty block costs one query; only cells near a surface go down to 1/8.
        static ulong[] Scan(int bx, int by, int bz)
        {
            if (!Any(bx, by, bz, 0, 0, 0, 8)) return null;
            scratch = new ulong[Words];
            Split(bx, by, bz, 0, 0, 0, 8);
            return scratch;
        }

        static void Split(int bx, int by, int bz, int x0, int y0, int z0, int size)
        {
            int half = size / 2;
            for (int dy = 0; dy < size; dy += half)
                for (int dz = 0; dz < size; dz += half)
                    for (int dx = 0; dx < size; dx += half)
                    {
                        int x = x0 + dx, y = y0 + dy, z = z0 + dz;
                        if (!Any(bx, by, bz, x, y, z, half)) continue;
                        if (half == 1) scratch[y] |= 1UL << (z * 8 + x);
                        else Split(bx, by, bz, x, y, z, half);
                    }
        }

        // Cube of `size` eighths starting at eighth (qx, qy, qz) inside the block.
        // Leaf cells are tested slightly smaller than they are, so a wall only fattens by
        // what it really touches: tight spots (the plane's door, the cabin) stay passable.
        static bool Any(int bx, int by, int bz, int qx, int qy, int qz, int size)
        {
            queries++;
            float s = size * 0.125f;
            float cx = bx + qx * 0.125f + s * 0.5f;
            float cy = by + qy * 0.125f + s * 0.5f;
            float cz = bz + qz * 0.125f + s * 0.5f;
            float k = Link.Scale;
            float h = (size == 1 ? s * 0.4f : s * 0.5f - 0.002f) * k;
            Vector3 center = new Vector3(cx * k, cy * k, -cz * k);
            int n = Physics.OverlapBoxNonAlloc(center, new Vector3(h, h, h), hits, Quaternion.identity, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                if (Solid(hits[i])) return true;
            }
            return false;
        }

        static bool Solid(Collider c)
        {
            if (c == null || !c.enabled || c.isTrigger) return false;
            int id = c.GetInstanceID();
            bool known;
            if (verdict.TryGetValue(id, out known)) return known;
            bool solid = Judge(c);
            verdict[id] = solid;
            if (solid && logged < 80 && seen.Add(id))
            {
                logged++;
                Plugin.Log.LogInfo("ForestCraft: solid " + c.GetType().Name + " '" + Path(c.transform) + "' layer " + c.gameObject.layer + " size " + c.bounds.size);
            }
            return solid;
        }

        static int logged;
        static readonly HashSet<int> seen = new HashSet<int>();

        static string Path(Transform t)
        {
            string p = t.name;
            if (t.parent != null) p = t.parent.name + "/" + p;
            return p;
        }

        static bool Judge(Collider c)
        {
            if (c is TerrainCollider) return false;      // the heightmap already covers it
            if (c is CharacterController) return false;  // creatures
            if (player != null && c.transform.IsChildOf(player)) return false;
            // The Forest's own collision matrix: rain blockers and the like never stop the player.
            if (playerLayer >= 0 && Physics.GetIgnoreLayerCollision(playerLayer, c.gameObject.layer)) return false;
            Rigidbody body = c.attachedRigidbody;
            if (body != null && !body.isKinematic) return false; // logs, items, ragdolls
            Vector3 size = c.bounds.size;
            if (Mathf.Max(size.x, Mathf.Max(size.y, size.z)) < 0.3f) return false; // sticks, small props
            return true;
        }
    }
}
