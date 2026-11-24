// ── Themes/Classic/Style/ClassicStyle.cs — the Classic style (themes plan 6.1) ─────────────────────────────────────────
//
// The style IS today's `Design.Definition` (the engine's Neutral palette with Wavee's fonts) carrying Classic's roles
// (`ClassicRoles`: type, space, motion and case from T2a, the colour roles from T2b), returned as ONE instance for every option set, so
// the definition the composition resolves is the instance the appearance seed installs and a switch back to Classic adopts by reference.
// The accent: Classic FOLLOWS THE WINDOWS ACCENT, always (it has no accent option), and it does so through its definition: with the OS
// accent at the WinUI default the definition is that one instance; with any other OS accent it is the same definition carrying that ramp
// as `Accent` (one instance per ramp), so the engine's accent is the OS accent without a `Tok.SetAccent` override.
// Its one option is the row density (M3, `ClassicDensity`), a metrics preset. It publishes Classic's metrics (T3): the row heights, control
// height, gutter and section gap Classic lays out with, and the track rows at the chosen density.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Colours;

namespace Wavee.Themes.Classic;

/// <summary>The style of Wavee Classic.</summary>
public sealed class ClassicStyle : IStyleLayer
{
    /// <summary>The style id.</summary>
    public const string LayerId = "Classic.Style";

    // Built once: Tok.Use compares definitions by value, but the composition's identity rule (an unchanged pairing installs exactly
    // the style's own instance) and ThemeHost.ApplyDefinition's reference check both want a stable instance.
    // Classic's control styles over the house table: the avatar's initials floor (ClassicControlRoles.Avatar) and the menu's choice
    // (ClassicControlRoles.Menu: the ComboBox item's pill on a radio row, the accent check on a toggle).
    private static readonly ThemeDefinition s_definition = Design.Definition with
    {
        AppRoles = ClassicRoles.Table,
        Controls = Design.Definition.Controls
            .With(ControlStyles.PersonPictureStyle, ClassicControlRoles.Avatar)
            .With(ControlStyles.MenuFlyoutStyle, ClassicControlRoles.Menu),
    };

    /// <inheritdoc />
    public string Id => LayerId;

    /// <inheritdoc />
    public ThemeOptionSchema Options { get; } = ThemeOptionSchema.Of(ClassicDensity.Option);

    // The definition for the last non-default OS accent: the OS accent changes rarely, so one entry is the whole memo.
    private static readonly object s_gate = new();
    private static AccentRamp s_lastRamp = AccentRamp.FluentDefault;
    private static ThemeDefinition s_lastDefinition = s_definition;

    /// <inheritdoc />
    public ThemeDefinition Definition(OptionValues style, in StyleInputs inputs)
    {
        AccentRamp osAccent = inputs.OsAccent;
        if (inputs.Colours.Any) return s_coloured.Get(new ColouredKey(osAccent, inputs.Colours), static k => Coloured(k));
        if (osAccent == AccentRamp.FluentDefault) return s_definition;
        lock (s_gate)
        {
            if (s_lastRamp != osAccent)
            {
                s_lastDefinition = s_definition with { Accent = osAccent };
                s_lastRamp = osAccent;
            }
            return s_lastDefinition;
        }
    }

    // ── a user's colour sets (#184) ───────────────────────────────────────────────────────────────────────────────────────────

    private readonly record struct ColouredKey(AccentRamp Os, ColourPair Colours);

    private static readonly DefinitionMemo<ColouredKey> s_coloured = new();

    // A mode with a colour set takes the engine's token set and shell palette from the ladder (`ColourTokens`), with the window on the set's well
    // (Classic's chrome ground; the content step is its ground) and NO window material (plan D5: an exact palette cannot sit on the wallpaper's
    // Mica). A mode without one keeps Classic's colours and no opinion on the material, which the engine resolves to the app's own Mica. The roles
    // take the set's rungs where Classic derives a ground (`ClassicRoles.For`); the accent follows Windows as ever unless the set owns it.
    private static ThemeDefinition Coloured(ColouredKey k)
    {
        ColourPair c = k.Colours;
        bool owns = c.OwnsAccent;
        AccentRamp themeRamp = k.Os == AccentRamp.FluentDefault ? s_definition.Accent : k.Os;
        var d = s_definition;
        return d with
        {
            Dark = c.Dark is { } ld ? ColourTokens.Apply(d.Dark, ld, owns) with { WindowBackground = ld.Well } : d.Dark,
            Light = c.Light is { } ll ? ColourTokens.Apply(d.Light, ll, owns) with { WindowBackground = ll.Well } : d.Light,
            DarkShell = c.Dark is { } sd ? ColourTokens.Shell(d.DarkShell, sd) : d.DarkShell,
            LightShell = c.Light is { } sl ? ColourTokens.Shell(d.LightShell, sl) : d.LightShell,
            Accent = ColourTokens.Ramp(c, themeRamp),
            Materials = new MaterialPolicy
            {
                Light = c.Light is null ? null : FluentGpu.Pal.WindowMaterial.None,
                Dark = c.Dark is null ? null : FluentGpu.Pal.WindowMaterial.None,
            },
            AppRoles = ClassicRoles.For(c.Light, c.Dark),
        };
    }

    /// <inheritdoc />
    public StyleMetrics Metrics(OptionValues style) => s_metrics with { TrackRows = ClassicDensity.Rows(style) };

    /// <summary>Classic's metrics. A one-line row is the nav item (44), a two-line or art row the track row (56, whose cover is a
    /// 40 thumb); the control is 32; the player bar is ONE 72-DIP row, so its seek line has no row of its own (0) and the
    /// transport row is the whole dock; the search field grows to 420; the page gutter is the definition's wide page margin and
    /// sections are 32 apart; a page column keeps 480 of content. Classic has no pivot band and no panorama title (0). A docked side
    /// pane is 380 wide from the Huge tier and 420 at 4K (the floor of Classic's two docked panes, `ClassicPanelTiers.HugeW` /
    /// `Huge4KW`); a nav pane item is the pane's 36 row (`SidebarGeometry.RowHeight`); cards sit the card grid's 12 apart.</summary>
    private static readonly StyleMetrics s_metrics = new(
        RowSingle: Design.Size.NavItemH, RowDouble: Design.Size.TrackRowH, RowWithArt: Design.Size.TrackRowH,
        ControlHeight: Design.Size.ControlH, PivotRow: 0f, SubPivotRow: 0f, SeekRow: 0f, TransportRow: Design.Size.PlayerBarH,
        SearchWidth: 420f, Gutter: Design.Definition.Spacing.PageWide, Section: ClassicRoles.Table.Space.SectionGap,
        MinPageColumn: 480f, PanoramaCompactBelow: 0f,
        DockedPane: 380f, DockedPane4K: 420f, PaneItem: 36f, CardGap: ClassicRoles.Table.Space.GridGap);
}
