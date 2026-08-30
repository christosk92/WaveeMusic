using System;
using FluentGpu.Controls;   // Route
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core.Sidebar;

namespace Wavee;

// MODE C / "Wavee Curated" (spec §C5) — the section-based sidebar the user authors in the full-page customizer.
// Mounted only by SidebarHost, under the "sidebar.curated" key (and a SECOND, independent mount for the narrow drawer);
// also mounted by the customizer's live-preview column, which is why the CTOR SIGNATURE IS FROZEN.
//
// R3.0.4 — this used to BE the renderer (750 lines of plan/epoch/reorder/rail machinery plus a 900-line row slot). Full
// unification moved all of it to `Features/Sidebar/Pane/` (`SidebarPane` + `SidebarPaneSlot` + `SidebarPaneRail` + the
// text/inline-control helpers), so Curated is now what every mode is: a DOCUMENT + a `SidebarPaneConfig`. Its document is
// the persisted one (`SidebarPreferences.Layout`), it is EDITABLE, and nothing else distinguishes it from Classic.
//
// LIVE EDITS still work exactly as before: every mutation goes through `SidebarPreferences.Dispatch(command)` → reducer →
// undo pre-image → `LayoutVersion` bump → autosave, and the pane subscribes `LayoutVersion` — so the customizer never
// talks to the renderer, it dispatches, and both the live pane and its own preview re-plan from the one document.
sealed class CuratedSidebar : Component
{
    readonly Signal<Route> _route;
    readonly Action<string, string?> _go;
    readonly Signal<bool> _compact;
    readonly Signal<float> _expandedWidth;
    readonly bool _inDrawer;

    SidebarPreferences? _prefs;
    ActionServices? _acts;

    // PHASE 1 — the RENDER-PATH materialization of the shortcut band, cached on the document it was derived from.
    //
    // `Document` is invoked on EVERY pane render, and `SidebarPane.PublishStage` decides between a per-row epoch diff
    // and a whole-window re-skin with `!ReferenceEquals(stage.Document, Doc)` — so a freshly-prepended document per
    // render would defeat that test exactly as an uncached Classic document once did. `SidebarPreferences.Layout` is
    // replaced only by a real change (the reducer's NoChange arm returns the input), so keying on its REFERENCE is both
    // sound and O(1).
    //
    // The materialized document is handed to the renderer and to nothing else: it is never dispatched, never compared
    // against the persisted document and never saved, so `SidebarCustomLayout.TopBar` stays exactly what it is on the
    // wire and the persisted section list never gains a section carrying the sentinel id.
    SidebarCustomLayout? _fallback;
    SidebarCustomLayout? _sourceDoc;
    SidebarCustomLayout? _renderDoc;

    public CuratedSidebar(Signal<Route> route, Action<string, string?> go, Signal<bool> compact,
                          Signal<float> expandedWidth, bool inDrawer = false)
    {
        _route = route; _go = go; _compact = compact; _expandedWidth = expandedWidth; _inDrawer = inDrawer;
    }

    public override Element Render()
    {
        // Refreshed each render, read by the config's delegates (the landed `WaveeSidebar._acts` pattern): a service
        // instance is reference-stable, so this never churns and the frozen config always sees the live one.
        _prefs = UseContext(SidebarPreferences.Slot);

        // PHASE 2 / Decision B — attach the shared edit session's SERVICES here, in Curated's composition root, because
        // this is the one place that has both the session (through the preferences service) and the app services its
        // options popover needs. They are plain fields on a reference-stable object, refreshed every render exactly like
        // `_prefs` above — never a signal write from Render.
        var acts = UseContext(ActionServices.Slot);
        _acts = acts;
        var registry = UseContext(WaveeExtensionRegistry.Slot) ?? acts?.Extensions;
        var overlay = UseContext(Overlay.Service);
        if (_prefs is { } prefsForEdit)
        {
            var session = prefsForEdit.Edit;
            session.Prefs = prefsForEdit;
            session.Acts = acts;
            session.Registry = registry;
            session.OverlaySvc = overlay;
        }

        // A probe / isolated harness mount has no preference service and therefore no document: fall back to the built-in
        // Curated template ONCE (Build mints fresh ids per call, so it must be memoized) and let every dynamic section
        // resolve to skeletons. That is the honest shape — never a blank pane.
        _fallback = UseMemo(static () => SidebarLayoutDefaults.CuratedLayout(), DepKey.Empty);

        // The config is built ONCE and frozen into the pane (the component-props contract). Every member is a delegate, so
        // it reads live state at the pane's render time.
        var config = UseMemo(() => new SidebarPaneConfig
        {
            Design = SidebarDesign.Curated,
            ScrollKeyPrefix = "sidebar.curated",
            Document = BuildDocument,
            // The document's own version is already in the pane's plan DepKey, so Curated needs no extra mode epoch.
            SetSectionCollapsed = (id, collapsed) => _prefs?.Dispatch(new SetSectionCollapsed(id, collapsed)),
            ReadOnly = false,
            SearchHead = true,
            // PHASE 1 — the shortcut band is no longer a config delegate: BuildDocument materialises it as the document's
            // first section (Decision A), so the renderer needs no `NavBand`/`RailHead` seam.
            //
            // PHASE 2 / Decision B — the ONE canvas seam, and the ONLY mode that supplies it: Classic's and V3's
            // documents are read-only, so an editor over them would offer commands the reducer cannot execute. The
            // RENDERER never learns that — it reads this delegate and never `Config.Design`. A DELEGATE, not a value:
            // this config is frozen at mount and a `SidebarEditState` here would pin frame 1's session forever.
            Edit = ReadEditSession,
            OnCustomize = OpenCustomizer,
            OnCreatePlaylist = CreatePlaylist,
            // Same affordance as Classic's: a "+" in every PlaylistTree section header. A config flag, never a Design
            // branch (rule 1) — and the reason the renderer needs to know nothing about which mode it is drawing.
            HeaderCreate = true,
        }, DepKey.Empty);

        return Embed.Comp(() => new SidebarPane(config, _route, _go, _compact, _expandedWidth, _inDrawer));
    }

    /// <summary>The document the pane renders: the persisted one, with the shortcut band materialised as its FIRST
    /// section (Decision A). Invoked inside the PANE's render, so the signals it reads subscribe the pane — here that
    /// is <c>SidebarPreferences.Layout</c>, which the pane also folds into its plan <c>DepKey</c> through
    /// <c>LayoutVersion</c>, so a band edit re-plans exactly like any other document edit.</summary>
    SidebarCustomLayout BuildDocument()
    {
        var source = _prefs?.Layout ?? _fallback ?? SidebarCustomLayout.Empty;
        if (_renderDoc is { } cached && ReferenceEquals(_sourceDoc, source)) return cached;
        _sourceDoc = source;
        // EffectiveTopBar, not TopBar: a never-customized band still renders its built-in Home, and that rule has ONE
        // owner on the model.
        _renderDoc = SidebarShortcutsSection.Prepend(source, source.EffectiveTopBar);
        return _renderDoc;
    }

    /// <summary>The live edit session as a value, or null when the customizer is not open — i.e. THE ONE GATE that arms
    /// the canvas, and the whole of Decision B's "the route survives, its job changes".
    ///
    /// <para><b>Why the ROUTE and not a flag.</b> Structural drag must be armed exactly while the user is customizing
    /// (the Discord lesson), so the disarm must be impossible to miss. A boolean set by the customizer page's
    /// mount/unmount effect would be: that page is a <c>Flow.KeepAlive</c> destination (<c>ContentHost</c>, MaxEntries
    /// 8), so Done / Back / a tab switch PARK it instead of unmounting it and the cleanup would not run until the page
    /// aged out of the ring — leaving the live sidebar in edit mode indefinitely. The active route cannot go stale, and
    /// reading it here (inside the PANE's render, through the frozen config delegate) is also the subscription that
    /// makes leaving the customizer re-plan the pane on the very next frame.</para>
    ///
    /// <para>This lives in the MODE component, which is where mode knowledge belongs: the renderer still sees only
    /// "there is a session" / "there is not", and still never branches on <c>Config.Design</c>.</para>
    ///
    /// <para>A drawer mount reads the SAME session on purpose: the canvas is whichever Curated pane is on screen, and
    /// two panes disagreeing about which card is expanded would be two editors.</para></summary>
    SidebarEditState? ReadEditSession()
    {
        if (_prefs is not { } prefs) return null;
        return string.Equals(_route.Value.Name, SidebarLayoutMenu.CustomizeRoute, StringComparison.Ordinal)
            ? prefs.Edit.Read()
            : null;
    }

    void OpenCustomizer()
    {
        _prefs?.SwitchDesign(SidebarDesign.Curated);   // no-op when already Curated
        _go(SidebarLayoutMenu.CustomizeRoute, null);
    }

    /// <summary>The PlaylistTree header "+"'s plain-click verb — the ONE create path
    /// (<see cref="PlaylistCreateFlow"/>), so Classic, V3 and Curated cannot drift on what "+" does or on what it names
    /// the playlist.</summary>
    void CreatePlaylist()
    {
        if (_acts is not { } acts) return;
        PlaylistCreateFlow.Create(acts, default, navigate: true);
    }
}
