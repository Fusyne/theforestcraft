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
            if (!Link.Driving) { wasAttack = false; return; }
            KeepStatsFull();
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
        static readonly string[] HitTags = { "SmallTree", "BreakableWood", "BreakableRock", "suitCase", "metalProp", "animalCollide", "lb_bird", "Fish" };

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

        static void Strike()
        {
            Camera cam = LocalPlayerSafe.Camera();
            if (cam == null) return;
            float reach = 3.2f * Link.Scale;
            Transform player = LocalPlayer.Transform;
            int n = Physics.RaycastNonAlloc(cam.transform.position, cam.transform.forward, hits, reach, ~0, QueryTriggerInteraction.Collide);
            EnemyHealth enemy = null;
            Collider other = null;
            Vector3 otherPoint = Vector3.zero;
            float best = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                Collider c = hits[i].collider;
                if (c == null || (player != null && c.transform.IsChildOf(player))) continue;
                if (hits[i].distance >= best) continue;
                EnemyHealth eh = c.GetComponentInParent<EnemyHealth>();
                if (eh == null && c.transform.root != null) eh = c.transform.root.GetComponentInChildren<EnemyHealth>();
                if (eh != null) { best = hits[i].distance; enemy = eh; other = null; continue; }
                if (Receiver(c, hits[i].point) != null) { best = hits[i].distance; enemy = null; other = c; otherPoint = hits[i].point; continue; }
                // Something solid in front (tree trunk, wall) stops the swing.
                if (!c.isTrigger) { best = hits[i].distance; enemy = null; other = null; }
            }
            if (enemy == null && other == null) return;
            // Minecraft's own numbers: attack damage of the held item and the cooldown bar.
            float damage = Link.ReadMcFloat(168);
            float strength = Mathf.Clamp01(Link.ReadMcFloat(172));
            if (damage <= 0f) damage = 1f;
            damage *= 0.2f + strength * strength * 0.8f;
            int forestDamage = Mathf.Max(1, Mathf.RoundToInt(damage * DamageToForest));
            if (enemy != null)
            {
                if (enemy.Health <= 0) return;
                enemy.Hit(forestDamage);
                Plugin.Log.LogInfo("ForestCraft: hit " + enemy.gameObject.name + " for " + forestDamage + " (health " + enemy.Health + ")");
                return;
            }
            // Same message The Forest's weapon sends, to the object that actually listens for it.
            GameObject target = Receiver(other, otherPoint);
            if (target == null) return;
            if (other.CompareTag("animalCollide") || other.CompareTag("lb_bird") || other.CompareTag("Fish"))
                target.SendMessageUpwards("Hit", forestDamage, SendMessageOptions.DontRequireReceiver);
            else if (target.GetComponent("BreakWood") != null)
                target.SendMessage("Hit", SendMessageOptions.DontRequireReceiver); // UnityScript Hit() takes nothing
            else
                target.SendMessage("Hit", forestDamage, SendMessageOptions.DontRequireReceiver);
            Plugin.Log.LogInfo("ForestCraft: axe hit " + other.tag + " " + target.name + " for " + forestDamage);
        }

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
        }

        public static void Damage(int forestDamage)
        {
            pendingToMinecraft += forestDamage * DamageToMinecraft;
        }
    }

    [HarmonyPatch(typeof(PlayerStats), "Hit")]
    static class PlayerDamageToMinecraft
    {
        static bool Prefix(int __0)
        {
            if (!Link.Driving) return true;
            if (__0 > 0) Combat.Damage(__0);
            return false;
        }
    }
}
