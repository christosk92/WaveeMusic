// ── Entities/Entities.Fake.MixServer.cs — FAKE (#48 Mix M2: the writes under --fake) ────────────────────────────────────
//
// --fake has no account, so Mix's writes (Spotify.Mixes) go to this stand-in for Spotify's playlist service instead of the
// network: it is installed in Library.Net's place for the `/signals` POST and the head read, RECORDS every request exactly as
// the live send would build it (Spotify.Build: the verb, the path, the header set and the sync-reason; the body as sent), and
// answers like the server (protocol §3): the whole list read through the lenses that apply, with the server's own state
// moved by the signal:
//
//   mix-state {1:1} / none      the list is mixed / not; the recipes are KEPT either way (§3.1)
//   set-transition + payload    the from-item's recipe = payload f1 (stored verbatim), its transition uri re-stamped, the
//                               mix lens etag moves (§3.2, §3.4)
//   set-transition, no payload  the from-item's recipe and uri are dropped (Reset to Auto), the etag moves
//   mix-opt-out / -remove-      the opt-out bit (how a real server reads it back is open, §3.3: the reply shows nothing)
//
// A request at a revision the list is not at is refused with 409 (the live helper then reads the head and sends once more).
// The inspector can take the server offline (no session: nothing is sent) or make it fail (503): `mix fake=offline|fail|ok`.
// Every value here is made up: the revision is counter 7 over a `beef` hash, etags count up from 0000beef, transition uris
// carry the suffix 1700000000000 + 1000 per save.
//
// THREADS: Adopt and the inspector's reads run on the UI thread; the request handlers run on the api threads the live send
// uses. One lock over the whole state.

using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Wavee;

public static partial class Entities
{
    public static class FakeMixServer
    {
        public enum Mode : byte { Ok, Fail, Offline }

        /// <summary>One request as it left: what the live send would have put on the wire.</summary>
        public sealed record Request(string Verb, string Path, string? ContentType, string? Lenses, string SyncReason, bool ListPlayer,
                                     byte[] Body, int Status, int ReplyBytes);

        sealed class Row
        {
            public required string Uri;
            public ulong ItemId;
            public long AddedAt;
            public string? AddedBy;
            public bool NotMixable;
            public string? Reason;
            public byte[]? Template;
        }

        sealed class ListState
        {
            public required string Title, Description;
            public required byte[] Revision;
            public required List<Row> Rows;
            public bool Mixed, OptOut;
            /// <summary>A list the server holds no rows for (the migration's ids): it answers its head and takes the opt-outs.</summary>
            public bool Stub;
            public int EtagSerial;
            public readonly Dictionary<ulong, (string Base64, string Uri)> Recipes = new();
            public byte[] MixState = Spotify.Api.SignalTemplate("mix-state");
            public string Etag => (0x0000BEEFu + (uint)EtagSerial).ToString("x8", CultureInfo.InvariantCulture);
        }

        static readonly object s_gate = new();
        static readonly Dictionary<string, ListState> s_lists = new(StringComparer.Ordinal);
        static readonly List<Request> s_log = new();
        static int s_saves;
        static volatile Mode s_mode;

        public static Mode Now { get => s_mode; set => s_mode = value; }
        /// <summary>No session that can send: Spotify.Mixes sends nothing and reports the write offline.</summary>
        public static bool Offline => s_mode == Mode.Offline;

        /// <summary>Put the recording handlers in Library.Net's place (--fake only; the seed calls it once).</summary>
        public static void Install()
        {
            if (!Platform.Args.Fake) return;
            Spotify.Library.Net.PlaylistSignals = Signals;
            Spotify.Library.Net.Playlist = Read;
        }

        /// <summary>UI THREAD. The server takes <paramref name="p"/>'s rows, recipes and templates as its own, once per list: the
        /// first write to a list it does not hold yet. <paramref name="etag"/>: the mix lens etag the seed landed (the serial counts
        /// up from it).</summary>
        public static void Adopt(Playlist p, string? etag = null)
        {
            if (!p.IsValid) return;
            string id = new(EntityUri.IdOf(p.Uri.Text.AsSpan()));
            bool optOut;
            lock (s_gate)
            {
                if (s_lists.TryGetValue(id, out var held) && !held.Stub) return;
                optOut = held?.OptOut ?? false;   // a stub the migration made keeps its opt-out
            }
            var edges = Current.Edges.PlaylistTracks;
            var targets = edges.Targets(p.Slot);
            var payload = edges.Payload(p.Slot);
            var signals = MixData.Signals(p.Slot);
            var rows = new List<Row>(targets.Length);
            for (int i = 0; i < targets.Length; i++)
            {
                var e = payload[i];
                string uri = e.Kind == PlaylistItemKind.Episode ? new Episode(targets[i]).Uri.Text : new Track(targets[i]).Uri.Text;
                ulong item = MixRecipe.Hex64(Strings.Resolve(e.ItemId));
                bool local = e.Kind == PlaylistItemKind.Track && new Track(targets[i]).IsLocal;
                var template = signals is null ? default : signals.TransitionTemplate(item);
                rows.Add(new Row
                {
                    Uri = uri, ItemId = item, AddedAt = e.AddedAt,
                    AddedBy = e.AddedBy > Table.None && e.AddedBy < Current.Users.Count ? new User(e.AddedBy).Uri.Text : null,
                    NotMixable = (e.Flags & PlaylistItemFlags.NotMixable) != 0 || local || e.Kind != PlaylistItemKind.Track,
                    Reason = signals?.NotMixableReason(item) ?? (local || e.Kind != PlaylistItemKind.Track ? "entity-kind" : null),
                    Template = !template.IsEmpty ? template.ToArray() : local || e.Kind != PlaylistItemKind.Track ? null : BuildTemplate(uri, item),
                });
            }
            var list = new ListState
            {
                Title = Strings.Resolve(p.TitleId), Description = Strings.Resolve(p.DescriptionId), Revision = RevisionOf(id), Rows = rows,
                Mixed = p.MixOn, OptOut = optOut,
                EtagSerial = etag is { Length: 8 } && uint.TryParse(etag, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint e0)
                    ? (int)(e0 - 0x0000BEEFu) : 0,
            };
            if (signals?.MixState is { } mixState) list.MixState = mixState;
            foreach (var t in MixData.All(p.Slot)) list.Recipes[t.Recipe.FromItemId] = (t.Base64, t.TransitionUri);
            lock (s_gate) s_lists[id] = list;
        }

        /// <summary>The requests recorded since the last <see cref="Clear"/>, oldest first.</summary>
        public static Request[] Log() { lock (s_gate) return s_log.ToArray(); }

        public static void Clear() { lock (s_gate) s_log.Clear(); }

        /// <summary>The server's view of a list (the inspector's dump): mixed, opted out, stored recipes, the mix lens etag.</summary>
        public static (bool Known, bool Mixed, bool OptOut, int Recipes, string Etag) StateOf(string id)
        {
            lock (s_gate)
                return s_lists.TryGetValue(id, out var l) ? (true, l.Mixed, l.OptOut, l.Recipes.Count, l.Etag) : (false, false, false, 0, "");
        }

        /// <summary>The recipe the server stores for a from-item (base64, as saved), or null.</summary>
        public static string? StoredRecipe(string id, ulong fromItem)
        {
            lock (s_gate) return s_lists.TryGetValue(id, out var l) && l.Recipes.TryGetValue(fromItem, out var r) ? r.Base64 : null;
        }

        /// <summary>A /diff answer for the list at its head (protocol §2: f6 {1 from, 3 to} and f22 only), as the server would send it
        /// to a client holding that head: the inspector's cache-identity check.</summary>
        public static byte[]? DiffAnswer(string id)
        {
            lock (s_gate)
            {
                if (!s_lists.TryGetValue(id, out var l)) return null;
                var buffer = new byte[512];
                var w = new Spotify.Decode.ProtoWriter(buffer);
                int f6 = w.Open(6);
                w.Utf8Always(1, l.Revision);
                w.Utf8Always(3, l.Revision);
                w.Close(f6);
                Lenses(ref w, l);
                return buffer.AsSpan(0, w.Length).ToArray();
            }
        }

        /// <summary>Move the mix lens etag as an edit made on another device would (signals cause no push; protocol §3.4).</summary>
        public static void EditElsewhere(string id)
        {
            lock (s_gate) if (s_lists.TryGetValue(id, out var l)) l.EtagSerial++;
        }

        // ── the handlers (api threads) ─────────────────────────────────────────────────────────────────────────────────

        static Spotify.Api.Result Read(string id)
        {
            var route = Spotify.Api.ListRoute(Spotify.Api.ListKind.Playlist, id);
            byte[] reply;
            int status = 200;
            lock (s_gate)
            {
                if (s_mode == Mode.Fail) { status = 503; reply = []; }
                else
                {
                    // A list the server holds no rows for yet (the migration's ids): a stub that answers its head.
                    if (!s_lists.TryGetValue(id, out var l))
                        s_lists[id] = l = new ListState { Title = "", Description = "", Revision = RevisionOf(id), Rows = [], Stub = true };
                    reply = Reply(l);
                }
                s_log.Add(new Request("GET", route.Path, null, Spotify.Api.ApplyLensesValue(route.Headers), route.SyncReason,
                    (route.Headers & Spotify.HeaderSet.MixLens) != 0, [], status, reply.Length));
            }
            return new Spotify.Api.Result(status, reply);
        }

        static Spotify.Api.Result Signals(string id, Spotify.SignalRoute route, byte[] body)
        {
            Span<char> path = stackalloc char[256];
            var request = Spotify.Build(Spotify.RequestKind.PlaylistSignals, new Spotify.RequestArgs { Id = id, Body = body, Number = (long)route }, path);
            string pathText = new(request.Path), syncReason = new(request.SyncReason);
            string? contentType = Spotify.Api.ContentTypeValue(request.Headers, Spotify.RequestKind.PlaylistSignals);
            string? lenses = Spotify.Api.ApplyLensesValue(request.Headers);
            bool listPlayer = (request.Headers & Spotify.HeaderSet.MixLens) != 0;
            lock (s_gate)
            {
                int status;
                byte[] reply = [];
                if (s_mode == Mode.Fail) status = 503;
                else if (!s_lists.TryGetValue(id, out var l)) status = 404;
                else if (!Apply(l, body, out status)) { }
                else reply = Reply(l);
                s_log.Add(new Request("POST", pathText, contentType, lenses, syncReason, listPlayer, body, status, reply.Length));
                return new Spotify.Api.Result(status, reply);
            }
        }

        /// <summary>The signal against the list's state. False with the status for a refusal.</summary>
        static bool Apply(ListState l, ReadOnlySpan<byte> body, out int status)
        {
            ReadOnlySpan<byte> revision = default, signal = default;
            for (var r = new Spotify.Decode.ProtoReader(body); r.Next();)
            {
                if (r.Field == 1 && r.Wire == 2) revision = r.Bytes();
                else if (r.Field == 2 && r.Wire == 2) signal = r.Bytes();
                else r.Skip();
            }
            if (!revision.SequenceEqual(l.Revision)) { status = 409; return false; }
            ReadOnlySpan<byte> name = default, args = default, payload = default;
            bool hasPayload = false;
            for (var r = new Spotify.Decode.ProtoReader(signal); r.Next();)
            {
                if (r.Field == 1 && r.Wire == 2) name = r.Bytes();
                else if (r.Field == 2 && r.Wire == 2) args = r.Bytes();
                else if (r.Field == 3 && r.Wire == 2) { payload = r.Bytes(); hasPayload = true; }
                else r.Skip();
            }
            status = 200;
            switch (Encoding.UTF8.GetString(name))
            {
                case "mix-state":
                    l.Mixed = hasPayload && new Spotify.Decode.ProtoReader(payload).Varint(1) == 1;
                    return true;
                case "set-transition":
                    ulong item = 0;
                    for (var a = new Spotify.Decode.ProtoReader(args); a.Next();)
                    {
                        if (a.Field == 3 && a.Wire == 2) { var id8 = a.Bytes(); if (id8.Length == 8) item = BinaryPrimitives.ReadUInt64BigEndian(id8); }
                        else a.Skip();
                    }
                    if (item == 0) { status = 400; return false; }
                    l.EtagSerial++;
                    if (!hasPayload) { l.Recipes.Remove(item); return true; }
                    string fresh = Encoding.ASCII.GetString(new Spotify.Decode.ProtoReader(payload).Bytes(1));
                    if (fresh.Length == 0) { status = 400; return false; }
                    // The transition's gid stays with its from-item; the suffix is the server time of the save (protocol §2).
                    string gid = l.Recipes.TryGetValue(item, out var old) && old.Uri.Split(':') is { Length: 4 } parts
                        ? parts[2] : "fakeTransition" + item.ToString("x16", CultureInfo.InvariantCulture);
                    l.Recipes[item] = (fresh, "spotify:transition:" + gid + ":" + (1_700_000_000_000L + 1_000L * ++s_saves).ToString(CultureInfo.InvariantCulture));
                    return true;
                case "mix-opt-out": l.OptOut = true; return true;
                case "mix-remove-opt-out": l.OptOut = false; return true;
                default: status = 400; return false;
            }
        }

        /// <summary>The list as the server reads it back (protocol §2): through the mix lens when the list is mixed (the `auto` lens
        /// applies it by itself), else plain. UNDER THE LOCK.</summary>
        static byte[] Reply(ListState l)
        {
            if (l.Stub)
            {
                var head = new byte[64];
                var hw = new Spotify.Decode.ProtoWriter(head);
                hw.Utf8Always(1, l.Revision);
                hw.UAlways(2, 0);
                return head.AsSpan(0, hw.Length).ToArray();
            }
            int size = 512 + l.Title.Length * 3 + l.Description.Length * 3;
            foreach (var row in l.Rows) size += 160 + row.Uri.Length * 3 + (row.Template?.Length ?? 0) + (row.AddedBy?.Length ?? 0) * 3;
            foreach (var recipe in l.Recipes.Values) size += recipe.Base64.Length + recipe.Uri.Length + 96;
            var buffer = new byte[size];
            var w = new Spotify.Decode.ProtoWriter(buffer);
            w.Utf8Always(1, l.Revision);
            w.UAlways(2, (ulong)l.Rows.Count);
            int attributes = w.Open(3);
            w.Str(1, l.Title);
            w.Str(2, l.Description);
            if (l.Mixed)
            {
                w.Entry(12, "can-view-transition", "true");
                w.Entry(12, "automix.mode", "auto");
                w.Entry(12, "mix", "true");
                w.Entry(12, "mix-type", "allow-custom");
                w.Entry(12, "automix.queue", "true");
                w.Entry(12, "automix.autoplay", "true");
                w.Entry(12, "automix.ignore_setting", "true");
                w.Entry(12, "has-custom-transitions", l.Recipes.Count > 0 ? "true" : "false");
            }
            w.Close(attributes);
            int contents = w.Open(5);
            Span<byte> id8 = stackalloc byte[8];
            foreach (var row in l.Rows)
            {
                int item = w.Open(3);
                w.StrAlways(1, row.Uri);
                int attrs = w.Open(2);
                if (row.AddedBy is { } by) w.Str(1, by.StartsWith("spotify:user:", StringComparison.Ordinal) ? by["spotify:user:".Length..] : by);
                if (row.AddedAt > 0) w.UAlways(2, (ulong)(row.AddedAt > 100_000_000 ? row.AddedAt * 1000 : row.AddedAt));
                if (l.Mixed)
                {
                    if (row.NotMixable)
                    {
                        w.Entry(11, "not-mixable", "true");
                        if (row.Reason is { } reason) w.Entry(11, "not-mixable.reason", reason);
                    }
                    else
                    {
                        if (l.Recipes.TryGetValue(row.ItemId, out var recipe))
                        {
                            w.Entry(11, "automix.auto_transition_recipe", recipe.Base64);
                            w.Entry(11, "automix.transition_uri", recipe.Uri);
                        }
                        w.Entry(11, "custom_reporting_attribution", "MixedPlaylist");
                    }
                }
                BinaryPrimitives.WriteUInt64BigEndian(id8, row.ItemId);
                if (row.ItemId != 0) w.Utf8Always(12, id8);
                if (l.Mixed && row.Template is { } template) w.Utf8Always(14, template);
                w.Close(attrs);
                w.Close(item);
            }
            int prompt = w.Open(5);
            w.Utf8Always(1, "UPDATE_PROMPT"u8);
            int promptArgs = w.Open(2);
            w.Utf8Always(1, "ssp-agentic-list"u8);
            w.Close(promptArgs);
            w.Close(prompt);
            if (l.Mixed) w.Utf8Always(5, l.MixState);
            w.Close(contents);
            Lenses(ref w, l);
            return buffer.AsSpan(0, w.Length).ToArray();
        }

        /// <summary>f22, the applied lenses in the holder form: `auto` always, `mix` with its etag while the list is mixed.</summary>
        static void Lenses(ref Spotify.Decode.ProtoWriter w, ListState l)
        {
            int holder = w.Open(22);
            Lens(ref w, "auto", "auto");
            if (l.Mixed) Lens(ref w, "mix", l.Etag);
            w.Close(holder);

            static void Lens(ref Spotify.Decode.ProtoWriter w, string name, string etag)
            {
                int entry = w.Open(1);
                w.StrAlways(1, name);
                w.StrAlways(2, etag);
                int status = w.Open(3);
                w.UAlways(1, 200);
                w.Close(status);
                w.Close(entry);
            }
        }

        /// <summary>A made-up 24-byte revision: counter 7, then a 20-byte hash of `beef` and the id's length.</summary>
        static byte[] RevisionOf(string id)
        {
            var revision = new byte[24];
            BinaryPrimitives.WriteUInt32BigEndian(revision, 7);
            for (int i = 4; i < 24; i++) revision[i] = (byte)((i & 1) == 0 ? 0xBE : 0xEF);
            revision[23] = (byte)id.Length;
            return revision;
        }

        /// <summary>Item attribute 14 as a lens read carries it: <c>{1 "set-transition", 2 {1 "mix", 2 uri, 3 item id}}</c>.</summary>
        static byte[] BuildTemplate(string uri, ulong itemId)
        {
            var buffer = new byte[uri.Length * 3 + 64];
            var w = new Spotify.Decode.ProtoWriter(buffer);
            w.Utf8Always(1, "set-transition"u8);
            int args = w.Open(2);
            w.Utf8Always(1, "mix"u8);
            w.StrAlways(2, uri);
            Span<byte> id = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(id, itemId);
            w.Utf8Always(3, id);
            w.Close(args);
            return buffer.AsSpan(0, w.Length).ToArray();
        }
    }
}
