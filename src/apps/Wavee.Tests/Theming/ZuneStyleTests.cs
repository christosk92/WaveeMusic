// ── Wavee.Tests/Theming/ZuneStyleTests.cs — the Zune style layer: definition, options, metrics, controls (themes plan 6.3, Z1) ──────
//
// What the composition reads from `ZuneStyle`: its id and options (four, with the plan's apply modes: the accent and the OS accent LIVE,
// the row size and the numerals on the TRANSACTION), a definition that is pure and memoized per input, an accent swap that is colour only
// and a numerals swap that is not, a Classic -> Zune definition swap that is never colour only, the metrics per density, the flat opaque
// ground with no window material, and the control table's look.
//
// NO TOKEN CHANGES HERE: nothing installs a definition. The control providers read the live tokens of whatever the process has (they only
// read); the assertions are on geometry, never on a live colour.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Theming;
using Wavee.Themes.Classic;
using Wavee.Themes.Zune;
using Wavee.Theming;
using Wavee.Theming.Roles;
using Xunit;
using WindowMaterial = FluentGpu.Pal.WindowMaterial;

namespace Wavee.Tests.Theming;

public sealed class ZuneStyleTests
{
    static readonly ZuneStyle Style = ZuneStyle.Create();

    static ThemeDefinition Def(OptionValues values) => Style.Definition(values, AccentRamp.FluentDefault);

    static WaveeStyleRoles Roles(ThemeDefinition d) => Assert.IsType<WaveeStyleRoles>(d.AppRoles);

    // ── identity and options ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_style_is_Zune_Style_and_Create_returns_it()
    {
        Assert.Equal("Zune.Style", ZuneStyle.LayerId);
        Assert.Equal(ZuneStyle.LayerId, ZuneStyle.Create().Id);
        Assert.IsAssignableFrom<IStyleLayer>(ZuneStyle.Create());
    }

    [Fact]
    public void The_options_are_the_accent_the_windows_accent_the_row_size_and_the_numerals_with_their_apply_modes()
    {
        var all = Style.Options.All;
        Assert.Equal(new[] { "accent", "useWindowsAccent", "density", "bigNumerals" }, all.Select(o => o.Id));
        Assert.Equal(ApplyMode.Live, Style.Options.Find("accent")!.Apply);              // colour only: the live cross-fade
        Assert.Equal(ApplyMode.Live, Style.Options.Find("useWindowsAccent")!.Apply);
        Assert.Equal(ApplyMode.Transaction, Style.Options.Find("density")!.Apply);      // every list re-lays out
        Assert.Equal(ApplyMode.Transaction, Style.Options.Find("bigNumerals")!.Apply);  // a type role changes
        Assert.Equal(ZuneAccent.Pink.Dark, ZuneStyle.AccentOption.Default);
        Assert.Equal(ZuneAccent.Swatches.Select(p => p.Dark).ToArray(), ZuneStyle.AccentOption.Swatches.ToArray());
        Assert.False(ZuneStyle.UseWindowsAccentOption.Default);
        Assert.True(ZuneStyle.BigNumeralsOption.Default);
        Assert.Equal(ZuneDensity.Standard, ZuneStyle.DensityOption.Default);
        Assert.Equal(new[] { ZuneDensity.Compact, ZuneDensity.Standard, ZuneDensity.Comfortable }, ZuneStyle.DensityOption.Choices.Select(c => c.Value));
        Assert.Equal(new byte[] { 0, 1, 2 }, Enum.GetValues<ZuneDensity>().Select(d => (byte)d));   // persisted: never renumber
        Assert.All(all, o => Assert.Equal(OptionVisibility.Always, o.Visibility));
    }

    // ── the definition ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_definition_is_pure_and_memoized_per_input()
    {
        var a = Def(OptionValues.Empty.With("accent", ZuneAccent.Blue.Dark));
        var b = Def(OptionValues.Empty.With("accent", ZuneAccent.Blue.Dark));
        Assert.Same(a, b);
        Assert.Same(ZuneStyle.DefaultDefinition, Def(OptionValues.Empty));
        Assert.Same(ZuneStyle.DefaultDefinition, Def(OptionValues.Empty.With("accent", ZuneAccent.Pink.Dark).With("bigNumerals", true)));
        // The row size moves only the metrics: the definition is the same instance.
        Assert.Same(ZuneStyle.DefaultDefinition, Def(OptionValues.Empty.With("density", ZuneDensity.Comfortable)));
        Assert.Equal(ZuneRoles.Table, Roles(ZuneStyle.DefaultDefinition));
    }

    [Fact]
    public void An_accent_pick_is_a_colour_only_change_that_moves_the_ramp_and_the_roles()
    {
        var pink = ZuneStyle.DefaultDefinition;
        var green = Def(OptionValues.Empty.With("accent", ZuneAccent.Green.Dark));
        Assert.NotEqual(pink, green);
        Assert.True(pink.DiffersOnlyInColours(green), "the accent applies live");
        Assert.Equal(ZuneAccent.Green.Dark, green.Accent.Light2);   // the dark fill
        Assert.Equal(ZuneAccent.Green.Light, green.Accent.Dark1);   // the light fill, one step darker
        Assert.Equal(ZuneAccent.Green.Dark, Roles(green).Ink.AccentAction.Resolve(ThemeKind.Dark));
    }

    [Fact]
    public void Following_the_windows_accent_carries_the_os_ramp_and_stays_colour_only()
    {
        var os = AccentRamp.Derive(ColorF.FromRgba(0x00, 0x99, 0x66));
        var other = AccentRamp.Derive(ColorF.FromRgba(0xCC, 0x33, 0x00));
        var values = OptionValues.Empty.With("useWindowsAccent", true);
        var followed = Style.Definition(values, os);
        Assert.Equal(os, followed.Accent);
        Assert.Same(followed, Style.Definition(values, os));
        Assert.Equal(other, Style.Definition(values, other).Accent);
        Assert.NotNull(Roles(followed).Ink.AccentAction.Of);   // the roles read the live accent tokens
        Assert.True(ZuneStyle.DefaultDefinition.DiffersOnlyInColours(followed));
        // Without the option the OS ramp is not an input.
        Assert.Same(ZuneStyle.DefaultDefinition, Style.Definition(OptionValues.Empty, os));
    }

    [Fact]
    public void Turning_the_big_numerals_off_is_not_a_colour_only_change()
    {
        var off = Def(OptionValues.Empty.With("bigNumerals", false));
        Assert.False(ZuneStyle.DefaultDefinition.DiffersOnlyInColours(off), "the numerals apply on the theme transaction");
        Assert.Equal(new TextRole(TypeRung.Body), Roles(off).Type.Numeral);
    }

    [Fact]
    public void A_Classic_to_Zune_definition_swap_is_never_colour_only()
    {
        var classic = new ClassicStyle().Definition(OptionValues.Empty, AccentRamp.FluentDefault);
        var zune = ZuneStyle.DefaultDefinition;
        Assert.False(classic.DiffersOnlyInColours(zune));
        Assert.False(zune.DiffersOnlyInColours(classic));
        // Not only the engine tables: the role tables alone differ in non-colour roles (type, shape, space, case).
        var classicRoles = Roles(classic);
        var zuneRoles = Roles(zune);
        Assert.False(classicRoles.DiffersOnlyInColours(zuneRoles));
        Assert.NotEqual(classicRoles.Type, zuneRoles.Type);
        Assert.NotEqual(classicRoles.Shape, zuneRoles.Shape);
        Assert.NotEqual(classicRoles.Case, zuneRoles.Case);
        Assert.NotEqual(classic.Controls, zune.Controls);
        Assert.NotEqual(classic.Radii, zune.Radii);
    }

    [Fact]
    public void The_ground_is_flat_and_opaque_with_no_window_material()
    {
        var d = ZuneStyle.DefaultDefinition;
        Assert.Equal(WindowMaterial.None, d.Materials.For(ThemeKind.Dark));
        Assert.Equal(WindowMaterial.None, d.Materials.For(ThemeKind.Light));
        Assert.Equal(ZuneTokens.DarkGround, d.Dark.WindowBackground);
        Assert.Equal(ZuneTokens.LightGround, d.Light.WindowBackground);
        Assert.Equal(ZuneTokens.DarkGround, d.Dark.FillSolidBase);
        Assert.Equal(ZuneTokens.DarkWell, d.Dark.FillSolidBaseAlt);
        Assert.Equal(ZuneTokens.DarkRaised, d.Dark.FillSolidTertiary);
        Assert.Equal(ZuneTokens.DarkDialog, d.Dark.FillLayerAlt);
        Assert.Equal(ZuneTokens.DarkInk, d.Dark.TextPrimary);
        Assert.Equal(ZuneTokens.DarkInkSecondary, d.Dark.TextSecondary);
        Assert.Equal(ZuneTokens.DarkInkTertiary, d.Dark.TextTertiary);
        Assert.Equal(ZuneTokens.LightInk, d.Light.TextPrimary);
        // The board's ink on the accent: black on the dark ground, white on the light one.
        Assert.Equal(ColorF.FromRgba(0, 0, 0), d.Dark.TextOnAccentPrimary);
        Assert.Equal(ColorF.FromRgba(0xFF, 0xFF, 0xFF), d.Light.TextOnAccentPrimary);
        Assert.Equal(1f, d.DarkShell.Toolbar.A);
        Assert.Equal(1f, d.LightShell.PlayerBar.A);
        // Material is a per-kind constant, so a light/dark flip stays on the live path.
        Assert.Equal(d.Materials.For(ThemeKind.Dark), d.Materials.For(ThemeKind.Light));
    }

    [Fact]
    public void The_radii_type_face_and_gutter_are_Zunes()
    {
        var d = ZuneStyle.DefaultDefinition;
        Assert.Equal(0f, d.Radii.Control);
        Assert.Equal(0f, d.Radii.Card);
        Assert.Equal(0f, d.Radii.Overlay);
        Assert.Equal(16f, d.Radii.Pill);
        Assert.True(d.Radii.Full >= 999f);
        Assert.Equal("Segoe UI Variable Text", d.Fonts.BodyPrimary);
        Assert.Equal(Design.Definition.Fonts.Icon, d.Fonts.Icon);   // the bundled icon face, never the system family
        Assert.Equal(48f, d.Spacing.PageWide);
        Assert.Equal(16f, d.Spacing.PageNarrow);
        Assert.True(d.Elevation.Dark.Card.IsNone && d.Elevation.Dark.DockTop.IsNone, "cards and the bar are flat");
        Assert.False(d.Elevation.Dark.Flyout.IsNone);
        Assert.Same(ZuneControls.Table, d.Controls);
    }

    // ── the metrics ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(ZuneDensity.Compact, 44f, 40f, 12f, 0)]
    [InlineData(ZuneDensity.Standard, 52f, 40f, 16f, 1)]
    [InlineData(ZuneDensity.Comfortable, 60f, 48f, 20f, 2)]
    public void The_metrics_follow_the_density(ZuneDensity density, float row, float paneItem, float cardGap, int rung)
    {
        var m = Style.Metrics(OptionValues.Empty.With("density", density));
        Assert.Equal(m, ZuneStyle.MetricsFor(density));
        Assert.Equal(row, m.RowSingle);
        Assert.Equal(row, m.RowDouble);
        Assert.Equal(row, m.RowWithArt);
        Assert.Equal(paneItem, m.PaneItem);
        Assert.Equal(cardGap, m.CardGap);
        Assert.Equal(rung, m.TrackRows.Rung);
        Assert.Equal(row, m.TrackRows.ModernRow);
        Assert.Equal(row, m.TrackRows.ClassicRow);
        Assert.False(m.TrackRows.IsUnset);
        Assert.True(m.PaneItem >= 40f, "a pane item is a Compact hit target");
    }

    [Fact]
    public void The_metrics_the_density_does_not_move_are_the_boards()
    {
        foreach (var density in Enum.GetValues<ZuneDensity>())
        {
            var m = ZuneStyle.MetricsFor(density);
            Assert.Equal(52f, m.PivotRow);
            Assert.Equal(32f, m.SubPivotRow);
            Assert.Equal(84f, m.PivotRow + m.SubPivotRow);   // the band (Z-Tokens: 84, 52 without row 2)
            Assert.Equal(2f, m.SeekRow);
            Assert.Equal(70f, m.TransportRow);
            Assert.Equal(72f, m.SeekRow + m.TransportRow);   // the play bar
            Assert.Equal(480f, m.MinPageColumn);
            Assert.Equal(360f, m.DockedPane);
            Assert.Equal(360f, m.DockedPane4K);
            Assert.Equal(220f, m.SearchWidth);
            Assert.Equal(48f, m.Gutter);
            Assert.Equal(32f, m.Section);
            Assert.Equal(32f, m.ControlHeight);
            Assert.Equal(640f, m.PanoramaCompactBelow);
        }
        Assert.Equal(ZuneStyle.MetricsFor(ZuneDensity.Standard), Style.Metrics(OptionValues.Empty));
    }

    // ── the control table ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_control_table_restyles_the_controls_the_boards_draw()
    {
        var t = ZuneControls.Table;
        Assert.True(t.Contains(ControlStyles.ButtonStyle));
        Assert.True(t.Contains(ControlStyles.ToggleButtonStyle));
        Assert.True(t.Contains(ControlStyles.SegmentedStyle));
        Assert.True(t.Contains(ControlStyles.ToggleSwitchStyle));
        Assert.True(t.Contains(ControlStyles.SliderStyle));
        Assert.True(t.Contains(ControlStyles.CheckBoxStyle));
        Assert.True(t.Contains(ControlStyles.MenuFlyoutStyle));
        Assert.True(t.Contains(ControlStyles.ContextMenuStyle));
        Assert.True(t.Contains(ControlStyles.ContentDialogStyle));
        Assert.True(t.Contains(ControlStyles.ComboBoxStyle));
        Assert.True(t.Contains(ControlStyles.TextBoxFieldStyle));
        Assert.True(t.Contains(ControlStyles.AutoSuggestBoxFieldStyle));
        Assert.True(t.Contains(ControlStyles.InfoBarStyle));
        Assert.True(t.Contains(ControlStyles.ToolTipStyle));
        Assert.True(t.Contains(ControlStyles.ProgressRingStyle));
        Assert.True(t.Contains(ControlStyles.ProgressBarStyle));
        Assert.True(t.Contains(ControlStyles.SelectorBarStyle));
        Assert.True(t.Contains(ControlStyles.ToastLaneStyle));
        Assert.NotEqual(ControlStyleTable.FluentDefault, t);
    }

    [Theory]
    [InlineData(ControlSize.Small, 32f)]
    [InlineData(ControlSize.Medium, 36f)]
    [InlineData(ControlSize.Large, 40f)]
    public void The_primary_action_is_a_round_disc_and_the_rest_are_words(ControlSize size, float disc)
    {
        var accent = ZuneControls.ButtonFor(ButtonAppearance.Accent, size)!;
        Assert.Equal(disc, accent.MinHeight);
        Assert.True(accent.CornerRadius >= disc / 2f, "the disc is round");
        Assert.Equal(0f, accent.BorderWidth);
        var ring = ZuneControls.ButtonFor(ButtonAppearance.Outline, size)!;
        Assert.Equal(2f, ring.BorderWidth);
        Assert.Equal(disc, ring.MinHeight);
        foreach (var word in new[] { ButtonAppearance.Standard, ButtonAppearance.Subtle })
        {
            var w = ZuneControls.ButtonFor(word, size)!;
            Assert.Equal(0f, w.CornerRadius);
            Assert.Equal(0f, w.BorderWidth);
            Assert.Null(w.BorderBrush);
        }
    }

    [Fact]
    public void The_switch_slider_chips_menus_dialog_and_fields_have_the_boards_geometry()
    {
        var sw = ZuneControls.Switch();
        Assert.Equal((40f, 20f, 16f, 20f), (sw.TrackWidth, sw.TrackHeight, sw.KnobRestSize, sw.KnobTravel));
        var slider = ZuneControls.LineSlider();
        Assert.Equal(2f, slider.TrackHeight);
        Assert.Equal(0f, slider.TrackCornerRadius);
        Assert.Equal(0f, slider.ThumbFill.A);   // the thumb is clear at rest: it shows on hover
        Assert.Equal(12f, slider.InnerThumbDiameter);
        var chip = ZuneControls.Chip();
        Assert.Equal((0f, 32f, 0f), (chip.CornerRadius, chip.MinHeight, chip.BorderWidth));
        var menu = ZuneControls.Menu();
        Assert.Equal(36f, menu.RowHeight);
        Assert.Equal(0f, menu.ItemRadius);
        Assert.Equal(0f, menu.Plate.Radius);
        Assert.Null(menu.Plate.Acrylic);
        Assert.Equal(320f, ZuneControls.TrackMenu().MinWidth);
        Assert.False(ZuneControls.TrackMenu().HeaderDivider);
        var dialog = ZuneControls.Dialog();
        Assert.Equal((0f, 0f, 548f, 26f, 300), (dialog.Radius, dialog.PlateBorderWidth, dialog.MaxWidth, dialog.TitleSize, dialog.TitleWeight));
        Assert.False(dialog.CommandsFill);
        Assert.Equal(16f, ZuneControls.PillField().Radius);
        Assert.Equal(1f, ZuneControls.PillField().BorderWidth);
        Assert.Equal(0f, ZuneControls.RuledField().Radius);
        Assert.Equal(1f, ZuneControls.RuledField().UnderlineThickness);
        Assert.Equal(2f, ZuneControls.LineProgress().TrackHeight);
        Assert.Equal(360f, ZuneControls.Toasts().CardMaxWidth);
        Assert.Equal(18f, ZuneControls.SquareBox().BoxSize);
    }
}
