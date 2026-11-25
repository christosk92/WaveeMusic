// ── Screens/Diagnostics.Inspect.Mix.cs ────────────────────────────────────────────────────────────────────────────────
// the `mix` verb (#48 Mix, plan §3.12 "a Mix section"): what Wavee holds of one playlist's Mix — the list and XM flags,
// the lens etag, each row's wire flags, the stored recipes decoded, each track's beat grid, analysis id and cue points —
// or a raw recipe decoded (`mix recipe=<base64>`), to compare with the capture's recipe decoder on the same bytes;
// `mix roundtrip` proves the recipe codec lossless (plan §2.1.1): decode → encode is byte-equal, a change moves one field;
// `mix semantics` prints the curve value map; `mix decode` runs a made-up lens read through the real decoder; `mix cancel`
// pulls a cancelled join's two voices through the engine's mixer offline (no device): the 30 ms ramps against the old cut.
//
// Role: SHELL (UI thread; reads the entity tables and MixData)

using System.Globalization;
using System.Text.Json;

namespace Wavee;

public static partial class Diagnostics
{
    public sealed partial class InspectSession
    {
        /// <summary>`mix {uri?, recipe?}`.</summary>
        string MixVerb(int id, string name, Inspect.Args a)
        {
            if (a.Has("curves"))
            {
                // The Connect metadata curves Wavee writes for a preset and window: compared with the capture's accepted strings.
                byte preset = (byte)a.Int("curves", 1);
                uint window = (uint)a.Int("window", 2890);
                int units = MixCurves.UnitsFor(window);
                Span<CurveSegment> seg = stackalloc CurveSegment[4];
                var entries = new List<(string Key, string Json)>();
                byte[] buf = new byte[2048];
                foreach (bool incoming in new[] { false, true })
                    foreach (var (lane, laneKey) in new[] { (MixLane.Volume, "curves"), (MixLane.LowEq, "eq_low_gain_curves"), (MixLane.FilterCutoff, "filter_cutoff_curves"), (MixLane.FilterResonance, "filter_resonance_curves") })
                    {
                        int n = MixCurves.Segments(preset, lane, incoming, units, seg);
                        if (n == 0) continue;
                        int len = MixCurves.WriteJson(seg[..n], buf);
                        entries.Add(("audio.fade_" + (incoming ? "in_" : "out_") + laneKey, System.Text.Encoding.UTF8.GetString(buf, 0, len)));
                    }
                return Inspect.Reply.Ok(id, name, w =>
                {
                    w.WriteNumber("units", units);
                    w.WriteStartArray("curves");
                    foreach (var (k, j) in entries) { w.WriteStartObject(); w.WriteString("key", k); w.WriteString("value", j); w.WriteEndObject(); }
                    w.WriteEndArray();
                });
            }
            if (a.Has("mixstate"))
            {
                // The `mix-state` (turn Mix on) body Wavee would POST for this revision (hex): compared byte-for-byte with the capture's #057.
                var hex = a.Str("mixstate");
                var rev = Convert.FromHexString(hex);
                var body = Spotify.Api.MixStateOnBody(rev);
                return Inspect.Reply.Ok(id, name, w => { w.WriteString("body", Convert.ToHexString(body).ToLowerInvariant()); w.WriteNumber("bytes", body.Length); });
            }
            string show = a.Str("show", "dump");
            if (show == "semantics") return MixSemantics(id, name);
            if (show == "cancel") return MixCancel(id, name, Math.Clamp(a.Float("at", 0.4f), 0.05f, 0.95f));
            if (a.Has("recipe") && show != "roundtrip")
            {
                string b64 = a.Str("recipe");
                var utf8 = System.Text.Encoding.ASCII.GetBytes(b64);
                if (!MixRecipe.TryDecodeBase64(utf8, out var recipe, out var raw))
                    return Inspect.Reply.Fail(id, name, "not a decodable recipe (bad base64 or no timing)");
                // The encoder's check: re-encode the decoded value carrying the raw bytes; a faithful codec writes them back.
                var again = MixRecipe.EncodeToArray(in recipe, raw);
                bool same = again.AsSpan().SequenceEqual(raw);
                return Inspect.Reply.Ok(id, name, w =>
                {
                    WriteRecipe(w, "recipe", in recipe);
                    w.WriteNumber("bytes", raw.Length);
                    w.WriteBoolean("reencodesIdentical", same);
                    if (!same) w.WriteString("reencoded", Convert.ToBase64String(again));
                });
            }
            // A made-up lens read through the real decoder and commit (the inspector's world is --fake's), then dumped below.
            if (show == "decode") Entities.LibrarySeed.LandMixWire();
            string uri = a.Str("uri", show == "decode" ? Entities.LibrarySeed.MixWireUri : Entities.LibrarySeed.MixUri);
            var eid = EntityId.Parse(uri.AsSpan());
            if (eid.Kind != EntityKind.Playlist || !Entities.Current.Playlists.TryGetSlot(eid, out int slot))
                return Inspect.Reply.Fail(id, name, "no playlist row for '" + uri + "'");
            if (show == "roundtrip") return MixRoundTrip(id, name, slot, uri, a);
            var p = new Playlist(slot);
            return Inspect.Reply.Ok(id, name, w =>
            {
                w.WriteString("uri", uri);
                w.WriteString("flags", p.MixFlags.ToString());
                w.WriteBoolean("on", p.MixOn);
                w.WriteBoolean("owesLensRead", PlaylistMixRules.OwesLensRead(p.MixFlags));
                w.WriteString("lensEtag", Entities.Strings.Resolve(p.MixLensEtagId));
                string bare = Entities.MixPlaylistId(slot);
                w.WriteBoolean("readsWithLens", bare.Length > 0 && MixData.ReadsWithLens(bare));
                w.WriteBoolean("listenWithout", MixListenWithout.Is(bare));
                w.WriteNumber("storedTransitions", MixData.Count(slot));
                // The signal templates the lens read carried (plan §2.1.2): what a mix-state or set-transition write echoes.
                var signals = MixData.Signals(slot);
                w.WriteString("mixStateSignal", signals?.MixState is { } ms ? DescribeSignal(ms) : "");
                w.WriteNumber("signalTemplates", signals?.Count ?? 0);
                w.WriteNumber("signalTemplateBytes", signals?.Bytes ?? 0);
                var rows = Entities.Current.Edges.PlaylistTracks;
                var targets = rows.Targets(slot);
                var payload = rows.Payload(slot);
                w.WriteStartArray("rows");
                for (int i = 0; i < targets.Length; i++)
                {
                    var t = new Track(targets[i]);
                    ref readonly var e = ref payload[i];
                    string item = Entities.Strings.Resolve(e.ItemId);
                    w.WriteStartObject();
                    w.WriteNumber("i", i);
                    w.WriteString("title", t.IsValid ? t.Title : "");
                    w.WriteString("item", item);
                    w.WriteNumber("durationMs", t.IsValid ? t.DurationMs : 0);
                    w.WriteNumber("bpm", t.IsValid ? t.Tempo / 10.0 : 0);
                    w.WriteNumber("camelot", t.IsValid ? t.Camelot : 0);
                    w.WriteBoolean("mixable", (e.Flags & PlaylistItemFlags.Mixable) != 0);
                    w.WriteBoolean("notMixable", (e.Flags & PlaylistItemFlags.NotMixable) != 0);
                    ulong itemId = MixRecipe.Hex64(item);
                    if (signals?.NotMixableReason(itemId) is { } why) w.WriteString("notMixableReason", why);
                    if (signals is not null && signals.TransitionTemplate(itemId) is { IsEmpty: false } template)
                    {
                        w.WriteString("signal", DescribeSignal(template));
                        w.WriteString("signalHex", Convert.ToHexString(template).ToLowerInvariant());
                    }
                    var beats = Entities.Current.Edges.TrackBeats.Payload(targets[i]);
                    int downs = 0;
                    foreach (uint b in beats) if (BeatGrid.IsDownbeat(b)) downs++;
                    w.WriteNumber("beats", beats.Length);
                    w.WriteNumber("downbeats", downs);
                    w.WriteString("beatsId", MixData.BeatsId(targets[i]) ?? "");
                    var cues = Entities.Current.Edges.TrackCues.Payload(targets[i]);
                    if (cues.Length > 0) { w.WriteNumber("startCueMs", cues[0].StartCueMs); w.WriteNumber("endCueMs", cues[0].EndCueMs); }
                    if (MixData.TryGet(slot, MixRecipe.Hex64(item), out var tr)) { var rec = tr.Recipe; WriteRecipe(w, "recipe", in rec, tr.TransitionUri); }
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                // The planner's timing for every adjacent pair (plan §3.5's reference table: read beside the capture's §3.3).
                w.WriteStartArray("timings");
                for (int i = 0; i + 1 < targets.Length; i++)
                {
                    var t = Playback.Mix.PlanListPair(slot, i, i + 1);
                    w.WriteStartObject();
                    w.WriteNumber("from", i);
                    w.WriteString("source", t.Source.ToString());
                    if (!t.Mix)
                    {
                        w.WriteString("whyNot", t.WhyNot.ToString());
                        // The base hand-off's fade for this pair with the user's Crossfade as it is now (0 = gapless).
                        w.WriteNumber("baseFadeMs", Playback.Audio.BaseFadeMs(in t, Playback.Audio.CrossfadeMs));
                    }
                    else
                    {
                        w.WriteNumber("preset", t.Preset);
                        w.WriteNumber("outStartMs", t.OutStartMs);
                        w.WriteNumber("outDurMs", t.OutDurationMs);
                        w.WriteNumber("inStartMs", t.InStartMs);
                        w.WriteNumber("inDurMs", t.InDurationMs);
                        w.WriteNumber("audibleEndMs", t.AudibleEndMs);
                    }
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                var cur = Playback.Mix.Current;
                w.WriteString("playingPair", cur.Mix ? cur.Source + " out=" + cur.OutStartMs + " dur=" + cur.OutDurationMs + " in=" + cur.InStartMs : "none (" + cur.WhyNot + ")");
                // The user's Crossfade, and the fade the pump's tick selects for the deck's pair now (Audio.MixBaseFadeMs).
                w.WriteNumber("userFadeMs", Playback.Audio.CrossfadeMs);
                w.WriteNumber("pumpFadeMs", Playback.Audio.HandOffFadeMsNow);
            });
        }

        /// <summary>`mix roundtrip`: every stored recipe of the playlist (and `recipe=`), decoded and encoded again over its own
        /// bytes (must be byte-equal), then encoded with ONE field changed (`change=`), with the bytes that differ: only that field's
        /// value (and, when its size changes, the length prefixes around it) may move.</summary>
        string MixRoundTrip(int id, string name, int slot, string uri, Inspect.Args a)
        {
            var items = new List<(string Label, byte[] Raw)>();
            foreach (var t in MixData.All(slot)) items.Add((t.Recipe.FromItemId.ToString("x16", CultureInfo.InvariantCulture), t.Raw));
            if (a.Has("recipe"))
            {
                var utf8 = System.Text.Encoding.ASCII.GetBytes(a.Str("recipe"));
                if (!MixRecipe.TryDecodeBase64(utf8, out _, out var raw)) return Inspect.Reply.Fail(id, name, "not a decodable recipe (bad base64 or no timing)");
                items.Add(("recipe=", raw));
            }
            string change = a.Str("change", "overlap");
            int equal = 0;
            return Inspect.Reply.Ok(id, name, w =>
            {
                w.WriteString("uri", uri);
                w.WriteString("change", change);
                w.WriteStartArray("recipes");
                foreach (var (label, raw) in items)
                {
                    w.WriteStartObject();
                    w.WriteString("from", label);
                    w.WriteNumber("bytes", raw.Length);
                    if (!MixRecipe.TryDecode(raw, out var recipe)) { w.WriteString("error", "undecodable"); w.WriteEndObject(); continue; }
                    w.WriteString("decoded", recipe.ToString());
                    var again = MixRecipe.EncodeToArray(in recipe, raw);
                    bool same = again.AsSpan().SequenceEqual(raw);
                    if (same) equal++;
                    w.WriteBoolean("byteEqual", same);
                    if (!same) w.WriteString("reencoded", Convert.ToHexString(again).ToLowerInvariant());

                    var edited = recipe;
                    string what = change switch
                    {
                        "bars" => "durationBars " + edited.DurationBars + " -> " + ++edited.DurationBars,
                        "eq" => "eqStyle " + (edited.EqStyle?.ToString(CultureInfo.InvariantCulture) ?? "none") + " -> "
                                + (edited.EqStyle = edited.EqStyle == 15 ? 7 : 15),
                        _ => "overlapMs " + edited.OverlapMs + " -> " + ++edited.OverlapMs,
                    };
                    var changed = MixRecipe.EncodeToArray(in edited, raw);
                    w.WriteStartObject("changed");
                    w.WriteString("field", what);
                    w.WriteNumber("bytes", changed.Length);
                    w.WriteBoolean("byteEqual", changed.AsSpan().SequenceEqual(raw));
                    // The change landed and nothing else moved: the changed bytes decode to the edited model.
                    bool decodes = MixRecipe.TryDecode(changed, out var back);
                    w.WriteBoolean("decodesToEdit", decodes && back.ToString() == edited.ToString());
                    WriteDiff(w, raw, changed);
                    w.WriteEndObject();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteNumber("byteEqualCount", equal);
                w.WriteNumber("count", items.Count);
            });
        }

        /// <summary>`mix semantics`: the curve value → sound map the renderer uses (the official client's evaluator), at fixed raw
        /// values, and what it yields in the engine: the automation MixRender.Build makes for each preset and side (its coefficient
        /// steps per lane, at most VoiceAutomation.MaxSteps) and the EQ table's dB range.</summary>
        static string MixSemantics(int id, string name)
        {
            ReadOnlySpan<float> raws = [0f, 0.1f, 0.25f, 0.4f, 0.5f, 0.75f, 1f];
            var rows = new List<(float Raw, float Db, byte Mode, float Hz)>();
            foreach (float raw in raws)
            {
                var (mode, hz, _) = MixCurves.Sweep(raw, 0.5f);
                rows.Add((raw, MixCurves.EqGainDb(raw), mode, hz));
            }
            var lanes = new List<(string Preset, uint Window, string Side, int Shelf, int Sweep, float MinDb, float MaxDb, double BuildMs)>();
            const int rate = 48_000, points = FluentGpu.Media.GainEnvelope.TablePoints;
            Span<CurveSegment> seg = stackalloc CurveSegment[4];
            foreach (byte preset in new[] { (byte)MixPreset.Fade, (byte)MixPreset.Rise })
                foreach (uint window in new uint[] { 3_000, 12_000 })
                {
                    var t = new MixTiming(MixSource.Saved, MixWhyNot.None, preset, 0, window, 0, window);
                    int frames = (int)(window * (long)rate / 1000);
                    long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    Playback.MixRender.Build(in t, rate, 0, frames, frames, out _, out _, out var outAuto, out var inAuto);
                    double buildMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                    foreach (bool incoming in new[] { false, true })
                    {
                        float min = 0f, max = 0f;
                        int n = MixCurves.Segments(preset, MixLane.LowEq, incoming, MixCurves.UnitsFor(window), seg);
                        for (int i = 0; i < points && n > 0; i++)
                        {
                            float db = MixCurves.EqGainDb(MixCurves.Evaluate(seg[..n], i / (float)(points - 1)));
                            min = Math.Min(min, db);
                            max = Math.Max(max, db);
                        }
                        var auto = incoming ? inAuto : outAuto;
                        lanes.Add((((MixPreset)preset).ToString(), window, incoming ? "in" : "out", auto?.ShelfSteps ?? 0, auto?.SweepSteps ?? 0, min, max, buildMs));
                    }
                }
            return Inspect.Reply.Ok(id, name, w =>
            {
                w.WriteNumber("eqKillDb", MixCurves.EqKillDb);
                w.WriteStartArray("map");
                foreach (var (raw, db, mode, hz) in rows)
                {
                    w.WriteStartObject();
                    w.WriteNumber("raw", raw);
                    w.WriteNumber("eqDb", Math.Round(db, 2));
                    w.WriteString("cutoff", mode switch { 1 => "LP", 2 => "HP", _ => "bypass" });
                    if (mode != 0) w.WriteNumber("cutoffHz", Math.Round(hz, 1));
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteNumber("maxSteps", FluentGpu.Media.VoiceAutomation.MaxSteps);
                w.WriteStartArray("automation");
                foreach (var (preset, window, side, shelf, sweep, min, max, buildMs) in lanes)
                {
                    w.WriteStartObject();
                    w.WriteString("preset", preset);
                    w.WriteNumber("windowMs", window);
                    w.WriteString("side", side);
                    w.WriteNumber("shelfSteps", shelf);
                    w.WriteNumber("sweepSteps", sweep);
                    w.WriteNumber("eqMinDb", Math.Round(min, 2));
                    w.WriteNumber("eqMaxDb", Math.Round(max, 2));
                    w.WriteNumber("buildMs", Math.Round(buildMs, 2));   // MixRender.Build, both sides (the pump thread's cost per join)
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            });
        }

        /// <summary>`mix cancel`: a 3 s Fade join cancelled at <paramref name="at"/> of its overlap, each voice (a 1 kHz full-scale
        /// sine) pulled ALONE through the engine's own CrossfadeMixer in 10 ms blocks, no device. The join installs the envelopes
        /// ScheduleMix installs; at the cancel block the voice gets what the pump gives it now (MixRender.Cancel, the call
        /// CancelMixLocked makes, B's fade as AbandonPendingJoin installs it) or what it got before (A the constant envelope, B a
        /// one-frame cut). Prints the gain (the peak of each ms) from 4 ms before the cancel to 60 ms after, and when B retired.</summary>
        static string MixCancel(int id, string name, float at)
        {
            const int rate = 48_000, channels = 2, maxBlock = 1024, pull = 480;
            const uint windowMs = 3_000;
            int frames = (int)(windowMs * (long)rate / 1000);
            long start = rate / 10;                                              // the overlap starts 100 ms into the pull
            var plan = new MixTiming(MixSource.Saved, MixWhyNot.None, (byte)MixPreset.Fade, 0, windowMs, 0, windowMs);
            Playback.MixRender.Build(in plan, rate, start, frames, frames, out var outTable, out var inTable, out _, out _);
            long decide = start + (long)(at * frames);
            decide -= decide % pull;                                              // the cancel is decided between two blocks
            long from = decide - 4 * rate / 1000, to = decide + 60 * rate / 1000;

            var aNew = Pull(false, true, out _, out bool before);
            var bNew = Pull(true, true, out long bNewRetired, out _);
            var aOld = Pull(false, false, out _, out _);
            var bOld = Pull(true, false, out long bOldRetired, out _);
            return Inspect.Reply.Ok(id, name, w =>
            {
                w.WriteNumber("windowMs", windowMs);
                w.WriteNumber("cancelAtMs", Math.Round((decide - start) * 1000.0 / rate, 1));
                w.WriteBoolean("insideOverlap", !before);
                w.WriteNumber("leadFrames", maxBlock);
                w.WriteNumber("rampMs", Playback.MixRender.CancelRampMs);
                w.WriteNumber("bRetiredMs", bNewRetired < 0 ? -1 : Math.Round((bNewRetired - decide) * 1000.0 / rate, 1));
                w.WriteNumber("bRetiredMsBefore", bOldRetired < 0 ? -1 : Math.Round((bOldRetired - decide) * 1000.0 / rate, 1));
                w.WriteStartArray("gain");
                for (int ms = 0; ms < aNew.Length; ms += 2)
                {
                    w.WriteStartObject();
                    w.WriteNumber("ms", ms - 4);
                    w.WriteNumber("a", Math.Round(aNew[ms], 3));
                    w.WriteNumber("b", Math.Round(bNew[ms], 3));
                    w.WriteNumber("aBefore", Math.Round(aOld[ms], 3));
                    w.WriteNumber("bBefore", Math.Round(bOld[ms], 3));
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            });

            float[] Pull(bool incoming, bool ramp, out long retiredAt, out bool beforeStart)
            {
                var mixer = new FluentGpu.Media.CrossfadeMixer(channels, maxBlock);
                var gate = new FluentGpu.Media.AudioTransitionGate(start);
                var outEnv = FluentGpu.Media.GainEnvelope.FromCurve(FluentGpu.Media.FadeKind.Out, start, frames, outTable).WithTransition(gate);
                var inEnv = FluentGpu.Media.GainEnvelope.FromCurve(FluentGpu.Media.FadeKind.In, start, frames, inTable).WithTransition(gate);
                mixer.AddVoice(new FluentGpu.Media.MixVoice
                {
                    Id = 1, Src = new FluentGpu.Media.SignalGeneratorSource(channels, rate, 1000.0, 1f, to + rate),
                    Env = incoming ? inEnv : outEnv, StartFrame = incoming ? start : 0, ReplayGainScalar = 1f,
                });
                var plane = new FluentGpu.Media.ParamPlane();
                var buffer = new float[pull * channels];
                var peaks = new float[(int)((to - from) * 1000 / rate)];
                retiredAt = -1;
                beforeStart = true;
                while (mixer.ConsumeSeq < to)
                {
                    long block = mixer.ConsumeSeq;
                    if (block == decide)
                    {
                        // The cancel, as the pump applies it now (ramp) or applied it before.
                        beforeStart = Playback.MixRender.Cancel(gate, outEnv, inEnv, block, maxBlock, rate, out var outgoing, out var fade);
                        var envelope = !ramp ? (incoming ? FluentGpu.Media.GainEnvelope.Fade(FluentGpu.Media.FadeKind.Out, 0, 1, FluentGpu.Media.CrossCurve.Linear) : FluentGpu.Media.GainEnvelope.Constant)
                                     : incoming ? fade ?? FluentGpu.Media.GainEnvelope.Fade(FluentGpu.Media.FadeKind.Out, 0, 1, FluentGpu.Media.CrossCurve.Linear) : outgoing;
                        mixer.TrySetVoiceEnvelope(1, envelope);
                    }
                    int n = (int)Math.Min(pull, to - block);
                    mixer.Render(buffer, n, new FluentGpu.Media.BlockCtx(block, rate, channels, plane));
                    for (int f = 0; f < n; f++)
                    {
                        long frame = block + f;
                        if (frame < from) continue;
                        int ms = (int)((frame - from) * 1000 / rate);
                        if (ms < peaks.Length) peaks[ms] = Math.Max(peaks[ms], Math.Abs(buffer[f * channels]));
                    }
                    if (retiredAt < 0 && mixer.VoiceCount == 0) retiredAt = block + n;
                }
                return peaks;
            }
        }

        /// <summary>A signal template <c>{1 name, 2 {1 lens, 2 uri, 3 item id}}</c> as one line: <c>set-transition {mix, uri,
        /// 5eed00000c800000}</c>; the args' text fields as text, a binary one in hex.</summary>
        static string DescribeSignal(ReadOnlySpan<byte> signal)
        {
            var sb = new System.Text.StringBuilder();
            for (var r = new Spotify.Decode.ProtoReader(signal); r.Next();)
            {
                if (r.Field == 1 && r.Wire == 2) { sb.Append(System.Text.Encoding.UTF8.GetString(r.Bytes())); continue; }
                if (r.Field != 2 || r.Wire != 2) { r.Skip(); continue; }
                sb.Append(" {");
                bool firstArg = true;
                for (var args = r.Message(); args.Next();)
                {
                    if (!firstArg) sb.Append(", ");
                    firstArg = false;
                    if (args.Wire != 2) { sb.Append(args.Field).Append(":?"); args.Skip(); continue; }
                    var value = args.Bytes();
                    bool text = true;
                    foreach (byte b in value) if (b is < 0x20 or > 0x7E) { text = false; break; }
                    sb.Append(text ? System.Text.Encoding.ASCII.GetString(value) : Convert.ToHexString(value).ToLowerInvariant());
                }
                sb.Append('}');
            }
            return sb.ToString();
        }

        /// <summary>Where two byte runs differ: the shared head and tail lengths and the differing middle of each, in hex.</summary>
        static void WriteDiff(Utf8JsonWriter w, ReadOnlySpan<byte> before, ReadOnlySpan<byte> after)
        {
            int head = 0;
            while (head < before.Length && head < after.Length && before[head] == after[head]) head++;
            int tail = 0;
            while (tail < before.Length - head && tail < after.Length - head && before[^(tail + 1)] == after[^(tail + 1)]) tail++;
            w.WriteStartObject("diff");
            w.WriteNumber("at", head);
            w.WriteString("before", Convert.ToHexString(before[head..^tail]).ToLowerInvariant());
            w.WriteString("after", Convert.ToHexString(after[head..^tail]).ToLowerInvariant());
            w.WriteNumber("sameHead", head);
            w.WriteNumber("sameTail", tail);
            w.WriteEndObject();
        }

        static void WriteRecipe(Utf8JsonWriter w, string key, in MixRecipe r, string? transitionUri = null)
        {
            w.WriteStartObject(key);
            w.WriteString("from", r.FromItemId.ToString("x16", CultureInfo.InvariantCulture));
            w.WriteString("to", r.ToItemId.ToString("x16", CultureInfo.InvariantCulture));
            w.WriteNumber("outStartMs", r.OutStartMs);
            w.WriteNumber("inStartMs", r.InStartMs);
            w.WriteNumber("overlapMs", r.OverlapMs);
            w.WriteNumber("speedA", r.SpeedA);
            w.WriteNumber("speedB", r.SpeedB);
            w.WriteNumber("durationBars", r.DurationBars);
            w.WriteBoolean("isBeatmatched", r.IsBeatmatched);
            w.WriteNumber("bpmA", r.BpmA);
            w.WriteNumber("bpmB", r.BpmB);
            w.WriteNumber("itemSpeedA", r.ItemSpeedA);
            w.WriteNumber("itemSpeedB", r.ItemSpeedB);
            w.WriteNumber("preset", r.Preset);
            w.WriteString("presetType", r.PresetType.ToString());
            w.WriteBoolean("noTransition", r.IsNoTransition);
            w.WriteString("styles", r.Styles());
            w.WriteString("curveOverrides", "0x" + r.CurveOverrides.ToString("x", CultureInfo.InvariantCulture));
            w.WriteBoolean("isOverlapOverride", r.IsOverlapOverride);
            w.WriteBoolean("isPresetIdOverride", r.IsPresetIdOverride);
            w.WriteString("beatmatchPreference", r.BeatmatchPreference.ToString());
            w.WriteString("trackA", r.TrackA ?? "");
            w.WriteString("trackB", r.TrackB ?? "");
            w.WriteString("playableA", r.PlayableA ?? "");
            w.WriteString("playableB", r.PlayableB ?? "");
            w.WriteString("beatsIdA", r.BeatsIdA ?? "");
            w.WriteString("beatsIdB", r.BeatsIdB ?? "");
            if (transitionUri is not null) w.WriteString("transitionUri", transitionUri);
            w.WriteEndObject();
        }
    }
}
