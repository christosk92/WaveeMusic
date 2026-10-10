// ── Shell/Sidebar.Layout.cs ────────────────────────────────────────────────────────────────────────────────────────
// the sidebar's information architecture as data: two layouts (Classic, Library), a closed eight-kind catalogue, the
// per-layout overlay the user edits, the ops that edit it, and the resolved document the planner reads
//
// Role: CORE
// Plan: docs/plans/wavee/sidebar-rework-implementation.md §P3.1-§P3.4, §P3.12 (FallbackTitleKey)
//
// Engine-free; it allocates only per user edit (never per frame), so Wavee.Tests pins every decision here.

using System;
using System.Collections.Generic;

namespace Wavee;

/// <summary>The two sidebar layouts. PERSISTED as <c>sidebar.layout.id</c> (0 Classic · 1 Library) — append only.</summary>
public enum SidebarLayoutId : byte { Classic = 0, Library = 1 }

/// <summary>The closed section catalogue (design C.1). Not persisted by number: the files use <see cref="SidebarCatalogue.IdOf"/>.</summary>
public enum SidebarSectionKind : byte
{
    Home = 0, Pinned = 1, Collections = 2, Playlists = 3, Library = 4, Recent = 5, NewReleases = 6, Settings = 7,
}

/// <summary>Row density for entity rows (design P.4): Default = row C (two lines, art 32), Compact = row B (one line, art
/// 24). Glyph rows and headers are unaffected. PERSISTED as <c>sidebar.pane.density</c>.</summary>
public enum SidebarDensity : byte { Default = 0, Compact = 1 }

/// <summary>Your Library's list presentation.</summary>
public enum SidebarLibraryView : byte { List = 0, Grid = 1 }

/// <summary>Your Library's sort. <see cref="CustomOrder"/> IS the Spotify rootlist order and applies under the Playlists
/// filter only (elsewhere it presents Recents, <see cref="SidebarLibraryHeadRules.Effective"/>; the stored sort is kept). Same values as the deleted V3
/// sort, so a stored int keeps its meaning.</summary>
public enum SidebarLibrarySort : byte { Recents = 0, RecentlyAdded = 1, Alphabetical = 2, Creator = 3, CustomOrder = 4 }

/// <summary>The active filter chip (none, or one kind). PERSISTED as <c>sidebar.library.filter</c>.</summary>
public enum SidebarLibraryFilter : byte { None = 0, Playlists = 1, Albums = 2, Artists = 3, Podcasts = 4, Audiobooks = 5 }

/// <summary>The library kinds a user can hide from Your Library (design Q12: no chip, no page in the dropdown, no items in
/// the unfiltered list).</summary>
[System.Flags]
public enum SidebarLibraryKinds : byte { None = 0, Albums = 1, Artists = 2, Podcasts = 4, Audiobooks = 8 }

/// <summary>THE CATALOGUE (design C.1): which sections each layout has, their stable ids, titles, locks and defaults.
/// Nothing outside this class decides what a section is.</summary>
public static class SidebarCatalogue
{
    public const string HomeRoute = "home";
    public const string SettingsRoute = "settings";
    public const string LikedRoute = "liked";

    /// <summary>Recent / New releases row limits.</summary>
    public static readonly int[] LimitChoices = [5, 10, 20];

    static readonly SidebarSectionKind[] s_classic =
    [
        SidebarSectionKind.Home, SidebarSectionKind.Pinned, SidebarSectionKind.Collections, SidebarSectionKind.Playlists,
        SidebarSectionKind.Recent, SidebarSectionKind.NewReleases, SidebarSectionKind.Settings,
    ];
    static readonly SidebarSectionKind[] s_library =
        [SidebarSectionKind.Home, SidebarSectionKind.Pinned, SidebarSectionKind.Library, SidebarSectionKind.Settings];

    static readonly string[] s_collectionItems = ["liked", "albums", "artists", "podcasts", "audiobooks"];
    static readonly string[] s_libraryKinds = ["albums", "artists", "podcasts", "audiobooks"];

    /// <summary>The layout's sections in catalogue (default) order.</summary>
    public static IReadOnlyList<SidebarSectionKind> KindsOf(SidebarLayoutId layout)
        => layout == SidebarLayoutId.Library ? s_library : s_classic;

    public static bool Has(SidebarLayoutId layout, SidebarSectionKind kind)
    {
        var kinds = KindsOf(layout);
        for (int i = 0; i < kinds.Count; i++) if (kinds[i] == kind) return true;
        return false;
    }

    /// <summary>The stable wire id. PERSISTED (both files) — never rename one.</summary>
    public static string IdOf(SidebarSectionKind kind) => kind switch
    {
        SidebarSectionKind.Home => "home",
        SidebarSectionKind.Pinned => "pinned",
        SidebarSectionKind.Collections => "collections",
        SidebarSectionKind.Playlists => "playlists",
        SidebarSectionKind.Library => "library",
        SidebarSectionKind.Recent => "recent",
        SidebarSectionKind.NewReleases => "newReleases",
        _ => "settings",
    };

    public static bool TryKindOf(string? id, out SidebarSectionKind kind)
    {
        switch (id)
        {
            case "home": kind = SidebarSectionKind.Home; return true;
            case "pinned": kind = SidebarSectionKind.Pinned; return true;
            case "collections": kind = SidebarSectionKind.Collections; return true;
            case "playlists": kind = SidebarSectionKind.Playlists; return true;
            case "library": kind = SidebarSectionKind.Library; return true;
            case "recent": kind = SidebarSectionKind.Recent; return true;
            case "newReleases": kind = SidebarSectionKind.NewReleases; return true;
            case "settings": kind = SidebarSectionKind.Settings; return true;
            default: kind = SidebarSectionKind.Home; return false;
        }
    }

    /// <summary>The header title's loc key; null for Home and Settings (their titles are the route table's).</summary>
    public static string? TitleKeyOf(SidebarSectionKind kind) => kind switch
    {
        SidebarSectionKind.Pinned => "sidebar.pinned",
        SidebarSectionKind.Collections => "sidebar.collections",
        SidebarSectionKind.Playlists => "sidebar.playlists",
        SidebarSectionKind.Library => "sidebar.yourLibrary",
        SidebarSectionKind.Recent => "sidebar.recentlyPlayed",
        SidebarSectionKind.NewReleases => "sidebar.section.title.newReleases",
        _ => null,
    };

    /// <summary>Can the user hide it? Home never (it is first and locked); Playlists and Your Library never (their "+" is
    /// how content starts — hiding them strands content); Settings yes (the profile menu and the palette keep it reachable).
    /// Pinned is hideable but LOCKED while it holds a route/module pin (<see cref="SidebarLayoutRules.Apply"/>).</summary>
    public static bool Hideable(SidebarSectionKind kind)
        => kind is SidebarSectionKind.Pinned or SidebarSectionKind.Collections or SidebarSectionKind.Recent
            or SidebarSectionKind.NewReleases or SidebarSectionKind.Settings;

    /// <summary>Movable sections (design Q2): Classic's middle sections only; Library is a fixed order.</summary>
    public static bool Movable(SidebarLayoutId layout, SidebarSectionKind kind)
        => layout == SidebarLayoutId.Classic
           && kind is SidebarSectionKind.Pinned or SidebarSectionKind.Collections or SidebarSectionKind.Playlists
               or SidebarSectionKind.Recent or SidebarSectionKind.NewReleases;

    /// <summary>Has a collapsible header (Home, Settings and Your Library have none).</summary>
    public static bool Collapsible(SidebarSectionKind kind)
        => kind is SidebarSectionKind.Pinned or SidebarSectionKind.Collections or SidebarSectionKind.Playlists
            or SidebarSectionKind.Recent or SidebarSectionKind.NewReleases;

    /// <summary>The section's hideable ITEMS in default order: Classic Collections = the five library pages (route keys);
    /// Library's <c>library</c> section = the four hideable kinds (its "Filters"). Empty for every other section.</summary>
    public static IReadOnlyList<string> ItemsOf(SidebarLayoutId layout, SidebarSectionKind kind)
        => kind == SidebarSectionKind.Collections && layout == SidebarLayoutId.Classic ? s_collectionItems
         : kind == SidebarSectionKind.Library && layout == SidebarLayoutId.Library ? s_libraryKinds
         : System.Array.Empty<string>();

    /// <summary>Can the items be reordered (Collections only; the library kinds have no order).</summary>
    public static bool ItemsMovable(SidebarSectionKind kind) => kind == SidebarSectionKind.Collections;

    public static int DefaultLimit(SidebarSectionKind kind)
        => kind == SidebarSectionKind.Recent ? 5 : kind == SidebarSectionKind.NewReleases ? 10 : 0;

    /// <summary>A section's default state in a layout. Recent and New releases ship hidden (Classic offers them; Library
    /// has no feeds). Your Library defaults: Recents, natural direction, List, Liked shown.</summary>
    public static SectionState DefaultState(SidebarSectionKind kind) => kind switch
    {
        SidebarSectionKind.Recent or SidebarSectionKind.NewReleases => new SectionState(IdOf(kind), Hidden: true, Limit: DefaultLimit(kind)),
        SidebarSectionKind.Library => new SectionState(IdOf(kind), Sort: SidebarLibrarySort.Recents, Descending: false,
            View: SidebarLibraryView.List, ShowLiked: true),
        _ => new SectionState(IdOf(kind)),
    };

    public static LayoutOverlay DefaultOverlay(SidebarLayoutId layout)
    {
        var kinds = KindsOf(layout);
        var sections = new SectionState[kinds.Count];
        for (int i = 0; i < sections.Length; i++) sections[i] = DefaultState(kinds[i]);
        return new LayoutOverlay(layout, sections);
    }

    /// <summary>A library kind's flag, from its item id.</summary>
    public static SidebarLibraryKinds KindFlagOf(string item) => item switch
    {
        "albums" => SidebarLibraryKinds.Albums,
        "artists" => SidebarLibraryKinds.Artists,
        "podcasts" => SidebarLibraryKinds.Podcasts,
        "audiobooks" => SidebarLibraryKinds.Audiobooks,
        _ => SidebarLibraryKinds.None,
    };
}

/// <summary>One section's user state inside one layout (design A.3). Null members mean "the catalogue default", which
/// keeps <c>sidebar.json</c> small and makes a new default reach every user who never changed it.</summary>
public sealed record SectionState(
    string Id,
    bool Hidden = false,
    bool Collapsed = false,
    int? Limit = null,
    IReadOnlyList<string>? HiddenItems = null,
    IReadOnlyList<string>? ItemOrder = null,
    SidebarLibrarySort? Sort = null,
    bool? Descending = null,
    SidebarLibraryView? View = null,
    bool? ShowLiked = null)
{
    public IReadOnlyList<string> HiddenList => HiddenItems ?? System.Array.Empty<string>();

    // The two lists compare by CONTENT (ordinal, order-sensitive): a record's synthesized Equals compares references, so a
    // state read back from disk would never equal the one that wrote it and every load would look like an edit.
    public bool Equals(SectionState? other)
        => other is not null && string.Equals(Id, other.Id, System.StringComparison.Ordinal) && Hidden == other.Hidden
           && Collapsed == other.Collapsed && Limit == other.Limit && Sort == other.Sort && Descending == other.Descending
           && View == other.View && ShowLiked == other.ShowLiked
           && SidebarLayoutRules.SameList(HiddenItems, other.HiddenItems) && SidebarLayoutRules.SameList(ItemOrder, other.ItemOrder);

    public override int GetHashCode() => System.HashCode.Combine(Id, Hidden, Collapsed, Limit, HiddenItems?.Count ?? 0, ItemOrder?.Count ?? 0);
}

/// <summary>One layout's sections in DISPLAY order — always every catalogue section of the layout exactly once
/// (<see cref="SidebarLayoutRules.MergeWithCatalogue"/> guarantees it).</summary>
public sealed record LayoutOverlay(SidebarLayoutId Layout, IReadOnlyList<SectionState> Sections)
{
    public int IndexOf(string id)
    {
        for (int i = 0; i < Sections.Count; i++)
            if (string.Equals(Sections[i].Id, id, System.StringComparison.Ordinal)) return i;
        return -1;
    }

    public SectionState? Find(string id) => IndexOf(id) is int i and >= 0 ? Sections[i] : null;

    public bool Equals(LayoutOverlay? other)
    {
        if (other is null || other.Layout != Layout || other.Sections.Count != Sections.Count) return false;
        for (int i = 0; i < Sections.Count; i++) if (!Sections[i].Equals(other.Sections[i])) return false;
        return true;
    }

    public override int GetHashCode() => System.HashCode.Combine(Layout, Sections.Count);
}

/// <summary>Both layouts' overlays (design D1: one overlay per layout, both kept across a switch).</summary>
public sealed record SidebarLayoutState(LayoutOverlay Classic, LayoutOverlay Library)
{
    public static SidebarLayoutState Default { get; } =
        new(SidebarCatalogue.DefaultOverlay(SidebarLayoutId.Classic), SidebarCatalogue.DefaultOverlay(SidebarLayoutId.Library));

    public LayoutOverlay Of(SidebarLayoutId layout) => layout == SidebarLayoutId.Library ? Library : Classic;

    public SidebarLayoutState With(LayoutOverlay overlay)
        => overlay.Layout == SidebarLayoutId.Library ? this with { Library = overlay } : this with { Classic = overlay };
}

/// <summary>One edit (design A.3: every mutation is <c>Sidebar.Dispatch(op)</c>). In-memory only, never serialized.</summary>
public abstract record SidebarOp(SidebarLayoutId Layout);
public sealed record SetSectionShown(SidebarLayoutId Layout, string SectionId, bool Shown) : SidebarOp(Layout);
/// <summary><paramref name="ToSlot"/> is the index among the layout's MOVABLE sections (the Outline band's slots).</summary>
public sealed record MoveSection(SidebarLayoutId Layout, string SectionId, int ToSlot) : SidebarOp(Layout);
/// <summary>A live collapse toggle. Persisted, never recorded in the undo ring, never counted by IsModified.</summary>
public sealed record SetSectionCollapsed(SidebarLayoutId Layout, string SectionId, bool Collapsed) : SidebarOp(Layout);
public sealed record SetSectionLimit(SidebarLayoutId Layout, string SectionId, int Limit) : SidebarOp(Layout);
public sealed record SetItemShown(SidebarLayoutId Layout, string SectionId, string ItemId, bool Shown) : SidebarOp(Layout);
public sealed record MoveItem(SidebarLayoutId Layout, string SectionId, string ItemId, int ToIndex) : SidebarOp(Layout);
public sealed record SetLibrarySort(SidebarLibrarySort Sort, bool Descending) : SidebarOp(SidebarLayoutId.Library);
public sealed record SetLibraryView(SidebarLibraryView View) : SidebarOp(SidebarLayoutId.Library);
public sealed record SetShowLiked(bool Shown) : SidebarOp(SidebarLayoutId.Library);
/// <summary>"Reset this layout": every section back to its default, collapse bits kept.</summary>
public sealed record ResetLayout(SidebarLayoutId Layout) : SidebarOp(Layout);
/// <summary>Undo/redo and "Reset everything": replace a whole overlay (merged and capped first).</summary>
public sealed record ReplaceOverlay(LayoutOverlay Overlay) : SidebarOp(Overlay.Layout);

public enum SidebarOpReject : byte
{
    None = 0, NoChange = 1, UnknownSection = 2, Locked = 3, NotMovable = 4, OutOfRange = 5, BadLimit = 6,
    UnknownItem = 7, OverCap = 8,
}

public readonly record struct SidebarOpResult(SidebarLayoutState State, bool Changed, SidebarOpReject Reject)
{
    public static SidebarOpResult Refused(SidebarLayoutState s, SidebarOpReject why) => new(s, false, why);
}

/// <summary>THE LAYOUT RULES (design A.2): every op, the "last item hides the section" coupling (Q5), the merge with the
/// catalogue, the caps, and <see cref="IsModified"/>. A refused op changes nothing (the caller pushes no undo and writes
/// no file).</summary>
public static class SidebarLayoutRules
{
    /// <summary>Caps (design corner cases): an over-cap write is refused, never truncated.</summary>
    public const int MaxSections = 16, MaxHiddenItems = 64, MaxOverlayBytes = 8 * 1024;

    /// <param name="pinnedLocked">Pinned holds a route or module pin (it may not be hidden — "Unpin Search, Radio first").</param>
    public static SidebarOpResult Apply(SidebarLayoutState state, SidebarOp op, bool pinnedLocked)
    {
        var overlay = state.Of(op.Layout);
        LayoutOverlay? next = op switch
        {
            SetSectionShown o => Shown(overlay, o, pinnedLocked, out var r1) ?? Fail(r1),
            MoveSection o => Move(overlay, o, out var r2) ?? Fail(r2),
            SetSectionCollapsed o => Collapse(overlay, o, out var r3) ?? Fail(r3),
            SetSectionLimit o => Limit(overlay, o, out var r4) ?? Fail(r4),
            SetItemShown o => ItemShown(overlay, o, out var r5) ?? Fail(r5),
            MoveItem o => ItemMove(overlay, o, out var r6) ?? Fail(r6),
            SetLibrarySort o => Update(overlay, "library", s => s with { Sort = o.Sort, Descending = o.Descending }),
            SetLibraryView o => Update(overlay, "library", s => s with { View = o.View }),
            SetShowLiked o => Update(overlay, "library", s => s with { ShowLiked = o.Shown }),
            ResetLayout => Reset(overlay),
            ReplaceOverlay o => MergeWithCatalogue(o.Overlay, null),
            _ => null,
        };
        if (s_reject != SidebarOpReject.None)
        {
            var why = s_reject;
            s_reject = SidebarOpReject.None;
            return SidebarOpResult.Refused(state, why);
        }
        if (next is null || next.Equals(overlay)) return SidebarOpResult.Refused(state, SidebarOpReject.NoChange);
        return new SidebarOpResult(state.With(next), true, SidebarOpReject.None);
    }

    // Apply's arms report a refusal through this one slot (UI thread only; the class is otherwise stateless).
    [System.ThreadStatic] static SidebarOpReject s_reject;
    static LayoutOverlay? Fail(SidebarOpReject why) { s_reject = why; return null; }

    static LayoutOverlay? Shown(LayoutOverlay o, SetSectionShown op, bool pinnedLocked, out SidebarOpReject why)
    {
        why = SidebarOpReject.None;
        int i = o.IndexOf(op.SectionId);
        if (i < 0 || !SidebarCatalogue.TryKindOf(op.SectionId, out var kind)) { why = SidebarOpReject.UnknownSection; return null; }
        if (!SidebarCatalogue.Hideable(kind)) { why = SidebarOpReject.Locked; return null; }
        if (kind == SidebarSectionKind.Pinned && !op.Shown && pinnedLocked) { why = SidebarOpReject.Locked; return null; }
        // Q5: showing a section only drops its Hidden bit; its hidden items stay as they were.
        return Replace(o, i, o.Sections[i] with { Hidden = !op.Shown });
    }

    static LayoutOverlay? Move(LayoutOverlay o, MoveSection op, out SidebarOpReject why)
    {
        why = SidebarOpReject.None;
        int from = o.IndexOf(op.SectionId);
        if (from < 0 || !SidebarCatalogue.TryKindOf(op.SectionId, out var kind)) { why = SidebarOpReject.UnknownSection; return null; }
        if (!SidebarCatalogue.Movable(o.Layout, kind)) { why = SidebarOpReject.NotMovable; return null; }
        // The band's slots are the movable sections, in display order; the locked ones keep their absolute positions.
        var slots = new List<int>(o.Sections.Count);
        for (int k = 0; k < o.Sections.Count; k++)
            if (SidebarCatalogue.TryKindOf(o.Sections[k].Id, out var kk) && SidebarCatalogue.Movable(o.Layout, kk)) slots.Add(k);
        int fromSlot = slots.IndexOf(from);
        if ((uint)op.ToSlot >= (uint)slots.Count) { why = SidebarOpReject.OutOfRange; return null; }
        if (op.ToSlot == fromSlot) return o;
        var movable = new List<SectionState>(slots.Count);
        for (int k = 0; k < slots.Count; k++) movable.Add(o.Sections[slots[k]]);
        var moved = movable[fromSlot];
        movable.RemoveAt(fromSlot);
        movable.Insert(op.ToSlot, moved);
        var sections = new SectionState[o.Sections.Count];
        for (int k = 0; k < sections.Length; k++) sections[k] = o.Sections[k];
        for (int k = 0; k < slots.Count; k++) sections[slots[k]] = movable[k];
        return o with { Sections = sections };
    }

    static LayoutOverlay? Collapse(LayoutOverlay o, SetSectionCollapsed op, out SidebarOpReject why)
    {
        why = SidebarOpReject.None;
        int i = o.IndexOf(op.SectionId);
        if (i < 0 || !SidebarCatalogue.TryKindOf(op.SectionId, out var kind)) { why = SidebarOpReject.UnknownSection; return null; }
        if (!SidebarCatalogue.Collapsible(kind)) { why = SidebarOpReject.Locked; return null; }
        return Replace(o, i, o.Sections[i] with { Collapsed = op.Collapsed });
    }

    static LayoutOverlay? Limit(LayoutOverlay o, SetSectionLimit op, out SidebarOpReject why)
    {
        why = SidebarOpReject.None;
        int i = o.IndexOf(op.SectionId);
        if (i < 0 || !SidebarCatalogue.TryKindOf(op.SectionId, out var kind)) { why = SidebarOpReject.UnknownSection; return null; }
        if (SidebarCatalogue.DefaultLimit(kind) == 0 || System.Array.IndexOf(SidebarCatalogue.LimitChoices, op.Limit) < 0)
        { why = SidebarOpReject.BadLimit; return null; }
        return Replace(o, i, o.Sections[i] with { Limit = op.Limit });
    }

    static LayoutOverlay? ItemShown(LayoutOverlay o, SetItemShown op, out SidebarOpReject why)
    {
        why = SidebarOpReject.None;
        int i = o.IndexOf(op.SectionId);
        if (i < 0 || !SidebarCatalogue.TryKindOf(op.SectionId, out var kind)) { why = SidebarOpReject.UnknownSection; return null; }
        var items = SidebarCatalogue.ItemsOf(o.Layout, kind);
        if (!Contains(items, op.ItemId)) { why = SidebarOpReject.UnknownItem; return null; }
        var s = o.Sections[i];
        var hidden = new List<string>(s.HiddenList);
        if (op.Shown) hidden.Remove(op.ItemId);
        else
        {
            if (Contains(hidden, op.ItemId)) return o;
            // Q5: hiding the LAST visible item of Collections hides the section and keeps the previous item set, so
            // "Show section" restores what it showed. Library's kinds never hide their section (it is locked).
            if (kind == SidebarSectionKind.Collections && hidden.Count + 1 >= items.Count)
                return Replace(o, i, s with { Hidden = true });
            if (hidden.Count >= MaxHiddenItems) { why = SidebarOpReject.OverCap; return null; }
            hidden.Add(op.ItemId);
        }
        return Replace(o, i, s with { HiddenItems = hidden.Count == 0 ? null : hidden.ToArray() });
    }

    static LayoutOverlay? ItemMove(LayoutOverlay o, MoveItem op, out SidebarOpReject why)
    {
        why = SidebarOpReject.None;
        int i = o.IndexOf(op.SectionId);
        if (i < 0 || !SidebarCatalogue.TryKindOf(op.SectionId, out var kind)) { why = SidebarOpReject.UnknownSection; return null; }
        if (!SidebarCatalogue.ItemsMovable(kind)) { why = SidebarOpReject.NotMovable; return null; }
        var order = new List<string>(EffectiveItemOrder(o.Layout, kind, o.Sections[i]));
        int from = order.IndexOf(op.ItemId);
        if (from < 0) { why = SidebarOpReject.UnknownItem; return null; }
        if ((uint)op.ToIndex >= (uint)order.Count) { why = SidebarOpReject.OutOfRange; return null; }
        order.RemoveAt(from);
        order.Insert(op.ToIndex, op.ItemId);
        bool isDefault = SameList(order, SidebarCatalogue.ItemsOf(o.Layout, kind));
        return Replace(o, i, o.Sections[i] with { ItemOrder = isDefault ? null : order.ToArray() });
    }

    static LayoutOverlay? Update(LayoutOverlay o, string id, System.Func<SectionState, SectionState> change)
    {
        int i = o.IndexOf(id);
        if (i < 0) return Fail(SidebarOpReject.UnknownSection);
        return Replace(o, i, change(o.Sections[i]));
    }

    static LayoutOverlay Reset(LayoutOverlay o)
    {
        var fresh = SidebarCatalogue.DefaultOverlay(o.Layout);
        var sections = new SectionState[fresh.Sections.Count];
        for (int k = 0; k < sections.Length; k++)
        {
            var old = o.Find(fresh.Sections[k].Id);
            sections[k] = old is null ? fresh.Sections[k] : fresh.Sections[k] with { Collapsed = old.Collapsed };
        }
        return fresh with { Sections = sections };
    }

    static LayoutOverlay Replace(LayoutOverlay o, int index, SectionState s)
    {
        var sections = new SectionState[o.Sections.Count];
        for (int k = 0; k < sections.Length; k++) sections[k] = k == index ? s : o.Sections[k];
        return o with { Sections = sections };
    }

    /// <summary>THE MERGE (design corner case "section added by an app update"): unknown ids are dropped (and reported
    /// into <paramref name="dropped"/> for the caller's log), duplicates keep the first, every missing catalogue section is
    /// inserted after the previous catalogue section that is present (default state), Home is forced first and — in
    /// Library — Your Library last before Settings; limits outside the choices fall back to the default; hidden items not
    /// in the catalogue are dropped; over-cap lists are cut at the cap (a merge reads a file, it never refuses a load).</summary>
    public static LayoutOverlay MergeWithCatalogue(LayoutOverlay overlay, List<string>? dropped)
    {
        var kinds = SidebarCatalogue.KindsOf(overlay.Layout);
        var seen = new HashSet<string>(System.StringComparer.Ordinal);
        var list = new List<SectionState>(kinds.Count);
        for (int i = 0; i < overlay.Sections.Count && list.Count < MaxSections; i++)
        {
            var s = overlay.Sections[i];
            if (!SidebarCatalogue.TryKindOf(s.Id, out var kind) || !SidebarCatalogue.Has(overlay.Layout, kind))
            { dropped?.Add(s.Id ?? ""); continue; }
            if (!seen.Add(s.Id)) continue;
            list.Add(Sanitize(overlay.Layout, kind, s));
        }
        for (int k = 0; k < kinds.Count; k++)
        {
            string id = SidebarCatalogue.IdOf(kinds[k]);
            if (seen.Contains(id)) continue;
            int at = 0;
            for (int p = k - 1; p >= 0; p--)
            {
                int prev = IndexIn(list, SidebarCatalogue.IdOf(kinds[p]));
                if (prev >= 0) { at = prev + 1; break; }
            }
            list.Insert(at, SidebarCatalogue.DefaultState(kinds[k]));
            seen.Add(id);
        }
        Pin(list, "home", first: true);
        if (overlay.Layout == SidebarLayoutId.Library) { Pin(list, "library", first: false); }
        Pin(list, "settings", first: false);
        return new LayoutOverlay(overlay.Layout, list.ToArray());
    }

    static SectionState Sanitize(SidebarLayoutId layout, SidebarSectionKind kind, SectionState s)
    {
        int? limit = s.Limit is int l && System.Array.IndexOf(SidebarCatalogue.LimitChoices, l) >= 0 ? l
            : SidebarCatalogue.DefaultLimit(kind) is int d and > 0 ? d : null;
        var items = SidebarCatalogue.ItemsOf(layout, kind);
        string[]? hidden = Filter(s.HiddenItems, items, MaxHiddenItems);
        string[]? order = SidebarCatalogue.ItemsMovable(kind) ? Filter(s.ItemOrder, items, items.Count) : null;
        bool locked = !SidebarCatalogue.Hideable(kind);
        return s with
        {
            Hidden = !locked && s.Hidden,
            Collapsed = SidebarCatalogue.Collapsible(kind) && s.Collapsed,
            Limit = limit,
            HiddenItems = hidden,
            ItemOrder = order,
        };
    }

    static string[]? Filter(IReadOnlyList<string>? source, IReadOnlyList<string> allowed, int cap)
    {
        if (source is null || source.Count == 0) return null;
        var kept = new List<string>(source.Count);
        for (int i = 0; i < source.Count && kept.Count < cap; i++)
            if (Contains(allowed, source[i]) && !kept.Contains(source[i])) kept.Add(source[i]);
        return kept.Count == 0 ? null : kept.ToArray();
    }

    static void Pin(List<SectionState> list, string id, bool first)
    {
        int i = IndexIn(list, id);
        if (i < 0) return;
        var s = list[i];
        list.RemoveAt(i);
        if (first) list.Insert(0, s); else list.Add(s);
    }

    static int IndexIn(List<SectionState> list, string id)
    {
        for (int i = 0; i < list.Count; i++) if (string.Equals(list[i].Id, id, System.StringComparison.Ordinal)) return i;
        return -1;
    }

    /// <summary>A section's items in its EFFECTIVE order (the stored order, completed by any catalogue item it lacks).</summary>
    public static IReadOnlyList<string> EffectiveItemOrder(SidebarLayoutId layout, SidebarSectionKind kind, SectionState s)
    {
        var items = SidebarCatalogue.ItemsOf(layout, kind);
        if (s.ItemOrder is not { Count: > 0 } order) return items;
        var list = new List<string>(items.Count);
        for (int i = 0; i < order.Count; i++) if (Contains(items, order[i]) && !list.Contains(order[i])) list.Add(order[i]);
        for (int i = 0; i < items.Count; i++) if (!list.Contains(items[i])) list.Add(items[i]);
        return list;
    }

    /// <summary>"Classic · modified" (design C.1): any section's shown / order / limit / sort / view / showLiked /
    /// hidden items / item order differs from the catalogue default. Collapse, density, width and the filter never count.</summary>
    public static bool IsModified(LayoutOverlay overlay)
    {
        var fresh = SidebarCatalogue.DefaultOverlay(overlay.Layout);
        if (overlay.Sections.Count != fresh.Sections.Count) return true;
        for (int i = 0; i < fresh.Sections.Count; i++)
        {
            var a = overlay.Sections[i];
            var b = fresh.Sections[i];
            if (!string.Equals(a.Id, b.Id, System.StringComparison.Ordinal)) return true;   // order
            if (!SidebarCatalogue.TryKindOf(a.Id, out var kind)) return true;
            if (a.Hidden != b.Hidden || (a.Limit ?? 0) != (b.Limit ?? 0)) return true;
            if ((a.Sort ?? b.Sort) != b.Sort || (a.Descending ?? b.Descending) != b.Descending
                || (a.View ?? b.View) != b.View || (a.ShowLiked ?? b.ShowLiked) != b.ShowLiked) return true;
            if (a.HiddenList.Count != 0) return true;
            if (!SameList(EffectiveItemOrder(overlay.Layout, kind, a), SidebarCatalogue.ItemsOf(overlay.Layout, kind))) return true;
        }
        return false;
    }

    public static bool SameList(IReadOnlyList<string>? a, IReadOnlyList<string>? b)
    {
        int ac = a?.Count ?? 0, bc = b?.Count ?? 0;
        if (ac != bc) return false;
        for (int i = 0; i < ac; i++) if (!string.Equals(a![i], b![i], System.StringComparison.Ordinal)) return false;
        return true;
    }

    static bool Contains(IReadOnlyList<string> list, string id)
    {
        for (int i = 0; i < list.Count; i++) if (string.Equals(list[i], id, System.StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>The resolved document for a layout (§P3.3).</summary>
    public static SidebarLayoutDoc Resolve(SidebarLayoutState state, SidebarLayoutId layout, SidebarDensity density)
    {
        var overlay = state.Of(layout);
        var sections = new SidebarSection[overlay.Sections.Count];
        var lib = SidebarLibraryOptions.Default;
        for (int i = 0; i < sections.Length; i++)
        {
            var s = overlay.Sections[i];
            SidebarCatalogue.TryKindOf(s.Id, out var kind);
            var order = EffectiveItemOrder(layout, kind, s);
            var visible = new List<string>(order.Count);
            for (int k = 0; k < order.Count; k++) if (!Contains(s.HiddenList, order[k])) visible.Add(order[k]);
            sections[i] = new SidebarSection(kind, s.Hidden, s.Collapsed, s.Limit ?? SidebarCatalogue.DefaultLimit(kind),
                SidebarSection.ShapeFor(kind, density), visible.ToArray());
            if (kind == SidebarSectionKind.Library)
            {
                var hiddenKinds = SidebarLibraryKinds.None;
                for (int k = 0; k < s.HiddenList.Count; k++) hiddenKinds |= SidebarCatalogue.KindFlagOf(s.HiddenList[k]);
                lib = new SidebarLibraryOptions(s.Sort ?? SidebarLibrarySort.Recents, s.Descending ?? false,
                    s.View ?? SidebarLibraryView.List, s.ShowLiked ?? true, hiddenKinds);
            }
        }
        return new SidebarLayoutDoc(layout, sections, density, lib);
    }
}

/// <summary>A resolved section — what the planner and the slot read. <see cref="Items"/> are the VISIBLE items in order
/// (Collections: route keys; Your Library: the shown kinds). Immutable; rebuilt only when the overlay or the density
/// changes, so the pane's publish reference test holds.</summary>
public sealed record SidebarSection(
    SidebarSectionKind Kind,
    bool Hidden,
    bool Collapsed,
    int Limit,
    SidebarRowShape Shape,
    IReadOnlyList<string> Items)
{
    public string Id => SidebarCatalogue.IdOf(Kind);

    /// <summary>The one row shape per section (design V.3): glyph sections are row A; entity sections follow the density.</summary>
    public static SidebarRowShape ShapeFor(SidebarSectionKind kind, SidebarDensity density)
        => kind is SidebarSectionKind.Home or SidebarSectionKind.Collections or SidebarSectionKind.Settings
            ? SidebarRowShape.Glyph
            : density == SidebarDensity.Compact ? SidebarRowShape.EntityOneLine : SidebarRowShape.EntityTwoLine;
}

/// <summary>Your Library's resolved options (the <c>library</c> section's state).</summary>
public sealed record SidebarLibraryOptions(
    SidebarLibrarySort Sort, bool Descending, SidebarLibraryView View, bool ShowLiked, SidebarLibraryKinds HiddenKinds)
{
    public static readonly SidebarLibraryOptions Default =
        new(SidebarLibrarySort.Recents, false, SidebarLibraryView.List, true, SidebarLibraryKinds.None);
}

/// <summary>THE DOCUMENT the planner reads (replaces <c>SidebarCustomLayout</c>): the layout, its sections in display
/// order (hidden ones included — the Outline needs them), the density and Your Library's options.</summary>
public sealed record SidebarLayoutDoc(
    SidebarLayoutId Layout,
    IReadOnlyList<SidebarSection> Sections,
    SidebarDensity Density,
    SidebarLibraryOptions Library)
{
    public static readonly SidebarLayoutDoc Empty = new(SidebarLayoutId.Classic, System.Array.Empty<SidebarSection>(),
        SidebarDensity.Default, SidebarLibraryOptions.Default);

    public SidebarSection? Find(SidebarSectionKind kind)
    {
        for (int i = 0; i < Sections.Count; i++) if (Sections[i].Kind == kind) return Sections[i];
        return null;
    }

    public SidebarSection? Find(string? id)
        => SidebarCatalogue.TryKindOf(id, out var kind) ? Find(kind) : null;

    /// <summary>True when <paramref name="other"/> is this document with only sections' <see cref="SidebarSection.Collapsed"/>
    /// changed: a collapse adds or removes a section's rows, which the plan's row diff sees, but changes nothing a row that
    /// is still there draws (its header's chevron aside, which the caller re-skins).</summary>
    public bool SameExceptCollapsed(SidebarLayoutDoc other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (Layout != other.Layout || Density != other.Density || !Library.Equals(other.Library)
            || Sections.Count != other.Sections.Count) return false;
        for (int i = 0; i < Sections.Count; i++)
        {
            SidebarSection a = Sections[i], b = other.Sections[i];
            if (a.Kind != b.Kind || a.Hidden != b.Hidden || a.Limit != b.Limit || a.Shape != b.Shape) return false;
            if (!ReferenceEquals(a.Items, b.Items))
            {
                if (a.Items.Count != b.Items.Count) return false;
                for (int k = 0; k < a.Items.Count; k++)
                    if (!string.Equals(a.Items[k], b.Items[k], System.StringComparison.Ordinal)) return false;
            }
        }
        return true;
    }
}

/// <summary>What is drawn where (design A.2 SidebarVisibilityRules): the pin dedupe, Library's pin and Liked rules, the
/// Pinned lock and its reason.</summary>
public static class SidebarVisibilityRules
{
    /// <summary>A pinned entity/route is drawn ONLY in Pinned while Pinned is visible and either expanded or presented as a
    /// rail tile; collapsing Pinned in the expanded pane returns pinned items to their home sections. Library always
    /// dedupes while Pinned is shown (pins are the list's first rows).</summary>
    public static bool DedupesPins(SidebarLayoutDoc doc, bool compact)
    {
        var pinned = doc.Find(SidebarSectionKind.Pinned);
        if (pinned is null || pinned.Hidden) return false;
        return doc.Layout == SidebarLayoutId.Library || compact || !pinned.Collapsed;
    }

    /// <summary>Library: does the pin band show <paramref name="pin"/> under the current filter/search? An ENTITY pin
    /// follows the chip, the hidden kinds and the search; a non-entity pin (a route, a module) has no kind, so ANY chip hides
    /// it, and with no chip it follows the search like every other row (design P.2a "the text filters pins and the list",
    /// Q2 "search filters pins too"): a "Search" pin stays while the user types "sea" and goes for "xyz".</summary>
    public static bool ShowsPinInLibrary(in SidebarLibraryEntry pin, SidebarLibraryFilter filter, string? search,
                                         SidebarLibraryKinds hiddenKinds)
    {
        bool entity = pin.Kind is SidebarEntryKind.Playlist or SidebarEntryKind.Folder or SidebarEntryKind.Album
            or SidebarEntryKind.Artist or SidebarEntryKind.Show;
        bool searching = !string.IsNullOrEmpty(search);
        if (!entity)
            return filter == SidebarLibraryFilter.None
                   && (!searching || SidebarSearch.Matches(pin.Name.Length > 0 ? pin.Name : pin.Id, pin.Creator, search!));
        if (SidebarLibraryFilters.IsHidden(hiddenKinds, in pin)) return false;
        if (filter != SidebarLibraryFilter.None && !SidebarLibraryFilters.Matches(filter, in pin)) return false;
        return !searching || SidebarSearch.Matches(in pin, search!);
    }

    /// <summary>Library: Liked Songs is the list's fixed first row under no chip or the Playlists chip, unless hidden
    /// ("Show Liked Songs"), filtered out by the search, or the list is DRILLED into a folder (a folder level holds that
    /// folder's children only — today's V3 <c>LikedVisible</c> is <c>!Drilled</c> too).</summary>
    public static bool ShowsLiked(SidebarLibraryOptions options, SidebarLibraryFilter filter, string? search, string? likedTitle,
                                  bool drilled = false)
    {
        if (drilled || !options.ShowLiked) return false;
        if (filter is not (SidebarLibraryFilter.None or SidebarLibraryFilter.Playlists)) return false;
        if (string.IsNullOrEmpty(search)) return true;
        return likedTitle is { Length: > 0 } t && t.Contains(search!, System.StringComparison.CurrentCultureIgnoreCase);
    }

    /// <summary>Pinned is LOCKED (cannot be hidden) while it holds a pin that would have nowhere else to show: a route or
    /// a module pin. Returns those pins' names for the reason line ("Unpin Search, Radio first"), or an empty list.</summary>
    public static void LockingPins(IReadOnlyList<SidebarPin> pins, List<string> names)
    {
        names.Clear();
        for (int i = 0; i < pins.Count; i++)
            if (pins[i].Kind == SidebarEntryKind.AppRoute) names.Add(pins[i].Name);
    }
}

/// <summary>The library filter vocabulary over projected entries (one place, so the chips, the pin band and the binder's
/// shaping agree). Podcasts and Audiobooks split the Show rows by <see cref="SidebarLibraryEntry.IsAudiobook"/>.</summary>
public static class SidebarLibraryFilters
{
    public static bool Matches(SidebarLibraryFilter filter, in SidebarLibraryEntry e) => filter switch
    {
        SidebarLibraryFilter.Playlists => e.Kind is SidebarEntryKind.Playlist or SidebarEntryKind.Folder,
        SidebarLibraryFilter.Albums => e.Kind == SidebarEntryKind.Album,
        SidebarLibraryFilter.Artists => e.Kind == SidebarEntryKind.Artist,
        SidebarLibraryFilter.Podcasts => e.Kind == SidebarEntryKind.Show && !e.IsAudiobook,
        SidebarLibraryFilter.Audiobooks => e.Kind == SidebarEntryKind.Show && e.IsAudiobook,
        _ => true,
    };

    public static bool IsHidden(SidebarLibraryKinds hidden, in SidebarLibraryEntry e) => e.Kind switch
    {
        SidebarEntryKind.Album => (hidden & SidebarLibraryKinds.Albums) != 0,
        SidebarEntryKind.Artist => (hidden & SidebarLibraryKinds.Artists) != 0,
        SidebarEntryKind.Show => (hidden & (e.IsAudiobook ? SidebarLibraryKinds.Audiobooks : SidebarLibraryKinds.Podcasts)) != 0,
        _ => false,
    };

    /// <summary>A hidden kind has no chip (Q12).</summary>
    public static bool HasChip(SidebarLibraryFilter filter, SidebarLibraryKinds hidden) => filter switch
    {
        SidebarLibraryFilter.Albums => (hidden & SidebarLibraryKinds.Albums) == 0,
        SidebarLibraryFilter.Artists => (hidden & SidebarLibraryKinds.Artists) == 0,
        SidebarLibraryFilter.Podcasts => (hidden & SidebarLibraryKinds.Podcasts) == 0,
        SidebarLibraryFilter.Audiobooks => (hidden & SidebarLibraryKinds.Audiobooks) == 0,
        _ => true,
    };

    /// <summary>The filter a stored int means, falling back to None when its kind was hidden meanwhile (the active chip
    /// of a hidden kind falls back to everything).</summary>
    public static SidebarLibraryFilter Effective(int stored, SidebarLibraryKinds hidden)
    {
        var f = (uint)stored <= 5 ? (SidebarLibraryFilter)stored : SidebarLibraryFilter.None;
        return HasChip(f, hidden) ? f : SidebarLibraryFilter.None;
    }
}

/// <summary>A pin row's state (design Q9): pending (last-known name + kind glyph, no warning) until the projection
/// hydrates; unavailable only after an AUTHORITATIVE miss (a converged rootlist that no longer carries a folder); offline
/// neutral. Never auto-dropped, never a toast.</summary>
public enum SidebarPinState : byte { Resolved = 0, Pending = 1, Unavailable = 2, Offline = 3 }

public static class SidebarPinStateRules
{
    public static SidebarPinState Of(bool identityKnown, bool authoritativeMiss, bool online)
        => authoritativeMiss ? SidebarPinState.Unavailable
         : identityKnown ? SidebarPinState.Resolved
         : !online ? SidebarPinState.Offline
         : SidebarPinState.Pending;

    /// <summary>The localized noun a pin row shows while nothing has named it yet (design D9: nothing blank is ever
    /// rendered). Null for a kind that always carries its own title (a route pin's destination title).</summary>
    public static string? FallbackTitleKey(SidebarEntryKind kind) => kind switch
    {
        SidebarEntryKind.Playlist => "nav.playlist",
        SidebarEntryKind.Folder => "sidebar.pin.folder",
        SidebarEntryKind.Album => "nav.album",
        SidebarEntryKind.Artist => "nav.artist",
        SidebarEntryKind.Show => "nav.show",          // "Podcast": IsAudiobook is not known before hydration either
        _ => null,
    };
}

/// <summary>The key-matched MOTION a section toggle or a pin move owes the rows it displaces (S2d, S3, J2). A collapse
/// commits its rows' removal and the planner's dedupe (a pin returning to Playlists, or leaving it) edits rows OUTSIDE the
/// band in the same publish; the reveal band animates only its own rows, so every other edit would snap. This turns the
/// publish into the ordered edit script (<see cref="Sidebar.PlanDiff.Splices"/>) plus one seed per row: an inserted row
/// outside the bands fades in, and a surviving row glides from where the user saw it (a FLIP start of
/// <c>removed extents above it - inserted extents above it</c>). Rows INSIDE a band are never seeded - the band presents
/// them - and band extents are never part of a glide, for the same reason.
/// <para>Scroll: a header-driven toggle or a row-driven pin has its trigger on screen (section headers are not sticky), so
/// every edit it makes lies at or below the trigger - below the scroll anchor - and needs no offset correction. A pin from
/// a scrolled Playlists row inserts above the viewport; the seeds then move only the rows the user saw move.</para>
/// Pure: Wavee.Tests pins it over planner-built rows (<c>SidebarDedupeMotionTests</c>).</summary>
public static class SidebarDedupeMotion
{
    /// <summary>One row's start: the FLIP translate (DIP, y) it begins at, and whether it fades in.</summary>
    public readonly record struct Seed(float Dy, bool Fade);

    /// <summary>The ids of the sections whose Collapsed flag differs between the documents, by index (the same rule the
    /// pane's header re-skin uses). Empty when none flipped.</summary>
    public static List<string> FlippedSections(SidebarLayoutDoc oldDoc, SidebarLayoutDoc newDoc)
    {
        var flipped = new List<string>(2);
        var a = oldDoc.Sections;
        var b = newDoc.Sections;
        for (int i = 0; i < b.Count && i < a.Count; i++)
            if (a[i].Collapsed != b[i].Collapsed) flipped.Add(b[i].Id);
        return flipped;
    }

    /// <summary>Did the PINNED section's rows gain or lose a key - a pin or an unpin? A pure reorder (the same keys in a
    /// new order) is not one: the drag's own displacement choreography owns it.</summary>
    public static bool PinnedKeysChanged(IReadOnlyList<SidebarRow> oldRows, IReadOnlyList<SidebarRow> newRows)
    {
        string pinned = SidebarCatalogue.IdOf(SidebarSectionKind.Pinned);
        var oldKeys = new List<string>(8);
        var newKeys = new List<string>(8);
        for (int i = 0; i < oldRows.Count; i++)
            if (string.Equals(oldRows[i].SectionId, pinned, StringComparison.Ordinal)) oldKeys.Add(oldRows[i].Key);
        for (int i = 0; i < newRows.Count; i++)
            if (string.Equals(newRows[i].SectionId, pinned, StringComparison.Ordinal)) newKeys.Add(newRows[i].Key);
        if (oldKeys.Count != newKeys.Count) return true;
        var set = new HashSet<string>(oldKeys, StringComparer.Ordinal);
        for (int i = 0; i < newKeys.Count; i++)
            if (!set.Contains(newKeys[i])) return true;
        return false;   // the same keys (in whatever order): nothing was pinned or unpinned
    }

    /// <summary>Every section the publish flipped was toggled through the pane's disclosure channel (and at least one
    /// flipped): only then does a collapse-only publish take the key-matched path.</summary>
    public static bool FlippedSectionsAll(SidebarLayoutDoc oldDoc, SidebarLayoutDoc newDoc, HashSet<string> choreographed)
    {
        var flipped = FlippedSections(oldDoc, newDoc);
        if (flipped.Count == 0) return false;
        for (int i = 0; i < flipped.Count; i++)
            if (!choreographed.Contains(flipped[i])) return false;
        return true;
    }

    /// <summary>The whole job of a keyed publish: the edit script from <paramref name="oldRows"/> to
    /// <paramref name="newRows"/> and the seeds. The bands are the flipped sections' body ranges (the old range read from
    /// the OLD rows, the new from the new ones) plus every section disclosure still in flight (<paramref name="inFlightSections"/>).
    /// <paramref name="oldExtentOf"/> reads the pre-swap layout (index in the OLD rows), <paramref name="newExtentOf"/> the
    /// analytic extent of a new row.</summary>
    public static Dictionary<int, Seed> ForPublish(
        SidebarLayoutDoc oldDoc, SidebarLayoutDoc newDoc,
        IReadOnlyList<SidebarRow> oldRows, IReadOnlyList<SidebarRow> newRows,
        IReadOnlyList<string> inFlightSections,
        Func<int, float> oldExtentOf, Func<int, float> newExtentOf,
        out List<(int At, int Removed, int Inserted)> splices)
    {
        splices = Sidebar.PlanDiff.Splices(oldRows, newRows);
        var ids = FlippedSections(oldDoc, newDoc);
        for (int i = 0; i < inFlightSections.Count; i++)
            if (!ids.Contains(inFlightSections[i])) ids.Add(inFlightSections[i]);
        var oldBands = new List<(int First, int Count)>(ids.Count);
        var newBands = new List<(int First, int Count)>(ids.Count);
        for (int i = 0; i < ids.Count; i++)
        {
            if (SidebarRowGeometry.TrySectionBodyRange(oldRows, ids[i], out int of, out int oc)) oldBands.Add((of, oc));
            if (SidebarRowGeometry.TrySectionBodyRange(newRows, ids[i], out int nf, out int nc)) newBands.Add((nf, nc));
        }
        return Seeds(splices, oldBands, newBands, oldExtentOf, newExtentOf, newRows.Count);
    }

    /// <summary>One walk over the splices with two running sums: the old extents of the rows REMOVED above, and the new
    /// extents of the rows INSERTED above, both outside the bands. A row inserted outside the bands fades in; a survivor
    /// outside them starts <c>removedAbove - insertedAbove</c> from where it now lies (when at least half a DIP). A
    /// non-finite extent for an inserted row answers an empty map: no motion, never a wrong one.</summary>
    public static Dictionary<int, Seed> Seeds(
        IReadOnlyList<(int At, int Removed, int Inserted)> splices,
        IReadOnlyList<(int First, int Count)> oldBands, IReadOnlyList<(int First, int Count)> newBands,
        Func<int, float> oldExtentOf, Func<int, float> newExtentOf, int newCount)
    {
        var seeds = new Dictionary<int, Seed>();
        double removedAbove = 0.0, insertedAbove = 0.0;
        int next = 0, sumRemoved = 0, sumInserted = 0;
        void Survivors(int end)
        {
            end = Math.Min(end, newCount);
            float dy = (float)(removedAbove - insertedAbove);
            if (MathF.Abs(dy) < 0.5f) return;
            for (int i = next; i < end; i++)
                if (!InBands(newBands, i)) seeds[i] = new Seed(dy, false);
        }
        for (int s = 0; s < splices.Count; s++)
        {
            var (at, removed, inserted) = splices[s];
            Survivors(at);
            int oldStart = at - sumInserted + sumRemoved;
            for (int r = 0; r < removed; r++)
            {
                int oldIndex = oldStart + r;
                if (InBands(oldBands, oldIndex)) continue;
                float e = oldExtentOf(oldIndex);
                if (float.IsFinite(e)) removedAbove += e;
            }
            for (int k = 0; k < inserted; k++)
            {
                int newIndex = at + k;
                if (InBands(newBands, newIndex)) continue;
                float e = newExtentOf(newIndex);
                if (!float.IsFinite(e)) return new Dictionary<int, Seed>();
                insertedAbove += e;
                seeds[newIndex] = new Seed(0f, true);
            }
            next = at + inserted;
            sumRemoved += removed;
            sumInserted += inserted;
        }
        Survivors(newCount);
        return seeds;
    }

    static bool InBands(IReadOnlyList<(int First, int Count)> bands, int index)
    {
        for (int i = 0; i < bands.Count; i++)
            if (index >= bands[i].First && index < bands[i].First + bands[i].Count) return true;
        return false;
    }
}
