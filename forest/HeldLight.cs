using UnityEngine;

namespace ForestCraft
{
    // A torch, lantern, glowstone... in Steve's hand lights The Forest around him (Minecraft
    // sends the item's light level at OFF_MC+192), like the lighter does in The Forest.
    static class HeldLight
    {
        static Light light;
        public static bool On { get { return light != null && light.enabled; } }

        public static void Update(bool thirdPerson, Vector3 feet)
        {
            int level = Link.Driving ? Link.ReadMcInt(192) : 0;
            if (level <= 0 || level > 15)
            {
                if (light != null && light.enabled) light.enabled = false;
                return;
            }
            if (light == null)
            {
                var go = new GameObject("ForestCraft held light");
                Object.DontDestroyOnLoad(go);
                light = go.AddComponent<Light>();
                light.type = LightType.Point;
                light.color = new Color(1f, 0.78f, 0.5f);
                // No shadows: the light sits in Steve's hand, his own body would shadow everything.
                light.shadows = LightShadows.None;
                light.renderMode = LightRenderMode.ForcePixel;
            }
            float k = Link.Scale;
            Camera cam = LocalPlayerSafe.Camera();
            Vector3 at;
            if (!thirdPerson && cam != null) at = cam.transform.position + cam.transform.forward * 0.6f * k + cam.transform.right * 0.35f * k - Vector3.up * 0.3f * k;
            else
            {
                // F5: at Steve's right hand, a little in front of him.
                Vector3 fwd = cam != null ? Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up).normalized : Vector3.forward;
                Vector3 right = Vector3.Cross(Vector3.up, fwd);
                at = feet + Vector3.up * 1.1f * k + fwd * 0.45f * k + right * 0.35f * k;
            }
            light.transform.position = at;
            // Minecraft light fades over (level) blocks; a soft warm glow, not a floodlight.
            light.range = Mathf.Max(3f, level * 0.75f) * k;
            light.intensity = 0.35f + level / 15f * 0.55f;
            if (!light.enabled) light.enabled = true;
        }
    }
}
