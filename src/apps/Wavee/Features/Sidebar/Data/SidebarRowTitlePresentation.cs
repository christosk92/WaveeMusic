namespace Wavee;

// A raw spotify: uri/id is never a useful title. Before this decision existed, every "no title yet" call site in
// SidebarPaneSlot (EntryRow's projected entries, and the pin band/unlisted-pin rows that ride the SAME entries list —
// see SidebarBinderPipeline.ResolveUnlistedPin / SidebarRowPlanner.PlanPinned) fell straight to
// SidebarPaneText.ShortUri(...) the moment an entity's Name facet was still Unknown, so a fresh sidebar (or a pin
// pinned before its identity resolved) drew a block of dimmed "5NdDCZh1OCLkpoXG…" rows for the few seconds the
// catalog needed to answer. The honest state while a title has not arrived is LOADING, never the id — this is the
// ONE pure decision every such row shares, so SidebarPaneSlot renders the sidebar's own skeleton bar in the title's
// place instead of reinventing "is this row nameless" per call site.
//
// ENGINE-FREE (no Signal<T>/Element/Icons/Loc/Tok — System only), like its Data/*.cs siblings: source-included by
// Wavee.Tests without a FluentGpu.Engine reference, so the decision is tested directly rather than through a
// rendered row.

/// <summary>What a row's title slot should show.</summary>
public enum SidebarRowTitle : byte
{
    /// <summary>Render <see cref="SidebarRowTitlePresentation.Text"/> as the title.</summary>
    Text = 0,

    /// <summary>No usable title has arrived yet — render the loading skeleton bar instead of any text (never the raw
    /// uri/id).</summary>
    Skeleton = 1,
}

/// <summary>The resolved title for one row: either real text to render, or "not yet" (<see cref="IsSkeleton"/>).</summary>
public readonly record struct SidebarRowTitlePresentation(SidebarRowTitle Kind, string Text)
{
    public bool IsSkeleton => Kind == SidebarRowTitle.Skeleton;

    static readonly SidebarRowTitlePresentation SkeletonValue = new(SidebarRowTitle.Skeleton, "");

    /// <summary>The ONE priority order every title-bearing row uses:
    /// <list type="number">
    /// <item>an authored <paramref name="labelOverride"/> always wins — a local alias is never "loading", whatever the
    /// underlying entity's own resolution state is;</item>
    /// <item>otherwise <paramref name="resolvedName"/> — a projected <c>SidebarLibraryEntry.Name</c> (or a pin's own
    /// cached name) — once it is non-empty;</item>
    /// <item>otherwise <paramref name="fallbackTitle"/> — a PRIOR successful resolution kept for missing-entity
    /// retention (<c>SidebarItemSpec.FallbackTitle</c>), so a row that resolved once and then went offline still shows
    /// its last-known title rather than regressing to a skeleton;</item>
    /// <item>otherwise the row has no usable text at all yet — <see cref="SidebarRowTitle.Skeleton"/>, never the raw
    /// uri/id (<c>SidebarPaneText.ShortUri</c> stays a caller-side accessibility fallback only, not a title).</item>
    /// </list></summary>
    public static SidebarRowTitlePresentation Resolve(string? labelOverride, string resolvedName, string? fallbackTitle = null)
    {
        if (labelOverride is { Length: > 0 } alias) return new SidebarRowTitlePresentation(SidebarRowTitle.Text, alias);
        if (resolvedName.Length > 0) return new SidebarRowTitlePresentation(SidebarRowTitle.Text, resolvedName);
        if (fallbackTitle is { Length: > 0 } cached) return new SidebarRowTitlePresentation(SidebarRowTitle.Text, cached);
        return SkeletonValue;
    }
}
