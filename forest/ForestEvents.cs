using UnityEngine;

namespace ForestCraft
{
    // Things happening in The Forest that Minecraft should make heard (and seen): an axe biting
    // into a tree, a tree coming down, a blow landing on a cannibal or an animal, a bush or a
    // rock hit. Minecraft plays its own sound and chips for each (ForestEvents.java).
    // Ring at 0xA18000: written count, then 64 entries of (kind, x, y, z) in Minecraft units.
    static class ForestEvents
    {
        public const int TreeHit = 1, TreeFelled = 2, Flesh = 3, Plant = 4, Rock = 5, Metal = 6, ArrowFlesh = 7;
        const int Off = 0xA18000;
        const int Ring = 64;
        static int written = -1;

        public static void Emit(int kind, Vector3 unity)
        {
            if (Link.View == System.IntPtr.Zero) return;
            if (written < 0) written = System.Math.Max(0, Link.ReadIntAt(Off));
            float k = Link.Scale;
            int at = Off + 16 + (written % Ring) * 16;
            Link.WriteIntAt(at, kind);
            Link.WriteFloatAt(at + 4, unity.x / k);
            Link.WriteFloatAt(at + 8, unity.y / k);
            Link.WriteFloatAt(at + 12, -unity.z / k);
            written++;
            Link.WriteIntAt(Off, written);
        }
    }
}
