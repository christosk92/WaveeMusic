// ── Themes/Classic/Style/ClassicStyle.cs — the Classic style (themes plan 6.1) ─────────────────────────────────────────
//
// The style IS today's `Design.Definition` (the engine's Neutral palette with Wavee's fonts) carrying Classic's roles
// (`ClassicRoles`: type, space, motion and case from T2a, the colour roles from T2b), returned as ONE instance for every option set
// and accent, so the definition the composition resolves is the instance the appearance seed installs and a switch back to Classic
// adopts by reference. Its one option is the row density (M3, `ClassicDensity`), a metrics preset. It publishes Classic's metrics (T3): the row heights, control
// height, gutter and section gap Classic lays out with, and the track rows at the chosen density.

using FluentGpu.Dsl;
using FluentGpu.Theming;
using Wavee.Theming;

namespace Wavee.Themes.Classic;

/// <summary>The style of Wavee Classic.</summary>
public sealed class ClassicStyle : IStyleLayer
{
    /// <summary>The style id.</summary>
    public const string LayerId = "Classic.Style";

    // Built once: Tok.Use compares definitions by value, but the composition's identity rule (an unchanged pairing installs exactly
    // the style's own instance) and ThemeHost.ApplyDefinition's reference check both want a stable instance.
    private static readonly ThemeDefinition s_definition = Design.Definition with { AppRoles = ClassicRoles.Table };

    /// <inheritdoc />
    public string Id => LayerId;

    /// <inheritdoc />
    public ThemeOptionSchema Options { get; } = ThemeOptionSchema.Of(ClassicDensity.Option);

    /// <inheritdoc />
    public ThemeDefinition Definition(OptionValues style, AccentRamp osAccent) => s_definition;

    /// <inheritdoc />
    public StyleMetrics Metrics(OptionValues style) => s_metrics with { TrackRows = ClassicDensity.Rows(style) };

    /// <summary>Classic's metrics. A one-line row is the nav item (44), a two-line or art row the track row (56, whose cover is a
    /// 40 thumb); the control is 32; the player bar is ONE 72-DIP row, so its seek line has no row of its own (0) and the
    /// transport row is the whole dock; the search field grows to 420; the page gutter is the definition's wide page margin and
    /// sections are 32 apart; a page column keeps 480 of content. Classic has no pivot band and no panorama title (0).</summary>
    private static readonly StyleMetrics s_metrics = new(
        RowSingle: Design.Size.NavItemH, RowDouble: Design.Size.TrackRowH, RowWithArt: Design.Size.TrackRowH,
        ControlHeight: Design.Size.ControlH, PivotRow: 0f, SubPivotRow: 0f, SeekRow: 0f, TransportRow: Design.Size.PlayerBarH,
        SearchWidth: 420f, Gutter: Design.Definition.Spacing.PageWide, Section: ClassicRoles.Table.Space.SectionGap,
        MinPageColumn: 480f, PanoramaCompactBelow: 0f);
}
