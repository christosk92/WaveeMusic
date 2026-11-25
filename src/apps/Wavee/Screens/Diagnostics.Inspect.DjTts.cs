// ── Screens/Diagnostics.Inspect.DjTts.cs ─────────────────────────────────────────────────────────────────────────────────
// the `djtts` verb: the AI DJ's text-to-speech client (Spotify.DjTts) driven and read back, no mount needed
//
// Role: SHELL (diagnostics only; under the inspector's --fake every request goes to Spotify.DjTts.Fake's in-process transports)
// Spec: docs/plans/wavee/dj-implementation.md §2.3, §2.10
//
//   djtts fulfil fixture=welcome            the request body (hex and decoded fields), what the transport received, the 303 and its
//                                           Location, and that nothing followed it (no CDN request during the fulfil)
//   djtts fulfil fixture=welcome via=manifest   the body taken VERBATIM from a media.manifest (built here with the fields reordered)
//   djtts ensure fixture=intro wait=8000    Ensure, then wait until the clip settles: its state, bytes, duration, the cache's counters
//   djtts ensure seconds=200 n=9 wait=30000 nine distinct 200 s clips (~8 MB each): fills the 64 MB cap, so the oldest spill
//   djtts cache                             every clip (least recently used first), the counters, the spill folder
//   djtts play fixture=intro                open the clip through SourceRoute.Tts and decode it with the pump's MP3 decoder, paced at
//                                           1x: the format, the gain the decoder folded against the formula, the exact length, the
//                                           position series, and when the first audio came against when the download ended
//   djtts gain                              the narration's gain under every normalization mode, from the one factor songs use
//   djtts reset disk=true                   forget every clip and counter (and the spill folder)

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Wavee;

public static partial class Diagnostics
{
    public sealed partial class InspectSession
    {
        /// <summary>`djtts {do, fixture, ssml, manifest, via, key, seconds, n, wait, pace, rate, ms, every, disk}`.</summary>
        string DjTtsVerb(int id, string name, Inspect.Args a)
        {
            string verb = a.Str("do", "cache").ToLowerInvariant();
            return verb switch
            {
                "fulfil" or "fulfill" => DjTtsFulfil(id, name, a),
                "ensure" => DjTtsEnsure(id, name, a),
                "cache" => Inspect.Reply.Ok(id, name, static w => WriteDjTtsCache(w)),
                "play" => DjTtsPlay(id, name, a),
                "gain" => Inspect.Reply.Ok(id, name, static w => WriteDjTtsGain(w)),
                "reset" => DjTtsReset(id, name, a),
                _ => Inspect.Reply.Fail(id, name, "djtts wants do=fulfil|ensure|cache|play|gain|reset, not '" + verb + "'"),
            };
        }

        /// <summary>The SSML the args name: a fixture's, or `ssml=` as given. "" when neither.</summary>
        static string DjTtsSsml(Inspect.Args a, out string fixture)
        {
            fixture = a.Str("fixture");
            if (fixture.Length > 0) return Spotify.DjTts.Fake.FixtureNamed(fixture)?.Ssml ?? "";
            return a.Str("ssml");
        }

        // ── fulfil ───────────────────────────────────────────────────────────────────────────────────────────────────────

        string DjTtsFulfil(int id, string name, Inspect.Args a)
        {
            string ssml = DjTtsSsml(a, out string fixture);
            string via = a.Str("via", "keys").ToLowerInvariant();
            byte[] keysBody = ssml.Length > 0 ? Spotify.DjTts.FulfilBody(new Spotify.DjTts.Narration(ssml)) : [];
            byte[] manifest = [];
            if (a.Str("manifest") is { Length: > 0 } b64)
            {
                try { manifest = Convert.FromBase64String(b64); }
                catch (FormatException) { return Inspect.Reply.Fail(id, name, "manifest= is not base64"); }
                via = "manifest";
            }
            else if (via == "manifest")
            {
                if (keysBody.Length == 0) return Inspect.Reply.Fail(id, name, "djtts fulfil via=manifest wants fixture= or ssml=");
                manifest = Spotify.DjTts.EncodeManifest(ReorderedBody(ssml), -16f, -3f);
            }
            byte[] sent;
            if (via == "manifest")
            {
                if (!Spotify.DjTts.TryReadManifest(manifest, out Spotify.DjTts.Manifest m))
                    return Inspect.Reply.Fail(id, name, "the manifest carries no request body (field 1.1.4)");
                sent = m.Body;
            }
            else
            {
                if (keysBody.Length == 0) return Inspect.Reply.Fail(id, name, "djtts fulfil wants fixture=welcome|jump|intro|flaky|broken or ssml=");
                sent = keysBody;
            }

            long fulfils0 = Spotify.DjTts.Fake.FulfilRequests, cdn0 = Spotify.DjTts.Fake.CdnRequests;
            long t0 = Stopwatch.GetTimestamp();
            Spotify.Api.TtsAnswer answer = Spotify.DjTts.FulfilNow(sent, CancellationToken.None);
            double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            long fulfils = Spotify.DjTts.Fake.FulfilRequests - fulfils0, cdn = Spotify.DjTts.Fake.CdnRequests - cdn0;
            Spotify.DjTts.Fake.SeenRequest seen = Spotify.DjTts.Fake.LastFulfil;

            return Inspect.Reply.Ok(id, name, w =>
            {
                w.WriteString("fixture", fixture);
                w.WriteString("source", via == "manifest" ? "manifest (field 1.1.4, verbatim)" : "narration keys");
                w.WriteString("key", Spotify.DjTts.KeyOf(sent));
                w.WriteNumber("bodyBytes", sent.Length);
                w.WriteString("body", Hex(sent));
                WriteFields(w, "fields", sent);
                if (via == "manifest")
                {
                    w.WriteString("manifest", Convert.ToBase64String(manifest));
                    w.WriteBoolean("sentIsManifestBody", Spotify.DjTts.TryReadManifest(manifest, out var again) && again.Body.AsSpan().SequenceEqual(sent));
                    if (keysBody.Length > 0)
                    {
                        w.WriteString("keysBody", Hex(keysBody));
                        w.WriteBoolean("sentEqualsKeysBody", keysBody.AsSpan().SequenceEqual(sent));
                    }
                }
                else
                {
                    // `12 <len> <ssml>` then the fixed tail the plan names: 18 05 28 01 30 06 38 c4 d8 02.
                    byte[] ssmlBytes = Encoding.UTF8.GetBytes(ssml);
                    int tag = 1 + VarintSize(ssmlBytes.Length), head = tag + ssmlBytes.Length;
                    w.WriteString("head", Hex(sent.AsSpan(0, tag)) + " <ssml " + ssmlBytes.Length + " B>");
                    w.WriteString("tail", Hex(sent.AsSpan(head)));
                    w.WriteBoolean("tailIsSpec", sent.AsSpan(head).SequenceEqual(SpecTail));
                    w.WriteBoolean("ssmlVerbatim", sent.AsSpan(tag, ssmlBytes.Length).SequenceEqual(ssmlBytes));
                }
                w.WriteStartObject("request");
                w.WriteString("method", seen.Method);
                w.WriteString("url", seen.Url);
                w.WriteStartArray("headers");
                foreach (string h in seen.Headers ?? []) w.WriteStringValue(h);
                w.WriteEndArray();
                w.WriteBoolean("receivedEqualsSent", seen.Body is { } got && got.AsSpan().SequenceEqual(sent));
                w.WriteEndObject();
                w.WriteNumber("status", answer.Status);
                w.WriteString("location", answer.Location ?? "");
                w.WriteBoolean("redirected", answer.Redirected);
                w.WriteBoolean("clientFollowsRedirects", Spotify.Api.TtsFollowsRedirects);
                w.WriteNumber("fulfilRequests", fulfils);
                w.WriteNumber("cdnRequestsDuringFulfil", cdn);
                w.WriteBoolean("followed", cdn != 0);
                w.WriteNumber("ms", Math.Round(ms, 1));
            });
        }

        static ReadOnlySpan<byte> SpecTail => [0x18, 0x05, 0x28, 0x01, 0x30, 0x06, 0x38, 0xc4, 0xd8, 0x02];

        /// <summary>The same message as the keys build, in REVERSE field order (7, 6, 5, 3, 2): a valid body with different bytes, so a
        /// fulfil that sends the manifest's bytes is told apart from one that rebuilds them.</summary>
        static byte[] ReorderedBody(string ssml)
        {
            byte[] text = Encoding.UTF8.GetBytes(ssml);
            byte[] buffer = new byte[text.Length + 40];
            var p = new Spotify.Decode.ProtoWriter(buffer);
            p.U(7, 44_100);
            p.U(6, (ulong)Spotify.DjTts.CodecMp3);
            p.U(5, 1);
            p.U(3, 5);
            p.Utf8Always(2, text);
            return buffer.AsSpan(0, p.Length).ToArray();
        }

        static int VarintSize(int value)
        {
            int n = 1;
            while (value >= 0x80) { value >>= 7; n++; }
            return n;
        }

        static string Hex(ReadOnlySpan<byte> bytes)
        {
            var sb = new StringBuilder(bytes.Length * 3);
            for (int i = 0; i < bytes.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        /// <summary>A fulfil body's fields as `field = value` lines (the protobuf read back, not the bytes trusted).</summary>
        static void WriteFields(Utf8JsonWriter w, string name, byte[] body)
        {
            w.WriteStartArray(name);
            var r = new Spotify.Decode.ProtoReader(body);
            while (r.Next())
            {
                int field = r.Field;
                if (r.Wire == 0) w.WriteStringValue(field + " = " + r.Varint().ToString(CultureInfo.InvariantCulture));
                else if (r.Wire == 2) w.WriteStringValue(field + " = \"" + Encoding.UTF8.GetString(r.Bytes()) + "\"");
                else { w.WriteStringValue(field + " (wire " + r.Wire + ")"); r.Skip(); }
            }
            w.WriteEndArray();
        }

        // ── ensure, cache, reset ─────────────────────────────────────────────────────────────────────────────────────────

        string DjTtsEnsure(int id, string name, Inspect.Args a)
        {
            var keys = new List<string>();
            var hits = new List<bool>();
            int count = Math.Clamp(a.Int("n", 1), 1, 200);
            float seconds = a.Float("seconds", 0f);
            string ssml = DjTtsSsml(a, out string fixture);
            if (a.Str("manifest") is { Length: > 0 } b64)
            {
                byte[] manifest;
                try { manifest = Convert.FromBase64String(b64); }
                catch (FormatException) { return Inspect.Reply.Fail(id, name, "manifest= is not base64"); }
                Spotify.DjTts.TryReadManifest(manifest, out Spotify.DjTts.Manifest m);
                hits.Add(m.Body is { Length: > 0 } && Spotify.DjTts.TryGet(Spotify.DjTts.KeyOf(m.Body), out _));
                string key = Spotify.DjTts.Ensure(manifest);
                if (key.Length == 0) return Inspect.Reply.Fail(id, name, "the manifest carries no request body (field 1.1.4)");
                keys.Add(key);
            }
            else if (seconds > 0f)
            {
                // Distinct clips of a given length: the cap's proof. The run number keeps them distinct across calls.
                int run = Interlocked.Increment(ref s_djTtsRun);
                for (int i = 0; i < count; i++)
                {
                    string text = string.Create(CultureInfo.InvariantCulture,
                        $"<speak xml:lang=\"en-US\">Filler {run}-{i}.<break time=\"{seconds:0.###}s\"/></speak>");
                    var n = new Spotify.DjTts.Narration(text);
                    hits.Add(Spotify.DjTts.TryGet(Spotify.DjTts.KeyOf(Spotify.DjTts.FulfilBody(n)), out _));
                    keys.Add(Spotify.DjTts.Ensure(n));
                }
            }
            else if (ssml.Length > 0)
            {
                var n = new Spotify.DjTts.Narration(ssml);
                hits.Add(Spotify.DjTts.TryGet(Spotify.DjTts.KeyOf(Spotify.DjTts.FulfilBody(n)), out _));
                keys.Add(Spotify.DjTts.Ensure(n));
            }
            else return Inspect.Reply.Fail(id, name, "djtts ensure wants fixture=, ssml=, manifest= or seconds= (with n=)");

            int wait = Math.Clamp(a.Int("wait", 0), 0, 600_000);
            long t0 = Stopwatch.GetTimestamp();
            while (wait > 0 && Stopwatch.GetElapsedTime(t0).TotalMilliseconds < wait)
            {
                bool moving = Spotify.DjTts.Busy;
                if (!moving)
                    foreach (string k in keys)
                        if (Spotify.DjTts.TryGet(k, out var c) && c.InFlight) { moving = true; break; }
                if (!moving) break;
                Thread.Sleep(20);
            }
            double waited = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            return Inspect.Reply.Ok(id, name, w =>
            {
                w.WriteString("fixture", fixture);
                w.WriteNumber("waitedMs", Math.Round(waited));
                w.WriteStartArray("clips");
                for (int i = 0; i < keys.Count; i++)
                {
                    w.WriteStartObject();
                    w.WriteBoolean("wasCached", hits[i]);
                    if (Spotify.DjTts.TryGet(keys[i], out var clip)) WriteClip(w, in clip);
                    else { w.WriteString("key", keys[i]); w.WriteString("state", "evicted"); }
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                WriteDjTtsStats(w);
            });
        }

        static int s_djTtsRun;

        static void WriteDjTtsCache(Utf8JsonWriter w)
        {
            WriteDjTtsStats(w);
            w.WriteStartArray("clips");
            foreach (Spotify.DjTts.ClipInfo clip in Spotify.DjTts.Snapshot())
            {
                w.WriteStartObject();
                WriteClip(w, in clip);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }

        static void WriteDjTtsStats(Utf8JsonWriter w)
        {
            Spotify.DjTts.Stats s = Spotify.DjTts.ReadStats();
            w.WriteStartObject("cache");
            w.WriteNumber("clips", s.Clips);
            w.WriteNumber("inFlight", s.InFlight);
            w.WriteNumber("memoryBytes", s.MemoryBytes);
            w.WriteNumber("memoryCapBytes", Spotify.DjTts.MemoryCapBytes);
            w.WriteNumber("hits", s.Hits);
            w.WriteNumber("misses", s.Misses);
            w.WriteNumber("fulfils", s.Fulfils);
            w.WriteNumber("retries", s.Retries);
            w.WriteNumber("dropped", s.Dropped);
            w.WriteNumber("spills", s.Spills);
            w.WriteNumber("reloads", s.Reloads);
            w.WriteNumber("evictions", s.Evictions);
            w.WriteNumber("diskAdopts", s.DiskAdopts);
            w.WriteBoolean("spilling", s.Spilling);
            w.WriteBoolean("diskAllowed", s.DiskAllowed);
            w.WriteString("folder", s.Folder);
            w.WriteNumber("diskFiles", s.DiskFiles);
            w.WriteNumber("diskBytes", s.DiskBytes);
            w.WriteNumber("diskBudgetBytes", Spotify.DjTts.DiskBudgetBytes);
            w.WriteEndObject();
            w.WriteStartObject("fakeWire");
            w.WriteNumber("fulfilRequests", Spotify.DjTts.Fake.FulfilRequests);
            w.WriteNumber("cdnRequests", Spotify.DjTts.Fake.CdnRequests);
            w.WriteNumber("cdnRequestsWithCredentials", Spotify.DjTts.Fake.CdnCredentials);
            w.WriteEndObject();
        }

        static void WriteClip(Utf8JsonWriter w, in Spotify.DjTts.ClipInfo c)
        {
            w.WriteString("key", c.Key);
            w.WriteString("state", c.State.ToString());
            if (c.Why.Length > 0) w.WriteString("why", c.Why);
            w.WriteString("source", c.Source);
            w.WriteNumber("attempts", c.Attempts);
            w.WriteNumber("bytes", c.Bytes);
            w.WriteNumber("contentLength", c.ExpectedBytes);
            w.WriteNumber("frames", c.Frames);
            w.WriteNumber("rate", c.SampleRate);
            w.WriteNumber("durationMs", c.DurationMs);
            w.WriteBoolean("inMemory", c.InMemory);
            w.WriteBoolean("onDisk", c.OnDisk);
            w.WriteNumber("readers", c.Readers);
            w.WriteString("cdnCache", c.CdnCache);
            w.WriteString("host", c.Host);
            w.WriteNumber("firstByteMs", c.FirstByteMs);
            w.WriteNumber("doneMs", c.DoneMs);
            w.WriteNumber("expiresUnix", c.ExpiresUnix);
        }

        string DjTtsReset(int id, string name, Inspect.Args a)
        {
            bool disk = a.Bool("disk", false);
            Spotify.DjTts.Reset(disk);
            Spotify.DjTts.Fake.ResetCounts();
            return Inspect.Reply.Ok(id, name, w => { w.WriteBoolean("disk", disk); WriteDjTtsStats(w); });
        }

        // ── gain ─────────────────────────────────────────────────────────────────────────────────────────────────────────

        static void WriteDjTtsGain(Utf8JsonWriter w)
        {
            const float loudness = Spotify.DjTts.DefaultLoudnessLufs, truePeak = Spotify.DjTts.DefaultTruePeakDb;
            float gainDb = Spotify.DjTts.GainDbOf(loudness), peak = Spotify.DjTts.PeakLinearOf(truePeak);
            w.WriteNumber("loudnessLufs", loudness);
            w.WriteNumber("truePeakDb", truePeak);
            w.WriteNumber("figureDb", gainDb);
            w.WriteNumber("peakLinear", Math.Round(peak, 5));
            w.WriteStartArray("modes");
            WriteMode(w, "off", false, Playback.Audio.NormalizationMode.Normal, gainDb, peak, loudness, truePeak);
            foreach (Playback.Audio.NormalizationMode mode in (Playback.Audio.NormalizationMode[])[Playback.Audio.NormalizationMode.Quiet,
                         Playback.Audio.NormalizationMode.Normal, Playback.Audio.NormalizationMode.Loud])
                WriteMode(w, mode.ToString().ToLowerInvariant(), true, mode, gainDb, peak, loudness, truePeak);
            w.WriteEndArray();
            WriteSettingNow(w);
        }

        static void WriteMode(Utf8JsonWriter w, string label, bool enabled, Playback.Audio.NormalizationMode mode, float gainDb, float peak,
            float loudness, float truePeak)
        {
            float target = -14f + Playback.Audio.PregainDb(mode);
            float raw = enabled ? target - loudness : 0f;
            float factor = Playback.Audio.NormalizationFactor(enabled, mode, false, gainDb, peak);
            double appliedDb = 20 * Math.Log10(factor);
            w.WriteStartObject();
            w.WriteString("mode", label);
            if (enabled) w.WriteNumber("targetLufs", target);
            w.WriteNumber("rawGainDb", raw);
            w.WriteNumber("ceilingDbfs", enabled ? Math.Round(20 * Math.Log10(mode == Playback.Audio.NormalizationMode.Loud ? 1.0 : Playback.Audio.LimiterCeilingLinear), 2) : 0);
            w.WriteBoolean("capped", appliedDb < raw - 0.005);
            w.WriteNumber("appliedLinear", Math.Round(factor, 5));
            w.WriteNumber("appliedDb", Math.Round(appliedDb, 2));
            w.WriteNumber("truePeakAfterDb", Math.Round(truePeak + appliedDb, 2));
            w.WriteEndObject();
        }

        static void WriteSettingNow(Utf8JsonWriter w)
        {
            w.WriteStartObject("setting");
            w.WriteBoolean("enabled", Platform.Settings.Get(Platform.Keys.NormalizationEnabled));
            w.WriteString("mode", Playback.Audio.ModeOf(Platform.Settings.Get(Platform.Keys.NormalizationMode)).ToString());
            w.WriteBoolean("album", Platform.Settings.Get(Platform.Keys.NormalizationAlbum));
            w.WriteEndObject();
        }

        // ── play ─────────────────────────────────────────────────────────────────────────────────────────────────────────

        string DjTtsPlay(int id, string name, Inspect.Args a)
        {
            string key = a.Str("key");
            string ssml = DjTtsSsml(a, out string fixture);
            if (key.Length == 0)
            {
                if (ssml.Length == 0) return Inspect.Reply.Fail(id, name, "djtts play wants key= or fixture=/ssml= (which it ensures first)");
                key = Spotify.DjTts.Ensure(new Spotify.DjTts.Narration(ssml));
            }
            if (!Spotify.DjTts.TryGet(key, out _)) return Inspect.Reply.Fail(id, name, "no clip " + key + " in the cache (djtts ensure first)");
            int rate = Math.Clamp(a.Int("rate", 44_100), 8_000, 768_000);
            bool pace = a.Bool("pace", true);
            long limitMs = Math.Max(0, a.Int("ms", 0));
            int every = Math.Clamp(a.Int("every", 1000), 50, 60_000);
            Spotify.DjTts.Fake.Fixture? fx = fixture.Length > 0 ? Spotify.DjTts.Fake.FixtureNamed(fixture) : null;

            long t0 = Stopwatch.GetTimestamp();
            double Now() => Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            EntityId clipId = Spotify.DjTts.IdOf(key);
            Playback.Audio.SourceRoute route = Playback.Audio.RouteOf(clipId);
            FluentGpu.Media.IMediaByteSource? src = Playback.Audio.Open(clipId, CancellationToken.None, out Playback.Audio.Opened opened, out Playback.Fault fault);
            if (src is not Playback.Audio.TtsClipSource tts)
            {
                src?.Close();
                return Inspect.Reply.Fail(id, name, "the route did not open the clip: route=" + route + " fault=" + fault);
            }
            double openMs = Now();
            bool openedComplete = tts.OpenedComplete;
            FluentGpu.Media.IAudioDecoder decoder = Playback.Audio.CreateDecoderFor(in opened);
            bool decoderOpen = decoder.TryOpen(tts, new FluentGpu.Media.MixFormat(rate, 2), out FluentGpu.Media.DecodedInfo info);
            double decoderOpenMs = Now();

            var series = new List<(double T, double Pos, long Landed, bool Ready)>();
            var buffer = new float[1024 * 2];
            long frames = 0;
            double firstFrameMs = -1, readyAtPosMs = -1, readyAtMs = -1;
            long landedAtFirst = 0;
            bool readyAtFirst = false;
            float peakSample = 0f;
            double nextSample = 0;
            if (decoderOpen)
            {
                while (limitMs == 0 || frames * 1000L / rate < limitMs)
                {
                    int n = decoder.Read(buffer);
                    if (n <= 0) break;
                    for (int i = 0; i < n * 2; i++) peakSample = Math.Max(peakSample, Math.Abs(buffer[i]));
                    if (frames == 0) { firstFrameMs = Now(); landedAtFirst = tts.Landed; readyAtFirst = tts.ClipReady; }
                    frames += n;
                    double pos = frames * 1000.0 / rate;
                    if (readyAtPosMs < 0 && tts.ClipReady) { readyAtPosMs = pos; readyAtMs = Now(); }
                    if (pos >= nextSample) { series.Add((Now(), pos, tts.Landed, tts.ClipReady)); nextSample += every; }
                    if (pace)
                    {
                        // 1x: the position never runs ahead of the wall clock measured from the first audio.
                        double ahead = pos - (Now() - firstFrameMs);
                        if (ahead > 20) Thread.Sleep((int)ahead);
                    }
                }
            }
            double endMs = Now();
            series.Add((endMs, frames * 1000.0 / rate, tts.Landed, tts.ClipReady));
            long exactFrames = decoder.Gapless.ExactFrames;
            bool tailKnown = decoder.Gapless.TailKnown;
            float applied = decoder is Playback.Audio.IGainFolding g ? g.AppliedGainLinear : float.NaN;
            tts.Close();
            Spotify.DjTts.TryGet(key, out Spotify.DjTts.ClipInfo after);

            bool enabled = Platform.Settings.Get(Platform.Keys.NormalizationEnabled);
            Playback.Audio.NormalizationMode mode = Playback.Audio.ModeOf(Platform.Settings.Get(Platform.Keys.NormalizationMode));
            float target = -14f + Playback.Audio.PregainDb(mode);
            float formula = Playback.Audio.NormalizationFactor(enabled, mode, false, opened.GainDb, opened.Peak);
            long expected = fx is not null ? fx.ExpectedFrames : after.Frames;
            long expectedAtRate = expected < 0 ? -1 : (long)Math.Round(expected * (double)rate / Spotify.DjTts.Fake.SampleRate);

            return Inspect.Reply.Ok(id, name, w =>
            {
                w.WriteString("key", key);
                w.WriteString("fixture", fixture);
                w.WriteString("uri", Spotify.DjTts.UriPrefix + key);
                w.WriteString("route", route.ToString());
                w.WriteString("format", opened.Format.ToString());
                w.WriteString("label", opened.Label);
                w.WriteString("decoder", decoder.GetType().Name);
                w.WriteBoolean("decoderOpened", decoderOpen);
                w.WriteNumber("sourceRate", info.SourceFormat.SampleRate);
                w.WriteNumber("sourceChannels", info.SourceFormat.Channels);
                w.WriteNumber("mixRate", rate);
                w.WriteBoolean("openedComplete", openedComplete);
                w.WriteBoolean("seekable", openedComplete);
                w.WriteNumber("openedDurationMs", opened.DurationMs);
                w.WriteBoolean("openedDurationExact", opened.DurationExact);

                w.WriteStartObject("length");
                w.WriteNumber("expectedFrames", expectedAtRate);
                w.WriteNumber("decodedFrames", frames);
                w.WriteNumber("decoderExactFrames", exactFrames);
                w.WriteBoolean("decoderTailKnown", tailKnown);
                w.WriteBoolean("decodedIsExpected", frames == expectedAtRate);
                w.WriteNumber("decodedMs", Math.Round(frames * 1000.0 / rate, 3));
                w.WriteNumber("clipFrames", after.Frames);
                w.WriteNumber("clipDurationMs", after.DurationMs);
                if (fx is not null) w.WriteNumber("fixtureMs", Math.Round(fx.ExpectedMs, 3));
                w.WriteEndObject();

                w.WriteStartObject("gain");
                w.WriteBoolean("normalization", enabled);
                w.WriteString("mode", mode.ToString());
                w.WriteNumber("targetLufs", enabled ? target : 0);
                w.WriteNumber("clipLoudnessLufs", after.LoudnessLufs);
                w.WriteNumber("clipTruePeakDb", after.TruePeakDb);
                w.WriteNumber("openedGainDb", opened.GainDb);
                w.WriteNumber("openedPeak", Math.Round(opened.Peak, 5));
                w.WriteNumber("rawGainDb", enabled ? target - after.LoudnessLufs : 0);
                w.WriteNumber("formulaLinear", Math.Round(formula, 5));
                w.WriteNumber("formulaDb", Math.Round(20 * Math.Log10(formula), 2));
                w.WriteNumber("decoderAppliedLinear", Math.Round(applied, 5));
                w.WriteNumber("decoderAppliedDb", Math.Round(20 * Math.Log10(applied), 2));
                w.WriteBoolean("matches", Math.Abs(applied - formula) < 1e-6f);
                w.WriteEndObject();

                w.WriteStartObject("timing");
                w.WriteBoolean("paced", pace);
                w.WriteNumber("openMs", Math.Round(openMs, 1));
                w.WriteNumber("decoderOpenMs", Math.Round(decoderOpenMs, 1));
                w.WriteNumber("firstAudioMs", Math.Round(firstFrameMs, 1));
                w.WriteNumber("bytesLandedAtFirstAudio", landedAtFirst);
                w.WriteBoolean("downloadDoneAtFirstAudio", readyAtFirst);
                w.WriteBoolean("startedBeforeDownloadDone", decoderOpen && frames > 0 && !readyAtFirst);
                w.WriteNumber("downloadDoneAtMs", Math.Round(readyAtMs, 1));
                w.WriteNumber("downloadDoneAtPositionMs", Math.Round(readyAtPosMs, 1));
                w.WriteNumber("endMs", Math.Round(endMs, 1));
                w.WriteNumber("clipFirstByteMs", after.FirstByteMs);
                w.WriteNumber("clipDoneMs", after.DoneMs);
                w.WriteNumber("peakSample", peakSample);
                w.WriteEndObject();

                w.WriteStartArray("series");
                foreach (var s in series)
                {
                    w.WriteStartObject();
                    w.WriteNumber("tMs", Math.Round(s.T));
                    w.WriteNumber("positionMs", Math.Round(s.Pos));
                    w.WriteNumber("landed", s.Landed);
                    w.WriteBoolean("done", s.Ready);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            });
        }
    }
}
