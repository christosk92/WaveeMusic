// ── AiLyrics/AiLyrics.Audio.cs ───────────────────────────────────────────────────────────────────────────────────────
// SpotifyPcm
//
// Role: HOST (worker thread)
//
// The song's audio for the AI job: its own open of the Spotify file (Ogg Vorbis 320, the rung the keys always cover),
// decoded at 44.1 kHz stereo by the app's own Vorbis decoder. Separate from the player's stream on purpose: the job
// reads ~10x faster than real time and must never steer or starve playback. The audio disk cache serves the bytes the
// player already fetched.
//
// The decoder folds the normalization gain into its output (IGainFolding); the job divides it back out, so the models
// see the level they were validated on.

using FluentGpu.Media;

namespace Wavee;

public static partial class AiLyrics
{
    public sealed class SpotifyPcm : IPcmSource, IDisposable
    {
        readonly Spotify.Audio.Opened _opened;
        readonly Playback.Audio.RingSource _src;
        readonly Playback.Audio.VorbisAudioDecoder _dec;
        readonly float _ungain;
        readonly CancellationToken _ct;

        public long DurationMs { get; }

        SpotifyPcm(Spotify.Audio.Opened o, Playback.Audio.RingSource src, Playback.Audio.VorbisAudioDecoder dec, CancellationToken ct)
        {
            _opened = o; _src = src; _dec = dec; _ct = ct;
            DurationMs = o.DurationMs;
            float g = dec.AppliedGainLinear;
            _ungain = g > 1e-6f ? 1f / g : 1f;
        }

        /// <summary>Opens the track's audio, or returns the fault that stopped it (offline, no key, no file).</summary>
        public static SpotifyPcm? Open(string uri, long durationMs, CancellationToken ct, out Spotify.Audio.Fault fault)
        {
            var o = Spotify.Audio.Open(uri, Spotify.Audio.Quality.VeryHigh320, durationMs, ct);
            fault = o.Fault;
            if (!o.Ok || o.Body is null) { o.Stream?.Dispose(); o.Body?.Dispose(); return null; }
            var src = new Playback.Audio.RingSource(o.Body);
            var dec = new Playback.Audio.VorbisAudioDecoder(o.GainDb, o.DurationMs, o.Peak, o.AlbumGainDb, o.AlbumPeak);
            if (!dec.TryOpen(src, new MixFormat(Separator.SampleRate, 2), out _))
            {
                dec.Dispose(); src.Close(); o.Stream?.Dispose();
                fault = Spotify.Audio.Fault.Refused;
                return null;
            }
            return new SpotifyPcm(o, src, dec, ct);
        }

        public int Read(Span<float> interleavedStereo)
        {
            while (true)
            {
                _ct.ThrowIfCancellationRequested();
                int n = _dec.Read(interleavedStereo);
                if (n == -3) { _ct.WaitHandle.WaitOne(20); continue; }             // the bytes are still on their way; a cancel ends the wait at once
                if (n <= 0) return n;
                if (_ungain != 1f) foreach (ref float s in interleavedStereo[..(n * 2)]) s *= _ungain;
                return n;
            }
        }

        public void Dispose()
        {
            _dec.Dispose();
            _src.Close();                                                     // an owning source disposes the body
            _opened.Stream?.Dispose();
        }
    }
}
