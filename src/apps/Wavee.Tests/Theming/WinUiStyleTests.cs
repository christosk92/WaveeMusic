// ── Wavee.Tests/Theming/WinUiStyleTests.cs — the WinUI style layer: definition, options, metrics, roles (themes plan 6.2, W1) ───────
//
// What the composition reads from `WinUiStyle`: its id and its three options (the backdrop and the density on the TRANSACTION, the Windows accent
// LIVE), a definition that is pure and memoized per input, an accent swap that is colour only and a backdrop swap that is not, the W-Tokens
// deltas over Wavee's neutral palette, the metrics per density, and the Panorama role values. NO TOKEN CHANGES HERE: nothing installs a definition.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Theming;
using Wavee.Themes.Classic;
using Wavee.Themes.WinUi;
using Wavee.Theming;
using Wavee.Theming.Roles;
using Xunit;
using WindowMaterial = FluentGpu.Pal.WindowMaterial;

namespace Wavee.Tests.Theming;

public sealed class WinUiStyleTests
{
    static readonly WinUiStyle Style = WinUiStyle.Create();

    static ThemeDefinition Def(OptionValues values) => Style.Definition(values, AccentRamp.FluentDefault);

    static WaveeStyleRoles Roles(ThemeDefinition d) => Assert.IsType<WaveeStyleRoles>(d.AppRoles);

    [Fact]
    public void The_style_is_WinUi_Style_and_Create_returns_it()
    {
        Assert.Equal("WinUi.Style", WinUiStyle.LayerId);
        Assert.Equal(WinUiStyle.LayerId, WinUiStyle.Create().Id);
    }

    [Fact]
    public void The_options_are_the_backdrop_the_density_and_the_windows_accent_with_their_apply_modes()
    {
        var all = Style.Options.All;
        Assert.Equal(new[] { "backdrop", "density", "useWindowsAccent" }, all.Select(o => o.Id));
        Assert.Equal(ApplyMode.Transaction, Style.Options.Find("backdrop")!.Apply);
        Assert.Equal(ApplyMode.Transaction, Style.Options.Find("density")!.Apply);
        Assert.Equal(ApplyMode.Live, Style.Options.Find("useWindowsAccent")!.Apply);
        Assert.Equal(WinUiBackdrop.Mica, WinUiStyle.BackdropOption.Default);
        Assert.Equal(WinUiDensity.Standard, WinUiStyle.DensityOption.Default);
        Assert.True(WinUiStyle.UseWindowsAccentOption.Default);
        Assert.Equal(new byte[] { 0, 1, 2, 3 }, Enum.GetValues<WinUiBackdrop>().Select(b => (byte)b));   // persisted: never renumber
        Assert.Equal(new byte[] { 0, 1 }, Enum.GetValues<WinUiDensity>().Select(d => (byte)d));
        Assert.Equal("style.density", WinUiDensityOption.QualifiedId);
    }

    [Fact]
    public void The_definition_is_pure_and_memoized_per_input()
    {
        Assert.Same(WinUiStyle.DefaultDefinition, Def(OptionValues.Empty));
        Assert.Same(WinUiStyle.DefaultDefinition, Def(OptionValues.Empty.With("useWindowsAccent", false)));   // no OS ramp read: the same blue
        Assert.Same(WinUiStyle.DefaultDefinition, Def(OptionValues.Empty.With("density", WinUiDensity.Compact)));
        Assert.Same(WinUiRoles.Table, Roles(WinUiStyle.DefaultDefinition));
        Assert.Same(Def(OptionValues.Empty.With("backdrop", WinUiBackdrop.Acrylic)), Def(OptionValues.Empty.With("backdrop", WinUiBackdrop.Acrylic)));
    }

    [Fact]
    public void The_accent_is_the_os_ramp_when_followed_and_the_0078d4_ramp_otherwise()
    {
        var os = AccentRamp.Derive(ColorF.FromRgba(0x00, 0x99, 0x66));
        Assert.Equal(os, Style.Definition(OptionValues.Empty, os).Accent);
        Assert.Equal(WinUiAccent.Default, Style.Definition(OptionValues.Empty.With("useWindowsAccent", false), os).Accent);
        Assert.Equal(WinUiAccent.Default, WinUiStyle.DefaultDefinition.Accent);
        Assert.NotEqual(AccentRamp.FluentDefault, WinUiStyle.DefaultDefinition.Accent);   // never the baked-token marker
        Assert.Equal(ColorF.FromRgba(0x60, 0xCD, 0xFF), WinUiAccent.Default.Light2);      // W-Tokens: the dark fill
        Assert.Equal(ColorF.FromRgba(0x99, 0xEB, 0xFF), WinUiAccent.Default.Light3);      // the dark accent text
        Assert.Equal(ColorF.FromRgba(0x00, 0x5F, 0xB8), WinUiAccent.Default.Dark1);       // the light fill
        Assert.Equal(ColorF.FromRgba(0x00, 0x3E, 0x92), WinUiAccent.Default.Dark2);       // the light accent text
        Assert.True(WinUiStyle.DefaultDefinition.DiffersOnlyInColours(Style.Definition(OptionValues.Empty, os)), "the accent applies live");
    }

    [Theory]
    [InlineData(WinUiBackdrop.Mica, WindowMaterial.Mica)]
    [InlineData(WinUiBackdrop.MicaAlt, WindowMaterial.MicaAlt)]
    [InlineData(WinUiBackdrop.Acrylic, WindowMaterial.Acrylic)]
    [InlineData(WinUiBackdrop.None, WindowMaterial.None)]
    public void The_backdrop_is_the_window_material_in_both_kinds(WinUiBackdrop backdrop, WindowMaterial material)
    {
        var d = Def(OptionValues.Empty.With("backdrop", backdrop));
        Assert.Equal(material, d.Materials.For(ThemeKind.Dark));
        Assert.Equal(material, d.Materials.For(ThemeKind.Light));
        if (backdrop != WinUiBackdrop.Mica)
            Assert.False(WinUiStyle.DefaultDefinition.DiffersOnlyInColours(d), "the backdrop applies on the transaction");
    }

    [Fact]
    public void The_tokens_are_the_boards()
    {
        var d = WinUiStyle.DefaultDefinition;
        Assert.Equal(ColorF.FromRgba(0x20, 0x20, 0x20), d.Dark.WindowBackground);
        Assert.Equal(ColorF.FromRgba(0xF3, 0xF3, 0xF3), d.Light.WindowBackground);
        Assert.Equal(ColorF.FromRgba(0x1C, 0x1C, 0x1C), d.Dark.FillSolidBaseAlt);
        Assert.Equal(ColorF.FromRgba(0xE9, 0xE9, 0xE9), d.Light.FillSolidBaseAlt);
        Assert.Equal(ColorF.FromRgba(0xFF, 0xFF, 0xFF), d.Dark.TextPrimary);
        Assert.Equal(WinUiTokens.DarkTextTertiary, d.Dark.TextTertiary);
        Assert.Equal(WinUiTokens.LightTextTertiary, d.Light.TextTertiary);
        Assert.Equal("Segoe UI Variable Text", d.Fonts.BodyPrimary);
        Assert.Equal(Design.Definition.Fonts.Icon, d.Fonts.Icon);
        Assert.Equal(RadiiScale.FluentDefault, d.Radii);
        Assert.Equal(TypeRamp.FluentDefault, d.Type);
        Assert.Equal(36f, d.Spacing.PageWide);
        Assert.Same(WinUiControls.Table, d.Controls);
        // The light text 3 holds AA on the lightest plate a count or a date sits on.
        Assert.True(ColorContrast.Ratio(ColorContrast.Flatten(d.Light.TextTertiary, ColorF.FromRgba(0xFF, 0xFF, 0xFF)), ColorF.FromRgba(0xFF, 0xFF, 0xFF)) >= 4.5f);
        Assert.True(ColorContrast.Ratio(ColorContrast.Flatten(d.Light.TextTertiary, ColorF.FromRgba(0xF2, 0xF2, 0xF2)), ColorF.FromRgba(0xF2, 0xF2, 0xF2)) >= 4.5f);
    }

    [Fact]
    public void A_Classic_to_WinUI_definition_swap_is_never_colour_only()
    {
        var classic = new ClassicStyle().Definition(OptionValues.Empty, AccentRamp.FluentDefault);
        Assert.False(classic.DiffersOnlyInColours(WinUiStyle.DefaultDefinition));
        Assert.False(Roles(classic).DiffersOnlyInColours(WinUiRoles.Table));
    }

    [Theory]
    [InlineData(WinUiDensity.Standard, 40f, 48f, 36f, 16f, 1)]
    [InlineData(WinUiDensity.Compact, 32f, 40f, 32f, 12f, 0)]
    public void The_metrics_follow_the_density(WinUiDensity density, float single, float dbl, float paneItem, float cardGap, int rung)
    {
        var m = Style.Metrics(OptionValues.Empty.With("density", density));
        Assert.Equal(m, WinUiStyle.MetricsFor(density));
        Assert.Equal((single, dbl, dbl, paneItem, cardGap, rung), (m.RowSingle, m.RowDouble, m.RowWithArt, m.PaneItem, m.CardGap, m.TrackRows.Rung));
        Assert.Equal(dbl, m.TrackRows.ModernRow);
        Assert.Equal(single, m.TrackRows.ClassicRow);
        Assert.Equal(density, WinUiDensityOption.OfRung(m.TrackRows.Rung));
        // Nothing else moves.
        Assert.Equal((32f, 48f, 40f, 20f, 64f, 280f, 36f, 24f, 660f, 1000f, 340f, 340f),
            (m.ControlHeight, m.PivotRow, m.SubPivotRow, m.SeekRow, m.TransportRow, m.SearchWidth, m.Gutter, m.Section, m.MinPageColumn,
             m.PanoramaCompactBelow, m.DockedPane, m.DockedPane4K));
    }

    [Fact]
    public void The_panorama_roles_are_the_canvas_values()
    {
        var t = WinUiRoles.Table.Type;
        Assert.Equal(TypeRung.Display, t.Panorama.Rung);              // 68/92/600
        Assert.Equal(TypeRung.TitleLarge, t.PanoramaCompact.Rung);    // 40/52
        Assert.Equal(TypeRung.Title, t.Pivot.Rung);                   // 28/36/600
        Assert.Equal(t.FacetTitle.RestWeight, t.FacetTitle.SelectedWeight);       // one weight: selection never reflows
        Assert.Equal(t.FacetCompact.RestWeight, t.FacetCompact.SelectedWeight);
        Assert.Equal(TypeRung.Body, t.SubPivot.Rung);                 // 14/20
        Assert.Equal((20f, 28f, (ushort)400), (t.Numeral.Size, t.Numeral.LineHeight, t.Numeral.Weight));
        Assert.Equal((28f, 36f, (ushort)400), (t.NumeralLarge.Size, t.NumeralLarge.LineHeight, t.NumeralLarge.Weight));
        Assert.Equal(TypeRung.Display, t.NowPlayingDisplay.Rung);
        Assert.Equal(TypeRung.TitleLarge, t.LyricsLine.Rung);
        var c = WinUiRoles.Table.Case;
        Assert.Equal(new[] { TextCase.AsWritten }, new[] { c.Label, c.Navigation, c.Heading, c.Command }.Distinct());
        var s = WinUiRoles.Table.Shape;
        Assert.Equal((8f, 8f, 4f, 8f, 4f, 8f), (s.Card, s.Cover, s.Control, s.Overlay, s.PinArt, s.ContentSeam));
    }

    [Fact]
    public void The_row_ladder_has_no_zebra_and_the_selection_only_goes_up()
    {
        var l = WinUiRoles.Table.Layer;
        foreach (var kind in new[] { ThemeKind.Dark, ThemeKind.Light })
        {
            Assert.Equal(0f, l.RowZebra.Resolve(kind).A);
            float rest = l.SelectedRest.Resolve(kind).A, pressed = l.SelectedPressed.Resolve(kind).A, hover = l.SelectedHover.Resolve(kind).A;
            Assert.True(rest < pressed && pressed < hover, kind + ": " + rest + " < " + pressed + " < " + hover);
            Assert.Equal(l.RowHover.Resolve(kind), l.SelectedRest.Resolve(kind));   // W-RowStates: selected = subtle fill 1 + the pill
        }
        Assert.Equal(WinUiTokens.DarkBase, l.ShellGround.Resolve(ThemeKind.Dark));
        Assert.Equal(WinUiTokens.LightBase, l.ShellGround.Resolve(ThemeKind.Light));
        Assert.Equal(WinUiTokens.DarkContent, l.ContentSurface.Resolve(ThemeKind.Dark));
        Assert.Equal(WinUiTokens.LightContent, l.ContentSurface.Resolve(ThemeKind.Light));
    }
}
