// ── Spotify/Spotify.Api.Library.cs ──────────────────────────────────────────────────────────────────────────────────
// the playlist4 lists (playlist, rootlist, recents), the collection, and the edge half of the fetch provider
//
// Role: SHELL (the sends, the edge walk) + CORE (the route values, the diff verdict, the bodies)
// Owner: F
// Wave: gap batch B1b (G-042 routes, G-043 CollectionAdd/Remove, G-054 zstd)
// Budget: 620 lines
// Spec: gap register G-042/G-043/G-054 — a named partial of Spotify.Api.cs, which this would have taken past its budget
//
// THE LIST ROUTES, AS CAPTURED. `Spotify.Build`'s kinds for these (owner D) predate the captures and miss what the
// gateway keys on — the playlist read's `decorate` list, the `/diff` query's `handlesContent` and `hint_revision`, the
// recents diff's revision. So these routes are VALUES built here (`Route`), pure and pinned by `SpotifyApiTests`, and
// the runner (`Send(in Route …)`) sends them with the same header stamping and the same single 401 retry:
//
//     GET /playlist/v2/playlist/<id>?decorate=revision,attributes,length,owner,capabilities,picture
//     GET /playlist/v2/user/<u>/rootlist?decorate=…                         the rootlist is a playlist of playlists
//     GET /playlist/v2/list/recents/page                                    CAwQAQ== · apply-lenses · list-items
//     GET <any of the three>/diff?revision=<n%2Chex>&handlesContent=&hint_revision=<n%2Chex>
//                                                                           (recents diff: CAEQAQ== · applied-lenses)
//
// THE HELD REVISION (G-042). An edge batch carries the revision each parent's list was last answered at
// (`FetchBatch.Revisions`, filled on the UI thread). With one, the read is the /diff and it branches like 0.2.9's
// `PlaylistFetcher`/`RecentsFetcher`: 304, `up_to_date`, or a diff with NO ops → unchanged (nothing staged, the list
// stands); a diff WITH ops, OR `changes_require_resync` (field 20 — "cannot be expressed as a diff against our
// base, refetch instead"; bug A1's Eurodance Mix regression was this flag's `contents` block trusted as a real
// snapshot) → the full read (0.3 has no op replayer; a full read converges either way); `contents` in the answer
// with NEITHER of those set → that answer; 509 (a revision too stale) or any other status → the full read.
//
// ZSTD (G-054). The list reads and the playlist mutations can answer `Content-Encoding: zstd`, which .NET's automatic
// decompression does not cover. The frame magic `28 B5 2F FD` IS the guard (the runner never sees the header), and the
// decode is ZstdSharp's, one-shot first and streamed when the frame states no content size — 0.2.9's `SpotifyZstd`.
//
// THE COLLECTION (gap-fix "library delta sync"). `CollectionEdge` tries a collection-v2 DELTA first
// (`TryCollectionDelta`) against a token+count ledger persisted in `Store`'s meta table, and falls back — on any
// doubt at all — to the full paged walk (`CollectionFullWalk`), which now streams each page straight into the staged
// run (`Decode.LibraryPageItems`) instead of buffering every page's body until the last one lands. The shared
// `collection` set lands BOTH of its relations (liked tracks, saved albums) from the one walk that paid for either.
// See the ledger section at the end of this file for the invariant that makes the delta safe.

using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Col = Wavee.Protocol.Collection;

namespace Wavee;

public static partial class Spotify
{
    public static partial class Api
    {
        // ── 1. the route value ───────────────────────────────────────────────────────────────────────────────────────

        /// <summary>A request as a VALUE for the routes <c>Spotify.Build</c> has no captured spelling for: verb, host,
        /// path (query included), headers, the <see cref="RequestKind"/> whose media type the Accept takes, the playlist
        /// sync reason, and whether the answer may be a zstd frame. Pure builders return it; a test asserts it.</summary>
        public readonly record struct Route(Verb Verb, ApiHost Host, string Path, HeaderSet Headers,
            RequestKind Kind = RequestKind.Custom, string SyncReason = "", bool Zstd = false);

        /// <summary>The three playlist4 lists the provider reads.</summary>
        public enum ListKind : byte { Playlist, Rootlist, Recents }

        /// <summary>What a list answer says about the list the caller holds.</summary>
        public enum ListDiff : byte
        {
            /// <summary>304, <c>up_to_date</c>, a diff with no ops, or nothing actionable: the held list stands.</summary>
            Unchanged,
            /// <summary>A diff WITH ops (the list moved), OR <c>changes_require_resync</c> (field 20: "the accepted
            /// delta cannot be expressed as a diff against our base — do NOT advance the stored revision in place,
            /// refetch instead") — either way a full read converges. <see cref="ReadList"/> is what actually issues
            /// it; this verdict alone is just "do not trust this body".</summary>
            Changed,
            /// <summary>The answer carries <c>contents</c>: decode it as the list.</summary>
            Contents,
        }

        const string ListDecorations = "?decorate=revision,attributes,length,owner,capabilities,picture";
        const HeaderSet RecentsHeaders = CommonProtobuf | HeaderSet.ApplyLenses | HeaderSet.AcceptListItems;

        /// <summary>The FULL read of a list. <paramref name="id"/> is the playlist id or the username; recents has none.
        /// PURE.</summary>
        public static Route ListRoute(ListKind kind, string id) => kind switch
        {
            ListKind.Recents => new Route(Verb.Get, ApiHost.Spclient, "/playlist/v2/list/recents/page", RecentsHeaders,
                RequestKind.RecentsPage, "CAwQAQ==", Zstd: true),
            ListKind.Rootlist => new Route(Verb.Get, ApiHost.Spclient,
                "/playlist/v2/user/" + Escaped(id) + "/rootlist" + ListDecorations, CommonProtobuf, RequestKind.RootlistRead, Zstd: true),
            _ => new Route(Verb.Get, ApiHost.Spclient,
                "/playlist/v2/playlist/" + Escaped(id) + ListDecorations, CommonProtobuf, RequestKind.PlaylistRead, Zstd: true),
        };

        /// <summary>The revision-gated /diff read of a list. <paramref name="revision"/> is the wire spelling
        /// <c>{counter},{hex}</c> (<see cref="FormatRevision"/>); it rides the query string TWICE and escaped, so the
        /// comma leaves as <c>%2C</c> — an unescaped one is a 509. PURE.</summary>
        public static Route ListDiffRoute(ListKind kind, string id, string revision)
        {
            string gate = "?revision=" + Escaped(revision) + "&handlesContent=&hint_revision=" + Escaped(revision);
            return kind switch
            {
                ListKind.Recents => new Route(Verb.Get, ApiHost.Spclient, "/playlist/v2/list/recents/page/diff" + gate,
                    RecentsHeaders | HeaderSet.AppliedLenses, RequestKind.RecentsDiff, "CAEQAQ==", Zstd: true),
                ListKind.Rootlist => new Route(Verb.Get, ApiHost.Spclient,
                    "/playlist/v2/user/" + Escaped(id) + "/rootlist/diff" + gate, CommonProtobuf, RequestKind.PlaylistDiff, Zstd: true),
                _ => new Route(Verb.Get, ApiHost.Spclient,
                    "/playlist/v2/playlist/" + Escaped(id) + "/diff" + gate, CommonProtobuf, RequestKind.PlaylistDiff, Zstd: true),
            };
        }

        /// <summary>Read a <c>SelectedListContent</c> for the one question a revalidation asks (see the header). PURE:
        /// <c>changes_require_resync</c> (20) wins outright — the server is saying this body is not a trustworthy
        /// snapshot, full stop, so no other field gets a vote; then <c>up_to_date</c> (10); then a <c>diff</c> (6) —
        /// no ops is unchanged, ops is changed; then <c>contents</c> (5); anything else, the empty body included,
        /// is unchanged.</summary>
        public static ListDiff DiffVerdict(ReadOnlySpan<byte> selectedListContent)
        {
            bool upToDate = false, diff = false, ops = false, contents = false, resyncRequired = false;
            var r = new Decode.ProtoReader(selectedListContent);
            while (r.Next())
            {
                if (r.Field == 5 && r.Wire == 2) { r.Skip(); contents = true; }
                else if (r.Field == 10 && r.Wire == 0) upToDate |= r.Bool();
                else if (r.Field == 6 && r.Wire == 2)
                {
                    diff = true;
                    var d = r.Message();                               // Diff { from_revision = 1, ops = 2, to_revision = 3 }
                    while (d.Next())
                    {
                        if (d.Field == 2 && d.Wire == 2) ops = true;
                        d.Skip();
                    }
                }
                // changes_require_resync (playlist4_external.proto:225): "the accepted delta cannot be expressed
                // as a diff against our base — do NOT advance the stored revision in place, refetch instead."
                // Regression: a diff response can set this AND still attach a `contents` block, which `ReadList`
                // used to hand straight to the decoder as if it were a trustworthy full-read body (bug A1's
                // Eurodance Mix defect — a stale/zeroed `length` overwriting a real count). Never reached before
                // this fix: the decoder's own `resyncRequired` guard (Spotify.Decode.cs's `PlaylistRevision`) is
                // now defence in depth, not the only line.
                else if (r.Field == 20 && r.Wire == 0) resyncRequired |= r.Bool();
                else r.Skip();
            }
            if (resyncRequired) return ListDiff.Changed;   // ignore every other field — ReadList falls through to the full read
            if (upToDate) return ListDiff.Unchanged;
            if (diff) return ops ? ListDiff.Changed : ListDiff.Unchanged;
            return contents ? ListDiff.Contents : ListDiff.Unchanged;
        }

        /// <summary>A <c>SelectedListContent</c>'s own revision (field 1) in its wire spelling, written into
        /// <paramref name="into"/>; 0 when it carries none. PURE.</summary>
        public static int RevisionOf(ReadOnlySpan<byte> selectedListContent, Span<char> into)
            => FormatRevision(new Decode.ProtoReader(selectedListContent).Bytes(1), into);

        /// <summary>A revision on the wire is <c>{counter},{hex}</c> — the 4-byte big-endian counter, a comma, and the
        /// 20-byte hash as lowercase hex. It goes into a QUERY STRING, so the comma must be percent-encoded or the
        /// gateway answers 509; the diff route's escape is what guarantees that.</summary>
        public static int FormatRevision(ReadOnlySpan<byte> revision, Span<char> into)
        {
            if (revision.Length < 5) return 0;
            int counter = (revision[0] << 24) | (revision[1] << 16) | (revision[2] << 8) | revision[3];
            if (!counter.TryFormat(into, out int written)) return 0;
            if (written + 1 + (revision.Length - 4) * 2 > into.Length) return 0;
            into[written++] = ',';
            written += Hex.Encode(revision[4..], into[written..]);
            return written;
        }

        // ── 2. zstd (G-054) ──────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Does this body start with a zstd frame's magic? PURE.</summary>
        public static bool IsZstd(ReadOnlySpan<byte> body)
            => body.Length >= 4 && body[0] == 0x28 && body[1] == 0xB5 && body[2] == 0x2F && body[3] == 0xFD;

        /// <summary>A zstd frame → the bytes it wraps; a body that is not one comes back as itself; a frame that does not
        /// decode is null (the caller's "this answer is unreadable", never an empty list). The one-shot decode needs the
        /// frame's content size, which some answers omit — the streamed decode does not.</summary>
        public static byte[]? Unzstd(byte[] body)
        {
            if (!IsZstd(body)) return body;
            try
            {
                using (var oneShot = new ZstdSharp.Decompressor())
                {
                    Span<byte> unwrapped = oneShot.Unwrap(body);
                    if (unwrapped.Length > 0) return unwrapped.ToArray();
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { /* no content size, or multi-frame: stream it */ }
            try
            {
                using var source = new MemoryStream(body);
                using var stream = new ZstdSharp.DecompressionStream(source);
                using var output = new MemoryStream(body.Length * 4);
                stream.CopyTo(output);
                // A frame that decodes to nothing is not an answer these routes send (an empty answer is a 0-byte
                // body, never a framed one): unreadable, like any other frame that does not decode.
                return output.Length == 0 ? null : output.ToArray();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Warn("spotify", "zstd body did not decode: " + ex.GetType().Name);
                return null;
            }
        }

        /// <summary>A MUTATION's answer unwrapped: the status stands whatever the body does (the server accepted or
        /// refused the write), and an undecodable body is simply empty.</summary>
        static Result Unwrapped(in Result result)
            => IsZstd(result.Body) ? result.WithBody(Unzstd(result.Body) ?? Array.Empty<byte>()) : result;

        // ── 3. the edge half of the fetch provider (G-042) ───────────────────────────────────────────────────────────

        /// <summary>A relation batch: each parent's list from the relation's route. Metadata routes ride ONE POST for
        /// every parent (it is a batch endpoint); everything else is one request per parent.</summary>
        static void AnswerEdge(FetchBatch batch, Staging s, ref FetchOutcome outcome)
        {
            FetchEdge edge = batch.Edge;
            int offset = Math.Max(0, batch.Offset);
            if (!ServesEdge(edge, offset)) return;                     // answered without: the door records a vacancy
            FetchRoute route = FetchRoutes.ForEdge(edge, offset);

            if (route.Transport == RouteTransport.Metadata)
            {
                ReadOnlySpan<int> one = [route.Extension];
                MetadataFor(batch, one, s, ref outcome);
                return;
            }

            for (int i = 0; i < batch.Count; i++)
            {
                if (Stale(batch)) return;
                string parent = batch.Uri(i);
                if (parent.Length == 0) continue;
                if (route.Transport == RouteTransport.Pathfinder)
                {
                    // Similar albums are seeded by a TRACK the planner resolved on the UI thread (Fetch.FillRevisions).
                    if (route.Op == PathfinderOp.SimilarAlbums)
                        SimilarAlbumsEdge(parent, i < batch.Revisions.Length ? batch.Revisions[i] : null, s, ref outcome);
                    else AnswerQuery(route.Op, parent, offset, s, ref outcome);
                    continue;
                }
                string? revision = i < batch.Revisions.Length ? batch.Revisions[i] : null;
                switch (route.Rest)
                {
                    case SpclientRoute.Rootlist: RootlistEdge(parent, revision, s, ref outcome); break;
                    case SpclientRoute.Recents: RecentsEdge(parent, revision, s, ref outcome); break;
                    case SpclientRoute.CollectionPage: CollectionEdge(edge, parent, i, batch, s, ref outcome); break;
                    case SpclientRoute.PlaylistRead: PlaylistEdge(parent, revision, s, ref outcome); break;   // revision-gated (B1b gap 8)
                    default: AnswerRest(route.Rest, parent, s, ref outcome); break;
                }
            }
        }

        /// <summary>The revision-gated read (see the header): the list's bytes to decode, or null when the held list
        /// stands or the read failed (the outcome holds which).</summary>
        static byte[]? ReadList(ListKind kind, string id, string? revision, ref FetchOutcome outcome)
        {
            CancellationToken ct = CancellationToken.None;
            if (revision is { Length: > 0 })
            {
                Result diff = Send(ListDiffRoute(kind, id, revision), [], ct);
                if (diff.NotModified) { outcome.Note(in diff); return null; }
                if (diff.Ok)
                {
                    ListDiff verdict = DiffVerdict(diff.Body);
                    if (verdict != ListDiff.Changed)
                    {
                        outcome.Note(in diff);
                        return verdict == ListDiff.Contents ? diff.Body : null;
                    }
                }
                // Ops this client does not replay, a 509 (the revision is too stale), anything else: the full read.
            }
            Result full = Send(ListRoute(kind, id), [], ct);
            outcome.Note(in full);
            if (full.Ok && full.Body.Length > 0) return full.Body;
            if (!full.Ok) Log.Warn("library", "list read failed (" + kind + ", status " + full.Status + ")");
            return null;
        }

        static void RootlistEdge(string meUri, string? revision, Staging s, ref FetchOutcome outcome)
        {
            byte[]? body = ReadList(ListKind.Rootlist, UsernameOf(meUri), revision, ref outcome);
            if (body is not null) Decode.Rootlist(body, Encoding.UTF8.GetBytes(meUri), s);
        }

        static void RecentsEdge(string meUri, string? revision, Staging s, ref FetchOutcome outcome)
        {
            byte[]? body = ReadList(ListKind.Recents, "", revision, ref outcome);
            // 0.2.9's invariant: rows come ONLY from a body that carried contents. Mapping one that did not yields zero
            // items, and staging that is how 1,708 resident rows once became none.
            if (body is null || DiffVerdict(body) != ListDiff.Contents) return;
            Span<char> chars = stackalloc char[128];
            int n = RevisionOf(body, chars);
            Span<byte> ascii = stackalloc byte[128];
            for (int i = 0; i < n; i++) ascii[i] = (byte)chars[i];
            Decode.Recents(body, Encoding.UTF8.GetBytes(meUri), ascii[..n], s);
        }

        /// <summary>How many 300-item pages one library walk may take (60 000 items) before it stops and answers with
        /// what it has — a bound, so a server that never stops handing out tokens cannot pin a worker.</summary>
        public const int MaxCollectionPages = 200;

        // TODO(orchestrator): `Entities/Fetch.cs`'s `FillRevisions` (~line 1022) has to hand this method the ledger's
        // token+count through `batch.Revisions[i]`, the same seam Rootlist/Recents/PlaylistTracks already use — this
        // file cannot make that edit (Fetch.cs is not ours). Add, right after the existing `AlbumSimilar` special case
        // and before the `StringId revision = edge switch { ... }` block:
        //
        //     if (FetchRoutes.LibrarySetOf(edge, out LibraryEdgeKind librarySet))
        //     {
        //         batch.Revisions[i] = Store.MetaGet(Spotify.Api.CollectionMetaKey(Entities.Current.Key.Account, librarySet));
        //         continue;
        //     }
        //
        // That is the WHOLE edit — a plain MetaGet, no table read, safe on the UI thread `FillRevisions` already runs
        // on. Without it `batch.Revisions[i]` stays null for every library edge and `CollectionEdge` below always
        // takes the full-walk path (harmless: "no ledger entry to trust" is this method's own default-safe branch).

        /// <summary>The edge door for one library relation (G-042, gap-fix "library delta sync"): try the collection-v2
        /// DELTA first — <see cref="TryCollectionDelta"/>, opportunistic, never an error path — and fall back to the full
        /// paged walk (<see cref="CollectionFullWalk"/>) on any doubt at all: no ledger entry to trust,
        /// <c>delta_update_possible == false</c>, a token that does not parse, a transport failure. <paramref name="i"/>
        /// is this parent's index into <see cref="FetchBatch.Revisions"/> — resolved on the UI thread, at ask time, by
        /// <c>Fetch.FillRevisions</c> (see the TODO just above for the exact edit that seam needs; not this file's to
        /// make).</summary>
        static void CollectionEdge(FetchEdge edge, string meUri, int i, FetchBatch batch, Staging s, ref FetchOutcome outcome)
        {
            if (!FetchRoutes.LibrarySetOf(edge, out LibraryEdgeKind set)) return;
            string username = UsernameOf(meUri);
            string wire = WireSet(set);

            string? handoff = i >= 0 && i < batch.Revisions.Length ? batch.Revisions[i] : null;
            LedgerEntry stored = LibrarySyncLedger.Decode(handoff);
            if (!stored.IsEmpty && !Stale(batch) && TryCollectionDelta(username, wire, set, stored, ref outcome))
                return;

            CollectionFullWalk(set, meUri, username, wire, batch, s, ref outcome);
        }

        /// <summary>STAGE 2: one collection-v2 <c>/collection/v2/delta</c> instead of the full paged walk. Returns
        /// <c>false</c> — silently, never an error the caller has to handle — for anything short of a clean, appliable
        /// answer: a transport failure, <c>delta_update_possible == false</c>, or a new token that is missing or does
        /// not parse. On a clean answer the actual APPLY (the live table write — adds and removes, C1) is POSTED to the
        /// UI thread: this method, like every other provider entry point, never touches a table
        /// (<see cref="FetchProvider.Start"/>'s contract).</summary>
        static bool TryCollectionDelta(string username, string wire, LibraryEdgeKind set, LedgerEntry stored, ref FetchOutcome outcome)
        {
            byte[] body = new Col.DeltaRequest { Username = username, Set = wire, LastSyncToken = stored.SyncToken }.ToByteArray();
            Result result = CollectionDelta(body, CancellationToken.None);
            if (!result.Ok) return false;                              // fall back to the full walk, quietly

            var items = new List<CollectionDeltaItem>();
            bool possible = Decode.LibraryDelta(result.Body, items, out string newSyncToken);
            if (!LibrarySyncLedger.CanApplyDelta(possible, newSyncToken)) return false;    // fall back, quietly, on any doubt

            int added = 0, removed = 0;
            for (int k = 0; k < items.Count; k++) { if (items[k].Removed) removed++; else added++; }

            outcome.Note(200);
            int baselineCount = stored.ItemCount;
            Spotify.Post(() => Library.ApplyCollectionDelta(set, username, items, baselineCount, added, removed, newSyncToken));
            return true;
        }

        /// <summary>STAGE 1: the full collection-v2 walk, streamed — one page decoded (and staged) as it lands rather
        /// than every page's body held in a <c>List&lt;byte[]&gt;</c> until the last one arrives
        /// (<see cref="Decode.LibraryPageItems"/>). <c>collection</c> is ONE wire set holding two relations (liked
        /// tracks, saved albums); the walk stages both from the same pages, since one pays for the other.
        ///
        /// <para>A walk that reaches a real terminal (an empty <c>next_page_token</c>) is committed and its ledger
        /// entry — <c>(sync token, item count)</c>, the SAME meta write — is persisted, but ONLY when the walk is at
        /// least as large as the last verified count (<see cref="LibrarySyncLedger.WalkIsTrustworthy"/>): a walk that
        /// came back SMALLER than what was last known good is never trusted to advance the ledger, which is exactly
        /// what forces the next sync back to a full walk instead of ever letting a delta build on a doubtful baseline.
        /// A walk that hits <see cref="MaxCollectionPages"/> WITHOUT a real terminal is discarded outright — never
        /// committed, never persisted — because a whole-list rewrite built from a truncated crawl is precisely the
        /// "silently deletes the newest likes" bug this gap-fix exists to close.</para></summary>
        static void CollectionFullWalk(LibraryEdgeKind set, string meUri, string username, string wire, FetchBatch batch,
            Staging s, ref FetchOutcome outcome)
        {
            byte[] meUtf8 = Encoding.UTF8.GetBytes(meUri);
            StagedId parent = Decode.LibraryParent(meUtf8, s);
            if (parent.IsEmpty) return;

            bool isPins = set == LibraryEdgeKind.Pins;
            bool shared = set is LibraryEdgeKind.Liked or LibraryEdgeKind.SavedAlbums;
            LibraryEdgeKind otherSet = set == LibraryEdgeKind.Liked ? LibraryEdgeKind.SavedAlbums : LibraryEdgeKind.Liked;

            Relation relation; EntityKind kind;
            if (isPins) { relation = Relation.Pins; kind = default; }
            else (relation, kind) = Decode.LibraryRelationOf(set);
            EdgeRun run = s.Run(relation);

            EdgeRun otherRun = default;
            EntityKind otherKind = default;
            if (shared)
            {
                (Relation otherRelation, EntityKind otherEntityKind) = Decode.LibraryRelationOf(otherSet);
                otherKind = otherEntityKind;
                otherRun = s.Run(otherRelation);
            }

            string token = "", lastSyncToken = "";
            bool terminal = false;
            for (int page = 0; page < MaxCollectionPages; page++)
            {
                if (Stale(batch)) { run.Discard(); if (shared) otherRun.Discard(); return; }
                Result result = CollectionPage(CollectionPageBody(username, wire, token, CollectionPageSize), CancellationToken.None);
                if (!result.Ok)
                {
                    run.Discard();
                    if (shared) otherRun.Discard();
                    outcome.Note(in result);                           // half a library is not an answer
                    return;
                }
                Decode.LibraryPageItems(result.Body, isPins ? null : kind, s, ref run);
                if (shared) Decode.LibraryPageItems(result.Body, otherKind, s, ref otherRun);
                lastSyncToken = SyncTokenOf(result.Body);
                token = NextPageToken(result.Body);
                if (token.Length == 0) { terminal = true; break; }
            }

            if (!terminal)
            {
                // The page cap fired without a real terminal: a whole-list rewrite from a truncated crawl would delete
                // real rows (0.2.10's production bug this gap-fix exists to close) — commit nothing, trust nothing.
                run.Discard();
                if (shared) otherRun.Discard();
                Log.Warn("library", "collection walk hit the page cap (" + MaxCollectionPages + ") without a terminal page ("
                                     + wire + "); nothing committed this pass");
                outcome.Note(0);
                return;
            }

            int walkedCount = run.Count;
            int otherWalkedCount = shared ? otherRun.Count : 0;
            run.EndEvenIfEmpty(in parent);
            if (shared) otherRun.EndEvenIfEmpty(in parent);
            outcome.Note(200);

            PersistLedgerIfTrustworthy(username, set, walkedCount, lastSyncToken);
            if (shared) PersistLedgerIfTrustworthy(username, otherSet, otherWalkedCount, lastSyncToken);
        }

        /// <summary>Advance ONE relation's sync ledger after a terminal full walk — but only when the walk is
        /// trustworthy against whatever was last verified (<see cref="LibrarySyncLedger.WalkIsTrustworthy"/>); a walk
        /// that looks smaller than the last known-good count leaves the ledger exactly as it was, which is what makes
        /// the NEXT sync fall back to a full walk rather than ever delta-ing off a doubtful baseline.</summary>
        static void PersistLedgerIfTrustworthy(string username, LibraryEdgeKind set, int walkedCount, string syncToken)
        {
            if (syncToken.Length == 0) return;
            string key = CollectionMetaKey(username, set);
            LedgerEntry stored = LibrarySyncLedger.Decode(Store.MetaGet(key));
            if (LibrarySyncLedger.WalkIsTrustworthy(walkedCount, stored))
                Store.MetaSet(key, LibrarySyncLedger.Encode(syncToken, walkedCount));
        }

        // ── 4. playlists, the rootlist and recents ───────────────────────────────────────────────────────────────────

        /// <summary>The 8 bytes a CREATE sends as its base revision: four zeroes and ASCII "root". Never stored.</summary>
        public static ReadOnlySpan<byte> CreateBaseRevision => [0, 0, 0, 0, (byte)'r', (byte)'o', (byte)'o', (byte)'t'];

        /// <summary>A membership item id: 8 random bytes as 16 lowercase hex characters. Minted by the CLIENT, which
        /// is why a freshly added row already has an addressable uid before the server has heard of it.</summary>
        public static string NewItemId()
        {
            Span<byte> bytes = stackalloc byte[8];
            RandomNumberGenerator.Fill(bytes);
            Span<char> hex = stackalloc char[16];
            return new string(hex[..Hex.Encode(bytes, hex)]);
        }

        /// <summary>A client-minted playlist id: 16 random bytes as 22 base62 characters. There is no create ENDPOINT
        /// any more — a create is a `/changes` POST to an id we invented — so the uri exists before the request does.</summary>
        public static string NewPlaylistId()
        {
            Span<byte> bytes = stackalloc byte[16];
            RandomNumberGenerator.Fill(bytes);
            UInt128 value = UInt128.Zero;
            foreach (byte b in bytes) value = (value << 8) | b;
            Span<char> id = stackalloc char[Base62.GidChars];
            return new string(id[..Base62.Encode(value, id)]);
        }

        /// <summary>The playlist's decorated v2 read: header, capabilities and the membership, unwrapped.</summary>
        public static Result Playlist(string playlistId, CancellationToken ct) => Send(ListRoute(ListKind.Playlist, playlistId), [], ct);

        /// <summary>The revision-gated diff. 304 means "you are current"; 509 means the revision is too stale and the
        /// caller must fall back to a full read. A revision too short to spell is the full read.</summary>
        public static Result PlaylistDiff(string playlistId, ReadOnlySpan<byte> revision, CancellationToken ct)
        {
            Span<char> formatted = stackalloc char[128];
            int length = FormatRevision(revision, formatted);
            if (length == 0) return Playlist(playlistId, ct);
            return Send(ListDiffRoute(ListKind.Playlist, playlistId, new string(formatted[..length])), [], ct);
        }

        public static Result PlaylistChanges(string playlistId, byte[] body, CancellationToken ct)
        {
            var args = new RequestArgs { Id = playlistId, Body = body };
            return Unwrapped(Send(RequestKind.PlaylistChanges, args, ct));
        }

        public static Result PlaylistCreate(string playlistId, byte[] body, CancellationToken ct)
        {
            var args = new RequestArgs { Id = playlistId, Body = body };
            return Unwrapped(Send(RequestKind.PlaylistCreate, args, ct));
        }

        public static Result PlaylistSignals(string playlistId, byte[] body, CancellationToken ct)
        {
            var args = new RequestArgs { Id = playlistId, Body = body };
            return Unwrapped(Send(RequestKind.PlaylistSignals, args, ct));
        }

        public static Result Rootlist(string username, CancellationToken ct) => Send(ListRoute(ListKind.Rootlist, username), [], ct);

        public static Result RootlistChanges(string username, byte[] body, CancellationToken ct)
        {
            var args = new RequestArgs { Id = username, Body = body };
            return Unwrapped(Send(RequestKind.RootlistChanges, args, ct));
        }

        public static Result Recents(CancellationToken ct) => Send(ListRoute(ListKind.Recents, ""), [], ct);

        /// <summary>The recents revalidation against the revision the caller holds (<c>{counter},{hex}</c>).</summary>
        public static Result RecentsDiff(string revision, CancellationToken ct) => Send(ListDiffRoute(ListKind.Recents, "", revision), [], ct);

        /// <summary>Upload a playlist cover, hop one. The image service is not one of D's hosts and there is no kind
        /// for it — the ONE absolute url in this file, sent through the same stamping as everything else.</summary>
        public static Result CoverUpload(byte[] jpeg, CancellationToken ct)
        {
            const HeaderSet headers = HeaderSet.Bearer | HeaderSet.ClientToken | HeaderSet.Identity
                | HeaderSet.AcceptJson | HeaderSet.ContentJson;
            return SendAuthed(Verb.Post, ImageUploadHost + "/v4/playlist", headers, RequestKind.Custom, jpeg, "", ct,
                contentType: "image/jpeg");
        }

        /// <summary>Hop two: register the upload token against the playlist.</summary>
        public static Result CoverRegister(string playlistId, string uploadToken, CancellationToken ct)
        {
            var buffer = new System.Buffers.ArrayBufferWriter<byte>(96);
            using (var w = new System.Text.Json.Utf8JsonWriter(buffer))
            {
                w.WriteStartObject();
                w.WriteString("uploadToken", uploadToken);
                w.WriteEndObject();
            }
            Span<char> path = stackalloc char[128];
            var p = new PathWriter(path);
            p.Append("/playlist/v2/playlist/");
            p.AppendEscaped(playlistId);
            p.Append("/register-image");
            var args = new RequestArgs
            {
                Path = p.Written,
                Host = ApiHost.SpclientWg,
                Verb = Verb.Post,
                Headers = CommonJson | HeaderSet.ContentJson,
                Body = buffer.WrittenSpan,
            };
            return Send(RequestKind.Custom, args, ct);
        }

        /// <summary>A collaborator invite link. The permission object MUST be nested — a flat body is a 400.</summary>
        public static Result PlaylistInvite(string playlistId, long ttlMs, CancellationToken ct)
        {
            var buffer = new System.Buffers.ArrayBufferWriter<byte>(96);
            using (var w = new System.Text.Json.Utf8JsonWriter(buffer))
            {
                w.WriteStartObject();
                w.WriteStartObject("permission");
                w.WriteString("permissionLevel", "CONTRIBUTOR");
                w.WriteEndObject();
                w.WriteNumber("ttlMs", ttlMs);
                w.WriteEndObject();
            }
            Span<char> path = stackalloc char[128];
            var p = new PathWriter(path);
            p.Append("/playlist-permission/v1/playlist/");
            p.AppendEscaped(playlistId);
            p.Append("/permission-grant");
            var args = new RequestArgs
            {
                Path = p.Written,
                Host = ApiHost.SpclientWg,
                Verb = Verb.Post,
                Headers = CommonJson | HeaderSet.ContentJson,
                Body = buffer.WrittenSpan,
            };
            return Send(RequestKind.Custom, args, ct);
        }

        /// <summary>Public/private. The body is two bytes: <c>08 01</c> BLOCKED (private), <c>08 02</c> VIEWER.</summary>
        public static Result PlaylistVisibility(string playlistId, bool isPublic, CancellationToken ct)
        {
            Span<char> path = stackalloc char[128];
            var p = new PathWriter(path);
            p.Append("/playlist-permission/v1/playlist/");
            p.AppendEscaped(playlistId);
            p.Append("/permission/base/level");
            ReadOnlySpan<byte> body = isPublic ? [0x08, 0x02] : [0x08, 0x01];
            var args = new RequestArgs
            {
                Path = p.Written,
                Host = ApiHost.Spclient,
                Verb = Verb.Post,
                Headers = CommonProtobuf | HeaderSet.ContentProtobuf,
                Body = body,
            };
            return Send(RequestKind.Custom, args, ct);
        }

        // ── 5. the collection (liked songs, saved albums, followed artists, shows, pins) ─────────────────────────────

        /// <summary>The collection-v2 set name one library edge lives in. The service's own spelling, not ours: four
        /// of the five kinds share the name "collection" and are told apart by the item's uri.</summary>
        public static string WireSet(LibraryEdgeKind set) => set switch
        {
            LibraryEdgeKind.FollowedArtists => "artist",
            LibraryEdgeKind.SavedShows => "show",
            LibraryEdgeKind.Pins => "ylpin",
            _ => "collection",
        };

        /// <summary>The <see cref="Store.MetaSet"/>/<see cref="Store.MetaGet"/> key for one account's one library
        /// relation's sync ledger (<see cref="LibrarySyncLedger"/>, gap-fix "library delta sync"). Keyed by the
        /// RELATION, not <see cref="WireSet"/>'s own name: Liked and SavedAlbums share the wire set "collection" (one
        /// walk pays for both, <see cref="CollectionFullWalk"/>) but each has its own item count, and sharing the meta
        /// key would let one relation's baseline silently stand in for the other's. Never a sqlite column — see
        /// <c>Entities/Store.cs</c>'s "small key/value persistence" section for why a schema change there deletes the
        /// whole `library.db`. PURE.</summary>
        public static string CollectionMetaKey(string username, LibraryEdgeKind set) => "collection_rev:" + username + ":" + set;

        /// <summary>The episodes set has no <see cref="LibraryEdgeKind"/> of its own (Wave 1 folds "listen later" into
        /// the show edges); the wire name is kept here so the page that wants it does not re-invent the string.</summary>
        public const string ListenLaterSet = "listenlater";

        public const int CollectionPageSize = 300;

        public static Result CollectionPage(byte[] body, CancellationToken ct)
        {
            var args = new RequestArgs { Body = body };
            return Send(RequestKind.CollectionPage, args, ct);
        }

        public static Result CollectionDelta(byte[] body, CancellationToken ct)
        {
            var args = new RequestArgs { Body = body };
            return Send(RequestKind.CollectionDelta, args, ct);
        }

        public static Result CollectionWrite(byte[] body, CancellationToken ct)
        {
            var args = new RequestArgs { Body = body };
            return Send(RequestKind.CollectionWrite, args, ct);
        }

        /// <summary><c>PageRequest{ username, set, pagination_token, limit }</c> — the cursor is the previous page's
        /// <c>next_page_token</c>, empty for the first. PURE.</summary>
        public static byte[] CollectionPageBody(string username, string wireSet, string paginationToken, int limit)
            => new Col.PageRequest { Username = username, Set = wireSet, PaginationToken = paginationToken, Limit = limit }
               .ToByteArray();

        /// <summary>A <c>PageResponse</c>'s <c>next_page_token</c> (field 2), or empty on the last page. PURE.</summary>
        public static string NextPageToken(ReadOnlySpan<byte> pageResponse)
        {
            ReadOnlySpan<byte> token = new Decode.ProtoReader(pageResponse).Bytes(2);
            return token.IsEmpty ? "" : Encoding.UTF8.GetString(token);
        }

        /// <summary>A <c>PageResponse</c>'s <c>sync_token</c> — FIELD 3, <see cref="NextPageToken"/>'s twin at field 2.
        /// Empty when the page carried none (an older page, or the field genuinely absent), never garbage: this is the
        /// value the walk's ledger persists (gap-fix "library delta sync", stage 1). PURE.</summary>
        public static string SyncTokenOf(ReadOnlySpan<byte> pageResponse)
        {
            ReadOnlySpan<byte> token = new Decode.ProtoReader(pageResponse).Bytes(3);
            return token.IsEmpty ? "" : Encoding.UTF8.GetString(token);
        }

        /// <summary><c>WriteRequest{ username, set, items[]{ uri, added_at (UNIX SECONDS), is_removed = !saved },
        /// client_update_id }</c> — 0.2.9's <c>CollectionWriteMapper</c>, unzipped. Empty uris are skipped; nothing to
        /// write is an empty body. PURE (the clock and the update id are the caller's).</summary>
        public static byte[] CollectionWriteBody(string username, LibraryEdgeKind set, ReadOnlySpan<string> uris, bool saved,
            long addedAtSeconds, string clientUpdateId)
        {
            var request = new Col.WriteRequest { Username = username, Set = WireSet(set), ClientUpdateId = clientUpdateId };
            int at = (int)Math.Clamp(addedAtSeconds, 0, int.MaxValue);
            foreach (string uri in uris)
                if (uri.Length > 0) request.Items.Add(new Col.CollectionItem { Uri = uri, AddedAt = at, IsRemoved = !saved });
            return request.Items.Count == 0 ? [] : request.ToByteArray();
        }

        /// <summary>Save <paramref name="uris"/> into a library set (like, save, follow, pin) — one write, stamped now,
        /// with a fresh client update id. BLOCKS. Nothing to write is a 204, not a request.</summary>
        public static Result CollectionAdd(string username, LibraryEdgeKind set, ReadOnlySpan<string> uris, CancellationToken ct)
            => CollectionWriteNow(username, set, uris, saved: true, ct);

        /// <summary>Remove <paramref name="uris"/> from a library set (unlike, unsave, unfollow, unpin). BLOCKS.</summary>
        public static Result CollectionRemove(string username, LibraryEdgeKind set, ReadOnlySpan<string> uris, CancellationToken ct)
            => CollectionWriteNow(username, set, uris, saved: false, ct);

        static Result CollectionWriteNow(string username, LibraryEdgeKind set, ReadOnlySpan<string> uris, bool saved,
            CancellationToken ct)
        {
            byte[] body = CollectionWriteBody(username, set, uris, saved, DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Guid.NewGuid().ToString("N"));
            return body.Length == 0 ? new Result(204, []) : CollectionWrite(body, ct);
        }
    }
}

// ── the sync ledger (gap-fix "library delta sync", stage 2's safety mechanism) ──────────────────────────────────────
//
// THE INVARIANT. A delta is a request to TRUST a small answer instead of paying for a full crawl, and trusting it is
// safe only when three things independently hold: the server said the delta was possible, the token it hands back
// parses, and the list the delta would be applied to has not moved since the baseline that token was captured
// against. After applying, the result must reconcile EXACTLY with what the delta's own items said would happen, or
// the apply is worthless and is discarded by simply never persisting it — the next sync re-derives the whole list
// from the server instead. On any doubt, anywhere in this chain, the answer is "do a full walk", never "guess and
// maybe drop a row": 0.2.10 carried `CollectionSnapshotLedger` / `CollectionDrift` for exactly this reason (a
// truncated snapshot once deleted a user's newest likes and then stored the current server revision, so no later
// delta ever re-shipped them). This is that lesson's shape — engine-free, unit-tested, no Staging, no tables.
//
// PERSISTED AS ONE STRING (`Store.MetaSet`/`MetaGet`; a `meta` row, never a sqlite column): `"<item count>|<token>"`.
// The count travels WITH the token in the SAME write, which is the whole point — a stored token with no count, or a
// count that no longer matches the live list, can never be trusted for a delta, by construction.

/// <summary>One persisted (or in-transit) ledger entry: a sync token AND the item count it was captured alongside.
/// <see cref="Empty"/> is "nothing has ever verified this list" — no walk, no delta.</summary>
public readonly record struct LedgerEntry(string SyncToken, int ItemCount)
{
    public static readonly LedgerEntry Empty = new("", -1);

    /// <summary>True for <see cref="Empty"/> and for anything <see cref="LibrarySyncLedger.Decode"/> could not parse.</summary>
    public bool IsEmpty => ItemCount < 0;
}

/// <summary>The pure decision surface behind the library sync ledger (stage 1's truncated-walk guard AND stage 2's
/// delta precondition/postcondition) — see the section header for the invariant. No Staging, no tables, no network:
/// every branch here is a value in, a value out, which is what makes it unit-testable without the engine
/// (<c>LibrarySyncTests.cs</c>).</summary>
public static class LibrarySyncLedger
{
    const char Sep = '|';

    /// <summary>The one string <see cref="Store.MetaSet"/> stores for a (scope, relation) pair. PURE.</summary>
    public static string Encode(string syncToken, int itemCount)
        => itemCount.ToString(System.Globalization.CultureInfo.InvariantCulture) + Sep + (syncToken ?? "");

    /// <summary>The inverse of <see cref="Encode"/>. Never throws: anything that does not parse — null, empty, no
    /// separator, a non-numeric or negative count — decodes as <see cref="LedgerEntry.Empty"/>, exactly as if nothing
    /// had ever been stored. PURE.</summary>
    public static LedgerEntry Decode(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return LedgerEntry.Empty;
        int sep = stored.IndexOf(Sep);
        if (sep < 0) return LedgerEntry.Empty;
        if (!int.TryParse(stored.AsSpan(0, sep), System.Globalization.NumberStyles.None,
                           System.Globalization.CultureInfo.InvariantCulture, out int count) || count < 0)
            return LedgerEntry.Empty;
        return new LedgerEntry(stored[(sep + 1)..], count);
    }

    /// <summary>Is the live list still exactly what <paramref name="stored"/> was captured against? The delta
    /// precondition (stage 2): on any mismatch — nothing ever stored included — the caller must fall back to a full
    /// walk rather than attempt an incremental apply. A local optimistic like/unlike since the ledger was last written
    /// is exactly this kind of doubt, and is meant to force the fallback. PURE.</summary>
    public static bool BaselineMatches(int currentLiveCount, LedgerEntry stored)
        => !stored.IsEmpty && currentLiveCount == stored.ItemCount;

    /// <summary>After applying a delta's adds and removes: does the ACTUAL resulting count agree with what the
    /// delta's own items implied? A mismatch means some adds were already present, or some removes named nothing the
    /// live list held — the local list had already drifted from the server's idea of it before this delta landed —
    /// and the apply must be discarded (never persisted) in favour of a full re-walk. PURE.</summary>
    public static bool PostApplyMatches(int baselineCount, int actualNewCount, int added, int removed)
        => actualNewCount == baselineCount + added - removed;

    /// <summary>Stage 2's final gate on a decoded delta answer, right before it may be applied: <c>delta_update_possible</c>
    /// must be true AND the new token must actually be there to parse back — anything else falls back to a full walk,
    /// silently, never as an error the caller has to handle. PURE.</summary>
    public static bool CanApplyDelta(bool deltaUpdatePossible, string? newSyncToken)
        => deltaUpdatePossible && !string.IsNullOrEmpty(newSyncToken);

    /// <summary>Stage 1's own guard on a FULL walk: one that comes back with FEWER items than the last verified count
    /// is never trusted to ADVANCE the ledger. <paramref name="stored"/> is simply left as it was (empty included),
    /// which is exactly what forces the next sync to fall back to a full walk rather than ever build a delta on a
    /// doubtful baseline — and it means a truncated walk can never look like a removal. A walk that matches or beats
    /// the stored count, or one with nothing stored to beat, is trustworthy. PURE.</summary>
    public static bool WalkIsTrustworthy(int walkedCount, LedgerEntry stored)
        => stored.IsEmpty || walkedCount >= stored.ItemCount;
}
