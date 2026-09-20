// ── Spotify/Spotify.Api.Library.cs ──────────────────────────────────────────────────────────────────────────────────
// the playlist4 lists (playlist, rootlist, recents), the collection, and the edge half of the fetch provider
//
// Role: SHELL (the sends, the edge walk) + CORE (the route values, the replay decision, the bodies)
// Owner: F (L2 for the revision-gated read and `ListReplay`, wave D3)
// Wave: gap batch B1b (G-042 routes, G-043 CollectionAdd/Remove, G-054 zstd); D3 (the replay arm)
// Budget: 1100 lines
// Spec: gap register G-042/G-043/G-054 — a named partial of Spotify.Api.cs, which this would have taken past its budget;
//       docs/plans/wavee/cache-integrity-and-playlist-diff-implementation.md §3.3 (the replay arm)
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
// THE HELD REVISION (G-042) AND THE REPLAY ARM (wave D3, plan §3.3). An edge batch carries the revision each parent's
// list was last answered at (`FetchBatch.Revisions`) and, for a playlist or the rootlist, the settled list that
// revision describes (`FetchBatch.Baselines`) — both snapshotted on the UI thread at send, because this thread never
// reads a table. With a revision the read is the /diff, decoded by the finished pure core (`PlaylistOps.DecodeDiff`,
// field 20 read FIRST — bug A1's Eurodance Mix was a resync-flagged `contents` block trusted as a snapshot) and decided
// by `ListReplay.Decide`:
//
//     304 · empty · up_to_date · 0-op diff naming the held rev    → UNCHANGED: nothing staged, the held list stands
//     contents (and a revision), no diff                          → CONTENTS: decoded as a full read
//     ops, baseline held, from == held, to well-formed, TryApply  → APPLIED: the replayed WHOLE list staged + `to` +
//       ok, adds − removes reconcile, header op fully stated        the count (+ the renamed header) — lands like a
//                                                                   disk read, written behind like a full read
//     anything else — resync, a misfit, no baseline, 509, …       → FULL READ, in the same call (it replaces the
//                                                                   revision, so nothing re-asks)
//
// One always-on line per revision-gated read says which, and why: `list.replay list= ops= kinds= verdict= ms=` — how
// the still-unobserved op shapes (plan §3.9/§3.10) get observed in the field. It never names a uri or a title.
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
using Pl = Wavee.Protocol.Playlist;

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
        public enum ListKind : byte { Playlist, Rootlist, Recents, Show }

        static readonly System.Collections.Concurrent.ConcurrentDictionary<(uint Epoch, ListKind Kind, string Id), byte> s_pushReads = new();
        public static void NoteListPush(ListKind kind, string id)
        {
            if (s_pushReads.Count >= 512) s_pushReads.Clear();
            s_pushReads[(Current.Epoch, kind, id)] = 0;
        }
        static Route WithPushReason(Route route, ListKind kind, string id)
            => s_pushReads.TryRemove((Current.Epoch, kind, id), out _) ? route with { SyncReason = "CAI=" } : route;


        const string ListDecorations = "?decorate=revision,attributes,length,owner,capabilities,picture";
        const HeaderSet RecentsHeaders = CommonProtobuf | HeaderSet.ApplyLenses | HeaderSet.AcceptListItems | HeaderSet.AcceptGeoblock | HeaderSet.DsaMode;

        /// <summary>The FULL read of a list. <paramref name="id"/> is the playlist id or the username; recents has none.
        /// PURE.</summary>
        public static Route ListRoute(ListKind kind, string id) => kind switch
        {
            ListKind.Recents => new Route(Verb.Get, ApiHost.Spclient, "/playlist/v2/list/recents/page", RecentsHeaders,
                RequestKind.RecentsPage, "CAwQAQ==", Zstd: true),
            ListKind.Show => new Route(Verb.Get, ApiHost.Spclient, "/playlist/v2/show/" + Escaped(id), RecentsHeaders, RequestKind.PlaylistRead, "CAwQAQ==", Zstd: true),
            ListKind.Rootlist => new Route(Verb.Get, ApiHost.Spclient,
                "/playlist/v2/user/" + Escaped(id) + "/rootlist" + ListDecorations + "&length=120", RecentsHeaders, RequestKind.RootlistRead, "CAU=", Zstd: true),
            _ => new Route(Verb.Get, ApiHost.Spclient,
                "/playlist/v2/playlist/" + Escaped(id) + ListDecorations, RecentsHeaders, RequestKind.PlaylistRead, "CAwQAQ==", Zstd: true),
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
                ListKind.Show => new Route(Verb.Get, ApiHost.Spclient, "/playlist/v2/show/" + Escaped(id) + "/diff" + gate,
                    RecentsHeaders | HeaderSet.AppliedLenses, RequestKind.PlaylistDiff, "CAEQAQ==", Zstd: true),
                ListKind.Rootlist => new Route(Verb.Get, ApiHost.Spclient,
                    "/playlist/v2/user/" + Escaped(id) + "/rootlist/diff" + gate, RecentsHeaders | HeaderSet.AppliedLenses, RequestKind.PlaylistDiff, "CAw=", Zstd: true),
                _ => new Route(Verb.Get, ApiHost.Spclient,
                    "/playlist/v2/playlist/" + Escaped(id) + "/diff" + gate, RecentsHeaders | HeaderSet.AppliedLenses, RequestKind.PlaylistDiff, "CAEQAQ==", Zstd: true),
            };
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
            uint counter = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(revision);
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
                ListRow[]? baseline = i < batch.Baselines.Length ? batch.Baselines[i] : null;
                switch (route.Rest)
                {
                    case SpclientRoute.ShowRead: ShowEdge(parent, revision, baseline, s, ref outcome); break;
                    case SpclientRoute.Rootlist: RootlistEdge(parent, revision, baseline, s, ref outcome); break;
                    case SpclientRoute.Recents: RecentsEdge(parent, revision, s, ref outcome); break;
                    case SpclientRoute.CollectionPage: CollectionEdge(edge, parent, i, batch, s, ref outcome); break;
                    case SpclientRoute.PlaylistRead: PlaylistEdge(parent, revision, baseline, s, ref outcome); break;   // revision-gated (B1b gap 8), replayed (D3)
                    default: AnswerRest(route.Rest, parent, s, ref outcome); break;
                }
            }
        }

        /// <summary>What one list read hands its decoder: a <see cref="Body"/> to decode as a read (a full read, or a
        /// <c>/diff</c> answered with <c>contents</c>), OR the replayed whole list (<see cref="Rows"/>) with the revision
        /// it is true at and the header change that rode it — or neither, when the held list stands or the read failed
        /// (the outcome holds which).</summary>
        readonly record struct ListRead(byte[]? Body, ListRow[]? Rows = null, string? Revision = null,
                                        PlaylistOps.ListAttributeChange Attrs = default);

        /// <summary>THE REVISION-GATED READ (see the header): the <c>/diff</c> against the held
        /// <paramref name="revision"/>, decided by <see cref="ListReplay.Decide(bool,ReadOnlySpan{byte},string,ListRow[])"/>
        /// over <paramref name="baseline"/> — unchanged, contents, applied, or the full read in the same call (a 509, a
        /// resync, a misfit, no baseline: never a re-ask loop, because the full read replaces the revision). Recents is
        /// never replayed (its snapshot is grouped, not a playlist4 list a baseline can hold), so its ops read in full.
        /// No revision is the plain full read. One always-on <c>list.replay</c> line per revision-gated read.</summary>
        static ListRead ReadList(ListKind kind, string id, string? revision, ListRow[]? baseline, ref FetchOutcome outcome)
        {
            if (revision is not { Length: > 0 }) return new ListRead(FullRead(kind, id, ref outcome));
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            Result diff = Send(WithPushReason(ListDiffRoute(kind, id, revision), kind, id), [], CancellationToken.None);
            ListReplay.Outcome decided = diff.Ok || diff.NotModified
                ? ListReplay.Decide(kind == ListKind.Rootlist, diff.Body, revision, kind == ListKind.Recents ? null : baseline)
                : ListReplay.Refused(ListReplay.StatusReason(diff.Status));          // a 509 (too stale), a failure
            ListRead read;
            switch (decided.Verdict)
            {
                case ListReplay.Verdict.Unchanged:
                    outcome.Note(in diff);
                    read = default;
                    break;
                case ListReplay.Verdict.Contents:
                    outcome.Note(in diff);
                    if (kind == ListKind.Rootlist)
                    {
                        var root = Pl.SelectedListContent.Parser.ParseFrom(diff.Body);
                        read = root.Contents is { } contents && (contents.Truncated || (root.HasLength && contents.Items.Count < root.Length))
                            ? new ListRead(FullRead(kind, id, ref outcome)) : new ListRead(diff.Body);
                    }
                    else read = new ListRead(diff.Body);
                    break;
                case ListReplay.Verdict.Applied:
                    outcome.Note(in diff);
                    read = new ListRead(null, decided.Rows, decided.Answer.To, decided.Attrs);
                    break;
                default:
                    read = new ListRead(FullRead(kind, id, ref outcome));
                    break;
            }
            LogReplay(kind, in decided, started);
            return read;
        }

        /// <summary>The list's full read: its bytes, or null when it failed (noted) or answered nothing.</summary>
        static byte[]? FullRead(ListKind kind, string id, ref FetchOutcome outcome)
        {
            Result full = kind == ListKind.Rootlist ? Rootlist(id, CancellationToken.None) : Send(WithPushReason(ListRoute(kind, id), kind, id), [], CancellationToken.None);
            outcome.Note(in full);
            if (full.Ok && full.Body.Length > 0) return full.Body;
            if (!full.Ok) Log.Warn("library", "list read failed (" + kind + ", status " + full.Status + ")");
            return null;
        }

        /// <summary>The always-on <c>list.replay list= ops= kinds= verdict= ms=</c> line (plan §3.9): which list, how many
        /// ops of which kinds the answer carried, what became of them, and how long the whole read took (the fallback full
        /// read included). Never a uri, never a name.</summary>
        static void LogReplay(ListKind kind, in ListReplay.Outcome decided, long started)
        {
            if (!Log.IsEnabled(WaveeLogLevel.Info)) return;
            PlaylistOps.Batch batch = decided.Answer.Batch ?? PlaylistOps.Batch.Empty;
            Log.Event(WaveeLogLevel.Info, "library", "list.replay", "", null, -1, null,
                WaveeLogField.Of("list", kind switch { ListKind.Rootlist => "rootlist", ListKind.Recents => "recents", _ => "playlist" }),
                WaveeLogField.Of("ops", batch.Ops.Length),
                WaveeLogField.Of("kinds", ListReplay.Kinds(batch)),
                WaveeLogField.Of("verdict", ListReplay.VerdictText(in decided)),
                WaveeLogField.Of("ms", (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds));
        }

        /// <summary>The account's rootlist: a replayed stream lands through <c>Decode.RootlistReplay</c> (markers, depths
        /// and positions recomputed by <see cref="ListReplay"/>), anything to decode through <c>Decode.Rootlist</c>. A
        /// replay the staging refuses — which the decision's own checks make unreachable — is read in full.</summary>
        static void RootlistEdge(string meUri, string? revision, ListRow[]? baseline, Staging s, ref FetchOutcome outcome)
        {
            string username = UsernameOf(meUri);
            ListRead read = ReadList(ListKind.Rootlist, username, revision, baseline, ref outcome);
            byte[]? body = read.Body;
            if (read.Rows is { } rows && !Decode.RootlistReplay(rows, meUri, read.Revision!, s))
            {
                Log.Warn("library", "a replayed rootlist did not stage; reading it in full");
                body = FullRead(ListKind.Rootlist, username, ref outcome);
            }
            if (body is not null) Decode.Rootlist(body, Encoding.UTF8.GetBytes(meUri), s);
        }

        static void RecentsEdge(string meUri, string? revision, Staging s, ref FetchOutcome outcome)
        {
            byte[]? body = ReadList(ListKind.Recents, "", revision, null, ref outcome).Body;
            // 0.2.9's invariant: rows come ONLY from a body that carried contents. Mapping one that did not yields zero
            // items, and staging that is how 1,708 resident rows once became none. The same strict reading the /diff
            // arm uses (resync first, a truncated frame refused) decides it.
            if (body is null || PlaylistOps.DecodeDiff(body).Answer != PlaylistOps.Answer.Contents) return;
            Span<char> chars = stackalloc char[160];
            int n = RevisionOf(body, chars);
            Span<byte> ascii = stackalloc byte[160];
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

            // TWO RUNS MAY NOT BE OPEN ON ONE STAGING AT ONCE. A run is a CONTIGUOUS slice of the shared edge list
            // (`StagedEdgeList.Append`: start + length), so appending to a second run BETWEEN two appends of the first
            // puts the second's children inside the first's slice. With one page that is invisible; from the second
            // page on, the main run's slice swallows whatever the other run staged from page one — which is exactly
            // how saved ALBUMS ended up inside a 349-item Liked list (18 of them: `entity.miskind table=Track
            // id=Album`, then `store.edge.dropped relation=Liked dropped=18` on every launch thereafter). `Discard`
            // and `Compact` would have sliced the wrong rows too.
            //
            // So the shared relation's children are COLLECTED while the pages stream and staged in ONE contiguous run
            // after the main one closes. The page bodies are still decoded and discarded as they land (G-042's whole
            // point); only the identified children are held, which is what the second run was holding anyway. One list
            // per full walk, and a full walk is the rare path.
            Relation otherRelation = default;
            EntityKind otherKind = default;
            List<(StagedId Target, int At)>? otherItems = null;
            if (shared)
            {
                (otherRelation, otherKind) = Decode.LibraryRelationOf(otherSet);
                otherItems = new List<(StagedId, int)>(CollectionPageSize);
            }

            string token = "", lastSyncToken = "";
            bool terminal = false;
            for (int page = 0; page < MaxCollectionPages; page++)
            {
                if (Stale(batch)) { run.Discard(); otherItems?.Clear(); return; }
                Result result = CollectionPage(CollectionPageBody(username, wire, token, CollectionPageSize), CancellationToken.None);
                if (!result.Ok)
                {
                    run.Discard();
                    otherItems?.Clear();
                    outcome.Note(in result);                           // half a library is not an answer
                    return;
                }
                Decode.LibraryPageItems(result.Body, isPins ? null : kind, s, ref run);
                if (otherItems is not null) Decode.LibraryPageItems(result.Body, otherKind, s, otherItems);
                lastSyncToken = SyncTokenOf(result.Body);
                token = NextPageToken(result.Body);
                if (token.Length == 0) { terminal = true; break; }
            }

            if (!terminal)
            {
                // The page cap fired without a real terminal: a whole-list rewrite from a truncated crawl would delete
                // real rows (0.2.10's production bug this gap-fix exists to close) — commit nothing, trust nothing.
                run.Discard();
                otherItems?.Clear();
                Log.Warn("library", "collection walk hit the page cap (" + MaxCollectionPages + ") without a terminal page ("
                                     + wire + "); nothing committed this pass");
                outcome.Note(0);
                return;
            }

            int walkedCount = run.Count;
            int otherWalkedCount = otherItems?.Count ?? 0;
            run.EndEvenIfEmpty(in parent);
            if (otherItems is not null)
            {
                // Now, and only now, is the edge list free for a second contiguous slice.
                EdgeRun otherRun = s.Run(otherRelation);
                for (int i = 0; i < otherItems.Count; i++)
                {
                    var target = otherItems[i].Target;
                    otherRun.Add(in target).At = otherItems[i].At;
                }
                otherRun.EndEvenIfEmpty(in parent);
            }
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
        public static Result Playlist(string playlistId, CancellationToken ct) => Send(WithPushReason(ListRoute(ListKind.Playlist, playlistId), ListKind.Playlist, playlistId), [], ct);

        /// <summary>The revision-gated diff. 304 means "you are current"; 509 means the revision is too stale and the
        /// caller must fall back to a full read. A revision too short to spell is the full read.</summary>
        public static Result PlaylistDiff(string playlistId, ReadOnlySpan<byte> revision, CancellationToken ct)
        {
            Span<char> formatted = stackalloc char[160];
            int length = FormatRevision(revision, formatted);
            if (length == 0) return Playlist(playlistId, ct);
            return Send(WithPushReason(ListDiffRoute(ListKind.Playlist, playlistId, new string(formatted[..length])), ListKind.Playlist, playlistId), [], ct);
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

        public static Result Rootlist(string username, CancellationToken ct)
        {
            Route route = WithPushReason(ListRoute(ListKind.Rootlist, username), ListKind.Rootlist, username);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                Result first = Send(route, [], ct);
                if (!first.Ok || first.Body.Length == 0) return first;
                var merged = Pl.SelectedListContent.Parser.ParseFrom(first.Body);
                if (merged.Contents is not { } contents) return first;
                bool retry = false;
                while (contents.Truncated || (merged.HasLength && contents.Items.Count < merged.Length))
                {
                    int offset = contents.Items.Count;
                    Route nextRoute = route with { Path = route.Path + "&from=" + offset.ToString(System.Globalization.CultureInfo.InvariantCulture) };
                    Result next = Send(nextRoute, [], ct);
                    if (!next.Ok) return next;
                    var page = Pl.SelectedListContent.Parser.ParseFrom(next.Body);
                    if (!page.Revision.Equals(merged.Revision)) { retry = true; break; }
                    if (page.Contents is not { } part || part.Pos != offset || part.Items.Count == 0) return Result.Transport;
                    contents.Items.Add(part.Items);
                    contents.MetaItems.Add(part.MetaItems);
                    contents.Truncated = part.Truncated;
                }
                if (retry) continue;
                contents.Pos = 0;
                contents.Truncated = false;
                return first.WithBody(merged.ToByteArray());
            }
            return Result.Transport;
        }

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

// ── the replay arm (wave D3, plan §3.3) ──────────────────────────────────────────────────────────────────────────────
//
// THE DECISION IS PURE, and it is here rather than inside `ReadList` so a test can drive it with the captured fixtures
// and no transport: a `/diff` answer (the bytes) + the revision held + the baseline the batch carried → a verdict, and
// for an APPLIED one the replayed whole list, ready for `Store.StageList` (through `Spotify.Decode.PlaylistReplay` /
// `RootlistReplay`). The finished core does the hard part — `PlaylistOps.DecodeDiff` (field 20 first, strict frames)
// and `PlaylistOps.TryApply` (sequential ops, pre-removal MOV, every REM's carried rows checked) — and this adds the
// guards only the caller can make:
//
//   · a baseline at all (`Fetch.FillBaselines`: a settled whole, beside a held revision), and `from` == the revision it
//     describes — else the ops describe another list;
//   · `to` well-formed and moved (the one revision gate — plan §2), and adds − removes reconcile with the baseline;
//   · a header op lands only when it states BOTH name and description (set, or unset): the full read's own staging
//     writes the whole Identity group, and a rename alone would blank a description nobody changed — so a partial one
//     is read in full rather than guessed (plan §7);
//   · a ROOTLIST replays over its whole wire stream: every baseline row's wire position must be its index (a skipped
//     non-playlist item makes them differ, and then the ops' indices are not ours), markers are identified by kind +
//     folder id (+ name on a start marker), and depths and positions are RECOMPUTED after the replay by the rootlist
//     decoder's own rule — a moved block carries its old depth otherwise.
//
// Any doubt is `FullRead` with its reason, which `ReadList` turns into the full read in the same call and the
// `list.replay … verdict=fullread:<why>` line names. The replay allocates (a copy of the baseline, the mapped op rows);
// a diff is rare and small, and nothing here runs per frame.

/// <summary>THE REPLAY ARM'S DECISION (plan §3.3; the header above). PURE: bytes, strings and arrays in, a verdict and
/// a list out — no table, no staging, no clock.</summary>
public static class ListReplay
{
    /// <summary>What one revision-gated read comes to.</summary>
    public enum Verdict : byte
    {
        /// <summary>The held list stands: stage nothing.</summary>
        Unchanged,
        /// <summary>The ops replayed: stage <see cref="Outcome.Rows"/> at the answer's <c>to</c> revision.</summary>
        Applied,
        /// <summary>The answer carries the list (and its revision): decode it as a full read.</summary>
        Contents,
        /// <summary>Read the list in full; <see cref="Outcome.Why"/> says why.</summary>
        FullRead,
    }

    // The <why> of `verdict=fullread:<why>` when it is the arm's and not a `PlaylistOps.Refusal`.
    public const string NoBaseline = "no-baseline";
    public const string FromMismatch = "from-mismatch";
    public const string ToRevision = "to-revision";
    public const string NoRevision = "no-revision";
    public const string Reconcile = "reconcile";
    public const string Header = "header";
    public const string RootlistPositions = "rootlist-positions";
    public const string RootlistItem = "rootlist-item";

    /// <summary>The decision. <see cref="Rows"/>, <see cref="Attrs"/> and <see cref="Tally"/> mean something only for
    /// <see cref="Verdict.Applied"/>; <see cref="Answer"/> is the decoded answer (its batch feeds the log line).</summary>
    public readonly record struct Outcome(Verdict Verdict, string Why, PlaylistOps.DiffAnswer Answer, ListRow[]? Rows,
                                          PlaylistOps.ListAttributeChange Attrs, PlaylistOps.Tally Tally);

    /// <summary>A <c>/diff</c> answer's bytes (a 304's empty body included) → the verdict. A <c>contents</c> answer must
    /// name its revision: rows landed without one would sit under the OLD revision, and the next <c>/diff</c> would replay
    /// someone else's ops over them.</summary>
    public static Outcome Decide(bool rootlist, ReadOnlySpan<byte> answer, string held, ListRow[]? baseline)
    {
        PlaylistOps.DiffAnswer decoded = PlaylistOps.DecodeDiff(answer);
        if (decoded.Answer == PlaylistOps.Answer.Contents)
        {
            Span<char> revision = stackalloc char[160];
            if (Spotify.Api.RevisionOf(answer, revision) == 0) return Full(in decoded, NoRevision);
        }
        return Decide(rootlist, in decoded, held, baseline);
    }

    /// <summary>A decoded answer → the verdict (see the section header for every guard). <paramref name="held"/> is the
    /// revision the batch sent (<c>FetchBatch.Revisions</c>); <paramref name="baseline"/> the list it describes
    /// (<c>FetchBatch.Baselines</c>), never modified — the replay runs over a copy.</summary>
    public static Outcome Decide(bool rootlist, in PlaylistOps.DiffAnswer answer, string held, ListRow[]? baseline)
    {
        switch (answer.Answer)
        {
            case PlaylistOps.Answer.Unchanged:
                // A trivial diff names the revision it answered about; it vouches for the held list only if that is it.
                return answer.From is not null && !string.Equals(answer.From, held, StringComparison.Ordinal)
                    ? Full(in answer, FromMismatch)
                    : new Outcome(Verdict.Unchanged, "", answer, null, default, default);
            case PlaylistOps.Answer.Contents:
                return new Outcome(Verdict.Contents, "", answer, null, default, default);
            case PlaylistOps.Answer.Replay:
                break;
            default:
                return Full(in answer, answer.Why.ToString());
        }

        if (baseline is null) return Full(in answer, NoBaseline);
        if (!string.Equals(answer.From, held, StringComparison.Ordinal)) return Full(in answer, FromMismatch);
        if (!ListWrite.IsWellFormedRevision(answer.To) || string.Equals(answer.To, held, StringComparison.Ordinal))
            return Full(in answer, ToRevision);
        if (rootlist && !WirePositional(baseline)) return Full(in answer, RootlistPositions);

        PlaylistOps.Batch batch = answer.Batch;
        var items = new ListRow[batch.Items.Length];
        for (int i = 0; i < items.Length; i++)
            if (!TryRow(rootlist, in batch.Items[i], out items[i])) return Full(in answer, RootlistItem);

        var list = new List<ListRow>(baseline);
        if (!PlaylistOps.TryApply<ListRow, RowAccess>(list, batch.Ops, items, batch.Lists, default,
                out PlaylistOps.ListAttributeChange attrs, out PlaylistOps.Tally tally, out PlaylistOps.Misfit misfit))
            return Full(in answer, misfit.Why.ToString());
        if (!tally.Reconciles(baseline.Length, list.Count)) return Full(in answer, Reconcile);
        if (!attrs.IsEmpty && (rootlist || !States(in attrs))) return Full(in answer, Header);

        ListRow[]? rows = rootlist ? Restream(list) : list.ToArray();
        if (rows is null) return Full(in answer, RootlistItem);
        return new Outcome(Verdict.Applied, "", answer, rows, attrs, tally);
    }

    /// <summary>A read that never got an answer to decide on (a 509, a transport failure): the full read.</summary>
    public static Outcome Refused(string why)
        => new(Verdict.FullRead, why,
               new PlaylistOps.DiffAnswer(PlaylistOps.Answer.FullRead, PlaylistOps.Refusal.None, null, null, PlaylistOps.Batch.Empty),
               null, default, default);

    /// <summary>The <c>&lt;why&gt;</c> of a <c>/diff</c> that answered neither 2xx nor 304 (<c>status-509</c>: too stale).</summary>
    public static string StatusReason(int status)
        => "status-" + status.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The log line's <c>verdict=</c>: <c>unchanged</c> · <c>applied</c> · <c>contents</c> · <c>fullread:&lt;why&gt;</c>.</summary>
    public static string VerdictText(in Outcome outcome) => outcome.Verdict switch
    {
        Verdict.Unchanged => "unchanged",
        Verdict.Applied => "applied",
        Verdict.Contents => "contents",
        _ => "fullread:" + outcome.Why,
    };

    /// <summary>The log line's <c>kinds=</c>: the op kinds a batch carried, each once, in first-seen order —
    /// <c>ADD</c>, <c>REM</c>, <c>MOV</c>, <c>ITEM</c> (UPDATE_ITEM_ATTRIBUTES), <c>LIST</c> (UPDATE_LIST_ATTRIBUTES).</summary>
    public static string Kinds(PlaylistOps.Batch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        PlaylistOps.Op[] ops = batch.Ops;
        if (ops.Length == 0) return "";
        var text = new StringBuilder(24);
        Span<bool> seen = stackalloc bool[8];
        for (int i = 0; i < ops.Length; i++)
        {
            int k = (int)ops[i].Kind;
            if ((uint)k >= (uint)seen.Length || seen[k]) continue;
            seen[k] = true;
            if (text.Length > 0) text.Append(',');
            text.Append(ops[i].Kind switch
            {
                PlaylistOps.Kind.Add => "ADD",
                PlaylistOps.Kind.Rem => "REM",
                PlaylistOps.Kind.Mov => "MOV",
                PlaylistOps.Kind.UpdateItemAttributes => "ITEM",
                PlaylistOps.Kind.UpdateListAttributes => "LIST",
                _ => "?",
            });
        }
        return text.ToString();
    }

    static Outcome Full(in PlaylistOps.DiffAnswer answer, string why) => new(Verdict.FullRead, why, answer, null, default, default);

    /// <summary>Does the header change state BOTH attributes — each set, or explicitly unset (<c>no_value</c>)? Only then
    /// can it land as the full read's Identity group does, without guessing the one it did not name.</summary>
    static bool States(in PlaylistOps.ListAttributeChange attrs)
        => (attrs.Name is not null || (attrs.Unset & PlaylistOps.ListAttrs.Name) != 0)
           && (attrs.Description is not null || (attrs.Unset & PlaylistOps.ListAttrs.Description) != 0);

    /// <summary>Is every baseline row at its own wire position? A rootlist op addresses the WIRE stream; a skipped
    /// non-playlist item (Spotify.Decode.Rootlist stages none, but counts its position) makes the two differ.</summary>
    static bool WirePositional(ListRow[] baseline)
    {
        if (baseline.Length > ushort.MaxValue + 1) return false;
        for (int i = 0; i < baseline.Length; i++)
            if (baseline[i].WirePos != i) return false;
        return true;
    }

    /// <summary>A replayed rootlist stream, re-derived exactly as <c>Spotify.Decode.Rootlist</c> derives a read one: a
    /// start marker sits at its folder's depth and raises it, an end marker lowers it first, an item sits at the depth
    /// around it (all clamped at <see cref="Spotify.Decode.MaxFolderDepth"/>), and the position is the index. Null when
    /// the stream holds something the decoder would skip (an item that is not a playlist): its positions would no longer
    /// be ours.</summary>
    static ListRow[]? Restream(List<ListRow> list)
    {
        if (list.Count > ushort.MaxValue + 1) return null;
        const int max = Spotify.Decode.MaxFolderDepth;
        var rows = new ListRow[list.Count];
        int depth = 0;
        for (int i = 0; i < rows.Length; i++)
        {
            ListRow row = list[i];
            int at;
            switch (row.Kind)
            {
                case RootlistKind.FolderStart:
                    at = Math.Min(depth, max);
                    if (depth < max) depth++;
                    break;
                case RootlistKind.FolderEnd:
                    depth = Math.Max(0, depth - 1);
                    at = Math.Min(depth, max);
                    break;
                default:
                    if (row.Uri.Length == 0 || EntityUri.KindOf(row.Uri.AsSpan()) != EntityKind.Playlist) return null;
                    at = Math.Min(depth, max);
                    break;
            }
            rows[i] = row with { Depth = (byte)at, WirePos = (ushort)i };
        }
        return rows;
    }

    const string StartGroup = "spotify:start-group:";
    const string EndGroup = "spotify:end-group:";
    /// <summary>The longest item id the full-read decoder stages (64 bytes as hex): a longer one it drops, and so do we.</summary>
    const int MaxItemIdHex = 128;

    /// <summary>One op row (<see cref="PlaylistOps.Batch.Items"/>, index for index — ADD rows, carried REM rows, anchors,
    /// UPDATE_ITEM values) → a <see cref="ListRow"/>, mapped exactly as the full-read decoders map a wire item: a
    /// playlist member's adder as <c>spotify:user:&lt;name&gt;</c>, its timestamp through <c>Spotify.Decode.Instant</c>,
    /// its item id as hex, its chart triple; a rootlist row as the marker or item <c>Spotify.Decode.Rootlist</c> stages
    /// (a start marker's name decoded by its rule, an end marker's instant dropped as it drops it). An empty uri is an
    /// UPDATE_ITEM's values — never a member. False for a rootlist row the decoder would skip (not a playlist, a start
    /// marker without an id).</summary>
    static bool TryRow(bool rootlist, in PlaylistOps.WireItem item, out ListRow row)
    {
        int at = Spotify.Decode.Instant(item.Timestamp);
        string uri = item.Uri;
        if (!rootlist || uri.Length == 0)
        {
            string? itemId = !rootlist && item.ItemId is { Length: > 0 and <= MaxItemIdHex } id ? id : null;
            row = new ListRow(uri, itemId, at, AdderUri(item.AddedBy), rootlist ? (byte)0 : item.ChartStatus,
                              rootlist ? (ushort)0 : item.ChartPos, rootlist ? (ushort)0 : item.ChartPrev, 0,
                              RootlistKind.Item, 0, 0, null, null);
            return true;
        }
        if (uri.StartsWith(StartGroup, StringComparison.Ordinal))
        {
            ReadOnlySpan<char> rest = uri.AsSpan(StartGroup.Length);
            int colon = rest.IndexOf(':');
            ReadOnlySpan<char> group = colon < 0 ? rest : rest[..colon];
            if (group.IsEmpty) { row = default; return false; }
            row = new ListRow("", null, at, null, 0, 0, 0, 0, RootlistKind.FolderStart, 0, 0, group.ToString(),
                              colon < 0 ? null : FolderName(rest[(colon + 1)..]));
            return true;
        }
        if (uri.StartsWith(EndGroup, StringComparison.Ordinal))
        {
            ReadOnlySpan<char> rest = uri.AsSpan(EndGroup.Length);
            int colon = rest.IndexOf(':');
            ReadOnlySpan<char> group = colon < 0 ? rest : rest[..colon];
            row = new ListRow("", null, 0, null, 0, 0, 0, 0, RootlistKind.FolderEnd, 0, 0, group.IsEmpty ? null : group.ToString(), null);
            return true;
        }
        if (EntityUri.KindOf(uri.AsSpan()) != EntityKind.Playlist) { row = default; return false; }
        row = new ListRow(uri, null, at, null, 0, 0, 0, 0, RootlistKind.Item, 0, 0, null, null);
        return true;
    }

    /// <summary>The wire's bare username → the adder's user uri, <c>Spotify.Decode.UserUri</c>'s rule: a value already a
    /// <c>spotify:</c> uri stands, an empty or over-long one is no adder.</summary>
    static string? AdderUri(string? username)
    {
        if (string.IsNullOrEmpty(username)) return null;
        if (username.StartsWith("spotify:", StringComparison.Ordinal)) return username;
        if (Encoding.UTF8.GetByteCount(username) > 200) return null;
        return "spotify:user:" + username;
    }

    /// <summary>A start marker's escaped name, <c>Spotify.Decode.FolderName</c>'s rule byte for byte: over the UTF-8,
    /// <c>+</c> is a space FIRST, then each well-formed <c>%XX</c> is its byte, a malformed escape is kept verbatim, and at
    /// most 512 bytes come out — so a replayed marker carries the very name a read of the same stream would.</summary>
    static string? FolderName(ReadOnlySpan<char> escaped)
    {
        if (escaped.IsEmpty) return null;
        byte[] utf8 = new byte[Encoding.UTF8.GetByteCount(escaped)];
        Encoding.UTF8.GetBytes(escaped, utf8);
        Span<byte> name = stackalloc byte[512];
        int n = 0;
        for (int i = 0; i < utf8.Length && n < name.Length; i++)
        {
            byte b = utf8[i];
            if (b == (byte)'+') { name[n++] = (byte)' '; continue; }
            if (b == (byte)'%' && i + 2 < utf8.Length && HexValue(utf8[i + 1]) is int hi and >= 0 && HexValue(utf8[i + 2]) is int lo and >= 0)
            {
                name[n++] = (byte)((hi << 4) | lo);
                i += 2;
                continue;
            }
            name[n++] = b;
        }
        return n == 0 ? null : Encoding.UTF8.GetString(name[..n]);

        static int HexValue(byte c) => c is >= (byte)'0' and <= (byte)'9' ? c - '0'
                                     : c is >= (byte)'a' and <= (byte)'f' ? c - 'a' + 10
                                     : c is >= (byte)'A' and <= (byte)'F' ? c - 'A' + 10 : -1;
    }

    /// <summary>How the replayer reads a <see cref="ListRow"/> (<see cref="PlaylistOps.IRowAccess{TRow}"/>). IDENTITY:
    /// by item id when both rows have one, else by uri — two spellings of one gid are one row, as the table folds them
    /// (<see cref="EntityId.TryParseGid(ReadOnlySpan{char},out EntityId)"/>) — and a folder marker, whose uri was split
    /// into columns, by kind + folder id, plus the decoded name on a start marker (the interface's own rule: "as if it
    /// still carried its <c>spotify:start-group:…</c> uri"). A row holds only an adder and an added-at, so an
    /// UPDATE_ITEM on the <c>public</c> bit cannot be held and refuses.</summary>
    public readonly struct RowAccess : PlaylistOps.IRowAccess<ListRow>
    {
        public bool SameUri(in ListRow a, in ListRow b)
        {
            if (a.Kind != RootlistKind.Item || b.Kind != RootlistKind.Item)
                return a.Kind == b.Kind && string.Equals(a.FolderId, b.FolderId, StringComparison.Ordinal)
                       && (a.Kind != RootlistKind.FolderStart || string.Equals(a.FolderName, b.FolderName, StringComparison.Ordinal));
            if (string.Equals(a.Uri, b.Uri, StringComparison.Ordinal)) return true;
            return EntityId.TryParseGid(a.Uri.AsSpan(), out EntityId x) && EntityId.TryParseGid(b.Uri.AsSpan(), out EntityId y)
                   && x == y;
        }

        public bool HasItemId(in ListRow row) => !string.IsNullOrEmpty(row.ItemId);

        public bool SameItemId(in ListRow a, in ListRow b) => string.Equals(a.ItemId, b.ItemId, StringComparison.Ordinal);

        public bool Holds(in ListRow row, in ListRow values, PlaylistOps.ItemAttrs set, PlaylistOps.ItemAttrs unset)
        {
            if (((set | unset) & PlaylistOps.ItemAttrs.Public) != 0) return false;
            if ((set & PlaylistOps.ItemAttrs.AddedBy) != 0
                && (row.AddedBy is null || !string.Equals(row.AddedBy, values.AddedBy, StringComparison.Ordinal))) return false;
            if ((set & PlaylistOps.ItemAttrs.Timestamp) != 0 && (row.AddedAt == 0 || row.AddedAt != values.AddedAt)) return false;
            if ((unset & PlaylistOps.ItemAttrs.AddedBy) != 0 && row.AddedBy is not null) return false;
            if ((unset & PlaylistOps.ItemAttrs.Timestamp) != 0 && row.AddedAt != 0) return false;
            return true;
        }

        public bool TryPatch(ref ListRow row, in ListRow values, PlaylistOps.ItemAttrs set, PlaylistOps.ItemAttrs unset)
        {
            if (((set | unset) & PlaylistOps.ItemAttrs.Public) != 0) return false;
            string? addedBy = row.AddedBy;
            int addedAt = row.AddedAt;
            if ((set & PlaylistOps.ItemAttrs.AddedBy) != 0) addedBy = values.AddedBy;
            if ((set & PlaylistOps.ItemAttrs.Timestamp) != 0) addedAt = values.AddedAt;
            if ((unset & PlaylistOps.ItemAttrs.AddedBy) != 0) addedBy = null;
            if ((unset & PlaylistOps.ItemAttrs.Timestamp) != 0) addedAt = 0;
            row = row with { AddedBy = addedBy, AddedAt = addedAt };
            return true;
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
