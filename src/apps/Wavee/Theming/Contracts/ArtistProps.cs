// ── Theming/Contracts/ArtistProps.cs — the props of the artist page view (themes plan 6.0, T10b1) ────────────────────────────
//
// The core host (`Artist.PageHost`) keeps the data and the decisions: the demands, the readiness, the section plan and the pivot's membership, the
// latched hero metrics, the watched accent, the scroll spy, the shell-tint claim and the Play / Shuffle / Radio verbs. A theme's view draws what this
// record carries. Every member is read-only (WVTH006); a writable cell travels as a `Func<Signal<T>>`, a live value as a read-only signal or a Func.
//
// The facts a view reads off the `Artist` handle (the shelves' slots, the biography, the profile links, the cities) are plain table reads that do not
// subscribe; the host wakes on every table they come from (`Artist.PageHost.Stamp`) and re-describes the page, so the view re-runs with fresh props.
// The verbs that name core state a theme may not reference (WVTH002: Shell, Playback, Entities) are the static `ArtistVerbs`.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;

namespace Wavee.Theming.Contracts;

/// <summary>One section the page renders, in the plan's order: the section and, for a pivot DESTINATION only, the callback that registers its anchor
/// node (`OnRealized`) with the host's scroll spy. A section without an anchor renders in place with no tab of its own.</summary>
public sealed record ArtistSectionSlot(ArtistSection Section, Action<NodeHandle>? Anchor);

/// <summary>One tab of the context band's pivot: its label and the click that parks its section under the band.</summary>
public sealed record ArtistPivotItem(string Label, Action OnClick);

/// <summary>The props of <see cref="ViewKeys.ArtistPage"/>. The host rebuilds it on every render it makes (the artist tables, the accent, the hero width
/// and the band's engaged edge are read there, as the page always did), so the view re-runs exactly when the page tree used to be rebuilt; it never
/// compares equal to another instance.</summary>
public sealed record ArtistPageProps
{
    /// <inheritdoc />
    public bool Equals(ArtistPageProps? other) => ReferenceEquals(this, other);

    /// <inheritdoc />
    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);

    // ── identity ──

    /// <summary>The route's key (the keys of the page, its scroller, the tint leaf, the wash and the hero's art identity derive from it).</summary>
    public required string RouteKey { get; init; }

    /// <summary>The entity scope epoch as text: the page body is a keyed child, so a scope switch remounts the sections and their anchors register again.</summary>
    public required string ScopeKey { get; init; }

    /// <summary>The artist the page shows (default for an invalid route).</summary>
    public required Artist Artist { get; init; }

    /// <summary>The scroll key of the page's offset (the tab's scope plus the route key).</summary>
    public required string ScrollKey { get; init; }

    // ── state ──

    /// <summary>Pending: the overview, the measured width and the chart have not all settled, so the derived skeleton shows.</summary>
    public required Func<bool> Pending { get; init; }

    /// <summary>The page failed (the overview is unknown, nobody is asking and the chart edge failed): the error vacancy shows.</summary>
    public required Func<bool> Failed { get; init; }

    /// <summary>The error vacancy's Retry; null while the route holds no valid artist.</summary>
    public Action? Retry { get; init; }

    /// <summary>True once the page body may be composed (the host composes it only then, exactly as the page always did).</summary>
    public required bool BodyReady { get; init; }

    // ── geometry ──

    /// <summary>The measured page width (the default Wide width until the first measure).</summary>
    public required float Width { get; init; }

    /// <summary>The ONE resolved (hysteretic) hero metrics of this render: the hero, the band's gutter, the magazine's gutter and the wash all read it.</summary>
    public required ArtistHeroMetrics Metrics { get; init; }

    /// <summary>The page's size observer: writes the measured width (a 0.5-DIP floor) on the host.</summary>
    public required Action<RectF> Measure { get; init; }

    // ── colour ──

    /// <summary>The watched chrome accent, read once per render.</summary>
    public required ColorF Accent { get; init; }

    /// <summary>The accent as a live read, for the leaves that bind it (the pivot, the facets, the pre-save buttons).</summary>
    public required Func<ColorF> AccentFn { get; init; }

    /// <summary>The accent as an UNTRACKED read, for the derived skeleton (it runs outside any render and must not subscribe anything).</summary>
    public required Func<ColorF> AccentPeek { get; init; }

    /// <summary>The accent as a signal, for the chart.</summary>
    public required IReadSignal<ColorF> AccentSignal { get; init; }

    /// <summary>The page accent the page PROVIDES to its subtree (`Design.AccentCtx`): the heart and every other ambient consumer read it.</summary>
    public required IReadSignal<Design.PageAccent>? PageAccent { get; init; }

    /// <summary>The header image the palette is graded from (null until the overview lands).</summary>
    public string? PaletteUrl { get; init; }

    /// <summary>The hero photo's LATCHED art identity (never re-read from the row: the latch keeps a CDN size swap from remounting the photo).</summary>
    public string? HeroUrl { get; init; }

    /// <summary>Colour washes are on (`appearance.colorWashes.enabled`): off, the blend wash renders nothing (the veil and the Play colour stay).</summary>
    public required bool Washes { get; init; }

    /// <summary>The shell-tint claim (`Palette.ShellTint`): a zero-size leaf the page keeps in its tree so the window chrome follows the page.</summary>
    public required Element Tint { get; init; }

    // ── the band and the spy ──

    /// <summary>The band is past the sentinel's sticky edge: it takes input and the hero's expanded copy stops hitting.</summary>
    public required bool Compact { get; init; }

    /// <summary>The sentinel's engaged edge (`Sticky(.., engaged:)`): the ENGINE writes it on the flip, so the view only hands it on.</summary>
    public required Func<Signal<bool>> CompactEdge { get; init; }

    /// <summary>The pivot tabs, in order (only a DESTINATION joins).</summary>
    public required IReadOnlyList<ArtistPivotItem> Pivot { get; init; }

    /// <summary>The lit tab (the spy's answer), bound by the pivot.</summary>
    public required IReadSignal<int> Active { get; init; }

    /// <summary>The page viewport's scroll handle: the spy projects its geometry off it and the band title scrolls it home.</summary>
    public required ScrollHandle Scroll { get; init; }

    /// <summary>The scroll viewport's node, reported once realized (the spy resolves against it).</summary>
    public required Action<NodeHandle> CaptureViewport { get; init; }

    /// <summary>The scroll offset, provided to the inline facet grids (`LazyScroll`) so they window against it.</summary>
    public required IReadSignal<float> ScrollY { get; init; }

    // ── the sections ──

    /// <summary>The sections in the plan's order (<see cref="ArtistSections.Plan"/>), each with its anchor when it is a pivot destination.</summary>
    public required IReadOnlyList<ArtistSectionSlot> Sections { get; init; }

    /// <summary>The "Fans also like" fallback pool (the followed artists' slots, excluding the page artist); empty when the related edge has the shelf.</summary>
    public required int[] Fans { get; init; }

    /// <summary>How many artists the "Fans also like" shelf lists (the related artists, else the fallback pool): the profile facts' Related tile.</summary>
    public required int RelatedCount { get; init; }

    // ── the verbs ──

    /// <summary>Play (or toggle) the artist context.</summary>
    public required Action Play { get; init; }

    /// <summary>Shuffle on, then the artist context.</summary>
    public required Action Shuffle { get; init; }

    /// <summary>Start a radio off the artist.</summary>
    public required Action Radio { get; init; }

    /// <summary>The band title's click: back to the top of the page.</summary>
    public required Action ScrollToTop { get; init; }

    /// <summary>Open the gallery lightbox at an index (the host holds the overlay service).</summary>
    public required Action<string[], int> OpenGallery { get; init; }

    // ── what the chart and the pick read ──

    /// <summary>The overlay service the pick card's menu opens on (null while there is none).</summary>
    public IOverlayService? Overlay { get; init; }

    /// <summary>The Top-tracks rows are the Classic grammar (`detail.rowstyle`); the page's skeleton draws the chart's own skeleton for that form. M2 deletes it.</summary>
    public required bool RowsClassic { get; init; }

    /// <summary>Track artwork is shown in the rows (`appearance.trackArtwork.hidden` off); the chart's skeleton follows it. M4 deletes it.</summary>
    public required bool ShowTrackArt { get; init; }
}
