using System.Collections.Generic;
using UnityEngine;
using TheForest.Utils;

namespace ForestCraft
{
    // Minecraft arrows that hit one of The Forest's creatures: Minecraft's own arrow stays
    // planted where it went in (child of the hit body part, so it follows the animation), and
    // whatever falls loose there in the next moment (a bird, a rabbit, a ragdoll) is pushed on
    // in the arrow's direction.
    static class StuckArrows
    {
        const int Max = 40;
        static readonly Queue<GameObject> planted = new Queue<GameObject>();

        struct Pending { public Vector3 at, velocity; public float until; }
        static readonly List<Pending> pushes = new List<Pending>();
        static readonly HashSet<int> pushed = new HashSet<int>();
        static readonly Collider[] near = new Collider[32];

        // The arrow Minecraft was drawing there, copied as it looked at the moment of the hit,
        // planted with its point a little way into the body and carried along by it.
        public static void Plant(Transform into, Vector3 point, Vector3 dir, Vector3 mcEnd)
        {
            if (into == null) return;
            GameObject arrow = Entities.CopyNear(mcEnd, 2.5f);
            if (arrow == null) return;
            float k = Link.Scale;
            // Minecraft's arrow is drawn around its position, the point about 0.45 block ahead.
            arrow.transform.position = point - dir * (0.3f * k);
            arrow.transform.SetParent(into, true);
            planted.Enqueue(arrow);
            while (planted.Count > Max)
            {
                GameObject old = planted.Dequeue();
                if (old == null) continue;
                MeshFilter mf = old.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null) Object.Destroy(mf.sharedMesh);
                Object.Destroy(old);
            }
        }

        public static void Push(Vector3 at, Vector3 dir, float speed)
        {
            pushes.Add(new Pending { at = at, velocity = dir * Mathf.Clamp(speed, 2f, 25f), until = Time.time + 0.5f });
            pushed.Clear();
        }

        // Each frame for half a second after a hit: loose bodies near the hit get the arrow's push once.
        public static void Update()
        {
            if (pushes.Count == 0) return;
            float now = Time.time;
            float radius = 1.2f * Link.Scale;
            for (int p = pushes.Count - 1; p >= 0; p--)
            {
                Pending push = pushes[p];
                if (now > push.until) { pushes.RemoveAt(p); continue; }
                int n = Physics.OverlapSphereNonAlloc(push.at, radius, near, ~0, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < n; i++)
                {
                    Rigidbody body = near[i] != null ? near[i].attachedRigidbody : null;
                    if (body == null || body.isKinematic) continue;
                    if (LocalPlayer.Rigidbody != null && body == LocalPlayer.Rigidbody) continue;
                    if (body.mass > 60f) continue; // a log, a boulder: not moved by an arrow
                    if (!pushed.Add(body.GetInstanceID())) continue;
                    body.AddForce(push.velocity, ForceMode.VelocityChange);
                }
            }
        }
    }
}
