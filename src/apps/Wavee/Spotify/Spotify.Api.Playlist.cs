// ── Spotify/Spotify.Api.Playlist.cs ────────────────────────────────────────────────────────────────────────────────────
// the playlist page's edit host (remove · move · rename · description · collaborative · visibility · invite · delete ·
// tune · cover · recommendations) and the three provider answers the PlaylistRead / ContentFilters routes need
//
// Role: SHELL
// Owner: O (WP-5.O stream A)
// Wave: 5
// Budget: 550 lines
// Spec: ch 06 §6 / §0.12 ("optimistic, then honest"), W16, W17, W18, W22b, W24, W10; WP-5.O contract §4 A.4
//
// THE SHAPE OF EVERY VERB (0.2.9 PlaylistMutationSource + PlaylistEditErrors, re-cut for 0.3):
//
//     UI thread                     api worker (Api.Run)                              UI thread (Spotify.Post)
//     ─────────                     ────────────────────                              ────────────────────────
//     gate (CanWrite, keyed rows)   read the playlist's head (GET v2, like Library's    scope still current?
//     snapshot + OPTIMISTIC write   PostDeposit — nothing holds a writable head)       ok  → settle (+ refresh)
//     Publish                  ──▶  POST /changes (Encode.PlaylistChanges)        ──▶  bad → REVERT + the mapped toast
//                                   one retry on 409 where the op is keyed-exact        (PlaylistEditErrors.Raise)
//
// The Local Files playlist has no remote: its row writes settle in place. Nothing here holds a span across a post.

using System.Text;
using System.Text.Json;
using FluentGpu.Controls;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using Google.Protobuf;
using Pl = Wavee.Protocol.Playlist;

namespace Wavee;

public static partial class Spotify
{
    public static partial class Api
    {
        // ── the provider answers (the shared patches call these; see the report) ──────────────────────────────────

        /// <summary>A PlaylistRead answer, both halves: the header + membership, and the format attributes.</summary>
        internal static void PlaylistAnswer(ReadOnlySpan<byte> selectedListContent, string uri, Staging s)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(uri);
            Decode.PlaylistRevision(selectedListContent, utf8, s);
            Decode.PlaylistFormatAttributes(selectedListContent, utf8, s);
        }

        /// <summary>The membership relation's read, REVISION-GATED (B1b gap 8): a held head asks the /diff first
        /// (<see cref="ReadList"/>), and an unchanged list stages nothing.</summary>
        static void PlaylistEdge(string uri, string? revision, Staging s, ref FetchOutcome outcome)
        {
            byte[]? body = ReadList(ListKind.Playlist, IdOf(uri), revision, ref outcome);
            if (body is not null) PlaylistAnswer(body, uri, s);
        }

        /// <summary><see cref="UserFields.ContentFilters"/>: 404 is "no chip set" — the known-empty publish.</summary>
        static void ContentFiltersAnswer(string uri, Staging s, ref FetchOutcome outcome)
        {
            Result result = LikedContentFilters(null, CancellationToken.None);
            outcome.Note(in result);
            if (result.Ok || result.Status == 404)
                Decode.LikedContentFilters(result.Ok ? result.Bytes : default, Encoding.UTF8.GetBytes(uri), s);
        }

        // ── the extender (0.2.9 PlaylistExtenderClient) ─────────────────────────────────────────────────────────────

        /// <summary>POST spclient-wg <c>/playlistextender/extendp/</c> (JSON in, zstd JSON out). PURE.</summary>
        public static Route ExtenderRoute
            => new(Verb.Post, ApiHost.SpclientWg, "/playlistextender/extendp/", CommonJson | HeaderSet.ContentJson,
                   RequestKind.Custom, Zstd: true);

        /// <summary><c>{"playlistURI", "trackSkipIDs": [...], "numResults"}</c>. PURE.</summary>
        public static byte[] ExtenderBody(string playlistUri, IReadOnlyList<string> skipIds, int count)
        {
            var buffer = new System.Buffers.ArrayBufferWriter<byte>(256);
            using (var w = new Utf8JsonWriter(buffer))
            {
                w.WriteStartObject();
                w.WriteString("playlistURI", playlistUri);
                w.WriteStartArray("trackSkipIDs");
                for (int i = 0; i < skipIds.Count; i++) w.WriteStringValue(skipIds[i]);
                w.WriteEndArray();
                w.WriteNumber("numResults", count);
                w.WriteEndObject();
            }
            return buffer.WrittenSpan.ToArray();
        }

        public static Result PlaylistExtend(string playlistUri, IReadOnlyList<string> skipIds, int count, CancellationToken ct)
            => Send(ExtenderRoute, ExtenderBody(playlistUri, skipIds, count), ct);

        /// <summary>The ApplyPlaylistSignals body (0.2.9 PlaylistSignalsClient): the 24-byte head + ONE signal with a fresh
        /// interaction uuid. PURE but for the uuid.</summary>
        public static byte[] SignalsBody(ReadOnlySpan<byte> revision, string identifier)
            => new Pl.ApplyPlaylistSignals
            {
                Revision = ByteString.CopyFrom(revision),
                Signals = { new Pl.AvailableSignal { Identifier = identifier, Interaction = new Pl.PlaylistSignalInteraction { Uuid = Guid.NewGuid().ToString("D") } } },
            }.ToByteArray();
    }

    /// <summary>THE playlist edit host (file header). UI thread unless a member says otherwise.</summary>
    public static class PlaylistEdits
    {
        /// <summary>The recommendations batch (0.2.9 RecBatch).</summary>
        public const int RecBatch = 20;
        /// <summary>An invite link's lifetime, 0.2.9's 7 days.</summary>
        public const long InviteTtlMs = 604_800_000;

        static Library.Transport Net => Library.Net;

        /// <summary>A write may go out: an account scope and a session that can send (Library's own gate).</summary>
        public static bool CanWrite(out Scope scope, out string username)
        {
            scope = Entities.Current;
            username = scope is not null && scope.Key.Provider == "spotify" && scope.MeSlot > Table.None ? scope.Key.Account : "";
            return username.Length > 0 && Current.IsOnline;
        }

        static bool IsLocal(Playlist p) => p.Uri.Provider == EntityProvider.Local;
        static string IdOf(Playlist p) => new(EntityUri.IdOf(p.Uri.Text.AsSpan()));
        static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        static void Fail(PlaylistMutationFailure kind, PlaylistEditVerb verb) => PlaylistEditErrors.Raise(kind, verb);

        // ── the membership snapshot (the revert) ──────────────────────────────────────────────────────────────────

        readonly record struct Membership(int[] Targets, PlaylistTrackEdge[] Payload, EdgeState State, int Total);

        static Membership Snapshot(Playlist p)
        {
            var e = Entities.Current.Edges.PlaylistTracks;
            return new Membership(e.Targets(p.Slot).ToArray(), e.Payload(p.Slot).ToArray(), e.State(p.Slot), e.Total(p.Slot));
        }

        static void Restore(Scope scope, Playlist p, in Membership m)
        {
            if (!ReferenceEquals(scope, Entities.Current)) return;
            scope.Edges.PlaylistTracks.Replace(p.Slot, m.Targets, m.Payload, m.State, m.Total);
            p.Refold();
            Entities.Publish();
        }

        static PlaylistMember MemberAt(in Membership m, int index)
            => new(new Track(m.Targets[index]).Id.Text, Entities.Strings.Resolve(m.Payload[index].ItemId));

        // ── remove rows (Track.MenuSeams.RemoveRows / TableProfile.RemoveRows) ────────────────────────────────────

        /// <summary>Remove membership rows by ORIGINAL index: ONE keyed REM. A row whose item id has not landed cannot be
        /// addressed (no positional fallback) and refuses the whole edit as Pending.</summary>
        public static void RemoveRows(Playlist p, IReadOnlyList<int> rows)
        {
            if (!p.IsValid || rows is not { Count: > 0 } || !p.Editable) return;
            var before = Snapshot(p);
            var drop = new bool[before.Targets.Length];
            var members = new List<PlaylistMember>(rows.Count);
            for (int i = 0; i < rows.Count; i++)
            {
                int at = rows[i];
                if ((uint)at >= (uint)drop.Length || drop[at]) continue;
                if (!IsLocal(p) && before.Payload[at].ItemId.IsEmpty) { Fail(PlaylistMutationFailure.Pending, PlaylistEditVerb.Remove); return; }
                drop[at] = true;
                members.Add(MemberAt(in before, at));
            }
            if (members.Count == 0) return;
            if (!IsLocal(p) && !CanWrite(out _, out _)) { Fail(PlaylistMutationFailure.NotSupported, PlaylistEditVerb.Remove); return; }

            var scope = Entities.Current;
            int kept = before.Targets.Length - members.Count;
            var targets = new int[kept];
            var payload = new PlaylistTrackEdge[kept];
            for (int i = 0, k = 0; i < drop.Length; i++)
                if (!drop[i]) { targets[k] = before.Targets[i]; payload[k++] = before.Payload[i]; }
            scope.Edges.PlaylistTracks.Replace(p.Slot, targets, payload, before.State, Math.Max(0, before.Total - members.Count));
            p.Refold();
            Entities.Publish();
            int count = members.Count;
            if (IsLocal(p)) { Removed(count); return; }

            var op = new PlaylistOp(PlaylistOpKind.Remove, Items: members, ItemsAsKey: true);
            Post(p, scope, op, retry409: true, ok: () => Removed(count),
                 failed: kind => { Restore(scope, p, in before); Fail(kind, PlaylistEditVerb.Remove); });

            static void Removed(int n) => Notify.Say(Strings.Detail.Edit.RemovedFromPlaylist(n), InfoBarSeverity.Success);
        }

        // ── move rows (TableProfile.MoveRows): ONE keyed MOV, PRE-move index ──────────────────────────────────────

        /// <summary>Move <paramref name="rows"/> (original indices + item ids) to insert before the row currently at
        /// <paramref name="preMoveIndex"/>. Answers whether the gesture was taken (a refusal is already toasted).</summary>
        public static bool MoveRows(Playlist p, ReadOnlySpan<RowRef> rows, int preMoveIndex)
        {
            if (!p.IsValid || rows.IsEmpty || !p.Editable) return false;
            var before = Snapshot(p);
            int n = before.Targets.Length;
            var moving = new bool[n];
            var order = new List<int>(rows.Length);
            for (int i = 0; i < rows.Length; i++)
                if ((uint)rows[i].Index < (uint)n && !moving[rows[i].Index]) { moving[rows[i].Index] = true; order.Add(rows[i].Index); }
            order.Sort();
            if (order.Count == 0) return false;
            int at = Math.Clamp(preMoveIndex, 0, n);

            // The new order: the unmoved rows above the slot, the block (in original order), the rest.
            var targets = new int[n];
            var payload = new PlaylistTrackEdge[n];
            int k = 0, anchor = -1;
            for (int i = 0; i < at; i++) if (!moving[i]) { anchor = i; targets[k] = before.Targets[i]; payload[k++] = before.Payload[i]; }
            foreach (int i in order) { targets[k] = before.Targets[i]; payload[k++] = before.Payload[i]; }
            for (int i = at; i < n; i++) if (!moving[i]) { targets[k] = before.Targets[i]; payload[k++] = before.Payload[i]; }

            bool same = true;
            for (int i = 0; i < n && same; i++) same = targets[i] == before.Targets[i] && payload[i].ItemId.Value == before.Payload[i].ItemId.Value;
            if (same) { Fail(PlaylistMutationFailure.NoOp, PlaylistEditVerb.Reorder); return false; }

            bool local = IsLocal(p);
            var members = new List<PlaylistMember>(order.Count);
            foreach (int i in order)
            {
                if (!local && before.Payload[i].ItemId.IsEmpty) { Fail(PlaylistMutationFailure.Pending, PlaylistEditVerb.Reorder); return false; }
                members.Add(MemberAt(in before, i));
            }
            if (!local && anchor >= 0 && before.Payload[anchor].ItemId.IsEmpty) { Fail(PlaylistMutationFailure.Pending, PlaylistEditVerb.Reorder); return false; }
            if (!local && !CanWrite(out _, out _)) { Fail(PlaylistMutationFailure.NotSupported, PlaylistEditVerb.Reorder); return false; }

            var scope = Entities.Current;
            scope.Edges.PlaylistTracks.Replace(p.Slot, targets, payload, before.State, before.Total);
            Entities.Publish();
            if (local) return true;

            var op = anchor < 0
                ? new PlaylistOp(PlaylistOpKind.Move, Items: members, ItemsAsKey: true, Anchor: PlaylistMoveAnchor.First)
                : new PlaylistOp(PlaylistOpKind.Move, Items: members, ItemsAsKey: true, Anchor: PlaylistMoveAnchor.AfterItem,
                                 AnchorItemId: Entities.Strings.Resolve(before.Payload[anchor].ItemId),
                                 AnchorUri: new Track(before.Targets[anchor]).Id.Text);
            // A reorder that lost a race is REPORTED (its own sentence), never silently rebased onto a moved list.
            Post(p, scope, op, retry409: false, ok: null,
                 failed: kind => { Restore(scope, p, in before); Fail(kind, PlaylistEditVerb.Reorder); });
            return true;
        }

        // ── list attributes: rename · description · collaborative ─────────────────────────────────────────────────

        /// <summary>Rename (ch 06 §6.1): an empty or unchanged trimmed name is discarded by the caller. The title flips
        /// now; a refusal puts back the title as it stood when editing STARTED (<paramref name="previous"/>).</summary>
        public static void Rename(Playlist p, string name, string previous, Action<bool>? settled)
        {
            if (!Gate(p, PlaylistEditVerb.Rename, settled)) return;
            var scope = Entities.Current;
            WriteTitle(p, name);
            Post(p, scope, new PlaylistOp(PlaylistOpKind.UpdateList, Patch: new PlaylistListPatch(Name: name)), retry409: true,
                 ok: () => settled?.Invoke(true),
                 failed: kind => { if (ReferenceEquals(scope, Entities.Current)) WriteTitle(p, previous); settled?.Invoke(false); Fail(kind, PlaylistEditVerb.Rename); });
        }

        /// <summary>The description; an empty string clears it (0.2.9 wrote <c>null</c>).</summary>
        public static void SetDescription(Playlist p, string description, string previous, Action<bool>? settled)
        {
            if (!Gate(p, PlaylistEditVerb.Generic, settled)) return;
            var scope = Entities.Current;
            WriteDescription(p, description);
            var patch = description.Length == 0 ? new PlaylistListPatch(ClearDescription: true) : new PlaylistListPatch(Description: description);
            Post(p, scope, new PlaylistOp(PlaylistOpKind.UpdateList, Patch: patch), retry409: true,
                 ok: () => settled?.Invoke(true),
                 failed: kind => { if (ReferenceEquals(scope, Entities.Current)) WriteDescription(p, previous); settled?.Invoke(false); Fail(kind, PlaylistEditVerb.Generic); });
        }

        /// <summary>Collaborative on/off — optimistic-only (W17): the ack or a dealer push re-publishes in place.</summary>
        public static void SetCollaborative(Playlist p, bool on, Action<bool>? settled = null)
        {
            if (!Gate(p, PlaylistEditVerb.Generic, settled)) return;
            var scope = Entities.Current;
            WriteCaps(p, PlaylistCaps.IsCollaborative, on);
            Post(p, scope, new PlaylistOp(PlaylistOpKind.UpdateList, Patch: new PlaylistListPatch(Collaborative: on)), retry409: true,
                 ok: () => settled?.Invoke(true),
                 failed: kind => { if (ReferenceEquals(scope, Entities.Current)) WriteCaps(p, PlaylistCaps.IsCollaborative, !on); settled?.Invoke(false); Fail(kind, PlaylistEditVerb.Generic); });
        }

        static bool Gate(Playlist p, PlaylistEditVerb verb, Action<bool>? settled)
        {
            if (p.IsValid && p.EditableMetadata && CanWrite(out _, out _)) return true;
            settled?.Invoke(false);
            if (p.IsValid) Fail(PlaylistMutationFailure.NotSupported, verb);
            return false;
        }

        static void WriteTitle(Playlist p, string name)
        {
            var t = Entities.Current.Playlists;
            t.SetText(ref t.Title, p.Slot, Entities.Intern(Encoding.UTF8.GetBytes(name)));
            t.Bump(p.Slot);
            Entities.Publish();
        }

        static void WriteDescription(Playlist p, string text)
        {
            var t = Entities.Current.Playlists;
            t.SetText(ref t.Description, p.Slot, text.Length == 0 ? StringId.Empty : Entities.Intern(Encoding.UTF8.GetBytes(text)));
            t.Bump(p.Slot);
            Entities.Publish();
        }

        static void WriteCaps(Playlist p, PlaylistCaps bit, bool on)
        {
            var t = Entities.Current.Playlists;
            t.Caps[p.Slot] = on ? (byte)(t.Caps[p.Slot] | (byte)bit) : (byte)(t.Caps[p.Slot] & ~(byte)bit);
            t.Bump(p.Slot);
            Entities.Publish();
        }

        // ── visibility (W17's second switch) ──────────────────────────────────────────────────────────────────────

        public static void SetVisibility(Playlist p, bool isPublic, Action<bool>? settled = null)
        {
            if (!Gate(p, PlaylistEditVerb.Generic, settled)) return;
            var scope = Entities.Current;
            WritePublic(p, isPublic);
            string id = IdOf(p);
            var net = Net;
            if (!net.Run(() =>
                {
                    Api.Result r = Api.PlaylistVisibility(id, isPublic, CancellationToken.None);
                    int status = r.Status;
                    Post(() =>
                    {
                        if (r.Ok) { settled?.Invoke(true); return; }
                        if (ReferenceEquals(scope, Entities.Current)) WritePublic(p, !isPublic);
                        settled?.Invoke(false);
                        Fail(PlaylistEditErrorKinds.KindOfStatus(status), PlaylistEditVerb.Generic);
                    });
                }))
            { WritePublic(p, !isPublic); settled?.Invoke(false); Fail(PlaylistMutationFailure.Unknown, PlaylistEditVerb.Generic); }
        }

        static void WritePublic(Playlist p, bool isPublic)
        {
            var t = Entities.Current.Playlists;
            uint flags = t.Flags[p.Slot];
            t.Flags[p.Slot] = isPublic ? flags | (uint)PlaylistFlags.Public : flags & ~(uint)PlaylistFlags.Public;
            t.Bump(p.Slot, (uint)PlaylistFields.Visibility);
            Entities.Publish();
        }

        // ── the invite link (W17's CTA) ───────────────────────────────────────────────────────────────────────────

        /// <summary>Mint a contributor invite; <paramref name="done"/> gets the link (<c>{share url}?pt={token}</c>) or null.
        /// A non-collaborative list is made collaborative first (0.2.9 EnsureCollaborativeForInvite).</summary>
        public static void CreateInvite(Playlist p, Action<string?> done)
        {
            if (!p.IsValid || !p.Live || !CanWrite(out _, out string username)) { done(null); return; }
            if (!p.IsCollaborative && p.EditableMetadata) SetCollaborative(p, true);
            string id = IdOf(p);
            string share = !p.ShareUrlId.IsEmpty ? Entities.Strings.Resolve(p.ShareUrlId) : "https://open.spotify.com/playlist/" + id;
            _ = username;
            if (!Net.Run(() =>
                {
                    Api.Result r = Api.PlaylistInvite(id, InviteTtlMs, CancellationToken.None);
                    string? token = r.Ok ? JsonString(r.Body, "token") : null;
                    int status = r.Status;
                    Post(() =>
                    {
                        if (token is { Length: > 0 }) { done(share + "?pt=" + Uri.EscapeDataString(token)); return; }
                        done(null);
                        Fail(r.Ok ? PlaylistMutationFailure.Unknown : PlaylistEditErrorKinds.KindOfStatus(status), PlaylistEditVerb.Generic);
                    });
                }))
                done(null);
        }

        static string? JsonString(byte[] body, string name)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(name, out var v)
                       && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            }
            catch (JsonException) { return null; }
        }

        // ── delete (W18): the rootlist REM, then home ─────────────────────────────────────────────────────────────

        /// <summary>Remove the playlist from the account's rootlist (a delete IS an unfollow of one's own list), navigate
        /// home, and put the row back if the server refuses. The caller has already confirmed.</summary>
        public static void Delete(Playlist p)
        {
            if (!p.IsValid || !p.IsOwner || !CanWrite(out var scope, out string username)) { Fail(PlaylistMutationFailure.NotSupported, PlaylistEditVerb.Generic); return; }
            var me = new User(scope.MeSlot);
            var entries = new List<RootlistEntry>();
            Encode.RootlistEntries(me, entries);
            string uri = p.Uri.Text;
            if (Encode.RootlistRemove(entries, uri) is not { } op) { Fail(PlaylistMutationFailure.Deleted, PlaylistEditVerb.Generic); return; }
            Encode.LandRootlist(me, Encode.ApplyLocally(entries, [op]));
            Entities.Publish();
            Actions.Services.Go?.Invoke(new Shell.Route(Shell.RouteKind.Home));

            string revisionText = Entities.Strings.Resolve(scope.Edges.RootlistRevision(scope.MeSlot));
            var net = Net;
            long now = NowMs();
            if (!net.Run(() =>
                {
                    Span<byte> head = stackalloc byte[Encode.MaxRevisionBytes];
                    int length = Encode.RevisionBytes(revisionText, head);
                    if (length == 0)
                    {
                        Api.Result read = net.Rootlist(username);
                        length = read.Ok ? Encode.ResultingRevision(read.Bytes, head) : 0;
                    }
                    Api.Result r = length == 0 ? Api.Result.Transport
                        : net.RootlistChanges(username, Encode.RootlistChanges(head[..length], [op], username, now, Encode.NewNonce()));
                    int status = r.Status;
                    bool ok = r.Ok;
                    Post(() =>
                    {
                        if (!ReferenceEquals(scope, Entities.Current)) return;
                        Entities.RefreshEdge(FetchEdge.Rootlist, scope.MeSlot);   // converge either way: the reply names no head we keep
                        if (!ok) Fail(PlaylistEditErrorKinds.KindOfStatus(status), PlaylistEditVerb.Generic);
                    });
                }))
                Entities.RefreshEdge(FetchEdge.Rootlist, scope.MeSlot);
        }

        // ── tune (W24): POST /signals, the reply IS the rebuilt list ──────────────────────────────────────────────

        public static void Tune(Playlist p, string identifier, Action<bool>? settled)
        {
            if (!p.IsValid || identifier.Length == 0 || !CanWrite(out var scope, out _)) { settled?.Invoke(false); Fail(PlaylistMutationFailure.NotSupported, PlaylistEditVerb.Generic); return; }
            string id = IdOf(p), uri = p.Uri.Text;
            if (!Net.Run(() =>
                {
                    Span<byte> head = stackalloc byte[Encode.MaxRevisionBytes];
                    Api.Result read = Api.Playlist(id, CancellationToken.None);
                    int length = read.Ok ? Encode.ResultingRevision(read.Bytes, head) : 0;
                    Api.Result r = length == 0 ? read : Api.PlaylistSignals(id, Api.SignalsBody(head[..length], identifier), CancellationToken.None);
                    Staging? staging = null;
                    if (length > 0 && r.Ok && r.Body.Length > 0)
                    {
                        staging = Staging.Rent();
                        staging.Epoch = scope.Epoch;
                        Api.PlaylistAnswer(r.Bytes, uri, staging);
                    }
                    Post(() =>
                    {
                        bool ok = staging is not null;
                        if (ok && ReferenceEquals(scope, Entities.Current)) { Entities.Commit(staging!); Entities.Publish(); }
                        if (staging is not null) Staging.Return(staging);
                        settled?.Invoke(ok);
                        Notify.Say(Loc.Get(ok ? Strings.Detail.Tuning.Applied : Strings.Detail.Tuning.ApplyFailed),
                                   ok ? InfoBarSeverity.Success : InfoBarSeverity.Error, dedupeKey: "playlist.tune");
                    });
                }))
                settled?.Invoke(false);
        }

        // ── cover (W16): two hops, then the header re-read (the cover changes only once the server agrees) ────────

        public static void UploadCover(Playlist p, string jpegPath, Action<bool>? settled)
        {
            if (!Gate(p, PlaylistEditVerb.Generic, settled)) return;
            var scope = Entities.Current;
            string id = IdOf(p), uri = p.Uri.Text;
            if (!Net.Run(() =>
                {
                    int status = 0;
                    Staging? staging = null;
                    try
                    {
                        byte[] jpeg = File.ReadAllBytes(jpegPath);
                        Api.Result up = Api.CoverUpload(jpeg, CancellationToken.None);
                        string? token = up.Ok ? JsonString(up.Body, "uploadToken") : null;
                        Api.Result reg = token is { Length: > 0 } ? Api.CoverRegister(id, token, CancellationToken.None) : up;
                        status = reg.Status;
                        Api.Result read = reg.Ok && token is not null ? Api.Playlist(id, CancellationToken.None) : default;
                        if (reg.Ok && token is not null && read.Ok)
                        {
                            staging = Staging.Rent();
                            staging.Epoch = scope.Epoch;
                            Api.PlaylistAnswer(read.Bytes, uri, staging);
                        }
                        else if (reg.Ok && token is not null) status = 200;
                    }
                    catch (IOException) { status = -1; }
                    catch (UnauthorizedAccessException) { status = -1; }
                    Post(() =>
                    {
                        if (staging is not null)
                        {
                            if (ReferenceEquals(scope, Entities.Current)) { Entities.Commit(staging); Entities.Publish(); }
                            Staging.Return(staging);
                        }
                        bool ok = status is >= 200 and < 300;
                        settled?.Invoke(ok);
                        if (!ok) Fail(PlaylistEditErrorKinds.KindOfStatus(status), PlaylistEditVerb.Generic);
                    });
                }))
                settled?.Invoke(false);
        }

        // ── recommendations (W10): extend, add ────────────────────────────────────────────────────────────────────

        static readonly Dictionary<int, HashSet<string>> s_skip = new();
        static Scope? s_skipScope;

        /// <summary>Ask the extender for <see cref="RecBatch"/> tracks that are neither members nor already offered, and land
        /// them on <c>Edges.PlaylistRecs</c>. The relation's fetch marks carry loading / failed.</summary>
        public static void Extend(Playlist p)
        {
            var scope = Entities.Current;
            var recs = scope.Edges.PlaylistRecs;
            if (!p.IsValid || IsLocal(p) || !CanWrite(out _, out _)) return;
            if (!ReferenceEquals(s_skipScope, scope)) { s_skip.Clear(); s_skipScope = scope; }
            if (!s_skip.TryGetValue(p.Slot, out var skip)) s_skip[p.Slot] = skip = new HashSet<string>(StringComparer.Ordinal);
            foreach (int t in p.TrackSlots) AddSkip(skip, t);
            foreach (int t in p.RecommendationSlots) AddSkip(skip, t);
            var skipIds = new List<string>(skip);
            string uri = p.Uri.Text;
            recs.MarkAsked(p.Slot, 0);
            if (!Net.Run(() =>
                {
                    Api.Result r = Api.PlaylistExtend(uri, skipIds, RecBatch, CancellationToken.None);
                    var uris = new List<string>(RecBatch);
                    Staging? staging = null;
                    if (r.Ok && r.Body.Length > 0)
                    {
                        staging = Staging.Rent();
                        staging.Epoch = scope.Epoch;
                        staging.Authority = Authority.Thin;
                        Decode.PlaylistExtender(r.Bytes, staging, uris);
                    }
                    int status = r.Status;
                    Post(() => Extended(scope, p, staging, uris, status));
                }))
                recs.MarkFailed(p.Slot, 0, 0);

            static void AddSkip(HashSet<string> skip, int trackSlot)
            {
                if (trackSlot <= Table.None) return;
                var id = new Track(trackSlot).Id;
                if (id.Provider == EntityProvider.Spotify) skip.Add(new string(EntityUri.IdOf(id.Text.AsSpan())));
            }
        }

        static void Extended(Scope scope, Playlist p, Staging? staging, List<string> uris, int status)
        {
            if (!ReferenceEquals(scope, Entities.Current)) { if (staging is not null) Staging.Return(staging); return; }
            if (staging is null) { scope.Edges.PlaylistRecs.MarkFailed(p.Slot, 0, status); Entities.Publish(); return; }
            Entities.Commit(staging);
            Staging.Return(staging);
            var slots = new int[uris.Count];
            for (int i = 0; i < uris.Count; i++) slots[i] = scope.Tracks.Slot(uris[i].AsSpan());
            scope.Edges.PlaylistRecs.MarkAnswered(p.Slot);
            p.ApplyRecommendations(slots);
            Entities.Publish();
        }

        /// <summary>The rec row's [+]: an ADD add_last with a client item id; the card stays until the write is confirmed,
        /// then leaves the batch (which refills when it empties — the section's own effect).</summary>
        public static void AddRecommendation(Playlist p, Track track, Action<bool>? settled)
        {
            if (!p.IsValid || !track.IsValid || !p.Editable || !CanWrite(out var scope, out _))
            { settled?.Invoke(false); Fail(PlaylistMutationFailure.NotSupported, PlaylistEditVerb.Add); return; }
            var member = new PlaylistMember(track.Id.Text, Api.NewItemId(), NowMs());
            int trackSlot = track.Slot;
            Post(p, scope, Encode.AppendTracks([member]), retry409: true,
                 ok: () =>
                 {
                     settled?.Invoke(true);
                     if (!ReferenceEquals(scope, Entities.Current)) return;
                     var recs = scope.Edges.PlaylistRecs.Targets(p.Slot).ToArray();
                     p.ApplyRecommendations(Array.FindAll(recs, s => s != trackSlot));
                     Entities.RefreshEdge(FetchEdge.PlaylistTracks, p.Slot);
                     Entities.Publish();
                 },
                 failed: kind => { settled?.Invoke(false); Fail(kind, PlaylistEditVerb.Add); });
        }

        // ── deposits at a slot, and the cross-playlist move ───────────────────────────────────────────────────────

        /// <summary>Add tracks at a PRE-move insertion slot (<paramref name="at"/> null = append): ONE ADD with a
        /// client-minted item id per row (a046), then "Added to {name}", the MRU remember and a membership refresh.</summary>
        public static void AddTracks(Playlist p, IReadOnlyList<Track> tracks, int? at, Action<bool>? settled = null)
        {
            if (!p.IsValid || !p.Editable || !CanWrite(out var scope, out _)) { settled?.Invoke(false); Fail(PlaylistMutationFailure.NotSupported, PlaylistEditVerb.Add); return; }
            long now = NowMs();
            var members = new List<PlaylistMember>(tracks.Count);
            for (int i = 0; i < tracks.Count; i++)
                if (tracks[i].IsValid && tracks[i].Id.Provider == EntityProvider.Spotify) members.Add(new PlaylistMember(tracks[i].Id.Text, Api.NewItemId(), now));
            if (members.Count == 0)
            {
                settled?.Invoke(false);
                Notify.Say(Loc.Get("library.nothingToAdd"), InfoBarSeverity.Informational, dedupeKey: "library.nothing-to-add");
                return;
            }
            string name = Entities.Strings.Resolve(p.TitleId), uri = p.Uri.Text;
            var op = at is { } slot && slot < p.TrackSlots.Length
                ? new PlaylistOp(PlaylistOpKind.Add, FromIndex: Math.Max(0, slot), Items: members)
                : Encode.AppendTracks(members);
            Post(p, scope, op, retry409: at is null,
                 ok: () =>
                 {
                     settled?.Invoke(true);
                     Playlist.RememberDeposit(uri);
                     Notify.Say(Loc.Format("detail.addedToPlaylist", ("name", name)), InfoBarSeverity.Success);
                 },
                 failed: kind => { settled?.Invoke(false); Fail(kind, PlaylistEditVerb.Add); });
        }

        /// <summary><c>Track.MenuSeams.MoveRows</c>: add to the target, THEN remove from the source — a failed add leaves
        /// the source untouched (Spotify has no cross-playlist move). A default target is "a new playlist": that path
        /// copies (the create seam reports no completion to remove against — see the report).</summary>
        public static void MoveToPlaylist(PlaylistHost host, Actions.Menu.DepositTarget target, IReadOnlyList<Track> tracks)
        {
            var scope = Entities.Current;
            if (!scope.Playlists.TryGetSlot(host.Playlist.Id, out int sourceSlot)) return;
            var source = new Playlist(sourceSlot);
            var rows = host.Rows;
            if (!target.Uri.IsValid)
            {
                var payload = new DragPayload(DragKind.Track, "", "", "", Tracks: [.. tracks]);
                Sidebar.LibraryWrites?.CreatePlaylistWith?.Invoke(payload);
                return;
            }
            var destination = Entities.Playlist(target.Uri);
            AddTracks(destination, tracks, null, ok => { if (ok && ReferenceEquals(scope, Entities.Current)) RemoveRows(source, rows); });
        }

        // ── the one /changes runner ───────────────────────────────────────────────────────────────────────────────

        /// <summary>API THREAD for the work, UI thread for the verdict: read the head, POST the op, retry ONCE on 409 when
        /// the op is keyed-exact. <paramref name="ok"/> then a membership refresh; <paramref name="failed"/> gets the kind.</summary>
        static void Post(Playlist p, Scope scope, PlaylistOp op, bool retry409, Action? ok, Action<PlaylistMutationFailure> failed)
        {
            if (!CanWrite(out _, out string username)) { failed(PlaylistMutationFailure.NotSupported); return; }
            string id = IdOf(p);
            int slot = p.Slot;
            long now = NowMs();
            var net = Net;
            if (net.Run(() =>
                {
                    Span<byte> head = stackalloc byte[Encode.MaxRevisionBytes];
                    int status = 0;
                    for (int attempt = 0; attempt < (retry409 ? 2 : 1); attempt++)
                    {
                        Api.Result read = net.Playlist(id);
                        int length = read.Ok ? Encode.ResultingRevision(read.Bytes, head) : 0;
                        if (length == 0) { status = read.Status; break; }
                        Api.Result r = net.PlaylistChanges(id, Encode.PlaylistChanges(head[..length], [op], username, now, Encode.NewNonce()));
                        status = r.Status;
                        if (r.Ok || status != 409) break;
                    }
                    int final = status;
                    Spotify.Post(() =>
                    {
                        if (final is >= 200 and < 300)
                        {
                            ok?.Invoke();
                            if (ReferenceEquals(scope, Entities.Current)) Entities.RefreshEdge(FetchEdge.PlaylistTracks, slot);
                        }
                        else failed(PlaylistEditErrorKinds.KindOfStatus(final));
                    });
                }))
                return;
            failed(PlaylistMutationFailure.Unknown);
        }

        static void Post(Action work) => Spotify.Post(work);
    }
}
