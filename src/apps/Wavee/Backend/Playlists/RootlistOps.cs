using System;

using System.Collections.Generic;

using System.Threading;

using System.Threading.Tasks;

using Wavee.Backend.Spotify;

using Wavee.Core;

using Pl = Wavee.Protocol.Playlist;



namespace Wavee.Backend.Playlists;



/// <summary>What a rootlist <c>/changes</c> POST left behind. The distinction that matters: a 409 means our INDICES are
/// stale and the op must be rebuilt against the freshly bootstrapped marker stream, while a 5xx/network fault means the
/// op is still perfectly valid and only the wire failed — replaying it verbatim is correct, and a queued op must NOT be
/// dead-lettered for it (that is what would leave a created playlist without its rootlist row).</summary>
public enum RootlistPostOutcome : byte { Applied, Rebased, Retry }

/// <summary>Why <see cref="RootlistOps.TryBuildMove(System.Collections.Generic.IReadOnlyList{RootlistEntry}, RootlistItemRef, RootlistItemRef, RootlistDropPlacement, out PlaylistOp?, out RootlistMoveCheck)"/>
/// built an op — or did not. Each value is a distinct SENTENCE upstream, which is the whole point: these were all one
/// silent <c>false</c>, and "the drag did nothing" was the only symptom the user ever saw.</summary>
public enum RootlistMoveCheck : byte
{
    Ok = 0,
    /// <summary>The source or the target is no longer in the rootlist.</summary>
    Missing = 1,
    /// <summary>Source and target are the same row.</summary>
    SameItem = 2,
    /// <summary>The placement cannot be expressed (Inside something that is not a folder).</summary>
    Invalid = 3,
    /// <summary>The destination is where the item already sits.</summary>
    NoOp = 4,
    /// <summary>A folder filed into its own subtree.</summary>
    Cycle = 5,
}

/// <summary>Rootlist index lookup + POST /rootlist/changes helpers (visibility, delete, follow).</summary>

public static class RootlistOps

{

    public static int FindPlaylistIndex(IReadOnlyList<RootlistEntry> entries, string playlistUri)

    {

        for (int i = 0; i < entries.Count; i++)

        {

            var e = entries[i];

            if (e.Kind == 0 && string.Equals(e.Uri, playlistUri, StringComparison.Ordinal))

                return i;

        }

        return -1;

    }



    public static async Task<byte[]?> BootstrapRootlistAsync(

        IStore store, ITransport transport, SessionContext ctx, CancellationToken ct)

    {

        var route = $"/playlist/v2/user/{ctx.Account}/rootlist?decorate=revision";

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Accept"] = "application/protobuf" };

        var r = await transport.Request(Channel.Spclient, route, ReadOnlyMemory<byte>.Empty, ct, "GET", headers).ConfigureAwait(false);

        if (!r.Ok) return store.RootlistRevision();

        return ApplyRootlistResponse(store, r.Body);

    }



    /// <summary>Fold a rootlist /changes or bootstrap GET response into the store and return the base revision the next
    /// write must use. I1: a revision that is not the 24-byte head is NEVER stored and NEVER returned — the caller would
    /// otherwise POST its next ListChanges against a malformed base. Rows still land (the 1-arg overload preserves the
    /// revision we already trust); an unparseable body is a LOGGED drop, not a silent one.</summary>
    public static byte[]? ApplyRootlistResponse(IStore store, byte[] body)
    {
        var bytes = SpotifyZstd.MaybeDecompressZstd(body);
        if (bytes.Length == 0) return store.RootlistRevision();
        Pl.SelectedListContent slc;
        try { slc = Pl.SelectedListContent.Parser.ParseFrom(bytes); }
        catch
        {
            PlaylistMutationDiagnostics.DealerDrop("rootlist/changes", "unparseable", bytes.Length);
            return store.RootlistRevision();
        }
        var rev = PlaylistWireMapper.ResultingRevision(slc);
        bool storable = PlaylistRevisions.IsWellFormed(rev);
        if (!storable && rev is not null)
            PlaylistMutationDiagnostics.RootlistBadRevision(rev.Length, "rootlist-response");

        if (slc.Contents is { } contents && contents.Items.Count > 0)
        {
            var uris = new List<string>(contents.Items.Count);
            var timestamps = new List<long>(contents.Items.Count);
            foreach (var it in contents.Items)
            {
                uris.Add(it.Uri);
                timestamps.Add(it.Attributes is { HasTimestamp: true } a ? a.Timestamp : 0);
            }
            var entries = RootlistTreeBuilder.EntriesFromUris(uris, timestamps);
            if (storable) store.SetRootlist(entries, rev);
            else store.SetRootlist(entries);           // 1-arg: rows adopted, the stored revision preserved
        }
        else if (storable)
            store.SetRootlist(store.Rootlist(), rev);

        return storable ? rev : store.RootlistRevision();
    }



    /// <summary>POST rootlist ops; returns false on 409 after rebasing (caller may retry).</summary>

    public static async Task<RootlistPostOutcome> TryPostRootlistOpsAsync(
        IStore store, ITransport transport, SessionContext ctx,
        IReadOnlyList<PlaylistOp> ops, string? logUri, CancellationToken ct)
        => await TryPostRootlistOpsAsync(store, transport, () => "", ctx, ops, logUri, ct).ConfigureAwait(false);

    public static async Task<RootlistPostOutcome> TryPostRootlistOpsAsync(

        IStore store, ITransport transport, Func<string> spclientBaseUrl, SessionContext ctx,

        IReadOnlyList<PlaylistOp> ops, string? logUri, CancellationToken ct)

    {

        var rev = store.RootlistRevision();

        if (rev is null) rev = await BootstrapRootlistAsync(store, transport, ctx, ct).ConfigureAwait(false);

        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var body = PlaylistWireMapper.BuildRootlistChanges(rev, ops, ctx.Account, nowMs);

        var route = $"/playlist/v2/user/{ctx.Account}/rootlist/changes";

        var r = await transport.Request(Channel.Spclient, route, body, ct, "POST",

            headers: SpotifyHeaders.PlaylistV2Mutation(ctx.Locale, spclientBaseUrl())).ConfigureAwait(false);

        if (r.Ok) { ApplyRootlistResponse(store, r.Body); return RootlistPostOutcome.Applied; }

        if (r.Status == 409)

        {

            await BootstrapRootlistAsync(store, transport, ctx, ct).ConfigureAwait(false);

            if (logUri is not null) PlaylistMutationDiagnostics.RootlistConflict(logUri);

            return RootlistPostOutcome.Rebased;

        }

        // 5xx / no status at all (a transport-level fault) leaves the op VALID: retry it verbatim. Throwing here is
        // what used to dead-letter a queued rootlist ADD on a transient server hiccup.
        if (r.Status is 0 or >= 500)
        {
            if (logUri is not null) PlaylistMutationDiagnostics.RootlistPostFailed(logUri, r.Status, ops[0].Kind.ToString());
            return RootlistPostOutcome.Retry;
        }

        if (logUri is not null) PlaylistMutationDiagnostics.RootlistPostFailed(logUri, r.Status, ops[0].Kind.ToString());

        throw r.Status switch
        {
            403 => new PlaylistMutationException(PlaylistMutationFailure.Forbidden,
                "You no longer have permission to change that."),
            404 or 410 => new PlaylistMutationException(PlaylistMutationFailure.Deleted,
                "That playlist no longer exists."),
            _ => new PlaylistMutationException(PlaylistMutationFailure.Unknown,
                $"The rootlist change was rejected ({r.Status})."),
        };

    }



    public static async Task PostRootlistOpsAsync(

        IStore store, ITransport transport, Func<string> spclientBaseUrl, SessionContext ctx,

        IReadOnlyList<PlaylistOp> ops, CancellationToken ct, string? logUri = null)

    {

        var outcome = await TryPostRootlistOpsAsync(store, transport, spclientBaseUrl, ctx, ops, logUri, ct).ConfigureAwait(false);
        if (outcome == RootlistPostOutcome.Rebased)
            throw new PlaylistMutationException(PlaylistMutationFailure.Conflict,
                "Your library changed while that was saving — try again.");
        if (outcome == RootlistPostOutcome.Retry)
            throw new PlaylistMutationException(PlaylistMutationFailure.Unknown,
                "Spotify could not be reached — that change was not saved.");

    }



    public static IReadOnlyList<RootlistEntry>? RemovePlaylistEntry(IReadOnlyList<RootlistEntry> cur, string uri)

    {

        int found = FindPlaylistIndex(cur, uri);

        if (found < 0) return null;

        var list = new List<RootlistEntry>(cur.Count - 1);

        for (int i = 0; i < cur.Count; i++) if (i != found) list.Add(cur[i]);

        for (int i = 0; i < list.Count; i++) list[i] = list[i] with { Position = i };

        return list;

    }

    /// <summary>Resolve a playlist row or balanced folder marker range into one rootlist MOV. The destination is expressed
    /// against the pre-removal marker stream, exactly like <see cref="PlaylistDiffApplier"/>.</summary>
    public static bool TryBuildMove(IReadOnlyList<RootlistEntry> entries, RootlistItemRef source,
                                    RootlistItemRef target, RootlistDropPlacement placement, out PlaylistOp? op)
        => TryBuildMove(entries, source, target, placement, out op, out _);

    /// <summary>The same builder, WITH the reason it refused.
    ///
    /// <para>Every one of these refusals used to reach the user as a bare <c>false</c> — nothing happened, no toast, no
    /// cue (D2/D8). The rule itself is unchanged and still lives here, where the index math is; naming the reason is what
    /// lets a caller refuse BEFORE the drop instead of discovering it three layers down.</para></summary>
    public static bool TryBuildMove(IReadOnlyList<RootlistEntry> entries, RootlistItemRef source,
                                    RootlistItemRef target, RootlistDropPlacement placement,
                                    out PlaylistOp? op, out RootlistMoveCheck reason)
    {
        op = null;
        if (!TryRange(entries, source, out int from, out int end)
            || !TryRange(entries, target, out int targetFrom, out int targetEnd))
        {
            reason = RootlistMoveCheck.Missing;
            return false;
        }
        if (from == targetFrom) { reason = RootlistMoveCheck.SameItem; return false; }
        int to = placement switch
        {
            RootlistDropPlacement.Before => targetFrom,
            RootlistDropPlacement.After => targetEnd,
            RootlistDropPlacement.Inside when target.IsFolder => Math.Max(targetFrom + 1, targetEnd - 1),
            _ => -1,
        };
        // Inside a NON-folder is not a placement this stream can express at all.
        if (to < 0) { reason = RootlistMoveCheck.Invalid; return false; }
        // Landing on either edge of the span it already occupies is a no-op; STRICTLY inside it is a folder being filed
        // into its own subtree.
        if (to == from || to == end) { reason = RootlistMoveCheck.NoOp; return false; }
        if (to > from && to < end) { reason = RootlistMoveCheck.Cycle; return false; }
        op = new PlaylistOp(PlaylistOpKind.Move, FromIndex: from, Length: end - from, ToIndex: to);
        reason = RootlistMoveCheck.Ok;
        return true;
    }

    /// <summary>Would this move be accepted, and if not why? Pure pre-validation over the CURRENT marker stream, so a UI
    /// can refuse a no-op or a cycle without duplicating the index math this file owns.</summary>
    public static RootlistMoveCheck CheckMove(IReadOnlyList<RootlistEntry> entries, RootlistItemRef source,
                                              RootlistItemRef target, RootlistDropPlacement placement)
    {
        TryBuildMove(entries, source, target, placement, out _, out var reason);
        return reason;
    }

    /// <summary>Build the ops for a whole ORDERED batch of moves — one <c>Delta</c>'s worth (multi-op rootlist deltas
    /// already ship: <see cref="BuildCreateFolder"/> is two ADDs).
    ///
    /// <para>The index math is <see cref="TryBuildMove"/>'s, unchanged and un-duplicated: each move is built against the
    /// CURRENT marker stream and then <see cref="ApplyLocally"/>'d before the next one is built, because that is exactly
    /// how the server applies the ops of one Delta ("each against the state left by the preceding ones"). Building all N
    /// against the original stream would post indices that are already stale by the second op.</para>
    ///
    /// <para>Three batch-only rules, all of them "a batch is not N separate drops":</para>
    /// <list type="bullet">
    /// <item>a source that IS the target is dropped from the batch up front — dropping a multi-selection right after one
    /// of its own members is a legal GATHER (the other members close up around it), not a
    /// <see cref="RootlistMoveCheck.SameItem"/>. Only when EVERY move is that self-pair is the batch
    /// <see cref="RootlistMoveCheck.SameItem"/>, which is what keeps the N=1 "onto itself" answer unchanged;</item>
    /// <item>a per-move <see cref="RootlistMoveCheck.NoOp"/> is skipped (a member already sitting where the batch is
    /// headed does not refuse the other members), while <see cref="RootlistMoveCheck.Cycle"/> /
    /// <see cref="RootlistMoveCheck.Missing"/> / <see cref="RootlistMoveCheck.Invalid"/> on ANY move refuses the WHOLE
    /// batch with that reason — half a filing is worse than none;</item>
    /// <item>two real ops can net to identity (<c>{B,C} Before D</c> on <c>[…B,C,D…]</c>), so the FINAL uri stream is
    /// compared to the input: equal ⇒ <see cref="RootlistMoveCheck.NoOp"/> and nothing is posted.</item>
    /// </list></summary>
    public static bool TryBuildMoves(IReadOnlyList<RootlistEntry> entries, IReadOnlyList<RootlistMove> moves,
                                     out IReadOnlyList<PlaylistOp> ops, out RootlistMoveCheck reason)
    {
        ops = Array.Empty<PlaylistOp>();
        if (moves.Count == 0) { reason = RootlistMoveCheck.NoOp; return false; }

        var built = new List<PlaylistOp>(moves.Count);
        var current = entries;
        int gathered = 0;
        for (int i = 0; i < moves.Count; i++)
        {
            var move = moves[i];
            if (move.Source == move.Target) { gathered++; continue; }        // the gather — legal, and it moves nothing
            if (TryBuildMove(current, move.Source, move.Target, move.Placement, out var op, out var r) && op is not null)
            {
                built.Add(op);
                current = ApplyLocally(current, [op]);                       // the stream the NEXT op indexes into
                continue;
            }
            if (r is RootlistMoveCheck.NoOp or RootlistMoveCheck.SameItem) continue;
            reason = r;
            return false;
        }
        if (gathered == moves.Count) { reason = RootlistMoveCheck.SameItem; return false; }
        if (SameStream(entries, current)) { reason = RootlistMoveCheck.NoOp; return false; }
        ops = built;
        reason = RootlistMoveCheck.Ok;
        return true;
    }

    /// <summary>Would this batch be accepted, and if not why? The pure mirror of <see cref="TryBuildMoves"/> (it IS that
    /// builder with the ops discarded), so the drop cue and the writer cannot disagree.</summary>
    public static RootlistMoveCheck CheckMoves(IReadOnlyList<RootlistEntry> entries, IReadOnlyList<RootlistMove> moves)
    {
        TryBuildMoves(entries, moves, out _, out var reason);
        return reason;
    }

    static bool SameStream(IReadOnlyList<RootlistEntry> a, IReadOnlyList<RootlistEntry> b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (!string.Equals(a[i].Uri, b[i].Uri, StringComparison.Ordinal)) return false;
        return true;
    }

    // ── folder CRUD (the pure builders; every one of them is byte-exact against a desktop capture) ────────────────────
    // A folder is a BALANCED MARKER PAIR inside the flat rootlist item stream:
    //     spotify:start-group:{groupId}:{urlencoded name}   …children…   spotify:end-group:{groupId}
    // so every folder operation is an ordinary index ADD/REM on that stream. All three builders are pure: they take the
    // CURRENT entries, return the ops, and never touch the store — which is what makes them testable against the wire
    // goldens and re-runnable after a 409 rebase.

    /// <summary>The start marker uri for a folder. The name is url-encoded with SPACE AS <c>+</c> — desktop's exact
    /// encoding (a164 "New+Folder", b037 "named+folder+update").</summary>
    public static string StartGroupUri(string groupId, string name)
        => "spotify:start-group:" + groupId + ":" + EncodeFolderName(name);

    public static string EndGroupUri(string groupId) => "spotify:end-group:" + groupId;

    static string EncodeFolderName(string name) => Uri.EscapeDataString(name).Replace("%20", "+", StringComparison.Ordinal);

    /// <summary>Where a new playlist/folder goes for a placement: index 0 at the top level, or immediately AFTER the
    /// parent folder's start-group marker (so it becomes that folder's first child). -1 = the parent folder is not in
    /// the rootlist any more.</summary>
    public static int PlacementIndex(IReadOnlyList<RootlistEntry> entries, RootlistPlacement placement)
    {
        if (placement.ParentFolderId is not { Length: > 0 } parent) return 0;
        int start = FindFolderStart(entries, parent);
        return start < 0 ? -1 : start + 1;
    }

    /// <summary>Index of a folder's start-group marker, or -1.</summary>
    public static int FindFolderStart(IReadOnlyList<RootlistEntry> entries, string groupId)
    {
        for (int i = 0; i < entries.Count; i++)
            if (entries[i].Kind == 1 && string.Equals(GroupId(entries[i].Uri), groupId, StringComparison.Ordinal)) return i;
        return -1;
    }

    /// <summary>Create a folder: ONE delta, TWO index ADDs — the start marker at <paramref name="insertAt"/> and the end
    /// marker directly after it, both stamped with the same create timestamp (golden a164). The pair is created empty;
    /// items are moved in afterwards.</summary>
    public static IReadOnlyList<PlaylistOp> BuildCreateFolder(
        IReadOnlyList<RootlistEntry> entries, string groupId, string name, int insertAt, long nowMs)
    {
        int at = insertAt < 0 ? 0 : insertAt > entries.Count ? entries.Count : insertAt;
        return new[]
        {
            new PlaylistOp(PlaylistOpKind.Add, FromIndex: at,
                Items: new[] { new PlaylistMember("", StartGroupUri(groupId, name), null, nowMs) }),
            new PlaylistOp(PlaylistOpKind.Add, FromIndex: at + 1,
                Items: new[] { new PlaylistMember("", EndGroupUri(groupId), null, nowMs) }),
        };
    }

    /// <summary>Rename a folder: ONE delta, REM{from,len=1} carrying NO items, then ADD re-inserting the start marker at
    /// the same index with the new name and — the load-bearing part — the marker's ORIGINAL create timestamp (goldens
    /// b037 inner / b128 root-level). The end marker is never touched, so the children stay exactly where they are.
    /// <para>Returns null when the folder is no longer in <paramref name="entries"/>. When the stored row carries no
    /// timestamp (a rootlist adopted before schema v8, or a marker the server sent without attributes) the caller is
    /// expected to have bootstrapped already; <paramref name="nowMs"/> is the last-resort stamp, and it is logged.</para></summary>
    public static IReadOnlyList<PlaylistOp>? BuildRenameFolder(
        IReadOnlyList<RootlistEntry> entries, string groupId, string name, long nowMs)
    {
        int start = FindFolderStart(entries, groupId);
        if (start < 0) return null;
        long ts = entries[start].AddedAtMs;
        if (ts <= 0)
        {
            PlaylistMutationDiagnostics.RootlistTimestampMissing(groupId);
            ts = nowMs;
        }
        return new[]
        {
            new PlaylistOp(PlaylistOpKind.Remove, FromIndex: start, Length: 1),
            new PlaylistOp(PlaylistOpKind.Add, FromIndex: start,
                Items: new[] { new PlaylistMember("", StartGroupUri(groupId, name), null, ts) }),
        };
    }

    /// <summary>Delete a folder: remove the END marker first, then the START one — removing the start first would shift
    /// the end marker's index by one. The children between them are NOT removed; with the pair gone they simply belong
    /// to the enclosing level, which is the "playlists inside move up a level" behaviour.
    /// <para>REFERENCE-INFERRED, not desktop-captured: no folder delete appears in either capture. The shape is taken
    /// from the WaveeMusic RootlistService (remove both markers, children stay) and is the exact inverse of the
    /// captured create.</para>
    /// <para>Returns null when the folder is no longer in <paramref name="entries"/> or its markers are unbalanced.</para></summary>
    public static IReadOnlyList<PlaylistOp>? BuildDeleteFolder(IReadOnlyList<RootlistEntry> entries, string groupId)
    {
        if (!TryRange(entries, new RootlistItemRef(groupId, IsFolder: true), out int start, out int end)) return null;
        int endMarker = end - 1;                       // TryRange's end is exclusive; the last row IS the end marker
        if (endMarker <= start || entries[endMarker].Kind != 2) return null;   // unbalanced: refuse rather than guess
        return new[]
        {
            new PlaylistOp(PlaylistOpKind.Remove, FromIndex: endMarker, Length: 1),
            new PlaylistOp(PlaylistOpKind.Remove, FromIndex: start, Length: 1),
        };
    }

    /// <summary>Apply positional rootlist ops (index ADD / index REM / index MOV) to the marker stream LOCALLY,
    /// producing the rows the server will now hold. EVERY rootlist <c>/changes</c> reply carries revision bookkeeping
    /// only (goldens a164-folder-create-response / b049 prove it: no contents), so the new tree has to be computed here
    /// rather than read out of the response — that is why a move that only POSTed left the sidebar stale. Ops apply in
    /// order, each against the state left by the preceding ones.</summary>
    public static IReadOnlyList<RootlistEntry> ApplyLocally(IReadOnlyList<RootlistEntry> entries, IReadOnlyList<PlaylistOp> ops)
    {
        var uris = new List<string>(entries.Count + ops.Count);
        var stamps = new List<long>(entries.Count + ops.Count);
        for (int i = 0; i < entries.Count; i++) { uris.Add(entries[i].Uri); stamps.Add(entries[i].AddedAtMs); }
        for (int o = 0; o < ops.Count; o++)
        {
            var op = ops[o];
            switch (op.Kind)
            {
                case PlaylistOpKind.Add when op.Items is { Count: > 0 } items:
                    int at = op.AddLast ? uris.Count : op.AddFirst ? 0 : op.FromIndex;
                    if (at < 0 || at > uris.Count) throw new ArgumentOutOfRangeException(nameof(ops), "rootlist ADD index out of range");
                    for (int i = 0; i < items.Count; i++)
                    {
                        uris.Insert(at + i, items[i].ItemUri);
                        stamps.Insert(at + i, items[i].AddedAt);
                    }
                    break;
                case PlaylistOpKind.Remove when !op.ItemsAsKey:
                    if (op.FromIndex < 0 || op.Length <= 0 || op.FromIndex + op.Length > uris.Count)
                        throw new ArgumentOutOfRangeException(nameof(ops), "rootlist REM range out of range");
                    uris.RemoveRange(op.FromIndex, op.Length);
                    stamps.RemoveRange(op.FromIndex, op.Length);
                    break;
                // The positional MOV, with the SAME semantics PlaylistDiffApplier (and the server) run: the destination
                // is expressed against the PRE-removal stream, so a forward move shifts back by the length it lifted.
                case PlaylistOpKind.Move when !op.ItemsAsKey:
                    if (op.FromIndex < 0 || op.Length < 0 || op.FromIndex + op.Length > uris.Count)
                        throw new ArgumentOutOfRangeException(nameof(ops), "rootlist MOV source out of range");
                    if (op.ToIndex < 0 || op.ToIndex > uris.Count)
                        throw new ArgumentOutOfRangeException(nameof(ops), "rootlist MOV dest out of range");
                    var movedUris = uris.GetRange(op.FromIndex, op.Length);
                    var movedStamps = stamps.GetRange(op.FromIndex, op.Length);
                    uris.RemoveRange(op.FromIndex, op.Length);
                    stamps.RemoveRange(op.FromIndex, op.Length);
                    int dest = op.ToIndex > op.FromIndex ? op.ToIndex - op.Length : op.ToIndex;
                    if (dest < 0)
                        throw new ArgumentOutOfRangeException(nameof(ops), "rootlist MOV dest inside the moved range");
                    uris.InsertRange(dest, movedUris);
                    stamps.InsertRange(dest, movedStamps);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(ops), "only positional ADD/REM/MOV rootlist ops can be applied locally");
            }
        }
        return RootlistTreeBuilder.EntriesFromUris(uris, stamps);
    }
    static bool TryRange(IReadOnlyList<RootlistEntry> entries, RootlistItemRef item, out int start, out int end)
    {
        start = -1; end = -1;
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            bool match = item.IsFolder
                ? entry.Kind == 1 && string.Equals(GroupId(entry.Uri), item.Key, StringComparison.Ordinal)
                : entry.Kind == 0 && string.Equals(entry.Uri, item.Key, StringComparison.Ordinal);
            if (!match) continue;
            start = i;
            if (!item.IsFolder) { end = i + 1; return true; }
            int nesting = 0;
            for (int j = i; j < entries.Count; j++)
            {
                if (entries[j].Kind == 1) nesting++;
                else if (entries[j].Kind == 2 && --nesting == 0) { end = j + 1; return true; }
            }
            end = entries.Count; // malformed missing end: move the intact remaining subtree
            return true;
        }
        return false;
    }

    static string GroupId(string uri)
    {
        const string prefix = "spotify:start-group:";
        if (!uri.StartsWith(prefix, StringComparison.Ordinal)) return uri;
        int name = uri.IndexOf(':', prefix.Length);
        return name < 0 ? uri[prefix.Length..] : uri[prefix.Length..name];
    }

}


