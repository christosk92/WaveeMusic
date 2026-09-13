using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;
using Wavee.Core.Sidebar;

namespace Wavee;

// MODE A / "CLASSIC" of the three-mode sidebar (spec §3.1). Mounted only by SidebarHost, under the "sidebar.classic" key.
//
// R3.0.2 — WHAT THIS FILE USED TO BE, AND WHY IT ISN'T ANY MORE. Classic shipped as a hand-built pane: its own expanded
// body, its own pinned section + reorder wiring + drop zone, its own 56-DIP rail, its own row builders, its own count
// badge, its own per-kind subtitle table, its own selection mechanism. Curated and V3 shipped their own of each. Three
// containers sharing only leaf primitives is why the user's screenshot review found four left insets, accent count pills
// in one mode and quiet numbers in another, no section rhythm and no collapse motion: nothing could be fixed in one place.
//
// Classic is now a LOCKED BUILT-IN DOCUMENT (`SidebarBuiltInDocuments.Classic`) rendered by the ONE `SidebarPane`. This
// component is the mode shell: it supplies the document, routes a header click to Classic's own persisted section flags,
// declares itself READ-ONLY (no customizer entry — the quick menu's "Customize" still switches to Curated first, which is
// landed behaviour), and hands the rail its create-playlist affordance. Everything visual comes from the shared renderer.
//
// THE PRESERVATION CONTRACT (§3.1.1) IS HONOURED AT THE PIXEL LEVEL, NOT THE IMPLEMENTATION LEVEL — the user's explicit
// choice in R3.0. What survives verbatim: the pane padding (8,8,8,12) and therefore the 8+6=14 row inset; the 44-DIP row
// height for every band (see the document's Display options); the 12/600/TextSecondary header typography; the leading
// `rule:` dividers; the 4-state NavigationViewItem backplate ramp; 32-DIP playlist covers with the song-count subtitle;
// the async count badges (now quiet numbers — the ONE badge the user asked for); the always-mounted expanded + rail layers
// measured at the persisted open width; the DevTools entry; the pinned drop zone; the quick layout menu on the first
// header and on the pane's background context menu. The DEVIATIONS are listed in the handoff, not hidden: selection is the
// per-row indicator (a recycling list cannot host a measured overlay pill), the pinned list is virtualized and therefore
// uncapped (no "Show all (n)" row), and pinned reorder uses the displacement channel rather than a live projection.
//
// MOTION: pane width is still the shell wrapper's (WaveeShell.cs SizeMode.Reflow); section chevrons rotate on
// MotionTok.ControlFast; a collapse/expand fades + glides the rows it adds or displaces (SidebarPane.Choreograph);
// reordering is MotionTok.ItemPlacement.
sealed class WaveeSidebar : Component
{
    readonly Signal<Route> _route;
    readonly Action<string, string?> _go;
    readonly Signal<bool> _compact;
    readonly Signal<float> _expandedWidth;

    SidebarPreferences? _prefs;
    ActionServices? _acts;

    // The document cache. `Document` is invoked on EVERY pane render, and a freshly-minted document per call defeats
    // SidebarPane.PublishStage's wholesale `!ReferenceEquals(stage.Document, Doc)` test — so every publish re-skinned the
    // WHOLE realized window (Slot×52 + Pill×44 + Chevron×6 per track boundary / navigation) and the landed per-row
    // SidebarRowDiff never ran. Keyed on the three collapse flags, exactly as LibraryV3Sidebar caches its synthesized
    // document; the flag READS stay unconditional below, because they ARE the pane's subscription.
    readonly ClassicDocumentCache _docCache = new();

    public WaveeSidebar(Signal<Route> route, Action<string, string?> go, Signal<bool> compact, Signal<float> expandedWidth)
    {
        _route = route; _go = go; _compact = compact; _expandedWidth = expandedWidth;
    }

    public override Element Render()
    {
        // Refreshed each render; the frozen config's delegates read these fields, so they always see the live services.
        _prefs = UseContext(SidebarPreferences.Slot);
        _acts = UseContext(ActionServices.Slot);

        var config = UseMemo(() => new SidebarPaneConfig
        {
            Design = SidebarDesign.Classic,
            ScrollKeyPrefix = "sidebar.classic",
            Document = BuildDocument,
            // Classic's collapse state is NOT document state: it lives in three persisted preference flags so the docked
            // pane and the narrow drawer (two independent mounts) agree and it survives a design round-trip. Reading them
            // in ModeEpoch is what makes the pane re-plan — and the realized rows re-skin — on a toggle.
            ModeEpoch = SectionEpoch,
            SetSectionCollapsed = SetSection,
            // A LOCKED document: no inline EntityList controls, no "Remove item" verb, no empty-pane customize CTA.
            ReadOnly = true,
            // Classic has no library-only search head (its Playlists section is a tree, not an EntityList).
            SearchHead = false,
            OnCreatePlaylist = CreatePlaylist,
            // The PlaylistTree header's "+" — Classic's ONE create affordance now that the trailing create ROW is gone.
            // A config flag, never a Design branch (rule 1).
            HeaderCreate = true,
            // PHASE 1 — the shortcut band is no longer a config delegate: it is the FIRST SECTION of the document
            // BuildDocument returns (Decision A), so the renderer needs no `NavBand`/`RailHead` seam and the rail form
            // rides the planner's existing `ShowInRail`.
            // The rail's create-playlist affordance. A rail plan is tiles-from-sections and cannot express authored
            // chrome, so Classic's landed 40-DIP "+" tile is appended by the pane instead of planned.
            RailFooter = () => Embed.Comp(() => new SidebarCreateButton(
                CreatePlaylist, menu: CreateMenu, box: SidebarRailItem.Box, glyph: 16f)),
        }, DepKey.Empty);

        return Embed.Comp(() => new SidebarPane(config, _route, _go, _compact, _expandedWidth));
    }

    /// <summary>Classic's locked document, built from its three live collapse flags. The section ids are STABLE strings,
    /// so the pane's reorder bands and collapse routing survive every rebuild.
    ///
    /// <para>The three flag reads are UNCONDITIONAL and come first — they are the pane's subscription (this runs inside
    /// the pane's own render), so short-circuiting them behind the cache would silently stop a toggle from re-planning.
    /// Only the BUILD is cached, and only while the flags are unchanged, so a publish that changed nothing hands the
    /// pane the same document instance and its per-row diff can do its job.</para></summary>
    SidebarCustomLayout BuildDocument()
    {
        var prefs = _prefs;
        // `prefs == null` (an isolated harness mount) keeps every section open and simply has no pins — the same defensive
        // shape the landed component used.
        bool pinnedOpen = prefs?.ClassicPinnedOpen.Value ?? true;
        bool libraryOpen = prefs?.ClassicLibraryOpen.Value ?? true;
        bool playlistsOpen = prefs?.ClassicPlaylistsOpen.Value ?? true;
        // PHASE 1 — the shortcut band is ONE GLOBAL LIST on the Curated document, so Classic's locked document takes it
        // as an argument and materialises it as its first section. A probe mount with no preference service falls back
        // to the built-in default (Home), which is exactly what the shell renders for a never-customized band.
        var topBar = prefs?.TopBar ?? SidebarCustomLayout.DefaultTopBar;
        return _docCache.Get(pinnedOpen, libraryOpen, playlistsOpen, topBar);
    }

    /// <summary>The three section flags folded into the pane's mode epoch. Reading them with <c>.Value</c> IS the
    /// subscription (the pane invokes this inside its own render and inside every realized row's epoch read).</summary>
    int SectionEpoch()
    {
        var prefs = _prefs;
        if (prefs is null) return 0;
        // The SAME bitmask BuildDocument's cache is keyed on (ClassicDocumentCache.FlagsOf) — one owner, so a toggle
        // cannot re-plan against a document the cache decided not to rebuild.
        return ClassicDocumentCache.FlagsOf(
            prefs.ClassicPinnedOpen.Value, prefs.ClassicLibraryOpen.Value, prefs.ClassicPlaylistsOpen.Value);
    }

    void SetSection(string sectionId, bool collapsed)
    {
        if (SidebarBuiltInDocuments.ClassicSectionOf(sectionId) is not { } section) return;
        _prefs?.SetClassicSection(section, !collapsed);
    }

    /// <summary>Create-affordance handler — the ONE create path (<see cref="PlaylistCreateFlow"/>): the numbered
    /// "{base} #N" name, the optimistic row already in the store, immediate navigation to a real 0-track owner page, and
    /// the CreateFailed toast/notice observed off-thread. It used to create an unnumbered "New playlist" here and awaited
    /// the round trip before navigating, so three of them in a sidebar were indistinguishable and each one felt like a
    /// network operation.</summary>
    void CreatePlaylist()
    {
        if (_acts is not { } acts) return;
        PlaylistCreateFlow.Create(acts, default, navigate: true);
    }

    /// <summary>The rail "+"'s flyout — the SAME two verbs the expanded pane's header "+" offers, so a collapsed
    /// sidebar can create a folder too. Built at open time; null (no services yet) makes the button fall back to its
    /// plain create-playlist click rather than opening an empty flyout.</summary>
    ContextMenuModel? CreateMenu()
    {
        if (_acts is not { } acts || acts.Library is null) return null;
        return new ContextMenuModel(new List<MenuFlyoutItem>(2)
        {
            new(Loc.Get(Strings.Detail.NewPlaylist), ActionIcons.Resolve(ActionIcons.Add), true, CreatePlaylist),
            new(Loc.Get(Strings.Sidebar.CreateFolder), ActionIcons.Resolve(ActionIcons.Folder),
                acts.Overlay is not null, () => FolderActions.NewFolder(acts, null)),
        });
    }
}
