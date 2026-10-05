using System.Collections.Generic;
using UnityEngine;
using TheForest.Utils;

namespace ForestCraft
{
    // The caves are under the island's heightmap: down there the ground over your head is not
    // the floor. While The Forest says the player is in the caves, or the feet are in one of the
    // entrance holes (where The Forest itself turns the terrain's collision off), Minecraft drops
    // the heightmap ground and walks on the cave meshes only (Solids sends those).
    // Written at OFF_FOREST+212: bit 1 in the caves, bit 2 in an entrance.
    static class Caves
    {
        static readonly List<Collider> entrances = new List<Collider>();
        static float nextScan;
        static bool logged;
        public static int Flags;
        public static bool TerrainIgnored;
        static float forgetAgainAt = -1f;

        // The Forest notices a player going into a cave through trigger volumes (CaveTriggers)
        // touched by its physics capsule. While Minecraft moves the body those never fire, so
        // the game never learnt the player was in a cave: the cave floors stayed switched off and
        // Minecraft kept the island's ground (above your head) as the floor. Do what
        // OnTriggerEnter would: hand the player to every cave trigger the body overlaps, and let
        // the trigger's own Update decide (in, out, which cave) exactly as in the game.
        static readonly Collider[] touching = new Collider[64];
        static readonly Dictionary<int, CaveTriggers> triggerOf = new Dictionary<int, CaveTriggers>();
        static readonly HashSet<int> announced = new HashSet<int>();

        static void FeedTriggers(Vector3 feet)
        {
            if (!Link.Driving) return;
            Transform p = LocalPlayer.Transform;
            if (p == null) return;
            float h = Mathf.Max(Drive.ForestHeight(), 2f);
            const float r = 0.8f;
            int n = Physics.OverlapCapsuleNonAlloc(feet + Vector3.up * r, feet + Vector3.up * (h - r), r, touching, ~0, QueryTriggerInteraction.Collide);
            if (triggerOf.Count > 4000) triggerOf.Clear();
            for (int i = 0; i < n; i++)
            {
                Collider c = touching[i];
                if (c == null || !c.isTrigger) continue;
                int id = c.GetInstanceID();
                CaveTriggers t;
                if (!triggerOf.TryGetValue(id, out t))
                {
                    t = c.GetComponent<CaveTriggers>();
                    if (t == null && c.attachedRigidbody != null) t = c.attachedRigidbody.GetComponent<CaveTriggers>();
                    triggerOf[id] = t;
                }
                if (t == null || t.IsEntryControlled) continue;
                if (t.playersEnteringTrigger.Contains(p) || t.playersExitingTrigger.Contains(p)) continue;
                t.playersEnteringTrigger.Add(p);
                t.enabled = true;
                if (announced.Add(t.GetInstanceID()))
                    Plugin.Log.LogInfo("ForestCraft: going through cave trigger '" + t.name + "'" + (t.IsOutsideArea ? " (outside)" : "") + (t.climbEntrance ? " (climb)" : ""));
            }
        }

        // Walking into something in a cave and not moving: say what is in the way, once per spot.
        static Vector3 stillAt;
        static float stillSince;
        static readonly HashSet<long> reported = new HashSet<long>();

        static void ReportBlocked(Vector3 feet)
        {
            bool walking = UnityEngine.Input.GetKey(KeyCode.W) || UnityEngine.Input.GetKey(KeyCode.Z) || UnityEngine.Input.GetKey(KeyCode.S)
                || UnityEngine.Input.GetKey(KeyCode.A) || UnityEngine.Input.GetKey(KeyCode.Q) || UnityEngine.Input.GetKey(KeyCode.D);
            float now = Time.realtimeSinceStartup;
            Terrain land = Terrain.activeTerrain;
            bool under = land != null && feet.y < land.SampleHeight(feet) + land.transform.position.y - 1f;
            if (!Link.Driving || (Flags == 0 && !under) || !walking || (feet - stillAt).sqrMagnitude > 0.15f * 0.15f)
            {
                stillAt = feet;
                stillSince = now;
                return;
            }
            if (now - stillSince < 1.5f) return;
            stillSince = now + 1000f;
            long key = ((long)Mathf.FloorToInt(feet.x / 3f) << 40) ^ ((long)Mathf.FloorToInt(feet.y / 3f) << 20) ^ (long)Mathf.FloorToInt(feet.z / 3f);
            if (!reported.Add(key) || reported.Count > 50) return;
            float k = Link.Scale;
            Vector3 half = new Vector3(0.3f + 0.25f, 0.9f, 0.3f + 0.25f) * k;
            Plugin.Log.LogInfo("ForestCraft: stuck in a cave at " + feet + " (flags " + Flags + ", under the ground " + under + ", in caves " + LocalPlayer.IsInCaves + "), around the body:"
                + Solids.Describe(feet + Vector3.up * half.y, half));
        }

        // Died in a cave and respawned outside: the body jumped there without walking out through
        // the cave's triggers, so The Forest still believed it was in the cave (terrain collision
        // off, cave floors on) and Minecraft fell through the island's ground. After any jump,
        // once the feet are on or above the island's surface, walk the player out of the caves the
        // way the exit trigger would.
        static Vector3 lastFeet;
        static float jumpedAt = -100f;

        static void LeaveAfterJump(Vector3 feet)
        {
            float now = Time.realtimeSinceStartup;
            if ((feet - lastFeet).sqrMagnitude > 8f * 8f) jumpedAt = now;
            lastFeet = feet;
            if (now - jumpedAt > 5f) return;
            bool inCaves = false;
            try { inCaves = LocalPlayer.IsInCaves; } catch { }
            if (!inCaves && !TerrainIgnored) return;
            Terrain land = Terrain.activeTerrain;
            if (land == null || feet.y < land.SampleHeight(feet) + land.transform.position.y - 0.5f) return;
            jumpedAt = -100f;
            Transform p = LocalPlayer.Transform;
            foreach (CaveTriggers t in Object.FindObjectsOfType<CaveTriggers>())
            {
                if (t == null) continue;
                t.playersEnteringTrigger.Remove(p);
                t.playersExitingTrigger.Remove(p);
            }
            try { LocalPlayer.ActiveAreaInfo.SetCurrentCave(CaveNames.NotInCaves); } catch { }
            if (LocalPlayer.Stats != null) LocalPlayer.Stats.NotInACave();
            TerrainIgnored = false;
            Plugin.Log.LogInfo("ForestCraft: respawned outside, out of the caves");
        }

        // Fell more than 3 units within half a second: say what was under the feet just before,
        // once per spot (a floor Minecraft didn't get, like the plane's).
        static Vector3 sampleFeet;
        static float sampleAt;
        static readonly HashSet<long> fallsReported = new HashSet<long>();

        static void ReportFall(Vector3 feet)
        {
            float now = Time.realtimeSinceStartup;
            if (!Link.Driving) { sampleAt = now; sampleFeet = feet; return; }
            if (feet.y < sampleFeet.y - 3f && (feet - sampleFeet).sqrMagnitude < 15f * 15f)
            {
                long key = ((long)Mathf.FloorToInt(sampleFeet.x / 3f) << 40) ^ ((long)Mathf.FloorToInt(sampleFeet.y / 3f) << 20) ^ (long)Mathf.FloorToInt(sampleFeet.z / 3f);
                if (fallsReported.Count < 30 && fallsReported.Add(key))
                {
                    float k = Link.Scale;
                    Vector3 half = new Vector3(0.45f, 0.4f, 0.45f) * k;
                    Plugin.Log.LogInfo("ForestCraft: fell from " + sampleFeet + " to " + feet + " (cave flags " + Flags + "), under the feet before:"
                        + Solids.Describe(sampleFeet + Vector3.down * (0.3f * k), half));
                }
                sampleFeet = feet; sampleAt = now;
                return;
            }
            if (now - sampleAt >= 0.5f) { sampleAt = now; sampleFeet = feet; }
        }

        public static void Update(Vector3 feet)
        {
            try { ReportFall(feet); } catch { }
            try { LeaveAfterJump(feet); } catch (System.Exception e) { Plugin.Log.LogWarning("ForestCraft: leaving the caves: " + e.Message); }
            try { ReportBlocked(feet); } catch { }
            try { FeedTriggers(feet); } catch (System.Exception e) { Plugin.Log.LogWarning("ForestCraft: cave triggers: " + e.Message); }
            int f = 0;
            try { if (LocalPlayer.IsInCaves) f |= 1; } catch { }
            if (Time.realtimeSinceStartup >= nextScan)
            {
                nextScan = Time.realtimeSinceStartup + 10f;
                entrances.Clear();
                foreach (ResolveHoleCollision r in Object.FindObjectsOfType<ResolveHoleCollision>())
                {
                    if (r == null || r.entranceTriggers == null) continue;
                    foreach (Collider c in r.entranceTriggers) if (c != null && !entrances.Contains(c)) entrances.Add(c);
                }
                if (!logged && entrances.Count > 0) { logged = true; Plugin.Log.LogInfo("ForestCraft: " + entrances.Count + " cave entrances known"); }
            }
            // The Forest itself lets its player through the island's ground (cave mouths,
            // holes): then so does Minecraft.
            if (TerrainIgnored) f |= 2;
            Ropes.Update(feet);
            if (Ropes.Near) f |= 2; // down a rope The Forest's player goes through the ground too
            for (int i = 0; i < entrances.Count; i++)
            {
                Collider c = entrances[i];
                if (c == null) continue;
                Bounds b = c.bounds;
                b.Expand(3f);
                if (b.Contains(feet)) { f |= 2; break; }
            }
            if (f != Flags) Plugin.Log.LogInfo("ForestCraft: " + ((f & 1) != 0 ? "in the caves" : (f & 2) != 0 ? "at a cave entrance" : "back outside"));
            // In or out of the caves, The Forest switches the cave floors on or off (and the
            // terrain's collision): what Solids had cached around here is wrong now. Rescan, and
            // once more a moment later in case some of it switches on a frame late.
            float now = Time.realtimeSinceStartup;
            if (f != Flags) { Solids.MarkStale(); forgetAgainAt = now + 0.5f; }
            else if (forgetAgainAt > 0f && now >= forgetAgainAt) { Solids.MarkStale(); forgetAgainAt = -1f; }
            Flags = f;
            Link.WriteIntAt(0x100 + 212, f);
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(PlayerStats), "IgnoreCollisionWithTerrain")]
    static class PatchIgnoreTerrain
    {
        static void Postfix(bool onoff) { Caves.TerrainIgnored = onoff; }
    }

    // Ropes and climbable walls (activateClimb at the bottom, activateClimbTop at the top): in
    // Minecraft they climb like ladders, and next to one the island's ground doesn't stop you
    // (the cave holes the ropes hang in). OFF_ROPES: count, then (x, z, y0, y1) Minecraft units.
    static class Ropes
    {
        const int Off = 0xA36500;
        const int Max = 64;
        struct Column { public Vector3 a, b; }
        static readonly List<Column> all = new List<Column>();
        static float nextScan;
        static bool logged;
        public static bool Near;

        public static void Update(Vector3 feet)
        {
            if (Time.realtimeSinceStartup >= nextScan)
            {
                nextScan = Time.realtimeSinceStartup + 10f;
                all.Clear();
                foreach (activateClimb c in Object.FindObjectsOfType<activateClimb>())
                {
                    if (c == null) continue;
                    activateClimbTop top = c.transform.parent != null ? c.transform.parent.GetComponentInChildren<activateClimbTop>(true) : null;
                    Vector3 p = c.transform.position, q = top != null ? top.transform.position : p + Vector3.up * 3f;
                    all.Add(new Column { a = p, b = q });
                }
                if (!logged && all.Count > 0) { logged = true; Plugin.Log.LogInfo("ForestCraft: " + all.Count + " ropes and climbable walls known"); }
            }
            Near = false;
            float k = Link.Scale;
            int n = 0;
            int at = Off + 16;
            for (int i = 0; i < all.Count && n < Max; i++)
            {
                Column c = all[i];
                float x = (c.a.x + c.b.x) * 0.5f, z = (c.a.z + c.b.z) * 0.5f;
                float y0 = Mathf.Min(c.a.y, c.b.y) - 1f, y1 = Mathf.Max(c.a.y, c.b.y) + 1.5f;
                float dx = x - feet.x, dz = z - feet.z;
                float d2 = dx * dx + dz * dz;
                if (d2 > 60f * 60f) continue;
                // Only right at the rope: a climbable cliff on the surface must not open the ground around it.
                if (d2 < 2f * 2f && feet.y > y0 - 2f && feet.y < y1 + 2f) Near = true;
                Link.WriteFloatAt(at, x / k);
                Link.WriteFloatAt(at + 4, -z / k);
                Link.WriteFloatAt(at + 8, y0 / k);
                Link.WriteFloatAt(at + 12, y1 / k);
                at += 16;
                n++;
            }
            Link.WriteIntAt(Off, n);
        }
    }
}
