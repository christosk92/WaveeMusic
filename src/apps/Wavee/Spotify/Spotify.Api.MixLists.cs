// ── Spotify/Spotify.Api.MixLists.cs — #48 Mix M8: Smart reorder, Duplicate Mix and who may use Mix at all ─────────────────────
//
// Three things the official desktop client 1.3.10 does with a mixed playlist beyond its transitions (plan M8):
//
//   Smart reorder   the playlist read through the `harmonize` lens: Spotify's own order of the rows for smooth transitions. The
//                   official client requests that lens together with its Auto mix toggle (`mix-auto-toggle-on`, then
//                   `requestLenses(uri, "harmonize", on)`) and tells the user "Playlist has been mixed and reordered for smooth
//                   transitions". Wavee READS the lens once (never folded: the list's own order stays what it is) and, after the
//                   user has seen the proposal, writes it as ordinary keyed moves in ONE /changes delta, so every device and every
//                   player sees the same order and the move is undoable (UX direction S5: "storage is a normal playlist move").
//                   Whether a lens alone would reorder the list for other devices, and the lens's place in the header, are open.
//   Duplicate Mix   POST spclient /mix-list-lens/api/v0/duplicate, JSON {playlist_uri, transition_overrides: [{item_id (the 8
//                   id bytes, base64), transition (a recipe, base64)}]} → {playlist_uri} [client: the official editor's Duplicate
//                   button]. Then, as the client does: the copy is filed at the top of the rootlist, its Mix is turned on
//                   (`mix-state` {1: 1}), and it opens.
//   Premium         a Free account gets the upsell where Mix would be ("Play and create mixed playlists with Premium"), never a
//                   Mix action it cannot perform. Wavee's own session refuses a Free account (the Premium gate, D12), so this is a
//                   cached shell after a downgrade; --fake switches it with `mix fake tier=free`.
//
// Every request goes through Library.Net, so --fake answers from the recording server (Entities.Fake.MixServer.cs) exactly as the
// M2 writes do. UI thread in and out; the work runs on the api threads.

using System.Buffers.Binary;
using System.Text.Json;

namespace Wavee;

public static partial class Spotify
{
    public static partial class Api
    {
        /// <summary>A playlist read through the <c>harmonize</c> lens (with <c>mix,auto</c>, as a mixed list is read) and the page-open
        /// sync-reason. PURE.</summary>
        public static Route HarmonizedRoute(string id)
        {
            var list = ListRoute(ListKind.Playlist, id);
            return list with { Headers = list.Headers | HeaderSet.MixLens | HeaderSet.HarmonizeLens, SyncReason = "CAEQAQ==" };
        }

        public static Result PlaylistHarmonized(string id, CancellationToken ct) => Send(HarmonizedRoute(id), [], ct);

        /// <summary>POST spclient <c>/mix-list-lens/api/v0/duplicate</c>, JSON in and out, no market. PURE.</summary>
        public static Route MixDuplicateRoute
            => new(Verb.Post, ApiHost.SpclientWg, "/mix-list-lens/api/v0/duplicate", CommonJson | HeaderSet.ContentJson, RequestKind.Custom);

        /// <summary><c>{"playlist_uri", "transition_overrides": [{"item_id": base64 of the 8 id bytes, "transition": base64 recipe}]}</c>
        /// (the client's <c>x$</c>). PURE.</summary>
        public static byte[] MixDuplicateBody(string playlistUri, IReadOnlyList<MixLists.Override>? overrides)
        {
            var buffer = new System.Buffers.ArrayBufferWriter<byte>(256);
            using (var w = new Utf8JsonWriter(buffer))
            {
                w.WriteStartObject();
                w.WriteString("playlist_uri", playlistUri);
                w.WriteStartArray("transition_overrides");
                Span<byte> id = stackalloc byte[8];
                if (overrides is not null)
                    for (int i = 0; i < overrides.Count; i++)
                    {
                        BinaryPrimitives.WriteUInt64BigEndian(id, overrides[i].ItemId);
                        w.WriteStartObject();
                        w.WriteString("item_id", Convert.ToBase64String(id));
                        w.WriteString("transition", overrides[i].Recipe);
                        w.WriteEndObject();
                    }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            return buffer.WrittenSpan.ToArray();
        }

        /// <summary>The copy's uri out of the duplicate's answer, or null. PURE.</summary>
        public static string? MixDuplicateAnswer(ReadOnlySpan<byte> json)
        {
            try
            {
                var reader = new Utf8JsonReader(json);
                using var doc = JsonDocument.ParseValue(ref reader);
                return doc.RootElement.ValueKind == JsonValueKind.Object
                       && doc.RootElement.TryGetProperty("playlist_uri", out var uri) && uri.ValueKind == JsonValueKind.String
                       && uri.GetString() is { Length: > 0 } text && text.StartsWith("spotify:playlist:", StringComparison.Ordinal) ? text : null;
            }
            catch (JsonException) { return null; }
        }

        public static Result MixDuplicate(byte[] body, CancellationToken ct) => Send(MixDuplicateRoute, body, ct);
    }

    public static class MixLists
    {
        /// <summary>A transition the copy carries instead of the source's: the from-item and the recipe (base64), as the editor holds
        /// it unsaved.</summary>
        public readonly record struct Override(ulong ItemId, string Recipe);

        /// <summary>How a Smart reorder read or write, or a duplicate, settled. <see cref="NoOrder"/>: the reply did not apply the
        /// harmonize lens; <see cref="Same"/>: Spotify's order is the list's; <see cref="Changed"/>: the list moved since the proposal.</summary>
        public enum Outcome : byte { Landed, Failed, Offline, Refused, NoOrder, Same, Changed }

        static volatile bool s_fakeFree;

        /// <summary>--fake only: the account reads as Free (the inspector's `mix fake tier=free`).</summary>
        public static bool FakeFree { get => s_fakeFree; set => s_fakeFree = value && Platform.Args.Fake; }

        /// <summary>May this account use Mix (play it mixed, write it, reorder, duplicate)? False only on a KNOWN Free verdict: an
        /// unknown tier (a login still under way) shows nothing new.</summary>
        public static bool Premium => Platform.Args.Fake ? !s_fakeFree : Current.Tier != Tier.Free;

        /// <summary>Smart reorder is offered: Mix is on and the user may write the list (owner or collaborator, Premium).</summary>
        public static bool CanReorder(Playlist p) => p.IsValid && p.MixOn && p.Editable && Mixes.CanWrite(p);

        /// <summary>Duplicate Mix is offered: a mixed list the user may not write (the official "You can't edit the transitions in this
        /// playlist"), on a Premium account.</summary>
        public static bool CanDuplicate(Playlist p) => p.IsValid && p.MixOn && Premium && !Mixes.CanWrite(p);

        /// <summary>A write can go out now (a session, or the fake server online).</summary>
        public static bool Online => Mixes.Online(out _);

        static string WireId(string uri) => new(EntityUri.IdOf(uri.AsSpan()));

        // ── Smart reorder ────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Read Spotify's order: the list through the harmonize lens, its rows' item ids in the reply's order. Nothing is folded.
        /// <paramref name="done"/> (UI thread) gets the order when the lens applied, else the reason.</summary>
        public static void ReadSmartOrder(Playlist p, Action<Outcome, ulong[]?> done)
        {
            if (!CanReorder(p)) { done(Outcome.Refused, null); return; }
            if (!Mixes.Online(out _)) { Log.Info("mix", "harmonize not read: offline"); done(Outcome.Offline, null); return; }
            if (Platform.Args.Fake) Entities.FakeMixServer.Adopt(p);
            string id = WireId(p.Uri.Text);
            var net = Library.Net;
            if (!net.Run(() =>
                {
                    Api.Result r = net.PlaylistHarmonized(id);
                    bool applied = false;
                    ulong[]? order = r.Ok && r.Length > 0 ? HarmonizedOrder(r.Bytes, out applied) : null;
                    var outcome = !r.Ok ? Outcome.Failed : !applied || order is null ? Outcome.NoOrder : Outcome.Landed;
                    Log.Info("mix", "harmonize read route=" + Api.ApplyLensesValue(Api.HarmonizedRoute(id).Headers) + " status=" + r.Status
                                    + " reply=" + r.Length + "B lens=" + (applied ? "applied" : "absent") + " items=" + (order?.Length ?? 0));
                    Post(() => done(outcome, outcome == Outcome.Landed ? order : null));
                }))
                done(Outcome.Failed, null);
        }

        /// <summary>The item ids of a list reply's rows in its order (8-byte attribute 12, 0 where a row has none), and whether f22 names
        /// the harmonize lens. PURE.</summary>
        internal static ulong[]? HarmonizedOrder(ReadOnlySpan<byte> selectedListContent, out bool lensApplied)
        {
            lensApplied = false;
            var ids = new List<ulong>();
            for (var r = new Decode.ProtoReader(selectedListContent); r.Next();)
            {
                if (r.Wire != 2) { r.Skip(); continue; }
                var value = r.Bytes();
                if (r.Field == 22) lensApplied |= NamesLens(value, "harmonize"u8, 0);
                else if (r.Field == 5)
                {
                    for (var c = new Decode.ProtoReader(value); c.Next();)
                    {
                        if (c.Field != 3 || c.Wire != 2) { c.Skip(); continue; }
                        ulong item = 0;
                        for (var it = new Decode.ProtoReader(c.Bytes()); it.Next();)
                        {
                            if (it.Field != 2 || it.Wire != 2) { it.Skip(); continue; }
                            var id8 = new Decode.ProtoReader(it.Bytes()).Bytes(12);
                            if (id8.Length == 8) item = BinaryPrimitives.ReadUInt64BigEndian(id8);
                        }
                        ids.Add(item);
                    }
                }
            }
            return ids.Count > 0 ? ids.ToArray() : null;

            // f22 is one lens entry {1 name, 2 etag, 3 status} or a holder of them (protocol §2).
            static bool NamesLens(ReadOnlySpan<byte> entry, ReadOnlySpan<byte> name, int depth)
            {
                if (new Decode.ProtoReader(entry).Bytes(1).SequenceEqual(name)) return true;
                if (depth > 0) return false;
                for (var h = new Decode.ProtoReader(entry); h.Next();)
                {
                    if (h.Wire == 2) { if (NamesLens(h.Bytes(), name, depth + 1)) return true; }
                    else h.Skip();
                }
                return false;
            }
        }

        /// <summary>The keyed moves that turn the list's order into <paramref name="from"/>'s (from[k] = the current index of the row that
        /// goes to position k): the rows on the longest run already in order stay, every other row moves after the row that precedes it
        /// in the new order (the first: to the top). Applied in order they give exactly the new order. PURE.</summary>
        public static bool[] Staying(ReadOnlySpan<int> from)
        {
            int n = from.Length;
            var tails = new int[n];          // tails[l] = the index k ending the best run of length l + 1
            var prev = new int[n];
            int length = 0;
            for (int k = 0; k < n; k++)
            {
                int lo = 0, hi = length;
                while (lo < hi) { int mid = (lo + hi) >> 1; if (from[tails[mid]] < from[k]) lo = mid + 1; else hi = mid; }
                prev[k] = lo > 0 ? tails[lo - 1] : -1;
                tails[lo] = k;
                if (lo == length) length++;
            }
            var stay = new bool[n];
            for (int k = length > 0 ? tails[length - 1] : -1; k >= 0; k = prev[k]) stay[k] = true;
            return stay;
        }

        /// <summary>Put the list in <paramref name="order"/> (item ids, every row exactly once): the rows land at once (the one door every
        /// optimistic row write takes, PlaylistEdits.LandOptimistic), then ONE /changes delta of keyed moves goes at the server's head and
        /// the list is read back and folded. A refusal puts the rows back. <paramref name="settled"/> hears the outcome and how many songs
        /// changed place.</summary>
        public static void Reorder(Playlist p, IReadOnlyList<ulong> order, Action<Outcome, int> settled)
        {
            if (!CanReorder(p)) { settled(Outcome.Refused, 0); return; }
            if (!Mixes.Online(out var scope)) { settled(Outcome.Offline, 0); return; }
            var edges = scope.Edges.PlaylistTracks;
            int[] targets = edges.Targets(p.Slot).ToArray();
            PlaylistTrackEdge[] payload = edges.Payload(p.Slot).ToArray();
            EdgeState state = edges.State(p.Slot);
            int total = edges.Total(p.Slot), n = targets.Length;
            if (order.Count != n || n == 0) { settled(Outcome.Changed, 0); return; }
            var at = new Dictionary<ulong, int>(n);
            for (int i = 0; i < n; i++)
            {
                ulong item = MixRecipe.Hex64(Entities.Strings.Resolve(payload[i].ItemId));
                if (item == 0 || !at.TryAdd(item, i)) { settled(Outcome.Changed, 0); return; }
            }
            var from = new int[n];
            int moved = 0;
            for (int k = 0; k < n; k++)
            {
                if (!at.TryGetValue(order[k], out from[k])) { settled(Outcome.Changed, 0); return; }
                if (from[k] != k) moved++;
            }
            if (moved == 0) { settled(Outcome.Same, 0); return; }

            var stay = Staying(from);
            var ops = new List<PlaylistOp>(n);
            for (int k = 0; k < n; k++)
            {
                if (stay[k]) continue;
                var member = Member(targets, payload, from[k]);
                ops.Add(k == 0
                    ? new PlaylistOp(PlaylistOpKind.Move, Items: [member], ItemsAsKey: true, Anchor: PlaylistMoveAnchor.First)
                    : new PlaylistOp(PlaylistOpKind.Move, Items: [member], ItemsAsKey: true, Anchor: PlaylistMoveAnchor.AfterItem,
                        AnchorItemId: Member(targets, payload, from[k - 1]).ItemId, AnchorUri: Member(targets, payload, from[k - 1]).Uri));
            }
            var newTargets = new int[n];
            var newPayload = new PlaylistTrackEdge[n];
            for (int k = 0; k < n; k++) { newTargets[k] = targets[from[k]]; newPayload[k] = payload[from[k]]; }

            string username = Platform.Args.Fake ? scope.Key.Account : PlaylistEdits.CanWrite(out _, out string account) ? account : "";
            string id = WireId(p.Uri.Text), uri = p.Uri.Text;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            PlaylistOp[] body = ops.ToArray();
            int slot = p.Slot;
            if (Platform.Args.Fake) Entities.FakeMixServer.Adopt(p);

            PlaylistEdits.LandOptimistic(p, newTargets, newPayload, state, total);
            Entities.Publish();

            var net = Library.Net;
            if (!net.Run(() =>
                {
                    Api.Result read = net.Playlist(id);
                    Span<byte> head = stackalloc byte[Encode.MaxRevisionBytes];
                    int length = read.Ok ? Encode.ResultingRevision(read.Bytes, head) : 0;
                    int status = read.Ok ? 0 : read.Status;
                    Staging? staging = null;
                    if (length > 0)
                    {
                        Api.Result r = net.PlaylistChanges(id, Encode.PlaylistChanges(head[..length], body, username, now, Encode.NewNonce()));
                        status = r.Status;
                        if (r.Ok)
                        {
                            Api.Result back = net.Playlist(id);
                            if (back.Ok && back.Length > 0)
                            {
                                staging = Staging.Rent();
                                staging.Epoch = scope.Epoch;
                                Api.PlaylistAnswer(back.Bytes, uri, staging);
                            }
                        }
                    }
                    bool ok = status is >= 200 and < 300;
                    Log.Info("mix", "smart reorder moves=" + body.Length + " songs=" + moved + " status=" + status + (ok ? staging is null ? "" : " folded" : " failed"));
                    Post(() =>
                    {
                        bool current = ReferenceEquals(scope, Entities.Current);
                        if (staging is not null)
                        {
                            if (current) { Entities.Commit(staging); Entities.Publish(); }
                            Staging.Return(staging);
                        }
                        if (!ok && current)
                        {
                            PlaylistEdits.LandOptimistic(new Playlist(slot), targets, payload, state, total);
                            Entities.Publish();
                        }
                        settled(ok ? Outcome.Landed : Outcome.Failed, moved);
                    });
                }))
            {
                PlaylistEdits.LandOptimistic(p, targets, payload, state, total);
                Entities.Publish();
                settled(Outcome.Failed, 0);
            }
        }

        /// <summary>The row at <paramref name="i"/> as a keyed op names it: its uri off the table its kind names, and its item id.</summary>
        static PlaylistMember Member(int[] targets, PlaylistTrackEdge[] payload, int i)
        {
            string uri = payload[i].Kind == PlaylistItemKind.Episode ? new Episode(targets[i]).Id.Text : new Track(targets[i]).Id.Text;
            return new PlaylistMember(uri, Entities.Strings.Resolve(payload[i].ItemId));
        }

        /// <summary>The list's rows as item ids, in order (the undo's target). UI thread.</summary>
        public static ulong[] OrderOf(Playlist p)
        {
            var payload = Entities.Current.Edges.PlaylistTracks.Payload(p.Slot);
            var order = new ulong[payload.Length];
            for (int i = 0; i < order.Length; i++) order[i] = MixRecipe.Hex64(Entities.Strings.Resolve(payload[i].ItemId));
            return order;
        }

        // ── Duplicate Mix ────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Duplicate a mixed playlist the user may not edit, as the official editor does: the duplicate call (with the unsaved
        /// transitions the editor holds, none from the menu), the copy read and folded, filed at the top of the rootlist, its Mix turned on,
        /// then opened. <paramref name="settled"/> hears how it went; a failure says the client's sentence.</summary>
        public static void Duplicate(Playlist source, IReadOnlyList<Override>? overrides, Action<Outcome>? settled = null)
        {
            if (!source.IsValid || !source.MixOn || !Premium) { settled?.Invoke(Outcome.Refused); return; }
            if (!Mixes.Online(out var scope)) { Said(Outcome.Offline); return; }
            byte[] body = Api.MixDuplicateBody(source.Uri.Text, overrides);
            string title = Entities.Strings.Resolve(source.TitleId);
            var net = Library.Net;
            if (!net.Run(() =>
                {
                    Api.Result r = net.MixDuplicate(body);
                    string? uri = r.Ok && r.Length > 0 ? Api.MixDuplicateAnswer(r.Bytes) : null;
                    Staging? staging = null;
                    int readStatus = 0;
                    if (uri is not null)
                    {
                        Api.Result read = net.Playlist(WireId(uri));
                        readStatus = read.Status;
                        if (read.Ok && read.Length > 0)
                        {
                            staging = Staging.Rent();
                            staging.Epoch = scope.Epoch;
                            Api.PlaylistAnswer(read.Bytes, uri, staging);
                        }
                    }
                    Log.Info("mix", "duplicate status=" + r.Status + " overrides=" + (overrides?.Count ?? 0)
                                    + (uri is null ? " (no copy)" : " copy read=" + readStatus + (staging is null ? "" : " folded")));
                    Post(() => Landed(uri, staging));
                }))
                Said(Outcome.Failed);

            void Landed(string? uri, Staging? staging)
            {
                bool current = ReferenceEquals(scope, Entities.Current);
                if (uri is null || !current)
                {
                    if (staging is not null) Staging.Return(staging);
                    Said(Outcome.Failed);
                    return;
                }
                int slot = SeedCopy(scope, uri, title);
                if (staging is not null) { Entities.Commit(staging); Staging.Return(staging); }
                Entities.Publish();
                if (slot == Table.None) { Said(Outcome.Failed); return; }
                File(scope, uri);
                var copy = new Playlist(slot);
                // The official client turns the copy's Mix on, then opens it; Wavee opens it whatever the Mix write says (the toggle shows it).
                Mixes.TurnOn(copy, _ =>
                {
                    if (!ReferenceEquals(scope, Entities.Current)) return;
                    Actions.Services.Go?.Invoke(Shell.For(new EntityUri(scope.Playlists.Id[slot]), Entities.Strings.Resolve(copy.TitleId)));
                    settled?.Invoke(Outcome.Landed);
                });
            }

            void Said(Outcome outcome)
            {
                Notify.Say(FluentGpu.Localization.Loc.Get(outcome == Outcome.Offline ? Strings.Mix.DuplicateOffline : Strings.Mix.DuplicateFailed),
                    FluentGpu.Controls.InfoBarSeverity.Warning, dedupeKey: "mix.duplicate");
                settled?.Invoke(outcome);
            }
        }

        /// <summary>The copy's row before its read lands (that answer overwrites it): the account's own playlist, its rows Complete and
        /// empty until the read's. Thin authority, as a create seeds its row.</summary>
        static int SeedCopy(Scope scope, string uri, string title)
        {
            var id = EntityId.Parse(uri.AsSpan());
            if (id.Kind != EntityKind.Playlist) return Table.None;
            var staging = Staging.Rent();
            try
            {
                staging.Epoch = scope.Epoch;
                ref var row = ref staging.Playlists.RowFor(new StagedId(id), Authority.Thin, (uint)(PlaylistFields.Identity | PlaylistFields.Capabilities | PlaylistFields.Visibility));
                row.Title = staging.AddText(System.Text.Encoding.UTF8.GetBytes(title));
                row.OwnerUri = new StagedId(scope.Users.Id[scope.MeSlot]);
                row.Caps = (byte)(PlaylistCaps.CanView | PlaylistCaps.CanEditItems | PlaylistCaps.CanEditMetadata | PlaylistCaps.IsOwner);
                row.Flags = row.FlagsMask = (uint)PlaylistFlags.Public;
                Entities.Commit(staging);
            }
            finally { Staging.Return(staging); }
            return Entities.Current.Playlists.TryGetSlot(id, out int slot) ? slot : Table.None;
        }

        /// <summary>File the copy at the top of the rootlist (the client's <c>add([uri], {before: "start"})</c>): Library's follow, which
        /// writes the rootlist ADD; --fake has no rootlist service, so the tree lands here as the write's optimistic half does.</summary>
        static void File(Scope scope, string uri)
        {
            if (!Platform.Args.Fake) { Library.FollowPlaylist(uri, true); return; }
            var entries = new List<RootlistEntry>();
            Encode.RootlistEntries(new User(scope.MeSlot), entries);
            if (Encode.RootlistRemove(entries, uri) is not null) return;
            Library.LandRootlistTree(scope, Encode.ApplyLocally(entries, [Encode.RootlistAdd(uri, 0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())]));
            Entities.Publish();
        }
    }
}
