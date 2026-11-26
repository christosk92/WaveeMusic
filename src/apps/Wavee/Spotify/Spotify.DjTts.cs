// ── Spotify/Spotify.DjTts.cs ─────────────────────────────────────────────────────────────────────────────────────────────
// the AI DJ's narration clips: fulfil → signed CDN url → download → session cache, and the clip a reader plays while it lands
//
// Role: SHELL (threads, sockets, files); the body, manifest, key and gain helpers are PURE
// Spec: docs/plans/wavee/dj-implementation.md §1.4, §1.5, §2.2, §2.3 · dj-protocol.md §2.1, §2.4
//
// ONE CLIP PER REQUEST BODY. A narration's fulfil body (`{2 ssml, 3 provider, 5 voice, 6 codec, 7 rate}`) is the narration item's
// `media.manifest` field 1.1.4 taken verbatim when there is one, else built here from the narration keys (`narration.<slot>.ssml`,
// `.voice`, `.tts_provider`, `.sample_rate`). The clip's KEY is the hex of the first 16 bytes of the body's SHA-256: the same SSML
// in the same voice is one clip and one request; the same words in another voice are another clip.
//
// THE FLOW (Ensure: idempotent, any thread). The fulfil on an api thread (`Api.TtsFulfil`: a 303 whose Location is the signed url;
// it is never followed), then at once the GET of that url on the audio CDN client (`Audio.CdnClient`: no bearer, no client-token —
// the url is signed and lives 300 s). Bytes land in the clip as they arrive: a cache MISS streams with no Content-Length while the
// server synthesises (a 24.5 s clip took 5.3 s), so a reader (`SourceRoute.Tts`, Playback.Audio.Tts.cs) plays the clip while it
// downloads, and the duration is learned when the body ends (NLayer's walk over the whole clip: the length the MP3 decoder plays).
// A failed fulfil, or a GET the CDN refuses at its headers, is tried once more (a fresh fulfil signs a fresh url); a second failure,
// or a body torn after bytes landed, is final: the clip is Failed, `[dj] narration dropped why=` is logged once, Settled says so.
//
// THE CACHE. Memory first, 64 MB across every clip. Past it the least recently used finished clips no reader holds are SPILLED to
// `<LocalFolder>\cache\dj-tts\<key>.tts` (a header and the MP3, written `.part` then moved, as Canvas.Files does) and freed; the next
// open reloads one. A spill carries the CDN's max-age (604 800 s): an older file is deleted and never read, a clip the CDN marked
// no-store or gave no max-age is never written, and the folder is trimmed least recently used first past 128 MB. A spilled file also
// answers the next session's Ensure for the same key without a request. `--fake` without a scratch profile keeps no files (the folder
// would be the user's own): a clip past the cap is then dropped from the cache, and an Ensure asks again.
//
// THREADS. Ensure, TryGet, ReadStats: any thread, one lock. IdOf: the UI thread (it interns the uri, C1). The fulfil: an api thread.
// The download and the spill: the pool. Settled: posted to the UI thread (Spotify.Post). A reader blocks only in Clip.ReadAt, on the
// decoder's thread, until bytes land, the body ends, the clip fails or the reader is cancelled.
//
// --fake (Spotify.DjTts.Fake.cs): the same code over two in-process transports, a spclient that answers 303 and a CDN that serves
// silent MPEG-1 Layer III clips built in code, so the fulfil, the download, the cache, the MP3 decoder and the duration all run with
// no network.

using System.Buffers;
using System.Security.Cryptography;
using System.Text;

namespace Wavee;

public static partial class Spotify
{
    public static partial class Audio
    {
        /// <summary>The audio CDN client (<see cref="Cdn"/>), for a narration's download (Spotify.DjTts.cs): the pooled connection and
        /// User-Agent a podcast enclosure is read with, and no Spotify credentials.</summary>
        internal static HttpClient CdnClient => Cdn;
    }

    /// <summary>The AI DJ's text-to-speech client: narration clips by key (see the file banner).</summary>
    public static partial class DjTts
    {
        // ── 1. the numbers ───────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The memory every clip shares (dj-implementation §2.3). Past it, finished clips spill to the cache folder.</summary>
        public const long MemoryCapBytes = 64L << 20;

        /// <summary>The cache folder's budget, least recently used first out past it.</summary>
        public const long DiskBudgetBytes = 128L << 20;

        /// <summary>A narration is asked for at most this many times (one retry), then dropped.</summary>
        public const int MaxAttempts = 2;

        /// <summary>How long the CDN may take to answer the GET's headers, and then to send the next byte (a MISS synthesises
        /// while it streams: the 24.5 s clip took 5.3 s in all, so this is many times the longest gap seen).</summary>
        public const int CdnIdleTimeoutMs = 15_000;

        /// <summary>The codec the body asks for (field 6): MP3, the only one Wavee requests or decodes here.</summary>
        public const int CodecMp3 = 6;

        /// <summary>The loudness and true peak every narration carried (`narration.*.loudness`, `.true_peak`, and the manifest's
        /// field 1.5): what a clip with no figures of its own is normalised from.</summary>
        public const float DefaultLoudnessLufs = -16f, DefaultTruePeakDb = -3f;

        /// <summary>The uri prefix of a clip's playable identity (<see cref="IdOf"/>; <see cref="EntityProvider.Tts"/>).</summary>
        public const string UriPrefix = "wavee:tts:";

        // ── 2. the request body, the manifest, the key, the gain (PURE) ──────────────────────────────────────────────────

        /// <summary>A narration as its keys name it (`narration.&lt;slot&gt;.*`, dj-implementation §1.3): what the fulfil body is built
        /// from when no `media.manifest` is at hand. The defaults are the only values ever observed.</summary>
        public readonly record struct Narration(string Ssml, string Voice = "VOICE1", string Provider = "SONANTIC_FAST",
            int SampleRate = 44_100, float LoudnessLufs = DefaultLoudnessLufs, float TruePeakDb = DefaultTruePeakDb);

        /// <summary>What a narration item's `media.manifest` carries (dj-protocol §2.4):
        /// `{1 {1 {1 verb, 3 path, 4 body, 5 1}, 5 {1 f32 loudness, 2 f32 true peak}}}`.</summary>
        public readonly record struct Manifest(string Verb, string Path, byte[] Body, float LoudnessLufs, float TruePeakDb);

        /// <summary>The fulfil body built from the narration keys: `{2 ssml, 3 provider, 5 voice, 6 MP3, 7 rate}`, in that field order
        /// (`12 &lt;len&gt; &lt;ssml&gt; 18 05 28 01 30 06 38 c4 d8 02` for the observed keys). PURE.</summary>
        public static byte[] FulfilBody(in Narration n)
        {
            byte[] ssml = Encoding.UTF8.GetBytes(n.Ssml ?? "");
            byte[] buffer = new byte[ssml.Length + 40];
            var w = new Decode.ProtoWriter(buffer);
            w.Utf8Always(2, ssml);
            w.U(3, (ulong)ProviderCode(n.Provider));
            w.U(5, (ulong)VoiceCode(n.Voice));
            w.U(6, CodecMp3);
            w.U(7, (ulong)Math.Max(0, n.SampleRate));
            return buffer.AsSpan(0, w.Length).ToArray();
        }

        /// <summary>`tts_provider` → field 3. Only SONANTIC_FAST (5) was ever seen; a bare number is taken as the code; anything else
        /// is sent as 5 and said once in the log (the meaning of the field is itself an inference). PURE but for that line.</summary>
        public static int ProviderCode(string? provider)
        {
            if (string.Equals(provider, "SONANTIC_FAST", StringComparison.OrdinalIgnoreCase)) return 5;
            if (int.TryParse(provider, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n) && n > 0) return n;
            WarnOnce(ref s_warnedProvider, "tts provider '" + provider + "' is not one Wavee knows: sent as 5 (SONANTIC_FAST)");
            return 5;
        }

        /// <summary>`voice` → field 5: `VOICE&lt;n&gt;` is n (VOICE1 was the only one seen), a bare number is itself, anything else is
        /// sent as 1 and said once.</summary>
        public static int VoiceCode(string? voice)
        {
            ReadOnlySpan<char> v = voice ?? "";
            if (v.StartsWith("VOICE", StringComparison.OrdinalIgnoreCase)) v = v[5..];
            if (int.TryParse(v, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n) && n > 0) return n;
            WarnOnce(ref s_warnedVoice, "tts voice '" + voice + "' is not one Wavee knows: sent as 1 (VOICE1)");
            return 1;
        }

        static int s_warnedProvider, s_warnedVoice;

        static void WarnOnce(ref int latch, string message)
        {
            if (Interlocked.Exchange(ref latch, 1) == 0) Log.Warn("dj", message);
        }

        /// <summary>Read a narration item's `media.manifest` (its raw bytes, base64 already undone). False when it carries no
        /// request body. Loudness and true peak default to the observed −16 LUFS / −3 dBTP when the manifest has none. PURE.</summary>
        public static bool TryReadManifest(ReadOnlySpan<byte> manifest, out Manifest m)
        {
            m = default;
            string verb = "", path = "";
            byte[] body = [];
            float loudness = DefaultLoudnessLufs, peak = DefaultTruePeakDb;
            var root = new Decode.ProtoReader(manifest);
            while (root.Next())
            {
                if (root.Field != 1 || root.Wire != 2) { root.Skip(); continue; }
                Decode.ProtoReader media = root.Message();
                while (media.Next())
                {
                    if (media.Field == 1 && media.Wire == 2)
                    {
                        Decode.ProtoReader request = media.Message();
                        while (request.Next())
                        {
                            if (request.Field == 1 && request.Wire == 2) verb = Encoding.UTF8.GetString(request.Bytes());
                            else if (request.Field == 3 && request.Wire == 2) path = Encoding.UTF8.GetString(request.Bytes());
                            else if (request.Field == 4 && request.Wire == 2) body = request.Bytes().ToArray();
                            else request.Skip();
                        }
                    }
                    else if (media.Field == 5 && media.Wire == 2)
                    {
                        Decode.ProtoReader level = media.Message();
                        while (level.Next())
                        {
                            if (level.Field == 1 && level.Wire == 5) loudness = BitConverter.UInt32BitsToSingle(level.Fixed32());
                            else if (level.Field == 2 && level.Wire == 5) peak = BitConverter.UInt32BitsToSingle(level.Fixed32());
                            else level.Skip();
                        }
                    }
                    else media.Skip();
                }
            }
            if (body.Length == 0) return false;
            m = new Manifest(verb, path, body, float.IsFinite(loudness) ? loudness : DefaultLoudnessLufs,
                float.IsFinite(peak) ? peak : DefaultTruePeakDb);
            return true;
        }

        /// <summary>A `media.manifest` for <paramref name="body"/> in the shape the official player publishes (dj-protocol §2.4): what a
        /// narration item's Connect metadata carries, and what <see cref="TryReadManifest"/> reads back. PURE.</summary>
        public static byte[] EncodeManifest(ReadOnlySpan<byte> body, float loudnessLufs, float truePeakDb)
        {
            byte[] buffer = new byte[body.Length + 96];
            var w = new Decode.ProtoWriter(buffer);
            int media = w.Open(1);
            int request = w.Open(1);
            w.Utf8Always(1, "POST"u8);
            w.Utf8Always(3, "client-tts/v1/fulfill"u8);
            w.Utf8Always(4, body);
            w.U(5, 1);
            w.Close(request);
            int level = w.Open(5);
            w.F32(1, loudnessLufs);
            w.F32(2, truePeakDb);
            w.Close(level);
            w.Close(media);
            return buffer.AsSpan(0, w.Length).ToArray();
        }

        /// <summary>The clip's key: the hex of the first 16 bytes of the body's SHA-256 (32 characters). PURE.</summary>
        public static string KeyOf(ReadOnlySpan<byte> body)
        {
            Span<byte> hash = stackalloc byte[32];
            SHA256.HashData(body, hash);
            return Convert.ToHexStringLower(hash[..16]);
        }

        /// <summary>The clip's normalization figure in the −14 LUFS frame every Spotify gain is in: −14 − loudness, so the narrations'
        /// −16 LUFS is +2 dB. `Playback.Audio.NormalizationFactor` then adds the user's mode (Quiet −9, Normal 0, Loud +3), which makes the
        /// whole gain the mode's target − the clip's loudness (−23, −14 or −11 LUFS − (−16)), and caps it by the true peak exactly as it
        /// caps a song's. Off is 0 dB. PURE.</summary>
        public static float GainDbOf(float loudnessLufs) => float.IsFinite(loudnessLufs) ? -14f - loudnessLufs : 0f;

        /// <summary>A true peak in dBTP as the linear peak the normalization cap reads (−3 dBTP is 0.708). 0 = unknown. PURE.</summary>
        public static float PeakLinearOf(float truePeakDb) => float.IsFinite(truePeakDb) ? MathF.Pow(10f, truePeakDb / 20f) : 0f;

        // ── 3. the clip ──────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Where a clip is. Fulfilling and Downloading are "in flight"; Ready has every byte (in memory or spilled);
        /// Failed is final for this session (the narration is dropped).</summary>
        public enum ClipState : byte { Fulfilling, Downloading, Ready, Failed }

        /// <summary>One clip as a value, for the wiring and the inspector. <see cref="Frames"/> is the decoded length at
        /// <see cref="SampleRate"/> (−1 until the body ended), <see cref="DurationMs"/> the same in ms. <see cref="ExpectedBytes"/> is
        /// the CDN's Content-Length, −1 for a streamed MISS. <see cref="FirstByteMs"/> and <see cref="DoneMs"/> are measured from the
        /// Ensure that started it.</summary>
        public readonly record struct ClipInfo(string Key, ClipState State, string Why, string Source, int Attempts,
            long Bytes, long ExpectedBytes, long Frames, int SampleRate, int Channels, long DurationMs,
            bool InMemory, bool OnDisk, int Readers, string CdnCache, string Host,
            long FirstByteMs, long DoneMs, float LoudnessLufs, float TruePeakDb, long ExpiresUnix)
        {
            /// <summary>Every byte is here and its length is known.</summary>
            public bool Ready => State == ClipState.Ready;
            /// <summary>Fulfilling or downloading.</summary>
            public bool InFlight => State is ClipState.Fulfilling or ClipState.Downloading;
        }

        /// <summary>One narration clip: its request, its bytes (growing while they download), its facts. Every field is read and
        /// written under <see cref="_sync"/>; a reader waits on it for bytes.</summary>
        internal sealed class Clip
        {
            readonly object _sync = new();
            readonly long _t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            byte[] _buf = [];
            int _len, _readers, _attempts;
            ClipState _state = ClipState.Fulfilling;
            string _why = "", _cdnCache = "", _host = "";
            long _expected = -1, _frames = -1, _firstByteMs = -1, _doneMs = -1, _expiresUnix, _lastUse = Environment.TickCount64;
            int _rate, _channels;
            bool _inMemory = true, _onDisk;

            internal Clip(string key, byte[] body, float loudness, float peak, string source)
            {
                Key = key;
                Body = body;
                LoudnessLufs = loudness;
                TruePeakDb = peak;
                Source = source;
            }

            public string Key { get; }
            /// <summary>The fulfil body (what the key hashes).</summary>
            public byte[] Body { get; }
            public float LoudnessLufs { get; }
            public float TruePeakDb { get; }
            /// <summary>keys · manifest · disk (a spill of an earlier session answered the Ensure).</summary>
            public string Source { get; private set; }

            public long LastUse => Volatile.Read(ref _lastUse);
            public void Touch() => Volatile.Write(ref _lastUse, Environment.TickCount64);

            public ClipState State { get { lock (_sync) return _state; } }

            /// <summary>A spill candidate: every byte here, held in memory, no reader on it.</summary>
            public bool Spillable { get { lock (_sync) return _state == ClipState.Ready && _inMemory && _readers == 0; } }

            public ClipInfo Info()
            {
                lock (_sync)
                {
                    long durationMs = _frames >= 0 && _rate > 0 ? _frames * 1000 / _rate : -1;
                    return new ClipInfo(Key, _state, _why, Source, _attempts, _len, _expected, _frames, _rate, _channels, durationMs,
                        _inMemory && _len > 0, _onDisk, _readers, _cdnCache, _host, _firstByteMs, _doneMs, LoudnessLufs, TruePeakDb,
                        _expiresUnix);
                }
            }

            long SinceStartMs => (long)System.Diagnostics.Stopwatch.GetElapsedTime(_t0).TotalMilliseconds;

            internal void Attempt(ClipState state)
            {
                lock (_sync)
                {
                    if (state == ClipState.Fulfilling) _attempts++;
                    _state = state;
                }
            }

            internal void BeginBody(long expected, string host, string cdnCache, long expiresUnix)
            {
                lock (_sync)
                {
                    _expected = expected;
                    _host = host;
                    _cdnCache = cdnCache;
                    _expiresUnix = expiresUnix;
                    if (expected > 0 && _buf.Length < expected) _buf = GC.AllocateUninitializedArray<byte>((int)expected);
                }
            }

            /// <summary>Bytes off the wire: appended, and every waiting reader woken.</summary>
            internal void Append(ReadOnlySpan<byte> bytes)
            {
                if (bytes.IsEmpty) return;
                lock (_sync)
                {
                    if (_len + bytes.Length > _buf.Length)
                    {
                        var grown = GC.AllocateUninitializedArray<byte>(Math.Max(_len + bytes.Length, Math.Max(64 * 1024, _buf.Length * 2)));
                        _buf.AsSpan(0, _len).CopyTo(grown);
                        _buf = grown;
                    }
                    bytes.CopyTo(_buf.AsSpan(_len));
                    _len += bytes.Length;
                    if (_firstByteMs < 0) _firstByteMs = SinceStartMs;
                    Monitor.PulseAll(_sync);
                }
                Interlocked.Add(ref s_memBytes, bytes.Length);
            }

            /// <summary>The body ended: trim the buffer to its length and hand it to the length walk.</summary>
            internal byte[] Whole()
            {
                lock (_sync)
                {
                    if (_buf.Length != _len) _buf = _buf.AsSpan(0, _len).ToArray();
                    return _buf;
                }
            }

            internal long Length { get { lock (_sync) return _len; } }

            internal void Ready(long frames, int rate, int channels)
            {
                lock (_sync)
                {
                    _frames = frames;
                    _rate = rate;
                    _channels = channels;
                    _state = ClipState.Ready;
                    _doneMs = SinceStartMs;
                    _why = "";
                    Monitor.PulseAll(_sync);
                }
            }

            /// <summary>Final: the narration is dropped. Its bytes are freed (a reader reads −1 from here on).</summary>
            internal void Fail(string why)
            {
                long freed;
                lock (_sync)
                {
                    _state = ClipState.Failed;
                    _why = why;
                    _doneMs = SinceStartMs;
                    freed = _inMemory ? _len : 0;
                    _buf = [];
                    _len = 0;
                    _inMemory = false;
                    Monitor.PulseAll(_sync);
                }
                Interlocked.Add(ref s_memBytes, -freed);
            }

            /// <summary>Its spill file was gone or unreadable when a reader needed it: the next Ensure asks for it again.</summary>
            internal bool Lost { get { lock (_sync) return _state == ClipState.Failed && _why == LostWhy; } }

            /// <summary>Back to an empty fetch (a retry after a refusal that landed no byte).</summary>
            internal void Restart()
            {
                lock (_sync)
                {
                    _expected = -1;
                    _cdnCache = "";
                }
            }

            /// <summary>A spill of an earlier session answered the Ensure: Ready, on disk, nothing in memory yet.</summary>
            internal void Adopt(in SpillHeader h)
            {
                lock (_sync)
                {
                    _len = h.Length;
                    _expected = h.Length;
                    _frames = h.Frames;
                    _rate = h.SampleRate;
                    _channels = h.Channels;
                    _expiresUnix = h.ExpiresUnix;
                    _inMemory = false;
                    _onDisk = true;
                    _state = ClipState.Ready;
                    _doneMs = SinceStartMs;
                    Source = "disk";
                }
            }

            /// <summary>A reader opened on the clip (it is never spilled while one is). Reloads a spilled clip first; false when that
            /// failed or the clip failed.</summary>
            internal bool AddReader(out bool complete, out byte[] whole, out int length)
            {
                lock (_sync)
                {
                    complete = false;
                    whole = [];
                    length = 0;
                    if (_state == ClipState.Failed) return false;
                    if (_state == ClipState.Ready && !EnsureLoadedLocked()) return false;
                    _readers++;
                    complete = _state == ClipState.Ready;
                    whole = _buf;
                    length = _len;
                    Touch();
                    return true;
                }
            }

            internal void RemoveReader()
            {
                lock (_sync) _readers = Math.Max(0, _readers - 1);
            }

            /// <summary>Wake a reader blocked in <see cref="ReadAt"/> (its source was cancelled).</summary>
            internal void Wake()
            {
                lock (_sync) Monitor.PulseAll(_sync);
            }

            /// <summary>Copy the bytes at <paramref name="position"/>, WAITING for them while the clip is in flight: &gt;0 copied,
            /// 0 only at the true end of a Ready clip, −1 when the clip failed or <paramref name="cancelled"/> went non-zero. The
            /// decoder's thread blocks here, and nowhere else.</summary>
            internal int ReadAt(long position, Span<byte> dst, ref int cancelled)
            {
                if (dst.IsEmpty) return 0;
                lock (_sync)
                {
                    while (true)
                    {
                        if (Volatile.Read(ref cancelled) != 0) return -1;
                        if (position < _len)
                        {
                            if (!_inMemory && !EnsureLoadedLocked()) return -1;
                            int n = (int)Math.Min(dst.Length, _len - position);
                            _buf.AsSpan((int)position, n).CopyTo(dst);
                            return n;
                        }
                        if (_state == ClipState.Ready) return 0;
                        if (_state == ClipState.Failed) return -1;
                        Monitor.Wait(_sync, 250);
                    }
                }
            }

            /// <summary>Write a finished, unread clip to the cache folder and free its memory. False (nothing changed) when it is not
            /// spillable or may not be kept on disk; the caller then drops it.</summary>
            internal bool TrySpill(string folder, out long freed)
            {
                freed = 0;
                byte[] bytes;
                SpillHeader header;
                lock (_sync)
                {
                    if (_state != ClipState.Ready || !_inMemory || _readers != 0) return false;
                    if (_expiresUnix <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return false;   // no max-age, or no-store
                    bytes = _buf;
                    header = new SpillHeader(_expiresUnix, _frames, _rate, _channels, LoudnessLufs, TruePeakDb, _len);
                }
                if (!_onDisk && !WriteSpill(folder, Key, in header, bytes)) return false;
                lock (_sync)
                {
                    if (_readers != 0) { _onDisk = true; return false; }   // a reader came between: keep it in memory
                    freed = _len;
                    _buf = [];
                    _inMemory = false;
                    _onDisk = true;
                }
                Interlocked.Add(ref s_memBytes, -freed);
                return true;
            }

            /// <summary>Free a finished, unread clip's memory without keeping it anywhere (no disk): the caller forgets the clip.</summary>
            internal bool TryDrop(out long freed)
            {
                freed = 0;
                lock (_sync)
                {
                    if (_state != ClipState.Ready || !_inMemory || _readers != 0) return false;
                    freed = _len;
                    _buf = [];
                    _inMemory = false;
                }
                Interlocked.Add(ref s_memBytes, -freed);
                return true;
            }

            bool EnsureLoadedLocked()
            {
                if (_inMemory) return true;
                if (!TryReadSpill(SpillFolder, Key, out SpillHeader header, out byte[] bytes) || header.Length != _len)
                {
                    _state = ClipState.Failed;
                    _why = LostWhy;
                    _len = 0;
                    Monitor.PulseAll(_sync);
                    Log.Warn("dj", $"tts.reload key={Key} failed: the spill is gone or unreadable (the next Ensure asks again)");
                    return false;
                }
                _buf = bytes;
                _inMemory = true;
                Interlocked.Add(ref s_memBytes, _len);
                Interlocked.Increment(ref s_reloads);
                Log.Info("dj", $"tts.reload key={Key} bytes={_len}");
                s_capCheckDue = 1;
                return true;
            }
        }

        // ── 4. the cache ─────────────────────────────────────────────────────────────────────────────────────────────────

        const string LostWhy = "spill-lost";

        static readonly Lock s_gate = new();
        static readonly Dictionary<string, Clip> s_clips = new(StringComparer.Ordinal);
        static readonly Dictionary<EntityId, string> s_ids = [];
        static long s_memBytes, s_hits, s_misses, s_diskAdopts, s_reloads, s_spills, s_evictions, s_dropped, s_fulfils, s_retries;
        static int s_spilling, s_capCheckDue;

        /// <summary>Make sure the clip for this narration is cached or on its way, and answer its key. Idempotent: a clip already in
        /// the cache (in flight, Ready, spilled, or Failed: a drop is final for the session) starts nothing. Any thread; never blocks.</summary>
        public static string Ensure(in Narration n)
            => EnsureBody(FulfilBody(n), n.LoudnessLufs, n.TruePeakDb, "keys");

        /// <summary><see cref="Ensure(in Narration)"/> from a narration item's `media.manifest` (raw bytes): its body is sent VERBATIM
        /// and its loudness and true peak are the clip's. "" when the manifest carries no body.</summary>
        public static string Ensure(ReadOnlySpan<byte> manifest)
            => TryReadManifest(manifest, out Manifest m) ? EnsureBody(m.Body, m.LoudnessLufs, m.TruePeakDb, "manifest") : "";

        static string EnsureBody(byte[] body, float loudness, float peak, string source)
        {
            string key = KeyOf(body);
            Clip clip;
            lock (s_gate)
            {
                if (s_clips.TryGetValue(key, out Clip? held) && !held.Lost)
                {
                    s_hits++;
                    held.Touch();
                    return key;
                }
                clip = new Clip(key, body, loudness, peak, source);
                s_clips[key] = clip;
                s_misses++;
            }
            _ = Task.Run(() => FetchAsync(clip));
            return key;
        }

        /// <summary>The clip under <paramref name="key"/>, as a value. False when no Ensure asked for it this session (or the cap
        /// dropped it with no folder to spill to). Any thread.</summary>
        public static bool TryGet(string key, out ClipInfo clip)
        {
            lock (s_gate)
            {
                if (s_clips.TryGetValue(key, out Clip? held))
                {
                    held.Touch();
                    clip = held.Info();
                    return true;
                }
            }
            clip = default;
            return false;
        }

        /// <summary>The playable identity of a clip: `wavee:tts:&lt;key&gt;` (<see cref="EntityProvider.Tts"/>, routed to
        /// <c>SourceRoute.Tts</c>). UI thread (it interns the uri, C1).</summary>
        public static EntityId IdOf(string key)
        {
            EntityId id = EntityId.Parse(UriPrefix + key);
            lock (s_gate) s_ids[id] = key;
            return id;
        }

        /// <summary>The clip a playable identity names, or null (an id <see cref="IdOf"/> never minted, or a clip the cache no longer
        /// holds). Any thread; no text is read (the pump's side of the route).</summary>
        internal static Clip? ClipOf(EntityId id)
        {
            lock (s_gate)
            {
                if (!s_ids.TryGetValue(id, out string? key) || !s_clips.TryGetValue(key, out Clip? clip)) return null;
                clip.Touch();
                return clip;
            }
        }

        /// <summary>A clip settled (Ready or Failed), on the UI thread: the wiring's cue to schedule or drop the narration.</summary>
        public static event Action<ClipInfo>? Settled;

        static void NoteSettled(Clip clip)
        {
            ClipInfo info = clip.Info();
            Spotify.Post(() => Settled?.Invoke(info));
        }

        /// <summary>The cache as one value (the inspector's `djtts cache`).</summary>
        public readonly record struct Stats(int Clips, int InFlight, long MemoryBytes, long Hits, long Misses, long DiskAdopts,
            long Reloads, long Spills, long Evictions, long Dropped, long Fulfils, long Retries, bool Spilling,
            bool DiskAllowed, string Folder, int DiskFiles, long DiskBytes);

        public static Stats ReadStats()
        {
            int clips, inFlight = 0;
            lock (s_gate)
            {
                clips = s_clips.Count;
                foreach (Clip c in s_clips.Values) if (c.State is ClipState.Fulfilling or ClipState.Downloading) inFlight++;
            }
            int files = 0;
            long diskBytes = 0;
            try
            {
                if (DiskAllowed && Directory.Exists(SpillFolder))
                    foreach (FileInfo f in new DirectoryInfo(SpillFolder).GetFiles("*" + SpillExtension)) { files++; diskBytes += f.Length; }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return new Stats(clips, inFlight, Interlocked.Read(ref s_memBytes), Interlocked.Read(ref s_hits), Interlocked.Read(ref s_misses),
                Interlocked.Read(ref s_diskAdopts), Interlocked.Read(ref s_reloads), Interlocked.Read(ref s_spills),
                Interlocked.Read(ref s_evictions), Interlocked.Read(ref s_dropped), Interlocked.Read(ref s_fulfils),
                Interlocked.Read(ref s_retries), Volatile.Read(ref s_spilling) != 0, DiskAllowed, SpillFolder, files, diskBytes);
        }

        /// <summary>Every clip, least recently used first (the inspector's list).</summary>
        public static List<ClipInfo> Snapshot()
        {
            var list = new List<(long Use, ClipInfo Info)>();
            lock (s_gate) foreach (Clip c in s_clips.Values) list.Add((c.LastUse, c.Info()));
            list.Sort(static (a, b) => a.Use.CompareTo(b.Use));
            return list.ConvertAll(static x => x.Info);
        }

        /// <summary>Is anything still moving: a clip in flight, or a spill pass? The inspector waits on it.</summary>
        public static bool Busy
        {
            get
            {
                if (Volatile.Read(ref s_spilling) != 0) return true;
                lock (s_gate)
                    foreach (Clip c in s_clips.Values)
                        if (c.State is ClipState.Fulfilling or ClipState.Downloading) return true;
                return false;
            }
        }

        /// <summary>Forget every clip and counter of this session; with <paramref name="disk"/> also delete the spill folder. For the
        /// inspector's repeatable runs: a clip a reader still holds keeps its bytes until that reader closes.</summary>
        public static void Reset(bool disk)
        {
            lock (s_gate)
            {
                s_clips.Clear();
                s_ids.Clear();
                s_memBytes = 0;
                s_hits = s_misses = s_diskAdopts = s_reloads = s_spills = s_evictions = s_dropped = s_fulfils = s_retries = 0;
            }
            if (!disk || !DiskAllowed) return;
            try { if (Directory.Exists(SpillFolder)) Directory.Delete(SpillFolder, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn("dj", "tts.reset could not clear the folder: " + ex.Message); }
        }

        // ── 5. the fetch: fulfil, then the GET at once ──────────────────────────────────────────────────────────────────

        /// <summary>The fulfil's transport: null is the live non-redirecting client (`Api.TtsFulfil`); `--fake` answers in process.</summary>
        internal static HttpMessageInvoker? FulfilVia => Platform.Args.Fake ? Fake.Spclient : null;

        /// <summary>The download's client: the audio CDN client; `--fake` serves the clips in process.</summary>
        static HttpClient CdnHttp => Platform.Args.Fake ? Fake.Cdn : Audio.CdnClient;

        /// <summary>One fulfil, now, on the calling thread (the inspector's `djtts fulfil`: the body is sent exactly as given).</summary>
        public static Api.TtsAnswer FulfilNow(byte[] body, CancellationToken ct)
        {
            Interlocked.Increment(ref s_fulfils);
            return Api.TtsFulfil(body, ct, FulfilVia);
        }

        static async Task FetchAsync(Clip clip)
        {
            try
            {
                if (TryAdoptSpill(clip)) { NoteSettled(clip); return; }
                string why = "";
                for (int attempt = 1; attempt <= MaxAttempts; attempt++)
                {
                    if (attempt > 1) { Interlocked.Increment(ref s_retries); Log.Info("dj", $"tts.retry key={clip.Key} after={why}"); }
                    clip.Attempt(ClipState.Fulfilling);
                    Interlocked.Increment(ref s_fulfils);
                    byte[] body = clip.Body;
                    Api.TtsAnswer answer = await Api.RunAsync(() => Api.TtsFulfil(body, CancellationToken.None, FulfilVia)).ConfigureAwait(false);
                    if (!answer.Redirected)
                    {
                        why = "fulfil-" + (answer.Status == 0 ? "transport" : answer.Status.ToString(System.Globalization.CultureInfo.InvariantCulture))
                            + (answer.Status is >= 300 and < 400 ? "-no-location" : "");
                        continue;
                    }
                    clip.Attempt(ClipState.Downloading);
                    Download got = await DownloadAsync(clip, answer.Location!).ConfigureAwait(false);
                    if (got.Ok) { Finish(clip); return; }
                    why = got.Why;
                    if (!got.Retryable) break;
                    clip.Restart();
                }
                Drop(clip, why);
            }
            catch (Exception ex)
            {
                Drop(clip, "fault-" + ex.GetType().Name);
            }
        }

        static void Drop(Clip clip, string why)
        {
            clip.Fail(why);
            Interlocked.Increment(ref s_dropped);
            ClipInfo info = clip.Info();
            Log.Warn("dj", $"narration dropped why={why} key={clip.Key} attempts={info.Attempts} bytes={info.Bytes}");
            NoteSettled(clip);
        }

        /// <summary>How a download ended: <see cref="Ok"/>, or why not and whether a fresh fulfil may fix it (a refusal at the
        /// headers landed no byte; a torn body may have been played from already).</summary>
        readonly record struct Download(bool Ok, bool Retryable, string Why);

        static async Task<Download> DownloadAsync(Clip clip, string url)
        {
            string host = HostOf(url);
            using var cts = new CancellationTokenSource(CdnIdleTimeoutMs);
            // The url is signed: no Authorization, no client-token. The CDN client carries neither, and nothing is stamped here.
            using var request = new HttpRequestMessage(HttpMethod.Get, url)
            {
                Version = System.Net.HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            };
            request.Options.Set(Wire.LogPathOption, LogPath(url));   // the path carries the narration's text: the wire log names a hash of it
            HttpResponseMessage response;
            try { response = await CdnHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
            {
                return new Download(false, true, "cdn-" + (ex is OperationCanceledException ? "timeout" : "transport"));
            }
            using (response)
            {
                int status = (int)response.StatusCode;
                if (status != 200)
                    return new Download(false, true, "cdn-" + status.ToString(System.Globalization.CultureInfo.InvariantCulture));
                long expected = response.Content.Headers.ContentLength ?? -1;
                var cache = response.Headers.CacheControl;
                long maxAge = cache is { NoStore: false, MaxAge: { } age } ? (long)age.TotalSeconds : 0;
                long expires = maxAge > 0 ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() + maxAge : 0;
                string xcache = response.Headers.TryGetValues("X-Cache", out IEnumerable<string>? xs) ? xs.FirstOrDefault() ?? "" : "";
                clip.BeginBody(expected, host, xcache, expires);
                Log.Info("dj", $"tts.cdn key={clip.Key} host={host} status=200 len={(expected >= 0 ? expected.ToString(System.Globalization.CultureInfo.InvariantCulture) : "stream")} "
                               + $"cache={(xcache.Length > 0 ? xcache : "-")} maxAge={maxAge}");
                byte[] chunk = ArrayPool<byte>.Shared.Rent(64 * 1024);
                long total = 0;
                try
                {
                    using Stream body = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
                    while (true)
                    {
                        cts.CancelAfter(CdnIdleTimeoutMs);              // the idle clock: a stream that keeps sending is alive
                        int n = await body.ReadAsync(chunk.AsMemory(), cts.Token).ConfigureAwait(false);
                        if (n <= 0) break;
                        clip.Append(chunk.AsSpan(0, n));
                        total += n;
                        CheckCap();
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
                {
                    return new Download(false, total == 0, "cdn-torn-" + (ex is OperationCanceledException ? "idle" : "transport") + "-at-" + total);
                }
                finally { ArrayPool<byte>.Shared.Return(chunk); }
                if (expected >= 0 && total != expected) return new Download(false, false, "cdn-short-" + total + "-of-" + expected);
                if (total == 0) return new Download(false, true, "cdn-empty");
                return new Download(true, false, "");
            }
        }

        /// <summary>The body ended: its length from NLayer's walk over the whole clip (the length the MP3 decoder plays, its LAME
        /// delay and padding trimmed when it carries them), then Ready.</summary>
        static void Finish(Clip clip)
        {
            byte[] bytes = clip.Whole();
            if (!TryMeasure(bytes, out long frames, out int rate, out int channels))
            {
                Drop(clip, "not-mpeg-audio");
                return;
            }
            clip.Ready(frames, rate, channels);
            ClipInfo info = clip.Info();
            Log.Info("dj", $"tts.ready key={clip.Key} bytes={info.Bytes} frames={frames} rate={rate} durMs={info.DurationMs} "
                           + $"firstByteMs={info.FirstByteMs} doneMs={info.DoneMs} attempts={info.Attempts} cache={(info.CdnCache.Length > 0 ? info.CdnCache : "-")}");
            NoteSettled(clip);
            CheckCap();
        }

        /// <summary>The decoded length of a whole MP3 (frames per channel at its own rate), as NLayer reports it to the decoder.</summary>
        public static bool TryMeasure(byte[] mp3, out long frames, out int rate, out int channels)
        {
            frames = -1;
            rate = channels = 0;
            try
            {
                using var stream = new MemoryStream(mp3, writable: false);
                using var file = new NLayer.MpegFile(stream);
                rate = file.SampleRate;
                channels = Math.Max(1, file.Channels);
                long length = file.Length;
                if (rate <= 0 || length < 0) return false;
                frames = length / (sizeof(float) * (long)channels);
                return frames > 0;
            }
            catch (Exception ex)
            {
                Log.Warn("dj", "tts.measure failed: " + ex.GetType().Name + " " + ex.Message);
                return false;
            }
        }

        static string HostOf(string url)
            => Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ? uri.Host : "-";

        /// <summary>The CDN url's path as the wire log may name it: the url carries the narration's SSML (its words) base64 in a path segment,
        /// so every segment from the first long one on (more than 24 characters: a voice, format or rate segment is short) is replaced by
        /// <c>{ssml:&lt;8 hex&gt;}</c>, a hash of the rest that tells two clips (and a retry of one) apart without the words. The query, signed,
        /// is never logged anyway. PURE.</summary>
        internal static string LogPath(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) return "/-";
            string path = uri.AbsolutePath;
            int start = 0;
            while (start < path.Length)
            {
                int end = path.IndexOf('/', start + 1);
                if (end < 0) end = path.Length;
                if (end - start - 1 > 24)
                {
                    uint hash = 2166136261;
                    for (int i = start; i < path.Length; i++) { hash ^= path[i]; hash *= 16777619; }
                    return path[..start] + "/{ssml:" + hash.ToString("x8", System.Globalization.CultureInfo.InvariantCulture) + "}";
                }
                start = end;
            }
            return path;
        }

        // ── 6. the cap and the spill ─────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The cache folder: `&lt;LocalFolder&gt;\cache\dj-tts` (under LocalFolder, so Settings ▸ Storage counts it and a
        /// factory reset clears it).</summary>
        public static string SpillFolder => Path.Combine(Platform.LocalFolder, "cache", "dj-tts");

        /// <summary>May clips be written to disk? Not under `--fake` without a scratch profile: LocalFolder is then the user's own.</summary>
        public static bool DiskAllowed => !(Platform.Args.Fake && Platform.ProfileRoot.Length == 0);

        const string SpillExtension = ".tts";

        /// <summary>Over the memory cap? Start the one spill pass (pool), which frees least recently used finished clips until under.</summary>
        static void CheckCap()
        {
            Volatile.Write(ref s_capCheckDue, 0);
            if (Interlocked.Read(ref s_memBytes) <= MemoryCapBytes) return;
            if (Interlocked.Exchange(ref s_spilling, 1) != 0) return;
            _ = Task.Run(SpillPass);
        }

        static void SpillPass()
        {
            try
            {
                bool disk = DiskAllowed;
                while (Interlocked.Read(ref s_memBytes) > MemoryCapBytes)
                {
                    Clip? victim = null;
                    lock (s_gate)
                        foreach (Clip c in s_clips.Values)
                            if (c.Spillable && (victim is null || c.LastUse < victim.LastUse)) victim = c;
                    if (victim is null) break;                         // only clips in flight or being read: nothing to free yet
                    if (disk && victim.TrySpill(SpillFolder, out long freed))
                    {
                        Interlocked.Increment(ref s_spills);
                        Log.Info("dj", $"tts.spill key={victim.Key} bytes={freed} mem={Interlocked.Read(ref s_memBytes)}");
                        TrimDisk();
                        continue;
                    }
                    if (victim.TryDrop(out long dropped))
                    {
                        lock (s_gate) if (s_clips.TryGetValue(victim.Key, out Clip? same) && ReferenceEquals(same, victim)) s_clips.Remove(victim.Key);
                        Interlocked.Increment(ref s_evictions);
                        Log.Info("dj", $"tts.evict key={victim.Key} bytes={dropped} disk={(disk ? 1 : 0)} mem={Interlocked.Read(ref s_memBytes)}");
                    }
                }
            }
            catch (Exception ex) { Log.Warn("dj", "tts.spill pass failed", ex); }
            finally { Volatile.Write(ref s_spilling, 0); }
        }

        /// <summary>A reload brought a clip back into memory: the next append or open checks the cap. Called by the route on open.</summary>
        internal static void CheckCapIfDue()
        {
            if (Volatile.Read(ref s_capCheckDue) != 0) CheckCap();
        }

        /// <summary>A spill file's header: magic, version, then the facts a Ready clip needs without its bytes.</summary>
        internal readonly record struct SpillHeader(long ExpiresUnix, long Frames, int SampleRate, int Channels, float LoudnessLufs,
            float TruePeakDb, int Length);

        const int SpillMagic = 0x53545457;   // "WTTS"
        const int SpillVersion = 1;

        static string SpillPath(string folder, string key) => Path.Combine(folder, key + SpillExtension);

        static bool WriteSpill(string folder, string key, in SpillHeader h, byte[] bytes)
        {
            string target = SpillPath(folder, key), part = target + ".part";
            try
            {
                Directory.CreateDirectory(folder);
                using (var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024))
                using (var w = new BinaryWriter(file))
                {
                    w.Write(SpillMagic);
                    w.Write(SpillVersion);
                    w.Write(h.ExpiresUnix);
                    w.Write(h.Frames);
                    w.Write(h.SampleRate);
                    w.Write(h.Channels);
                    w.Write(h.LoudnessLufs);
                    w.Write(h.TruePeakDb);
                    w.Write(h.Length);
                    w.Write(bytes, 0, h.Length);
                }
                File.Move(part, target, overwrite: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn("dj", "tts.spill write failed: " + ex.GetType().Name + " " + ex.Message);
                try { File.Delete(part); } catch (Exception) { }
                return false;
            }
        }

        static bool TryReadHeader(BinaryReader r, out SpillHeader h)
        {
            h = default;
            if (r.ReadInt32() != SpillMagic || r.ReadInt32() != SpillVersion) return false;
            h = new SpillHeader(r.ReadInt64(), r.ReadInt64(), r.ReadInt32(), r.ReadInt32(), r.ReadSingle(), r.ReadSingle(), r.ReadInt32());
            return h.Length > 0 && h.Frames > 0 && h.SampleRate > 0;
        }

        /// <summary>Read a spill: false (and the file deleted) when it is past its max-age, malformed or short.</summary>
        static bool TryReadSpill(string folder, string key, out SpillHeader h, out byte[] bytes)
        {
            h = default;
            bytes = [];
            string path = SpillPath(folder, key);
            try
            {
                if (!File.Exists(path)) return false;
                bool good;
                using (var r = new BinaryReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024)))
                {
                    good = TryReadHeader(r, out h) && h.ExpiresUnix > DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    if (good)
                    {
                        bytes = r.ReadBytes(h.Length);
                        good = bytes.Length == h.Length;
                    }
                }
                if (!good) { File.Delete(path); bytes = []; return false; }
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);       // the LRU stamp
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException)
            {
                Log.Warn("dj", "tts.spill read failed: " + ex.GetType().Name + " " + ex.Message);
                return false;
            }
        }

        /// <summary>An Ensure for a key an earlier session spilled: adopt the file (Ready, on disk) when it is still fresh. Pool thread.</summary>
        static bool TryAdoptSpill(Clip clip)
        {
            if (!DiskAllowed) return false;
            string path = SpillPath(SpillFolder, clip.Key);
            try
            {
                if (!File.Exists(path)) return false;
                SpillHeader h;
                bool fresh;
                using (var r = new BinaryReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4 * 1024)))
                    fresh = TryReadHeader(r, out h) && h.ExpiresUnix > DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (!fresh) { File.Delete(path); return false; }
                clip.Adopt(in h);
                Interlocked.Increment(ref s_diskAdopts);
                Log.Info("dj", $"tts.disk key={clip.Key} bytes={h.Length} frames={h.Frames} expires={h.ExpiresUnix}");
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException)
            {
                Log.Warn("dj", "tts.disk read failed: " + ex.GetType().Name + " " + ex.Message);
                return false;
            }
        }

        /// <summary>The folder's housekeeping after a spill: a leftover `.part` and every file past its max-age go, then the least
        /// recently used until the folder is under <see cref="DiskBudgetBytes"/>. Pool thread (the spill pass).</summary>
        static void TrimDisk()
        {
            try
            {
                var dir = new DirectoryInfo(SpillFolder);
                if (!dir.Exists) return;
                foreach (FileInfo part in dir.GetFiles("*.part"))
                    if (DateTime.UtcNow - part.LastWriteTimeUtc > TimeSpan.FromMinutes(1)) part.Delete();
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), total = 0;
                var kept = new List<FileInfo>();
                foreach (FileInfo f in dir.GetFiles("*" + SpillExtension))
                {
                    bool fresh;
                    using (var r = new BinaryReader(f.OpenRead())) fresh = TryReadHeader(r, out SpillHeader h) && h.ExpiresUnix > now;
                    if (!fresh) { f.Delete(); continue; }
                    kept.Add(f);
                    total += f.Length;
                }
                if (total <= DiskBudgetBytes) return;
                kept.Sort(static (a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
                foreach (FileInfo f in kept)
                {
                    if (total <= DiskBudgetBytes) break;
                    long n = f.Length;
                    f.Delete();
                    total -= n;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException)
            {
                Log.Warn("dj", "tts.trim failed: " + ex.GetType().Name + " " + ex.Message);
            }
        }
    }
}
