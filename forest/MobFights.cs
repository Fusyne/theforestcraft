using System;
using System.Collections.Generic;
using UnityEngine;
using TheForest.Utils;

namespace ForestCraft
{
    // The Forest's cannibals against Minecraft's villagers and golems (Fighters.java).
    // Each villager / golem near the player gets an invisible stand-in here: tagged like one of
    // The Forest's own creatures (enemyRoot, body enemyCollide), which is what cannibals already
    // target and hit when they fight each other. A cannibal closer to one than to the player is
    // turned on it (its own AI: it runs at it and swings); its blows land on the stand-in, which
    // hands them to Minecraft as damage to that mob. Back the other way, Minecraft's iron golems
    // walk up to the cannibals and smash them (often off their feet), and villagers flee.
    static class MobFights
    {
        const int OffMobs = 0xA1C400, OffNatives = 0xA1D000, OffHurt = 0xA1E400, HurtRing = 64, OffSmash = 0xA1EC00, SmashRing = 64;
        const float Engage = 14f;          // blocks: a cannibal closer than this to a mob goes for it
        const float BlowToMinecraft = 0.7f; // a cannibal's blow (6, The Forest's units) -> Minecraft health
        const float GolemToForest = 2f;     // a golem's blow (Minecraft health) -> The Forest's

        class Stand
        {
            public int id, kind, seen;
            public GameObject root;
            public CapsuleCollider body;
            public McMobTarget target;
        }

        class Native
        {
            public EnemyHealth health;
            public mutantScriptSetup setup;
            public Transform body;
            public Stand fighting;
            public float swingAt, releaseAt, landAt;
        }

        static readonly Dictionary<int, Stand> stands = new Dictionary<int, Stand>();
        static readonly Dictionary<int, Native> natives = new Dictionary<int, Native>();
        static readonly Dictionary<GameObject, EnemyHealth> healthOf = new Dictionary<GameObject, EnemyHealth>();
        static readonly List<GameObject> cannibals = new List<GameObject>();
        static readonly List<int> gone = new List<int>();
        static int mobsSeq = int.MinValue, pass, enemyLayer = -1;
        static float nextNatives, nextEngage;

        public static void Update()
        {
            if (!Link.Driving || Link.View == IntPtr.Zero) { Clear(); return; }
            float k = Link.Scale;
            ReadMobs(k);
            if (Time.time >= nextNatives) { nextNatives = Time.time + 0.1f; PublishNatives(k); }
            if (Time.time >= nextEngage) { nextEngage = Time.time + 0.5f; EngageAll(k); }
            Stealth.Update();
            foreach (Native nat in natives.Values) if (nat.fighting != null) Close(nat, k);
            Smashes(k);
        }

        static void Clear()
        {
            foreach (var s in stands.Values) if (s.root != null) UnityEngine.Object.Destroy(s.root);
            stands.Clear();
            natives.Clear();
        }

        // ---- Minecraft's mobs -> stand-ins -------------------------------------------------------
        static int EnemyLayer()
        {
            if (enemyLayer >= 0) return enemyLayer;
            foreach (GameObject g in cannibals)
            {
                if (g == null) continue;
                foreach (Collider c in g.GetComponentsInChildren<Collider>(true))
                {
                    if (c != null && c.CompareTag("enemyCollide"))
                    {
                        enemyLayer = c.gameObject.layer;
                        Plugin.Log.LogInfo("ForestCraft: cannibal body layer " + enemyLayer);
                        return enemyLayer;
                    }
                }
            }
            return 0;
        }

        static void ReadMobs(float k)
        {
            int seq = Link.ReadIntAt(OffMobs);
            if (seq == mobsSeq) return;
            mobsSeq = seq;
            pass++;
            int count = Mathf.Clamp(Link.ReadIntAt(OffMobs + 4), 0, 64);
            for (int i = 0; i < count; i++)
            {
                int at = OffMobs + 16 + i * 32;
                int id = Link.ReadIntAt(at), kind = Link.ReadIntAt(at + 4);
                Vector3 p = new Vector3(Link.ReadFloatAt(at + 8) * k, Link.ReadFloatAt(at + 12) * k, -Link.ReadFloatAt(at + 16) * k);
                float w = Link.ReadFloatAt(at + 20) * k, h = Link.ReadFloatAt(at + 24) * k;
                Stand s;
                if (!stands.TryGetValue(id, out s) || s.root == null)
                {
                    s = Make(id, kind);
                    if (s == null) continue;
                    stands[id] = s;
                }
                s.seen = pass;
                s.root.transform.position = p;
                // Wider than the mob: a cannibal stops at arm's length of the player's size and
                // its swing fell short of a villager's thin body.
                s.body.height = Mathf.Max(h, 0.5f * k) + 0.3f * k;
                s.body.radius = Mathf.Max(w * 0.5f, 0.3f * k) + 0.5f * k;
                s.body.center = new Vector3(0f, s.body.height * 0.5f, 0f);
            }
            gone.Clear();
            foreach (var pair in stands) if (pair.Value.seen != pass || pair.Value.root == null) gone.Add(pair.Key);
            foreach (int id in gone)
            {
                if (stands[id].root != null) UnityEngine.Object.Destroy(stands[id].root);
                stands.Remove(id);
            }
        }

        static Stand Make(int id, int kind)
        {
            try
            {
                var root = new GameObject("ForestCraft " + (kind == 1 ? "villager" : kind == 2 ? "iron golem" : "snow golem") + " " + id);
                UnityEngine.Object.DontDestroyOnLoad(root);
                root.tag = "enemyRoot";
                var body = new GameObject("body");
                body.transform.SetParent(root.transform, false);
                body.tag = "enemyCollide";
                body.layer = EnemyLayer();
                var cap = body.AddComponent<CapsuleCollider>();
                cap.isTrigger = true;
                var rb = body.AddComponent<Rigidbody>();
                rb.isKinematic = true;
                rb.useGravity = false;
                var target = body.AddComponent<McMobTarget>();
                target.McId = id;
                return new Stand { id = id, kind = kind, root = root, body = cap, target = target };
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("ForestCraft: stand-in for Minecraft mob " + id + ": " + e.Message);
                return null;
            }
        }

        static int hurtWritten = -1;

        // A cannibal's blow landed on a stand-in.
        public static void Hurt(int id, float forestDamage, Vector3 from)
        {
            if (Link.View == IntPtr.Zero) return;
            float k = Link.Scale;
            if (hurtWritten < 0) hurtWritten = Math.Max(0, Link.ReadIntAt(OffHurt));
            int at = OffHurt + 16 + (hurtWritten % HurtRing) * 16;
            Link.WriteIntAt(at, id);
            Link.WriteFloatAt(at + 4, forestDamage * BlowToMinecraft);
            Link.WriteFloatAt(at + 8, from.x / k);
            Link.WriteFloatAt(at + 12, -from.z / k);
            hurtWritten++;
            Link.WriteIntAt(OffHurt, hurtWritten);
        }

        // ---- The Forest's cannibals -> Minecraft -------------------------------------------------
        static void GatherCannibals()
        {
            cannibals.Clear();
            try
            {
                var mc = Scene.MutantControler;
                if (mc == null) return;
                foreach (GameObject g in mc.activeCannibals) if (g != null && g.activeInHierarchy) cannibals.Add(g);
                foreach (GameObject g in mc.activeInstantSpawnedCannibals) if (g != null && g.activeInHierarchy && !cannibals.Contains(g)) cannibals.Add(g);
            }
            catch { }
        }

        static void PublishNatives(float k)
        {
            GatherCannibals();
            int n = 0;
            foreach (GameObject g in cannibals)
            {
                if (n >= 64) break;
                EnemyHealth eh;
                if (!healthOf.TryGetValue(g, out eh) || eh == null)
                {
                    eh = g.GetComponentInChildren<EnemyHealth>();
                    healthOf[g] = eh;
                    if (healthOf.Count > 256) healthOf.Clear();
                }
                if (eh == null || eh.Health <= 0) continue;
                int id = eh.GetInstanceID();
                Native nat;
                if (!natives.TryGetValue(id, out nat)) { nat = new Native { health = eh, setup = g.GetComponentInChildren<mutantScriptSetup>(), body = g.transform }; natives[id] = nat; }
                Vector3 p = eh.transform.position;
                int at = OffNatives + 16 + n * 20;
                Link.WriteIntAt(at, id);
                Link.WriteFloatAt(at + 4, p.x / k);
                Link.WriteFloatAt(at + 8, p.y / k);
                Link.WriteFloatAt(at + 12, -p.z / k);
                Link.WriteFloatAt(at + 16, eh.Health);
                n++;
            }
            Link.WriteIntAt(OffNatives + 4, n);
            Link.WriteIntAt(OffNatives, Link.ReadIntAt(OffNatives) + 1);
        }

        static void EngageAll(float k)
        {
            Transform player = LocalPlayer.Transform;
            gone.Clear();
            foreach (var pair in natives)
            {
                Native nat = pair.Value;
                if (nat.health == null || nat.health.Health <= 0 || nat.setup == null || nat.setup.search == null) { gone.Add(pair.Key); continue; }
                Vector3 at = nat.health.transform.position;
                Stand best = null;
                float bestD = Engage * k;
                foreach (Stand s in stands.Values)
                {
                    if (s.root == null) continue;
                    float d = Vector3.Distance(at, s.root.transform.position);
                    if (d < bestD) { bestD = d; best = s; }
                }
                float toPlayer = player != null ? Vector3.Distance(at, player.position) : float.MaxValue;
                if (Stealth.On) toPlayer = float.MaxValue;
                if (best != null && (bestD < toPlayer * 0.8f || toPlayer > 10f * k))
                {
                    if (nat.fighting != best || nat.setup.search.currentTarget != best.root)
                    {
                        bool first = nat.fighting != best;
                        nat.fighting = best;
                        nat.setup.search.switchToNewTarget(best.root);
                        Aggressive(nat.setup);
                        if (first) Plugin.Log.LogInfo("ForestCraft: " + nat.health.gameObject.name + " goes for " + best.root.name);
                    }
                }
                else if (nat.fighting != null)
                {
                    nat.fighting = null;
                    if (LocalPlayer.GameObject != null) nat.setup.search.switchToNewTarget(LocalPlayer.GameObject);
                }
            }
            foreach (int id in gone) natives.Remove(id);
        }

        // Up close, The Forest's combat brain doesn't always swing at a target that isn't a player:
        // face it and swing (its own attack animations); the weapon's hit lands on the stand-in.
        // If the arm passed beside it, the blow still counts once the swing is over.
        static readonly string[] Swings = { "attackBOOL", "attackRightBOOL", "attackLeftBOOL" };

        static void Close(Native nat, float k)
        {
            Stand s = nat.fighting;
            if (s == null || s.root == null || nat.health == null || nat.health.Health <= 0 || nat.setup == null) return;
            Animator anim = nat.setup.animator;
            Transform body = nat.body;
            if (body == null) return;
            Vector3 to = s.root.transform.position - nat.health.transform.position;
            to.y = 0f;
            float reach = s.body.radius + 1.4f * k;
            float now = Time.time;
            if (nat.releaseAt > 0f && now >= nat.releaseAt)
            {
                nat.releaseAt = 0f;
                if (anim != null) foreach (string b in Swings) anim.SetBool(b, false);
            }
            if (nat.landAt > 0f && now >= nat.landAt)
            {
                nat.landAt = 0f;
                if (s.target != null && s.target.LastHit < nat.swingAt && to.magnitude < reach + 0.6f * k)
                {
                    Hurt(s.id, 6, nat.health.transform.position);
                    ForestEvents.Emit(ForestEvents.Flesh, s.root.transform.position + Vector3.up * s.body.height * 0.6f);
                }
            }
            if (to.magnitude > reach || anim == null || !anim.enabled) return;
            // Turned towards it, like it turns to face the player.
            Quaternion face = Quaternion.LookRotation(to.normalized, Vector3.up);
            body.rotation = Quaternion.Slerp(body.rotation, face, 6f * Time.deltaTime);
            if (now - nat.swingAt < 1.8f) return;
            if (Quaternion.Angle(body.rotation, face) > 35f) return;
            nat.swingAt = now;
            anim.SetBool(Swings[UnityEngine.Random.Range(0, Swings.Length)], true);
            nat.releaseAt = now + 0.35f;
            nat.landAt = now + 0.75f;
        }

        // The way The Forest makes a cannibal fight back an enemy: aggressive combat, brain on.
        static void Aggressive(mutantScriptSetup setup)
        {
            try { if (setup.aiManager != null) setup.aiManager.setAggressiveCombat(); } catch { }
            try
            {
                object brain = typeof(mutantScriptSetup).GetField("pmBrain").GetValue(setup);
                if (brain != null) brain.GetType().GetMethod("SendEvent", new[] { typeof(string) }).Invoke(brain, new object[] { "toSetAggressive" });
            }
            catch { }
        }

        // ---- iron golems' blows ---------------------------------------------------------------------
        static int smashRead = int.MinValue;

        static void Smashes(float k)
        {
            int total = Link.ReadIntAt(OffSmash);
            if (smashRead == int.MinValue || total < smashRead) { smashRead = total; return; }
            if (total - smashRead > SmashRing) smashRead = total - SmashRing;
            for (; smashRead < total; smashRead++)
            {
                int at = OffSmash + 16 + (smashRead % SmashRing) * 8;
                int id = Link.ReadIntAt(at);
                float damage = Link.ReadFloatAt(at + 4);
                Native nat;
                if (!natives.TryGetValue(id, out nat) || nat.health == null || nat.health.Health <= 0) continue;
                EnemyHealth eh = nat.health;
                int forest = Mathf.Max(1, Mathf.RoundToInt(damage * GolemToForest));
                if (eh.targetSwitcher != null) eh.targetSwitcher.attackerType = 4;
                eh.getAttackDirection(UnityEngine.Random.Range(0, 2));
                eh.getCombo(3);
                if (UnityEngine.Random.value < 0.25f) eh.hitFallDown(forest);
                else eh.Hit(forest);
                ForestEvents.Emit(ForestEvents.Flesh, eh.transform.position + Vector3.up * 1.2f * k);
                Plugin.Log.LogInfo("ForestCraft: an iron golem hit " + eh.gameObject.name + " for " + forest + " (health " + eh.Health + ")");
            }
        }
    }

    // On a Minecraft mob's stand-in: takes what The Forest's weapons send to a creature's body.
    class McMobTarget : MonoBehaviour
    {
        public int McId;
        GameObject attacker;
        float lastHit = -10f;
        public float LastHit { get { return lastHit; } }

        void getAttacker(GameObject go) { attacker = go; }
        void getAttackerType(int type) { }
        void getAttackDirection(int dir) { }
        void getCombo(int combo) { }
        void getHitDirection(Vector3 from) { }

        void Hit(int damage)
        {
            if (Time.time - lastHit < 0.3f || damage <= 0) return;
            lastHit = Time.time;
            MobFights.Hurt(McId, damage, attacker != null ? attacker.transform.position : transform.position);
        }

        void Burn() { Hit(4); }
        void Explosion(float dist) { Hit(20); }
    }

    // "Invisible to the natives" (F8 menu). The Forest's own console command only moves the
    // player's root to an unseen layer, but its cannibals see by casting a ray at the player and
    // taking whatever solid thing it meets first (another collider of the player's body counts),
    // and one already after you never looks again. Here: they cannot see you any more, and those
    // hunting you lose your track (every 2 s while it is on).
    static class Stealth
    {
        public static bool On;
        static float next;

        public static void Set(bool on)
        {
            On = on;
            next = 0f;
            Plugin.Log.LogInfo("ForestCraft: invisible to the natives " + (on ? "on" : "off"));
        }

        public static bool IsPlayer(GameObject g)
        {
            Transform p = LocalPlayer.Transform;
            return g != null && p != null && g.transform.root == p.root;
        }

        public static void Update()
        {
            if (!On || Time.time < next) return;
            next = Time.time + 2f;
            try
            {
                var mc = Scene.MutantControler;
                if (mc == null) return;
                foreach (GameObject g in mc.activeCannibals) LoseTrack(g);
                foreach (GameObject g in mc.activeInstantSpawnedCannibals) LoseTrack(g);
            }
            catch { }
        }

        static void LoseTrack(GameObject g)
        {
            if (g == null || !g.activeInHierarchy) return;
            mutantScriptSetup setup = g.GetComponentInChildren<mutantScriptSetup>();
            if (setup == null || setup.search == null || !IsPlayer(setup.search.currentTarget)) return;
            Send(setup, "pmCombat", "toTargetLost");
            Send(setup, "pmBrain", "toSetPassive");
        }

        static void Send(mutantScriptSetup setup, string fsm, string evt)
        {
            try
            {
                object f = typeof(mutantScriptSetup).GetField(fsm).GetValue(setup);
                if (f != null) f.GetType().GetMethod("SendEvent", new[] { typeof(string) }).Invoke(f, new object[] { evt });
            }
            catch { }
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(mutantSearchFunctions), "raycastTargets")]
    static class StealthNoSight
    {
        static bool Prefix(ref bool __result)
        {
            if (!Stealth.On) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(mutantSearchFunctions), "rayCastActiveTarget")]
    static class StealthNoTracking
    {
        static bool Prefix(mutantSearchFunctions __instance, ref bool __result)
        {
            if (!Stealth.On || !Stealth.IsPlayer(__instance.hitGo)) return true;
            __result = false;
            return false;
        }
    }
}
