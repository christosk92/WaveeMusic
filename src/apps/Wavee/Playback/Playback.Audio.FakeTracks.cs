// ── Playback/Playback.Audio.FakeTracks.cs ────────────────────────────────────────────────────────────────────────────
// `--fake` only: the AI DJ fixture's tracks play as real (silent) MP3 bytes, so a talk-over and a radio join run through the real
// pump headless (plan-dj §2.10)
//
// Role: SHELL (a byte source the decoder reads; the set is written once on the UI thread at the fake seed)
//
// Every other `--fake` track loads into a silent session (no bytes, no decoder: `LoadCoreAsync`), so nothing can be prepared or
// joined into it. The DJ's seams are joins (Playback.Dj.TalkOver.cs), so the tracks the fake DJ pages serve (`Entities.SeedDjCues`)
// are registered here and, while a DJ session plays, open as a virtual MP3 of silent frames (Spotify.DjTts.Fake's frame: 44.1 kHz mono, 1152 samples each) of
// the track's catalogue length: an exact, seekable length the real decoder walks, as a narration clip's is. Nothing is allocated per
// byte: a read writes the frame header at each frame's start and zeros elsewhere.

using FluentGpu.Media;

namespace Wavee;

public static partial class Playback
{
    public static partial class Audio
    {
        static readonly Dictionary<EntityId, int> s_fakeAudible = new();
        static readonly Lock s_fakeAudibleGate = new();

        /// <summary>`--fake`: <paramref name="id"/> plays as <paramref name="durationMs"/> of silent MP3 bytes. UI thread, at the seed.</summary>
        internal static void FakeAudible(EntityId id, int durationMs)
        {
            if (!Platform.Args.Fake || id.IsEmpty || durationMs <= 0) return;
            lock (s_fakeAudibleGate) s_fakeAudible[id] = durationMs;
        }

        /// <summary>Does <paramref name="id"/> load as bytes under `--fake`: a narration clip, or a DJ fixture track while a DJ session plays
        /// (the same track played from any other list stays the silent session every other fixture plays)?</summary>
        static bool FakeHasBytes(EntityId id)
        {
            if (id.Provider == EntityProvider.Tts) return true;
            if (Dj.Current is null) return false;
            lock (s_fakeAudibleGate) return s_fakeAudible.ContainsKey(id);
        }

        /// <summary>The silent body of a registered `--fake` track, or null.</summary>
        static IMediaByteSource? FakeTrackSource(EntityId id, out Opened opened)
        {
            opened = default;
            int durationMs;
            if (!FakeHasBytes(id)) return null;
            lock (s_fakeAudibleGate) if (!s_fakeAudible.TryGetValue(id, out durationMs)) return null;
            const int rate = Spotify.DjTts.Fake.SampleRate, samples = Spotify.DjTts.Fake.SamplesPerFrame;
            int frames = (int)Math.Max(1, (long)durationMs * rate / 1000 / samples);
            long exactMs = (long)frames * samples * 1000 / rate;
            opened = new Opened(Spotify.Audio.Format.Mp3, exactMs, 0f, LabelFor(Spotify.Audio.Format.Mp3, 0, 0), 320, IsLive: false, DurationExact: true);
            return new SilentMp3Source(frames);
        }

        /// <summary>A virtual MP3 of <c>frames</c> silent frames: seekable, of known length.</summary>
        sealed class SilentMp3Source(int frames) : IMediaByteSource
        {
            readonly long _length = (long)frames * Spotify.DjTts.Fake.FrameBytes;
            long _pos;
            int _cancelled;

            public long? Length => _length;
            public SourceCaps Caps => new() { Seekable = true, KnownLength = true, ExpensiveSeek = false };

            public bool TryOpen(in DataSpec spec)
            {
                _pos = Math.Clamp(spec.Position, 0, _length);
                return true;
            }

            public int Read(Span<byte> dst)
            {
                if (Volatile.Read(ref _cancelled) != 0) return -1;
                int n = (int)Math.Min(dst.Length, _length - _pos);
                if (n <= 0) return 0;
                Span<byte> outSpan = dst[..n];
                outSpan.Clear();
                ReadOnlySpan<byte> header = Spotify.DjTts.Fake.FrameHeader;
                for (int i = 0; i < n;)
                {
                    long at = _pos + i;
                    int inFrame = (int)(at % Spotify.DjTts.Fake.FrameBytes);
                    if (inFrame < header.Length) { outSpan[i] = header[inFrame]; i++; continue; }
                    i += Spotify.DjTts.Fake.FrameBytes - inFrame;   // the frame's zeros, already cleared
                }
                _pos += n;
                return n;
            }

            public long Seek(long offset)
            {
                _pos = Math.Clamp(offset, 0, _length);
                return _pos;
            }

            public void Cancel() => Volatile.Write(ref _cancelled, 1);
            public void Close() => Cancel();
        }
    }
}
