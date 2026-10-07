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
        static float ownHopUntil = -100f;

        /// <summary>ForestCraft itself sends the body across a cave mouth or door: not a respawn.</summary>
        public static void OwnHop() { ownHopUntil = Time.realtimeSinceStartup + 3f; jumpedAt = -100f; Link.OwnMove(); }

        static void LeaveAfterJump(Vector3 feet)
        {
            float now = Time.realtimeSinceStartup;
            // Our own hops through cave mouths are no respawn: going in must keep the player in.
            if (now < ownHopUntil) { lastFeet = feet; jumpedAt = -100f; return; }
            if ((feet - lastFeet).sqrMagnitude > 8f * 8f) jumpedAt = now;
            lastFeet = feet;
            if (now - jumpedAt > 5f) return;
            bool inCaves = false;
            try { inCaves = LocalPlayer.IsInCaves; } catch { }
            if (!inCaves && !TerrainIgnored) return;
            Terrain land = Terrain.activeTerrain;
            if (land == null || feet.y < land.SampleHeight(feet) + land.transform.position.y - 0.5f) return;
            jumpedAt = -100f;
            ExitCaves();
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

        // Just out of the caves (The Forest's exit trigger said so) but the feet still under the
        // island's heightmap (the cave mouth runs under the hill): Minecraft would put the
        // island's ground back around the body and wall it in. Until the feet are back up on the
        // surface (or far from where the cave was left), Minecraft leaves out the island's ground
        // level with the body and above it (bit 4, feet height at +244), and keeps what is under
        // the feet, so it can't fall through the island either.
        static bool leaving;
        static Vector3 leftAt;

        static bool UnderSurface(Vector3 feet, float margin)
        {
            Terrain land = Terrain.activeTerrain;
            return land != null && feet.y < land.SampleHeight(feet) + land.transform.position.y - margin;
        }

        /// <summary>F8 > Joueur: up onto the island's surface, out of the caves (stuck somewhere).</summary>
        public static void Unstick()
        {
            Vector3 feet = Drive.LastFeet;
            Terrain land = Terrain.activeTerrain;
            float k = Link.Scale;
            float up = 1f;
            if (land != null)
            {
                float surface = land.SampleHeight(feet) + land.transform.position.y;
                if (surface > feet.y) up += (surface - feet.y) / k;
            }
            ExitCaves();
            leaving = false;
            DevConsole.SendToMinecraft("quiet:tp @s ~ ~" + up.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " ~");
            Plugin.Log.LogInfo("ForestCraft: unstuck: up " + up.ToString("0.0") + " blocks, out of the caves");
        }

        // What The Forest's exit trigger does: the player is not in a cave any more.
        static void ExitCaves()
        {
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
        }

        public static void Update(Vector3 feet, int input)
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
            Ropes.Update(feet, input);
            if (Ropes.Through) f |= 2; // up or down a rope through the island's ground
            if ((Flags & 1) != 0 && (f & 1) == 0 && Link.Driving)
            {
                leaving = true;
                leftAt = feet;
            }
            if (leaving)
            {
                if ((f & 1) != 0) leaving = false;
                else if (!UnderSurface(feet, 0.05f) || (feet - leftAt).sqrMagnitude > 60f * 60f)
                {
                    leaving = false;
                    Plugin.Log.LogInfo("ForestCraft: out of the cave mouth, on the island's ground again");
                }
                else
                {
                    f |= 4;
                    Link.WriteFloatAt(0x100 + 244, feet.y / Link.Scale);
                }
            }
            for (int i = 0; i < entrances.Count; i++)
            {
                Collider c = entrances[i];
                if (c == null) continue;
                Bounds b = c.bounds;
                b.Expand(3f);
                if (b.Contains(feet)) { f |= 2; break; }
            }
            if (f != Flags) Plugin.Log.LogInfo("ForestCraft: " + ((f & 1) != 0 ? "in the caves" : (f & 2) != 0 ? (Ropes.Through ? "on a rope through the ground" : "at a cave entrance") : (f & 4) != 0 ? "out of the caves, still under the island's ground (cave mouth)" : "back outside")
                + " at " + feet.ToString("F1"));
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

    // The Forest's cave doors (CaveTriggers.CaveDoorRoutine): a fade to black, then the player
    // is put a few units across the doorway. While Minecraft has the body: no fade, Minecraft is
    // sent across at once (the same hop, relative) and The Forest is told in/out of the cave.
    // A short pause after each crossing: until Minecraft has jumped, the body is still in the
    // doorway, and must not be sent back through it.
    [HarmonyLib.HarmonyPatch(typeof(CaveTriggers), "CaveDoorRoutine")]
    static class PatchCaveDoor
    {
        static float lastCross = -100f;

        static bool Prefix(CaveTriggers __instance, ref System.Collections.IEnumerator __result)
        {
            if (!Link.Driving) return true;
            __result = Nothing();
            float now = Time.realtimeSinceStartup;
            if (now - lastCross < 1.5f) return false;
            Transform player = LocalPlayer.Transform;
            if (player == null) return false;
            lastCross = now;
            Transform door = __instance.transform;
            Vector3 local = door.InverseTransformPoint(player.position);
            bool entering = local.z < 0f;
            local.z *= entering ? -1.25f : -1.5f;
            Vector3 hop = door.TransformPoint(local) - player.position;
            player.gameObject.SendMessage(entering ? "InACave" : "NotInACave", SendMessageOptions.DontRequireReceiver);
            bool onRope = false;
            try { onRope = LocalPlayer.AnimControl.onRope; } catch { }
            if (!onRope)
            {
                float k = Link.Scale;
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                Caves.OwnHop();
                DevConsole.SendToMinecraft("quiet:tp @s ~" + (hop.x / k).ToString("0.000", ci) + " ~" + (hop.y / k).ToString("0.000", ci) + " ~" + (-hop.z / k).ToString("0.000", ci));
            }
            Plugin.Log.LogInfo("ForestCraft: cave door '" + door.name + "': " + (entering ? "in" : "out") + ", Minecraft sent " + hop.magnitude.ToString("0.0") + " units across, no fade");
            return false;
        }

        static System.Collections.IEnumerator Nothing() { yield break; }
    }

    // The Forest's cave mouths with an "E" (activateCave -> playerEnterCaveAction.doCave): The
    // Forest takes the body for a squeeze-through animation (its own arms along the rock) and its
    // root motion carries the player in or out. While Minecraft has the body: no animation, the
    // player is sent straight to the other side, where the way back starts (the partner
    // activateCave's spot: inside for an entry, outside for an exit), and The Forest is told
    // in/out of the caves as the animation would.
    [HarmonyLib.HarmonyPatch(typeof(playerEnterCaveAction), "doCave")]
    static class PatchEnterCave
    {
        static bool Prefix(playerEnterCaveAction __instance, GameObject posGo, bool enter, ref System.Collections.IEnumerator __result)
        {
            if (!Link.Driving || posGo == null) return true;
            Transform player = LocalPlayer.Transform;
            if (player == null) return true;
            bool timmy = HarmonyLib.Traverse.Create(__instance).Field("timmyCutscene").GetValue<bool>();
            if (timmy) return true; // the story's own cutscene: left as it is
            Vector3 from = posGo.transform.position;
            Transform dest = Partner(posGo, enter);
            Vector3 to = dest != null ? dest.position : from + posGo.transform.forward * 4f;
            // Going out, the spot outside is right against the rock of the mouth: a step further
            // out (the way from the inside spot to it), so the body isn't wedged in the wall.
            if (!enter && dest != null)
            {
                Vector3 outward = to - from;
                outward.y = 0f;
                if (outward.sqrMagnitude > 0.01f) to += outward.normalized * 1.5f;
            }
            __result = Nothing();
            bool ignoreLighting = HarmonyLib.Traverse.Create(__instance).Field("ignoreLighting").GetValue<bool>();
            if (!ignoreLighting)
                LocalPlayer.GameObject.SendMessage(enter ? "InACave" : "NotInACave", SendMessageOptions.DontRequireReceiver);
            // Minecraft's feet go where The Forest's would stand at the other side.
            // The spot is where The Forest's feet stand (its animation starts there).
            Vector3 hop = to - Drive.LastFeet;
            float k = Link.Scale;
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            Caves.OwnHop();
            DevConsole.SendToMinecraft("quiet:tp @s ~" + (hop.x / k).ToString("0.000", ci) + " ~" + ((hop.y + 0.05f) / k).ToString("0.000", ci) + " ~" + (-hop.z / k).ToString("0.000", ci));
            Plugin.Log.LogInfo("ForestCraft: cave " + (enter ? "entry" : "exit") + " at " + from.ToString("F1") + ": no animation, Minecraft sent to "
                + to.ToString("F1") + (dest != null ? " ('" + dest.name + "')" : " (no other side found: straight ahead)"));
            return false;
        }

        // The other side of this cave mouth: the activateCave facing the other way nearest to it.
        static Transform Partner(GameObject posGo, bool enter)
        {
            Transform best = null;
            float bestD = 40f * 40f;
            foreach (activateCave c in Resources.FindObjectsOfTypeAll<activateCave>())
            {
                if (c == null || !c.gameObject.scene.IsValid() || c.entry == enter) continue;
                GameObject spot = enter ? c.exitPos : c.enterPos;
                if (spot == null || spot == posGo) continue;
                float d = (spot.transform.position - posGo.transform.position).sqrMagnitude;
                if (d < bestD) { bestD = d; best = spot.transform; }
            }
            return best;
        }

        static System.Collections.IEnumerator Nothing() { yield break; }
    }

    // The Forest's ropes, taken with E (PlayerClimbRopeAction.enterClimbRope at the bottom,
    // enterClimbRopeTop at the top): The Forest takes the body for its climbing animation. While
    // Minecraft has the body: no animation, the player is sent to the other end of the rope (on
    // the ground beside the top, a step away from the hole; or at the foot of it), and when the
    // rope goes through the island's ground into the caves, The Forest is told in/out of them as
    // the cave triggers along the rope would.
    static class RopeHop
    {
        static float lastHop = -100f;

        public static bool Go(Transform trn, bool fromTop)
        {
            if (!Link.Driving || trn == null || trn.parent == null) return true;
            float now = Time.realtimeSinceStartup;
            if (now - lastHop < 1.5f) return false;
            activateClimbTop top = fromTop ? trn.GetComponent<activateClimbTop>() : trn.parent.GetComponentInChildren<activateClimbTop>(true);
            activateClimb bottom = fromTop ? trn.parent.GetComponentInChildren<activateClimb>(true) : trn.GetComponent<activateClimb>();
            if (top == null || bottom == null) return true; // not a rope we know the two ends of
            lastHop = now;
            Vector3 to;
            if (fromTop) to = BottomSpot(bottom.transform);
            else to = TopSpot(top.transform, bottom.transform);
            // Through the island's ground: in or out of the caves.
            bool hole = UnderSurface(bottom.transform.position, 2f) && !UnderSurface(top.transform.position, 1f);
            if (hole)
            {
                if (fromTop)
                {
                    CaveNames cave = NearestCave(bottom.transform.position);
                    try { LocalPlayer.ActiveAreaInfo.SetCurrentCave(cave); } catch { }
                    LocalPlayer.GameObject.SendMessage("InACave", SendMessageOptions.DontRequireReceiver);
                }
                else
                {
                    try { LocalPlayer.ActiveAreaInfo.SetCurrentCave(CaveNames.NotInCaves); } catch { }
                    LocalPlayer.GameObject.SendMessage("NotInACave", SendMessageOptions.DontRequireReceiver);
                }
            }
            Vector3 hop = to - Drive.LastFeet;
            float k = Link.Scale;
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            Caves.OwnHop();
            DevConsole.SendToMinecraft("quiet:tp @s ~" + (hop.x / k).ToString("0.000", ci) + " ~" + ((hop.y + 0.05f) / k).ToString("0.000", ci) + " ~" + (-hop.z / k).ToString("0.000", ci));
            Plugin.Log.LogInfo("ForestCraft: rope " + (fromTop ? "down" : "up") + " without the climbing animation: Minecraft sent to " + to.ToString("F1") + (hole ? (fromTop ? ", into the caves" : ", out of the caves") : ""));
            return false;
        }

        // Up at the top: the first spot, going away from the rope over the ledge, with a floor
        // about level with the top of the rope and room for the whole body (not inside the rock
        // around the hole, which is where the top of a short rope in a cave often is).
        static Vector3 TopSpot(Transform top, Transform bottom)
        {
            Vector3 t = top.position;
            Vector3 away = t - bottom.position;
            away.y = 0f;
            if (away.sqrMagnitude < 0.3f * 0.3f) { away = -top.forward; away.y = 0f; }
            if (away.sqrMagnitude < 1e-4f) away = Vector3.forward;
            away.Normalize();
            float h = Mathf.Max(Drive.ForestHeight(), 2f);
            const float r = 0.45f;
            float[] turns = { 0f, 30f, -30f, 60f, -60f, 90f, -90f, 120f, -120f, 150f, -150f, 180f };
            int noFloor = 0, tooFar = 0, blocked = 0;
            string blocker = null;
            for (float d = 0.6f; d <= 6.01f; d += 0.4f)
            {
                foreach (float turn in turns)
                {
                    Vector3 dir = Quaternion.Euler(0f, turn, 0f) * away;
                    Vector3 p = t + dir * d;
                    Vector3 floor;
                    if (!FloorAt(p, t.y + 4.5f, out floor)) { noFloor++; continue; }
                    // A ledge up to a body's height above the top of the rope (the planks or the
                    // rock it is tied to), and not below it (that is down the hole again).
                    if (floor.y < t.y - 1.5f || floor.y > t.y + 4f) { tooFar++; continue; }
                    string what = Blocker(floor, h, r);
                    if (what == null) return floor;
                    blocked++;
                    if (blocker == null) blocker = what;
                }
            }
            Plugin.Log.LogInfo("ForestCraft: no free spot found at the top of the rope (no floor " + noFloor + ", too high or low " + tooFar
                + ", no room " + blocked + (blocker != null ? ", first in the way: " + blocker : "") + "): next to it");
            return Floor(t + away * 1.2f);
        }

        // Down at the foot of the rope: the nearest spot with a floor about level with the
        // bottom of the rope and room for the body.
        static Vector3 BottomSpot(Transform bottom)
        {
            Vector3 b = bottom.position;
            float h = Mathf.Max(Drive.ForestHeight(), 2f);
            for (float d = 0f; d <= 4.01f; d += 0.5f)
            {
                for (int i = 0; i < (d == 0f ? 1 : 8); i++)
                {
                    Vector3 p = b + Quaternion.Euler(0f, i * 45f, 0f) * (-bottom.forward) * d;
                    Vector3 floor;
                    if (!FloorAt(p, b.y + 2.5f, out floor) || Mathf.Abs(floor.y - b.y) > 2.5f) continue;
                    if (Blocker(floor, h, 0.45f) == null) return floor;
                }
            }
            Plugin.Log.LogInfo("ForestCraft: no free spot found at the foot of the rope: on it");
            return Floor(b);
        }

        // The highest floor under this spot, from a given height down.
        static bool FloorAt(Vector3 spot, float fromY, out Vector3 floor)
        {
            Transform player = LocalPlayer.Transform;
            Vector3 from = new Vector3(spot.x, fromY, spot.z);
            RaycastHit[] hits = Physics.RaycastAll(from, Vector3.down, fromY - spot.y + 3f, ~0, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            floor = spot;
            foreach (RaycastHit hit in hits)
            {
                if (hit.collider == null || (player != null && hit.collider.transform.IsChildOf(player))) continue;
                if (hit.distance < 0.05f) continue; // the ray started inside it: rock, not a floor
                if (hit.distance < best) { best = hit.distance; floor = hit.point; }
            }
            return best < float.MaxValue;
        }

        // What is in the way of a body standing here, or null.
        static string Blocker(Vector3 feet, float h, float r)
        {
            Transform player = LocalPlayer.Transform;
            Collider[] around = Physics.OverlapCapsule(feet + Vector3.up * (r + 0.2f), feet + Vector3.up * (h - r), r, ~0, QueryTriggerInteraction.Ignore);
            foreach (Collider c in around)
            {
                if (c == null || (player != null && c.transform.IsChildOf(player))) continue;
                if (c is TerrainCollider) continue; // the island's ground: off in the caves, and under the feet outside
                if (c.attachedRigidbody != null && !c.attachedRigidbody.isKinematic) continue; // logs, items: pushed aside
                return (c.transform.parent != null ? c.transform.parent.name + "/" : "") + c.name;
            }
            return null;
        }

        // Where feet stand at this spot: the first solid surface below (just above it).
        static Vector3 Floor(Vector3 spot)
        {
            Transform player = LocalPlayer.Transform;
            RaycastHit[] hits = Physics.RaycastAll(spot + Vector3.up * 2.5f, Vector3.down, 12f, ~0, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            Vector3 at = spot;
            foreach (RaycastHit h in hits)
            {
                if (h.collider == null || (player != null && h.collider.transform.IsChildOf(player))) continue;
                if (h.distance < best) { best = h.distance; at = h.point; }
            }
            return at;
        }

        static bool UnderSurface(Vector3 p, float margin)
        {
            Terrain land = Terrain.activeTerrain;
            return land != null && p.y < land.SampleHeight(p) + land.transform.position.y - margin;
        }

        static CaveNames NearestCave(Vector3 p)
        {
            CaveNames best = CaveNames.NotInCaves;
            float bestD = 40f * 40f;
            foreach (CaveTriggers t in Resources.FindObjectsOfTypeAll<CaveTriggers>())
            {
                if (t == null || !t.gameObject.scene.IsValid()) continue;
                CaveNames c = t.ForwardCaveNum != CaveNames.NotInCaves ? t.ForwardCaveNum : t.BackwardCaveNum;
                if (c == CaveNames.NotInCaves) continue;
                float d = (t.transform.position - p).sqrMagnitude;
                if (d < bestD) { bestD = d; best = c; }
            }
            if (best == CaveNames.NotInCaves) best = CaveNames.Cave01;
            return best;
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(TheForest.Player.Actions.PlayerClimbRopeAction), "enterClimbRope")]
    static class PatchRopeBottom
    {
        static bool Prefix(Transform trn) { return RopeHop.Go(trn, false); }
    }

    [HarmonyLib.HarmonyPatch(typeof(TheForest.Player.Actions.PlayerClimbRopeAction), "enterClimbRopeTop")]
    static class PatchRopeTop
    {
        static bool Prefix(Transform trn) { return RopeHop.Go(trn, true); }
    }

    [HarmonyLib.HarmonyPatch(typeof(PlayerStats), "IgnoreCollisionWithTerrain")]
    static class PatchIgnoreTerrain
    {
        static void Postfix(bool onoff) { Caves.TerrainIgnored = onoff; }
    }

    // Ropes and climbable walls (activateClimb at the bottom, activateClimbTop at the top): in
    // Minecraft they climb like ladders (forward or jump: up, back: down, sneak: hold). Where a
    // rope goes through the island's ground (a hole into the caves), the ground is let through
    // while on the rope under the surface, or when stepping down onto it from the top (back or
    // sneak at the rope); once the feet are up on the surface, the ground is there to walk off on.
    // OFF_ROPES: count, then (x0, z0, y0, x1, z1, y1) bottom and top, Minecraft units.
    static class Ropes
    {
        const int Off = 0xA36500;
        const int Max = 64;
        const int Entry = 24;
        struct Column { public Vector3 a, b; public bool hole; }
        static readonly List<Column> all = new List<Column>();
        static float nextScan;
        static bool logged;
        /// <summary>The island's ground must let the player through here (on a rope, under it).</summary>
        public static bool Through;
        static int nearLogged = -1;

        public static void Update(Vector3 feet, int input)
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
                    if (top != null && Vector3.Distance(p, q) < c.closeTriggerThreshhold) continue; // too short to climb, as in the game
                    if (q.y < p.y) { Vector3 w = p; p = q; q = w; }
                    // Into the caves through the island's ground (its bottom well under the surface),
                    // not a climbable cliff out on the surface.
                    all.Add(new Column { a = p, b = q, hole = UnderSurface(p, 2f) });
                }
                if (!logged && all.Count > 0)
                {
                    logged = true;
                    var sb = new System.Text.StringBuilder("ForestCraft: " + all.Count + " ropes and climbable walls known:");
                    foreach (Column c in all) sb.Append(" [").Append(c.a.ToString("F0")).Append(" to ").Append(c.b.ToString("F0")).Append(c.hole ? " hole" : "").Append(']');
                    Plugin.Log.LogInfo(sb.ToString());
                }
            }
            Through = false;
            float k = Link.Scale;
            int n = 0, at = Off + 16, near = -1;
            for (int i = 0; i < all.Count && n < Max; i++)
            {
                Column c = all[i];
                float y0 = c.a.y - 1f, y1 = c.b.y + 1.5f;
                float mx = (c.a.x + c.b.x) * 0.5f - feet.x, mz = (c.a.z + c.b.z) * 0.5f - feet.z;
                if (mx * mx + mz * mz > 60f * 60f) continue;
                // The rope where the feet are: from the bottom trigger to the top one.
                float t = Mathf.Clamp01((feet.y - c.a.y) / Mathf.Max(0.01f, c.b.y - c.a.y));
                Vector3 axis = Vector3.Lerp(c.a, c.b, t);
                float dx = axis.x - feet.x, dz = axis.z - feet.z;
                if (dx * dx + dz * dz < 1.6f * 1.6f && feet.y > y0 - 0.5f && feet.y < y1 + 0.5f)
                {
                    near = i;
                    bool under = UnderSurface(feet, 0.25f);
                    bool down = (input & (2 | 32)) != 0; // back or sneak at the top: down the rope
                    if (c.hole && (under || down)) Through = true;
                }
                Link.WriteFloatAt(at, c.a.x / k);
                Link.WriteFloatAt(at + 4, -c.a.z / k);
                Link.WriteFloatAt(at + 8, y0 / k);
                Link.WriteFloatAt(at + 12, c.b.x / k);
                Link.WriteFloatAt(at + 16, -c.b.z / k);
                Link.WriteFloatAt(at + 20, y1 / k);
                at += Entry;
                n++;
            }
            Link.WriteIntAt(Off, n);
            if (near != nearLogged)
            {
                nearLogged = near;
                if (near >= 0) Plugin.Log.LogInfo("ForestCraft: at a rope from " + all[near].a.ToString("F1") + " to " + all[near].b.ToString("F1"));
            }
        }

        static bool UnderSurface(Vector3 feet, float margin)
        {
            Terrain land = Terrain.activeTerrain;
            return land != null && feet.y < land.SampleHeight(feet) + land.transform.position.y - margin;
        }
    }
}
