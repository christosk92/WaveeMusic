// ── Shell/Sidebar.Rules.cs ─────────────────────────────────────────────────────────────────────────────────────────
// the sidebar's pure rules for the selection pill (where the one pill sits), the row subtitle grammar, the typeahead
// focus stops and the label-overflow estimate
//
// Role: CORE
// Plan: docs/plans/wavee/sidebar-rework-implementation.md §P1.2
//
// Engine-free and allocation-free after warm-up, so Wavee.Tests pins every decision here. The UI (Sidebar.UI*.cs)
// only draws what these rules decide: the pill's row, the subtitle's words and the focus stops.

using System.Collections.Generic;

namespace Wavee;

/// <summary>Where the one selection pill sits (design V.5).</summary>
public enum SidebarPillAnchor : byte { None = 0, Row = 1, AncestorFolder = 2, SectionHeader = 3 }

/// <summary>The pill's plan index and why it is there. <see cref="None"/> ⇒ no pill (the route is in a hidden section,
/// or nowhere in the sidebar).</summary>
public readonly record struct SidebarPillTarget(int PlanIndex, SidebarPillAnchor Anchor)
{
    public static readonly SidebarPillTarget None = new(-1, SidebarPillAnchor.None);
}

/// <summary>THE ONE-PILL RULE (design V.5): exactly one pill, on (1) the visible row for the route, else (2) the deepest
/// visible ancestor folder (NavigationView.cpp:5555-5586), else (3) the header — or the compact rail's section tile — of
/// the collapsed section holding it (a Wavee extension), else (4) nowhere. Navigation is never affected.</summary>
public static class SidebarPillRules
{
    /// <param name="routeAt">The pane's row → route projection (null/"" = not a navigation target).</param>
    /// <param name="ancestorFolderIds">The route's containing folders, DEEPEST first (<see cref="AncestorFolders"/>).</param>
    /// <param name="owningSectionId">The section that would show the route if it were expanded, or null.</param>
    public static SidebarPillTarget Resolve(IReadOnlyList<SidebarRow> rows, IReadOnlyList<SidebarLibraryEntry> entries,
        System.Func<int, string?> routeAt, string? route, IReadOnlyList<string> ancestorFolderIds, string? owningSectionId)
    {
        if (string.IsNullOrEmpty(route)) return SidebarPillTarget.None;
        int row = SidebarRowGeometry.IndexOfRoute(rows.Count, routeAt, route);
        if (row >= 0) return new SidebarPillTarget(row, SidebarPillAnchor.Row);
        for (int a = 0; a < ancestorFolderIds.Count; a++)
        {
            int folder = SidebarRowGeometry.FolderHeaderIndexOf(rows, entries, ancestorFolderIds[a]);
            if (folder >= 0) return new SidebarPillTarget(folder, SidebarPillAnchor.AncestorFolder);
        }
        if (owningSectionId is { Length: > 0 } section)
            for (int i = 0; i < rows.Count; i++)
                if (IsSectionAnchor(rows[i].Kind) && string.Equals(rows[i].SectionId, section, System.StringComparison.Ordinal))
                    return new SidebarPillTarget(i, SidebarPillAnchor.SectionHeader);
        return SidebarPillTarget.None;
    }

    /// <summary>A row that can stand for a whole collapsed section.</summary>
    public static bool IsSectionAnchor(SidebarRowKind kind) => kind is SidebarRowKind.SectionHeader or SidebarRowKind.SectionTile;

    /// <summary>The folders containing <paramref name="route"/> in a depth-first flattened tree, deepest first, into a
    /// caller-owned list (cleared first). Walks backwards from the route's entry, taking each shallower folder once.</summary>
    public static void AncestorFolders(IReadOnlyList<SidebarLibraryEntry>? tree, string? route, List<string> into)
    {
        into.Clear();
        if (tree is null || string.IsNullOrEmpty(route)) return;
        int at = -1;
        for (int i = 0; i < tree.Count; i++)
            if (string.Equals(tree[i].Id, route, System.StringComparison.Ordinal)) { at = i; break; }
        if (at < 0) return;
        int depth = tree[at].Depth;
        for (int i = at - 1; i >= 0 && depth > 0; i--)
        {
            var e = tree[i];
            if (!e.IsFolder || e.Depth >= depth) continue;
            into.Add(e.FolderId.Length > 0 ? e.FolderId : e.Id);
            depth = e.Depth;
        }
    }
}

/// <summary>What a row's second line says, as data (formatting is the UI's: it owns the loc table).</summary>
public enum SidebarSubtitleKind : byte { None = 0, Songs = 1, Items = 2, Album = 3, Podcast = 4, Artist = 5, Text = 6 }

public readonly record struct SidebarSubtitle(SidebarSubtitleKind Kind, int Count, string Detail)
{
    public static readonly SidebarSubtitle None = new(SidebarSubtitleKind.None, 0, "");
}

/// <summary>The subtitle grammar (design P.1): a playlist with an episode counts "items", any other playlist "songs";
/// an album is "Album · first artist"; a show "Podcast · publisher"; an artist "Artist"; a folder "N items"; a track or
/// route its creator. An UNKNOWN count shows no subtitle — never "0 songs" (bug A1: gate on CountKnown).</summary>
public static class SidebarSubtitleRules
{
    public static SidebarSubtitle Of(in SidebarLibraryEntry e) => e.Kind switch
    {
        SidebarEntryKind.Playlist => !e.CountKnown ? SidebarSubtitle.None
            : new SidebarSubtitle(e.HasEpisodes ? SidebarSubtitleKind.Items : SidebarSubtitleKind.Songs, e.TrackCount, ""),
        SidebarEntryKind.Album => new SidebarSubtitle(SidebarSubtitleKind.Album, 0,
            e.FirstArtistName.Length > 0 ? e.FirstArtistName : e.Creator),
        SidebarEntryKind.Show => new SidebarSubtitle(SidebarSubtitleKind.Podcast, 0, e.Publisher),
        SidebarEntryKind.Artist => new SidebarSubtitle(SidebarSubtitleKind.Artist, 0, ""),
        SidebarEntryKind.Folder => e.CountKnown ? new SidebarSubtitle(SidebarSubtitleKind.Items, e.ChildCount, "") : SidebarSubtitle.None,
        SidebarEntryKind.Track or SidebarEntryKind.AppRoute => e.Creator.Length > 0
            ? new SidebarSubtitle(SidebarSubtitleKind.Text, 0, e.Creator) : SidebarSubtitle.None,
        _ => SidebarSubtitle.None,
    };
}

/// <summary>Which plan rows take keyboard focus and what typeahead reads from them (the ItemsView's own roving and
/// typeahead do the rest — <c>ListOptions.IsItemEnabled</c> / <c>ItemText</c>).</summary>
public static class SidebarTypeAheadRules
{
    /// <summary>Headers, collapsed-section tiles, glyph rows, entity rows and folders are focus stops; separators, hints,
    /// skeletons, drop bands and the tree's end gutter are not.</summary>
    public static bool IsFocusStop(SidebarRowKind kind) => kind is SidebarRowKind.SectionHeader or SidebarRowKind.SectionTile
        or SidebarRowKind.IconRow or SidebarRowKind.EntityRow or SidebarRowKind.FolderHeader or SidebarRowKind.Placeholder;

    /// <summary>The typeahead text of a row: its label for a focus stop, "" otherwise.</summary>
    public static string TextOf(SidebarRowKind kind, string label) => IsFocusStop(kind) ? label : "";
}

/// <summary>Does a one-line label overflow its column? An ESTIMATE (the engine exposes no "was trimmed" fact): Segoe UI
/// Variable at 14 px averages ~7 DIP per character. Used only to decide whether a row carries its label as a tooltip, so
/// a near miss costs a redundant tooltip, never a missing one for a clearly long title.</summary>
public static class SidebarLabelFit
{
    public const float AverageCharWidth = 7f;

    /// <summary>The label column of a row in a pane <paramref name="paneWidth"/> wide at <paramref name="depth"/>, with
    /// <paramref name="trailing"/> DIP of trailing content.</summary>
    public static float LabelWidth(float paneWidth, int depth, float trailing)
        => paneWidth - 2f * SidebarRowGeometry.PaneEdge - SidebarRowGeometry.IndentFor(depth) - SidebarRowGeometry.IconColumn
           - SidebarRowGeometry.LabelGap - SidebarRowGeometry.TrailingPad - trailing;

    public static bool Overflows(string? label, float labelWidth)
        => label is { Length: > 0 } && label.Length * AverageCharWidth > labelWidth;
}
