using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace EmbervilleMP.Steam
{
    // One mono 16-bit sound stream played straight through Windows (winmm). The game plays everything through
    // Wwise and has Unity's own audio switched off, so voice can't use an AudioSource.
    // Buffers are handed to Windows from the main thread and freed once Windows marks them done (no callbacks).
    internal sealed class WaveOut : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct WaveFormat
        {
            public ushort wFormatTag, nChannels;
            public uint nSamplesPerSec, nAvgBytesPerSec;
            public ushort nBlockAlign, wBitsPerSample, cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WaveHeader
        {
            public IntPtr lpData;
            public uint dwBufferLength, dwBytesRecorded;
            public IntPtr dwUser;
            public uint dwFlags, dwLoops;
            public IntPtr lpNext, reserved;
        }

        [DllImport("winmm.dll")] private static extern int waveOutOpen(out IntPtr h, uint device, ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);
        [DllImport("winmm.dll")] private static extern int waveOutPrepareHeader(IntPtr h, IntPtr header, uint size);
        [DllImport("winmm.dll")] private static extern int waveOutUnprepareHeader(IntPtr h, IntPtr header, uint size);
        [DllImport("winmm.dll")] private static extern int waveOutWrite(IntPtr h, IntPtr header, uint size);
        [DllImport("winmm.dll")] private static extern int waveOutReset(IntPtr h);
        [DllImport("winmm.dll")] private static extern int waveOutClose(IntPtr h);

        private const uint WaveMapper = 0xFFFFFFFF;
        private const uint Done = 1;
        private static readonly uint HeaderSize = (uint)Marshal.SizeOf(typeof(WaveHeader));

        private readonly int rate;
        private IntPtr handle;
        private readonly List<(IntPtr header, IntPtr data)> queued = new List<(IntPtr, IntPtr)>();
        private byte[] pending = new byte[0];
        private int pendingLength;
        private float lastData;
        public int BuffersPlayed { get; private set; }
        public bool Ok => handle != IntPtr.Zero;

        public WaveOut(int sampleRate)
        {
            rate = sampleRate;
            var f = new WaveFormat
            {
                wFormatTag = 1, nChannels = 1, nSamplesPerSec = (uint)sampleRate, wBitsPerSample = 16,
                nBlockAlign = 2, nAvgBytesPerSec = (uint)sampleRate * 2,
            };
            int err = waveOutOpen(out handle, WaveMapper, ref f, IntPtr.Zero, IntPtr.Zero, 0);
            if (err != 0)
            {
                handle = IntPtr.Zero;
                Plugin.Log.LogWarning($"[voice] No sound output for voice (waveOutOpen error {err})");
            }
        }

        // PCM16 mono, scaled by volume (0..1).
        public void Write(byte[] pcm, int length, float volume)
        {
            if (!Ok || length <= 0) return;
            if (pendingLength + length > pending.Length) Array.Resize(ref pending, Math.Max(pending.Length * 2, pendingLength + length));
            if (volume >= 0.999f) Buffer.BlockCopy(pcm, 0, pending, pendingLength, length);
            else
                for (int i = 0; i + 1 < length; i += 2)
                {
                    int s = (short)(pcm[i] | pcm[i + 1] << 8);
                    s = (int)(s * volume);
                    pending[pendingLength + i] = (byte)s;
                    pending[pendingLength + i + 1] = (byte)(s >> 8);
                }
            pendingLength += length;
            lastData = UnityEngine.Time.realtimeSinceStartup;
        }

        // Every frame: frees finished buffers; starts playing once ~80 ms are buffered (fewer pops on uneven
        // packets), keeps feeding while it plays, and flushes the tail when the talker stops.
        public void Pump()
        {
            if (!Ok) return;
            for (int i = queued.Count - 1; i >= 0; i--)
            {
                var h = (WaveHeader)Marshal.PtrToStructure(queued[i].header, typeof(WaveHeader));
                if ((h.dwFlags & Done) == 0) continue;
                Free(queued[i]);
                queued.RemoveAt(i);
                BuffersPlayed++;
            }
            if (pendingLength == 0) return;
            bool playing = queued.Count > 0;
            bool enough = pendingLength >= rate * 2 * 80 / 1000;
            bool tail = UnityEngine.Time.realtimeSinceStartup - lastData > 0.1f;
            if (!playing && !enough && !tail) return;
            Submit(pending, pendingLength);
            pendingLength = 0;
        }

        private void Submit(byte[] pcm, int length)
        {
            IntPtr data = Marshal.AllocHGlobal(length);
            Marshal.Copy(pcm, 0, data, length);
            IntPtr header = Marshal.AllocHGlobal((int)HeaderSize);
            Marshal.StructureToPtr(new WaveHeader { lpData = data, dwBufferLength = (uint)length }, header, false);
            if (waveOutPrepareHeader(handle, header, HeaderSize) != 0 || waveOutWrite(handle, header, HeaderSize) != 0)
            {
                waveOutUnprepareHeader(handle, header, HeaderSize);
                Marshal.FreeHGlobal(header);
                Marshal.FreeHGlobal(data);
                return;
            }
            queued.Add((header, data));
        }

        private void Free((IntPtr header, IntPtr data) b)
        {
            waveOutUnprepareHeader(handle, b.header, HeaderSize);
            Marshal.FreeHGlobal(b.header);
            Marshal.FreeHGlobal(b.data);
        }

        public void Dispose()
        {
            if (!Ok) return;
            waveOutReset(handle);   // marks every queued buffer done
            foreach (var b in queued) Free(b);
            queued.Clear();
            waveOutClose(handle);
            handle = IntPtr.Zero;
        }
    }
}
