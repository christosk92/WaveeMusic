// ── Theming/Contracts/DetailFrameProps.cs — the props of the detail frame's views (themes plan 6.0, T9a) ───────────────────────
//
// `Detail.FrameHost` (`Entities\Detail.UI.cs`) keeps everything that DECIDES: the page mode and both anti-flicker fail-safes, the
// Hero page-layout override, the per-scope rail cells (width, collapsed, the resist fade, the page-aware cap), the accent and the
// shell-tint / page-tone claims, the data gate, the insights toggle and its facts latch, the table's mount and the page-body drop
// target. It builds ONE `DetailFrameProps` per render and mounts `ViewKeys.DetailFrame`; the theme's view arranges the pieces
// (notice, content column, rail | grip | content row, the vertical arm's show header and the insights sheet) and mounts the rail,
// the collapsed strip and the show header through their own keys.
//
// WHY THE RECORDS COMPARE BY REFERENCE. The host builds a record on every render it makes, and the page's slot builders are PRESENCE
// equal (`Detail.FrameSlots`): a data-equal spec can carry newer builders. A view that skipped on an "equal" record would keep the old
// builders, so these records never compare equal to another instance: the view re-runs exactly when the host re-rendered, which is
// when the tree used to be rebuilt. (`DetailHeroProps` is the one with value equality: the table owns that gate.)
//
// Every member is read-only (WVTH006); the live cells travel as read-only signals, a bound `Prop`, or a `Func` the view binds.

using System.Runtime.CompilerServices;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace Wavee.Theming.Contracts;

/// <summary>The props of <see cref="ViewKeys.DetailFrame"/>: one detail page's scaffold, for the arm the host decided.</summary>
public sealed record DetailFrameProps
{
    /// <summary>The vertical (hero / stacked) arm: no rail; the table hosts the hero. False: the two-column arm.</summary>
    public required bool Vertical { get; init; }

    /// <summary>The vertical arm of a TRACK page (the hero system); false for a show's vertical arm (the fixed show header).</summary>
    public required bool VerticalTracks { get; init; }

    /// <summary>The page's ambient accent: the view provides it as <c>Design.AccentCtx</c> so the heart and every ambient consumer read it.</summary>
    public required IReadSignal<Design.PageAccent> PageAccent { get; init; }

    /// <summary>The scaffold root's bounds callback (the host measures the page: mode, rail cap, hero band).</summary>
    public required Action<RectF> OnMeasured { get; init; }

    /// <summary>The shell-tint claim leaf (a hand-over, never a clear): core's palette claim, placed first.</summary>
    public required Element Tint { get; init; }

    /// <summary>The page tone plane (the ONE ground): core's palette leaf, placed second.</summary>
    public required Element Tone { get; init; }

    /// <summary>The notice strip (about the PAGE, read before the content it qualifies; never an error page).</summary>
    public required Element Notice { get; init; }

    /// <summary>The key of the content column's box (<c>right:tracks</c> / <c>right:eps</c>), so the list reconciles in place across a collapse.</summary>
    public required string ContentKey { get; init; }

    /// <summary>The content column's body: the track table (already keyed by its route and arm) or the show's episodes slot.</summary>
    public required Element Content { get; init; }

    /// <summary>The content column's floor for the page mode (<c>Breakpoints.ContentMinWidthForMode</c>).</summary>
    public required float ContentMinWidth { get; init; }

    /// <summary>The page-body drop target (tracks only); null otherwise.</summary>
    public DropTargetSpec? Drop { get; init; }

    /// <summary>The vertical arm of a show / episode page: the fixed header above the episodes (null on a track page).</summary>
    public DetailShowHeaderProps? ShowHeader { get; init; }

    /// <summary>A TRACK page's insights sheet (null for a show / episode page): laid over the vertical arm's page, or over the two-column
    /// arm's list column (tuned Classic I1, contract 1.5). Its <c>Page</c> is set by the view.</summary>
    public DetailInsightsProps? Sheet { get; init; }

    /// <summary>The two-column arm: the rail, its grip and the collapsed strip (null on a page with no rail, <see cref="Head"/>).</summary>
    public DetailTwoColumnProps? TwoColumn { get; init; }

    /// <summary>The wide arm of a page with no rail (<c>Detail.Config.Rail</c> false, Liked Songs, tuned Classic D16): the page head (title
    /// and meta) above the content column, whose list bar carries the page's own commands. Appended (contract 1.9).</summary>
    public DetailHeadProps? Head { get; init; }

    /// <summary>The two-column arm's right ASIDE (null: none at this tier, or the page has none): set where the window tier's record docks one
    /// (<c>DetailTierLayout.AsideDocked</c>) and the page has an aside body or facts to show. What it holds left the rail, the band under the rows and the
    /// list's tail (<c>Detail.AsideDocked</c>, provided around the frame). Appended (contract 1.21).</summary>
    public DetailAsideProps? Aside { get; init; }

    /// <inheritdoc />
    public bool Equals(DetailFrameProps? other) => ReferenceEquals(this, other);

    /// <inheritdoc />
    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);
}

/// <summary>The two-column arm of <see cref="DetailFrameProps"/>: what the host decided about the rail.</summary>
public sealed record DetailTwoColumnProps
{
    /// <summary>The rail is collapsed to its identity strip (only where the grip that can undo it exists).</summary>
    public required bool Collapsed { get; init; }

    /// <summary>The grip is present (the rail is resizable at this mode).</summary>
    public required bool Resizable { get; init; }

    /// <summary>The expanded rail's width: the live, page-aware column the row lays out.</summary>
    public required float RailWidth { get; init; }

    /// <summary>The collapsed strip's width (<c>RailPolicy.CompactStripW</c>).</summary>
    public required float CompactWidth { get; init; }

    /// <summary>The grip's slot width while collapsed (<c>RailPolicy.CollapsedGripW</c>).</summary>
    public required float CollapsedGripWidth { get; init; }

    /// <summary>The engine splitter, wired by the host to the scope's cell (drag writes, release commits); null when there is no grip. The
    /// view places it in the seam layer in BOTH arms: a drag collapses and re-opens mid-gesture, and a grip that changed parent would remount.</summary>
    public Element? Grip { get; init; }

    /// <summary>The rail's resist-cue opacity (paint-bound: the cue never re-renders the rail).</summary>
    public required Prop<float> RailOpacity { get; init; }

    /// <summary>The expanded rail (null while collapsed).</summary>
    public DetailRailProps? Rail { get; init; }

    /// <summary>The collapsed strip (null while expanded).</summary>
    public DetailCompactRailProps? CompactRail { get; init; }
}

/// <summary>The right aside of a two-column page (<see cref="DetailFrameProps.Aside"/>, contract 1.21): the page's own body (the album's release, artist,
/// fans and "More by"; a playlist's recommended songs) and, on a facts page, the insights the frame's <see cref="DetailFrameProps.Sheet"/> toggle shows
/// in it instead of a sheet or a dock.</summary>
public sealed record DetailAsideProps
{
    /// <summary>The page's kind (the structure sizes the aside per kind).</summary>
    public required DetailKind Kind { get; init; }

    /// <summary>The page's aside body (<c>Detail.FrameSlots.Aside</c>), or null: the aside holds only the insights.</summary>
    public Element? Body { get; init; }

    /// <summary>The page's route key (keys the aside's scroll).</summary>
    public required string RouteKey { get; init; }

    /// <inheritdoc />
    public bool Equals(DetailAsideProps? other) => ReferenceEquals(this, other);

    /// <inheritdoc />
    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);
}

/// <summary>The head of a page with no rail (<see cref="DetailFrameProps.Head"/>): the title and the meta line over the list.</summary>
public sealed record DetailHeadProps
{
    /// <summary>The page's spec (the identity's title and meta): the newest the host holds.</summary>
    public required Detail.FrameSpec Spec { get; init; }

    /// <summary>What the list shows against what it has: the meta reads "3 of 48 songs · 11 min (filtered)" while a find or a filter
    /// narrows the list (<c>Detail.Text.FilteredMeta</c>, as the rail's count line does).</summary>
    public IReadSignal<Detail.ListCount>? Shown { get; init; }

    /// <inheritdoc />
    public bool Equals(DetailHeadProps? other) => ReferenceEquals(this, other);

    /// <inheritdoc />
    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);
}

/// <summary>The props of <see cref="ViewKeys.DetailRail"/>: the expanded rail's column behind its skeleton boundary.</summary>
public sealed record DetailRailProps
{
    /// <summary>The page's spec (identity, config, slots): the newest the host holds.</summary>
    public required Detail.FrameSpec Spec { get; init; }

    /// <summary>The page's verbs, as the host's cached trampolines (they read the newest delegate at invocation).</summary>
    public required Detail.FrameActions Actions { get; init; }

    /// <summary>The rail's column width.</summary>
    public required float RailWidth { get; init; }

    /// <summary>The title's size CAP (the window-height rung, decided by the host).</summary>
    public required float TitleSize { get; init; }

    /// <summary>The description's clamp (the window-height rung).</summary>
    public required int DescriptionLines { get; init; }

    /// <summary>The page accent thunk (reading it subscribes the caller to the frame's accent).</summary>
    public required Func<ColorF> Accent { get; init; }

    /// <summary>The play-all trampoline (the table's visible order, else the context).</summary>
    public required Action Play { get; init; }

    /// <summary>The header has not answered yet: the whole rail shimmers and cross-dissolves once when this drops.</summary>
    public required Func<bool> Pending { get; init; }

    /// <summary>What the list shows against what it has (the table publishes it): while a find or a filter narrows the list the count line
    /// reads "3 of 48 songs · 11 min (filtered)" (<c>Detail.Text.FilteredMeta</c>; tuned Classic D4). Null: the page's meta only. Appended (contract 1.5).</summary>
    public IReadSignal<Detail.ListCount>? Shown { get; init; }

    /// <inheritdoc />
    public bool Equals(DetailRailProps? other) => ReferenceEquals(this, other);

    /// <inheritdoc />
    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);
}

/// <summary>The props of <see cref="ViewKeys.DetailCompactRail"/>: the collapsed rail's identity strip.</summary>
public sealed record DetailCompactRailProps
{
    /// <summary>The page's spec (identity, slots).</summary>
    public required Detail.FrameSpec Spec { get; init; }

    /// <summary>The strip's width.</summary>
    public required float StripWidth { get; init; }

    /// <summary>The cover and the chevron both expand the rail.</summary>
    public required Action Expand { get; init; }

    /// <summary>The header has not answered yet (the strip's identity shimmers; the chevron stays real).</summary>
    public required Func<bool> Pending { get; init; }

    /// <inheritdoc />
    public bool Equals(DetailCompactRailProps? other) => ReferenceEquals(this, other);

    /// <inheritdoc />
    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);
}

/// <summary>The props of <see cref="ViewKeys.DetailShowHeader"/>: the vertical show / episode header.</summary>
public sealed record DetailShowHeaderProps
{
    /// <summary>The page's spec.</summary>
    public required Detail.FrameSpec Spec { get; init; }

    /// <summary>The page's verbs (trampolines).</summary>
    public required Detail.FrameActions Actions { get; init; }

    /// <summary>The page accent thunk.</summary>
    public required Func<ColorF> Accent { get; init; }

    /// <summary>The play-all trampoline.</summary>
    public required Action Play { get; init; }

    /// <summary>The page width estimate (NaN while unmeasured): the info column's measure derives from it.</summary>
    public required float PageWidth { get; init; }

    /// <inheritdoc />
    public bool Equals(DetailShowHeaderProps? other) => ReferenceEquals(this, other);

    /// <inheritdoc />
    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);
}

/// <summary>The props of <see cref="ViewKeys.DetailHero"/>: the vertical arm's item 0 (the expanded hero and the 56-DIP context band).
/// The spec gates on data; the table's pieces gate on geometry and on element / signal REFERENCE (chrome is not data: a rebuilt toolbar
/// re-renders the hero, never the list).</summary>
public sealed record DetailHeroProps(Detail.VerticalSpec Spec, Detail.HeroParts Parts)
{
    /// <inheritdoc />
    public bool Equals(DetailHeroProps? o)
    {
        if (ReferenceEquals(this, o)) return true;
        if (o is null || !Spec.Equals(o.Spec)) return false;
        var a = Parts;
        var b = o.Parts;
        return a.ColumnWidth == b.ColumnWidth && a.CompactLeft == b.CompactLeft && a.HeroHeight == b.HeroHeight
            && ReferenceEquals(a.Toolbar, b.Toolbar) && ReferenceEquals(a.BandActions, b.BandActions)
            && ReferenceEquals(a.SelectionBar, b.SelectionBar) && ReferenceEquals(a.SearchField, b.SearchField)
            && ReferenceEquals(a.SelectionVisible, b.SelectionVisible)
            && ReferenceEquals(a.CompactInteractive, b.CompactInteractive)
            && ReferenceEquals(a.SearchExpanded, b.SearchExpanded)
            && (a.OnHeroMeasured is null) == (b.OnHeroMeasured is null);
    }

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Spec, Parts.ColumnWidth, Parts.HeroHeight);
}

/// <summary>The style behind <see cref="StyleKeys.DetailHero"/>: the hero's LOADING band as a pure builder. The table mounts it as the
/// shimmer SOURCE of its skeleton region, and the engine's deriver cannot run a view slot (a component's render), so it is a style the
/// table reads through the ambient registry, the way it reads <see cref="TrackListStyle"/>.</summary>
public sealed class DetailHeroStyle
{
    /// <summary>The reserved band: the same parts at the same sizes as the loaded hero, from the bucketed width, unfilled. The last argument is the
    /// window tiers the calling table is drawn in (its root's facts, never the process's last window), the tiers the loaded hero reads too.</summary>
    public required Func<Detail.VerticalSpec, float, float, TierFacts, Element> Skeleton { get; init; }
}

/// <summary>The props of <see cref="ViewKeys.DetailInsights"/>: the insights sheet over a track page's list (either arm).</summary>
public sealed record DetailInsightsProps
{
    /// <summary>What the sheet is laid over (the view sets it; the host leaves it null): the vertical arm's page, or an empty pass-through
    /// layer over the two-column arm's list.</summary>
    public Element? Page { get; init; }

    /// <summary>The frame's one toggle for this mounted page (the open state and the verbs that move it).</summary>
    public required Detail.InsightsToggle Toggle { get; init; }

    /// <summary>The LATCHED facts builder (null: the pane renders nothing and the toggle can never open it).</summary>
    public Func<float, Element>? Facts { get; init; }

    /// <summary>The page's measured width (the sheet's own width derives from it).</summary>
    public required float PageWidth { get; init; }

    /// <summary>The route key (keys the sheet's body scroll).</summary>
    public required string RouteKey { get; init; }

    /// <summary>Runs when the sheet closed: the host hands focus back to the toggle.</summary>
    public Action? OnClosed { get; init; }

    /// <summary>Draw the pane DOCKED: a column of its own beside the list that the list yields its width to, instead of a sheet laid over it
    /// (the view sets it from its tier record, Classic Huge: C-Liked-Huge; contract 1.14). <see cref="Page"/> is unused then.</summary>
    public bool Docked { get; init; }

    /// <inheritdoc />
    public bool Equals(DetailInsightsProps? other) => ReferenceEquals(this, other);

    /// <inheritdoc />
    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);
}

/// <summary>Which entry point of the insights sheet a <see cref="DetailInsightsToggleProps"/> draws.</summary>
public enum DetailInsightsToggleForm : byte
{
    /// <summary>The list bar's glyph button (tuned Classic I1): the two-column arm's bar, and the vertical arm's hero bar, reachable while the
    /// hero is expanded (the band is transparent then).</summary>
    HeroButton,

    /// <summary>The pinned band's plateless word in the Find · Filter · Play cluster: reachable once the band is stuck.</summary>
    BandWord,
}

/// <summary>The props of <see cref="ViewKeys.DetailInsightsToggle"/>: one entry point to the sheet, the same toggle behind both.</summary>
/// <param name="Toggle">The frame's toggle (the open signal and the verbs).</param>
/// <param name="Form">Which entry point to draw.</param>
public sealed record DetailInsightsToggleProps(Detail.InsightsToggle Toggle, DetailInsightsToggleForm Form);
