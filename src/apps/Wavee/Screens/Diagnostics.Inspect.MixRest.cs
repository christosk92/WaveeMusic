// ── Screens/Diagnostics.Inspect.MixRest.cs ────────────────────────────────────────────────────────────────────────────
// The `mix` verb's M8 half (#48 Mix, Spotify.Api.MixLists.cs), through the REAL paths with --fake's recording server in
// Library.Net's place, printing every request as it left (Diagnostics.Inspect.MixWrites.cs's WriteRequests):
//
//   `mix show=reorder [apply=false] [undo=true]`   Smart reorder: the harmonize read, the order Spotify proposes against the list's,
//                                                  then (unless apply=false) the write and the list after it; undo=true writes the
//                                                  previous order back as the toast's Undo does.
//   `mix show=duplicate [override=true]`           Duplicate Mix on the fake mixed playlist read as someone else's (the menu's case):
//                                                  the duplicate call (override=true: the first stored pair's recipe as an unsaved
//                                                  edit, the editor's case), the copy read, filed in the rootlist, its Mix on, and
//                                                  the route it opened.
//
// Role: SHELL (UI thread; drives frames while a reply crosses the api thread)

using System.Globalization;
using System.Text.Json;

namespace Wavee;

public static partial class Diagnostics
{
    public sealed partial class InspectSession
    {
        /// <summary>`mix show=reorder|duplicate`.</summary>
        string MixRestVerb(int id, string name, string show, Inspect.Args a)
        {
            if (!Platform.Args.Fake) return Inspect.Reply.Fail(id, name, "show=" + show + " needs --fake (the recording server)");
            if (_shell is null) return Inspect.Reply.Fail(id, name, "mount first: the replies land on the UI thread's frames");
            string uri = a.Str("uri", Entities.LibrarySeed.MixUri);
            var eid = EntityId.Parse(uri.AsSpan());
            if (eid.Kind != EntityKind.Playlist || !Entities.Current.Playlists.TryGetSlot(eid, out int slot))
                return Inspect.Reply.Fail(id, name, "no playlist row for '" + uri + "'");
            var p = new Playlist(slot);
            return show == "reorder" ? MixReorder(id, name, p, a) : MixDuplicate(id, name, p, a);
        }

        string MixReorder(int id, string name, Playlist p, Inspect.Args a)
        {
            Entities.FakeMixServer.Clear();
            long mark = LogMark();
            string before = Titles(p);
            var steps = new List<(string Step, string Outcome, string After)>();
            ulong[]? proposal = null;
            var outcome = Spotify.MixLists.Outcome.Refused;
            bool done = false;
            Spotify.MixLists.ReadSmartOrder(p, (o, order) => { outcome = o; proposal = order; done = true; });
            PumpUntil(() => done, "harmonize");
            steps.Add(("harmonize read", outcome.ToString(), proposal is null ? "" : string.Join(", ", proposal.Select(Hex))));
            if (proposal is not null && a.Bool("apply", true))
            {
                var previous = Spotify.MixLists.OrderOf(p);
                Write("reorder", proposal);
                if (a.Bool("undo", false)) Write("undo", previous);
            }
            var recorded = Entities.FakeMixServer.Log();
            var logged = MixLogSince(mark);
            string after = Titles(p);
            return Inspect.Reply.Ok(id, name, w =>
            {
                w.WriteString("before", before);
                w.WriteStartArray("steps");
                foreach (var (step, o, text) in steps)
                {
                    w.WriteStartObject();
                    w.WriteString("step", step);
                    w.WriteString("outcome", o);
                    w.WriteString("after", text);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteString("after", after);
                w.WriteStartArray("log");
                foreach (var l in logged) w.WriteStringValue(l);
                w.WriteEndArray();
                WriteRequests(w, recorded);
                var state = Entities.FakeMixServer.StateOf(new string(EntityUri.IdOf(p.Uri.Text.AsSpan())));
                w.WriteString("serverEtag", state.Etag);
                w.WriteNumber("serverRecipes", state.Recipes);
                w.WriteNumber("storedTransitionsHere", MixData.Count(p.Slot));
            });

            void Write(string label, IReadOnlyList<ulong> order)
            {
                bool settled = false;
                var o = Spotify.MixLists.Outcome.Refused;
                int moved = 0;
                Spotify.MixLists.Reorder(p, order, (r, m) => { o = r; moved = m; settled = true; });
                PumpUntil(() => settled, label);
                steps.Add((label, o.ToString(), "moved=" + moved + " now: " + Titles(p)));
            }
        }

        string MixDuplicate(int id, string name, Playlist p, Inspect.Args a)
        {
            var table = Entities.Current.Playlists;
            byte caps = table.Caps[p.Slot];
            // The menu offers Duplicate Mix on a mixed list the user may not edit: the fake list is read as someone else's for the call.
            table.Caps[p.Slot] = (byte)PlaylistCaps.CanView;
            table.Bump(p.Slot);
            RunFrames(1);
            bool offered = Spotify.MixLists.CanDuplicate(p);
            List<Spotify.MixLists.Override>? overrides = null;
            if (a.Bool("override", false))
                foreach (var t in MixData.All(p.Slot)) { overrides = [new Spotify.MixLists.Override(t.Recipe.FromItemId, t.Base64)]; break; }
            Entities.FakeMixServer.Clear();
            long mark = LogMark();
            var outcome = Spotify.MixLists.Outcome.Refused;
            bool done = false;
            Spotify.MixLists.Duplicate(p, overrides, o => { outcome = o; done = true; });
            PumpUntil(() => done, "duplicate");
            RunFrames(4);
            table.Caps[p.Slot] = caps;
            table.Bump(p.Slot);
            var recorded = Entities.FakeMixServer.Log();
            var logged = MixLogSince(mark);
            // The copy: the newest plmixcopy row, filed where, mixed or not, and what this PC holds of it.
            string copyUri = "", filed = "", copyState = "";
            for (int i = 1; i < 64; i++)
            {
                var cid = EntityId.Parse(("spotify:playlist:plmixcopy" + i.ToString(CultureInfo.InvariantCulture)).AsSpan());
                if (!table.TryGetSlot(cid, out int cs)) break;
                var copy = new Playlist(cs);
                copyUri = copy.Uri.Text;
                copyState = "title=" + Entities.Strings.Resolve(copy.TitleId) + " owner=" + copy.IsOwner + " mixOn=" + copy.MixOn
                            + " rows=" + Entities.Current.Edges.PlaylistTracks.Targets(cs).Length + " storedTransitions=" + MixData.Count(cs)
                            + " flags=" + (PlaylistMixFlags)table.MixFlags[cs];
                var entries = new List<RootlistEntry>();
                Spotify.Encode.RootlistEntries(new User(Entities.Current.MeSlot), entries);
                for (int e = 0; e < entries.Count; e++)
                    if (entries[e].Uri == copyUri) { filed = "rootlist index " + e.ToString(CultureInfo.InvariantCulture) + " of " + entries.Count.ToString(CultureInfo.InvariantCulture); break; }
            }
            return Inspect.Reply.Ok(id, name, w =>
            {
                w.WriteBoolean("offered", offered);
                w.WriteString("outcome", outcome.ToString());
                w.WriteString("copy", copyUri);
                w.WriteString("copyState", copyState);
                w.WriteString("filed", filed);
                w.WriteString("route", RouteText());
                w.WriteStartArray("log");
                foreach (var l in logged) w.WriteStringValue(l);
                w.WriteEndArray();
                WriteRequests(w, recorded);
            });
        }

        static string Hex(ulong item) => item.ToString("x16", CultureInfo.InvariantCulture);

        /// <summary>The list's rows as "title (bpm)", in order.</summary>
        static string Titles(Playlist p)
        {
            var targets = Entities.Current.Edges.PlaylistTracks.Targets(p.Slot);
            var parts = new List<string>(targets.Length);
            foreach (int t in targets)
            {
                var track = new Track(t);
                parts.Add(track.IsValid ? track.Title + (track.Tempo > 0 ? " (" + Track.Format.Bpm(track.Tempo / 10.0) + ")" : "") : "?");
            }
            return string.Join(" | ", parts);
        }
    }
}
