// ── Shell/Sidebar.Rules.cs ─────────────────────────────────────────────────────────────────────────────────────────
// the sidebar's pure rules for the selection pill (where the one pill sits), the row subtitle grammar, the typeahead
// focus stops, the label-overflow estimate, the fixed-route pin rules and the menu label clip
//
// Role: CORE
// Plan: docs/plans/wavee/sidebar-rework-implementation.md §P1.2, §P3.9
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

/// <summary>Which container a selection pill lives in (design V.5): the list, the fixed head above it, the footer below.</summary>
public enum SidebarPillLane : byte { List = 0, Head = 1, Footer = 2 }

public static class SidebarPillMotionRules
{
    /// <summary>The pill SLIDES (WinUI's same-level worm) only within ONE container at one depth (same x). Across containers
    /// — head ↔ list ↔ footer — it never slides: it scales out and in place (the non-same-level animation).</summary>
    public static bool Slides(SidebarPillLane from, SidebarPillLane to, float dx)
        => from == to && System.MathF.Abs(dx) < 0.5f;
}

/// <summary>What a row's second line says, as data (formatting is the UI's: it owns the loc table).</summary>
public enum SidebarSubtitleKind : byte { None = 0, Songs = 1, Items = 2, Album = 3, Podcast = 4, Artist = 5, Text = 6, Episodes = 7 }

public readonly record struct SidebarSubtitle(SidebarSubtitleKind Kind, int Count, string Detail)
{
    public static readonly SidebarSubtitle None = new(SidebarSubtitleKind.None, 0, "");
}

/// <summary>The subtitle grammar (design P.1): Your Episodes counts "episodes", a playlist with an episode "items", any other
/// playlist "songs";
/// an album is "Album · first artist"; a show "Podcast · publisher"; an artist "Artist"; a folder "N items"; a track or
/// route its creator. An UNKNOWN count shows no subtitle — never "0 songs" (bug A1: gate on CountKnown).</summary>
public static class SidebarSubtitleRules
{
    public static SidebarSubtitle Of(in SidebarLibraryEntry e) => e.Kind switch
    {
        SidebarEntryKind.Playlist => !e.CountKnown ? SidebarSubtitle.None
            : new SidebarSubtitle(e.Episodes ? SidebarSubtitleKind.Episodes
                : e.HasEpisodes ? SidebarSubtitleKind.Items : SidebarSubtitleKind.Songs, e.TrackCount, ""),
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
        or SidebarRowKind.IconRow or SidebarRowKind.EntityRow or SidebarRowKind.FolderHeader;

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

/// <summary>The fixed-home routes (design D10 + Q1a): Home is always first and Liked Songs has a fixed home in each
/// layout, so neither is EVER a pin — not by the menu, not by a drop, not from the server (Spotify pins Liked Songs by
/// default; that server pin stays on the server, untouched, and never reaches the sidebar), not from a migrated file.</summary>
public static class SidebarPinRules
{
    public static bool IsFixedRoute(string? pinId) => pinId is "home" or "liked";

    /// <summary>The drag chip's caption key when a drag over Pinned (a pin row or the empty "Drop here to pin" band) can
    /// NOT be pinned — design corner case "Pin of an unpinnable thing (track, episode, local file, search result, Home)":
    /// the menu item is absent (<c>PinRowRule</c>) and a drop is refused with a reason. A search result is one of these
    /// kinds, so it needs no arm. Null when the payload pins. Order: a local file before the plain track (a local file
    /// IS a track payload), then the fixed routes.</summary>
    public static string? RefusalKeyOf(DragKind kind, string? id, string? uri)
    {
        if (kind == DragKind.Track && uri is { } u
            && (u.StartsWith("wavee:local:", System.StringComparison.Ordinal) || u.StartsWith("local:", System.StringComparison.Ordinal)))
            return "sidebar.pin.cantPin.local";
        if (kind == DragKind.Track) return "sidebar.pin.cantPin.track";
        if (kind == DragKind.Episode) return "sidebar.pin.cantPin.episode";
        if (kind == DragKind.Route && id == "home") return "sidebar.pin.cantPin.home";
        if (kind == DragKind.Route && id == "liked") return "sidebar.pin.cantPin.liked";
        return null;
    }

    /// <summary>Does a row draw the 12-px pin mark? In Your Library the pins have no header, so each depth-0 row of the
    /// pinned section is marked; the children an expanded pinned folder lists under it (depth ≥ 1) are not pins and are
    /// not marked. Everywhere else the existing rule holds: a pinned entity that is not a track (#85).</summary>
    public static bool ShowsPinMark(SidebarLayoutId layout, SidebarSectionKind section, int rowDepth, bool isPinned, bool isTrack)
        => (layout == SidebarLayoutId.Library && section == SidebarSectionKind.Pinned && rowDepth == 0)
           || SidebarRowGeometry.ShowsPinGlyph(isPinned, isTrack);
}

/// <summary>Menu-row label hygiene — pure, engine-free, and therefore directly testable. A context-menu row label
/// grows to fit its text with no trimming of its own, so a long DYNAMIC label (an interpolated playlist name) would
/// otherwise clip: it widens the whole flyout, and every other row with it. The fix is minting the label pre-clipped
/// before it reaches the loc format string.</summary>
public static class MenuLabel
{
    /// <summary>The default clip width for an interpolated entity name inside a menu label, in characters. Sized
    /// against the 250-DIP context-menu minimum: ~28 characters of 14px UI text plus the surrounding verb fills that
    /// column without widening it.</summary>
    public const int NameChars = 28;

    /// <summary>Clip <paramref name="name"/> to <paramref name="max"/> characters, ending in a single ellipsis.
    /// Shorter names (and a null/empty one) come back untouched — the ellipsis appears only when something was
    /// actually dropped.</summary>
    public static string Clip(string? name, int max = NameChars)
    {
        if (name is not { Length: > 0 }) return "";
        if (max < 1) return "…";
        if (name.Length <= max) return name;
        // Trim the trailing space the cut usually lands on, so the result is "Late night…" not "Late night …".
        return string.Concat(name.AsSpan(0, max - 1).TrimEnd(), "…");
    }
}
