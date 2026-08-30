using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core.Sidebar;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// PHASE 3 — THE COMPANION PAGE. Route "sidebar-customize", registered by ContentHost + ShellNav like any other
// destination, so it still participates in tabs, back/forward and KeepAlive.
//
// WHAT THIS PAGE STOPPED BEING. It used to render an ABSTRACTION of the sidebar — an outline, a docked inspector, a
// preview column and a bottom sheet, across a four-tier region ladder — while the real sidebar sat 24 DIP to its left,
// live, unused. That is structurally the Windows 11 split-brain model the research pass identified as the disliked one,
// and it is what produced the outline↔preview drift the reporter called "buggy". Decision B moved the editing surface
// onto the docked pane (Phase 2: the pane IS the canvas and the preview), so this page keeps exactly the jobs a canvas
// cannot do for itself:
//
//   1. PRESETS (P7) — the three designs and the five templates, one click each. This also makes today's SILENT
//      force-switch to Curated (SidebarLayoutMenu.Rows dispatches SwitchDesign before navigating) visible AND
//      reversible, which it never was.
//   2. THE PERSISTENT PALETTE — always visible, no popover and no accordion, with a Destinations group so "home"
//      answers with Home.
//   3. HIDDEN SECTIONS (P2) — a recovery list, so nothing a user hid is ever unreachable.
//   4. RESET + the documented escape hatch to sidebar-layout.json.
//
// ONE SCROLLING COLUMN, at every width. There is no tier to resolve any more: the four regions that needed one are
// gone, and a column that reflows is what let the old page hide the eyebrow, the saved-locally dot, the inline Reset
// and the preview from anyone whose window was not ~1600 wide.
//
// ONE DOCUMENT, NO PREVIEW COPY. The page mounts no copy of the layout: it reads UseContext(SidebarPreferences.Slot)
// and every edit goes through prefs.Dispatch(command) → reducer → undo pre-image → LayoutVersion bump → autosave. The
// docked sidebar and this page re-render from that one signal in the same frame — no apply step, no dirty state.
//
// PROPS FREEZE AT MOUNT, so every sub-component takes THIS page (a reference-stable holder of signals + delegates) as
// its single ctor arg. Each sub-component re-reads prefs.LayoutVersion itself, so a document edit re-renders it without
// the page rebuilding its children.
sealed class SidebarCustomizerPage : Component, ISidebarEditHost
{
    /// <summary>The header is a two-line title lane (eyebrow over title) beside the command cluster.</summary>
    const float HeaderHeight = 64f;

    /// <summary>The one content column's max width. A settings-style column that runs the full width of a 2560-DIP
    /// monitor is unreadable, and this page is a form, not a canvas — the canvas is the sidebar.</summary>
    const float ColumnMaxWidth = 720f;

    // ── shared editor state (signals, so a sub-component subscribes exactly what it reads) ────────────────────────────

    /// <summary>The <see cref="ISidebarEditHost"/> subject. The page itself no longer HAS a selection surface (the
    /// outline is gone and per-section options are a popover on the card, P3) — it is kept because the interface
    /// requires it and because <c>SidebarPickers</c>/<c>SidebarPropertyPanel</c> are written against the interface.
    /// The section the user is actually working on lives on the shared session; see <see cref="SelectedStaticLinks"/>.</summary>
    internal readonly Signal<string?> Selected = new(null);

    /// <summary>The selected item inside the selected section (the item pickers' subject).</summary>
    internal readonly Signal<string?> SelectedItem = new(null);

    /// <summary>The palette's live search text (session-only, never persisted).
    ///
    /// <para>DEFECT 4 — the CLEARING POLICY, stated once, here. The old palette never cleared it, so a popover reopened
    /// still filtered and looked empty. The palette is now persistent, so "on close" and "on collapse" no longer exist
    /// as moments; the two that remain are: (a) after an accepted ADD, because the query described what the user was
    /// looking for and they just found it, and (b) on entering/leaving the page, alongside the session's ergonomics
    /// reset. It is deliberately NOT cleared when the palette switches into contribution-pick mode — that mode now
    /// HONOURS the query (defect 5), so clearing it there would throw away the filter the user just typed.</para></summary>
    internal readonly Signal<string> PaletteQuery = new("");

    /// <summary>Bumped whenever <see cref="_rejectKey"/> changes — the inline reject strip's render dep AND the
    /// auto-dismiss timer's key.</summary>
    internal readonly Signal<int> RejectEpoch = new(0);

    /// <summary>Banner dismissals + the health re-read nudge (the store's fault properties are not signals).</summary>
    readonly Signal<int> _bannerEpoch = new(0);
    bool _corruptDismissed;
    SidebarPersistenceFault _dismissedPersistenceFault;

    /// <summary>The reject message's loc key (a PLAIN field — the epoch signal above carries the reactivity, so a render
    /// that shows it never subscribes to a string).</summary>
    string? _rejectKey;

    internal SidebarPreferences? Prefs;
    internal WaveeExtensionRegistry? Registry;
    internal ActionServices? Acts;
    internal IOverlayService? OverlaySvc;
    internal LibraryStore? Store;
    internal Action<string, string?>? Go;
    internal Action? Back;

    /// <summary>The persisted visit log used only as a deterministic fallback when this page is mounted outside the shell
    /// and therefore has no <see cref="HistoryStore.BackCtx"/> provider.</summary>
    internal HistoryStore? History;

    /// <summary>The route's one-shot <c>?topbar</c> argument, still parsed because <c>ContentHost</c> still passes
    /// <c>r.Arg</c> and shell chrome may still navigate with it. It currently has NO reader: the surface it used to
    /// focus was the outline's dedicated top-bar card, and Phase 1 turned the shortcut band into an ordinary section on
    /// the CANVAS, which this page cannot reach into. Kept (rather than silently dropped) so the argument keeps a named
    /// owner for whoever wires "reveal the Shortcuts card" on the pane side.</summary>
    internal bool FocusTopBarRequested { get; }

    // ── PHASE 2 / DECISION B — this page as an `ISidebarEditHost` ────────────────────────────────────────────────────
    //
    // Purely ADDITIVE: not one existing member changed shape. The customizer's generated control set
    // (`SidebarPropertyPanel` + the `Cz*` rows + the item/action pickers) used to take THIS TYPE; it now takes the small
    // interface below so the very same controls can also be hosted by the live pane's per-section options popover
    // (P3), driven by the shared `SidebarEditSession`. Explicit implementation keeps every call site inside this folder
    // reading the concrete members it always did.
    SidebarPreferences? ISidebarEditHost.Prefs => Prefs;
    ActionServices? ISidebarEditHost.Acts => Acts;
    WaveeExtensionRegistry? ISidebarEditHost.Registry => Registry;
    IOverlayService? ISidebarEditHost.OverlaySvc => OverlaySvc;
    Signal<int> ISidebarEditHost.RejectEpoch => RejectEpoch;
    Signal<string?> ISidebarEditHost.Selected => Selected;
    Signal<string?> ISidebarEditHost.SelectedItem => SelectedItem;
    SidebarRejectReason ISidebarEditHost.Dispatch(SidebarCommand command) => Dispatch(command);
    SidebarRejectReason ISidebarEditHost.DispatchTopBar(SidebarCommand command) => DispatchTopBar(command);
    void ISidebarEditHost.Select(string? sectionId) => Select(sectionId);

    public SidebarCustomizerPage(string? focusTarget)
        => FocusTopBarRequested = string.Equals(
            focusTarget, SidebarLayoutMenu.TopBarFocusArg, StringComparison.Ordinal);

    public override Element Render()
    {
        Prefs = UseContext(SidebarPreferences.Slot);
        Acts = UseContext(ActionServices.Slot);
        OverlaySvc = UseContext(Overlay.Service);
        Store = UseContext(LibraryStore.Slot);
        Registry = UseContext(WaveeExtensionRegistry.Slot) ?? Acts?.Extensions;
        Go = UseContext(HistoryStore.NavCtx);
        Back = UseContext(HistoryStore.BackCtx);
        History = UseContext(HistoryStore.Slot);

        // Every accepted command bumps this: the header's undo/redo enablement, the hidden list and the banners all read
        // the document, so the page itself must subscribe it too.
        int layoutVersion = Prefs?.LayoutVersion.Value ?? 0;
        _ = layoutVersion;
        _ = _bannerEpoch.Value;
        int rejectEpoch = RejectEpoch.Value;

        // PHASE 2 / DECISION B — the page's ONE piece of canvas wiring: reset the edit session's ERGONOMICS (and, per
        // defect 4, the palette query) on the way in and out, so a visit opens on a predictable state rather than on
        // whatever card was open last time.
        //
        // It deliberately does NOT arm or disarm the canvas. Arming is derived from the active ROUTE in
        // `CuratedSidebar.ReadEditSession`, because this page is a `Flow.KeepAlive` destination: Done / Back / a tab
        // switch PARK it rather than unmounting it, so an unmount cleanup would not run until the page aged out of the
        // 8-entry ring — and a flag cleared that late would leave structural drag armed on the live sidebar for the
        // rest of the session. This effect is therefore allowed to be missed; the gate is not.
        //
        // The pane is a SIBLING in the tree, so the seam between the two is the shared session on `SidebarPreferences`
        // and nothing else — the customizer never talks to the renderer (iron rule 6).
        var editSession = Prefs?.Edit;
        UseEffect(() =>
        {
            editSession?.ResetErgonomics();
            PaletteQuery.SetIfChanged("");
            return () =>
            {
                editSession?.ResetErgonomics();
                PaletteQuery.SetIfChanged("");
            };
        }, DepKey.Empty);

        // The inline reject strip auto-dismisses after 4 s. Keyed on the epoch, so each new rejection re-arms it.
        UseTimeout(ClearReject, 4000f, DepKey.From(rejectEpoch));

        return new BoxEl
        {
            Key = "customizer", Grow = 1f, Shrink = 1f, Direction = 1, MinWidth = 0f, MinHeight = 0f,
            ClipToBounds = true,
            Children = [HeaderBar(), Divider(), Banners(), Body()],
        };
    }

    // ── the header (Back · title · Undo · Redo · Reset · Done) ───────────────────────────────────────────────────────

    /// <summary>The header is FIXED now — no priority-fit table, no overflow bar.
    /// <para>The table existed because four regions plus a native <c>CommandBar</c> plus Done had to share a header at
    /// four tiers. What is left is six affordances totalling well under 400 DIP, and the title lane is the only flexible
    /// child (Grow 1 · Basis 0 · Shrink 1 · MinWidth 0), so under pressure the title ellipsizes and the cluster holds its
    /// width. A fit table for six fixed buttons would be machinery describing nothing.</para></summary>
    Element HeaderBar()
    {
        var prefs = Prefs;
        bool canUndo = prefs?.CanUndo ?? false;
        bool canRedo = prefs?.CanRedo ?? false;
        string undoTip = canUndo && prefs?.UndoLabel is { Length: > 0 } u
            ? Loc.Format(CzLoc.UndoOf, ("action", Loc.Get(u)))
            : Loc.Get(CzLoc.Undo);
        string redoTip = canRedo && prefs?.RedoLabel is { Length: > 0 } r
            ? Loc.Format(CzLoc.RedoOf, ("action", Loc.Get(r)))
            : Loc.Get(CzLoc.Redo);

        var persistence = prefs?.PersistenceHealth.Value ?? SidebarWriteResult.Healthy;

        var kids = new List<Element>(6)
        {
            BackButton(),
            new BoxEl
            {
                Direction = 1, Grow = 1f, Basis = 0f, Shrink = 1f, MinWidth = 0f, Justify = FlexJustify.Center,
                Children =
                [
                    // THE EYEBROW: the ACTIVE template, which is the one piece of context the title cannot carry —
                    // "Customize sidebar" is true of every document, "Wavee curated" says which one this is.
                    WaveeType.Eyebrow(Loc.Get(SidebarTemplates.NameLocKey(
                        prefs?.Layout.TemplateId ?? SidebarTemplates.Curated))) with
                    {
                        Color = WaveeAccent.Decor, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                    },
                    new TextEl(Loc.Get(CzLoc.Title))
                    {
                        Size = 16f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1,
                        Trim = TextTrim.CharacterEllipsis,
                    },
                ],
            },
        };

        if (persistence.Success) kids.Add(SavedIndicator());

        kids.Add(new BoxEl
        {
            Direction = 0, Shrink = 0f, Gap = Spacing.XS, AlignItems = FlexAlign.Center,
            Children =
            [
                ToolTip.Wrap(IconButton.Create(Icons.Undo, UndoStep, size: ControlSize.Small,
                    isEnabled: canUndo) with { Shrink = 0f }, undoTip),
                ToolTip.Wrap(IconButton.Create(Icons.Redo, RedoStep, size: ControlSize.Small,
                    isEnabled: canRedo) with { Shrink = 0f }, redoTip),
                Button.Create(Loc.Get(CzLoc.Reset), ConfirmReset, ButtonAppearance.Subtle, ControlSize.Small)
                    with { Shrink = 0f },
                Button.Create(Loc.Get(CzLoc.Done), Done, ButtonAppearance.Accent, ControlSize.Small)
                    with { Shrink = 0f },
            ],
        });

        return new BoxEl
        {
            Key = "cmdbar", Direction = 0, Height = HeaderHeight, Shrink = 0f, Gap = Spacing.S,
            AlignItems = FlexAlign.Center,
            Padding = new Edges4(Spacing.S, 0f, Spacing.L, 0f),
            Children = [.. kids],
        };
    }

    /// <summary>The customizer's back arrow invokes the shell's real browser-style Back callback. A standalone/headless
    /// mount falls back to the newest non-customizer visit without changing production navigation history.</summary>
    /// <remarks>The extra wrapper box is LOAD-BEARING: <c>ToolTip</c>'s own root declares
    /// <c>AlignSelf = FlexAlign.Start</c>, which OPTS OUT of the header row's <c>AlignItems = Center</c> and pinned the
    /// arrow to the TOP of the 64-DIP header while the two-line title lane stayed centred — reading as an arrow stacked
    /// above the title. This plain box has no AlignSelf of its own, so the header centres IT, and inside it the
    /// tooltip's Start is a no-op because the box hugs the button's own height.</remarks>
    Element BackButton() => new BoxEl
    {
        Shrink = 0f,
        Children =
        [
            ToolTip.Wrap(
                IconButton.Create(Icons.Back, GoBack, size: ControlSize.Small) with { Shrink = 0f },
                Loc.Get(CzLoc.Back)),
        ],
    };

    /// <summary>The ONE "leave the customizer" path, shared by Back and <see cref="Done"/> (defect 12). It flushes the
    /// coalesced document write, then returns the user WHERE THEY CAME FROM through <c>HistoryStore.BackCtx</c> — the
    /// shell's real browser-style Back. Home is the last resort for a standalone/headless mount with no shell above it,
    /// never the first choice.</summary>
    void GoBack()
    {
        Prefs?.Flush();
        if (Back is { } back)
        {
            back();
            return;
        }
        if (History?.Entries is { Count: > 0 } log)
        {
            for (int i = log.Count - 1; i >= 0; i--)
            {
                var route = log[i].Route;
                if (string.Equals(route.Name, SidebarLayoutMenu.CustomizeRoute, StringComparison.Ordinal)) continue;
                Go?.Invoke(route.Name, route.Arg);
                return;
            }
        }
        Go?.Invoke("home", null);
    }

    /// <summary>DEFECT 12 — Done used to hard-navigate <c>Go("home")</c> under a comment claiming "a page has no
    /// reachable seam onto the shell's back stack", which the sibling Back button disproved on the very next screen:
    /// <c>HistoryStore.BackCtx</c> carries <c>WaveeShell.Back</c> and has since the customizer shipped. Done and Back
    /// are the same gesture with different words on it, so they are now literally the same method — two exits that can
    /// never disagree about where "out" is.</summary>
    void Done() => GoBack();

    /// <summary>The SAVED-LOCALLY indicator: a 6-DIP success dot plus the label, shown only while
    /// <c>PersistenceHealth</c> is healthy. It deliberately says nothing on a fault — the error <c>InfoBar</c> in
    /// <see cref="Banners"/> owns failures, and two voices telling the same story is how a UI starts lying.</summary>
    static Element SavedIndicator() => new BoxEl
    {
        Direction = 0, Shrink = 0f, Gap = Spacing.XS, AlignItems = FlexAlign.Center,
        Margin = new Edges4(0f, 0f, Spacing.XS, 0f),
        Children =
        [
            new BoxEl
            {
                Width = 6f, Height = 6f, Shrink = 0f, Corners = Radii.Circle(6f),
                Fill = Tok.SystemFillSuccess, HitTestVisible = false,
            },
            new TextEl(Loc.Get(CzLoc.SavedLocally))
            {
                Size = 11f, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            },
        ],
    };

    // ── banners: corruption, save fault, inline rejects ──────────────────────────────────────────────────────────────

    Element Banners()
    {
        var kids = new List<Element>(3);
        var prefs = Prefs;
        var persistence = prefs?.PersistenceHealth.Value ?? SidebarWriteResult.Healthy;
        if (persistence.Success) _dismissedPersistenceFault = SidebarPersistenceFault.None;

        if (prefs is { Fault: not SidebarLoadFault.None } && !_corruptDismissed)
        {
            string path = prefs.FaultDetail ?? "";
            kids.Add(InfoBar.Create(
                InfoBarSeverity.Warning,
                Loc.Get(CzLoc.Corrupt),
                Loc.Format(CzLoc.CorruptSub, ("path", path)),
                onClose: () => { _corruptDismissed = true; BumpBanner(); },
                actionButton: new BoxEl
                {
                    Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        Button.Create(Loc.Get(CzLoc.CopyPath), () => CopyPath(path), ButtonAppearance.Subtle,
                            ControlSize.Small),
                        Button.Create(Loc.Get(CzLoc.FaultDiscard), DiscardCorrupt, ButtonAppearance.Standard,
                            ControlSize.Small),
                    ],
                }));
        }

        // The BYTE-BUDGET fault: Commit silently no-ops while over budget, so the customizer is the one place that can
        // tell the user their edits are not reaching disk — and which section to shrink.
        bool saveFault = !persistence.Success
            && persistence.Fault is SidebarPersistenceFault.ConfigTooLarge
                or SidebarPersistenceFault.DocumentTooLarge
                or SidebarPersistenceFault.IoFailure;
        if (saveFault && persistence.Fault != _dismissedPersistenceFault)
            kids.Add(InfoBar.Create(
                InfoBarSeverity.Error,
                Loc.Get(CzLoc.SaveFault),
                persistence.SafeDetail is { Length: > 0 } d
                    ? Loc.Get(CzLoc.SaveFaultSub) + "  (" + d + ")"
                    : Loc.Get(CzLoc.SaveFaultSub),
                onClose: () => { _dismissedPersistenceFault = persistence.Fault; BumpBanner(); }));

        if (_rejectKey is { Length: > 0 } key)
            kids.Add(InfoBar.Create(InfoBarSeverity.Informational, Loc.Get(key), "", onClose: ClearReject));

        if (kids.Count == 0) return new BoxEl { Key = "banners", Height = 0f, Shrink = 0f };
        return new BoxEl
        {
            Key = "banners", Direction = 1, Shrink = 0f, Gap = Spacing.XS,
            Padding = new Edges4(Spacing.L, Spacing.S, Spacing.L, 0f),
            Children = [.. kids],
        };
    }

    // ── the ONE column ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Presets · palette · hidden · advanced, top to bottom, in one scroller at every width.</summary>
    Element Body() => ScrollView(new BoxEl
    {
        Direction = 1, Gap = Spacing.L, MaxWidth = ColumnMaxWidth, MinWidth = 0f,
        Padding = new Edges4(Spacing.L, Spacing.M, Spacing.L, Spacing.XL),
        Children =
        [
            Embed.Comp(() => new SidebarPresetBlock(this)),
            Embed.Comp(() => new SidebarCustomizerPalette(this)),
            Embed.Comp(() => new SidebarHiddenSections(this)),
            Embed.Comp(() => new SidebarAdvancedBlock(this)),
        ],
    }) with
    {
        Key = "customizer-column", Grow = 1f, Shrink = 1f, MinHeight = 0f, MinWidth = 0f,
        AutoEdgeFade = true, ScrollKey = "customizer.column",
    };

    // ── the editor's ONE mutation path ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Dispatch through <c>SidebarPreferences</c> (reducer → undo → autosave) and surface the rejection inline.
    /// The only mutation path in the whole page, so a rejected command can never look like an applied one.</summary>
    internal SidebarRejectReason Dispatch(SidebarCommand command) => Dispatch(command, topBar: false);

    /// <summary>The same mutation path with the shortcut band's rejection vocabulary enabled.</summary>
    internal SidebarRejectReason DispatchTopBar(SidebarCommand command) => Dispatch(command, topBar: true);

    SidebarRejectReason Dispatch(SidebarCommand command, bool topBar)
    {
        if (Prefs is not { } prefs) return SidebarRejectReason.None;
        var reason = prefs.Dispatch(command);
        if (reason == SidebarRejectReason.None)
        {
            if (_rejectKey is not null) ClearReject();
            return reason;
        }
        string? key = RejectLocKey(reason, topBar);
        if (key is null) return reason;
        _rejectKey = key;
        RejectEpoch.Value = RejectEpoch.Peek() + 1;
        return reason;
    }

    /// <summary>DEFECT 11 — every rejection the reducer can return now says something.
    ///
    /// <para>This table used to cover five reasons and let the other eight fall through to <c>null</c>, i.e. silence: the
    /// 40-section cap, a duplicate item, a non-whitelisted icon, an unknown id and a no-op edit all produced a click that
    /// did nothing and explained nothing. The old comment justified it as "surfacing [key] would be worse than staying
    /// quiet", which was true of a catalog that lacked the strings — so the strings landed instead.</para>
    ///
    /// <para>The <c>topBar</c> arms come FIRST because the shortcut band has its own vocabulary for the same reasons
    /// ("Shortcuts holds up to 6 items", not "your sidebar is full"). Everything else falls through to the general arm,
    /// including Phase 1's <c>KindNotDuplicable</c> — a duplicate refused because the section reads a SHARED store
    /// (Pinned / PlaylistTree), where two copies would render the same rows and both commit reorders into it.</para>
    ///
    /// <para>Still deliberately null: nothing. Every enum member is covered; the <c>_</c> arm exists only for a future
    /// build's appended reason, which stays quiet rather than rendering a missing key.</para></summary>
    static string? RejectLocKey(SidebarRejectReason reason, bool topBar) => reason switch
    {
        SidebarRejectReason.SectionCapReached when topBar => CzLoc.TopBarCapReached,
        SidebarRejectReason.DuplicateItem when topBar => CzLoc.TopBarDuplicate,
        SidebarRejectReason.InvalidIcon when topBar => CzLoc.TopBarInvalidIcon,
        SidebarRejectReason.UnknownItem when topBar => CzLoc.TopBarUnknownItem,
        SidebarRejectReason.NoChange when topBar => CzLoc.TopBarNoChange,

        SidebarRejectReason.NestingTooDeep or SidebarRejectReason.KindNotNestable => CzLoc.RejectNesting,
        SidebarRejectReason.ConfigTooLarge => CzLoc.RejectConfigTooLarge,
        SidebarRejectReason.ExtensionRefMissing => CzLoc.RejectExtensionRefMissing,
        SidebarRejectReason.SectionCapReached => CzLoc.RejectSectionCap,
        SidebarRejectReason.DuplicateItem => CzLoc.RejectDuplicateItem,
        SidebarRejectReason.InvalidIcon => CzLoc.RejectInvalidIcon,
        SidebarRejectReason.UnknownItem => CzLoc.RejectUnknownItem,
        SidebarRejectReason.UnknownSection => CzLoc.RejectUnknownSection,
        SidebarRejectReason.UnknownTemplate => CzLoc.RejectUnknownTemplate,
        SidebarRejectReason.KindDoesNotAcceptItems => CzLoc.RejectNoItems,
        SidebarRejectReason.KindNotDuplicable => CzLoc.RejectNotDuplicable,
        SidebarRejectReason.NoChange => CzLoc.RejectNoChange,
        _ => null,
    };

    void ClearReject()
    {
        if (_rejectKey is null) return;
        _rejectKey = null;
        RejectEpoch.Value = RejectEpoch.Peek() + 1;
    }

    void BumpBanner() => _bannerEpoch.Value = _bannerEpoch.Peek() + 1;

    void UndoStep()
    {
        Prefs?.Undo();
        ClearReject();
    }

    void RedoStep()
    {
        Prefs?.Redo();
        ClearReject();
    }

    void DiscardCorrupt()
    {
        Prefs?.DiscardCorruptDocument();
        _corruptDismissed = false;
        BumpBanner();
    }

    internal void CopyPath(string path)
    {
        if (path.Length == 0) return;
        Acts?.Clipboard?.SetText(path);
    }

    // ── selection + section adds (shared with the palette) ───────────────────────────────────────────────────────────

    internal void Select(string? sectionId)
    {
        Selected.SetIfChanged(sectionId);
        SelectedItem.SetIfChanged(null);
    }

    /// <summary>Top-level section count — where the palette appends.</summary>
    internal int TopLevelCount => Prefs?.Layout.Sections.Count ?? 0;

    /// <summary>The <c>StaticLinks</c> section a destination click should APPEND to, or null for "create a sibling".
    ///
    /// <para>WHICH signal means "selected" is worth being explicit about, because there are three candidates and only
    /// two of them are honest. The canvas owns both: <c>OptionsSection</c> is the card whose options popover is open,
    /// and <c>Expanded</c> is the card whose real rows are revealed. The popover CLEARS its subject on close
    /// (<c>SidebarSectionOptionsButton</c>), so on its own it is only ever set for the seconds a popover is up — which
    /// would make the append rule feel random. <c>Expanded</c> is the durable "this is the section I am working on", and
    /// it is also the only one the user can see from the palette. So: the popover's subject when there is one, else the
    /// expanded card. The page's own <see cref="Selected"/> is deliberately NOT in the chain — nothing sets it any more,
    /// and a stale value there would silently redirect an add.</para></summary>
    internal SidebarSectionSpec? SelectedStaticLinks()
    {
        if (Prefs is not { } prefs) return null;
        var session = prefs.Edit;
        string? id = session.OptionsSection.Value ?? session.Expanded.Value;
        if (id is not { Length: > 0 }) return null;
        // The sentinel Shortcuts band IS a StaticLinks section on screen and its items ARE editable — through
        // `SidebarItemCommands`, which routes the sentinel to `AddTopBarItem`. That routing is never re-decided here.
        if (SidebarIds.IsTopBar(id)) return null;
        var spec = prefs.Layout.Find(id);
        return spec is { Kind: SidebarSectionKind.StaticLinks } ? spec : null;
    }

    /// <summary>Add a plain section of <paramref name="kind"/> at the end and select it.</summary>
    internal void AddSectionOfKind(SidebarSectionKind kind)
    {
        int at = TopLevelCount;
        if (Dispatch(new AddSection(kind, at)) != SidebarRejectReason.None) return;
        AfterAdd(at);
    }

    /// <summary>DEFECT 7 — a bare "Links" section used to be added EMPTY, planning as one generic grey hint with no
    /// add-item affordance outside the property panel; adding it twice gave two identical dead rows (the screenshots).
    /// The destination picker now opens FIRST and the section is created with that item already in it, so the gesture is
    /// still exactly one <c>AddSection</c> — one undo step — and cancelling the picker adds nothing at all rather than
    /// leaving a husk behind.</summary>
    internal void AddLinksSection()
    {
        SidebarPickers.OpenItem(this, item =>
        {
            int at = TopLevelCount;
            if (Dispatch(new AddSection(SidebarSectionKind.StaticLinks, at, Item: item))
                != SidebarRejectReason.None) return;
            AfterAdd(at);
        });
    }

    /// <summary>PHASE 3 — one app PAGE as a shortcut.
    ///
    /// <para>Two outcomes, and the rule between them is the pure <c>SidebarPalette.AppendsToSelection</c>: with a
    /// <c>StaticLinks</c> section selected on the canvas the route is APPENDED to it (through
    /// <c>SidebarItemCommands.Add</c>, the ONE Add/Move/Remove chooser — never a raw <c>AddItem</c>, so the sentinel
    /// band routes correctly if it ever becomes selectable); otherwise it becomes its own pre-seeded section. Either
    /// way it is ONE command and therefore one undo step.</para>
    ///
    /// <para>No <c>IconOverride</c> is seeded: a Route item resolves its label AND its glyph from <c>ShellNav.Dest</c>
    /// at the row site, so freezing one here would both duplicate that owner and risk an <c>InvalidIcon</c> rejection
    /// for any glyph outside <c>SidebarIconNames.Allowed</c>.</para></summary>
    internal void AddDestination(string routeKey)
    {
        if (routeKey.Length == 0) return;
        var item = new SidebarItemSpec(SidebarIds.NewItem(), SidebarItemTarget.Route, routeKey);

        if (SelectedStaticLinks() is { } into)
        {
            if (Dispatch(SidebarItemCommands.Add(into.Id, item, into.ItemList.Count))
                == SidebarRejectReason.None) AfterAdd(-1);
            return;
        }

        int at = TopLevelCount;
        if (Dispatch(new AddSection(SidebarSectionKind.StaticLinks, at, Item: item))
            != SidebarRejectReason.None) return;
        AfterAdd(at);
    }

    /// <summary>Add a contributed (Extension) section for one contribution id, seeded with its schema defaults so the
    /// feed is bounded before the options popover is touched.</summary>
    internal void AddContribution(string contributionId)
    {
        if (string.IsNullOrEmpty(contributionId)) return;
        var config = SidebarJson.EmptyObject;
        int schemaVersion = 1;
        if (Registry is { } reg && reg.TryGetSource(contributionId, out var source))
        {
            config = SidebarConfigJson.Defaults(source.ConfigSchema);
            schemaVersion = source.ConfigSchema.Version;
        }
        int at = TopLevelCount;
        var xref = new SidebarExtensionRef(SidebarContributions.WaveeExtensionId, contributionId, schemaVersion, config);
        if (Dispatch(new AddSection(SidebarSectionKind.Extension, at, Extension: xref)) != SidebarRejectReason.None)
            return;
        AfterAdd(at);
    }

    /// <summary>"Recently played": a JumpBackIn section flipped to the play log. Two commands (and therefore two undo
    /// steps) because <c>AddSection</c> seeds the kind's DEFAULT display and carries no display override — an honest
    /// deviation from "one palette click = one undo step".</summary>
    internal void AddRecentlyPlayed()
    {
        int at = TopLevelCount;
        if (Dispatch(new AddSection(SidebarSectionKind.JumpBackIn, at)) != SidebarRejectReason.None) return;
        string? id = IdAt(at);
        if (id is not null)
            Dispatch(new SetDisplayOption(id, SidebarDisplayField.RecentsSource, (int)SidebarRecentsSource.Played));
        AfterAdd(at);
    }

    /// <summary>An action shortcut: the picker first, then ONE <c>AddSection(StaticLinks, Item: the bound action)</c>, so
    /// the whole gesture is a single undo step.</summary>
    internal void AddActionShortcut()
    {
        SidebarActionPicker.Open(this, null, binding =>
        {
            int at = TopLevelCount;
            var item = new SidebarItemSpec(SidebarIds.NewItem(), SidebarItemTarget.Action,
                Key: binding.ActionKey, Action: binding);
            if (Dispatch(new AddSection(SidebarSectionKind.StaticLinks, at, Item: item)) != SidebarRejectReason.None)
                return;
            AfterAdd(at);
        });
    }

    /// <summary>Liked Songs as one pre-seeded shortcut section: one reducer command, one undo entry, no frozen label.</summary>
    internal void AddLikedSongsShortcut()
    {
        int at = TopLevelCount;
        var item = new SidebarItemSpec(SidebarIds.NewItem(), SidebarItemTarget.Route, "liked",
            IconOverride: "Heart");
        if (Dispatch(new AddSection(SidebarSectionKind.StaticLinks, at, Item: item)) != SidebarRejectReason.None)
            return;
        AfterAdd(at);
    }

    /// <summary>What every ACCEPTED add does afterwards: clear the palette query (defect 4's policy) and record the new
    /// section as this host's subject. A negative index means "the add went INTO an existing section", which has no new
    /// id to point at.
    ///
    /// <para>It deliberately does NOT expand the new card on the canvas, tempting as that is. Phase 2 split the shared
    /// session's ownership — the page owns <c>ShowContents</c>, the CANVAS owns <c>Expanded</c> and
    /// <c>OptionsSection</c> — and a page that reaches across that line to open a card is the first crack in the seam
    /// that keeps these two surfaces from becoming one tangled editor. The new section appears in the live sidebar in
    /// the same frame regardless; that IS the feedback (P1).</para></summary>
    void AfterAdd(int index)
    {
        PaletteQuery.SetIfChanged("");
        if (index < 0) return;
        if (IdAt(index) is { } id) Select(id);
    }

    string? IdAt(int index)
    {
        var sections = Prefs?.Layout.Sections;
        return sections is not null && (uint)index < (uint)sections.Count ? sections[index].Id : null;
    }

    // ── templates + reset (confirmations) ────────────────────────────────────────────────────────────────────────────

    /// <summary>Apply a template. The confirmation is SKIPPED when the current document still equals a freshly built copy
    /// of its own template (modulo ids) — there is nothing to lose. Both paths dispatch exactly one command, so both cost
    /// exactly one undo slot and the dialog can honestly say "you can undo this".</summary>
    internal void ApplyTemplate(string templateId)
    {
        if (Prefs is not { } prefs) return;
        string name = Loc.Get(SidebarTemplates.NameLocKey(templateId));
        bool pristine = SidebarLayoutCompare.EqualTemplateSectionsIgnoringIds(
            prefs.Layout, SidebarTemplates.Build(prefs.Layout.TemplateId));
        if (pristine)
        {
            Dispatch(new Wavee.Core.Sidebar.ApplyTemplate(templateId));
            Select(null);
            return;
        }
        ShowTemplateConfirmation(
            Loc.Format(CzLoc.ApplyTemplateTitle, ("template", name)),
            Loc.Get(CzLoc.ApplyTemplateBody),
            Loc.Get(CzLoc.ApplyTemplateConfirm),
            SidebarTemplates.Build(templateId),
            () => { Dispatch(new Wavee.Core.Sidebar.ApplyTemplate(templateId)); Select(null); });
    }

    internal void ConfirmReset()
    {
        if (Prefs is not { } prefs) return;
        string name = Loc.Get(SidebarTemplates.NameLocKey(prefs.Layout.TemplateId));
        var target = SidebarTemplates.Build(prefs.Layout.TemplateId);
        if (SidebarLayoutCompare.EqualTemplateSectionsIgnoringIds(prefs.Layout, target))
        {
            Dispatch(new ResetLayout());
            Select(null);
            return;
        }
        ShowTemplateConfirmation(
            Loc.Get(CzLoc.ResetTitle),
            Loc.Format(CzLoc.ResetBody, ("template", name)),
            Loc.Get(CzLoc.ResetConfirm),
            target,
            () => { Dispatch(new ResetLayout()); Select(null); });
    }

    void ShowTemplateConfirmation(string title, string body, string primary,
        SidebarCustomLayout target, Action confirm)
    {
        if (OverlaySvc is not { } overlay) { confirm(); return; }
        ContentDialog.Show(overlay, dialog =>
        {
            dialog.Title = title;
            dialog.PrimaryText = primary;
            dialog.CloseText = Loc.Get(Strings.Auth.Cancel);
            dialog.DefaultButton = ContentDialog.DefaultBtn.Primary;
            dialog.DialogWidth = SidebarPickers.DialogW;
            dialog.Content = new BoxEl
            {
                Direction = 1, Gap = Spacing.M, MinWidth = SidebarPickers.BodyW,
                Children =
                [
                    new TextEl(body)
                    {
                        Size = 13f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = 3,
                    },
                    SidebarMiniature.Template(target),
                ],
            };
            dialog.PrimaryClick = confirm;
        });
    }
}

/// <summary>
/// P7 — ONE DECISION BEATS AN EDITOR. The designs as a segmented control, then the five templates as cards.
///
/// <para>The segmented control also fixes a real dishonesty: <c>SidebarLayoutMenu.Rows</c>' "Customize sidebar" row
/// calls <c>prefs.SwitchDesign(SidebarDesign.Curated)</c> before navigating here, silently, because the customizer
/// edits the Curated document. A user who opened it from Library V3 therefore had their sidebar replaced by a page that
/// never mentioned it and offered no way back. Now the switch is VISIBLE (the control shows Custom selected) and
/// REVERSIBLE (picking another design switches back and leaves the document untouched, exactly as the menu's own radio
/// rows do — through <c>SwitchDesign</c>, never a raw <c>Design.Value</c> write, so per-mode remembered state survives).</para>
///
/// <para>The caption states the consequence rather than hiding it: these sections apply to Custom.</para>
/// </summary>
sealed class SidebarPresetBlock : Component
{
    readonly SidebarCustomizerPage _page;

    public SidebarPresetBlock(SidebarCustomizerPage page) => _page = page;

    public override Element Render()
    {
        var prefs = _page.Prefs;
        int active = prefs is null ? 0 : SidebarDesignGating.IndexOf(prefs.Design.Value);
        var index = UseSignal(active);
        // Controlled against the service, never against the click: a design switch from anywhere else (the pane's quick
        // menu, Settings) must move this control too. A LAYOUT effect, never a render-time signal write.
        UseLayoutEffect(() => index.SetIfChanged(active), DepKey.From(active));

        bool showContents = prefs?.Edit.ShowContents.Value ?? false;
        var contents = UseSignal(showContents);
        UseLayoutEffect(() => contents.SetIfChanged(showContents), DepKey.From(showContents ? 1 : 0));

        var designs = new SegmentedItem[SidebarDesignInfo.Count];
        for (int i = 0; i < designs.Length; i++)
            designs[i] = new SegmentedItem(Loc.Get(SidebarDesignGating.TitleKey(SidebarDesignInfo.FromInt(i))));

        var rows = new List<Element>(2)
        {
            // The group's eyebrow already says "Sidebar design", so the ROW's label is the consequence instead of a
            // second copy of the same noun — the one thing a user arriving from Library V3 needs told.
            CzRow.Wide(Loc.Get(CzLoc.DesignsSub), null,
                Segmented.Create(designs, index, SwitchDesign)),
            // The companion's half of Phase 2's shared session: OFF means every section is a uniform card (and section
            // drag is armed); ON reveals every visible section's body for item-level work. The canvas owns the rest.
            CzRow.Prop(Loc.Get(CzLoc.ShowContents), null,
                ToggleSwitch.Create(contents, SetShowContents)),
        };

        return new BoxEl
        {
            Direction = 1, Shrink = 0f, Gap = Spacing.M,
            Children =
            [
                CzRow.Group(CzLoc.Designs, rows),
                Embed.Comp(() => new SidebarTemplateList(_page)),
            ],
        };
    }

    void SwitchDesign(int value)
    {
        // Through the service's own seam: SwitchDesign snapshots the outgoing design's pane + view state and reseeds the
        // incoming one's before flipping (locked decision 3). A bare `Design.Value = …` would drop that silently.
        _page.Prefs?.SwitchDesign(SidebarDesignGating.FromIndex(value));
    }

    void SetShowContents(bool on) => _page.Prefs?.Edit.ShowContents.SetIfChanged(on);
}

/// <summary>
/// P2 — NOTHING VANISHES INTO AN INVISIBLE ELSEWHERE. Every hidden section in the document, with one Show action.
///
/// <para>"How do I get my toolbar back" is a top support thread in every ecosystem the research pass looked at, and the
/// canvas already answers it for a section you can see (a hidden card stays put, dimmed, with its eye-off badge). This
/// list answers it for the case the canvas cannot: a hidden section inside a collapsed group, or one the user scrolled
/// past a week ago. It walks CHILDREN as well as top-level sections for exactly that reason.</para>
///
/// <para>An UNKNOWN kind (a section a newer build wrote, preserved verbatim by <c>SidebarWireCarry</c>) is listed like
/// any other: it is in the user's document, it can be hidden, and refusing to show it here would be this build deciding
/// that a section it does not understand is not the user's. It renders with the neutral mark and a generic name rather
/// than crashing a list built over <c>Sections</c>.</para>
/// </summary>
sealed class SidebarHiddenSections : Component
{
    readonly SidebarCustomizerPage _page;

    public SidebarHiddenSections(SidebarCustomizerPage page) => _page = page;

    public override Element Render()
    {
        var prefs = _page.Prefs;
        _ = prefs?.LayoutVersion.Value ?? 0;
        _ = _page.RejectEpoch.Value;

        var rows = new List<Element>(4);
        if (prefs is not null) Append(prefs.Layout.Sections, rows);

        if (rows.Count == 0)
            rows.Add(CzRow.Prop(Loc.Get(CzLoc.HiddenNone), null, null, enabled: false));

        return CzRow.Group(CzLoc.HiddenSections, rows, caption: Loc.Get(CzLoc.HiddenSectionsSub));
    }

    void Append(IReadOnlyList<SidebarSectionSpec> sections, List<Element> into)
    {
        for (int i = 0; i < sections.Count; i++)
        {
            var s = sections[i];
            if (s.Hidden) into.Add(Row(s));
            Append(s.ChildList, into);   // depth-1 by construction, so this recurses exactly once
        }
    }

    Element Row(SidebarSectionSpec section)
    {
        string id = section.Id;
        string title = CzGlyphs.TitleOf(section);
        if (title.Length == 0) title = Loc.Get(CzLoc.UnknownSection);
        return CzRow.Prop(title, Loc.Get(CzLoc.Hidden),
            Button.Create(Loc.Get(CzLoc.Show), () => _page.Dispatch(new SetSectionHidden(id, false)),
                ButtonAppearance.Standard, ControlSize.Small),
            icon: CzGlyphs.ForKind(section.Kind));
    }
}

/// <summary>
/// Reset, and the documented power-user escape hatch: where <c>sidebar-layout.json</c> lives.
///
/// <para>Surfacing the path is the honest half of "preserve, don't destroy": the document round-trips unknown kinds and
/// members, the store keeps one rotated <c>.bak</c>, and a corrupt file is preserved rather than overwritten — none of
/// which a user can act on without knowing where the file is. It offers BOTH affordances because they fail differently:
/// Explorer can be unavailable (a locked-down box, a remote session) and the clipboard cannot.</para>
/// </summary>
sealed class SidebarAdvancedBlock : Component
{
    readonly SidebarCustomizerPage _page;

    public SidebarAdvancedBlock(SidebarCustomizerPage page) => _page = page;

    public override Element Render()
    {
        var prefs = _page.Prefs;
        _ = prefs?.LayoutVersion.Value ?? 0;
        string template = Loc.Get(SidebarTemplates.NameLocKey(prefs?.Layout.TemplateId ?? SidebarTemplates.Curated));
        // Resolved once per render, not per frame: the store's path is a fixed %LOCALAPPDATA% composition.
        string path = SidebarLayoutStore.DefaultPath();

        return CzRow.Group(CzLoc.Advanced,
        [
            CzRow.Prop(Loc.Get(CzLoc.Reset), Loc.Format(CzLoc.ResetBody, ("template", template)),
                Button.Create(Loc.Get(CzLoc.Reset), _page.ConfirmReset, ButtonAppearance.Standard,
                    ControlSize.Small)),
            CzRow.Wide(Loc.Get(CzLoc.LayoutFile), Loc.Get(CzLoc.LayoutFileSub), new BoxEl
            {
                Direction = 1, Gap = Spacing.S, Grow = 1f, Shrink = 1f, MinWidth = 0f,
                Children =
                [
                    new TextEl(path)
                    {
                        Size = 11f, Color = Tok.TextTertiary, MaxLines = 2, Wrap = TextWrap.Wrap,
                        Trim = TextTrim.CharacterEllipsis,
                    },
                    new BoxEl
                    {
                        Direction = 0, Gap = Spacing.S, Shrink = 0f,
                        Children =
                        [
                            Button.Create(Loc.Get(CzLoc.ShowFile), () => ShellOpen.RevealInExplorer(path),
                                ButtonAppearance.Standard, ControlSize.Small),
                            Button.Create(Loc.Get(CzLoc.CopyPath), () => _page.CopyPath(path),
                                ButtonAppearance.Subtle, ControlSize.Small),
                        ],
                    },
                ],
            }),
        ]);
    }
}

/// <summary>The customizer's loc KEYS as literals, in one place — the landed <c>CuratedLoc</c> precedent (a typo renders
/// loudly as <c>[key]</c> instead of silently, and this file is read by surfaces the generated <c>Strings</c> table
/// cannot reach).</summary>
static class CzLoc
{
    public const string Title = "sidebar.customizer.title";
    public const string Templates = "sidebar.customizer.templates";
    public const string AddSection = "sidebar.customizer.addSection";
    public const string Properties = "sidebar.customizer.properties";
    public const string GroupGeneral = "sidebar.customizer.group.general";
    public const string GroupContent = "sidebar.customizer.group.content";
    public const string GroupAppearance = "sidebar.customizer.group.appearance";
    public const string GroupBehavior = "sidebar.customizer.group.behavior";
    public const string NoSelection = "sidebar.customizer.noSelection";
    public const string Undo = "sidebar.customizer.undo";
    public const string Redo = "sidebar.customizer.redo";
    public const string UndoOf = "sidebar.customizer.undoOf";
    public const string RedoOf = "sidebar.customizer.redoOf";
    public const string Reset = "sidebar.customizer.reset";
    public const string Done = "sidebar.customizer.done";
    public const string RenameHint = "sidebar.customizer.renameHint";
    public const string Hidden = "sidebar.customizer.hidden";
    public const string RejectNesting = "sidebar.customizer.rejectNesting";
    public const string RejectConfigTooLarge = "sidebar.customizer.rejectConfigTooLarge";
    public const string RejectExtensionRefMissing = "sidebar.customizer.rejectExtensionRefMissing";
    public const string DuplicateSuffix = "sidebar.customizer.duplicateSuffix";
    public const string ItemLabel = "sidebar.customizer.itemLabel";
    public const string ItemLabelPlaceholder = "sidebar.customizer.itemLabelPlaceholder";
    public const string ItemIcon = "sidebar.customizer.itemIcon";
    public const string ItemAdd = "sidebar.customizer.itemAdd";
    public const string ItemRemove = "sidebar.customizer.itemRemove";
    public const string MoveUp = "sidebar.customizer.moveUp";
    public const string MoveDown = "sidebar.customizer.moveDown";
    public const string MissingEntity = "sidebar.customizer.missingEntity";
    public const string MissingEntitySub = "sidebar.customizer.missingEntitySub";
    public const string Corrupt = "sidebar.customizer.corrupt";
    public const string CorruptSub = "sidebar.customizer.corruptSub";
    public const string CopyPath = "sidebar.customizer.copyPath";
    public const string FaultDiscard = "sidebar.layoutFault.discard";
    public const string SaveFault = "sidebar.customizer.saveFault";
    public const string SaveFaultSub = "sidebar.customizer.saveFaultSub";
    public const string ApplyTemplateTitle = "sidebar.customizer.applyTemplateTitle";
    public const string ApplyTemplateBody = "sidebar.customizer.applyTemplateBody";
    public const string ApplyTemplateConfirm = "sidebar.customizer.applyTemplateConfirm";
    public const string ResetTitle = "sidebar.customizer.resetTitle";
    public const string ResetBody = "sidebar.customizer.resetBody";
    public const string ResetConfirm = "sidebar.customizer.resetConfirm";

    /// <summary>The LIVE collapse row's label ("Collapse section") — it doubles as the undo label for the same command,
    /// which is exactly right: the control and the history entry should name one action.</summary>
    public const string Collapse = "sidebar.customizer.undo.collapseSection";

    public const string Rename = "sidebar.customizer.undo.renameSection";
    public const string Duplicate = "sidebar.customizer.undo.duplicateSection";
    public const string RemoveSection = "sidebar.customizer.undo.removeSection";
    public const string Show = "sidebar.customizer.undo.showSection";
    public const string Hide = "sidebar.customizer.undo.hideSection";
    public const string ExtensionManage = "sidebar.extension.manage";
    public const string ItemCount = "sidebar.v3.itemCount";
    public const string SearchPlaceholder = "sidebar.v3.searchPlaceholder";

    public const string SavedLocally = "sidebar.customizer.savedLocally";
    public const string SectionCount = "sidebar.customizer.sectionCount";

    /// <summary>The band's own cap message. Under <c>sidebar.topbar.*</c>, not <c>sidebar.customizer.*</c>, because that
    /// is where it has always lived — a key is the persisted identity of a string, and renaming it would orphan three
    /// translations.</summary>
    public const string TopBarCapReached = "sidebar.topbar.capReached";
    public const string TopBarDuplicate = "sidebar.customizer.topBarDuplicate";
    public const string TopBarInvalidIcon = "sidebar.customizer.topBarInvalidIcon";
    public const string TopBarUnknownItem = "sidebar.customizer.topBarUnknownItem";
    public const string TopBarNoChange = "sidebar.customizer.topBarNoChange";
    public const string TopBarAddItem = "sidebar.customizer.topBarAddItem";
    public const string TopBarAddTrack = "sidebar.customizer.topBarAddTrack";

    /// <summary>The back affordance's tooltip. REUSED from the auth flow rather than invented: it is the bare word "Back"
    /// in all three locales, and a customizer-specific key would be a fourth spelling of one word.</summary>
    public const string Back = "auth.back";

    public const string PaletteSearch = "sidebar.customizer.paletteSearch";
    public const string ItemAction = "sidebar.customizer.itemAction";
    public const string ItemActionSub = "sidebar.customizer.itemActionSub";
    public const string TargetLabel = "sidebar.customizer.targetLabel";
    public const string TargetNone = "sidebar.customizer.targetNone";
    public const string TargetEntity = "sidebar.customizer.targetEntity";
    public const string TargetTrack = "sidebar.customizer.targetTrack";
    public const string TargetRoute = "sidebar.customizer.targetRoute";
    public const string TargetNowPlaying = "sidebar.section.nowPlaying";   // reused, not new

    // ── PHASE 3 ──────────────────────────────────────────────────────────────────────────────────────────────────────

    public const string Designs = "sidebar.customizer.designs";
    public const string DesignsSub = "sidebar.customizer.designsSub";
    public const string ShowContents = "sidebar.customizer.editShowContents";
    public const string HiddenSections = "sidebar.customizer.hiddenSections";
    public const string HiddenSectionsSub = "sidebar.customizer.hiddenSectionsSub";
    public const string HiddenNone = "sidebar.customizer.hiddenNone";
    public const string UnknownSection = "sidebar.customizer.unknownSection";
    public const string Advanced = "sidebar.customizer.advanced";
    public const string LayoutFile = "sidebar.customizer.layoutFile";
    public const string LayoutFileSub = "sidebar.customizer.layoutFileSub";
    public const string ShowFile = "sidebar.customizer.showFile";

    /// <summary>DEFECT 6 — the palette's own empty-search line. It used to borrow V3's
    /// <c>sidebar.v3.empty.search</c> as a HARD-CODED string, which said "No results for X" about a library the palette
    /// is not; two surfaces sharing one key also means neither can be reworded without breaking the other.</summary>
    public const string PaletteEmpty = "sidebar.customizer.paletteEmpty";

    /// <summary>The one description every Destinations row shares.</summary>
    public const string DestinationSub = SidebarPalette.DestinationSubLocKey;

    /// <summary>Shown on the Destinations header while a StaticLinks section is selected on the canvas — the palette
    /// says out loud that the next click APPENDS rather than creating a sibling.</summary>
    public const string AppendsTo = "sidebar.customizer.appendsTo";

    /// <summary>DEFECT 5 — a contribution the registry offers but no palette entry names. The row shows the
    /// contribution's own id ONCE, under this label, instead of printing the raw id twice.</summary>
    public const string ContributionUnnamed = "sidebar.customizer.contributionUnnamed";

    // DEFECT 11 — the general (non-shortcut-band) rejection vocabulary.
    public const string RejectSectionCap = "sidebar.customizer.rejectSectionCap";
    public const string RejectDuplicateItem = "sidebar.customizer.rejectDuplicateItem";
    public const string RejectInvalidIcon = "sidebar.customizer.rejectInvalidIcon";
    public const string RejectUnknownItem = "sidebar.customizer.rejectUnknownItem";
    public const string RejectUnknownSection = "sidebar.customizer.rejectUnknownSection";
    public const string RejectUnknownTemplate = "sidebar.customizer.rejectUnknownTemplate";
    public const string RejectNoItems = "sidebar.customizer.rejectNoItems";
    public const string RejectNotDuplicable = "sidebar.customizer.rejectNotDuplicable";
    public const string RejectNoChange = "sidebar.customizer.rejectNoChange";
}
