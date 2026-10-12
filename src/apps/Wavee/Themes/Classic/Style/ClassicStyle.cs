// ── Themes/Classic/Style/ClassicStyle.cs — the Classic style (themes plan 6.1) ─────────────────────────────────────────
//
// T1: the style IS today's `Design.Definition` (the engine's Neutral palette with Wavee's fonts), returned unchanged for
// every option set and accent, so the definition the composition resolves is the instance the appearance seed already
// installed. T2a/T2b move colour, type, space and shape out of `Design` into this layer's roles and publish them as the
// definition's `AppRoles`; no option exists yet. It publishes Classic's metrics: the row heights, control height, gutter and
// section gap Classic lays out with, taken from `Design`.

using FluentGpu.Dsl;
using FluentGpu.Theming;
using Wavee.Theming;

namespace Wavee.Themes.Classic;

/// <summary>The style of Wavee Classic.</summary>
public sealed class ClassicStyle : IStyleLayer
{
    /// <summary>The style id.</summary>
    public const string LayerId = "Classic.Style";

    /// <inheritdoc />
    public string Id => LayerId;

    /// <inheritdoc />
    public ThemeOptionSchema Options => ThemeOptionSchema.Empty;

    /// <inheritdoc />
    public ThemeDefinition Definition(OptionValues style, AccentRamp osAccent) => Design.Definition;

    /// <inheritdoc />
    public StyleMetrics Metrics(OptionValues style) => s_metrics;

    /// <summary>Classic's metrics. A one-line row is the nav item (44), a two-line or art row the track row (56, whose cover is a
    /// 40 thumb); the control is 32; the player bar is ONE 72-DIP row, so its seek line has no row of its own (0) and the
    /// transport row is the whole dock; the search field grows to 420; the page gutter is the definition's wide page margin and
    /// sections are 32 apart; a page column keeps 480 of content. Classic has no pivot band and no panorama title (0).</summary>
    private static readonly StyleMetrics s_metrics = new(
        RowSingle: Design.Size.NavItemH, RowDouble: Design.Size.TrackRowH, RowWithArt: Design.Size.TrackRowH,
        ControlHeight: Design.Size.ControlH, PivotRow: 0f, SubPivotRow: 0f, SeekRow: 0f, TransportRow: Design.Size.PlayerBarH,
        SearchWidth: 420f, Gutter: Design.Definition.Spacing.PageWide, Section: Design.Size.SectionGap,
        MinPageColumn: 480f, PanoramaCompactBelow: 0f);
}
