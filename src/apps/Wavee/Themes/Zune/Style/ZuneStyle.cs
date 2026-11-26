// ── Themes/Zune/Style/ZuneStyle.cs — the Zune style (themes plan 6.3, Z1) ─────────────────────────────────────────────────────
//
// The style layer of the Zune theme ("Zune.Style"), composed by `ZuneTheme` with `ZuneStructure`. It publishes:
//   · the DEFINITION: Wavee's definition (the bundled icon face) re-grounded on Zune's flat opaque ladder (`ZuneTokens`, both kinds), the
//     square radii ramp (0 for controls, cards and overlays; the 16 pill for the search field; full), the page gutter 48, Zune's elevation
//     (flat cards and bar, the boards' flyout and dialog shadows), the Segoe UI Variable Text chain as the body face, NO window material
//     (`WindowMaterial.None` in both kinds: the ground is the window's own opaque background), the control table (`ZuneControls`), the accent
//     ramp (`ZuneAccent`) and the roles (`ZuneRoles`);
//   · the METRICS: rows 44 / 52 / 60 by density, the band's 52 + 32, the bar's 2 + 70, the 480 page column, 360-wide docked panes, the 220
//     search pill, the 48 gutter and 32 section gap;
//   · four OPTIONS (Z-Theme-Options): the accent swatch and "use the windows accent" (colour only, LIVE), the row size and the big light
//     numerals (geometry and type, TRANSACTION).
//
// PURE AND MEMOIZED. `Definition` is a function of (accent, use-windows-accent, big numerals, and the OS ramp when it is followed) and returns
// the SAME instance for the same inputs, so an unchanged pick is not a change (`Tok.Use` adopts the reference) and the composition's identity
// rule holds. Density is not an input of the definition: it moves only the metrics.

using System.Collections.Immutable;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Colours;
using WindowMaterial = FluentGpu.Pal.WindowMaterial;

namespace Wavee.Themes.Zune;

/// <summary>The loc keys the Zune style's options use (they belong in a <c>theme.zune.style</c> object of <c>en-US.json</c>).</summary>
public static class ZuneStyleKeys
{
    /// <summary>The colour group of the options.</summary>
    public const string GroupColour = "theme.zune.style.group.colour";
    /// <summary>"accent".</summary>
    public const string Accent = "theme.zune.style.accent";
    /// <summary>"play, the playing row, selection".</summary>
    public const string AccentSub = "theme.zune.style.accentSub";
    /// <summary>"use the windows accent".</summary>
    public const string UseWindowsAccent = "theme.zune.style.useWindowsAccent";
    /// <summary>What following the OS accent does.</summary>
    public const string UseWindowsAccentSub = "theme.zune.style.useWindowsAccentSub";
    /// <summary>"row size".</summary>
    public const string Density = "theme.zune.style.density";
    /// <summary>"every song list".</summary>
    public const string DensitySub = "theme.zune.style.densitySub";
    /// <summary>"compact 44".</summary>
    public const string DensityCompact = "theme.zune.style.densityCompact";
    /// <summary>"standard 52".</summary>
    public const string DensityStandard = "theme.zune.style.densityStandard";
    /// <summary>"comfortable 60".</summary>
    public const string DensityComfortable = "theme.zune.style.densityComfortable";
    /// <summary>"big light numbers".</summary>
    public const string BigNumerals = "theme.zune.style.bigNumerals";
    /// <summary>"albums, artists, the queue".</summary>
    public const string BigNumeralsSub = "theme.zune.style.bigNumeralsSub";
}

/// <summary>The style of the Zune theme.</summary>
public sealed class ZuneStyle : IStyleLayer
{
    /// <summary>The style id.</summary>
    public const string LayerId = "Zune.Style";

    /// <summary>The style layer the Zune theme composes.</summary>
    public static ZuneStyle Create() => new();

    // ── the options (Z-Theme-Options) ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary><c>style.accent</c>: one of five swatches, pink by default; the light ground takes each one a step darker. Live (colour only).</summary>
    public static ColorOption AccentOption { get; } = new(
        Id: "accent",
        TitleKey: ZuneStyleKeys.Accent,
        DescriptionKey: ZuneStyleKeys.AccentSub,
        Group: ZuneStyleKeys.GroupColour,
        Default: ZuneAccent.Pink.Dark,
        Swatches: ImmutableArray.Create(ZuneAccent.Pink.Dark, ZuneAccent.Orange.Dark, ZuneAccent.Blue.Dark, ZuneAccent.Green.Dark, ZuneAccent.Purple.Dark))
    {
        SetByColours = ColourOverride.Accent,
    };

    /// <summary><c>style.useWindowsAccent</c>: the OS accent ramp instead of the swatch. Live (colour only).</summary>
    public static ToggleOption UseWindowsAccentOption { get; } = new(
        "useWindowsAccent", ZuneStyleKeys.UseWindowsAccent, ZuneStyleKeys.UseWindowsAccentSub, ZuneStyleKeys.GroupColour, Default: false)
    {
        SetByColours = ColourOverride.Accent,
    };

    /// <summary><c>style.density</c>: the row size (<see cref="ZuneDensityOption"/>). Transaction.</summary>
    public static SelectOption<ZuneDensity> DensityOption => ZuneDensityOption.Option;

    /// <summary><c>style.bigNumerals</c>: the 22 / 28 light numerals, or body-size numbers when off (<see cref="TypeRoles.WithBigNumerals"/>).
    /// Transaction (a type role changes).</summary>
    public static ToggleOption BigNumeralsOption { get; } = new(
        "bigNumerals", ZuneStyleKeys.BigNumerals, ZuneStyleKeys.BigNumeralsSub, Strings.Settings.ThemeOptions.Group.TrackLists, Default: true)
    {
        Apply = ApplyMode.Transaction,
    };

    // ── the definition ──────────────────────────────────────────────────────────────────────────────────────────────────────

    // Everything but the accent and the roles, built once. Declared before anything that reads it (static initialisers run in textual order).
    private static readonly ThemeDefinition s_base = BaseDefinition();

    private static readonly DefinitionMemo<DefinitionKey> s_definitions = new();

    private readonly record struct DefinitionKey(int Accent, bool FollowsWindows, bool BigNumerals, AccentRamp Os, ColourPair Colours);

    /// <inheritdoc />
    public string Id => LayerId;

    /// <inheritdoc />
    public ThemeOptionSchema Options { get; } = ThemeOptionSchema.Of(AccentOption, UseWindowsAccentOption, DensityOption, BigNumeralsOption);

    /// <inheritdoc />
    public ThemeDefinition Definition(OptionValues style, in StyleInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(style);
        var pair = ZuneAccent.PairOf(style.Get(AccentOption.Key));
        bool followsWindows = style.Get(UseWindowsAccentOption.Key);
        bool bigNumerals = style.Get(BigNumeralsOption.Key);
        // No colour set in either mode is the board's look: the key and the build are exactly today's.
        ColourPair colours = inputs.Colours.Any ? inputs.Colours : ColourPair.None;
        var key = new DefinitionKey(Pack(pair.Dark), followsWindows, bigNumerals, followsWindows ? inputs.OsAccent : default, colours);
        return s_definitions.Get(key, k => Build(pair, k));
    }

    // A mode with a colour set takes its ladder rung for rung (`ZuneGrounds.From`): the engine's whole token set from the ladder
    // (`ColourTokens.Apply`), then Zune's own washes and hairlines over it, the shell's bands and the role table's grounds. When the set owns
    // the accent, the ramp carries each mode's ladder accent (the theme's swatch or the OS ramp on a mode without a set) and the roles read
    // it through the accent tokens, the path "use the windows accent" already takes.
    private static ThemeDefinition Build(ZuneAccentPair pair, DefinitionKey k)
    {
        AccentRamp themeRamp = k.FollowsWindows ? k.Os : ZuneAccent.RampOf(pair);
        ColourPair c = k.Colours;
        if (!c.Any)
            return s_base with { Accent = themeRamp, AppRoles = ZuneRoles.For(pair, k.FollowsWindows, k.BigNumerals) };

        bool owns = c.OwnsAccent;
        ZuneGrounds dark = ZuneGrounds.Of(c.Dark, dark: true), light = ZuneGrounds.Of(c.Light, dark: false);
        var wavee = Design.Definition;
        return s_base with
        {
            Dark = c.Dark is { } ld ? OnAccent(DarkTokens(ColourTokens.Apply(wavee.Dark, ld, owns), dark), ld, owns) : s_base.Dark,
            Light = c.Light is { } ll ? OnAccent(LightTokens(ColourTokens.Apply(wavee.Light, ll, owns), light), ll, owns) : s_base.Light,
            DarkShell = ShellOf(dark.Ground, dark.Well, dark.Ink),
            LightShell = ShellOf(light.Ground, light.Well, light.Ink),
            Accent = ColourTokens.Ramp(c, themeRamp),
            AppRoles = ZuneRoles.For(pair, k.FollowsWindows || owns, k.BigNumerals, light, dark),
        };
    }

    // Zune's board puts black on the dark accent and white on the light one; a palette's accent carries its own (`ColourLadder.OnAccent`).
    private static TokenSet OnAccent(TokenSet t, ColourLadder l, bool owns)
        => owns ? t with { TextOnAccentPrimary = l.OnAccent, TextOnAccentSecondary = l.OnAccent with { A = 0.70f } } : t;

    /// <summary>The definition at the style's defaults (pink, big numerals, the swatch rather than the OS accent).</summary>
    public static ThemeDefinition DefaultDefinition { get; } = new ZuneStyle().Definition(OptionValues.Empty, StyleInputs.Default);

    private static int Pack(ColorF c)
    {
        static int Ch(float v) => Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
        return (Ch(c.R) << 16) | (Ch(c.G) << 8) | Ch(c.B);
    }

    private static ThemeDefinition BaseDefinition()
    {
        var wavee = Design.Definition;
        return wavee with
        {
            Dark = DarkTokens(wavee.Dark, ZuneGrounds.DefaultDark),
            Light = LightTokens(wavee.Light, ZuneGrounds.DefaultLight),
            DarkShell = ShellOf(ZuneGrounds.DefaultDark.Ground, ZuneGrounds.DefaultDark.Well, ZuneGrounds.DefaultDark.Ink),
            LightShell = ShellOf(ZuneGrounds.DefaultLight.Ground, ZuneGrounds.DefaultLight.Well, ZuneGrounds.DefaultLight.Ink),
            // 0 for every rectangle, the 16 pill (the search field), full for the round metro buttons (Z-Tokens).
            Radii = new RadiiScale { None = 0f, Control = 0f, Card = 0f, Overlay = 0f, Pill = 16f, Full = ZuneControls.Round },
            // The page gutter 48 (the band inset and every title share this x), 16 in a narrow window, cards 16 apart (Z-Tokens, Z-Sizes-Breakpoints).
            Spacing = SpacingScale.FluentDefault with { PageWide = 48f, PageNarrow = 16f, Card = 16f },
            Elevation = ZuneElevation,
            Fonts = new FontSet(ZuneRoles.TextFaceChain, wavee.Fonts.Icon),
            Materials = new MaterialPolicy { Light = WindowMaterial.None, Dark = WindowMaterial.None },
            Controls = ZuneControls.Table,
            Accent = ZuneAccent.RampOf(ZuneAccent.Pink),
            AppRoles = ZuneRoles.Table,
        };
    }

    private static ColorF White(float a) => ColorF.FromRgba(0xFF, 0xFF, 0xFF) with { A = a };

    private static ColorF Black(float a) => ColorF.FromRgba(0x00, 0x00, 0x00) with { A = a };

    // The dark token set: the ground ladder, the ink, the 5 % hover and 8 % press, the 8 % hairline, no flyout ring, black on the accent.
    private static TokenSet DarkTokens(TokenSet t, ZuneGrounds g) => t with
    {
        WindowBackground = g.Ground,
        FillSolidBase = g.Ground,
        FillSolidBaseAlt = g.Well,
        FillSolidSecondary = g.Panel,
        FillSolidTertiary = g.Raised,
        FillLayerDefault = g.Panel,
        FillLayerAlt = g.Dialog,
        LayerOnMicaBaseAlt = g.Ground,
        FillSubtleSecondary = g.TokenWash with { A = ZuneTokens.HoverA },
        FillSubtleTertiary = g.TokenWash with { A = ZuneTokens.PressedA },
        StrokeDividerDefault = g.TokenWash with { A = ZuneTokens.HairlineDarkA },
        StrokeCardDefault = g.TokenWash with { A = ZuneTokens.HairlineDarkA },
        StrokeSurfaceDefault = g.TokenWash with { A = ZuneTokens.HairlineDarkA },
        StrokeFlyoutDefault = ColorF.Transparent,
        TextPrimary = g.Ink,
        TextSecondary = g.InkSecondary,
        TextTertiary = g.InkTertiary,
        TextOnAccentPrimary = Black(1f),
        TextOnAccentSecondary = Black(0.70f),
    };

    // The light token set: the same ladder on #f5f5f5, black washes, a 10 % hairline, the 6 % flyout ring Z-Light-PlayBar draws, white on the accent.
    private static TokenSet LightTokens(TokenSet t, ZuneGrounds g) => t with
    {
        WindowBackground = g.Ground,
        FillSolidBase = g.Ground,
        FillSolidBaseAlt = g.Well,
        FillSolidSecondary = g.Panel,
        FillSolidTertiary = g.Raised,
        FillLayerDefault = g.Panel,
        FillLayerAlt = g.Dialog,
        LayerOnMicaBaseAlt = g.Ground,
        FillSubtleSecondary = g.TokenWash with { A = ZuneTokens.HoverA },
        FillSubtleTertiary = g.TokenWash with { A = ZuneTokens.PressedA },
        StrokeDividerDefault = g.TokenWash with { A = ZuneTokens.HairlineLightA },
        StrokeCardDefault = g.TokenWash with { A = ZuneTokens.HairlineLightA },
        StrokeSurfaceDefault = g.TokenWash with { A = ZuneTokens.HairlineLightA },
        StrokeFlyoutDefault = g.TokenWash with { A = 0.06f },
        TextPrimary = g.Ink,
        TextSecondary = g.InkSecondary,
        TextTertiary = g.InkTertiary,
        TextOnAccentPrimary = White(1f),
        TextOnAccentSecondary = White(0.70f),
    };

    // The shell rungs are opaque: every band is the ground, the bar the well; rows have no zebra, the 5 % / 8 % ink washes.
    private static ShellPalette ShellOf(ColorF ground, ColorF well, ColorF ink)
    {
        var hover = ink with { A = ZuneTokens.HoverA };
        var pressed = ink with { A = ZuneTokens.PressedA };
        return new ShellPalette(
            Toolbar: ground, Sidebar: ground, PlayerBar: well, FileArea: ground, Content: ground, ContentAlt: well,
            RowZebra: ColorF.Transparent, RowHover: hover, RowHoverZebra: hover, RowPressed: pressed, RowPressedZebra: pressed);
    }

    private static ShadowSpec Shadow(float blur, float offsetY, byte alpha) => new(Blur: blur, OffsetY: offsetY, OffsetX: 0f, Color: Black(alpha / 255f));

    private static ShadowSpec None => new(Blur: 0f, OffsetY: 0f, OffsetX: 0f, Color: ColorF.Transparent);

    // Flat cards and a flat bar; the boards' overlay shadows: menus 0 16 40 at 50 % (Z-Overlays), dialogs 0 24 64 at 60 % (Z-Dialogs), light
    // menus 0 12 32 at 18 % (Z-Light-PlayBar). The tool tip takes the menu's band; the light dialog (no board) the light menu's at a deeper blur.
    private static ElevationSet ZuneElevation => new()
    {
        Dark = new ElevationRamp
        {
            Card = None, CardHover = None, DockTop = None,
            Tooltip = Shadow(40f, 16f, 0x80),
            Flyout = Shadow(40f, 16f, 0x80),
            Dialog = Shadow(64f, 24f, 0x99),
        },
        Light = new ElevationRamp
        {
            Card = None, CardHover = None, DockTop = None,
            Tooltip = Shadow(32f, 12f, 0x2E),
            Flyout = Shadow(32f, 12f, 0x2E),
            Dialog = Shadow(64f, 24f, 0x40),
        },
    };

    // ── the metrics ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public StyleMetrics Metrics(OptionValues style)
    {
        ArgumentNullException.ThrowIfNull(style);
        return MetricsFor(ZuneDensityOption.Of(style));
    }

    /// <summary>Zune's metrics at <paramref name="density"/>. A row (one line, two lines or with art) is the density's 44 / 52 / 60; a control
    /// 32 (chips, the pill); the band's rows 52 + 32 = 84; the bar's seek row 2 over a 70 transport row = 72; the search pill 220 (Z-Part-TitleBar's
    /// Standard; the Huge 320 and 4K 400 are the structure's tier values); the page gutter 48 and sections 32 apart; a page column keeps 480; the
    /// panorama falls back to <c>PanoramaCompact</c> under a 640 page (Z-Sizes-Breakpoints: 64 in the compact window); each docked pane is 360
    /// (Z-Part-Panel's docked2 / docked3 columns, 4K too); a pane item and the card gap follow the density; a track row's gutter never
    /// drops under 16 (Z-Album-Compact's rows, audit B8).</summary>
    public static StyleMetrics MetricsFor(ZuneDensity density)
    {
        float row = ZuneDensityOption.Row(density);
        return new StyleMetrics(
            RowSingle: row, RowDouble: row, RowWithArt: row, ControlHeight: 32f,
            PivotRow: 52f, SubPivotRow: 32f, SeekRow: 2f, TransportRow: 70f, SearchWidth: 220f,
            Gutter: 48f, Section: 32f, MinPageColumn: 480f, PanoramaCompactBelow: 640f,
            DockedPane: 360f, DockedPane4K: 360f, PaneItem: ZuneDensityOption.PaneItem(density), CardGap: ZuneDensityOption.CardGap(density),
            TrackRows: ZuneDensityOption.Rows(density), RowPadXFloor: 16f);
    }
}
