using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace ForestCraft
{
    // The island's surface far around the player (128 x 128 blocks), for Minecraft's animals and
    // monsters: the detailed grid only covers 32 x 32 blocks, beyond it a mob would walk on
    // nothing. One height per block (original surface, Minecraft units), refreshed a little each
    // frame once the player has moved 16 blocks. OFF_FAR: seq, x, z, size, then size^2 floats.
    static class FarGround
    {
        public const int Off = 0x9F0000;
        public const int Size = 128;
        const int PerFrame = 2048;

        static readonly float[] next = new float[Size * Size];
        static int jobX, jobZ, cursor = -1;
        static int pubX = int.MinValue, pubZ;

        public static void Reset() { pubX = int.MinValue; cursor = -1; }

        public static void Update(float mcX, float mcZ)
        {
            IntPtr view = Link.View;
            if (view == IntPtr.Zero || !Ground.Ready) return;
            if (cursor < 0)
            {
                int cx = Mathf.FloorToInt(mcX) - Size / 2, cz = Mathf.FloorToInt(mcZ) - Size / 2;
                if (pubX != int.MinValue && Mathf.Abs(cx - pubX) < 16 && Mathf.Abs(cz - pubZ) < 16) return;
                jobX = cx; jobZ = cz; cursor = 0;
            }
            int end = Mathf.Min(cursor + PerFrame, Size * Size);
            for (; cursor < end; cursor++)
            {
                int gx = cursor % Size, gz = cursor / Size;
                next[cursor] = Ground.HeightMc(jobX + gx + 0.5f, jobZ + gz + 0.5f);
            }
            if (cursor < Size * Size) return;
            cursor = -1;
            int seq = Marshal.ReadInt32(view, Off) + 1;
            if ((seq & 1) == 0) seq++;
            Marshal.WriteInt32(view, Off, seq);
            Marshal.WriteInt32(view, Off + 4, jobX);
            Marshal.WriteInt32(view, Off + 8, jobZ);
            Marshal.WriteInt32(view, Off + 12, Size);
            Marshal.Copy(next, 0, new IntPtr(view.ToInt64() + Off + 16), next.Length);
            Marshal.WriteInt32(view, Off, seq + 1);
            pubX = jobX; pubZ = jobZ;
        }
    }
}
