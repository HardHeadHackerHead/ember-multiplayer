using System.Collections.Generic;
using EmbervilleMP.Net;
using LiteNetLib;
using LiteNetLib.Utils;
using Steamworks;
using UnityEngine;

namespace EmbervilleMP.Steam
{
    internal enum VoiceMode { PushToTalk, Open, Off }

    // Voice chat: Steam records and compresses the microphone (push-to-talk V by default), the packets travel like
    // any other message (the host relays), and each listener decompresses them and plays every talker on its own
    // Windows sound stream. Needs Steam on the talking side; anyone can listen.
    internal class Voice
    {
        public const int SampleRate = 24000;
        private const byte Compressed = 0, RawPcm = 1;   // raw: tests only (no microphone needed)

        public static Voice Instance { get; private set; }
        public bool Recording { get; private set; }
        public int PacketsSent, PacketsReceived, PacketsDecoded;
        public bool ForceTalk;   // tests

        private readonly Session session;
        private readonly Dictionary<byte, WaveOut> speakers = new Dictionary<byte, WaveOut>();
        private readonly Dictionary<byte, float> lastHeard = new Dictionary<byte, float>();
        private readonly byte[] capture = new byte[16 * 1024];
        private readonly byte[] pcm = new byte[SampleRate * 2 * 2];

        public Voice(Session session)
        {
            Instance = this;
            this.session = session;
            session.On(Msg.Voice, OnVoice);
            session.PlayerLeft += p => DropSpeaker(p.Id);
            session.Ended += Stop;
        }

        public static VoiceMode Mode => Plugin.Settings.VoiceMode.Value;

        public bool IsSpeaking(byte id) => lastHeard.TryGetValue(id, out float t) && Time.realtimeSinceStartup - t < 0.3f;

        public WaveOut SpeakerFor(byte id) => speakers.TryGetValue(id, out var w) ? w : null;

        public void Update()
        {
            bool want = ForceTalk || Mode == VoiceMode.Open && Game.InGame ||
                        Mode == VoiceMode.PushToTalk && Input.GetKey(Plugin.Settings.VoiceKey.Value) && GUIUtility.keyboardControl == 0 &&
                        !(MPRunner.Instance.ChatBox?.Typing ?? false);
            if (!SteamBootstrap.Ready) want = false;
            if (want != Recording)
            {
                if (want) SteamUser.StartVoiceRecording(); else SteamUser.StopVoiceRecording();
                Recording = want;
            }
            // Steam keeps handing out data for a moment after we stop (the end of the last word).
            if (SteamBootstrap.Ready && SteamUser.GetAvailableVoice(out uint size) == EVoiceResult.k_EVoiceResultOK && size > 0 &&
                SteamUser.GetVoice(true, capture, (uint)capture.Length, out uint written) == EVoiceResult.k_EVoiceResultOK && written > 0)
                SendPacket(Compressed, capture, (int)written);
            foreach (var s in speakers.Values) s.Pump();
        }

        // Unreliable like the movement (a late voice packet is useless); a big one (a long frame hitch) goes reliable,
        // since an unreliable packet must fit in one datagram.
        public void SendPacket(byte kind, byte[] data, int length)
        {
            session.Send(Msg.Voice, w => { w.Put(kind); w.Put(data, 0, length); },
                length > 1000 ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable);
            PacketsSent++;
        }

        private void OnVoice(byte from, NetDataReader r)
        {
            PacketsReceived++;
            byte kind = r.GetByte();
            byte[] data = r.GetRemainingBytes();
            float volume = Mathf.Clamp01(Plugin.Settings.VoiceVolume.Value);
            if (Mode == VoiceMode.Off || volume <= 0f && !Args.Has("test")) return;
            int length;
            if (kind == RawPcm)
            {
                length = Mathf.Min(data.Length, pcm.Length);
                System.Buffer.BlockCopy(data, 0, pcm, 0, length);
            }
            else
            {
                if (!SteamBootstrap.Ready) return;
                if (SteamUser.DecompressVoice(data, (uint)data.Length, pcm, (uint)pcm.Length, out uint n, SampleRate) != EVoiceResult.k_EVoiceResultOK) return;
                length = (int)n;
            }
            PacketsDecoded++;
            lastHeard[from] = Time.realtimeSinceStartup;
            if (!speakers.TryGetValue(from, out var speaker)) speakers[from] = speaker = new WaveOut(SampleRate);
            speaker.Write(pcm, length, volume);
        }

        private void DropSpeaker(byte id)
        {
            if (speakers.TryGetValue(id, out var s)) s.Dispose();
            speakers.Remove(id);
            lastHeard.Remove(id);
        }

        public void Stop()
        {
            if (Recording && SteamBootstrap.Ready) SteamUser.StopVoiceRecording();
            Recording = false;
            foreach (var s in speakers.Values) s.Dispose();
            speakers.Clear();
            if (Instance == this) Instance = null;
        }

        // The pause menu's voice line: next mode.
        public static void CycleMode()
        {
            var m = Plugin.Settings.VoiceMode;
            m.Value = m.Value == VoiceMode.PushToTalk ? VoiceMode.Open : m.Value == VoiceMode.Open ? VoiceMode.Off : VoiceMode.PushToTalk;
            Plugin.Settings.Save();
        }

        public static string ModeLabel => Mode == VoiceMode.PushToTalk ? $"push to talk ({Plugin.Settings.VoiceKey.Value})"
            : Mode == VoiceMode.Open ? "open mic" : "off";
    }
}
