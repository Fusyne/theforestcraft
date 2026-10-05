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

        // Forest item name (as in forest_items.txt, any case) -> Minecraft item id.
        // Names not listed stay in The Forest's inventory.
        static readonly string[,] Table =
        {
            // tools and weapons
            { "Axe Plane", "stone_axe" }, { "AxeRusty", "stone_axe" }, { "AxeCrafted", "stone_axe" },
            { "AxeModern", "iron_axe" }, { "Chainsaw", "diamond_axe" },
            { "Katana", "iron_sword" }, { "Machete", "iron_sword" },
            { "Club", "wooden_sword" }, { "ClubCrafted", "wooden_sword" },
            { "Spear", "wooden_sword" }, { "SpearUpgraded", "stone_sword" },
            { "Bow", "bow" }, { "RecurveBow", "bow" }, { "BowCross", "crossbow" }, { "FlintLock", "crossbow" },
            { "Arrows", "arrow" }, { "CrossbowAmmo", "arrow" }, { "flintlockAmmo", "iron_nugget" },
            { "Lighter", "flint_and_steel" }, { "Compass", "compass" }, { "Watch", "clock" },
            { "PaintBrush", "brush" }, { "Pouch", "bundle" }, { "Walkman", "jukebox" },
            // fire and explosives
            { "Flare", "torch" }, { "FireStick", "torch" }, { "PlasticTorch", "torch" },
            { "FlareGun", "fire_charge" }, { "FlareGunAmmo", "fire_charge" }, { "Molotov", "fire_charge" },
            { "dynamite", "tnt" }, { "BombTimed", "tnt" }, { "HeadBomb", "tnt" },
            // materials
            { "Stick", "stick" }, { "StickUpgraded", "stick" }, { "Log", "oak_log" }, { "Leaf", "oak_leaves" },
            { "Rock", "cobblestone" }, { "RockUpgraded", "cobblestone" }, { "Small Rock", "cobblestone" },
            { "Rope", "string" }, { "Cloth", "white_wool" }, { "Feather", "feather" }, { "TapeSticky", "slime_ball" },
            { "TreeSap", "slime_ball" }, { "bone", "bone" }, { "Tooth", "bone_meal" }, { "Skull", "skeleton_skull" },
            { "Battery", "redstone" }, { "CBoard", "redstone" }, { "Fuel", "coal" }, { "Glass", "glass_bottle" },
            { "Coins", "gold_nugget" }, { "Cash", "emerald" }, { "TurtleShell", "turtle_scute" },
            { "BluePaint", "blue_dye" }, { "OrangePaint", "orange_dye" }, { "Tennis Ball", "snowball" },
            { "LizardSkin", "leather" }, { "DeerSkin", "leather" }, { "BoarSkin", "leather" },
            { "RacoonSkin", "leather" }, { "CreepySkin", "leather" }, { "RabbitSkin", "rabbit_hide" },
            // armour
            { "Rebreather", "turtle_helmet" }, { "StealthArmor", "leather_chestplate" }, { "Warmsuit", "leather_chestplate" },
            { "BoneArmor", "chainmail_chestplate" }, { "SnowShoes", "leather_boots" }, { "RabbitFurBoots", "leather_boots" },
            // food and medicine
            { "Meds", "golden_apple" }, { "MedicineCrafted", "golden_apple" }, { "MedicineCraftedPlus", "golden_apple" },
            { "EnergyMix", "honey_bottle" }, { "EnergyMixPlus", "honey_bottle" }, { "Soda", "honey_bottle" },
            { "Booze", "potion" }, { "ChocolateBar", "cookie" }, { "PlaneFood", "bread" },
            { "GenericMeat", "beef" }, { "SmallGenericMeat", "porkchop" }, { "Lizard", "rabbit" }, { "Rabbit Dead", "rabbit" },
            { "Rabbit Alive", "rabbit_spawn_egg" }, { "Cod", "cod" }, { "Oyster", "cod" },
            { "BlueBerry", "sweet_berries" }, { "BlackBerry", "sweet_berries" }, { "twinberry", "glow_berries" }, { "SnowBerry", "glow_berries" },
            { "MushroomAmanita", "red_mushroom" }, { "MushroomChanterelle", "brown_mushroom" }, { "MushroomJack", "brown_mushroom" },
            { "MushroomDeerMush", "brown_mushroom" }, { "MushroomLibertyCap", "brown_mushroom" }, { "MushroomPuffmush", "brown_mushroom" },
            // plants and seeds
            { "Marigold", "dandelion" }, { "ConeFlower", "allium" }, { "Chicory", "cornflower" }, { "Aloe", "green_dye" },
            { "Seed_Aloe", "wheat_seeds" }, { "Seed_Coneflower", "beetroot_seeds" }, { "Seed_BlueBerry", "sweet_berries" },
            // cannibals
            { "Head", "zombie_head" }, { "Arm", "rotten_flesh" }, { "Leg", "rotten_flesh" },
            // music
            { "Cassette 1", "music_disc_cat" }, { "Cassette 2", "music_disc_blocks" }, { "Cassette 3", "music_disc_chirp" },
            { "Cassette 4", "music_disc_far" }, { "Cassette 5", "music_disc_mall" },
        };

        // Kept by The Forest on purpose: the story (maps, photos, keycards, toys, tapes...) and what
        // only works there (climbing axe, survival book, bags, quiver, pot, waterskin...).
        static readonly string[] ForestOnlyPrefixes =
        {
            "axe climbing", "survivalbook", "flintlock part", "map", "cavemap", "photo", "polaroid", "page", "biblepage",
            "bible", "megan", "camcorder", "magazine", "animalhead_", "creepyhead_", "toy", "sketch", "keycard",
            "artifact", "email", "shippingmanifest", "passengermanifest", "restrainingorder", "terminationletter",
            "morguereport", "newspaper", "timmydrawing", "fortune", "bookdarkhaired", "chainsawad", "cross",
            "rockbag", "stickbag", "smallrockbag", "spearbag", "quiver", "pot", "waterskin", "aircanister",
            "repairtool", "pedometer", "walkytalky", "hairspray", "metaltintray", "slingshot", "tennisraquet",
            "milkcarton",
        };

        static Dictionary<string, string> byName;

        public static void Reset(IntPtr view)
        {
            Marshal.WriteInt32(view, OffGive, 0);
            written = 0;
        }

        public static string MinecraftFor(string forestName)
        {
            if (string.IsNullOrEmpty(forestName)) return null;
            if (byName == null)
            {
                byName = new Dictionary<string, string>();
                for (int i = 0; i < Table.GetLength(0); i++) byName[Table[i, 0].Trim().ToLowerInvariant()] = "minecraft:" + Table[i, 1];
            }
            string n = forestName.Trim().ToLowerInvariant();
            string id;
            if (byName.TryGetValue(n, out id)) return id;
            // The Forest's held torch variants (PlasticTorch_Bow...) are torches.
            if (n.StartsWith("plastictorch")) return "minecraft:torch";
            return null;
        }

        /// <summary>Stays in The Forest on purpose (story item, or only useful there).</summary>
        public static bool ForestOnly(string forestName)
        {
            if (string.IsNullOrEmpty(forestName)) return false;
            string n = forestName.Trim().ToLowerInvariant();
            foreach (string p in ForestOnlyPrefixes) if (n.StartsWith(p)) return true;
            return false;
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
                    lines.Add(item._id + "\t" + item._name + "\t" + (MinecraftFor(item._name) ?? (ForestOnly(item._name) ? "(The Forest)" : "-")));
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
                Plugin.Log.LogInfo("ForestCraft: picked up " + name + " x" + amount + (Loot.ForestOnly(name) ? " (kept by The Forest: needed there)" : " (no Minecraft match, kept by The Forest)"));
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
