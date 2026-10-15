// ── Wavee.ShellTests/ColourRolesTests.cs — the colour roles follow the kind and the definition (T2b, X44) ───────────────────
//
// A colour role is a pair or a recipe over the engine's LIVE tokens, evaluated where the element is built, so a light/dark flip and a
// colour-only definition swap must each reach the NEXT read and the engine's epoch re-render must show it on screen. That needs
// `Tok.Use`, which is process-wide state: it lives here, in the one process whose facts never run in parallel, and every fact puts the
// definition and the kind back. (Each role's value is pinned against the formula it replaced in
// `Wavee.Tests\Theming\ClassicColourRolesTests.cs`, for both kinds wherever the formula reads no live token; the roles that DO read live
// tokens are pinned for both kinds here.)

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Theming;
using Wavee.Themes.Classic;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using Xunit;

namespace Wavee.ShellTests;

public sealed class ColourRolesTests(ShellHost host)
{
    static void Restore(ThemeDefinition definition, ThemeKind kind)
    {
        if (!ReferenceEquals(Tok.Definition, definition) || Tok.Theme != kind) Tok.Use(definition, kind);
    }

    static ShellPalette Shell(ThemeKind k) => k == ThemeKind.Light ? Tok.Palette.LightShell : Tok.Palette.DarkShell;

    // The formulas of the roles that read the engine's live tokens, as the old `Design` / call-site branches wrote them.
    static ColorF OldZebra(ThemeKind k) => k == ThemeKind.Light ? Shell(k).RowZebra : Tok.FillSubtleTertiary;

    static readonly ThemeKind[] Kinds = [ThemeKind.Light, ThemeKind.Dark];

    // ── a flip ───────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_roles_that_read_live_tokens_follow_a_light_dark_flip_in_both_directions()
        => host.Run(() =>
        {
            ThemeDefinition original = Tok.Definition;
            ThemeKind kind = Tok.Theme;
            try
            {
                foreach (ThemeKind k in Kinds)
                {
                    Design.SeedPalette(k);   // what every light/dark verb calls
                    Assert.Equal(k, Tok.Theme);
                    Assert.Same(ClassicRoles.Table, Tok.Definition.AppRoles);

                    Assert.Equal(OldZebra(k), R.Layer.RowZebra);
                    Assert.Equal(ColorContrast.Over(Shell(k).RowHover, OldZebra(k)), R.Layer.RowHoverZebra);
                    Assert.Equal(ColorContrast.Over(Shell(k).RowPressed, OldZebra(k)), R.Layer.RowPressedZebra);
                    Assert.Equal(Shell(k).RowHover, R.Layer.RowHover);
                    Assert.Equal(Shell(k).RowPressed, R.Layer.RowPressed);
                    Assert.Equal(Tok.AccentSubtle, R.Layer.SelectedRest);
                    Assert.Equal(ColorContrast.Over(Tok.FillSubtleSecondary, Tok.AccentSubtle), R.Layer.SelectedHover);
                    Assert.Equal(ColorContrast.Over(Tok.FillSubtleTertiary, Tok.AccentSubtle), R.Layer.SelectedPressed);
                    Assert.Equal(Tok.FillSubtleSecondary, R.Layer.ChromeHover);
                    Assert.Equal(Tok.FillSubtleTertiary, R.Layer.ChromePressed);
                    Assert.Equal(Tok.AccentDefault, R.Layer.Badge);
                    Assert.Equal(k == ThemeKind.Dark ? ColorF.FromRgba(0x1C, 0x1C, 0x1E) : Tok.FillCardSecondary, R.Layer.EventPlate);
                    Assert.Equal(k == ThemeKind.Dark ? ColorF.FromRgba(0x1B, 0x1B, 0x1D) : Tok.FillCardSecondary, R.Layer.EventHeroPlate);
                    Assert.Equal(k == ThemeKind.Light ? Tok.SystemFillSuccess : ColorF.FromRgba(0x1D, 0xB9, 0x54), R.Ink.PremiumText);
                    Assert.Equal(Tok.AccentTextPrimary, R.Ink.AccentDecor);

                    // …the per-kind pairs, the stage arm and the wash strengths read the kind that is live
                    Assert.Equal(k == ThemeKind.Dark ? ColorF.FromRgba(0x2A, 0x2A, 0x2A) : ColorF.FromRgba(0xF2, 0xF2, 0xF2), R.Layer.ArtPlaceholder);
                    Assert.Equal(k == ThemeKind.Dark, StageInk.IsDark);
                    Assert.Equal(k == ThemeKind.Dark ? Tok.MediaStage : ColorScience.PageToneNeutralLight, StageInk.Veil);
                    Assert.Equal(k == ThemeKind.Dark ? 0.10f : 0.055f, R.Wash.HeroAlpha);
                    Assert.Equal(k == ThemeKind.Dark ? 0.085f : 0.05f, R.Wash.ShelfAlpha);
                    Assert.Equal(k == ThemeKind.Dark ? 0.20f : 0.30f, R.Wash.PageTonePlaneAlpha);
                    Assert.Equal(k == ThemeKind.Dark ? 1f : 0.5f, R.Lyrics.BloomScale);
                    Assert.Equal(k == ThemeKind.Dark ? Tok.OnMediaPrimary : ColorScience.PageToneNeutralLight, R.Lyrics.BloomOnMedia);
                }
            }
            finally { Restore(original, kind); }
        });

    [Fact]
    public void A_flip_is_a_colour_only_change_that_moves_the_epoch_and_not_the_style_epoch()
        => host.Run(() =>
        {
            ThemeDefinition original = Tok.Definition;
            ThemeKind kind = Tok.Theme;
            ThemeKind other = kind == ThemeKind.Dark ? ThemeKind.Light : ThemeKind.Dark;
            try
            {
                int epoch = Tok.Epoch, style = Tok.StyleEpoch;

                Design.SeedPalette(other);
                Assert.Equal(epoch + 1, Tok.Epoch);    // X44: the readers that re-derive by the epoch see the flip in its publication
                Assert.Equal(style, Tok.StyleEpoch);   // the roles did not move: a colour flip is not a transaction

                Design.SeedPalette(kind);
                Assert.Equal(epoch + 2, Tok.Epoch);    // the epoch only counts up
            }
            finally { Restore(original, kind); }
        });

    // ── a colour-only definition swap ────────────────────────────────────────────────────────────────────────────────────

    static WaveeStyleRoles Recoloured() => ClassicRoles.Table with
    {
        Layer = ClassicRoles.Table.Layer with { RowHover = ColorRole.Fixed(ColorF.FromRgba(0xAA, 0x11, 0x22)) },
        OnMedia = ClassicRoles.Table.OnMedia with { ScrimRest = ColorRole.Pair(ColorF.FromRgba(1, 2, 3), ColorF.FromRgba(4, 5, 6)) },
        Wash = ClassicRoles.Table.Wash with { HeroAlpha = new ModePair<float>(0.31f, 0.32f) },
        Lyrics = ClassicRoles.Table.Lyrics with { ThemePlateAlpha = 0.5f },
    };

    [Fact]
    public void A_colour_only_definition_swap_reaches_the_next_read_without_a_style_epoch()
        => host.Run(() =>
        {
            ThemeDefinition original = Tok.Definition;
            ThemeKind kind = Tok.Theme;
            try
            {
                int epoch = Tok.Epoch, style = Tok.StyleEpoch;

                Tok.Use(original with { AppRoles = Recoloured() }, kind);

                Assert.Equal(epoch + 1, Tok.Epoch);
                Assert.Equal(style, Tok.StyleEpoch);   // colours only: the live cross-fade path
                Assert.Equal(ColorF.FromRgba(0xAA, 0x11, 0x22), R.Layer.RowHover);
                Assert.Equal(kind == ThemeKind.Light ? ColorF.FromRgba(1, 2, 3) : ColorF.FromRgba(4, 5, 6), R.OnMedia.ScrimRest);
                Assert.Equal(kind == ThemeKind.Light ? 0.31f : 0.32f, R.Wash.HeroAlpha);
                Assert.Equal(0.5f, R.Lyrics.ThemePlateAlpha);

                Tok.Use(original, kind);
                Assert.Same(ClassicRoles.Table, R.Current);
                Assert.Equal(Shell(kind).RowHover, R.Layer.RowHover);
                Assert.Equal(style, Tok.StyleEpoch);
            }
            finally { Restore(original, kind); }
        });

    [Fact]
    public void A_stage_arm_swap_is_followed_by_the_live_facade_and_the_pure_view()
        => host.Run(() =>
        {
            ThemeDefinition original = Tok.Definition;
            ThemeKind kind = Tok.Theme;
            try
            {
                var arm = ClassicRoles.Table.StageArm with { Ink = ColorRole.Fixed(ColorF.FromRgba(0xCC, 0x00, 0x00)), IsDark = static _ => true };
                Tok.Use(original with { AppRoles = ClassicRoles.Table with { StageArm = arm } }, kind);
                Assert.Equal(ColorF.FromRgba(0xCC, 0x00, 0x00), StageInk.Ink);
                Assert.Equal(ColorF.FromRgba(0xCC, 0x00, 0x00), R.StageArm.For(ThemeKind.Light).Ink);
                Assert.True(StageInk.IsDark);
                Assert.Equal(ClassicRoles.Table.StageArm.Plate, R.StageArm.Plate);   // the other members are the table's own
            }
            finally { Restore(original, kind); }
        });

    // ── a mounted view ───────────────────────────────────────────────────────────────────────────────────────────────────

    sealed record ProbeProps(string Name);

    static readonly ViewKey<ProbeProps> Probe = new("Colours.Probe");

    /// <summary>One view: a keyed box filled with the row-hover layer role.</summary>
    sealed class ProbeStructure : IStructureLayer
    {
        public string Id => "Colours.Structure";
        public ThemeOptionSchema Options => ThemeOptionSchema.Empty;
        public RoleSet RequiredRoles => RoleSet.None;

        public void Register(ViewRegistryBuilder views)
            => views.Add(Probe, static (ProbeProps props, in ViewContext ctx) =>
                new BoxEl { Key = "colour:" + props.Name, Width = 40f, Height = 40f, Fill = R.Layer.RowHover }, "Colours.Probe.Template");

        public ThemePolicies Policies(OptionValues structure, in StyleMetrics metrics) => ThemePolicies.Default;
        public ShellCapabilities Capabilities(OptionValues structure) => ShellCapabilities.Unrestricted;
    }

    sealed class ProbeStyle : IStyleLayer
    {
        readonly ThemeDefinition _definition = Tok.Definition with { Id = "colours-probe", AppRoles = ClassicRoles.Table };

        public string Id => "Colours.Style";
        public ThemeOptionSchema Options => ThemeOptionSchema.Empty;
        public ThemeDefinition Definition(OptionValues style, AccentRamp osAccent) => _definition;
        public StyleMetrics Metrics(OptionValues style) => StyleMetrics.Unset;
    }

    static ThemeComposition Probed() => new(
        new ThemeManifest("colours-probe", "colours.name", ContractVersion.Major, ContractVersion.Minor, "Colours.Structure", "Colours.Style", null,
            ThemeAvailability.Developer, new ThemePreview("l.png", "d.png")),
        new ProbeStructure(), new ProbeStyle());

    static bool Same(ColorF a, ColorF b)
        => MathF.Abs(a.R - b.R) < 0.002f && MathF.Abs(a.G - b.G) < 0.002f && MathF.Abs(a.B - b.B) < 0.002f && MathF.Abs(a.A - b.A) < 0.002f;

    [Fact]
    public void A_mounted_view_repaints_with_the_new_colour_role_when_the_definition_swaps_colours_only()
        => ViewHarness.With(host, Probe, new ProbeProps("a"), harness =>
        {
            var node = harness.Node("colour:a");
            Assert.True(Same(Shell(Tok.Theme).RowHover, harness.Shell.Host.Scene.Paint(node).Fill));

            int style = Tok.StyleEpoch;
            Tok.Use(Tok.Definition with { AppRoles = Recoloured() }, Tok.Theme);   // the harness puts the original back on dispose
            Assert.Equal(style, Tok.StyleEpoch);
            harness.RunFrames(3);

            Assert.True(Same(ColorF.FromRgba(0xAA, 0x11, 0x22), harness.Shell.Host.Scene.Paint(harness.Node("colour:a")).Fill));
        }, new ViewHarnessOptions { Composition = Probed() });
}
