// ── Themes/Zune/Structure/Views/Concert/ZuneConcertFilterBar.cs — Zune's concert filter bar (themes plan 6.3; P5 Z6b2) ───────────────────────────
//
// The pinned filter row (no board: built from Zune's parts, Q18): the "filter by" word and the events ticker over ONE horizontally scrolling row
// [where ▾] │ [when] │ [all · top 3 · +N], flat on the ground (no card, no border) and pinned under the band while the feed scrolls beneath it. It
// owns NO queries — every edit is a verb of the host's `ConcertFilterBarProps`, which writes one of the page's signals, and the host's effect turns
// the new tuple into a new subject. The bar keeps view state only: whether the genre strip is expanded and the two flyouts' overlay handles (the when
// and where flyouts are `ZuneConcertFlyouts`; "search cities" and "use my location" hand off to the host's picker through `OpenPicker`).

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scroll.Effects;
using FluentGpu.Signals;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using static FluentGpu.Dsl.Ui;
using Cells = Wavee.Themes.Zune.ZuneConcertCells;
using Rules = Wavee.Themes.Zune.ZuneConcertRules;

namespace Wavee.Themes.Zune;

/// <summary>The pinned filter row.</summary>
internal sealed class ZuneConcertFilterBar : Component
{
    /// <summary>The bar's props: the host's inputs and the tab's scroll scope (the filter row's offset key).</summary>
    internal sealed record Args(ConcertFilterBarProps Io, string ScrollScope);

    Args _a = null!;
    readonly Signal<bool> _expanded = new(false);
    readonly Action _openWhen, _openWhere, _thisWeekend, _clearGenres, _expand, _collapse, _searchCities;
    readonly Action<NodeHandle> _captureWhen;
    readonly Func<NodeHandle> _whenAnchor;
    readonly Func<Element> _whenContent, _whereContent;
    Func<ZuneConcertCountTicker>? _ticker;
    NodeHandle _whenNode;
    IOverlayService? _overlay;
    OverlayHandle? _whenHandle, _whereHandle;

    ConcertFilterBarProps Io => _a.Io;

    public ZuneConcertFilterBar()
    {
        _openWhen = OpenWhen;
        _openWhere = OpenWhere;
        _captureWhen = h => _whenNode = h;
        _whenAnchor = () => _whenNode;
        _thisWeekend = () => Io.SetWhen(new ConcertWhen(ConcertWhenKind.ThisWeekend, Loc.Get(Strings.Concerts.Filter.ThisWeekend),
            ConcertHub.PresetRange(ConcertWhenKind.ThisWeekend, DateTimeOffset.Now)));
        _clearGenres = () => { if (Io.Selected.Peek().Count > 0) Io.SetSelected(Array.Empty<string>()); };
        _expand = () => _expanded.Value = true;
        _collapse = () => _expanded.Value = false;
        _searchCities = () => { _whereHandle?.Close(); Io.OpenPicker(); };
        Action<ConcertWhen> pickWhen = w => { _whenHandle?.Close(); Io.SetWhen(w); };
        Action<int> pickRadius = km => Io.SetRadius(km);
        _whenContent = () => Embed.Comp(() => new ZuneConcertWhenFlyout(pickWhen));
        // "Use my location" hands off to the picker, whose own button runs the consent flow (0.2.9's TogglePicker).
        _whereContent = () => Embed.Comp(() => new ZuneConcertWhereFlyout(Io.Radius, pickRadius, _searchCities, _searchCities));
    }

    /// <summary>The pills' height in the tiers this bar is drawn in (the hub's record: the Compact 40 target, the tuned 32), written by render.</summary>
    float _pill = Rules.PillHeight;

    public override Element Render()
    {
        _a = UseProps<Args>();
        _overlay = UseContext(Overlay.Service);
        // The tiers of the facts this bar is drawn in (the page's ctx.Tier()), never the process's last-published window: a crossing re-renders the bar.
        var themeFacts = UseContext(ThemeFactsContext.Signal);
        _pill = ZuneConcertLayout.HubPillOf(themeFacts is null ? TierFacts.Unknown : themeFacts.Value.Tiers);
        var io = _a.Io;
        var place = io.Place.Value;
        bool? inferred = io.Inferred.Value;
        int radius = io.Radius.Value;
        var when = io.When.Value;
        var selected = io.Selected.Value;
        var all = io.Concepts.Value;
        bool expanded = _expanded.Value;
        _ticker ??= () => new ZuneConcertCountTicker();

        Element caption = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f, Gap = Spacing.S,
            Children =
            [
                R.Type.DenseMeta(Loc.Get(Strings.Concerts.Filter.FilterBy)) with
                {
                    Color = Tok.TextSecondary, Case = R.Case.For(LabelKind.Label), MaxLines = 1, Wrap = TextWrap.NoWrap, Shrink = 0f,
                },
                new BoxEl { Grow = 1f },
                Embed.Comp(io.Count, _ticker) with { Key = "concert-count" },
            ],
        };
        // the pill states an INFERRED place as "Near X (approximate)"
        Element where = Cells.WherePill(ConcertHub.WhereLabel(place, inferred, radius), _openWhere, _pill) with { OnRealized = io.CaptureWhere };
        Element row = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M,
            Children = [where, Cells.BarDivider(), WhenArea(when), Cells.BarDivider(), Genres(all, selected, expanded)],
        };
        Element scroller = ScrollView(new BoxEl { Direction = 0, Children = [row] }, horizontal: true) with
        {
            Grow = 0f, Height = Rules.BarScrollerH, AutoEdgeFade = true, AutoEdgeFadeBand = Rules.BarEdgeFadeBand, SuppressScrollBar = true,
            ScrollKey = _a.ScrollScope + "concert-hub-filter",
        };
        return new BoxEl
        {
            Key = "concert-filter-bar:row", Direction = 1, Gap = Spacing.XS, MinWidth = 0f, Padding = new Edges4(0f, Spacing.S, 0f, Spacing.S),
            Fill = Prop.Of(static () => R.Layer.Content),
            Children = [caption, scroller],
        }.Sticky(0f);
    }

    /// <summary>Rest: [📅 Dates ▾] + the "This weekend" chip. Active: the SAME "when-pill" node fused (§0 #3).</summary>
    Element WhenArea(ConcertWhen when)
    {
        Element[] kids = when.Kind == ConcertWhenKind.Any || when.Range is null
            ?
            [
                Cells.RestDatePill(_openWhen, _pill) with { OnRealized = _captureWhen },
                Cells.FilterToken(Loc.Get(Strings.Concerts.Filter.ThisWeekend), false, _thisWeekend, animate: Cells.ChipExit, size: TokenSize) with { Key = "when-chip" },
            ]
            : [Cells.FusedPill("when-pill", ZuneSegmentedPillStyle.AccentAt(_pill), when.Name, ConcertHub.WhenLabel(when.Range, CultureInfo.CurrentCulture), _openWhen) with { OnRealized = _captureWhen }];
        return new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, Children = kids };
    }

    Element Genres(IReadOnlyList<ConcertConcept> all, IReadOnlyList<string> selected, bool expanded)
    {
        var shown = ConcertHub.TopConcepts(all, selected, expanded);
        var kids = new List<Element>(shown.Count + 2) { Cells.FilterToken(Loc.Get(Strings.Concerts.AllGenres), selected.Count == 0, _clearGenres, size: TokenSize) };
        for (int i = 0; i < shown.Count; i++)
        {
            string uri = shown[i].Uri;
            bool active = false;
            for (int k = 0; k < selected.Count && !active; k++) active = string.Equals(selected[k], uri, StringComparison.Ordinal);
            kids.Add(Cells.FilterToken(ConcertHub.ConceptLabel(shown[i].Name, Loc.Get(Strings.Concerts.GenreFallback)), active,
                () => Io.SetSelected(ConcertHub.ToggleConcept(Io.Selected.Peek(), uri)), size: TokenSize));
        }
        if (all.Count > shown.Count) kids.Add(Cells.MoreToken(Strings.Concerts.Filter.MoreGenres(all.Count - shown.Count), false, _expand, _pill));
        else if (expanded && all.Count > ConcertHub.TopConceptCount) kids.Add(Cells.MoreToken(Loc.Get(Strings.Concerts.Filter.ShowLess), true, _collapse, _pill));
        return new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, Children = kids.ToArray() };
    }

    /// <summary>The genre tokens' size: Large (the stock 40) where the pills are at the Compact target.</summary>
    ControlSize TokenSize => _pill >= Rules.CompactTarget ? ControlSize.Large : ControlSize.Medium;

    void OpenWhen()
    {
        if (Controls.IsNullOverlay(_overlay)) return;
        if (_whenHandle is { IsOpen: true } open) { open.Close(); return; }
        _whenHandle = _overlay.Open(_whenAnchor, _whenContent, FlyoutPlacement.BottomEdgeAlignedLeft, Concert.FlyoutOptions);
        _whenHandle.ClosedAction = () => _whenHandle = null;
    }

    void OpenWhere()
    {
        if (Controls.IsNullOverlay(_overlay)) return;
        if (_whereHandle is { IsOpen: true } open) { open.Close(); return; }
        _whereHandle = _overlay.Open(Io.WhereAnchor, _whereContent, FlyoutPlacement.BottomEdgeAlignedLeft, Concert.FlyoutOptions);
        _whereHandle.ClosedAction = () => _whereHandle = null;
    }
}
