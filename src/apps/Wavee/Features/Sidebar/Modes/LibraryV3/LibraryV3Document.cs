using System;
using System.Collections.Generic;
using Wavee.Core.Sidebar;

namespace Wavee;

// R3.0.3 — LIBRARY V3 AS A SYNTHESIZED DOCUMENT.
//
// V3 used to ship its own pane container (LibraryV3Index/List/Row/Rail): a second planner, a second row vocabulary, a
// second height ladder and a second rail. Full unification makes every mode a DOCUMENT plus a `SidebarPaneConfig` over the
// ONE `SidebarPane`, so V3's chrome (header · toolbar · chips · breadcrumb) stays and its CONTENT becomes the ephemeral
// document this file builds.
//
// EPHEMERAL, NEVER PERSISTED. Unlike Curated's document (edited by the customizer, autosaved) and unlike Classic's locked
// built-in, this one is rebuilt from the V3 view state on every state change and thrown away. Nothing dispatches a command
// against it; `SidebarPaneConfig.ReadOnly` is true for V3 precisely because the chrome — not the document — owns the state.
//
// PURE BY CONSTRUCTION (System + Wavee.Core.Sidebar + the engine-free V3 enums). It is source-included by
// src/apps/Wavee.Tests, so LibraryV3DocumentTests drives the REAL mapping rules rather than a copy of them: no signal, no
// preference service, no Element, no Loc, no Icons, no width.
//
// WHERE THE DATA COMES FROM. The document only says WHAT to render; the rows come from the planner input the mode
// component shapes (`LibraryV3Sidebar.ShapeInput`), which hands the pane the ALREADY-SHAPED V3 projection
// (`SidebarPreferences.Entries` — filtered → sorted → pins-first by `SidebarBinderPipeline`, re-grouped into tree order by
// `LibraryV3View`). That is what keeps ONE filter/sort/search implementation in the app: the section's `Query` below
// MIRRORS that shaping (so the rail's own planner pass and any re-sort reproduce it) instead of re-deciding it.
static class LibraryV3Document
{
    /// <summary>The document's template id. Deliberately NOT one of <see cref="SidebarTemplates"/>' ids (V3 is not a
    /// Curated template and must never be mistaken for one by "reset to template").</summary>
    public const string TemplateId = "v3.synth";

    // STABLE section ids. The pane keys its reorder bands, its scroll identity and its section lookup off them, so a fresh
    // id per rebuild — and this document IS rebuilt on every state change — would reset all three on every keystroke.
    public const string PinsId = "v3.pins";
    public const string SystemId = "v3.system";
    public const string LibraryId = "v3.library";

    /// <summary>The Liked Songs shortcut's route key (§3.0 obligation 2) and its item id inside the system section.</summary>
    public const string LikedRouteKey = "liked";
    public const string SystemLikedItemId = "v3.system.liked";

    /// <summary>The divider that closes the system row off from the library when there is no pin band to do it instead
    /// (see obligation 2's placement rule below).</summary>
    public const string SystemRuleId = "v3.rule.system";

    /// <summary>Build the ephemeral document for one V3 view state.</summary>
    /// <param name="topBar">W3 — the shell's shortcut band (<c>SidebarPreferences.TopBar</c>), the ONE global list
    /// Classic/Curated still materialise as a document section (<c>SidebarShortcutsSection</c>). V3 does NOT: its own
    /// band renders as fixed chrome above the header (<c>LibraryV3NavBand</c>, mounted by <c>LibraryV3Chrome</c>), so
    /// this document must never carry it — a band that scrolls, filters and searches with the list is exactly the
    /// "shortcuts always showing / never where you'd expect" complaint W3 exists to remove. The parameter survives for
    /// exactly ONE rule: a user who put Liked Songs in the band must not also get the document's own system row two
    /// places down — see obligation 2 below.</param>
    public static SidebarCustomLayout Build(in LibraryV3DocState state,
        IReadOnlyList<SidebarItemSpec>? topBar = null)
    {
        var sections = new List<SidebarSectionSpec>(4);

        // 1 — LIKED SONGS, the library's own row. §3.0 obligation 2, scoped to the lenses where a saved-songs shortcut is
        //     truthful (state.LikedVisible: not pinned, not searching, not drilled, All/Playlists only) and skipped when
        //     the shell's shortcut band already carries a `liked` ROUTE item two rows away (ContainsRoute — the existing
        //     dedupe obligation the `topBar` parameter exists for; an ENTITY item whose uri maps onto Liked does not
        //     count, per ContainsRoute's own contract).
        //
        //     It sits BEFORE the pin band on purpose: Liked Songs is the library's own saved-songs collection, so it
        //     reads as the first row of the pinned band rather than a shelf above it. The renderer draws it under V3's
        //     RowStyle.Slot with the dynamic Liked cover and a "Playlist · n songs" subtitle, because the section asks
        //     for Artwork AND Subtitles — Cozy + Subtitles is what selects the 44-DIP height the content rows share
        //     (HeightFor(Cozy, subtitle) == 44, the same ladder the pin band and the library section use).
        //     CountBadges: false keeps the count in the subtitle only (a StaticLinks section cannot show the badge
        //     anyway — AllowsDisplayField(StaticLinks, CountBadges) is false). ShowInRail: true gives the 56-DIP rail
        //     its tile through the shared planner, now that the mode's rail head no longer draws the fixed destinations
        //     itself (W3 — LibraryV3Sidebar.BuildRailHead dropped them).
        //
        //     If Liked is itself pinned (LikedPinned, folded into LikedVisible), the pin band carries it instead — same
        //     place in the list, now reorderable like any other pin.
        bool systemRow = state.LikedVisible && !SidebarShortcutsSection.ContainsRoute(topBar, LikedRouteKey);
        if (systemRow)
        {
            sections.Add(new SidebarSectionSpec(SystemId, SidebarSectionKind.StaticLinks,
                Title: null, TitleLocKey: null,        // V3 renders NO section headers (§3.2.7) — no title ⇒ no header row
                Hidden: false, Collapsed: false,
                Display: new SidebarDisplayOptions(
                    Density: SidebarDensity.Cozy,
                    Presentation: SidebarPresentation.List,
                    Artwork: true, Subtitles: true, CountBadges: false,
                    CollapsedByDefault: false, ShowInRail: true),
                Items: [new SidebarItemSpec(SystemLikedItemId, SidebarItemTarget.Route, LikedRouteKey,
                                            IconOverride: "Heart")]));

            // The pin band's own PinEnd gutter already closes the whole band off from the library when pins are
            // visible; without a pin band, nothing else marks where the system row ends, so the system row would run
            // straight into the library with no seam. A plain rule closes it — the same chrome Classic uses between
            // its own fixed bands.
            if (!state.PinsBandVisible)
                sections.Add(new SidebarSectionSpec(SystemRuleId, SidebarSectionKind.Divider));
        }

        // 2 — the PIN BAND. The shaped projection already carries the surviving pins as its leading band (pin order,
        //     filter-aware), and the mode component hands exactly that band to the planner as `Pins`; this section renders
        //     it, which is also what gives V3 drop-to-pin and pin reordering it never had.
        //     Absent when there is nothing pinned (an empty Pinned section is the drop-zone card, which V3's chrome does
        //     not budget for) and at a drilled-in level (a folder level is that folder's contents, not the library root).
        if (state.PinsBandVisible)
            sections.Add(new SidebarSectionSpec(PinsId, SidebarSectionKind.Pinned,
                Title: null, TitleLocKey: null,        // V3 renders NO section headers (§3.2.7) — no title ⇒ no header row
                Hidden: false, Collapsed: false,
                Display: ContentDisplay(in state)));

        // 3 — THE ONE LIBRARY SECTION. Kind is the only thing that varies, and it varies for exactly one reason: only the
        //     PlaylistTree path stamps a row's NESTING DEPTH (indentation) and preserves the given order verbatim, while
        //     only the EntityList path can present a GRID. See KindFor.
        var kind = KindFor(in state);
        sections.Add(new SidebarSectionSpec(LibraryId, kind,
            Title: null, TitleLocKey: null,
            Hidden: false, Collapsed: false,
            Display: ContentDisplay(in state) with
            {
                // The chrome owns V3's three ACTIONABLE empty states (§3.2.10: clear search / clear filter / create
                // playlist), so the pane must not also draw its own quiet one-line hint under them.
                EmptyBehavior = SidebarEmptyBehavior.HideBody,
            },
            // A PlaylistTree receives an already shaped, tree-regrouped sequence from LibraryV3View. Null is the shared
            // planner's explicit "preserve this rootlist order" contract; an honest query remains necessary for the flat
            // EntityList path (including the rail pass).
            Query: kind == SidebarSectionKind.PlaylistTree ? null : QueryFor(in state)));

        return new SidebarCustomLayout(TemplateId, sections);
    }

    /// <summary>Which section kind renders the library for this state.
    ///
    /// <para><b>PlaylistTree</b> when folders can appear inline — a LIST view, no search (a search flattens), no drilled-in
    /// level (a level is one folder's direct children, already flat) and a lens that contains playlists. That path is the
    /// only one that stamps <c>depth + entry.Depth</c> onto its rows (the indent) and the only one that emits rows in the
    /// given order without re-sorting, which is what the tree-grouped order needs.</para>
    ///
    /// <para><b>EntityList</b> otherwise: it is the only kind that honours <c>Presentation.Grid</c>, and for a flat lens
    /// (Albums / Artists / Podcasts), a search or a drill level there is no nesting to express.</para></summary>
    public static SidebarSectionKind KindFor(in LibraryV3DocState state)
        => FoldersApply(in state) ? SidebarSectionKind.PlaylistTree : SidebarSectionKind.EntityList;

    /// <summary>§3.2.7's folder rule, made mechanical: folder rows exist only under the All / Playlists lenses, only in a
    /// list view, only with an empty search, and never at a drilled-in level.</summary>
    public static bool FoldersApply(in LibraryV3DocState state)
        => IsList(state.View) && !state.Searching && !state.Drilled
           && (state.Filter == (int)SidebarV3Filter.All || state.Filter == (int)SidebarV3Filter.Playlists);

    /// <summary>The display options every CONTENT band (the pin band and the library) shares — V3's view code, mapped once.
    /// <para>CompactList ⇒ List + Compact (32 DIP rows, 20-DIP art, no subtitle) · List ⇒ List + Cozy (44 / 32 / subtitle)
    /// · CompactGrid ⇒ Grid, no subtitle · Grid ⇒ Grid with a subtitle. Grid column count is DERIVED from the pane width by
    /// the caller (§3.2.8: the sidebar's cell size is derived, never chosen — which is why V3's flyout has no size row and
    /// why the persisted <c>V3GridSize</c> stays unread).</para></summary>
    public static SidebarDisplayOptions ContentDisplay(in LibraryV3DocState state) => new(
        Density: DensityFor(state.View),
        Presentation: PresentationFor(state.View),
        Artwork: true,
        Subtitles: SubtitlesFor(state.View),
        CountBadges: false,
        CollapsedByDefault: false,
        ShowInRail: true,
        MaxItems: 0,
        GridColumns: ClampColumns(state.GridColumns));

    public static SidebarPresentation PresentationFor(int view)
        => IsGrid(view) ? SidebarPresentation.Grid : SidebarPresentation.List;

    /// <summary>Compact only for the compact LIST; every other view is Cozy (a grid strip's cell height is measured, so its
    /// density only picks the art ladder the pane falls back to).</summary>
    public static SidebarDensity DensityFor(int view)
        => view == (int)SidebarV3View.CompactList ? SidebarDensity.Compact : SidebarDensity.Cozy;

    /// <summary>Only the two roomy views carry a second line (§3.2.8). Compact density suppresses subtitles anyway; saying
    /// so here keeps the pane's height ladder honest (Cozy + subtitle = 44, Cozy alone = 40).</summary>
    public static bool SubtitlesFor(int view)
        => view == (int)SidebarV3View.List || view == (int)SidebarV3View.Grid;

    /// <summary>The section's query — the MIRROR of the shaping the projection already applied.
    ///
    /// <para>It is not a second filter: the rows handed to the planner are already the shaped V3 projection, so every
    /// clause here is a no-op on the expanded pane. It still has to be right, because the RAIL plans from the same document
    /// (<c>SidebarRowPlanner.BuildRail</c> re-filters and re-sorts an EntityList section for its tiles) and because the
    /// document is the honest statement of what the section IS.</para>
    ///
    /// <para>DIRECTION RECONCILIATION: V3's <c>Descending</c> flag means "REVERSE this sort's natural direction" (what
    /// <c>SidebarSort</c> takes), while <c>SidebarEntityQuery.Descending</c> means "descending" literally — and the
    /// planner's comparator undoes that mapping again for the two recency modes. This inverts it for exactly those two, so
    /// the query round-trips to the same comparator the projection used.</para></summary>
    public static SidebarEntityQuery QueryFor(in LibraryV3DocState state)
    {
        var sort = SortFor(state.Sort, state.Filter);
        bool recency = sort is SidebarSortMode.Recents or SidebarSortMode.RecentlyAdded;
        return new SidebarEntityQuery(
            Kinds: KindsFor(state.Filter),
            Sort: sort,
            Descending: recency ? !state.Descending : state.Descending,
            Qualifier: QualifierFor(in state));
    }

    /// <summary>V3 chip → the query's kind set. Playlists includes folders (the Core mask has no folder bit; the app-side
    /// <c>SidebarEntryKinds.From</c> maps Playlists onto the whole tree).</summary>
    public static SidebarEntityKinds KindsFor(int filter) => filter switch
    {
        (int)SidebarV3Filter.Playlists => SidebarEntityKinds.Playlists,
        (int)SidebarV3Filter.Podcasts => SidebarEntityKinds.Shows,
        (int)SidebarV3Filter.Albums => SidebarEntityKinds.Albums,
        (int)SidebarV3Filter.Artists => SidebarEntityKinds.Artists,
        _ => SidebarEntityKinds.All,
    };

    /// <summary>V3 sort code → the query's sort mode, with locked decision 10's fallback applied: Custom order exists only
    /// under the Playlists lens and degrades to Alphabetical FOR DISPLAY everywhere else, exactly as
    /// <c>SidebarSort.Effective</c> does for the projection (the persisted preference is never rewritten here).</summary>
    public static SidebarSortMode SortFor(int sort, int filter)
    {
        if (sort == (int)SidebarV3Sort.Custom && filter != (int)SidebarV3Filter.Playlists)
            return SidebarSortMode.Alphabetical;
        return sort switch
        {
            (int)SidebarV3Sort.RecentlyAdded => SidebarSortMode.RecentlyAdded,
            (int)SidebarV3Sort.Alphabetical => SidebarSortMode.Alphabetical,
            (int)SidebarV3Sort.Creator => SidebarSortMode.Creator,
            (int)SidebarV3Sort.Custom => SidebarSortMode.CustomOrder,
            _ => SidebarSortMode.Recents,
        };
    }

    /// <summary>The qualifier the query carries. It is EFFECTIVE, not persisted: a qualifier the data cannot evidence
    /// (<c>QualifiersAvailable == false</c>, locked decision 10) and a qualifier outside the Playlists lens are both Any —
    /// the same two coercions the projection applied, so the mirror cannot filter MORE than the rows it describes.</summary>
    public static SidebarPlaylistQualifier QualifierFor(in LibraryV3DocState state)
    {
        if (!state.QualifiersAvailable || state.Filter != (int)SidebarV3Filter.Playlists)
            return SidebarPlaylistQualifier.Any;
        return state.Qualifier switch
        {
            (int)SidebarV3Qualifier.ByYou => SidebarPlaylistQualifier.ByYou,
            (int)SidebarV3Qualifier.BySpotify => SidebarPlaylistQualifier.BySpotify,
            (int)SidebarV3Qualifier.Mixed => SidebarPlaylistQualifier.Mixed,
            _ => SidebarPlaylistQualifier.Any,
        };
    }

    public static bool IsGrid(int view) => view >= (int)SidebarV3View.CompactGrid;
    public static bool IsList(int view) => view <= (int)SidebarV3View.List;

    /// <summary>The reducer clamps a persisted document's grid columns to [2,4]; a derived count must land in the same
    /// range, and a pane too narrow for two columns still gets two (the pane's strip wraps rather than overflowing).</summary>
    public static int ClampColumns(int columns) => columns < 2 ? 2 : columns > 4 ? 4 : columns;
}

/// <summary>
/// The V3 view state the document is a function of — plain values only, so the synthesizer stays pure and testable. Every
/// member is what the mode component read from <see cref="SidebarPreferences"/> (or derived from the pane width) on the
/// render that built the document.
/// </summary>
/// <param name="Filter">A <see cref="SidebarV3Filter"/> code, already normalized.</param>
/// <param name="Qualifier">A <see cref="SidebarV3Qualifier"/> code, already normalized.</param>
/// <param name="Sort">A <see cref="SidebarV3Sort"/> code, already normalized.</param>
/// <param name="Descending">V3's direction flag — "reverse the sort's natural direction", NOT "descending".</param>
/// <param name="View">A <see cref="SidebarV3View"/> code, already normalized.</param>
/// <param name="GridColumns">The column count derived from the pane width (ignored by the list views).</param>
/// <param name="Searching">Whether the library-only search box holds a non-empty query (a search FLATTENS the tree).</param>
/// <param name="DrillFolderId">The folder whose direct children are being listed (Revision 2's narrow drill-in level), or
/// null/"" at the library root.</param>
/// <param name="HasPins">Whether any pin SURVIVED the active lens (the projection's pin band is non-empty).</param>
/// <param name="LikedPinned">Whether Liked Songs is itself pinned — it is then rendered as pin #n, never twice.</param>
/// <param name="QualifiersAvailable">Whether the data evidences ≥2 provenance classes (locked decision 10).</param>
/// <param name="DragInFlight">Issue #85 (H1) — a Wavee resource drag is live ANYWHERE right now (the mode root's own
/// <c>UseDragState()</c> read, folded in here rather than read from this pure struct — see <c>PinsBandVisible</c>).</param>
readonly record struct LibraryV3DocState(
    int Filter = (int)SidebarV3Filter.All,
    int Qualifier = (int)SidebarV3Qualifier.Any,
    int Sort = (int)SidebarV3Sort.Recents,
    bool Descending = false,
    int View = (int)SidebarV3View.List,
    int GridColumns = 2,
    bool Searching = false,
    string? DrillFolderId = null,
    bool HasPins = false,
    bool LikedPinned = false,
    bool QualifiersAvailable = false,
    bool DragInFlight = false)
{
    /// <summary>At a drilled-in level the pane shows exactly one folder's direct children — no pin band, no shortcut.</summary>
    public bool Drilled => DrillFolderId is { Length: > 0 };

    /// <summary>The pin band renders whenever pins survived the lens and the pane is at the library root with no query
    /// — OR (issue #85, H1) while a drag is live anywhere: with zero pins the band used to disappear ENTIRELY, which
    /// made pinning by drag impossible on a fresh install (the only other path in was a row's context menu). The band
    /// stays hidden at rest with no pins so V3's chrome budget is unchanged; it exists only for the duration of a
    /// drag, which is exactly what makes the drop target affordable. <see cref="SidebarPinDropZone"/> itself already
    /// downgrades to its quiet resting look for a drag that cannot actually be pinned (a track), so rendering the
    /// section for ANY live resource drag — not only a pin-eligible one — is enough; the zone decides the rest.
    /// <para>A SEARCH deliberately dissolves it regardless: search results are one flat relevance list (the
    /// projection still leads with the matching pins, so they are not lost — they are simply not a separate band),
    /// which is also what makes "the list is empty" mean "no results" and nothing else.</para></summary>
    public bool PinsBandVisible => (HasPins || DragInFlight) && !Drilled && !Searching;

    /// <summary>§3.0 obligation 2's exact scope: the unfiltered library and the Playlists lens (where Spotify lists it
    /// too), never while searching (a route row is not a search result), never when it is already a pin, never inside a
    /// folder.</summary>
    public bool LikedVisible
        => !LikedPinned && !Searching && !Drilled
           && (Filter == (int)SidebarV3Filter.All || Filter == (int)SidebarV3Filter.Playlists);
}
