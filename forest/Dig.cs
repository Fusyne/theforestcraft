using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using TheForest.Utils;

namespace ForestCraft
{
    // Holding attack on one of The Forest's trees chops it with The Forest's own TreeHealth
    // (one DamageTree per swing). When it falls, Minecraft gets oak logs, like breaking a log.
    static class Trees
    {
        static float nextHit;
        static TreeHealth target;
        static int logSeq;
        static bool warned;
        static Vector3 aimPoint;
        static Collider lastAimed; // named only when logged (a name is a new string each time)
        static readonly RaycastHit[] hits = new RaycastHit[16];

        public static void Update(int input)
        {
            bool attack = (input & 128) != 0 && Link.Driving && Link.ReadMcInt(128) == 1;
            if (!attack) { target = null; return; }
            Camera cam = LocalPlayerSafe.Camera();
            if (cam == null) return;
            TreeHealth tree = Aimed(cam.transform.position, cam.transform.forward, 5.5f * Link.Scale);
            if (tree == null)
            {
                if (target != null || !warned) { warned = true; Plugin.Log.LogInfo("ForestCraft: aiming at a Forest object without TreeHealth: " + (lastAimed == null ? "?" : (lastAimed.transform.parent != null ? lastAimed.transform.parent.name + "/" : "") + lastAimed.name)); }
                target = null;
                return;
            }
            float now = Time.time;
            if (tree != target)
            {
                target = tree;
                nextHit = now + 0.15f;
                Plugin.Log.LogInfo("ForestCraft: chopping " + tree.name + " (health " + tree.Health + ")");
                return;
            }
            if (now < nextHit) return;
            nextHit = now + 0.2f;
            if (tree.Health <= 0) return;
            // A standing tree's first chops only swap it for its notched version (SpawnedCutTree,
            // DoSpawnCutTree): it falls, and gives its logs, when that one runs out of health.
            bool falls = tree.SpawnedCutTree && tree.Health <= 1;
            tree.DamageTree();
            ForestEvents.Emit(falls ? ForestEvents.TreeFelled : ForestEvents.TreeHit, aimPoint);
            if (tree.Health <= 0)
            {
                if (falls)
                {
                    logSeq++;
                    Link.WriteLogs(logSeq, 4);
                }
                Solids.ForgetAll();
                Plugin.Log.LogInfo("ForestCraft: " + (falls ? "tree felled: " : "tree notched: ") + tree.name);
                target = null;
            }
        }

        static TreeHealth Aimed(Vector3 from, Vector3 dir, float reach)
        {
            Transform player = LocalPlayer.Transform;
            int n = Physics.RaycastNonAlloc(from, dir, hits, reach, ~0, QueryTriggerInteraction.Collide);
            float best = float.MaxValue;
            TreeHealth found = null;
            for (int i = 0; i < n; i++)
            {
                Collider c = hits[i].collider;
                if (c == null) continue;
                if (player != null && c.transform.IsChildOf(player)) continue;
                if (hits[i].distance < best) lastAimed = c;
                TreeHealth th = c.GetComponentInParent<TreeHealth>();
                if (th == null) continue;
                if (hits[i].distance < best) { best = hits[i].distance; found = th; aimPoint = hits[i].point; }
            }
            return found;
        }
    }
}
