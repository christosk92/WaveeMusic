// ── Screens/Diagnostics.Inspect.Home.Preview.cs ─────────────────────────────────────────────────────────────────────────────
// `home do=preview`: the Spine Shelf's song preview read back (Home/SpineShelf/PreviewAudio.cs): the claim, both voices, the gain stages
// (the preview's sound fade, the duck on the user's music, the user's own amplitude and what the local player applies) and the counters;
// with `samples=true` the per-tick levels since the last read and each curve's largest one-tick change (a step shows as a large maxStep).
//
// Role: SHELL (UI thread)

using System.Text.Json;
using Wavee.HomeUi;

namespace Wavee;

public static partial class Diagnostics
{
    public sealed partial class InspectSession
    {
        static readonly PreviewAudio.Sample[] s_previewSamples = new PreviewAudio.Sample[512];

        static void WritePreview(Utf8JsonWriter w, bool samples)
        {
            w.WriteString("claim", PreviewAudio.ClaimTile);
            w.WriteBoolean("active", PreviewAudio.Active.Peek());
            w.WriteBoolean("sound", PreviewAudio.Sound.Peek());
            w.WriteBoolean("remote", PreviewAudio.Remote);
            Inspect.Reply.Num(w, "soundGain", PreviewAudio.SoundGain);
            Inspect.Reply.Num(w, "duck", PreviewAudio.DuckNow);
            Inspect.Reply.Num(w, "playerDuck", Playback.Audio.DuckGain);
            Inspect.Reply.Num(w, "userVolume", Playback.LocalVolume);
            Inspect.Reply.Num(w, "userAmp", Playback.Audio.VolumeTaper.Amplitude(Playback.LocalVolume));
            Inspect.Reply.Num(w, "applied", Playback.Audio.AppliedAmplitude);
            Inspect.Reply.Num(w, "savedVolume", Platform.Settings.Get(Platform.Keys.SavedVolume));
            w.WriteBoolean("musicPlaying", Playback.IsPlaying.Peek());
            w.WriteStartArray("voices");
            for (int i = 0; i < 2; i++)
            {
                var (tile, gain, playing, state) = PreviewAudio.VoiceInfo(i);
                w.WriteStartObject();
                w.WriteString("tile", tile);
                Inspect.Reply.Num(w, "gain", gain);
                w.WriteBoolean("playing", playing);
                w.WriteString("state", state);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteNumber("ticks", PreviewAudio.Ticks);
            w.WriteNumber("duckWrites", PreviewAudio.DuckWrites);
            w.WriteNumber("releases", PreviewAudio.Releases);
            w.WriteNumber("steals", PreviewAudio.Steals);
            w.WriteString("lastRelease", PreviewAudio.LastRelease);
            if (!samples) return;
            int n = PreviewAudio.DrainSamples(s_previewSamples);
            float s0 = 0f, s1 = 0f, sd = 0f, sa = 0f;
            w.WriteStartArray("samples");   // [ms, voice0, voice1, sound, duck, userAmp, applied]
            for (int i = 0; i < n; i++)
            {
                ref readonly var s = ref s_previewSamples[i];
                if (i > 0)
                {
                    ref readonly var p = ref s_previewSamples[i - 1];
                    s0 = MathF.Max(s0, MathF.Abs(s.V0 - p.V0));
                    s1 = MathF.Max(s1, MathF.Abs(s.V1 - p.V1));
                    sd = MathF.Max(sd, MathF.Abs(s.Duck - p.Duck));
                    sa = MathF.Max(sa, MathF.Abs(s.Applied - p.Applied));
                }
                w.WriteStartArray();
                w.WriteNumberValue(s.Ms);
                w.WriteNumberValue(MathF.Round(s.V0, 4));
                w.WriteNumberValue(MathF.Round(s.V1, 4));
                w.WriteNumberValue(MathF.Round(s.SoundGain, 4));
                w.WriteNumberValue(MathF.Round(s.Duck, 4));
                w.WriteNumberValue(MathF.Round(s.UserAmp, 4));
                w.WriteNumberValue(MathF.Round(s.Applied, 4));
                w.WriteEndArray();
            }
            w.WriteEndArray();
            w.WriteStartObject("maxStep");
            Inspect.Reply.Num(w, "voice0", s0);
            Inspect.Reply.Num(w, "voice1", s1);
            Inspect.Reply.Num(w, "duck", sd);
            Inspect.Reply.Num(w, "applied", sa);
            w.WriteEndObject();
        }
    }
}
