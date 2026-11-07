// ── Themes/SwiftUI/Style/SwiftUIStyle.cs — the SwiftUI spike style (SPIKE, not for release) ────────────────────────────────────────
//
// The style layer of the SwiftUI spike ("SwiftUI.Style"). It publishes:
//   · the DEFINITION: Wavee's definition with the macOS 26 grounds and label ladder (`SwiftUITokens`), the TYPE RAMP of Apple's text styles
//     (below), concentric radii, systemBlue, no window material (the macOS window is opaque; the glass is in-app, on the sidebar, the player
//     and the popups), the SwiftUI control table and roles over WinUI's (handed in by the composition);
//   · the METRICS of the base style it is handed (WinUI's, so the borrowed WinUI structure keeps its row and pane geometry), with its density
//     option (the only option the spike exposes).
//
// THE TYPE RAMP (Apple HIG "Typography": macOS built-in text styles, with the iOS Large Title for page titles; weights are the "emphasized"
// variants Apple Music uses for headings). Engine rung -> Apple style:
//   Caption     11/14 400   macOS Subheadline (11/14). Footnote and the captions (10/13) are under the inspector's 11 floor.
//   Body        13/17 400   macOS Body (13/16; one more DIP of leading for Windows' rasterizer).
//   BodyStrong  13/17 600   macOS Headline (13, Bold on macOS, Semibold on iOS: Segoe's 700 reads heavier than SF's, so 600).
//   BodyLarge   15/20 400   macOS Title 3 (15/20).
//   Subtitle    22/28 700   macOS Title 1 / iOS Title 2 (22), emphasized: the section headers ("Recently Played").
//   Title       26/32 700   macOS Large Title (26/32), emphasized.
//   TitleLarge  34/41 700   iOS Large Title (34/41), emphasized: page, album and playlist titles.
//   Display     52/60 800   not a HIG style: Apple Music's artist name over the hero art (heavy).

using FluentGpu.Dsl;
using FluentGpu.Theming;
using Wavee.Theming;
using WindowMaterial = FluentGpu.Pal.WindowMaterial;

namespace Wavee.Themes.SwiftUI;

/// <summary>The style of the SwiftUI spike.</summary>
public sealed class SwiftUIStyle : IStyleLayer
{
    /// <summary>The style id.</summary>
    public const string LayerId = "SwiftUI.Style";

    private readonly IStyleLayer _metricsBase;
    private readonly ThemeDefinition _definition;

    /// <summary>The style over <paramref name="metricsBase"/>'s metrics (exposing <paramref name="densityOption"/>, the base's option those
    /// metrics read), and over <paramref name="baseRoles"/> / <paramref name="baseControls"/> (the composition hands in WinUI's).</summary>
    public SwiftUIStyle(IStyleLayer metricsBase, ThemeOption densityOption, WaveeStyleRoles baseRoles, ControlStyleTable baseControls)
    {
        ArgumentNullException.ThrowIfNull(metricsBase);
        _metricsBase = metricsBase;
        Options = ThemeOptionSchema.Of(densityOption);
        _definition = Build(baseRoles, baseControls);
    }

    /// <inheritdoc />
    public string Id => LayerId;

    /// <inheritdoc />
    public ThemeOptionSchema Options { get; }

    /// <summary>The engine type ramp (see the file header).</summary>
    public static TypeRamp Ramp { get; } = new()
    {
        Caption = new TypeStyle(11f, 14f),
        Body = new TypeStyle(13f, 17f),
        BodyStrong = new TypeStyle(13f, 17f, 600),
        BodyLarge = new TypeStyle(15f, 20f),
        Subtitle = new TypeStyle(22f, 28f, 700),
        Title = new TypeStyle(26f, 32f, 700),
        TitleLarge = new TypeStyle(34f, 41f, 700),
        Display = new TypeStyle(52f, 60f, 800),
    };

    /// <summary>Concentric radii: controls 8, cards 12, overlays 16, capsules full.</summary>
    public static RadiiScale Shape { get; } = RadiiScale.FluentDefault with { Control = 8f, Card = 12f, Overlay = 16f, Pill = 999f };

    /// <inheritdoc />
    public ThemeDefinition Definition(OptionValues style, AccentRamp osAccent) => _definition;

    /// <inheritdoc />
    public StyleMetrics Metrics(OptionValues style) => _metricsBase.Metrics(style);

    private static ThemeDefinition Build(WaveeStyleRoles baseRoles, ControlStyleTable baseControls)
    {
        var wavee = Design.Definition;
        float labelA = SwiftUITokens.LabelA;
        var white = FluentGpu.Foundation.ColorF.FromRgba(0xFF, 0xFF, 0xFF);
        var black = FluentGpu.Foundation.ColorF.FromRgba(0x00, 0x00, 0x00);
        return wavee with
        {
            Dark = wavee.Dark with
            {
                WindowBackground = SwiftUITokens.DarkGround,
                FillSolidBase = SwiftUITokens.DarkGround,
                FillSolidBaseAlt = SwiftUITokens.DarkGrouped,
                FillSolidSecondary = SwiftUITokens.DarkRaised,
                TextPrimary = white with { A = labelA },
                TextSecondary = white with { A = SwiftUITokens.SecondaryDarkA },
                TextTertiary = white with { A = SwiftUITokens.TertiaryDarkA },
                TextDisabled = white with { A = SwiftUITokens.DisabledA },
                TextOnAccentPrimary = white,
                TextOnAccentSecondary = white with { A = 0.85f },
                StrokeDividerDefault = white with { A = SwiftUITokens.SeparatorA },
                StrokeCardDefault = white with { A = SwiftUITokens.SeparatorA },
                FillCardDefault = white with { A = 0.05f },
                FillCardSecondary = white with { A = 0.03f },
            },
            Light = wavee.Light with
            {
                WindowBackground = SwiftUITokens.LightGround,
                FillSolidBase = SwiftUITokens.LightGround,
                FillSolidBaseAlt = SwiftUITokens.LightGrouped,
                FillSolidSecondary = SwiftUITokens.LightGrouped,
                TextPrimary = black with { A = labelA },
                TextSecondary = black with { A = SwiftUITokens.SecondaryLightA },
                TextTertiary = black with { A = SwiftUITokens.TertiaryLightA },
                TextDisabled = black with { A = SwiftUITokens.DisabledA },
                TextOnAccentPrimary = white,
                TextOnAccentSecondary = white with { A = 0.85f },
                StrokeDividerDefault = black with { A = SwiftUITokens.SeparatorA },
                StrokeCardDefault = black with { A = SwiftUITokens.SeparatorA },
                FillCardDefault = black with { A = 0.03f },
                FillCardSecondary = black with { A = 0.02f },
            },
            Type = Ramp,
            Radii = Shape,
            Fonts = new FontSet(SwiftUIRoles.TextFaceChain, wavee.Fonts.Icon),
            Controls = SwiftUIControls.From(baseControls),
            Accent = SwiftUITokens.Accent,
            Materials = new MaterialPolicy { Light = WindowMaterial.None, Dark = WindowMaterial.None },
            AppRoles = SwiftUIRoles.From(baseRoles),
        };
    }
}
