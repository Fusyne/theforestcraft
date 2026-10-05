using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace ForestCraft
{
    // Shared file both games map. The Forest creates it; Minecraft opens it.
    // 1 Unity unit = 1 Minecraft block. Unity +Z (forward) becomes Minecraft -Z.
    public static class Link
    {
        public const int Magic = 0x46524346; // "FCRF"
        public const int Version = 1;
        public const int Grid = 32;
        public const int InGame = 1;
        public const int Menu = 2;
        public const int McInWorld = 1;
        public const int McOnGround = 2;
        public const int McSettled = 4;
        // Unity +Z is Minecraft -Z, so a Unity yaw of 0 looks along Minecraft yaw 180.
        public const float YawFromMc = 180f;

        const int MapBytes = 16 << 20;
        public const int OffFrame = 0x200000;
        const int OffHeader = 0x000;
        const int OffForest = 0x100;
        const int OffMc = 0x200;
        const int OffGrid = 0x300;
        const int OffSolids = 0x2000;

        // Unity units per Minecraft block. The Forest's player is taller than Steve (1.8 blocks),
        // so 1:1 made the island look oversized. Fixed once, before Minecraft is first placed.
        public static float Scale = 1f;

        static IntPtr view;
        static bool created;
        static bool puppetLatched;
        public static int SentTeleport;

        public static string FilePath
        {
            get
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ForestCraft");
                return Path.Combine(dir, "link.bin");
            }
        }

        public static bool Create()
        {
            if (created) return view != IntPtr.Zero;
            created = true;
            try
            {
                string path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                IntPtr file = CreateFileW(path, 0xC0000000, 3, IntPtr.Zero, 4, 0x80, IntPtr.Zero);
                if (file == new IntPtr(-1)) return false;
                SetFilePointer(file, MapBytes, IntPtr.Zero, 0);
                SetEndOfFile(file);
                IntPtr mapping = CreateFileMappingW(file, IntPtr.Zero, 4, 0, MapBytes, null);
                CloseHandle(file);
                if (mapping == IntPtr.Zero) return false;
                view = MapViewOfFile(mapping, 0xF001F, 0, 0, (UIntPtr)MapBytes);
                CloseHandle(mapping);
                if (view == IntPtr.Zero) return false;
                WriteInt(OffHeader + 0, Magic);
                WriteInt(OffHeader + 4, Version);
                WriteInt(OffHeader + 8, System.Diagnostics.Process.GetCurrentProcess().Id);
                WriteInt(OffHeader + 32, 0);
                WriteInt(OffHeader + 12, 0); // Minecraft writes its pid when it links
                Blocks.Reset(view);
                DigWorld.Reset(view);
                McScreen.Reset(view);
                Loot.Reset(view);
                WriteInt(OffForest + 84, 0);
                WriteInt(OffForest + 124, 0);
                WriteFloat(OffForest + 128, 0f);
                Heartbeat();
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("link create failed: " + e.Message);
                return false;
            }
        }

        public static void Heartbeat()
        {
            if (view == IntPtr.Zero) return;
            WriteLong(OffHeader + 16, UnixMs());
        }

        public static void RequestQuit()
        {
            if (view == IntPtr.Zero) return;
            WriteInt(OffHeader + 32, 1);
        }

        public static int McPid()
        {
            return view == IntPtr.Zero ? 0 : ReadInt(OffHeader + 12);
        }

        public static bool McAlive()
        {
            if (view == IntPtr.Zero) return false;
            if (ReadInt(OffHeader + 12) == 0) return false;
            long beat = Marshal.ReadInt64(view, OffHeader + 24);
            if (beat == 0) return false;
            long now = UnixMs();
            return now >= beat && now - beat < 2000;
        }

        static int drivingFrame = -1;
        static int lastMoved = int.MinValue;
        static float movedAt = -100f;
        static bool drivingCached;

        // Asked many times a frame (and by the input patches, for every button The Forest
        // polls): worked out once per frame.
        public static bool Driving
        {
            get
            {
                int frame = Time.frameCount;
                if (frame == drivingFrame) return drivingCached;
                drivingCached = DrivingNow();
                drivingFrame = frame;
                return drivingCached;
            }
        }

        static bool DrivingNow()
        {
            {
                // SkyCraft's rule: once Minecraft has arrived, it owns the body, including jumps.
                // Releasing on every airborne tick is what snapped the player back to The Forest.
                if (!McAlive() || !ForestInWorld() || LocalPlayerSafe.Scripted())
                {
                    puppetLatched = false;
                    return false;
                }
                McSnapshot mc;
                if (!ReadMc(out mc)) return puppetLatched;
                if ((mc.flags & McInWorld) == 0 || (mc.flags & McSettled) == 0 || mc.teleportAck != SentTeleport)
                {
                    puppetLatched = false;
                    return false;
                }
                // A respawn moves the body far away in one go (Minecraft counts those at +188):
                // the puppet follows it; only an unannounced drop means Minecraft fell through.
                int moved = ReadMcInt(188);
                if (lastMoved == int.MinValue) lastMoved = moved;
                else if (moved != lastMoved) { lastMoved = moved; movedAt = Time.realtimeSinceStartup; }
                bool justMoved = Time.realtimeSinceStartup - movedAt < 15f;
                Vector3 gap = McToUnity(mc.x, mc.y, mc.z) - Drive.Feet();
                if (gap.y < -5f && !justMoved)
                {
                    puppetLatched = false;
                    return false;
                }
                puppetLatched = true;
                return true;
            }
        }

        public static bool ForestInWorld()
        {
            return LocalPlayerSafe.InWorld() && !LocalPlayerSafe.InMenu();
        }

        public static void WriteForest(double x, double y, double z, float yaw, float pitch, int teleportSeq, int input, float mouseX, float mouseY, int mouseSeq)
        {
            if (view == IntPtr.Zero) return;
            int flags = 0;
            if (LocalPlayerSafe.InWorld()) flags |= InGame;
            if (LocalPlayerSafe.InMenu()) flags |= Menu;
            int seq = ReadInt(OffForest) + 1;
            if ((seq & 1) == 0) seq++;
            WriteInt(OffForest, seq);
            WriteInt(OffForest + 4, flags);
            WriteDouble(OffForest + 8, x);
            WriteDouble(OffForest + 16, y);
            WriteDouble(OffForest + 24, z);
            WriteFloat(OffForest + 32, yaw);
            WriteFloat(OffForest + 36, pitch);
            WriteInt(OffForest + 40, teleportSeq);
            WriteInt(OffForest + 44, input);
            WriteFloat(OffForest + 48, mouseX);
            WriteFloat(OffForest + 52, mouseY);
            WriteInt(OffForest + 56, mouseSeq);
            WriteInt(OffForest, seq + 1);
            SentTeleport = teleportSeq;
        }

        // View settings The Forest owns: F5 mode, Minecraft frame size, hotbar slot, 3rd-person distance.
        public static IntPtr View { get { return view; } }

        public static void WriteIntAt(int absolute, int value)
        {
            if (view != IntPtr.Zero) WriteInt(absolute, value);
        }

        public static void WriteFloatAt(int absolute, float value)
        {
            if (view != IntPtr.Zero) WriteFloat(absolute, value);
        }

        public static float ReadFloatAt(int absolute)
        {
            return view == IntPtr.Zero ? 0f : ReadFloat(absolute);
        }

        public static int ReadIntAt(int absolute)
        {
            return view == IntPtr.Zero ? 0 : ReadInt(absolute);
        }

        public static int ReadMcInt(int offset)
        {
            return view == IntPtr.Zero ? 0 : ReadInt(OffMc + offset);
        }

        // A tree fell: Minecraft adds `count` oak logs when it sees the new seq.
        public static void WriteLogs(int seq, int count)
        {
            if (view == IntPtr.Zero) return;
            WriteInt(OffForest + 88, count);
            WriteInt(OffForest + 84, seq);
        }

        public static float ReadMcFloat(int offset)
        {
            return view == IntPtr.Zero ? 0f : ReadFloat(OffMc + offset);
        }

        // Sun elevation in degrees (NaN = unknown): Minecraft's time of day follows it.
        public static void WriteSun(float elevation)
        {
            if (view == IntPtr.Zero) return;
            WriteFloat(OffForest + 80, elevation);
        }

        public static void WriteView(int cameraMode, int frameW, int frameH, int slot, float camDist)
        {
            if (view == IntPtr.Zero) return;
            WriteInt(OffForest + 60, cameraMode);
            WriteInt(OffForest + 64, frameW);
            WriteInt(OffForest + 68, frameH);
            WriteInt(OffForest + 72, slot);
            WriteFloat(OffForest + 76, camDist);
        }

        // Minecraft's camera feel, written every Minecraft frame: eye height, FOV (sprint widens it), view bobbing.
        public static bool ReadView(out float eye, out float fov, out float walk, out float bob)
        {
            eye = 1.62f; fov = 0f; walk = 0f; bob = 0f;
            if (view == IntPtr.Zero) return false;
            eye = ReadFloat(OffMc + 76);
            fov = ReadFloat(OffMc + 80);
            walk = ReadFloat(OffMc + 84);
            bob = ReadFloat(OffMc + 88);
            if (float.IsNaN(eye) || eye <= 0f || eye > 3f) eye = 1.62f;
            if (float.IsNaN(fov) || fov < 10f || fov > 170f) fov = 0f;
            if (float.IsNaN(walk)) walk = 0f;
            if (float.IsNaN(bob) || Mathf.Abs(bob) > 1f) bob = 0f;
            return true;
        }

        public struct McSnapshot
        {
            public int flags;
            public double x, y, z;
            public float yaw, pitch;
            public int teleportAck;
            public int mouseAck;
            public double px, py, pz;
            public int tick;
        }

        public static bool ReadMc(out McSnapshot mc)
        {
            mc = new McSnapshot();
            if (view == IntPtr.Zero) return false;
            for (int n = 0; n < 8; n++)
            {
                int seq = ReadInt(OffMc);
                if (seq == 0) return false;
                if ((seq & 1) != 0) continue;
                mc.flags = ReadInt(OffMc + 4);
                mc.x = ReadDouble(OffMc + 8);
                mc.y = ReadDouble(OffMc + 16);
                mc.z = ReadDouble(OffMc + 24);
                mc.yaw = ReadFloat(OffMc + 32);
                mc.pitch = ReadFloat(OffMc + 36);
                mc.teleportAck = ReadInt(OffMc + 40);
                mc.mouseAck = ReadInt(OffMc + 44);
                mc.px = ReadDouble(OffMc + 48);
                mc.py = ReadDouble(OffMc + 56);
                mc.pz = ReadDouble(OffMc + 64);
                mc.tick = ReadInt(OffMc + 72);
                if (ReadInt(OffMc) == seq) return true;
            }
            return false;
        }

        public static void PublishGrid(int originX, int originZ, float[] heights)
        {
            if (view == IntPtr.Zero || heights == null || heights.Length != Grid * Grid) return;
            int seq = ReadInt(OffGrid) + 1;
            if ((seq & 1) == 0) seq++;
            WriteInt(OffGrid, seq);
            WriteInt(OffGrid + 4, originX);
            WriteInt(OffGrid + 8, originZ);
            WriteInt(OffGrid + 12, Grid);
            Marshal.Copy(heights, 0, new IntPtr(view.ToInt64() + OffGrid + 16), heights.Length);
            WriteInt(OffGrid, seq + 1);
        }

        const int OffGround = 0xA30000;

        // The original surface in detail around the player: heights at block corners
        // ((Grid+1)^2) and each column's lowest and highest point (Grid^2 each), Minecraft units.
        public static void PublishGround(int originX, int originZ, float[] corners, float[] mins, float[] maxs, byte[] kinds)
        {
            if (view == IntPtr.Zero) return;
            int seq = ReadInt(OffGround) + 1;
            if ((seq & 1) == 0) seq++;
            WriteInt(OffGround, seq);
            WriteInt(OffGround + 4, originX);
            WriteInt(OffGround + 8, originZ);
            WriteInt(OffGround + 12, Grid);
            int at = OffGround + 16;
            Marshal.Copy(corners, 0, new IntPtr(view.ToInt64() + at), corners.Length); at += corners.Length * 4;
            Marshal.Copy(mins, 0, new IntPtr(view.ToInt64() + at), mins.Length); at += mins.Length * 4;
            Marshal.Copy(maxs, 0, new IntPtr(view.ToInt64() + at), maxs.Length);
            // What each column is made of (0 dirt, 1 sand), Grid^2 bytes at +0x3200.
            if (kinds != null) Marshal.Copy(kinds, 0, new IntPtr(view.ToInt64() + OffGround + 0x3200), Math.Min(kinds.Length, Grid * Grid));
            WriteInt(OffGround, seq + 1);
        }

        static byte[] solidBytes;

        public static void PublishSolids(int ox, int oy, int oz, int sx, int sy, int sz, ulong[] masks)
        {
            if (view == IntPtr.Zero || masks == null || masks.Length != sx * sy * sz * Solids.Words) return;
            int bytes = masks.Length * 8;
            if (solidBytes == null || solidBytes.Length != bytes) solidBytes = new byte[bytes];
            Buffer.BlockCopy(masks, 0, solidBytes, 0, bytes);
            int seq = ReadInt(OffSolids) + 1;
            if ((seq & 1) == 0) seq++;
            WriteInt(OffSolids, seq);
            WriteInt(OffSolids + 4, ox);
            WriteInt(OffSolids + 8, oy);
            WriteInt(OffSolids + 12, oz);
            WriteInt(OffSolids + 16, sx);
            WriteInt(OffSolids + 20, sy);
            WriteInt(OffSolids + 24, sz);
            Marshal.Copy(solidBytes, 0, new IntPtr(view.ToInt64() + OffSolids + 32), bytes);
            WriteInt(OffSolids, seq + 1);
        }

        public static void UnityToMc(UnityEngine.Vector3 u, out double x, out double y, out double z)
        {
            x = u.x / Scale;
            y = u.y / Scale;
            z = -u.z / Scale;
        }

        // Real alpha when Minecraft's background is transparent (the normal case now);
        // falls back to the old sky-color key if an opaque pass ever fills the frame.
        public static bool ReadFrameRaw(ref int lastSeq, out byte[] raw, out Color32[] keyedPixels, out bool keyed, out int width, out int height)
        {
            raw = null; keyedPixels = null; keyed = false; width = 0; height = 0;
            if (view == IntPtr.Zero) return false;
            int seq = ReadInt(OffFrame);
            if ((seq & 1) != 0 || seq == 0 || seq == lastSeq) return false;
            int w = ReadInt(OffFrame + 4);
            int h = ReadInt(OffFrame + 8);
            int count = w * h;
            if (w < 16 || h < 16 || count > 1920 * 1080) return false;
            int bytes = count * 4;
            if (frameRaw == null || frameRaw.Length != bytes) frameRaw = new byte[bytes];
            Marshal.Copy(new IntPtr(view.ToInt64() + OffFrame + 16), frameRaw, 0, bytes);
            if (ReadInt(OffFrame) != seq) return false;
            lastSeq = seq;
            width = w;
            height = h;
            // Minecraft always clears to transparent now, so its alpha is the mask. (The old
            // sky-colour key kicked in whenever a screen's dark background covered the frame,
            // and then cut every dark pixel out of the inventory.)
            raw = frameRaw;
            return true;
        }

        /// <summary>
        /// A new Minecraft frame, as a pointer into the shared memory (no 8 MB copy into a managed
        /// array first): the texture loads straight from it. seq is checked again afterwards
        /// with FrameStillSame; a frame overwritten meanwhile is loaded again next time.
        /// </summary>
        public static bool NewFrame(int lastSeq, out IntPtr pixels, out int seq, out int width, out int height)
        {
            pixels = IntPtr.Zero; seq = 0; width = 0; height = 0;
            if (view == IntPtr.Zero) return false;
            seq = ReadInt(OffFrame);
            if ((seq & 1) != 0 || seq == 0 || seq == lastSeq) return false;
            int w = ReadInt(OffFrame + 4);
            int h = ReadInt(OffFrame + 8);
            if (w < 16 || h < 16 || w * h > 1920 * 1080) return false;
            width = w;
            height = h;
            pixels = new IntPtr(view.ToInt64() + OffFrame + 16);
            return true;
        }

        public static bool FrameStillSame(int seq)
        {
            return view != IntPtr.Zero && ReadInt(OffFrame) == seq;
        }

        static bool KeyFrame(byte[] src, int w, int h, out Color32[] pixels)
        {
            int count = w * h;
            if (framePixels == null || framePixels.Length != count) framePixels = new Color32[count];
            int key = ((h - 4) * w + (w / 2)) * 4;
            int kr = src[key], kg = src[key + 1], kb = src[key + 2];
            for (int i = 0; i < count; i++)
            {
                int o = i * 4;
                int r = src[o], g = src[o + 1], b = src[o + 2];
                int dr = r - kr; if (dr < 0) dr = -dr;
                int dg = g - kg; if (dg < 0) dg = -dg;
                int db = b - kb; if (db < 0) db = -db;
                framePixels[i] = new Color32((byte)r, (byte)g, (byte)b, (byte)(dr + dg + db < 48 ? 0 : 255));
            }
            pixels = framePixels;
            return true;
        }

        public static bool ReadFrame(ref int lastSeq, out Color32[] pixels, out int width, out int height)
        {
            pixels = framePixels;
            width = frameW;
            height = frameH;
            if (view == IntPtr.Zero) return false;
            int seq = ReadInt(OffFrame);
            if ((seq & 1) != 0 || seq == 0 || seq == lastSeq) return framePixels != null && frameW > 0;
            int w = ReadInt(OffFrame + 4);
            int h = ReadInt(OffFrame + 8);
            int count = w * h;
            if (w < 16 || h < 16 || count > 1920 * 1080) return false;
            int bytes = count * 4;
            if (frameRaw == null || frameRaw.Length != bytes) frameRaw = new byte[bytes];
            Marshal.Copy(new IntPtr(view.ToInt64() + OffFrame + 16), frameRaw, 0, bytes);
            if (ReadInt(OffFrame) != seq) return framePixels != null && frameW > 0;
            if (framePixels == null || framePixels.Length != count) framePixels = new Color32[count];
            int key = ((h - 4) * w + (w / 2)) * 4;
            int kr = frameRaw[key];
            int kg = frameRaw[key + 1];
            int kb = frameRaw[key + 2];
            for (int i = 0; i < count; i++)
            {
                int o = i * 4;
                int r = frameRaw[o];
                int g = frameRaw[o + 1];
                int b = frameRaw[o + 2];
                int dr = r - kr; if (dr < 0) dr = -dr;
                int dg = g - kg; if (dg < 0) dg = -dg;
                int db = b - kb; if (db < 0) db = -db;
                byte a = (byte)(dr + dg + db < 48 ? 0 : 255);
                framePixels[i] = new Color32((byte)r, (byte)g, (byte)b, a);
            }
            frameW = w;
            frameH = h;
            lastSeq = seq;
            pixels = framePixels;
            width = w;
            height = h;
            return true;
        }

        static byte[] frameRaw;
        static Color32[] framePixels;
        static int frameW, frameH;

        public static UnityEngine.Vector3 McToUnity(double x, double y, double z)
        {
            return new UnityEngine.Vector3((float)(x * Scale), (float)(y * Scale), (float)(-z * Scale));
        }

        static void WriteInt(int offset, int value) { Marshal.WriteInt32(view, offset, value); }
        static void WriteLong(int offset, long value) { Marshal.WriteInt64(view, offset, value); }
        // float <-> int bits without BitConverter.GetBytes (a new byte[] per call: thousands per
        // frame here, it was most of the garbage behind the ~100 ms collections).
        [StructLayout(LayoutKind.Explicit)]
        struct Bits { [FieldOffset(0)] public float f; [FieldOffset(0)] public int i; }

        static void WriteFloat(int offset, float value) { Bits b = default(Bits); b.f = value; Marshal.WriteInt32(view, offset, b.i); }
        static void WriteDouble(int offset, double value) { Marshal.WriteInt64(view, offset, BitConverter.DoubleToInt64Bits(value)); }
        static int ReadInt(int offset) { return Marshal.ReadInt32(view, offset); }
        static float ReadFloat(int offset) { Bits b = default(Bits); b.i = Marshal.ReadInt32(view, offset); return b.f; }
        static double ReadDouble(int offset) { return BitConverter.Int64BitsToDouble(Marshal.ReadInt64(view, offset)); }

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr template);
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateFileMappingW(IntPtr file, IntPtr sec, int protect, int high, int low, string name);
        [DllImport("kernel32", SetLastError = true)]
        static extern IntPtr MapViewOfFile(IntPtr mapping, uint access, int offHigh, int offLow, UIntPtr count);
        [DllImport("kernel32")]
        static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32")]
        static extern uint SetFilePointer(IntPtr file, int distance, IntPtr high, uint method);
        [DllImport("kernel32")]
        static extern bool SetEndOfFile(IntPtr file);
        static long UnixMs()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        }
    }
}
