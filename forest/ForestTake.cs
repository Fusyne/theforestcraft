using HarmonyLib;

namespace ForestCraft
{
    // While Minecraft has the body, The Forest's own inventory (and survival book) stay shut:
    // the inventory is Minecraft's (I). E keeps being The Forest's "take" (pick up, open, sleep...).
    static class ForestButtons
    {
        static readonly string[] Muted = { "Inventory", "Survival Book", "SurvivalBook", "Book" };

        public static bool Handle(string button, ref bool result)
        {
            if (!Link.Driving || System.Array.IndexOf(Muted, button) < 0) return true;
            result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(TheForest.Utils.Input), "GetButtonDown")]
    static class ForestButtonsDown
    {
        static bool Prefix(string __0, ref bool __result) { return ForestButtons.Handle(__0, ref __result); }
    }

    [HarmonyPatch(typeof(TheForest.Utils.Input), "GetButton")]
    static class ForestButtonsHeld
    {
        static bool Prefix(string __0, ref bool __result) { return ForestButtons.Handle(__0, ref __result); }
    }

    [HarmonyPatch(typeof(TheForest.Utils.Input), "GetButtonUp")]
    static class ForestButtonsUp
    {
        static bool Prefix(string __0, ref bool __result) { return ForestButtons.Handle(__0, ref __result); }
    }
}
