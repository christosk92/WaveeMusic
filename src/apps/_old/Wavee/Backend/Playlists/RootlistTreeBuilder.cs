using System;
using System.Collections.Generic;
using Wavee.Backend.Persistence;
using Wavee.Core;

namespace Wavee.Backend.Playlists;

// Turns the flat rootlist marker stream (persisted as ordered ColdRootlistEntry rows) into the sidebar PlaylistNode tree.
// Markers: kind 0 = a playlist uri, kind 1 = start-group, kind 2 = end-group. The playlist header (name/cover/owner) is
// resolved from the shared Store via the injected resolver, so this stays pure + unit-testable.
public static class RootlistTreeBuilder
{
    /// <summary>The ONE marker shape the tree walk understands — the shared shape of a cold row and a live row, so the
    /// parse loop exists once and both public overloads are thin adapters over it.</summary>
    public readonly record struct RootlistMarker(int Kind, string Uri, string? GroupName);

    /// <summary>Cold (persisted) rows → the tree.</summary>
    public static IReadOnlyList<PlaylistNode> Build(IReadOnlyList<ColdRootlistEntry> entries, Func<string, PlaylistSummary> resolve)
    {
        var markers = new RootlistMarker[entries.Count];
        for (int i = 0; i < entries.Count; i++) markers[i] = new RootlistMarker(entries[i].Kind, entries[i].Uri, entries[i].GroupName);
        return BuildCore(markers, resolve);
    }

    /// <summary>Live (in-memory Store) rows → the tree. Same parse, no duplicated loop — StoreLibrarySource reads
    /// <c>_store.Rootlist()</c>, which is the RootlistEntry shape.</summary>
    public static IReadOnlyList<PlaylistNode> Build(IReadOnlyList<RootlistEntry> entries, Func<string, PlaylistSummary> resolve)
    {
        var markers = new RootlistMarker[entries.Count];
        for (int i = 0; i < entries.Count; i++) markers[i] = new RootlistMarker(entries[i].Kind, entries[i].Uri, entries[i].GroupName);
        return BuildCore(markers, resolve);
    }

    // Folders are RECURSIVE: an end-group pushes a PlaylistFolder NODE into its parent's node list (it no longer
    // flattens its children up one level), so folder-in-folder survives to the sidebar. The marker rows' own Depth
    // column is deliberately NOT read — nesting depth is derived from the start/end markers alone, so a malformed depth
    // value can never reshape the tree.
    static IReadOnlyList<PlaylistNode> BuildCore(IReadOnlyList<RootlistMarker> markers, Func<string, PlaylistSummary> resolve)
    {
        var top = new List<PlaylistNode>();
        var open = new Stack<(string Id, string Name, List<PlaylistNode> Items)>();

        for (int i = 0; i < markers.Count; i++)
        {
            var e = markers[i];
            switch (e.Kind)
            {
                case 1:   // start-group
                    open.Push((GroupId(e.Uri), e.GroupName ?? "", new List<PlaylistNode>()));
                    break;

                case 2:   // end-group (an end without a matching start is ignored — nothing to close)
                    if (open.Count > 0)
                    {
                        var f = open.Pop();
                        var folder = new PlaylistFolder(f.Id, f.Name, f.Items);
                        (open.Count > 0 ? open.Peek().Items : top).Add(folder);
                    }
                    break;

                default:  // a playlist (or any item) uri
                    if (EntityUri.KindOf(e.Uri) == EntityKind.Playlist)
                    {
                        var leaf = new PlaylistLeaf(resolve(e.Uri));
                        (open.Count > 0 ? open.Peek().Items : top).Add(leaf);
                    }
                    break;
            }
        }

        // Unbalanced markers (a missing end-group) must not swallow the folder + its children — flush what's still open,
        // INNERMOST FIRST, each into the folder that was open around it, so the result is still a well-formed nested tree
        // instead of a pile of sibling folders at the top level.
        while (open.Count > 0)
        {
            var f = open.Pop();
            var folder = new PlaylistFolder(f.Id, f.Name, f.Items);
            (open.Count > 0 ? open.Peek().Items : top).Add(folder);
        }
        return top;
    }

    // "spotify:start-group:{id}:{name}" / "spotify:end-group:{id}" → the {id} segment.
    static string GroupId(string uri)
    {
        var parts = uri.Split(':');
        return parts.Length >= 3 ? parts[2] : uri;
    }

    // ── the ONE home for the flat rootlist marker → ordered RootlistEntry parse ──
    // A rootlist is a playlist whose items are playlist-uri rows interleaved with start-group / end-group markers. Both the
    // full-fetch path (PlaylistFetcher) and the in-place / write-response paths (LibrarySync, RootlistFollowStrategy) build
    // the same ordered rows from a bare uri sequence — so the marker parsing lives here once (kind 0=item, 1=start, 2=end;
    // depth tracks nesting; Position is the flat item index, which the rootlist-changes REM op indexes against).
    public static IReadOnlyList<RootlistEntry> EntriesFromUris(IEnumerable<string> uris)
        => EntriesFromUris(uris, null);

    /// <summary>The same parse, carrying each row's server ADD timestamp (playlist4 <c>ItemAttributes.timestamp</c>).
    /// <paramref name="timestamps"/> is positional against <paramref name="uris"/>; a shorter/absent list leaves the
    /// remaining rows at 0 ("not captured"), which the folder-rename path bootstraps rather than guesses.</summary>
    public static IReadOnlyList<RootlistEntry> EntriesFromUris(IEnumerable<string> uris, IReadOnlyList<long>? timestamps)
    {
        var entries = new List<RootlistEntry>();
        int pos = 0, depth = 0;
        foreach (var uri in uris)
        {
            long ts = timestamps is not null && pos < timestamps.Count ? timestamps[pos] : 0;
            if (uri.StartsWith("spotify:start-group:", StringComparison.Ordinal)) { entries.Add(new RootlistEntry(pos++, 1, uri, GroupNameOf(uri), depth, ts)); depth++; }
            else if (uri.StartsWith("spotify:end-group:", StringComparison.Ordinal)) { depth = Math.Max(0, depth - 1); entries.Add(new RootlistEntry(pos++, 2, uri, null, depth, ts)); }
            else entries.Add(new RootlistEntry(pos++, 0, uri, null, depth, ts));
        }
        return entries;
    }

    // "spotify:start-group:{id}:{name}" → the decoded {name} segment. Desktop encodes a folder name with SPACE AS `+`
    // (a164 "New+Folder", b037 "named+folder+update"; a literal + is %2B), so `+` is a space here — Uri.UnescapeDataString
    // alone would leave the pluses in the label. A name may itself contain `:` — take everything after the third colon.
    static string? GroupNameOf(string uri)
    {
        int a = uri.IndexOf(':', "spotify:".Length);                       // after "start-group"
        int b = a < 0 ? -1 : uri.IndexOf(':', a + 1);                      // after {id}
        if (b < 0) return null;
        return Uri.UnescapeDataString(uri.AsSpan(b + 1).ToString().Replace('+', ' '));
    }
}
