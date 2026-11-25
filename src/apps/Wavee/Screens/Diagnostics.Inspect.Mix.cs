// ── Screens/Diagnostics.Inspect.Mix.cs ────────────────────────────────────────────────────────────────────────────────
// the `mix` verb (#48 Mix, plan §3.12 "a Mix section"): what Wavee holds of one playlist's Mix — the list and XM flags,
// the lens etag, each row's wire flags, the stored recipes decoded, each track's beat grid, analysis id and cue points —
// or a raw recipe decoded (`mix recipe=<base64>`), to compare with the capture's recipe decoder on the same bytes;
// `mix roundtrip` proves the recipe codec lossless (plan §2.1.1): decode → encode is byte-equal, a change moves one field.
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
            string uri = a.Str("uri", Entities.LibrarySeed.MixUri);
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
                    if (!t.Mix) w.WriteString("whyNot", t.WhyNot.ToString());
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
