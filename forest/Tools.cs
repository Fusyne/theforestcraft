using System;
using System.Collections.Generic;
using UnityEngine;
using TheForest.Utils;

namespace ForestCraft
{
    // Minecraft's tools on The Forest's creatures, used with a right click while aiming at one
    // (Minecraft says what Steve holds at OFF_MC+200):
    //   flint and steel / fire charge: sets it alight (The Forest's own fire, the way a burning
    //   stick does); lava bucket: soaks it in fire (long, strong burn, and it hurts); water bucket:
    //   puts the fire out; lead: ties it up, and it follows (dragged) until untied or the lead snaps.
    // And Minecraft's world around them: walking through Steve's fire or lava burns them, his water
    // puts them out, his TNT and creepers blow them up (Hazards.java on the Minecraft side).
    static class Tools
    {
        public const int Flint = 1, FireCharge = 2, LavaBucket = 3, WaterBucket = 4, Lead = 5;
        const int OffHazards = 0xA1A000, OffBlasts = 0xA1E000, BlastRing = 32;
        const int Ignite = 9, LavaPour = 10, Splash = 11, LeadTied = 12, LeadBroke = 13, LeadUntied = 14;
        const string Survival = "execute if entity @s[gamemode=!creative] run ";
        static bool wasUse;

        public static void Update(int input)
        {
            if (!Link.Driving || Link.View == IntPtr.Zero) { wasUse = false; return; }
            bool use = (input & 256) != 0;
            if (use && !wasUse) Use(Link.ReadMcInt(200));
            wasUse = use;
            try { LeadUpdate(); } catch (Exception e) { Plugin.Log.LogWarning("ForestCraft: lead: " + e.Message); Release(false, LeadUntied); }
            try { Hazards(); } catch (Exception e) { Warn("hazards", e); }
            try { Blasts(); } catch (Exception e) { Warn("explosion", e); }
            try { Corpses.Update(); } catch (Exception e) { Warn("corpses", e); }
        }

        static float nextWarn;
        static void Warn(string what, Exception e)
        {
            if (Time.realtimeSinceStartup < nextWarn) return;
            nextWarn = Time.realtimeSinceStartup + 10f;
            Plugin.Log.LogWarning("ForestCraft: " + what + ": " + e);
        }

        // ---- right click with a tool ------------------------------------------------------------

        static bool IsCreature(Collider c)
        {
            return c != null && (c.CompareTag("animalCollide") || c.CompareTag("lb_bird") || c.CompareTag("Fish"));
        }

        static void Use(int kind)
        {
            if (kind == 0) return;
            EnemyHealth enemy;
            Collider other, part;
            Vector3 point;
            bool aimed = Combat.Aim(out enemy, out other, out point, out part);
            animalHealth animal = IsCreature(other) ? other.GetComponentInParent<animalHealth>() : null;
            if (kind == Lead)
            {
                if (leashed != null && (!aimed || (enemy != null && enemy == leashedEnemy) || (animal != null && animal == leashedAnimal)))
                {
                    Release(true, LeadUntied);
                    return;
                }
                if (enemy != null || animal != null) Tie(enemy, animal, point);
                return;
            }
            if (enemy == null && animal == null && !IsCreature(other)) return;
            switch (kind)
            {
                case Flint:
                case FireCharge:
                    SetAlight(enemy, animal, other, false);
                    ForestEvents.Emit(Ignite, point);
                    if (kind == Flint) DevConsole.SendToMinecraft(Survival + "item modify entity @s weapon.mainhand {\"function\":\"minecraft:set_damage\",\"damage\":-0.015625,\"add\":true}");
                    else DevConsole.SendToMinecraft(Survival + "clear @s minecraft:fire_charge 1");
                    Plugin.Log.LogInfo("ForestCraft: set alight " + Name(enemy, animal, other));
                    break;
                case LavaBucket:
                    SetAlight(enemy, animal, other, true);
                    if (enemy != null) Combat.HurtEnemy(enemy, 20);
                    else if (animal != null) animal.SendMessage("Hit", 10, SendMessageOptions.DontRequireReceiver);
                    ForestEvents.Emit(LavaPour, point);
                    DevConsole.SendToMinecraft(Survival + "item replace entity @s weapon.mainhand with minecraft:bucket");
                    Plugin.Log.LogInfo("ForestCraft: lava poured on " + Name(enemy, animal, other));
                    break;
                case WaterBucket:
                    PutOut(enemy, animal);
                    ForestEvents.Emit(Splash, point);
                    DevConsole.SendToMinecraft(Survival + "item replace entity @s weapon.mainhand with minecraft:bucket");
                    Plugin.Log.LogInfo("ForestCraft: water thrown on " + Name(enemy, animal, other));
                    break;
            }
        }

        static string Name(EnemyHealth enemy, animalHealth animal, Collider other)
        {
            return enemy != null ? enemy.gameObject.name : animal != null ? animal.gameObject.name : other != null ? other.name : "?";
        }

        // The Forest's own fire on a creature. Strong: soaked first (like a molotov), so it burns
        // long and hard instead of a short singe.
        static void SetAlight(EnemyHealth enemy, animalHealth animal, Collider other, bool strong)
        {
            if (enemy != null)
            {
                if (enemy.Health <= 0) return;
                if (enemy.targetSwitcher != null) enemy.targetSwitcher.attackerType = 4;
                if (strong) enemy.setFireDouse();
                enemy.Burn();
                return;
            }
            if (animal != null) { animal.Burn(); return; }
            if (other != null) other.SendMessageUpwards("Burn", SendMessageOptions.DontRequireReceiver);
        }

        static void PutOut(EnemyHealth enemy, animalHealth animal)
        {
            if (enemy != null)
            {
                foreach (mutantHitReceiver r in enemy.transform.root.GetComponentsInChildren<mutantHitReceiver>()) { r.GotClean(); break; }
                enemy.disableBurn();
                enemy.resetDouse();
            }
            if (animal != null) animal.SendMessage("cancelFire", SendMessageOptions.DontRequireReceiver);
        }

        // ---- the lead ---------------------------------------------------------------------------
        const float Slack = 4f, Snap = 12f, Pull = 14f; // blocks, blocks, blocks per second
        static Transform leashed;
        static EnemyHealth leashedEnemy;
        static animalHealth leashedAnimal;
        static Vector3 attachLocal;
        static LineRenderer line;
        static readonly Vector3[] linePoints = new Vector3[20];

        // The whole creature: for a cannibal the object The Forest spawned (its AI list), for an
        // animal the object that carries its body.
        static Transform BodyOf(EnemyHealth enemy, animalHealth animal)
        {
            if (enemy != null)
            {
                try
                {
                    var mc = Scene.MutantControler;
                    if (mc != null)
                    {
                        foreach (GameObject g in mc.activeCannibals) if (g != null && enemy.transform.IsChildOf(g.transform)) return g.transform;
                        foreach (GameObject g in mc.activeInstantSpawnedCannibals) if (g != null && enemy.transform.IsChildOf(g.transform)) return g.transform;
                    }
                }
                catch { }
                return enemy.transform.root;
            }
            Rigidbody rb = animal.GetComponentInParent<Rigidbody>();
            return rb != null ? rb.transform : animal.transform;
        }

        static void Tie(EnemyHealth enemy, animalHealth animal, Vector3 point)
        {
            if ((enemy != null && enemy.Health <= 0) || (enemy == null && animal != null && animal.Health <= 0)) return; // dead: nothing to tie
            if (leashed != null) Release(true, LeadUntied);
            leashed = BodyOf(enemy, animal);
            leashedEnemy = enemy;
            leashedAnimal = animal;
            attachLocal = leashed.InverseTransformPoint(point);
            ForestEvents.Emit(LeadTied, point);
            DevConsole.SendToMinecraft(Survival + "clear @s minecraft:lead 1");
            Plugin.Log.LogInfo("ForestCraft: lead tied to " + leashed.name);
        }

        static void Release(bool giveBack, int sound)
        {
            if (leashed != null)
            {
                ForestEvents.Emit(sound, leashed.TransformPoint(attachLocal));
                if (giveBack) DevConsole.SendToMinecraft(Survival + "give @s minecraft:lead");
                Plugin.Log.LogInfo("ForestCraft: lead " + (sound == LeadBroke ? "snapped" : "untied"));
            }
            leashed = null;
            leashedEnemy = null;
            leashedAnimal = null;
            if (line != null) line.enabled = false;
        }

        static readonly RaycastHit[] groundHits = new RaycastHit[16];

        static void LeadUpdate()
        {
            if (leashed == null) return;
            if (!leashed.gameObject.activeInHierarchy || (leashedEnemy != null && leashedEnemy.Health <= 0)
                || (leashedEnemy == null && (leashedAnimal == null || leashedAnimal.Health <= 0)))
            {
                Release(true, LeadUntied);
                return;
            }
            float k = Link.Scale;
            Vector3 feet = Drive.LastFeet;
            Vector3 to = feet - leashed.position;
            to.y = 0f;
            float dist = to.magnitude;
            if (dist > Snap * k) { Release(true, LeadBroke); return; }
            if (dist > Slack * k)
            {
                // Dragged along: towards Steve, at most at a run, kept on whatever is under it.
                Vector3 dir = to / dist;
                float step = Mathf.Min(dist - Slack * k, Pull * k * Time.deltaTime);
                Vector3 p = leashed.position + dir * step;
                p.y = GroundUnder(p, leashed.position.y);
                Rigidbody rb = leashed.GetComponent<Rigidbody>();
                if (rb != null && !rb.isKinematic) { rb.position = p; rb.velocity = Vector3.zero; }
                leashed.position = p;
                Quaternion face = Quaternion.LookRotation(dir, Vector3.up);
                leashed.rotation = Quaternion.Slerp(leashed.rotation, face, 8f * Time.deltaTime);
            }
            DrawLead(k, dist);
        }

        static float GroundUnder(Vector3 p, float fallback) { return GroundUnder(p, fallback, leashed); }

        static float GroundUnder(Vector3 p, float fallback, Transform skip)
        {
            float k = Link.Scale;
            int n = Physics.RaycastNonAlloc(p + Vector3.up * 2f * k, Vector3.down, groundHits, 5f * k, ~0, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue, y = fallback;
            Transform player = LocalPlayer.Transform;
            for (int i = 0; i < n; i++)
            {
                Collider c = groundHits[i].collider;
                if (c == null || c.isTrigger || (skip != null && c.transform.IsChildOf(skip)) || (player != null && c.transform.IsChildOf(player))) continue;
                if (groundHits[i].distance < best) { best = groundHits[i].distance; y = groundHits[i].point.y; }
            }
            return y;
        }

        static void DrawLead(float k, float dist) { DrawLead(k, dist, leashed.TransformPoint(attachLocal)); }

        static void DrawLead(float k, float dist, Vector3 end)
        {
            if (line == null)
            {
                var go = new GameObject("ForestCraft lead");
                UnityEngine.Object.DontDestroyOnLoad(go);
                line = go.AddComponent<LineRenderer>();
                line.useWorldSpace = true;
                line.positionCount = linePoints.Length;
                line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                line.receiveShadows = false;
                Shader s = Shader.Find("Hidden/Internal-Colored");
                if (s != null)
                {
                    var m = new Material(s);
                    m.SetInt("_Cull", 0);
                    m.SetInt("_ZWrite", 1);
                    line.sharedMaterial = m;
                }
                Color brown = new Color(0.36f, 0.25f, 0.14f, 1f);
                line.startColor = line.endColor = brown;
            }
            line.startWidth = line.endWidth = 0.06f * k;
            Camera cam = LocalPlayerSafe.Camera();
            Vector3 hand = cam != null
                ? cam.transform.position + cam.transform.forward * 0.5f * k + cam.transform.right * 0.3f * k - Vector3.up * 0.35f * k
                : Drive.LastFeet + Vector3.up * 1.1f * k;
            // Hangs when slack, straight when pulled.
            float sag = Mathf.Clamp((Slack * k - dist) * 0.25f, 0.05f * k, 1.2f * k);
            for (int i = 0; i < linePoints.Length; i++)
            {
                float t = i / (float)(linePoints.Length - 1);
                linePoints[i] = Vector3.Lerp(hand, end, t) - Vector3.up * sag * 4f * t * (1f - t);
            }
            line.SetPositions(linePoints);
            if (!line.enabled) line.enabled = true;
        }

        // ---- Minecraft's fire, lava and water around creatures ----------------------------------
        static int hazardSeq = int.MinValue;
        static float nextHazard;
        static readonly Collider[] inBlock = new Collider[32];
        static readonly Dictionary<int, float> cooldown = new Dictionary<int, float>();

        static bool Ready(UnityEngine.Object o, float wait)
        {
            float now = Time.time, until;
            int id = o.GetInstanceID();
            if (cooldown.TryGetValue(id, out until) && now < until) return false;
            cooldown[id] = now + wait;
            if (cooldown.Count > 256) cooldown.Clear();
            return true;
        }

        static void Hazards()
        {
            if (Time.time < nextHazard) return;
            nextHazard = Time.time + 0.25f;
            int seq = Link.ReadIntAt(OffHazards);
            if (seq == 0) return;
            hazardSeq = seq;
            int count = Mathf.Clamp(Link.ReadIntAt(OffHazards + 4), 0, 512);
            if (count == 0) return;
            float k = Link.Scale;
            Vector3 half = new Vector3(0.55f, 0.55f, 0.55f) * k;
            Transform player = LocalPlayer.Transform;
            for (int i = 0; i < count; i++)
            {
                int at = OffHazards + 16 + i * 16;
                int x = Link.ReadIntAt(at), y = Link.ReadIntAt(at + 4), z = Link.ReadIntAt(at + 8), kind = Link.ReadIntAt(at + 12);
                Vector3 c = new Vector3((x + 0.5f) * k, (y + 0.5f) * k, -(z + 0.5f) * k);
                int n = Physics.OverlapBoxNonAlloc(c, half, inBlock, Quaternion.identity, ~0, QueryTriggerInteraction.Collide);
                for (int j = 0; j < n; j++)
                {
                    Collider col = inBlock[j];
                    if (col == null || (player != null && col.transform.IsChildOf(player)) || Blocks.IsOurs(col.transform)) continue;
                    EnemyHealth enemy = col.GetComponentInParent<EnemyHealth>();
                    animalHealth animal = enemy == null && IsCreature(col) ? col.GetComponentInParent<animalHealth>() : null;
                    if (enemy == null && animal == null) continue;
                    if (enemy != null && enemy.Health <= 0) continue;
                    UnityEngine.Object who = enemy != null ? (UnityEngine.Object)enemy : animal;
                    if (kind == 1)
                    {
                        if (enemy != null && enemy.onFire) continue;
                        if (!Ready(who, 4f)) continue;
                        SetAlight(enemy, animal, col, false);
                        Plugin.Log.LogInfo("ForestCraft: " + Name(enemy, animal, col) + " walked into fire");
                    }
                    else if (kind == 2)
                    {
                        if (!Ready(who, 0.5f)) continue;
                        if (enemy != null) { if (!enemy.onFire) SetAlight(enemy, null, col, true); Combat.HurtEnemy(enemy, 8); }
                        else { if (Ready(animal.gameObject, 5f)) animal.Burn(); animal.SendMessage("Hit", 4, SendMessageOptions.DontRequireReceiver); }
                    }
                    else if (kind == 3 && enemy != null && enemy.onFire)
                    {
                        if (!Ready(who, 1f)) continue;
                        PutOut(enemy, null);
                        ForestEvents.Emit(Splash, c);
                    }
                }
            }
        }

        // ---- Minecraft's explosions ---------------------------------------------------------------
        static int blastsRead = int.MinValue;
        static readonly Collider[] inBlast = new Collider[128];
        static readonly HashSet<int> blasted = new HashSet<int>();

        static void Blasts()
        {
            int total = Link.ReadIntAt(OffBlasts);
            if (blastsRead == int.MinValue || total < blastsRead) { blastsRead = total; return; }
            if (total - blastsRead > BlastRing) blastsRead = total - BlastRing;
            float k = Link.Scale;
            Transform player = LocalPlayer.Transform;
            for (; blastsRead < total; blastsRead++)
            {
                int at = OffBlasts + 16 + (blastsRead % BlastRing) * 16;
                Vector3 c = new Vector3(Link.ReadFloatAt(at) * k, Link.ReadFloatAt(at + 4) * k, -Link.ReadFloatAt(at + 8) * k);
                float radius = Mathf.Max(0.5f, Link.ReadFloatAt(at + 12)) * k;
                float reach = radius * 2.3f;
                int n = Physics.OverlapSphereNonAlloc(c, reach, inBlast, ~0, QueryTriggerInteraction.Collide);
                blasted.Clear();
                int hurt = 0, felled = 0;
                for (int i = 0; i < n; i++)
                {
                    Collider col = inBlast[i];
                    if (col == null || (player != null && col.transform.IsChildOf(player)) || Blocks.IsOurs(col.transform)) continue;
                    EnemyHealth enemy = col.GetComponentInParent<EnemyHealth>();
                    if (enemy != null)
                    {
                        if (!blasted.Add(enemy.GetInstanceID()) || enemy.Health <= 0) continue;
                        float d = Vector3.Distance(c, enemy.transform.position);
                        // The Forest's bombs: under 10.5 units blown apart, then hurt, then knocked.
                        if (enemy.targetSwitcher != null) enemy.targetSwitcher.attackerType = 4;
                        enemy.Explosion(Mathf.Max(0.01f, d / radius * 10.5f));
                        hurt++;
                        continue;
                    }
                    animalHealth animal = IsCreature(col) ? col.GetComponentInParent<animalHealth>() : null;
                    if (animal != null)
                    {
                        if (!blasted.Add(animal.GetInstanceID())) continue;
                        if (Vector3.Distance(c, animal.transform.position) < radius * 1.5f) animal.Explosion();
                        else animal.SendMessage("Hit", 10, SendMessageOptions.DontRequireReceiver);
                        hurt++;
                        continue;
                    }
                    // Trees close to the blast come down, The Forest's way for its own bombs
                    // (TreeHealth.Explosion: the trunk falls, its stump stays).
                    TreeHealth tree = col.GetComponentInParent<TreeHealth>();
                    if (tree != null)
                    {
                        if (!blasted.Add(tree.GetInstanceID())) continue;
                        Vector3 foot = tree.transform.position;
                        float dh = new Vector2(foot.x - c.x, foot.z - c.z).magnitude;
                        if (dh < radius * 1.6f && c.y > foot.y - radius && c.y < foot.y + radius * 2f)
                        {
                            tree.SendMessage("Explosion", dh, SendMessageOptions.DontRequireReceiver);
                            felled++;
                        }
                        continue;
                    }
                    // Loose things fly.
                    Rigidbody rb = col.attachedRigidbody;
                    Corpses.Wake(rb);
                    if (rb != null && !rb.isKinematic && rb.mass <= 200f && blasted.Add(rb.GetInstanceID()))
                        rb.AddExplosionForce(12f * k, c, reach, 0.6f, ForceMode.VelocityChange);
                }
                if (felled > 0) Solids.ForgetAll(); // the trunks are gone (fallen logs move on their own)
                Plugin.Log.LogInfo("ForestCraft: Minecraft explosion (radius " + (radius / k).ToString("0.0") + " blocks), " + hurt + " creatures caught, " + felled + " trees felled");
            }
        }
    }
}
