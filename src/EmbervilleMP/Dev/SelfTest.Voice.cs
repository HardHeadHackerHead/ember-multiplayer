using System.Collections;
using EmbervilleMP.Net;
using EmbervilleMP.Steam;
using Steamworks;
using UnityEngine;

namespace EmbervilleMP
{
    internal static partial class SelfTest
    {
        // Voice: the guest "talks" a test tone (raw PCM: no microphone needed) through the real voice path; the host
        // decodes it and plays it on its Windows sound stream at volume 0 (silent). Then each copy checks Steam's own
        // codec locally: one second of microphone, compressed and decompressed in memory, never sent anywhere.
        [Scenario("voice", Timeout = 180)]
        private static IEnumerator VoiceTest()
        {
            Plugin.Settings.VoiceVolume.Value = 0f;   // tests are silent (settings aren't saved in tests)
            yield return WaitInGame();
            yield return WaitFor(() => Session.Current != null && Session.Current.Players.Count >= 2 && Voice.Instance != null, 60f);
            yield return Barrier("ready", 60f);
            if (!Args.Host)
            {
                var tone = new byte[960];   // 20 ms at 24 kHz
                for (int k = 0; k < 75; k++)  // 1.5 s
                {
                    for (int i = 0; i < 480; i++)
                    {
                        short s = (short)(Mathf.Sin((k * 480 + i) * 2f * Mathf.PI * 440f / Voice.SampleRate) * 8000);
                        tone[i * 2] = (byte)s;
                        tone[i * 2 + 1] = (byte)(s >> 8);
                    }
                    Voice.Instance.SendPacket(1, tone, tone.Length);
                    yield return new WaitForSecondsRealtime(0.02f);
                }
                Check("sent the tone", Voice.Instance.PacketsSent >= 75, Voice.Instance.PacketsSent.ToString());
            }
            else
            {
                bool heardTalking = false;
                float until = Time.realtimeSinceStartup + 20f;
                while (Time.realtimeSinceStartup < until && Voice.Instance.PacketsDecoded < 60)
                {
                    heardTalking |= Voice.Instance.IsSpeaking(2);
                    yield return null;
                }
                yield return new WaitForSecondsRealtime(1f);
                Check("voice packets arrived and decoded", Voice.Instance.PacketsDecoded >= 60, $"{Voice.Instance.PacketsDecoded}/{Voice.Instance.PacketsReceived}");
                Check("the talker shows as talking", heardTalking);
                var speaker = Voice.Instance.SpeakerFor(2);
                Check("Windows played it", speaker != null && speaker.Ok && speaker.BuffersPlayed > 0, speaker == null ? "no speaker" : $"ok={speaker.Ok} played={speaker.BuffersPlayed}");
            }
            yield return Barrier("sent");

            // Steam's codec, locally
            if (!SteamBootstrap.Ready) Plugin.Log.LogInfo("[voice] Steam isn't running: codec check skipped");
            else
            {
                SteamUser.StartVoiceRecording();
                yield return new WaitForSecondsRealtime(1f);
                SteamUser.StopVoiceRecording();
                yield return new WaitForSecondsRealtime(0.3f);
                var buf = new byte[16 * 1024];
                var avail = SteamUser.GetAvailableVoice(out uint size);
                uint written = 0;
                var got = avail == EVoiceResult.k_EVoiceResultOK ? SteamUser.GetVoice(true, buf, (uint)buf.Length, out written) : avail;
                if (got != EVoiceResult.k_EVoiceResultOK || written == 0)
                    Plugin.Log.LogInfo($"[voice] No microphone data to check the codec with ({got}); skipped");
                else
                {
                    var pcm = new byte[Voice.SampleRate * 4];
                    var dec = SteamUser.DecompressVoice(buf, written, pcm, (uint)pcm.Length, out uint n, Voice.SampleRate);
                    Check("Steam compresses and decompresses voice", dec == EVoiceResult.k_EVoiceResultOK && n > 0, $"{written} bytes -> {n} bytes PCM ({dec})");
                }
            }
            yield return Barrier("done");
            yield return Finish("voice");
        }
    }
}
