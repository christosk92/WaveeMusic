// ── Themes/WinUi/Style/WinUiStyle.cs — the WinUI style (themes plan 6.2, W1) ───────────────────────────────────────────────────────
//
// The style layer of the WinUI theme ("WinUi.Style"), composed by `WinUiTheme` with `WinUiStructure` (and by Panorama with Zune's structure). It
// publishes:
//   · the DEFINITION: Wavee's definition (the neutral palette, which is WinUI's own, and the bundled icon face) with the W-Tokens deltas (Text 3 at
//     0x8B on dark and, for AA, 0x8C on light; the Mica Alt tab-strip tones), the Segoe UI Variable Text chain as the body face, the window material the `backdrop` option
//     picks, the control table (`WinUiControls`), the accent ramp (`WinUiAccent`: the OS accent, or the theme's #0078d4 ramp) and the roles
//     (`WinUiRoles`). The engine's FluentDefault ramp, spacing (36 page inset), radii (4 / 8) and elevation are the WinUI ones and stay.
//   · the METRICS: rows 40 / 48 (compact 32 / 40), the 32 control, the 48 pivot row and the 40 sub-pivot row, the player's 20 seek row over a 64
//     transport row, the 280 search field, the 36 page inset and 24 section gap, a 660 page column, the panorama's compact title under 1000, a
//     340 docked pane, pane items 36 (compact 32) and cards 16 apart (compact 12);
//   · three OPTIONS (W-ThemeOptions; the structure owns the other three): the backdrop (TRANSACTION: the material is not a colour), the density
//     (TRANSACTION: every list re-lays out) and "use the Windows accent" (LIVE: colour only).
//
// PURE AND MEMOIZED. `Definition` is a function of (backdrop, use-windows-accent, and the OS ramp when it is followed) and returns the SAME instance
// for the same inputs, so an unchanged pick is not a change (`Tok.Use` adopts the reference) and the composition's identity rule holds. The density
// is not an input of the definition: it moves only the metrics.

using System.Collections.Immutable;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Colours;
using WindowMaterial = FluentGpu.Pal.WindowMaterial;

namespace Wavee.Themes.WinUi;

/// <summary>The window backdrops the WinUI style offers (W-ThemeOptions). The values are persisted: never renumber, append.</summary>
public enum WinUiBackdrop : byte
{
    /// <summary>Mica, the default.</summary>
    Mica = 0,
    /// <summary>Mica Alt: the stronger wallpaper tint of a tabbed window.</summary>
    MicaAlt = 1,
    /// <summary>Acrylic.</summary>
    Acrylic = 2,
    /// <summary>None: the solid base colour (#202020 / #f3f3f3).</summary>
    None = 3,
}

/// <summary>The style of the WinUI theme.</summary>
public sealed class WinUiStyle : IStyleLayer
{
    /// <summary>The style id.</summary>
    public const string LayerId = "WinUi.Style";

    /// <summary>The style layer the WinUI theme composes.</summary>
    public static WinUiStyle Create() => new();

    // ── the options (W-ThemeOptions) ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary><c>style.backdrop</c>: Mica (the default), Mica Alt, Acrylic or None. A ComboBox; the material is not a colour, so it applies on the
    /// theme transaction. Also in the title bar's window menu (right-click the empty title bar, design 5.4).</summary>
    public static SelectOption<WinUiBackdrop> BackdropOption { get; } = new(
        Id: "backdrop",
        TitleKey: Strings.Theme.Winui.Style.Backdrop,
        DescriptionKey: Strings.Theme.Winui.Style.BackdropSub,
        Group: Strings.Theme.Winui.Options.Group.Window,
        Default: WinUiBackdrop.Mica,
        Choices: ImmutableArray.Create(
            new SelectChoice<WinUiBackdrop>(WinUiBackdrop.Mica, Strings.Theme.Winui.Style.BackdropMica),
            new SelectChoice<WinUiBackdrop>(WinUiBackdrop.MicaAlt, Strings.Theme.Winui.Style.BackdropMicaAlt),
            new SelectChoice<WinUiBackdrop>(WinUiBackdrop.Acrylic, Strings.Theme.Winui.Style.BackdropAcrylic),
            new SelectChoice<WinUiBackdrop>(WinUiBackdrop.None, Strings.Theme.Winui.Style.BackdropNone)),
        Picker: PickerStyle.Combo)
    {
        Apply = ApplyMode.Transaction,
        Contexts = [SettingsSurfaces.TitleBarMenu],
        SetByColours = ColourOverride.Material,
    };

    /// <summary><c>style.rowDensity</c>: Default or Compact (<see cref="WinUiDensityOption"/>). Transaction.</summary>
    public static SelectOption<WinUiDensity> DensityOption => WinUiDensityOption.Option;

    /// <summary><c>style.useWindowsAccent</c>: the OS accent ramp (on, the default) or the theme's own #0078d4 ramp. Live (colour only).</summary>
    public static ToggleOption UseWindowsAccentOption { get; } = new(
        "useWindowsAccent", Strings.Theme.Winui.Style.UseWindowsAccent, Strings.Theme.Winui.Style.UseWindowsAccentSub,
        Strings.Theme.Winui.Options.Group.Window, Default: true)
    {
        SetByColours = ColourOverride.Accent,
    };

    // ── the definition ──────────────────────────────────────────────────────────────────────────────────────────────────────

    // Everything but the accent and the material, built once. Declared before anything that reads it (static initialisers run in textual order).
    private static readonly ThemeDefinition s_base = BaseDefinition();

    private static readonly DefinitionMemo<DefinitionKey> s_definitions = new();

    private readonly record struct DefinitionKey(WinUiBackdrop Backdrop, AccentRamp Accent, ColourPair Colours);

    /// <inheritdoc />
    public string Id => LayerId;

    /// <inheritdoc />
    public ThemeOptionSchema Options { get; } = ThemeOptionSchema.Of(BackdropOption, DensityOption, UseWindowsAccentOption);

    /// <inheritdoc />
    public ThemeDefinition Definition(OptionValues style, in StyleInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(style);
        var backdrop = style.Get(BackdropOption.Key);
        var accent = WinUiAccent.RampFor(style.Get(UseWindowsAccentOption.Key), inputs.OsAccent);
        // No colour set in either mode is the board's look: the key and the build are exactly today's.
        ColourPair colours = inputs.Colours.Any ? inputs.Colours : ColourPair.None;
        return s_definitions.Get(new DefinitionKey(backdrop, accent, colours), static key => Build(key));
    }

    // A mode with a colour set (#184) draws on NO material (plan D5: an exact palette cannot be exact over the wallpaper's Mica) and takes the
    // set's grounds: the engine's token set from the ladder (`ColourTokens.Apply`), then WinUI's own deltas over it (`Grounded`), the role table's
    // grounds. A mode without one keeps the backdrop the option picks, so a mixed pair has a material per mode and the light/dark flip takes the
    // theme transaction (`Appearance.Switch`). When the set owns the accent, the ramp carries each mode's ladder accent.
    private static ThemeDefinition Build(DefinitionKey key)
    {
        var material = MaterialOf(key.Backdrop);
        ColourPair c = key.Colours;
        if (!c.Any)
            return s_base with { Accent = key.Accent, Materials = new MaterialPolicy { Light = material, Dark = material } };

        bool owns = c.OwnsAccent;
        var wavee = Design.Definition;
        WinUiGrounds dark = WinUiGrounds.Of(c.Dark, dark: true), light = WinUiGrounds.Of(c.Light, dark: false);
        return s_base with
        {
            Dark = c.Dark is { } ld ? Grounded(ColourTokens.Apply(wavee.Dark, ld, owns), dark) : s_base.Dark,
            Light = c.Light is { } ll ? Grounded(ColourTokens.Apply(wavee.Light, ll, owns), light) : s_base.Light,
            Accent = ColourTokens.Ramp(c, key.Accent),
            Materials = new MaterialPolicy
            {
                Light = c.Light is null ? material : WindowMaterial.None,
                Dark = c.Dark is null ? material : WindowMaterial.None,
            },
            AppRoles = WinUiRoles.For(light, dark),
        };
    }

    // WinUI's deltas over a token set: the window and the page base are the base (the chrome stands on it; the content layer is a step off
    // it), the pane plate the Mica Alt tone, Text 3 WinUI's own.
    private static TokenSet Grounded(TokenSet t, WinUiGrounds g) => t with
    {
        WindowBackground = g.Base,
        FillSolidBase = g.Base,
        FillSolidBaseAlt = g.BaseAlt,
        TextTertiary = g.TextTertiary,
    };

    /// <summary>The definition at the style's defaults (Mica, the Windows accent; with no OS ramp read, the theme's blue).</summary>
    public static ThemeDefinition DefaultDefinition { get; } = new WinUiStyle().Definition(OptionValues.Empty, StyleInputs.Default);

    /// <summary>The window material of <paramref name="backdrop"/>.</summary>
    public static WindowMaterial MaterialOf(WinUiBackdrop backdrop) => backdrop switch
    {
        WinUiBackdrop.MicaAlt => WindowMaterial.MicaAlt,
        WinUiBackdrop.Acrylic => WindowMaterial.Acrylic,
        WinUiBackdrop.None => WindowMaterial.None,
        _ => WindowMaterial.Mica,
    };

    private static ThemeDefinition BaseDefinition()
    {
        var wavee = Design.Definition;
        return wavee with
        {
            Dark = Grounded(wavee.Dark, WinUiGrounds.DefaultDark),
            Light = Grounded(wavee.Light, WinUiGrounds.DefaultLight),
            Fonts = new FontSet(WinUiRoles.TextFaceChain, wavee.Fonts.Icon),
            Controls = WinUiControls.Table,
            Accent = WinUiAccent.Default,
            Materials = new MaterialPolicy { Light = WindowMaterial.Mica, Dark = WindowMaterial.Mica },
            AppRoles = WinUiRoles.Table,
        };
    }

    // ── the metrics ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public StyleMetrics Metrics(OptionValues style)
    {
        ArgumentNullException.ThrowIfNull(style);
        return MetricsFor(WinUiDensityOption.Of(style));
    }

    /// <summary>WinUI's metrics at <paramref name="density"/>: a one-line row 40 and a two-line or art row 48 (compact 32 / 40); a control 32; the
    /// pivot row 48 and the sub-pivot (SelectorBar) row 40; the player's 20 seek row over a 64 transport row; the search field 280; the 36 page
    /// inset and 24 section gap (W-Tokens' 4-px grid); a page column keeps 660; the panorama title drops to Title Large under a 1000 page; a docked
    /// pane is 340 (4K too); a pane item 36 (compact 32) and the card gap 16 (compact 12).</summary>
    public static StyleMetrics MetricsFor(WinUiDensity density) => new(
        RowSingle: WinUiDensityOption.RowSingle(density), RowDouble: WinUiDensityOption.RowDouble(density),
        RowWithArt: WinUiDensityOption.RowDouble(density), ControlHeight: 32f,
        PivotRow: 48f, SubPivotRow: 40f, SeekRow: 20f, TransportRow: 64f, SearchWidth: 280f,
        Gutter: 36f, Section: 24f, MinPageColumn: 660f, PanoramaCompactBelow: 1000f,
        DockedPane: 340f, DockedPane4K: 340f,
        PaneItem: WinUiDensityOption.PaneItem(density), CardGap: WinUiDensityOption.CardGap(density),
        TrackRows: WinUiDensityOption.Rows(density));
}
