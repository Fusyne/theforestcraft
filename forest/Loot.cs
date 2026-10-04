using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using HarmonyLib;
using TheForest.Items;
using TheForest.Items.Inventory;
using TheForest.Items.World;
using UnityEngine;

namespace ForestCraft
{
    // What you pick up in The Forest becomes Minecraft items. The Forest's own pickup flow is kept
    // (look at it, press E: the "take" icon, sounds, the object disappearing); only the moment it
    // would enter The Forest's inventory is redirected to Minecraft. Items with no Minecraft
    // counterpart stay in The Forest's inventory, and are logged so the table can grow.
    static class Loot
    {
        const int OffGive = 0xA88000;
        const int Ring = 64;
        const int Entry = 64;

        static int written;
        public static PickUp Hovered; // the pickup The Forest shows a "take" icon for

        // Forest item name (lowercase, "contains") -> Minecraft item id. First match wins.
        static readonly string[,] Table =
        {
            { "planeaxe", "stone_axe" }, { "modernaxe", "iron_axe" }, { "climbingaxe", "iron_pickaxe" },
            { "chainsaw", "diamond_axe" }, { "craftedaxe", "stone_axe" }, { "axe", "stone_axe" },
            { "katana", "iron_sword" }, { "machete", "iron_sword" }, { "sword", "iron_sword" },
            { "club", "wooden_sword" }, { "spear", "wooden_sword" },
            { "arrow", "arrow" }, { "bow", "bow" },
            { "stick", "stick" }, { "rock", "cobblestone" }, { "stone", "cobblestone" }, { "log", "oak_log" },
            { "leaf", "oak_leaves" }, { "rope", "string" }, { "cloth", "white_wool" }, { "feather", "feather" },
            { "skull", "skeleton_skull" }, { "bone", "bone" }, { "tooth", "bone_meal" }, { "sap", "slime_ball" },
            { "coin", "gold_nugget" }, { "battery", "redstone" }, { "flaregun", "fire_charge" }, { "flare", "torch" },
            { "lighter", "flint_and_steel" }, { "torch", "torch" }, { "molotov", "fire_charge" },
            { "dynamite", "tnt" }, { "bomb", "tnt" }, { "booze", "potion" }, { "soda", "honey_bottle" },
            { "snack", "cookie" }, { "candy", "cookie" }, { "cereal", "bread" }, { "cooked", "cooked_beef" },
            { "meat", "beef" }, { "fish", "cod" }, { "berr", "sweet_berries" }, { "mushroom", "brown_mushroom" },
            { "egg", "egg" }, { "aloe", "green_dye" }, { "coneflower", "purple_dye" }, { "chicory", "light_blue_dye" },
            { "marigold", "yellow_dye" }, { "seed", "wheat_seeds" }, { "skin", "leather" }, { "fur", "rabbit_hide" },
            { "turtle", "turtle_scute" }, { "watch", "clock" }, { "compass", "compass" }, { "map", "map" },
            { "rebreather", "turtle_helmet" }, { "cassette", "music_disc_cat" },
        };

        public static void Reset(IntPtr view)
        {
            Marshal.WriteInt32(view, OffGive, 0);
            written = 0;
        }

        public static string MinecraftFor(string forestName)
        {
            if (string.IsNullOrEmpty(forestName)) return null;
            string n = forestName.ToLowerInvariant();
            for (int i = 0; i < Table.GetLength(0); i++)
                if (n.Contains(Table[i, 0])) return "minecraft:" + Table[i, 1];
            return null;
        }

        public static void Give(string id, int count)
        {
            IntPtr view = Link.View;
            if (view == IntPtr.Zero || string.IsNullOrEmpty(id) || count <= 0) return;
            int at = OffGive + 16 + (written % Ring) * Entry;
            byte[] bytes = System.Text.Encoding.ASCII.GetBytes(id);
            int len = Math.Min(bytes.Length, Entry - 8);
            Marshal.WriteInt32(view, at, count);
            Marshal.WriteInt32(view, at + 4, len);
            Marshal.Copy(bytes, 0, new IntPtr(view.ToInt64() + at + 8), len);
            written++;
            Marshal.WriteInt32(view, OffGive, written);
        }

        static bool dumped;

        // Once: every Forest item name to forest_items.txt (with its Minecraft match, if any).
        public static void DumpNames()
        {
            if (dumped) return;
            dumped = true;
            try
            {
                var lines = new List<string>();
                foreach (Item item in ItemDatabase.Items)
                {
                    if (item == null) continue;
                    lines.Add(item._id + "\t" + item._name + "\t" + (MinecraftFor(item._name) ?? "-"));
                }
                File.WriteAllLines(Path.Combine(Path.GetDirectoryName(Link.FilePath), "forest_items.txt"), lines.ToArray());
                Plugin.Log.LogInfo("ForestCraft: " + lines.Count + " Forest item names written to forest_items.txt");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("ForestCraft: item list not written: " + e.Message);
            }
        }
    }

    [HarmonyPatch(typeof(PlayerInventory), "AddItem")]
    static class LootAddItem
    {
        static bool Prefix(int __0, int __1, ref bool __result) { return LootRedirect.Redirect(__0, __1, ref __result); }
    }

    [HarmonyPatch(typeof(PlayerInventory), "AddItemNF")]
    static class LootAddItemNF
    {
        static bool Prefix(int __0, int __1, ref bool __result) { return LootRedirect.Redirect(__0, __1, ref __result); }
    }

    static class LootRedirect
    {
        public static bool Redirect(int itemId, int amount, ref bool result)
        {
            if (!Link.Driving || amount <= 0) return true;
            Item item = null;
            try { item = ItemDatabase.ItemById(itemId); } catch (Exception) { }
            string name = item != null ? item._name : ("#" + itemId);
            string id = Loot.MinecraftFor(name);
            if (id == null)
            {
                Plugin.Log.LogInfo("ForestCraft: picked up " + name + " x" + amount + " (no Minecraft match, kept by The Forest)");
                return true;
            }
            Loot.Give(id, amount);
            Plugin.Log.LogInfo("ForestCraft: picked up " + name + " x" + amount + " -> " + id);
            result = true;  // The Forest believes it went in: the world object goes away as usual
            return false;
        }
    }

    // Which pickup The Forest is showing the "take" icon for: E then means "take", not
    // "open Minecraft's inventory".
    [HarmonyPatch(typeof(PickUp), "GrabEnter")]
    static class LootHoverEnter
    {
        static void Postfix(PickUp __instance) { Loot.Hovered = __instance; }
    }

    [HarmonyPatch(typeof(PickUp), "GrabExit")]
    static class LootHoverExit
    {
        static void Postfix(PickUp __instance) { if (Loot.Hovered == __instance) Loot.Hovered = null; }
    }
}
