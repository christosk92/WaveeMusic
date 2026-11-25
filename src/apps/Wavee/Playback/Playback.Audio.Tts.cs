// ── Playback/Playback.Audio.Tts.cs ───────────────────────────────────────────────────────────────────────────────────────
// `SourceRoute.Tts`: an AI DJ narration clip (Spotify.DjTts) as the pump's byte source, cached or still downloading
//
// Role: SHELL (a byte source; blocks only in the clip's wait, on the decoder's thread)
// Spec: docs/plans/wavee/dj-implementation.md §2.2 (the source route, loudness, duration) · §2.3 (play while downloading)
//
// WHICH BYTES. A clip's identity is `wavee:tts:<key>` (`Spotify.DjTts.IdOf`). Its bytes are the ones DjTts downloaded: the signed
// CDN url is single-use and lives 300 s, so the route never goes back to the network — it reads the clip DjTts holds (reloading a
// spilled one from the cache folder first). Not the podcast's ranged ring (`Spotify.Audio.Body`): that reader fetches byte ranges of
// a url and learns a file's end only from a range's `Content-Range`, which a synthesising MISS (no length, possibly no ranges) never
// sends, while the clip here already knows exactly when its body ended.
//
// TWO SHAPES. A clip that is Ready when it opens is a plain seekable buffer of known length: NLayer walks its frames and the decoder
// reports the exact length (`Gapless.ExactFrames`, `Opened.DurationExact`). A clip still downloading opens FORWARD-ONLY with no length:
// `Read` hands out what has landed and WAITS for the rest, and answers 0 only at the body's true end (the one invariant every source owes
// the decoder, banner §15) or −1 when the clip failed or the source was cancelled. So a streamed MISS starts playing on its first bytes;
// its duration is the clip's once the body ends (`DjTts.TryGet(key).DurationMs`, `Settled`), as the decoder cannot know it sooner.
//
// LOUDNESS. A narration is −16 LUFS, −3 dBTP (its manifest's field 1.5). `Opened.GainDb` is its figure in the −14 frame every Spotify
// gain is in (`DjTts.GainDbOf`: +2 dB) and `Opened.Peak` its linear true peak (0.708), so the MP3 decoder folds them through the ONE
// normalization factor a song's file gain goes through (`GainLinear` → `NormalizationFactor`): the user's mode adds its pregain (the
// whole is the mode's target − (−16): Quiet −7, Normal +2, Loud +5 dB), the peak caps it as it caps a song (Normal: the limiter's
// −1.5 dBFS ceiling, +1.5 dB; Loud: 0 dBFS, +3 dB), and normalization off is 0 dB.

using FluentGpu.Media;

namespace Wavee;

public static partial class Playback
{
    public static partial class Audio
    {
        /// <summary>A narration clip: its bytes from Spotify.DjTts, its format MP3, its gain from its loudness (banner).</summary>
        static IMediaByteSource? TtsSource(EntityId id, out Opened opened, out Fault fault)
        {
            opened = default;
            if (Spotify.DjTts.ClipOf(id) is not { } clip || !clip.AddReader(out bool complete, out byte[] whole, out int length))
            {
                fault = Fault.Unavailable;              // never ensured, dropped (its fulfil failed twice), or its spill was lost
                return null;
            }
            Spotify.DjTts.CheckCapIfDue();              // a reload brought a spilled clip back into memory
            Spotify.DjTts.ClipInfo info = clip.Info();
            fault = Fault.None;
            long durationMs = complete && info.DurationMs > 0 ? info.DurationMs : 0;
            int kbps = durationMs > 0 ? (int)(length * 8L / durationMs) : 0;
            opened = new Opened(Spotify.Audio.Format.Mp3, durationMs, Spotify.DjTts.GainDbOf(clip.LoudnessLufs),
                LabelFor(Spotify.Audio.Format.Mp3, 0, 0), kbps, IsLive: false, Peak: Spotify.DjTts.PeakLinearOf(clip.TruePeakDb),
                DurationExact: durationMs > 0);
            Log.Info("audio", $"audio.open tts key={clip.Key} complete={(complete ? 1 : 0)} bytes={length} durMs={durationMs} "
                              + $"gain={opened.GainDb:+0.0;-0.0} dB peak={opened.Peak:0.000} kbps={kbps}");
            return new TtsClipSource(clip, complete ? whole : null, length);
        }

        /// <summary>The byte source over one clip (banner: TWO SHAPES). Closing it releases the clip for the cache's spill.</summary>
        public sealed class TtsClipSource : IMediaByteSource
        {
            readonly Spotify.DjTts.Clip _clip;
            readonly byte[]? _whole;           // the Ready clip's bytes (seekable); null while it downloads (forward-only)
            readonly int _length;
            long _pos;
            int _cancelled, _closed;

            internal TtsClipSource(Spotify.DjTts.Clip clip, byte[]? whole, int length)
            {
                _clip = clip;
                _whole = whole;
                _length = length;
            }

            /// <summary>Was the clip whole when this source opened (seekable, its length known)?</summary>
            public bool OpenedComplete => _whole is not null;

            /// <summary>The bytes the clip holds now (all of them once Ready).</summary>
            public long Landed => _clip.Length;

            /// <summary>Has the clip's body ended?</summary>
            public bool ClipReady => _clip.State == Spotify.DjTts.ClipState.Ready;

            public long? Length => _whole is not null ? _length : null;

            public SourceCaps Caps => new() { Seekable = _whole is not null, KnownLength = _whole is not null, ExpensiveSeek = false };

            public bool TryOpen(in DataSpec spec)
            {
                long at = Math.Max(0, spec.Position);
                if (_whole is null && at != _pos) return false;     // forward-only while it downloads
                _pos = Math.Min(at, _whole is null ? at : _length);
                return Volatile.Read(ref _closed) == 0;
            }

            public int Read(Span<byte> dst)
            {
                if (dst.IsEmpty) return 0;
                if (_whole is not null)
                {
                    if (Volatile.Read(ref _cancelled) != 0) return -1;
                    int n = (int)Math.Min(dst.Length, _length - _pos);
                    if (n <= 0) return 0;
                    _whole.AsSpan((int)_pos, n).CopyTo(dst);
                    _pos += n;
                    return n;
                }
                int got = _clip.ReadAt(_pos, dst, ref _cancelled);
                if (got > 0) _pos += got;
                return got;
            }

            public long Seek(long offset)
            {
                if (_whole is null) return -1;
                _pos = Math.Clamp(offset, 0, _length);
                return _pos;
            }

            /// <summary>Release a read blocked in the clip's wait (non-blocking, any thread): it answers −1.</summary>
            public void Cancel()
            {
                Volatile.Write(ref _cancelled, 1);
                _clip.Wake();
            }

            public void Close()
            {
                Cancel();
                if (Interlocked.Exchange(ref _closed, 1) == 0) _clip.RemoveReader();
            }
        }
    }
}
