// ── Theming/Contracts/NavFlyouts.cs — the props of the compact rail's two flyouts (themes plan 6.1, T5c) ───────────────────────
//
// A collapsed section's tile and a folder's tile in the compact rail open a flyout beside them. The core pane keeps everything that
// DECIDES: which flyout is open (one at a time, a second click closes it), where it anchors and how it dismisses (right of the
// tile, focus trap, light dismiss, constrained to the root), which rows exist, what a row's menu holds, what a drop does and what
// activating a row means (`INavRowHost`: `PlanSectionExpanded`, `EntryMenu`, `FolderMenu`, `ResourceDropSpec`, `RailFolderDropSpec`,
// `Navigate`, `ExpandFolderInPane`), and the drill-in stack (`SidebarFolderFlyoutNav`, pure). The structure draws the panel: the
// width, the list, the header and its back chevron, the page slide and the keyboard cursor's highlight.
//
// Both records are built fresh on every open (a flyout always starts at the tile that opened it), so the props are mount seeds.
// They carry the pane as `INavRowHost`, which is internal, so the host member is internal too: only the assembly's structures read it.
//
// WVTH006: sealed records with no writable member.

namespace Wavee.Theming.Contracts;

/// <summary>The props of <see cref="ViewKeys.ShellNavSectionFlyout"/>: a collapsed section's rows.</summary>
public sealed record NavSectionFlyoutProps
{
    internal NavSectionFlyoutProps(INavRowHost host, string sectionId, Action close)
    {
        Host = host; SectionId = sectionId; Close = close;
    }

    /// <summary>The pane the flyout opened from.</summary>
    internal INavRowHost Host { get; }

    /// <summary>The collapsed section whose rows the flyout lists.</summary>
    public string SectionId { get; }

    /// <summary>Closes the flyout (a row that navigates closes it; a folder row nests in place and does not).</summary>
    public Action Close { get; }
}

/// <summary>The props of <see cref="ViewKeys.ShellNavFolderFlyout"/>: a compact folder tile's children panel.</summary>
public sealed record NavFolderFlyoutProps
{
    internal NavFolderFlyoutProps(INavRowHost host, string sectionId, string rootFolderId, string rootFolderName, Action close)
    {
        Host = host; SectionId = sectionId; RootFolderId = rootFolderId; RootFolderName = rootFolderName; Close = close;
    }

    /// <summary>The pane the flyout opened from.</summary>
    internal INavRowHost Host { get; }

    /// <summary>The section the tile came from: the menus' section and the drop spec's reorder identity.</summary>
    public string SectionId { get; }

    /// <summary>The clicked folder: the stack starts here.</summary>
    public string RootFolderId { get; }

    /// <summary>The clicked folder's name (the header until a nested level replaces it).</summary>
    public string RootFolderName { get; }

    /// <summary>Closes the flyout (activating a route row, or a menu verb that shows the full pane).</summary>
    public Action Close { get; }
}
