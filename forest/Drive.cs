using HarmonyLib;
using UnityEngine;
using TheForest.Utils;

namespace ForestCraft
{
    static class LocalPlayerSafe
    {
        public static bool InWorld()
        {
            try { return LocalPlayer.IsInWorld; }
            catch { return false; }
        }

        public static bool Scripted()
        {
            try
            {
                Transform body = LocalPlayer.Transform;
                if (body != null && body.parent != null) return true;
                FirstPersonCharacter fpc = LocalPlayer.FpCharacter;
                if (fpc != null && fpc.Locked) return true;
                // FindObjectOfType walks every object in the scene: once the plane is gone it was
                // searched for every frame. Look again only every few seconds.
                if (crash == null && Time.realtimeSinceStartup >= nextCrashSearch)
                {
                    nextCrashSearch = Time.realtimeSinceStartup + 5f;
                    crash = UnityEngine.Object.FindObjectOfType<PlaneCrashController>();
                }
                if (crash != null && !crash.Crashed) return true;
            }
            catch { }
            return false;
        }

        static PlaneCrashController crash;
        static float nextCrashSearch;

        public static bool InMenu()
        {
            try { return LocalPlayer.IsInPauseMenu || LocalPlayer.IsInInventory || LocalPlayer.IsInBook; }
            catch { return false; }
        }

        public static Camera Camera()
        {
            try { return LocalPlayer.MainCam; }
            catch { return null; }
        }
    }

    static class Drive
    {
        static bool held;
        static bool rotatorWasEnabled = true;
        static bool camRotatorWasEnabled = true;
        static Renderer[] hiddenBody;
        static bool savedKinematic;
        static bool savedDetect = true;
        static RigidbodyInterpolation savedInterpolation;

        public static Vector3 Feet()
        {
            Transform root = LocalPlayer.Transform;
            if (root == null) return Vector3.zero;
            FirstPersonCharacter fpc = LocalPlayer.FpCharacter;
            if (fpc != null && fpc.capsule != null)
            {
                CapsuleCollider capsule = fpc.capsule;
                Vector3 center = root.TransformPoint(capsule.center);
                center.y -= capsule.height * 0.5f * Mathf.Abs(root.lossyScale.y);
                return center;
            }
            return root.position;
        }

        static int lastTick = -1;
        static float tickSeenAt;
        static Vector3 fromFeet, toFeet;

        // Minecraft moves 20 times a second. Like its own renderer, draw between the
        // previous and the current tick, timed on this side so the two frame rates don't matter.
        static Vector3 SmoothFeet(Link.McSnapshot mc)
        {
            float now = Time.realtimeSinceStartup;
            if (mc.tick != lastTick)
            {
                Vector3 prev = Link.McToUnity(mc.px, mc.py, mc.pz);
                Vector3 cur = Link.McToUnity(mc.x, mc.y, mc.z);
                // Big jump (teleport, first frame): no lerp across it.
                if (lastTick == -1 || (cur - prev).sqrMagnitude > 25f) prev = cur;
                fromFeet = prev;
                toFeet = cur;
                tickSeenAt = now;
                lastTick = mc.tick;
            }
            float t = Mathf.Clamp01((now - tickSeenAt) / 0.05f);
            return Vector3.Lerp(fromFeet, toFeet, t);
        }

        /// <summary>The Forest player's standing height in Unity units (capsule), or 0.</summary>
        public static float ForestHeight()
        {
            Transform root = LocalPlayer.Transform;
            FirstPersonCharacter fpc = LocalPlayer.FpCharacter;
            if (root == null || fpc == null || fpc.capsule == null) return 0f;
            return fpc.capsule.height * Mathf.Abs(root.lossyScale.y);
        }

        public static void Apply(float yaw, float pitch, int mode)
        {
            Link.McSnapshot mc;
            if (!Link.ReadMc(out mc)) return;
            Transform root = LocalPlayer.Transform;
            FirstPersonCharacter fpc = LocalPlayer.FpCharacter;
            if (root == null || fpc == null) return;
            Vector3 feetNow = Feet();
            Vector3 targetFeet = SmoothFeet(mc);
            LastFeet = targetFeet;
            Vector3 rootPos = root.position + (targetFeet - feetNow);
            Rigidbody body = LocalPlayer.Rigidbody;
            if (body != null)
            {
                if (!held)
                {
                    savedKinematic = body.isKinematic;
                    savedDetect = body.detectCollisions;
                    savedInterpolation = body.interpolation;
                }
                // PhysX must not integrate a velocity of its own, or it walks the body
                // forward and the next Minecraft sample pulls it back.
                body.isKinematic = true;
                // Kinematic, so nothing pushes it; but its colliders stay on: The Forest's own
                // triggers (cave entrances, water, areas) must still see the player go through.
                body.detectCollisions = true;
                body.interpolation = RigidbodyInterpolation.None;
                body.velocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
                body.position = rootPos;
                body.rotation = Quaternion.Euler(0f, yaw, 0f);
            }
            root.position = rootPos;
            root.rotation = Quaternion.Euler(0f, yaw, 0f);
            LastRootSet = rootPos;
            HasLastRoot = true;
            PoseCamera(targetFeet, yaw, pitch, mode);
            fpc.MovementLocked = true;
            HoldRotators();
            HideBody();
            held = true;
        }

        // ---- Minecraft's camera, drawn by The Forest's camera ----
        // The Forest's camera hangs off an animated head, so it carried The Forest's walk feel.
        // It is now placed like Minecraft's: eye height (sneak lowers it), view bobbing,
        // the sprint FOV, and F5 behind / in front. Applied in onPreCull so nothing moves it after.
        public static float ThirdPersonDistance = 4f;
        public static Vector3 LastFeet;
        static bool camHooked, camPosed;
        static Vector3 camPos;
        static Quaternion camRot;
        static float camFov, savedFov = -1f, eyeSmooth = 1.62f;
        static readonly RaycastHit[] camHits = new RaycastHit[16];

        static void PoseCamera(Vector3 feet, float yaw, float pitch, int mode)
        {
            float eye, fov, walk, bob;
            Link.ReadView(out eye, out fov, out walk, out bob);
            eyeSmooth = Mathf.Lerp(eyeSmooth, eye, 1f - Mathf.Exp(-Time.unscaledDeltaTime * 14f));
            float k = Link.Scale;
            Vector3 eyePos = feet + new Vector3(0f, eyeSmooth * k, 0f);
            Quaternion look = Quaternion.Euler(pitch, yaw, 0f);
            Vector3 forward = look * Vector3.forward;
            Quaternion rot = look;
            Vector3 pos = eyePos;
            if (mode != 0)
            {
                Vector3 dir = mode == 1 ? -forward : forward;
                float dist = Clearance(eyePos, dir, 4f * k);
                ThirdPersonDistance = dist / k;
                pos = eyePos + dir * dist;
                if (mode == 2) rot = Quaternion.Euler(-pitch, yaw + 180f, 0f);
            }
            else
            {
                ThirdPersonDistance = 4f;
            }
            if (bob != 0f)
            {
                // GameRenderer.bobView, inverted from "move the world" to "move the camera".
                float g = walk * Mathf.PI;
                float side = Mathf.Sin(g) * bob * 0.5f;
                float up = Mathf.Abs(Mathf.Cos(g) * bob);
                float roll = Mathf.Sin(g) * bob * 3f;
                float dip = Mathf.Abs(Mathf.Cos(g - 0.2f) * bob) * 5f;
                rot = rot * Quaternion.Euler(dip, 0f, -roll);
                pos += rot * new Vector3(-side, up, 0f) * k;
            }
            camPos = pos;
            camRot = rot;
            camFov = fov;
            camPosed = true;
            Camera cam = LocalPlayerSafe.Camera();
            if (cam != null)
            {
                if (savedFov < 0f) savedFov = cam.fieldOfView;
                Place(cam);
            }
            if (!camHooked)
            {
                Camera.onPreCull += PreCull;
                camHooked = true;
            }
        }

        static void PreCull(Camera cam)
        {
            if (!camPosed || cam == null) return;
            if (cam != LocalPlayerSafe.Camera()) return;
            Place(cam);
        }

        static void Place(Camera cam)
        {
            Transform t = cam.transform;
            t.position = camPos;
            t.rotation = camRot;
            if (camFov > 0f) cam.fieldOfView = camFov;
        }

        // Pull the 3rd-person camera in front of trees/rocks/terrain, like Minecraft's getMaxZoom.
        static float Clearance(Vector3 from, Vector3 dir, float max)
        {
            Transform player = LocalPlayer.Transform;
            int n = Physics.SphereCastNonAlloc(from, 0.1f, dir, camHits, max, ~0, QueryTriggerInteraction.Ignore);
            float best = max;
            for (int i = 0; i < n; i++)
            {
                Collider c = camHits[i].collider;
                if (c == null) continue;
                if (player != null && c.transform.IsChildOf(player)) continue;
                Rigidbody rb = c.attachedRigidbody;
                if (rb != null && !rb.isKinematic) continue;
                if (camHits[i].distance <= 0f) continue;
                if (camHits[i].distance < best) best = camHits[i].distance;
            }
            return Mathf.Max(0.3f, best - 0.1f);
        }

        // Where Apply put the body last: if it is somewhere else next frame, The Forest moved it.
        public static Vector3 LastRootSet;
        public static bool HasLastRoot;

        public static void Release()
        {
            HasLastRoot = false;
            lastTick = -1;
            camPosed = false;
            if (savedFov > 0f)
            {
                Camera cam = LocalPlayerSafe.Camera();
                if (cam != null) cam.fieldOfView = savedFov;
                savedFov = -1f;
            }
            if (!held) return;
            held = false;
            FirstPersonCharacter fpc = LocalPlayer.FpCharacter;
            if (fpc != null) fpc.MovementLocked = false;
            Rigidbody body = LocalPlayer.Rigidbody;
            if (body != null)
            {
                body.isKinematic = savedKinematic;
                body.detectCollisions = savedDetect;
                body.interpolation = savedInterpolation;
            }
            if (LocalPlayer.MainRotator != null) LocalPlayer.MainRotator.enabled = rotatorWasEnabled;
            if (LocalPlayer.CamRotator != null) LocalPlayer.CamRotator.enabled = camRotatorWasEnabled;
            ShowBody();
        }

        // Everything The Forest's player shows (body, arms, the axe or any item picked up and
        // equipped later, armor...) stays hidden while Minecraft has the body. Re-checked a few
        // times a second, not only once: items equipped afterwards used to float above Steve.
        static readonly System.Collections.Generic.List<Renderer> hidden = new System.Collections.Generic.List<Renderer>();
        static readonly System.Collections.Generic.List<Renderer> scan = new System.Collections.Generic.List<Renderer>(512);
        static float nextHideScan;

        static void HideBody()
        {
            if (hiddenBody != null && Time.realtimeSinceStartup < nextHideScan) return;
            nextHideScan = Time.realtimeSinceStartup + 0.25f;
            hiddenBody = hiddenBody ?? new Renderer[0];
            HideUnder(LocalPlayer.GameObject != null ? LocalPlayer.GameObject.transform : null);
            // Held items hang off the camera rig, which isn't always under the player object.
            Camera cam = LocalPlayerSafe.Camera();
            if (cam != null) HideUnder(cam.transform, true);
        }

        static void HideUnder(Transform t, bool meshesOnly = false)
        {
            if (t == null) return;
            // The player carries hundreds of renderers (every item it can hold): fill a reused
            // list rather than allocating an array 4 times a second (garbage = GC hitches).
            scan.Clear();
            t.GetComponentsInChildren<Renderer>(true, scan);
            for (int i = 0; i < scan.Count; i++)
            {
                Renderer r = scan[i];
                if (r == null || !r.enabled) continue;
                // The Forest's map, out in Steve's hands: it stays visible.
                if (HeldMap.Keeps(r)) continue;
                // Under the camera only models (held items, arms): rain, splashes and other
                // camera effects are particles and must keep showing.
                if (meshesOnly && !(r is MeshRenderer) && !(r is SkinnedMeshRenderer)) continue;
                r.enabled = false;
                hidden.Add(r);
            }
        }

        static void ShowBody()
        {
            if (hiddenBody == null) return;
            for (int i = 0; i < hidden.Count; i++)
            {
                if (hidden[i] != null) hidden[i].enabled = true;
            }
            hidden.Clear();
            hiddenBody = null;
        }

        static void HoldRotators()
        {
            if (LocalPlayer.MainRotator != null)
            {
                if (!held) rotatorWasEnabled = LocalPlayer.MainRotator.enabled;
                LocalPlayer.MainRotator.enabled = false;
            }
            if (LocalPlayer.CamRotator != null)
            {
                if (!held) camRotatorWasEnabled = LocalPlayer.CamRotator.enabled;
                LocalPlayer.CamRotator.enabled = false;
            }
        }
    }

    [HarmonyPatch(typeof(FirstPersonCharacter), "FixedUpdate")]
    static class PatchFixed
    {
        static bool Prefix() { return !Link.Driving; }
    }

    [HarmonyPatch(typeof(FirstPersonCharacter), "Update")]
    static class PatchUpdate
    {
        static bool Prefix() { return !Link.Driving; }
    }
}
