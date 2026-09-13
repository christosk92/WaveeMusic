using System;
using System.Collections.Generic;

namespace Wavee;

// The four first-party sources that read the BINDER'S OWN projection — wavee.library, wavee.playlistTree,
// wavee.history.visited, wavee.history.played.
//
// Data/Sources/*.cs is deliberately one level BELOW the source-included glob (`Features\Sidebar\Data\*.cs`): these files
// hold engine-bound services, while everything decidable lives in the engine-free half (SidebarSourceMap /
// SidebarBinderPipeline / SidebarDataSource), which the tests drive.
//
// THREADING: UI thread only. Fill runs on the rebuild path — no allocation beyond list growth, no LINQ, no closures, and
// only SetHealthQuiet (never Raise, which would re-enter the binder).

/// <summary>
/// The live projection the first-party sources read, owned by <c>SidebarProjectionBinder</c>. The binder builds the
/// projection FIRST and resolves contributions second, so a source that reads this during a fill always sees the current
/// pass — never the previous one.
/// </summary>
public interface ISidebarProjectionSnapshot
{
    /// <summary>The unified projection over every kind, in SOURCE order (unsorted, unfiltered).</summary>
    IReadOnlyList<SidebarLibraryEntry> All { get; }

    /// <summary>The rootlist tree, depth-first flattened with <c>Depth</c> stamped and folders carried as
    /// <c>SidebarEntryKind.Folder</c> rows.</summary>
    IReadOnlyList<SidebarLibraryEntry> Tree { get; }

    /// <summary>id/uri → entry, for the feed sources' join.</summary>
    SidebarSourceIndex Index { get; }

    SidebarSourceState LibraryState { get; }
    SidebarSourceState TreeState { get; }

    /// <summary>Navigation history, oldest first (empty until the shell attaches its <c>HistoryStore</c>).</summary>
    IReadOnlyList<SidebarVisit> Visits { get; }

    /// <summary>Playback history, newest first, context-collapsed (empty when no play log is wired).</summary>
    IReadOnlyList<SidebarPlayedContext> Played { get; }
}

/// <summary>
/// <c>wavee.library</c> — the unified library projection as a contributed source. The ONE source that honours the full
/// filter/sort surface, so an Extension section can express anything a built-in <c>EntityList</c> can, including the
/// include/exclude uri sets ("only these artists" without a hand-maintained item list).
/// </summary>
public sealed class SidebarLibrarySource : SidebarDataSourceBase
{
    // Reused across fills: the include/exclude sets are read out of the (opaque) section config, which is JSON.
    readonly List<string> _include = new();
    readonly List<string> _exclude = new();
    readonly ISidebarProjectionSnapshot _snapshot;

    public SidebarLibrarySource(ISidebarProjectionSnapshot snapshot) : base(SidebarContributions.Library)
        => _snapshot = snapshot;

    public override SidebarSourceItemType ItemType => SidebarSourceItemType.Entity;

    public override SidebarSourceFilters SupportedFilters =>
        SidebarSourceFilters.Kinds | SidebarSourceFilters.Qualifier | SidebarSourceFilters.Search
        | SidebarSourceFilters.IncludeExcludeUris;

    public override SidebarSourceSorts SupportedSorts => SidebarSourceSorts.All;
    public override SidebarSourcePaging Paging => SidebarSourcePaging.TopN;

    public override SidebarConfigSchema ConfigSchema { get; } = new(1,
    [
        new SidebarConfigField("kinds", SidebarConfigFieldKind.Enum, "sidebar.source.library.kinds",
            DefaultJson: "\"all\"", EnumValues: ["all", "playlists", "albums", "artists", "shows"]),
        new SidebarConfigField("sort", SidebarConfigFieldKind.Enum, "sidebar.source.library.sort",
            DefaultJson: "\"recents\"", EnumValues: ["recents", "added", "alphabetical", "creator"]),
        new SidebarConfigField("descending", SidebarConfigFieldKind.Bool, "sidebar.source.library.descending",
            DefaultJson: "true"),
        new SidebarConfigField("qualifier", SidebarConfigFieldKind.Enum, "sidebar.source.library.qualifier",
            DefaultJson: "\"any\"", EnumValues: ["any", "byYou", "bySpotify", "mixed"]),
        new SidebarConfigField("includeUris", SidebarConfigFieldKind.UriList, "sidebar.source.library.includeUris"),
        new SidebarConfigField("excludeUris", SidebarConfigFieldKind.UriList, "sidebar.source.library.excludeUris"),
        new SidebarConfigField("maxItems", SidebarConfigFieldKind.Int, "sidebar.source.library.maxItems",
            Min: 0, Max: 500),
    ]);

    public override int Fill(List<SidebarLibraryEntry> into, in SidebarSourceRequest request)
    {
        var all = _snapshot.All;
        SetHealthQuiet(all.Count == 0 ? _snapshot.LibraryState : SidebarSourceState.Ready);

        var kinds = KindsOf(request.Config.Str("kinds"));
        byte qualifier = QualifierOf(request.Config.Str("qualifier"));
        string search = SidebarSearch.Normalize(request.Search);
        bool searching = search.Length > 0;

        _include.Clear();
        _exclude.Clear();
        request.Config.Strings("includeUris", _include);
        request.Config.Strings("excludeUris", _exclude);

        int max = Max(request, request.Config.Int("maxItems"));
        int start = into.Count;
        for (int i = 0; i < all.Count && into.Count - start < max; i++)
        {
            var e = all[i];
            if (!SidebarEntryKinds.Has(kinds, e.Kind)) continue;
            if (searching && e.Kind == SidebarEntryKind.Folder) continue;
            if (qualifier != 0 && (e.Kind != SidebarEntryKind.Playlist || !e.MatchesQualifier(qualifier))) continue;
            if (searching && !SidebarSearch.Matches(in e, search)) continue;
            if (_include.Count > 0 && !Contains(_include, in e)) continue;
            if (_exclude.Count > 0 && Contains(_exclude, in e)) continue;
            into.Add(e);
        }

        int count = into.Count - start;
        if (count > 1)
        {
            // Sort ONLY this source's slice: the pool is shared by every extension section in the document.
            var sorted = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(into).Slice(start, count);
            sorted.Sort(SidebarSort.For(SortOf(request.Config.Str("sort")),
                                        request.Config.Bool("descending", true)));
        }
        return count;
    }

    static bool Contains(List<string> uris, in SidebarLibraryEntry e)
    {
        for (int i = 0; i < uris.Count; i++)
            if (string.Equals(uris[i], e.Uri, StringComparison.Ordinal)
                || string.Equals(uris[i], e.Id, StringComparison.Ordinal)) return true;
        return false;
    }

    internal static int Max(in SidebarSourceRequest request, int configured, int fallback = 500)
    {
        int m = request.MaxItems > 0 ? request.MaxItems : configured;
        return m > 0 ? m : fallback;
    }

    static SidebarEntryKindMask KindsOf(string? kinds) => kinds switch
    {
        "playlists" => SidebarEntryKindMask.PlaylistTree,
        "albums" => SidebarEntryKindMask.Album,
        "artists" => SidebarEntryKindMask.Artist,
        "shows" => SidebarEntryKindMask.Show,
        _ => SidebarEntryKindMask.All,
    };

    static byte QualifierOf(string? qualifier) => qualifier switch
    {
        "byYou" => (byte)SidebarPlaylistFlavor.ByYou,
        "bySpotify" => (byte)SidebarPlaylistFlavor.BySpotify,
        "mixed" => (byte)SidebarPlaylistFlavor.Mixed,
        _ => (byte)0,
    };

    static SidebarV3Sort SortOf(string? sort) => sort switch
    {
        "added" => SidebarV3Sort.RecentlyAdded,
        "alphabetical" => SidebarV3Sort.Alphabetical,
        "creator" => SidebarV3Sort.Creator,
        _ => SidebarV3Sort.Recents,
    };
}

/// <summary><c>wavee.playlistTree</c> — the folder-aware rootlist tree, depth-first flattened. Search flattens to matching
/// leaves (a folder is a container, not a result), exactly as a built-in PlaylistTree section behaves.</summary>
public sealed class SidebarPlaylistTreeSource : SidebarDataSourceBase
{
    readonly ISidebarProjectionSnapshot _snapshot;

    public SidebarPlaylistTreeSource(ISidebarProjectionSnapshot snapshot) : base(SidebarContributions.PlaylistTree)
        => _snapshot = snapshot;

    public override SidebarSourceFilters SupportedFilters => SidebarSourceFilters.Search;
    public override SidebarSourceSorts SupportedSorts => SidebarSourceSorts.SourceOrder;

    public override int Fill(List<SidebarLibraryEntry> into, in SidebarSourceRequest request)
    {
        var tree = _snapshot.Tree;
        SetHealthQuiet(tree.Count == 0 ? _snapshot.TreeState : SidebarSourceState.Ready);

        string search = SidebarSearch.Normalize(request.Search);
        bool searching = search.Length > 0;
        int max = SidebarLibrarySource.Max(request, request.Config.Int("maxItems"), 5000);
        int n = 0;
        for (int i = 0; i < tree.Count && n < max; i++)
        {
            var e = tree[i];
            if (searching)
            {
                if (e.Kind == SidebarEntryKind.Folder || !SidebarSearch.Matches(in e, search)) continue;
            }
            into.Add(e);
            n++;
        }
        return n;
    }
}

/// <summary><c>wavee.history.visited</c> — recently OPENED (HistoryStore). NOT "recently played": the label must stay
/// honest (see <c>SidebarRecency</c>'s semantics note).</summary>
public sealed class SidebarVisitedSource : SidebarDataSourceBase
{
    readonly ISidebarProjectionSnapshot _snapshot;

    public SidebarVisitedSource(ISidebarProjectionSnapshot snapshot) : base(SidebarContributions.HistoryVisited)
        => _snapshot = snapshot;

    public override SidebarSourceItemType ItemType => SidebarSourceItemType.Mixed;   // entities AND app routes
    public override SidebarSourceSorts SupportedSorts => SidebarSourceSorts.SourceOrder;

    public override SidebarConfigSchema ConfigSchema { get; } = new(1,
    [
        new SidebarConfigField("maxItems", SidebarConfigFieldKind.Int, "sidebar.source.recents.maxItems",
            DefaultJson: "6", Min: 1, Max: 40),
    ]);

    public override int Fill(List<SidebarLibraryEntry> into, in SidebarSourceRequest request)
    {
        SetHealthQuiet(SidebarSourceState.Ready);   // a local log is never pending: an empty log is an empty section
        return SidebarSourceMap.Visited(_snapshot.Visits,
            static v => v.RouteKey, static v => v.TicksUtc,
            _snapshot.Index, into, SidebarLibrarySource.Max(request, request.Config.Int("maxItems"), 6));
    }
}

/// <summary><c>wavee.history.played</c> — recently PLAYED (PlayLogStore), collapsed to distinct contexts.</summary>
public sealed class SidebarPlayedSource : SidebarDataSourceBase
{
    readonly ISidebarProjectionSnapshot _snapshot;

    public SidebarPlayedSource(ISidebarProjectionSnapshot snapshot) : base(SidebarContributions.HistoryPlayed)
        => _snapshot = snapshot;

    public override SidebarSourceItemType ItemType => SidebarSourceItemType.Mixed;   // containers AND bare tracks
    public override SidebarSourceSorts SupportedSorts => SidebarSourceSorts.SourceOrder;

    public override SidebarConfigSchema ConfigSchema { get; } = new(1,
    [
        new SidebarConfigField("maxItems", SidebarConfigFieldKind.Int, "sidebar.source.recents.maxItems",
            DefaultJson: "6", Min: 1, Max: 40),
    ]);

    public override int Fill(List<SidebarLibraryEntry> into, in SidebarSourceRequest request)
    {
        SetHealthQuiet(SidebarSourceState.Ready);
        return SidebarSourceMap.Played(_snapshot.Played, _snapshot.Index, into,
            SidebarLibrarySource.Max(request, request.Config.Int("maxItems"), 6));
    }
}
