using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Core.Sidebar;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// §C1.8.6 — an EntityList section's INLINE CONTROLS: the kind-filter chip strip plus the sort/view trigger, rendered as
// part of the section's HEADER chrome (never as its own virtualized row). Renamed from `CuratedInlineControls` by R3.0.1;
// gated by `SidebarPaneConfig.ReadOnly`, so a LOCKED document (Classic) never shows controls that would edit it.
//
// THE POINT, and why this is not Mode B's chip row: every edit here rewrites THIS SECTION'S PERSISTED SPEC through the
// command pipeline (`SetQuery` / `SetDisplayOption` → reducer → undo pre-image → autosave). So one EntityList with
// InlineControls is a self-contained, fully customizable "Your Library" component whose filter/sort/view survive a
// restart and can be undone in the customizer — unlike Mode B's own chips, which are mode-global session/preference state.
//
// DEVIATION (stated, not hidden): §C5.1 suggested reusing Mode B's `LibrarySortPanel`. That component is driven by four
// mode-global `Signal`s and its sort vocabulary is the LIBRARY PAGE's (…, ReleaseDate), not `SidebarSortMode`
// (…, CustomOrder). Bridging signals ↔ commands would either drop the undo/autosave contract or mislabel a sort, so this
// strip is its own — small, command-shaped, and using the already-shipped `sidebar.*` loc keys.

static class SidebarPaneInlineControls
{
    /// <summary>The kind-filter chips (small variant). Toggling the LAST chip off would leave a query that can match
    /// nothing, so it falls back to "everything" instead — a filter row must never be able to blank its own section.</summary>
    public static Element Chips(SidebarPane owner, SidebarSectionSpec section)
    {
        var q = section.Query ?? SidebarEntityQuery.Default;
        return new BoxEl
        {
            // R3.1.2: no horizontal inset of its own — the pane owns the 8-DIP edge, and a second one here made the chip
            // strip the fifth distinct left edge in the pane.
            Direction = 0, Wrap = true, Gap = 4f, Padding = new Edges4(0f, 0f, 0f, 2f),
            Children =
            [
                Chip(owner, section, q, SidebarEntityKinds.Playlists, SidebarPaneLoc.FilterPlaylists),
                Chip(owner, section, q, SidebarEntityKinds.Albums, SidebarPaneLoc.FilterAlbums),
                Chip(owner, section, q, SidebarEntityKinds.Artists, SidebarPaneLoc.FilterArtists),
                Chip(owner, section, q, SidebarEntityKinds.Shows, SidebarPaneLoc.FilterPodcasts),
            ],
        };
    }

    static Element Chip(SidebarPane owner, SidebarSectionSpec section, SidebarEntityQuery q,
                        SidebarEntityKinds kind, string labelKey)
    {
        // "On" means the chip's kind is the ONLY thing showing, matching Mode B's single-choice chip semantics; the query
        // model is a flag set, so this reads as "is this the whole filter".
        bool on = q.Kinds == kind;
        string id = section.Id;
        return new BoxEl
        {
            Key = labelKey,
            Height = SidebarRowGeometry.ChipHeight, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Padding = new Edges4(10f, 0f, 10f, 0f), Corners = Radii.PillAll,
            Fill = on ? Tok.AccentDefault : Tok.FillSubtleSecondary,
            Role = AutomationRole.Button, Cursor = CursorId.Hand,
            // Tapping the active chip clears the filter back to everything (Mode B's "clear filter" gesture).
            OnClick = () => owner.Dispatch(new SetQuery(id,
                q with { Kinds = on ? SidebarEntityKinds.All : kind })),
            Children =
            [
                new TextEl(Loc.Get(labelKey))
                {
                    Size = 12f, Weight = (ushort)(on ? 600 : 400),
                    Color = on ? Tok.TextOnAccentPrimary : Tok.TextSecondary, MaxLines = 1,
                },
            ],
        }.Interactive(Interaction.Subtle);
    }

    /// <summary>The header's sort/view trigger — a 24-DIP icon button, so it sits in the same trailing slot Classic's
    /// header affordances use.</summary>
    public static Element SortTrigger(SidebarPane owner, SidebarSectionSpec section)
        => Embed.Comp(() => new SidebarPaneSortButton(owner, section.Id));

    /// <summary>The flyout rows. Built at OPEN time (labels resolve then, never at render time — the culture-epoch rule)
    /// and read from the LIVE document, so a reopen after an edit shows the new state.</summary>
    public static IReadOnlyList<MenuFlyoutItem> Rows(SidebarPane owner, string sectionId)
    {
        var section = owner.SectionOf(sectionId);
        if (section is null) return Array.Empty<MenuFlyoutItem>();
        var q = section.Query ?? SidebarEntityQuery.Default;
        bool playlistsOnly = q.Kinds == SidebarEntityKinds.Playlists;

        var rows = new List<MenuFlyoutItem>(10)
        {
            Sort(owner, sectionId, q, SidebarSortMode.Recents, SidebarPaneLoc.SortRecents),
            Sort(owner, sectionId, q, SidebarSortMode.RecentlyAdded, SidebarPaneLoc.SortRecentlyAdded),
            Sort(owner, sectionId, q, SidebarSortMode.Alphabetical, SidebarPaneLoc.SortAlphabetical),
            Sort(owner, sectionId, q, SidebarSortMode.Creator, SidebarPaneLoc.SortCreator),
        };
        // CustomOrder is only meaningful for a playlists-only query (locked decision 10) — offering it elsewhere would be
        // a row that silently does nothing.
        if (playlistsOnly) rows.Add(Sort(owner, sectionId, q, SidebarSortMode.CustomOrder, SidebarPaneLoc.SortCustom));
        // "Reversed" means "not this sort's NATURAL direction": recency is naturally newest-first (Descending), collation
        // is naturally A→Z (ascending). The one place that mapping is spelled out on the render side; the comparator's own
        // direction reconciliation lives in SidebarRowPlanner.EntryOrder.
        bool recency = q.Sort is SidebarSortMode.Recents or SidebarSortMode.RecentlyAdded;
        bool reversed = recency ? !q.Descending : q.Descending;
        rows.Add(MenuFlyoutItem.Toggle(Loc.Get(SidebarPaneLoc.SortReversed), reversed,
            () => owner.Dispatch(new SetQuery(sectionId, q with { Descending = !q.Descending }))));
        rows.Add(MenuFlyoutItem.Separator);

        bool grid = section.Opts.Presentation == SidebarPresentation.Grid;
        rows.Add(MenuFlyoutItem.RadioItem(Loc.Get(SidebarPaneLoc.ViewList), !grid,
            () => owner.Dispatch(new SetDisplayOption(sectionId, SidebarDisplayField.Presentation,
                (int)SidebarPresentation.List)), Icons.ViewList));
        rows.Add(MenuFlyoutItem.RadioItem(Loc.Get(SidebarPaneLoc.ViewGrid), grid,
            () => owner.Dispatch(new SetDisplayOption(sectionId, SidebarDisplayField.Presentation,
                (int)SidebarPresentation.Grid)), Icons.ViewGrid));
        return rows;
    }

    static MenuFlyoutItem Sort(SidebarPane owner, string sectionId, SidebarEntityQuery q, SidebarSortMode mode,
                               string labelKey)
        => MenuFlyoutItem.RadioItem(Loc.Get(labelKey), q.Sort == mode,
            () => owner.Dispatch(new SetQuery(sectionId, q with { Sort = mode })));
}

/// <summary>The sort/view icon button. A Component because it needs the overlay service, an anchor node and the open
/// handle; its props are the owner plus a section ID (both mount-constant) per the props-freeze contract — the section's
/// live spec is re-read from the document at open time, never captured.</summary>
sealed class SidebarPaneSortButton : Component
{
    readonly SidebarPane _owner;
    readonly string _sectionId;

    public SidebarPaneSortButton(SidebarPane owner, string sectionId) { _owner = owner; _sectionId = sectionId; }

    public override Element Render()
    {
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var svc = UseContext(Overlay.Service);

        void Toggle()
        {
            if (svc is null) return;
            if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
            var items = SidebarPaneInlineControls.Rows(_owner, _sectionId);   // built HERE, at open time
            if (items.Count == 0) return;
            handle.Value = svc.Open(
                () => anchor.Value,
                () => MenuFlyout.Create(items, () => handle.Value?.Close()),
                FlyoutPlacement.BottomEdgeAlignedRight,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup)
                { ConstrainToRootBounds = false });
            handle.Value.ClosedAction = () => handle.Value = null;
        }

        return ToolTip.Wrap(
            new BoxEl
            {
                Width = 24f, Height = 24f, Shrink = 0f,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Corners = Radii.ControlAll,
                Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
                OnRealized = h => anchor.Value = h,
                OnClick = Toggle,
                Children = [Icon(Icons.Sort, 14f, Tok.TextSecondary)],
            }.Interactive(Interaction.Subtle),
            Loc.Get(SidebarPaneLoc.SortLabel));
    }
}

/// <summary>§C5.1's pinned search head: a library-only search box above the scroll surface, rendered only when the
/// document actually contains an EntityList section. It writes the PANE's OWN session-only signal — the planner input
/// overrides <c>Search</c> with it — so it can never disturb Mode B's mode-global search state.</summary>
sealed class SidebarPaneSearchHead : Component
{
    readonly Signal<string> _text;
    readonly Signal<float> _paneWidth;

    public SidebarPaneSearchHead(Signal<string> text, Signal<float> paneWidth)
    {
        _text = text; _paneWidth = paneWidth;
    }

    public override Element Render()
    {
        // The box tracks the pane width without this component re-rendering per drag frame (a bound width signal).
        var width = UseComputed(() => MathF.Max(120f, _paneWidth.Value - SidebarPaneMetrics.PaneInsetH));
        return new BoxEl
        {
            // This sits OUTSIDE the padded list wrapper (it is fixed chrome, never a scrolling row), so it carries the
            // pane's horizontal inset itself — the one place that duplication is correct.
            Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f,
            Padding = new Edges4(8f, 8f, 8f, 4f),
            Children =
            [
                Embed.Comp(() => new EditableText
                {
                    Text = _text,
                    Placeholder = Loc.Get(SidebarPaneLoc.SearchPlaceholder),
                    WidthSignal = width,
                    Height = 32f,
                    FontSize = 13f,
                    ShowDeleteButton = true,
                    LeftAffix = Icon(Icons.Search, 14f, Tok.TextSecondary),
                }),
            ],
        };
    }
}
