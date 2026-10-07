using System.Collections;
using HarmonyLib;
using UnityEngine;

namespace ForestCraft
{
    // The opening after the crash: The Forest lies its player on the plane's floor (the crawl, the
    // scene with Timmy), then plays a slow stand-up before handing over the controls. With
    // Minecraft linked, both are left out: straight from the crash to the wrecked plane, standing.
    // The Forest's own end of that scene (TriggerCutScene.CleanUp, which clears the cutscene's
    // props and starts startPlayerInPlane) is run at once instead of after the crawl, and
    // TriggerCutScene.FastStart (its developers' quick start) skips the stand-up.
    [HarmonyPatch(typeof(TriggerCutScene), "startTimmyCutscene")]
    static class SkipFloorScene
    {
        static bool Prefix(TriggerCutScene __instance, ref IEnumerator __result)
        {
            if (!Link.McAlive()) return true;
            __result = Nothing();
            try
            {
                Traverse t = Traverse.Create(__instance);
                t.Method("disablePlaneAnim").GetValue();
                t.Method("ShowEnemies").GetValue();
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("ForestCraft: skipping the floor scene: " + e.Message); }
            StandUpFast.Arm();
            __instance.StartCoroutine("CleanUp");
            Plugin.Log.LogInfo("ForestCraft: crash opening: floor scene and stand-up skipped");
            return false;
        }

        static IEnumerator Nothing() { yield break; }
    }

    // Skipping the flight with Space goes straight to the scene's end, without the floor scene:
    // the stand-up is left out there too.
    [HarmonyPatch(typeof(TriggerCutScene), "startPlayerInPlane")]
    static class SkipStandUp
    {
        static void Prefix()
        {
            if (!Link.McAlive()) return;
            StandUpFast.Arm();
            Plugin.Log.LogInfo("ForestCraft: crash opening: stand-up skipped");
        }
    }

    // The scene's end runs once: if The Forest's own sequence calls it again later, it would
    // stop the player's start in the plane half way and put the player back on its spot.
    [HarmonyPatch(typeof(TriggerCutScene), "CleanUp")]
    static class CleanUpOnce
    {
        static TriggerCutScene done;

        static bool Prefix(TriggerCutScene __instance, ref IEnumerator __result)
        {
            if (done != __instance) { done = __instance; return true; }
            __result = Nothing();
            return false;
        }

        static IEnumerator Nothing() { yield break; }
    }

    // FastStart only for this start (it is a console switch of The Forest), off again afterwards.
    // Minecraft only gets the body once The Forest has finished putting the player in the plane
    // (startPlayerInPlane: about 3.5 s, the wreck settling, its floor's colliders moving into
    // place); what was scanned of those colliders before then is thrown away, or Minecraft stood
    // on the floor where it was a moment earlier and fell through the real one.
    static class StandUpFast
    {
        static bool armed, was, holding;
        static float until, holdUntil;

        /// <summary>Minecraft may have the body (not in the middle of the start in the plane).</summary>
        public static bool Ready { get { return !holding; } }

        public static void Arm()
        {
            if (!armed) was = TriggerCutScene.FastStart;
            armed = true;
            float now = Time.realtimeSinceStartup;
            until = now + 15f;
            holding = true;
            holdUntil = now + 4f;
            TriggerCutScene.FastStart = true;
        }

        public static void Update()
        {
            float now = Time.realtimeSinceStartup;
            if (holding && now >= holdUntil)
            {
                holding = false;
                Solids.ForgetAll();
                Plugin.Log.LogInfo("ForestCraft: the player is up in the plane: its floor is scanned again before Minecraft takes the body");
            }
            if (!armed || now < until) return;
            armed = false;
            TriggerCutScene.FastStart = was;
        }
    }
}
