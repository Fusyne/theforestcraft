using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ForestCraft
{
    // The Forest's dead bodies are ragdolls: a dozen physics bodies each, which keep simulating
    // (and twitching on slopes) long after they fell. With a crowd killed around you that is a lot
    // of physics every frame. Each new one (clsragdollify.metgoragdoll) is watched: once it has
    // lain still for 2 s (or after 15 s anyway) its bodies are frozen where they lie. Cutting it
    // up still works; an explosion wakes it up again.
    static class Corpses
    {
        class Body
        {
            public Transform root;
            public Rigidbody[] bodies;
            public float born, calmSince = -1f;
            public bool frozen;
        }

        static readonly List<Body> bodies = new List<Body>();
        static readonly HashSet<Transform> ours = new HashSet<Transform>();
        static float next;
        static int frozenCount;

        public static void Born(Transform rag)
        {
            if (rag == null) return;
            bodies.Add(new Body { root = rag, bodies = rag.GetComponentsInChildren<Rigidbody>(), born = Time.time });
        }

        /// <summary>A ragdoll ForestCraft drives itself (a creature hanging from the lead).</summary>
        public static void Ours(Transform rag, bool mine)
        {
            if (rag == null) return;
            if (mine) ours.Add(rag); else ours.Remove(rag);
        }

        public static void Update()
        {
            if (Time.time < next) return;
            next = Time.time + 0.5f;
            float k = Link.Scale;
            float still = 0.3f * k;
            frozenCount = 0;
            for (int i = bodies.Count - 1; i >= 0; i--)
            {
                Body b = bodies[i];
                if (b.root == null) { bodies.RemoveAt(i); continue; }
                if (b.frozen) { frozenCount++; continue; }
                if (ours.Contains(b.root)) { b.calmSince = -1f; continue; }
                bool calm = true;
                foreach (Rigidbody rb in b.bodies)
                {
                    if (rb == null || rb.isKinematic) continue;
                    if (rb.velocity.sqrMagnitude > still * still || rb.angularVelocity.sqrMagnitude > 1f) { calm = false; break; }
                }
                if (calm) { if (b.calmSince < 0f) b.calmSince = Time.time; }
                else b.calmSince = -1f;
                if ((b.calmSince >= 0f && Time.time - b.calmSince > 2f) || Time.time - b.born > 15f)
                {
                    foreach (Rigidbody rb in b.bodies)
                    {
                        if (rb == null || rb.isKinematic) continue;
                        rb.velocity = Vector3.zero;
                        rb.angularVelocity = Vector3.zero;
                        rb.isKinematic = true;
                    }
                    b.frozen = true;
                    frozenCount++;
                }
            }
        }

        /// <summary>A frozen body caught by a blast moves again.</summary>
        public static void Wake(Rigidbody hit)
        {
            if (hit == null || !hit.isKinematic) return;
            foreach (Body b in bodies)
            {
                if (!b.frozen || b.root == null || !hit.transform.IsChildOf(b.root)) continue;
                foreach (Rigidbody rb in b.bodies) if (rb != null) rb.isKinematic = false;
                b.frozen = false;
                b.born = Time.time;
                b.calmSince = -1f;
                return;
            }
        }

        public static string Info()
        {
            return "corpses " + bodies.Count + " (" + frozenCount + " frozen)";
        }
    }

    [HarmonyPatch(typeof(clsragdollify), "metgoragdoll")]
    static class CorpseBorn
    {
        static void Postfix(Transform __result) { Corpses.Born(__result); }
    }
}
