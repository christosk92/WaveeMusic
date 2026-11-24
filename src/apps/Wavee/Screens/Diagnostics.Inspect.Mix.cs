// ── Screens/Diagnostics.Inspect.Mix.cs ────────────────────────────────────────────────────────────────────────────────
// the `mix` verb (#48 Mix, plan §3.12 "a Mix section"): what Wavee holds of one playlist's Mix — the list and XM flags,
// the lens etag, each row's wire flags, the stored recipes decoded, each track's beat grid, analysis id and cue points —
// or a raw recipe decoded (`mix recipe=<base64>`), to compare with the capture's recipe decoder on the same bytes.
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
            if (a.Has("recipe"))
            {
                string b64 = a.Str("recipe");
                var utf8 = System.Text.Encoding.ASCII.GetBytes(b64);
                if (!MixRecipe.TryDecodeBase64(utf8, out var recipe, out var raw))
                    return Inspect.Reply.Fail(id, name, "not a decodable recipe (bad base64 or no timing)");
                // The encoder's check: re-encode the decoded value carrying the raw bytes; a faithful codec writes them back.
                var again = new byte[raw.Length + 64];
                int n = MixRecipe.Encode(in recipe, raw, again);
                bool same = again.AsSpan(0, n).SequenceEqual(raw);
                return Inspect.Reply.Ok(id, name, w =>
                {
                    WriteRecipe(w, "recipe", in recipe);
                    w.WriteNumber("bytes", raw.Length);
                    w.WriteBoolean("reencodesIdentical", same);
                    if (!same) w.WriteString("reencoded", Convert.ToBase64String(again, 0, n));
                });
            }
            string uri = a.Str("uri", Entities.LibrarySeed.MixUri);
            var eid = EntityId.Parse(uri.AsSpan());
            if (eid.Kind != EntityKind.Playlist || !Entities.Current.Playlists.TryGetSlot(eid, out int slot))
                return Inspect.Reply.Fail(id, name, "no playlist row for '" + uri + "'");
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

        static void WriteRecipe(Utf8JsonWriter w, string key, in MixRecipe r, string? transitionUri = null)
        {
            w.WriteStartObject(key);
            w.WriteString("from", r.FromItemId.ToString("x16", CultureInfo.InvariantCulture));
            w.WriteString("to", r.ToItemId.ToString("x16", CultureInfo.InvariantCulture));
            w.WriteNumber("outStartMs", r.OutStartMs);
            w.WriteNumber("inStartMs", r.InStartMs);
            w.WriteNumber("overlapMs", r.OverlapMs);
            w.WriteNumber("rateA", r.RateA);
            w.WriteNumber("rateB", r.RateB);
            w.WriteBoolean("beatmatch", r.Beatmatch);
            w.WriteNumber("bpmA", r.BpmA);
            w.WriteNumber("bpmB", r.BpmB);
            w.WriteNumber("preset", r.Preset);
            w.WriteNumber("styleVersion", r.StyleVersion);
            w.WriteString("trackA", r.TrackA ?? "");
            w.WriteString("trackB", r.TrackB ?? "");
            w.WriteString("beatsIdA", r.BeatsIdA ?? "");
            w.WriteString("beatsIdB", r.BeatsIdB ?? "");
            if (transitionUri is not null) w.WriteString("transitionUri", transitionUri);
            w.WriteEndObject();
        }
    }
}
