using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using TheForest.Utils;

namespace ForestCraft
{
    // The Forest's map in Steve's hands. M (as in the original game, once its map is found) takes
    // it out or puts it away: The Forest equips its own map (its real model: folded paper, the
    // island, explored and revealed caves, the player's pin, its reading light) and Minecraft holds
    // it like a Minecraft map (its arms, the tilt with the look), without its own paper
    // (HeldMapMixin). Minecraft says where that paper is on screen each frame; The Forest's map
    // object is moved there (it would hang off The Forest's hidden, animated hands otherwise) and
    // kept visible while the rest of The Forest's body stays hidden.
    static class HeldMap
    {
        const int Off = 0xA1F000;
        static int seenCount = int.MinValue;
        static float lastUpdate = -10f;
        static TheForest.Items.World.CaveMapDrawer drawer;
        static Transform root;
        static bool open, placed, hooked, logged, cornersLogged, dumped;
        static Vector3 placePos, placeScale;
        static Quaternion placeRot;
        // The map's paper (_rendererBG) may hang elsewhere than the plane we move: it follows the
        // plane with the offset it has in The Forest's hands (taken once, before anything moved).
        static Renderer bg;
        static bool bgFollows;
        static Matrix4x4 bgRel;
        static Vector3 bgLocalPos, bgLocalScale, rootLocalPos, rootLocalScale;
        static Quaternion bgLocalRot, rootLocalRot;

        /// <summary>The map is out and must stay visible (Drive doesn't hide what is under Root).</summary>
        public static bool Showing { get { return open && root != null && root.gameObject.activeInHierarchy; } }
        public static Transform Root { get { return root; } }

        /// <summary>A renderer of the map out in Steve's hands (Drive must not hide it).</summary>
        public static bool Keeps(Renderer r)
        {
            if (!Showing) return false;
            Transform t = r.transform;
            return t.IsChildOf(root) || (bg != null && t.IsChildOf(bg.transform));
        }

        public static void Update()
        {
            if (!Link.Driving || Link.View == IntPtr.Zero) { Link.WriteIntAt(0x100 + 240, 0); return; }
            Find();
            Toggle();
            bool showing = Showing;
            Link.WriteIntAt(0x100 + 240, showing ? 1 : 0);
            int count = Link.ReadIntAt(Off);
            if (count != seenCount)
            {
                if (seenCount != int.MinValue) lastUpdate = Time.time;
                seenCount = count;
            }
            placed = false;
            if (!showing) return;
            if (Time.time - lastUpdate > 0.25f) { Hide(); return; }
            Camera cam = LocalPlayerSafe.Camera();
            if (cam == null) return;
            // F5: Minecraft draws no first-person hands, so no paper to put it in: hidden then.
            if (Link.ReadIntAt(0x100 + 60) != 0) { Hide(); return; }
            CaptureBackground();
            Place(cam);
            Reveal();
            if (!dumped) { dumped = true; Dump(); }
            if (!hooked) { hooked = true; Camera.onPreCull += PreCull; }
        }

        // ---- M: out / away -----------------------------------------------------------------------
        static void Toggle()
        {
            if (drawer == null || LocalPlayer.Inventory == null) return;
            if (McScreen.Open || ModMenu.Open || DevConsole.Open) return;
            int id = drawer._caveMapItemId;
            if (open && (root == null || !root.gameObject.activeInHierarchy) && Time.time - openedAt > 1f) Close(false);
            if (!UnityEngine.Input.GetKeyDown(KeyCode.M)) return;
            if (open) { Close(true); return; }
            if (!LocalPlayer.Inventory.Owns(id))
            {
                Plugin.Log.LogInfo("ForestCraft: no map yet (it is in the hanging cave, or F8 > Monde)");
                return;
            }
            LocalPlayer.Inventory.Equip(id, false);
            open = true;
            openedAt = Time.time;
            Plugin.Log.LogInfo("ForestCraft: map out");
        }

        static float openedAt;

        static void Close(bool unequip)
        {
            open = false;
            // Back where The Forest keeps them (next time it is taken out, it starts from there).
            if (root != null)
            {
                root.localPosition = rootLocalPos;
                root.localRotation = rootLocalRot;
                root.localScale = rootLocalScale;
            }
            if (bgFollows && bg != null)
            {
                bg.transform.localPosition = bgLocalPos;
                bg.transform.localRotation = bgLocalRot;
                bg.transform.localScale = bgLocalScale;
            }
            if (unequip && drawer != null && LocalPlayer.Inventory != null)
            {
                int id = drawer._caveMapItemId;
                try
                {
                    if (LocalPlayer.Inventory.HasInSlot(TheForest.Items.Item.EquipmentSlot.RightHand, id))
                        LocalPlayer.Inventory.UnequipItemAtSlot(TheForest.Items.Item.EquipmentSlot.RightHand, false, true, false);
                    else if (LocalPlayer.Inventory.HasInSlot(TheForest.Items.Item.EquipmentSlot.LeftHand, id))
                        LocalPlayer.Inventory.UnequipItemAtSlot(TheForest.Items.Item.EquipmentSlot.LeftHand, false, true, false);
                }
                catch (Exception e) { Plugin.Log.LogWarning("ForestCraft: putting the map away: " + e.Message); }
            }
            Plugin.Log.LogInfo("ForestCraft: map away");
        }

        static void Find()
        {
            var d = LocalPlayer.MapDrawer;
            if (d == null || d._targetMeshFilter == null || d._playerPositionPin == null) return;
            if (d == drawer && root != null) return;
            drawer = d;
            root = d._playerPositionPin.parent; // the held map ('CaveMapHeld'), a 1x1 plane
            bg = d._rendererBG;
            bgFollows = false;
            bgTried = false;
            // Its place in The Forest's hands, untouched yet: restored when the map is put away.
            rootLocalPos = root.localPosition;
            rootLocalRot = root.localRotation;
            rootLocalScale = root.localScale;
            if (!logged && root != null) { logged = true; Plugin.Log.LogInfo("ForestCraft: The Forest's map object '" + root.name + "'"); }
        }

        // ---- where Minecraft's paper is ------------------------------------------------------------
        static void Place(Camera cam)
        {
            Vector3 tl = Read(Off + 8), tr = Read(Off + 20), bl = Read(Off + 32);
            float mcFov = Link.ReadFloatAt(Off + 44);
            if (mcFov < 10f || mcFov > 170f) mcFov = 70f;
            float tanMc = Mathf.Tan(mcFov * 0.5f * Mathf.Deg2Rad);
            float nearest = Mathf.Min(-tl.z, Mathf.Min(-tr.z, -bl.z));
            if (nearest <= 0.01f) return;
            float ds = cam.nearClipPlane * 1.6f / nearest;
            Vector3 a = ToWorld(cam, tl, tanMc, ds), b = ToWorld(cam, tr, tanMc, ds), c = ToWorld(cam, bl, tanMc, ds);
            Vector3 right = b - a, down = c - a;
            // Seen from its front (local -Z, the side the overlay is on: the paper 'Map_low' sits
            // just behind it at +Z, and like any opaque model it is culled from the back), Z away
            // from the camera, and turned half a turn in its plane (X to the left, Y down): the
            // way the paper's drawing reads upright (anchors and lettering the right way up).
            Vector3 X = -right, Y = down;
            Vector3 Z = Vector3.Cross(X.normalized, Y.normalized);
            placePos = a + 0.5f * right + 0.5f * down;
            placeRot = Quaternion.LookRotation(Z, Y.normalized);
            placeScale = new Vector3(X.magnitude, Y.magnitude, X.magnitude);
            placed = true;
            Apply();
            if (!cornersLogged)
            {
                cornersLogged = true;
                Plugin.Log.LogInfo("ForestCraft: held map corners (view) " + tl.ToString("F2") + " " + tr.ToString("F2") + " " + bl.ToString("F2")
                    + ", Forest camera fov " + cam.fieldOfView.ToString("F1") + " near " + cam.nearClipPlane.ToString("F3"));
            }
        }

        static void Apply()
        {
            if (!placed || root == null) return;
            root.position = placePos;
            root.rotation = placeRot;
            Transform p = root.parent;
            Vector3 ps = p != null ? p.lossyScale : Vector3.one;
            root.localScale = new Vector3(placeScale.x / Safe(ps.x), placeScale.y / Safe(ps.y), placeScale.z / Safe(ps.z));
            FollowBackground();
        }

        static bool bgTried;

        // Once, in The Forest's own pose (before the plane is moved): where the paper sits
        // relative to the plane, if it isn't under it already.
        static void CaptureBackground()
        {
            if (bgTried || bg == null || root == null) return;
            bgTried = true;
            if (bg.transform.IsChildOf(root)) return;
            Transform b = bg.transform;
            bgRel = root.worldToLocalMatrix * b.localToWorldMatrix;
            bgLocalPos = b.localPosition;
            bgLocalRot = b.localRotation;
            bgLocalScale = b.localScale;
            bgFollows = true;
            Plugin.Log.LogInfo("ForestCraft: map paper '" + b.name + "' follows the map plane");
        }

        static void FollowBackground()
        {
            if (!bgFollows || bg == null) return;
            Transform b = bg.transform;
            Matrix4x4 w = root.localToWorldMatrix * bgRel;
            Vector3 fx = w.GetColumn(0), fy = w.GetColumn(1), fz = w.GetColumn(2);
            if (fy.sqrMagnitude < 1e-12f || fz.sqrMagnitude < 1e-12f) return;
            b.position = w.GetColumn(3);
            b.rotation = Quaternion.LookRotation(fz, fy);
            Transform p = b.parent;
            Vector3 ps = p != null ? p.lossyScale : Vector3.one;
            b.localScale = new Vector3(fx.magnitude / Safe(ps.x), fy.magnitude / Safe(ps.y), fz.magnitude / Safe(ps.z));
        }

        static float Safe(float v) { return Mathf.Abs(v) < 1e-6f ? 1e-6f : v; }

        // Again just before the camera renders (anything animated after our update moved it back).
        static void PreCull(Camera cam)
        {
            if (!Showing || !placed || cam != LocalPlayerSafe.Camera()) return;
            Apply();
        }

        // The map's own renderers on (Drive hides everything else of The Forest's body).
        static void Reveal()
        {
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>())
            {
                if (drawer._compass != null && r.transform.IsChildOf(drawer._compass.transform) && !drawer._compass.activeSelf) continue;
                if (!r.enabled) r.enabled = true;
            }
            if (bg != null && !bg.transform.IsChildOf(root))
                foreach (Renderer r in bg.GetComponentsInChildren<Renderer>()) if (!r.enabled) r.enabled = true;
        }

        static void Hide()
        {
            if (root == null) return;
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>()) if (r.enabled) r.enabled = false;
            if (bg != null) foreach (Renderer r in bg.GetComponentsInChildren<Renderer>()) if (r.enabled) r.enabled = false;
        }

        // Once: what the map is made of (to see why it looks transparent).
        static void Dump()
        {
            try
            {
                var sb = new System.Text.StringBuilder("ForestCraft: map object chain:");
                for (Transform t = root; t != null && t.parent != null; t = t.parent)
                    sb.Append(" <- '").Append(t.name).Append("'").Append(t.gameObject.activeSelf ? "" : " (off)");
                Plugin.Log.LogInfo(sb.ToString());
                Transform top = root;
                int n = 0;
                foreach (Renderer r in top.GetComponentsInChildren<Renderer>(true))
                {
                    if (n++ > 40) break;
                    var m = r.sharedMaterial;
                    string path = r.transform == top ? top.name : r.transform.name;
                    for (Transform t = r.transform.parent; t != null && t != top; t = t.parent) path = t.name + "/" + path;
                    Plugin.Log.LogInfo("ForestCraft:   map part '" + path + "' " + r.GetType().Name + (r.enabled ? "" : " disabled") + (r.gameObject.activeInHierarchy ? "" : " inactive")
                        + " layer " + r.gameObject.layer + " shader " + (m != null && m.shader != null ? m.shader.name : "-") + " queue " + (m != null ? m.renderQueue : -1)
                        + " color " + (m != null && m.HasProperty("_Color") ? m.color.ToString() : "-") + " scale " + r.transform.lossyScale.ToString("F3"));
                }
                Plugin.Log.LogInfo("ForestCraft: map drawer bg '" + (drawer._rendererBG != null ? drawer._rendererBG.name + (drawer._rendererBG.transform.IsChildOf(root) ? " (under the plane)" : " (outside the plane)") + (drawer._rendererBG.enabled ? "" : " disabled") + (drawer._rendererBG.gameObject.activeInHierarchy ? "" : " inactive") : "-") + "', overlay '" + drawer._targetMeshFilter.name + "'");
            }
            catch (Exception e) { Plugin.Log.LogWarning("ForestCraft: map dump: " + e.Message); }
        }

        static Vector3 Read(int at)
        {
            return new Vector3(Link.ReadFloatAt(at), Link.ReadFloatAt(at + 4), Link.ReadFloatAt(at + 8));
        }

        static Vector3 ToWorld(Camera cam, Vector3 v, float tanMc, float ds)
        {
            float depth = -v.z;
            float ndcX = v.x / (depth * tanMc * cam.aspect), ndcY = v.y / (depth * tanMc);
            return cam.ViewportToWorldPoint(new Vector3(ndcX * 0.5f + 0.5f, ndcY * 0.5f + 0.5f, depth * ds));
        }
    }

    // The Forest's own map key does nothing while Minecraft has the body: M is ForestCraft's
    // (out / away, with Steve's hands), or the two would fight over the map.
    [HarmonyPatch(typeof(TheForest.Utils.Input), "GetButtonDown", new[] { typeof(string) })]
    static class ForestMapKeyDown
    {
        static bool Prefix(string button, ref bool __result)
        {
            if (button != "Map" || !Link.Driving) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(TheForest.Utils.Input), "GetButton", new[] { typeof(string) })]
    static class ForestMapKey
    {
        static bool Prefix(string button, ref bool __result)
        {
            if (button != "Map" || !Link.Driving) return true;
            __result = false;
            return false;
        }
    }
}
