// ── Theming/Contracts/DetailFactsStyle.cs — what a structure supplies for the facts bento (the insights' body; themes line p6) ─────────
//
// The insights panel's body is the page's facts bento (`User.FactsPanel`): the time slot (this week / the years), tempo, the top artists, the
// blend, rediscover, the pills and the since-line. Its core components keep every hook, memo, summary, settle, lens verb, tooltip and the
// all-artists flyout; the PICTURES they compose are one STYLE, read through `ListStyleLookup` like the list surfaces: a section's shell and
// head, the hero figure, the figure-and-chart arrangement, the charts' styles, an artist row, a disclosure, a blend legend entry and the
// blend bar's metrics, a pill and a line of prose. Classic registers the Fluent look (`User.FactsFluentStyle`, the cards it always drew);
// Zune draws its own (`ZuneFacts.Style`); WinUI takes Classic's through the stand-in.
//
// Every member is a delegate or a getter-backed record so a style reads tokens where the picture is built (a theme flip re-reads them).
// A template draws and never names `Playback`, `Entities`, `Store` or `Shell` (WVTH002).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee.Theming.Contracts;

/// <summary>A distribution fact that did not earn a section, as a one-line lens (the years / tempo / blend pill). The core wraps the
/// picture in its tooltip; <see cref="Toggle"/> null is a plain (non-button) pill.</summary>
public readonly record struct DetailFactsPill(string Key, PathData Glyph, string Lead, string Strong, string? Trailing, bool Lit, Action? Toggle);

/// <summary>A disclosure row ("7 more", the blend's "6 more  14 %" ⇄ "Show less"): <see cref="Open"/> is a live read for the chevron.
/// <see cref="InLegend"/>: the entry closes a wrapping legend row (the blend's), not a list of rows (the artists').</summary>
public readonly record struct DetailFactsDisclosure(string Key, string Label, string? Trailing, Func<bool> Open, Action Toggle,
                                                   Action<KeyEventArgs>? KeyDown, bool InLegend);

/// <summary>The blend bar's metrics: its height, its corners and the gap between segments.</summary>
public readonly record struct DetailFactsBar(float Height, CornerRadius4 Corners, float Gap);

/// <summary>The pictures of the facts bento (<see cref="StyleKeys.DetailFacts"/>).</summary>
public sealed class DetailFactsStyle
{
    /// <summary>The gap between sections, and the stack's own padding (<c>true</c>: the vertical list footer's outer padding).</summary>
    public required Func<float> SectionGap { get; init; }
    public required Func<bool, Edges4> StackPadding { get; init; }

    /// <summary>One section: <c>(key, head, body)</c>. The core's entrance and shove ride on the returned box (its root keeps the key).</summary>
    public required Func<string, Element, Element, BoxEl> Section { get; init; }

    /// <summary>A section's horizontal inset on each side (what a measured section width loses before its content: the face pile's fit).</summary>
    public required Func<float> SectionInset { get; init; }

    /// <summary>The section head: <c>(title, trailing fact or null)</c>. It must fit any width (wrap or trim the trailing fact, never clip it).</summary>
    public required Func<string, string?, Element> Head { get; init; }

    /// <summary>The hero figure over its caption: <c>(value, caption, column width or NaN)</c>.</summary>
    public required Func<string, string, float, Element> Figure { get; init; }

    /// <summary>The figure and its chart, arranged: <c>(figure, chart)</c>.</summary>
    public required Func<Element, Element, Element> FigureRow { get; init; }

    /// <summary>The week / years strip's style (the core adds its tooltip delay).</summary>
    public required Func<SparkBars.Style> Spark { get; init; }

    /// <summary>The tempo distribution's style (the core adds its tooltip delay).</summary>
    public required Func<DensityPlot.Style> Density { get; init; }

    /// <summary>A named artist row, bare (the core wraps its tooltip): <c>(name, count, lit, click or null)</c>.</summary>
    public required Func<string, string, bool, Action?, Element> ArtistRow { get; init; }

    /// <summary>The gap between the face pile and the names, and between two names.</summary>
    public required Func<float> ArtistGap { get; init; }
    public required Func<float> ArtistRowGap { get; init; }

    /// <summary>A disclosure row.</summary>
    public required Func<DetailFactsDisclosure, Element> Disclosure { get; init; }

    /// <summary>A blend legend entry, bare (the core wraps its tooltip): <c>(swatch ink, label, share, lit, click)</c>.</summary>
    public required Func<ColorF, string, string, bool, Action, Element> LegendEntry { get; init; }

    /// <summary>The gap between legend entries, the blend bar's metrics and the rule over the opened tail.</summary>
    public required Func<float> LegendGap { get; init; }
    public required Func<DetailFactsBar> BlendBar { get; init; }
    public required Func<ColorF> Rule { get; init; }

    /// <summary>A pill, bare (the core wraps its tooltip), and the gap between pills.</summary>
    public required Func<DetailFactsPill, Element> Pill { get; init; }
    public required Func<float> PillGap { get; init; }

    /// <summary>A line of prose in the panel: <c>(text, secondary ink?, max lines)</c> (the since-line, rediscover's sentence, the tail's floor note).</summary>
    public required Func<string, bool, int, TextEl> Prose { get; init; }

    /// <summary>The since-line's inset (the last line of the stack).</summary>
    public required Func<Edges4> FootnotePadding { get; init; }

    /// <summary>A section's one command (rediscover's "Play them"): <c>(label, click)</c>.</summary>
    public required Func<string, Action, Element> Command { get; init; }
}
