using System;
using HarmonyLib;
using UnityEngine;
using TheForest.Utils;

namespace ForestCraft
{
    // Fighting The Forest's natives with Minecraft's body.
    //  - Minecraft hits them: on each attack click, a ray from the camera (Minecraft's reach,
    //    3 blocks) looks for an enemy; it takes Minecraft's damage (held item, attack cooldown)
    //    through The Forest's own EnemyHealth.Hit, so their reactions and deaths are The Forest's.
    //  - They hit us: every damage The Forest's player would take (PlayerStats.Hit) is sent to
    //    Minecraft instead (armor, hearts, death are Minecraft's) and The Forest's health is spared.
    // The Forest's hunger, thirst, energy and stamina are kept full: Minecraft's hunger is the one.
    static class Combat
    {
        const float DamageToForest = 5f;   // Minecraft heart points -> The Forest enemy health
        const float DamageToMinecraft = 0.2f; // The Forest player health -> Minecraft health
        static bool wasAttack;
        static float pendingToMinecraft;
        static int damageSeq;
        static readonly RaycastHit[] hits = new RaycastHit[16];

        public static void Update(int input)
        {
            if (!Link.Driving) { wasAttack = false; feetKnown = false; return; }
            KeepStatsFull();
            TrackFall();
            bool attack = (input & 128) != 0;
            if (attack && !wasAttack) Strike();
            wasAttack = attack;
            if (pendingToMinecraft > 0f)
            {
                // Cumulative total + seq: Minecraft applies what it hasn't applied yet.
                Link.WriteFloatAt(0x100 + 128, Link.ReadFloatAt(0x100 + 128) + pendingToMinecraft);
                Link.WriteIntAt(0x100 + 124, ++damageSeq);
                pendingToMinecraft = 0f;
            }
        }

        // What The Forest's axe reacts to (weaponInfo sends "Hit" by tag): bushes and branches,
        // breakable wood and rock, suitcases and metal props, animals, birds, fish.
        // Dead animals (and hanging bodies) are "corpseProp"/"hanging": a hit there cuts them up (meat, skin, bones).
        static readonly string[] HitTags = { "SmallTree", "BreakableWood", "BreakableRock", "suitCase", "metalProp", "animalCollide", "lb_bird", "Fish", "corpseProp", "hanging" };

        // Scripts that take The Forest's weapon hits (C# and UnityScript, found by name).
        static readonly string[] Receivers = { "SuitCase", "BushDamage", "CutBush2", "BreakWoodSimple", "BreakCrate", "BreakWood", "animalHealth" };

        // The object to send "Hit" to. The collider a ray hits is often only the physical body of
        // the thing (a suitcase's case, a crate's box) while the script that takes the hit sits on a
        // sibling or child: look up the collider's parents, then around it (children of the parent
        // and grandparent), keeping only a receiver close to where the ray landed.
        static GameObject Receiver(Collider c, Vector3 point)
        {
            Transform t = c.transform;
            for (int depth = 0; t != null && depth < 4; depth++, t = t.parent)
            {
                for (int i = 0; i < Receivers.Length; i++)
                    if (t.GetComponent(Receivers[i]) != null) return t.gameObject;
                if (Array.IndexOf(HitTags, t.tag) >= 0) return t.gameObject;
            }
            float near = 1.5f * Link.Scale;
            Transform around = c.transform;
            for (int up = 0; up < 2 && around.parent != null; up++) around = around.parent;
            GameObject best = null;
            float bestDist = near;
            MonoBehaviour[] scripts = around.GetComponentsInChildren<MonoBehaviour>(true);
            for (int i = 0; i < scripts.Length; i++)
            {
                MonoBehaviour m = scripts[i];
                if (m == null || Array.IndexOf(Receivers, m.GetType().Name) < 0) continue;
                float d = Vector3.Distance(m.transform.position, point);
                if (d < bestDist) { bestDist = d; best = m.gameObject; }
            }
            return best;
        }

        // What a swing (or an arrow) along this line meets first: one of The Forest's enemies,
        // something that takes weapon hits (animal, bush, suitcase, carcass...), or nothing.
        // The collider and point FindTarget's answer was found on (for arrows left stuck in it).
        static Collider LastHit;
        static Vector3 LastPoint;

        static Vector3 StopPoint; // where FindTarget's line ended (on what stopped it)

        // Something that takes hits close to where the swing landed: a bird pecking on the ground,
        // a rabbit, a carcass lying there. Aimed at, the line hits the ground at their feet.
        static readonly Collider[] around = new Collider[48];
        static bool FindAround(Vector3 at, float radius, out EnemyHealth enemy, out Collider other, out Vector3 otherPoint)
        {
            enemy = null; other = null; otherPoint = at;
            Transform player = LocalPlayer.Transform;
            int n = Physics.OverlapSphereNonAlloc(at, radius, around, ~0, QueryTriggerInteraction.Collide);
            float best = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                Collider c = around[i];
                if (c == null || (player != null && c.transform.IsChildOf(player)) || Blocks.IsOurs(c.transform)) continue;
                bool creature = c.CompareTag("lb_bird") || c.CompareTag("animalCollide") || c.CompareTag("Fish") || c.CompareTag("corpseProp")
                    || c.CompareTag("hanging") || c.CompareTag("enemyCollide") || c.CompareTag("EnemyBodyPart");
                EnemyHealth eh = creature ? null : c.GetComponentInParent<EnemyHealth>();
                if (!creature && eh == null) continue;
                Vector3 p = c.ClosestPointOnBounds(at);
                float d = (p - at).sqrMagnitude;
                if (d >= best) continue;
                if (eh == null) eh = c.GetComponentInParent<EnemyHealth>();
                if (eh != null) { best = d; enemy = eh; other = null; LastHit = c; LastPoint = p; continue; }
                if (Receiver(c, p) == null) continue;
                best = d; enemy = null; other = c; otherPoint = p; LastHit = c; LastPoint = p;
            }
            return enemy != null || other != null;
        }

        static bool FindTarget(Vector3 from, Vector3 dir, float reach, float radius, out EnemyHealth enemy, out Collider other, out Vector3 otherPoint)
        {
            Transform player = LocalPlayer.Transform;
            int n = radius > 0f
                ? Physics.SphereCastNonAlloc(from, radius, dir, hits, reach, ~0, QueryTriggerInteraction.Collide)
                : Physics.RaycastNonAlloc(from, dir, hits, reach, ~0, QueryTriggerInteraction.Collide);
            enemy = null;
            other = null;
            otherPoint = Vector3.zero;
            float best = float.MaxValue;
            LastHit = null;
            StopPoint = from + dir * reach;
            for (int i = 0; i < n; i++)
            {
                Collider c = hits[i].collider;
                if (c == null || (player != null && c.transform.IsChildOf(player))) continue;
                if (Blocks.IsOurs(c.transform)) continue; // Minecraft's own blocks: Minecraft already stops at them
                // A sphere starting inside something reports distance 0 and no point: skip those.
                if (radius > 0f && hits[i].distance <= 0f) continue;
                if (hits[i].distance >= best) continue;
                EnemyHealth eh = c.GetComponentInParent<EnemyHealth>();
                if (eh == null && c.transform.root != null) eh = c.transform.root.GetComponentInChildren<EnemyHealth>();
                if (eh != null) { best = hits[i].distance; enemy = eh; other = null; LastHit = c; LastPoint = hits[i].point; continue; }
                if (Receiver(c, hits[i].point) != null) { best = hits[i].distance; enemy = null; other = c; otherPoint = hits[i].point; LastHit = c; LastPoint = hits[i].point; continue; }
                // Something solid in front (tree trunk, wall) stops the swing. Not the ground for the
                // fat sphere: skimming the ground, it met it before a bird or a rabbit sitting on it.
                if (!c.isTrigger && !(radius > 0f && c is TerrainCollider))
                {
                    best = hits[i].distance; enemy = null; other = null; LastHit = null;
                    StopPoint = radius > 0f ? from + dir * hits[i].distance : hits[i].point;
                }
            }
            return enemy != null || other != null;
        }

        /// <summary>The creature (enemy, or animal/bird/fish/carcass collider) under the crosshair, as a swing finds it.</summary>
        public static bool Aim(out EnemyHealth enemy, out Collider other, out Vector3 point, out Collider part)
        {
            enemy = null; other = null; point = Vector3.zero; part = null;
            Camera cam = LocalPlayerSafe.Camera();
            if (cam == null) return false;
            float reach = 3.2f * Link.Scale;
            if (!FindTarget(cam.transform.position, cam.transform.forward, reach, 0f, out enemy, out other, out point))
            {
                Vector3 landed = StopPoint;
                bool reached = (landed - cam.transform.position).sqrMagnitude < reach * reach * 0.999f;
                if (!FindTarget(cam.transform.position, cam.transform.forward, reach, 0.3f * Link.Scale, out enemy, out other, out point)
                    && !(reached && FindAround(landed, 0.5f * Link.Scale, out enemy, out other, out point)))
                    return false;
            }
            part = LastHit;
            if (enemy != null) point = LastHit != null ? LastPoint : enemy.transform.position + Vector3.up * Link.Scale;
            return true;
        }

        /// <summary>Damage to a cannibal from Minecraft (fire, lava...), told as the player's.</summary>
        public static void HurtEnemy(EnemyHealth enemy, int damage)
        {
            if (enemy == null || enemy.Health <= 0) return;
            if (enemy.targetSwitcher != null) enemy.targetSwitcher.attackerType = 4;
            enemy.getCombo(1);
            enemy.Hit(damage);
        }

        static void Strike()
        {
            Camera cam = LocalPlayerSafe.Camera();
            if (cam == null) return;
            float reach = 3.2f * Link.Scale;
            EnemyHealth enemy;
            Collider other;
            Vector3 otherPoint;
            // The exact line first; then a fatter one, so a rabbit or a leg isn't missed by a hair.
            // The exact line first; then a fatter one; then what sits where the line hit (the ground
            // under a bird, a rabbit).
            if (!FindTarget(cam.transform.position, cam.transform.forward, reach, 0f, out enemy, out other, out otherPoint))
            {
                Vector3 landed = StopPoint;
                bool reached = (landed - cam.transform.position).sqrMagnitude < reach * reach * 0.999f;
                if (!FindTarget(cam.transform.position, cam.transform.forward, reach, 0.3f * Link.Scale, out enemy, out other, out otherPoint)
                    && !(reached && FindAround(landed, 0.5f * Link.Scale, out enemy, out other, out otherPoint)))
                    return;
            }
            // Minecraft's own numbers: attack damage of the held item and the cooldown bar.
            float damage = Link.ReadMcFloat(168);
            float strength = Mathf.Clamp01(Link.ReadMcFloat(172));
            if (damage <= 0f) damage = 1f;
            damage *= 0.2f + strength * strength * 0.8f;
            // Minecraft's critical hit: a fully charged blow while coming down from a jump.
            heavyStrike = strength >= 0.9f && fallSpeed > 0.5f * Link.Scale;
            if (heavyStrike) damage *= 1.5f;
            int forestDamage = Mathf.Max(1, Mathf.RoundToInt(damage * DamageToForest));
            Apply(enemy, other, otherPoint, forestDamage, "hit");
            heavyStrike = false;
        }

        static void Apply(EnemyHealth enemy, Collider other, Vector3 otherPoint, int forestDamage, string what)
        {
            bool arrow = what != "hit";
            if (enemy != null)
            {
                if (enemy.Health <= 0) return;
                Vector3 at = LastHit != null ? LastPoint : enemy.transform.position + Vector3.up * Link.Scale;
                string how = HitEnemy(enemy, LastHit, at, forestDamage, arrow, heavyStrike);
                ForestEvents.Emit(heavyStrike ? ForestEvents.Crit : arrow ? ForestEvents.ArrowFlesh : ForestEvents.Flesh, at);
                Plugin.Log.LogInfo("ForestCraft: " + what + " " + enemy.gameObject.name + " for " + forestDamage + how + " (health " + enemy.Health + ")");
                return;
            }
            // Same message The Forest's weapon sends, to the object that actually listens for it.
            GameObject target = Receiver(other, otherPoint);
            if (target == null) return;
            if (other.CompareTag("corpseProp") || other.CompareTag("hanging"))
                other.gameObject.SendMessageUpwards("Hit", 0, SendMessageOptions.DontRequireReceiver); // cutting up a carcass
            else if (other.CompareTag("animalCollide") || other.CompareTag("lb_bird") || other.CompareTag("Fish"))
                target.SendMessageUpwards("Hit", forestDamage, SendMessageOptions.DontRequireReceiver);
            else if (target.GetComponent("BreakWood") != null)
                target.SendMessage("Hit", SendMessageOptions.DontRequireReceiver); // UnityScript Hit() takes nothing
            else
                target.SendMessage("Hit", forestDamage, SendMessageOptions.DontRequireReceiver);
            int kind = ForestEvents.Metal;
            if (other.CompareTag("animalCollide") || other.CompareTag("lb_bird") || other.CompareTag("Fish") || other.CompareTag("corpseProp") || other.CompareTag("hanging"))
                kind = arrow ? ForestEvents.ArrowFlesh : ForestEvents.Flesh;
            else if (other.CompareTag("SmallTree")) kind = ForestEvents.Plant;
            else if (other.CompareTag("BreakableRock")) kind = ForestEvents.Rock;
            else if (other.CompareTag("BreakableWood")) kind = ForestEvents.TreeHit;
            ForestEvents.Emit(kind, otherPoint);
            Plugin.Log.LogInfo("ForestCraft: " + what + " " + other.tag + " " + target.name + " for " + forestDamage);
        }

        // ---- hitting The Forest's natives the way its own weapons do ------------------------------
        // EnemyHealth.Hit alone took the health away but, for the plain cannibals, nothing else:
        // without knowing the player hit them (attacker type 4), from which side and in which
        // swing, they neither staggered nor bled. weaponInfo tells the body part hit
        // (mutantHitReceiver) all of it first; so do we: from behind or in front, the swing's side
        // (left/right, a stab for arrows), the combo of quick blows, a sneak attack on one who
        // hasn't seen us (The Forest kills on the spot), and a critical blow that can knock down.
        static bool heavyStrike;
        static int combo, swingSide;
        static float lastEnemyHit = -10f;
        static float lastFeetY, fallSpeed;
        static bool feetKnown;
        static string fleshEvent;
        static float fleshSearchAt;

        static void TrackFall()
        {
            float y = Drive.LastFeet.y;
            float dt = Time.deltaTime;
            if (feetKnown && dt > 0f) fallSpeed = (lastFeetY - y) / dt;
            lastFeetY = y;
            feetKnown = true;
        }

        static string HitEnemy(EnemyHealth enemy, Collider part, Vector3 at, int damage, bool arrow, bool heavy)
        {
            var opt = SendMessageOptions.DontRequireReceiver;
            mutantHitReceiver receiver = null;
            if (part != null)
            {
                receiver = part.GetComponent<mutantHitReceiver>();
                if (receiver == null) receiver = part.GetComponentInParent<mutantHitReceiver>();
            }
            GameObject target = receiver != null ? receiver.gameObject : part != null ? part.gameObject : enemy.gameObject;
            GameObject player = LocalPlayer.GameObject;
            target.SendMessage("getAttackerType", 4, opt);
            if (player != null) target.SendMessage("getAttacker", player, opt);
            if (enemy.targetSwitcher != null) enemy.targetSwitcher.attackerType = 4;

            bool behind = false;
            Transform root = enemy.transform.root;
            Transform body = root.childCount > 0 ? root.GetChild(0) : root;
            if (LocalPlayer.Transform != null)
            {
                Vector3 l = body.InverseTransformPoint(LocalPlayer.Transform.position);
                float angle = Mathf.Atan2(l.x, l.z) * Mathf.Rad2Deg;
                behind = angle < -140f || angle > 140f;
            }
            float now = Time.time;
            combo = now - lastEnemyHit < 1.5f ? Mathf.Min(3, combo + 1) : 1;
            lastEnemyHit = now;
            if (heavy) combo = 3;
            swingSide ^= 1;
            int dir = arrow ? 3 : swingSide;
            int takeDir = behind ? 1 : 0;
            if (receiver != null)
            {
                receiver.takeDamage(takeDir);
                receiver.getAttackDirection(dir);
                if (!arrow) receiver.getStealthAttack();
                receiver.getCombo(combo);
                if (heavy) receiver.sendHitFallDown(damage);
                else receiver.hitRelay(damage);
            }
            else
            {
                enemy.takeDamage(takeDir);
                enemy.getAttackDirection(dir);
                if (!arrow) enemy.getStealthAttack();
                enemy.getCombo(combo);
                if (heavy) enemy.hitFallDown(damage);
                else enemy.Hit(damage);
            }
            PlayFlesh(at);
            return (behind ? ", from behind" : "") + (heavy ? ", critical" : "") + ", combo " + combo + (receiver == null ? ", no hit receiver" : "");
        }

        // The Forest's own blow-in-flesh sound, taken from the player's weapons.
        static void PlayFlesh(Vector3 at)
        {
            if (fleshEvent == null && Time.realtimeSinceStartup >= fleshSearchAt)
            {
                fleshSearchAt = Time.realtimeSinceStartup + 30f;
                try
                {
                    GameObject player = LocalPlayer.GameObject;
                    if (player != null)
                    {
                        // weaponInfo derives from a Bolt type: read it by name, no extra reference.
                        foreach (MonoBehaviour w in player.GetComponentsInChildren<MonoBehaviour>(true))
                        {
                            if (w == null || w.GetType().Name != "weaponInfo") continue;
                            var t = w.GetType();
                            var current = t.GetField("currentWeaponScript");
                            var flesh = t.GetField("fleshHitEvent");
                            if (flesh == null) continue;
                            object cur = current != null ? current.GetValue(w) : null;
                            string e = cur != null ? flesh.GetValue(cur) as string : null;
                            if (string.IsNullOrEmpty(e)) e = flesh.GetValue(w) as string;
                            if (!string.IsNullOrEmpty(e)) { fleshEvent = e; break; }
                        }
                    }
                    Plugin.Log.LogInfo("ForestCraft: flesh hit sound " + (fleshEvent ?? "not found"));
                }
                catch (Exception e) { Plugin.Log.LogWarning("ForestCraft: flesh hit sound: " + e.Message); }
            }
            if (fleshEvent == null) return;
            try { FMODCommon.PlayOneshot(fleshEvent, at); } catch { }
        }

        // ---- Minecraft's arrows (Shots on the Minecraft side) -----------------------------------
        const int OffShots = 0xA10000;
        const int ShotRing = 128;
        const int OffShotHits = OffShots + 0x4000;
        const int HitRing = 64;
        static int shotsRead = int.MinValue;
        static int hitsWritten = -1;

        // Each tick of each flying arrow: does it go through one of The Forest's creatures?
        public static void Arrows()
        {
            System.IntPtr view = Link.View;
            if (view == System.IntPtr.Zero || !Link.Driving) return;
            int total = Link.ReadIntAt(OffShots);
            if (shotsRead == int.MinValue || total < shotsRead) { shotsRead = total; return; }
            if (total - shotsRead > ShotRing) shotsRead = total - ShotRing;
            if (hitsWritten < 0) hitsWritten = System.Math.Max(0, Link.ReadIntAt(OffShotHits));
            float k = Link.Scale;
            for (; shotsRead < total; shotsRead++)
            {
                int at = OffShots + 16 + (shotsRead % ShotRing) * 32;
                int id = Link.ReadIntAt(at);
                Vector3 a = new Vector3(Link.ReadFloatAt(at + 4) * k, Link.ReadFloatAt(at + 8) * k, -Link.ReadFloatAt(at + 12) * k);
                Vector3 b = new Vector3(Link.ReadFloatAt(at + 16) * k, Link.ReadFloatAt(at + 20) * k, -Link.ReadFloatAt(at + 24) * k);
                float damage = Link.ReadFloatAt(at + 28);
                Vector3 d = b - a;
                float len = d.magnitude;
                if (len < 0.01f) continue;
                EnemyHealth enemy;
                Collider other;
                Vector3 point;
                if (!FindTarget(a, d / len, len, 0.15f * k, out enemy, out other, out point)
                    && !FindTarget(a, d / len, len, 0f, out enemy, out other, out point)) continue;
                if (other != null && (other.CompareTag("SmallTree") || other.CompareTag("BreakableWood") || other.CompareTag("BreakableRock")
                    || other.CompareTag("suitCase") || other.CompareTag("metalProp") || other.CompareTag("corpseProp") || other.CompareTag("hanging"))) continue;
                Apply(enemy, other, point, Mathf.Max(1, Mathf.RoundToInt(damage * DamageToForest)), "arrow hit");
                Vector3 dir = d / len;
                // Planted where it went in, moving with that part of the body.
                if (LastHit != null) StuckArrows.Plant(LastHit.transform, LastPoint, dir, new Vector3(Link.ReadFloatAt(at + 16), Link.ReadFloatAt(at + 20), Link.ReadFloatAt(at + 24)));
                // Small things (a bird, a rabbit, a body falling as it dies) carry on in the
                // arrow's direction: speed in units per second, from Minecraft's blocks per tick.
                StuckArrows.Push(LastHit != null ? LastPoint : b, dir, len / k * 20f * k * 0.35f);
                // The arrow stays in the creature: Minecraft removes it.
                Link.WriteIntAt(OffShotHits + 16 + (hitsWritten % HitRing) * 4, id);
                hitsWritten++;
                Link.WriteIntAt(OffShotHits, hitsWritten);
            }
        }

        public static void UpdatePushes() { StuckArrows.Update(); }

        static void KeepStatsFull()
        {
            PlayerStats stats = LocalPlayer.Stats;
            if (stats == null) return;
            stats.Fullness = 1f;
            stats.Thirst = 0f;
            stats.Starvation = 0f;
            if (stats.Energy < 100f) stats.Energy = 100f;
            if (stats.Stamina < 100f) stats.Stamina = 100f;
            if (stats.Health < 100f) stats.Health = 100f;
            if (stats.HealthTarget < 100f) stats.HealthTarget = 100f;
            // Breath under water is Minecraft's (bubbles, drowning): The Forest's lungs stay full.
            if (stats.AirBreathing != null)
            {
                stats.AirBreathing.CurrentLungAir = stats.AirBreathing.MaxLungAirCapacity;
                stats.AirBreathing.CurrentLungAirTimer.Reset();
            }
        }

        public static void Damage(int forestDamage)
        {
            pendingToMinecraft += forestDamage * DamageToMinecraft;
        }
    }

    [HarmonyPatch(typeof(PlayerStats), "Hit")]
    static class PlayerDamageToMinecraft
    {
        static bool Prefix(int __0, PlayerStats.DamageType __2)
        {
            if (!Link.Driving) return true;
            // Drowning and cold are Minecraft's own business now (its water, its weather):
            // only blows, fire and poison carry over.
            if (__0 > 0 && __2 != PlayerStats.DamageType.Drowning && __2 != PlayerStats.DamageType.Frost) Combat.Damage(__0);
            return false;
        }
    }
}
