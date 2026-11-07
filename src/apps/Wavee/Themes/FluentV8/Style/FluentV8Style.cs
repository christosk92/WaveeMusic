// ── Themes/FluentV8/Style/FluentV8Style.cs — the FluentV8 style: Fluent UI React v8 over the WinUI style (SPIKE) ─────────────────────────
//
// A throwaway prototype. The style DERIVES from the style layer the composition hands it (WinUI's, `FluentV8Theme.Create`): it takes that
// definition and metrics and overrides what makes Fluent UI React v8 (the Office / Microsoft 365 web look) different from WinUI 3:
//   · the v8 neutral ramp and semantic colours instead of the Windows 11 ones (`FluentV8Palette`): a white page on a neutralLighterAlt frame,
//     neutralLighter hover, neutralLight checked, neutralSecondaryAlt button borders, flat (no elevation-gradient) strokes;
//   · the themePrimary #0078d4 accent ramp (dark: #2899f5), never the OS accent;
//   · Segoe UI (not Segoe UI Variable) and the v8 type sizes (superLarge 42 for page titles);
//   · roundedCorner2 everywhere (controls, cards, covers, overlays), square seams, a straight 2-px selection bar;
//   · the MS Depth shadows (depth4 cards, depth8 menus and callouts, depth64 panels and dialogs);
//   · no window material (v8 is flat: no Mica, no acrylic);
//   · the v8 controls (`FluentV8Controls`).
// Everything else (metrics, motion, the on-media ladder, the stage) is WinUI's, unchanged.
//
// The style cannot name WinUI (WVTH001: a theme's style references only its own theme): the composition passes it in as an `IStyleLayer`.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Theming;
using Wavee.Theming;
using WindowMaterial = FluentGpu.Pal.WindowMaterial;

namespace Wavee.Themes.FluentV8;

/// <summary>The style of the FluentV8 spike theme: Fluent UI React v8 tokens over an inner style layer.</summary>
public sealed class FluentV8Style : IStyleLayer
{
    /// <summary>The style id.</summary>
    public const string LayerId = "FluentV8.Style";

    /// <summary>The v8 text face chain (FluentFonts: 'Segoe UI', 'Segoe UI Web (West European)', then the system UI faces).</summary>
    public const string FaceChain = "Segoe UI";

    private readonly IStyleLayer _inner;
    private readonly ThemeOptionSchema _options;
    private readonly object _gate = new();
    private ThemeDefinition? _memo;
    private ThemeDefinition? _memoInner;

    /// <summary>The style over <paramref name="inner"/> (the composition passes WinUI's style).</summary>
    public FluentV8Style(IStyleLayer inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        // v8 has no window material and never follows the OS accent: keep only the inner options that still mean something (density).
        var kept = new List<ThemeOption>();
        foreach (var o in inner.Options.All)
            if (o.Id is not ("backdrop" or "useWindowsAccent")) kept.Add(o);
        _options = ThemeOptionSchema.Of(kept.ToArray());
    }

    /// <inheritdoc />
    public string Id => LayerId;

    /// <inheritdoc />
    public ThemeOptionSchema Options => _options;

    /// <inheritdoc />
    public ThemeDefinition Definition(OptionValues style, AccentRamp osAccent)
    {
        var inner = _inner.Definition(style, AccentRamp.FluentDefault);
        lock (_gate)
        {
            if (_memo is not null && ReferenceEquals(_memoInner, inner)) return _memo;
            _memoInner = inner;
            return _memo = Derive(inner);
        }
    }

    /// <inheritdoc />
    public StyleMetrics Metrics(OptionValues style) => _inner.Metrics(style);

    /// <summary>The v8 accent ramp: light reads Dark1 as the accent fill (themePrimary) and Dark2 as accent text (themeDark); dark reads Light2 as
    /// the fill (#2899f5) and Light3 as accent text (themeDarker #82c7ff).</summary>
    public static AccentRamp Accent { get; } = new(
        Base: FluentV8Palette.ThemePrimary,
        Light1: FluentV8Palette.DarkThemeDarkAlt, Light2: FluentV8Palette.DarkThemePrimary, Light3: FluentV8Palette.DarkThemeDarker,
        Dark1: FluentV8Palette.ThemePrimary, Dark2: FluentV8Palette.ThemeDark, Dark3: FluentV8Palette.ThemeDarker);

    private static ShadowSpec Depth(float blur, float y, float a) => new(Blur: blur, OffsetY: y, OffsetX: 0f, Color: ColorF.FromRgba(0, 0, 0) with { A = a });

    /// <summary>MS Depth: depth4 / depth8 / depth16 / depth64 (FluentDepths.ts), the dark ramp a little denser so it reads on #1b1a19.</summary>
    public static ElevationSet Elevation { get; } = new()
    {
        Light = new ElevationRamp
        {
            Card = Depth(3.6f, 1.6f, 0.132f),       // depth4
            CardHover = Depth(7.2f, 3.2f, 0.132f),  // depth8
            Tooltip = Depth(7.2f, 3.2f, 0.132f),    // depth8 (Callout / Tooltip)
            Flyout = Depth(7.2f, 3.2f, 0.132f),     // depth8 (ContextualMenu)
            DockTop = Depth(3.6f, -1.6f, 0.108f),   // depth4, upward
            Dialog = Depth(57.6f, 25.6f, 0.22f),    // depth64 (Panel, Dialog)
        },
        Dark = new ElevationRamp
        {
            Card = Depth(3.6f, 1.6f, 0.30f),
            CardHover = Depth(7.2f, 3.2f, 0.34f),
            Tooltip = Depth(7.2f, 3.2f, 0.34f),
            Flyout = Depth(7.2f, 3.2f, 0.34f),
            DockTop = Depth(3.6f, -1.6f, 0.24f),
            Dialog = Depth(57.6f, 25.6f, 0.44f),
        },
    };

    /// <summary>roundedCorner2 for every control, card and overlay; pills and circles stay round.</summary>
    public static RadiiScale Radii { get; } = new() { Control = 2f, Card = 2f, Overlay = 2f, Pill = 16f };

    /// <summary>The v8 ramp onto the engine rungs: small 12, medium 14, large 18, xLarge 20, xxLarge 28, superLarge 42, mega 68.</summary>
    public static TypeRamp Type { get; } = new()
    {
        Caption = new(12f, 16f),
        Body = new(14f, 20f),
        BodyStrong = new(14f, 20f, 600),
        BodyLarge = new(18f, 24f),
        Subtitle = new(20f, 28f, 600),
        Title = new(28f, 36f, 600),
        TitleLarge = new(42f, 52f, 600),
        Display = new(68f, 92f, 600),
    };

    private static ThemeDefinition Derive(ThemeDefinition w)
    {
        static ColorF A(ColorF c, float a) => c with { A = a };
        var light = w.Light with
        {
            WindowBackground = FluentV8Palette.NeutralLighterAlt,
            FillSolidBase = FluentV8Palette.White,
            FillSolidBaseAlt = FluentV8Palette.NeutralLighterAlt,
            FillSolidSecondary = FluentV8Palette.NeutralLighter,
            FillSolidTertiary = FluentV8Palette.NeutralLight,
            TextPrimary = FluentV8Palette.NeutralPrimary,
            TextSecondary = FluentV8Palette.NeutralSecondary,
            TextTertiary = FluentV8Palette.NeutralSecondary,
            TextDisabled = FluentV8Palette.NeutralTertiary,
            FillControlDefault = FluentV8Palette.White,
            FillControlSecondary = FluentV8Palette.NeutralLighter,
            FillControlTertiary = FluentV8Palette.NeutralLight,
            FillControlDisabled = FluentV8Palette.NeutralLighter,
            FillControlInputActive = FluentV8Palette.White,
            FillSubtleSecondary = FluentV8Palette.NeutralLighter,
            FillSubtleTertiary = FluentV8Palette.NeutralLight,
            FillCardDefault = FluentV8Palette.White,
            FillCardSecondary = FluentV8Palette.NeutralLighterAlt,
            FillLayerDefault = FluentV8Palette.White,
            FillLayerAlt = FluentV8Palette.White,
            StrokeControlDefault = FluentV8Palette.NeutralSecondaryAlt,
            StrokeControlSecondary = FluentV8Palette.NeutralSecondaryAlt,
            StrokeControlStrongDefault = FluentV8Palette.NeutralSecondary,
            StrokeCardDefault = FluentV8Palette.NeutralLight,
            StrokeDividerDefault = FluentV8Palette.NeutralLight,
            StrokeSurfaceDefault = FluentV8Palette.NeutralLight,
            StrokeFlyoutDefault = A(FluentV8Palette.NeutralPrimary, 0f),
            FillControlStrong = FluentV8Palette.NeutralSecondaryAlt,   // not neutralTertiaryAlt: the pager dots and rails need 3 : 1 on white
            ScrollThumb = FluentV8Palette.NeutralTertiaryAlt,
            AcrylicTint = FluentV8Palette.White,
            AcrylicBase = FluentV8Palette.White,
        };
        var dark = w.Dark with
        {
            WindowBackground = FluentV8Palette.DarkNeutralLighterAlt,
            FillSolidBase = FluentV8Palette.DarkWhite,
            FillSolidBaseAlt = FluentV8Palette.DarkNeutralLighterAlt,
            FillSolidSecondary = FluentV8Palette.DarkNeutralLighter,
            FillSolidTertiary = FluentV8Palette.DarkNeutralLight,
            TextPrimary = FluentV8Palette.DarkNeutralPrimary,
            TextSecondary = FluentV8Palette.DarkNeutralSecondary,
            TextTertiary = FluentV8Palette.DarkNeutralSecondary,
            TextDisabled = FluentV8Palette.DarkNeutralTertiary,
            FillControlDefault = FluentV8Palette.DarkWhite,
            FillControlSecondary = FluentV8Palette.DarkNeutralLighter,
            FillControlTertiary = FluentV8Palette.DarkNeutralLight,
            FillControlDisabled = FluentV8Palette.DarkNeutralLighter,
            FillControlInputActive = FluentV8Palette.DarkWhite,
            FillSubtleSecondary = FluentV8Palette.DarkNeutralLighter,
            FillSubtleTertiary = FluentV8Palette.DarkNeutralLight,
            FillCardDefault = FluentV8Palette.DarkWhite,
            FillCardSecondary = FluentV8Palette.DarkNeutralLighterAlt,
            FillLayerDefault = FluentV8Palette.DarkWhite,
            FillLayerAlt = FluentV8Palette.DarkWhite,
            StrokeControlDefault = FluentV8Palette.DarkNeutralSecondaryAlt,
            StrokeControlSecondary = FluentV8Palette.DarkNeutralSecondaryAlt,
            StrokeControlStrongDefault = FluentV8Palette.DarkNeutralSecondary,
            StrokeCardDefault = FluentV8Palette.DarkNeutralLight,
            StrokeDividerDefault = FluentV8Palette.DarkNeutralLight,
            StrokeSurfaceDefault = FluentV8Palette.DarkNeutralLight,
            StrokeFlyoutDefault = A(FluentV8Palette.DarkNeutralPrimary, 0f),
            FillControlStrong = FluentV8Palette.DarkNeutralTertiary,
            ScrollThumb = FluentV8Palette.DarkNeutralTertiary,
            AcrylicTint = FluentV8Palette.DarkWhite,
            AcrylicBase = FluentV8Palette.DarkWhite,
        };
        return w with
        {
            Light = light,
            Dark = dark,
            Accent = Accent,
            Type = Type,
            Radii = Radii,
            Elevation = Elevation,
            Fonts = new FontSet(FaceChain, w.Fonts.Icon),
            Materials = new MaterialPolicy { Light = WindowMaterial.None, Dark = WindowMaterial.None },
            Controls = FluentV8Controls.Table,
            AppRoles = w.AppRoles is WaveeStyleRoles roles ? FluentV8Roles.Derive(roles) : w.AppRoles,
        };
    }
}
