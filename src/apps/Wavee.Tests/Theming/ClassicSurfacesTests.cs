// ── Wavee.Tests/Theming/ClassicSurfacesTests.cs — Classic's three artwork-colour options (tuned Classic CT9: U1 Rich, U2, U3, F2) ──
//
// Surfaces gained its Rich level (the style's stronger wash pairs; the tone plane capped to Subtle in light while the navigation pane is
// docked, F2), and two new toggles decide whether the page accent (U2) and the now-playing colours (U3) follow the artwork. The toggles
// reach the core as `ThemePolicies.Artwork`; these facts pin the options, the rules, the policy bundles and what the core reads off them.
// The mounted pictures (the tone plane, the veil, the tint, the Settings cards) are `Wavee.ShellTests\ClassicSurfacesMountedTests.cs`.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using Wavee.Themes.Classic;
using Xunit;

namespace Wavee.Tests.Theming;

public sealed class ClassicSurfacesTests
{
    [Fact]
    public void Surfaces_stores_its_three_levels_as_appended_ints()
    {
        Assert.Equal(0, (int)SurfaceWash.Off);
        Assert.Equal(1, (int)SurfaceWash.Subtle);   // M6's stored values keep their meaning
        Assert.Equal(2, (int)SurfaceWash.Rich);
        Assert.Equal(Strings.Settings.ThemeOptions.Group.Surfaces, ClassicStructure.Surfaces.Group);
        Assert.Equal(ClassicStructure.SurfacesQualifiedId, "structure." + ClassicStructure.Surfaces.Id);
    }

    [Fact]
    public void The_artwork_toggles_are_new_structure_options_on_by_default_in_the_surfaces_group()
    {
        var schema = new ClassicStructure().Options;
        foreach (var (option, id) in new[] { (ClassicStructure.AccentFromArtwork, "accentFromArtwork"), (ClassicStructure.NowPlayingColors, "nowPlayingColors") })
        {
            Assert.Equal(id, option.Id);
            Assert.True(option.Default);
            Assert.Equal(Strings.Settings.ThemeOptions.Group.Surfaces, option.Group);
            Assert.Same(option, schema.Find(id));
            Assert.Equal("theme.classic.structure." + id, ThemeOptionStore.SettingName(ClassicTheme.Id, OptionLayer.Structure, id));
        }
        Assert.Equal(ClassicStructure.AccentFromArtworkQualifiedId, "structure." + ClassicStructure.AccentFromArtwork.Id);
        Assert.Equal(ClassicStructure.NowPlayingColorsQualifiedId, "structure." + ClassicStructure.NowPlayingColors.Id);
    }

    [Theory]
    [InlineData(SurfaceWash.Off, false, false)]
    [InlineData(SurfaceWash.Subtle, true, false)]
    [InlineData(SurfaceWash.Rich, true, true)]
    public void Each_level_draws_and_strengthens_the_washes_as_named(SurfaceWash level, bool washes, bool rich)
    {
        Assert.Equal(washes, ClassicStructure.WashesOn(level));
        Assert.Equal(rich, ClassicStructure.RichWashes(level));
    }

    [Theory]
    // F2: only the light theme beside a docked pane caps the Rich tone plane to Subtle, so the card stays a step lighter than the chrome.
    [InlineData(SurfaceWash.Rich, true, true, false)]
    [InlineData(SurfaceWash.Rich, true, false, true)]
    [InlineData(SurfaceWash.Rich, false, true, true)]
    [InlineData(SurfaceWash.Rich, false, false, true)]
    [InlineData(SurfaceWash.Subtle, false, false, false)]
    [InlineData(SurfaceWash.Off, false, false, false)]
    public void The_rich_tone_plane_is_capped_only_in_light_beside_a_docked_pane(SurfaceWash level, bool light, bool docked, bool richPlane)
        => Assert.Equal(richPlane, ClassicStructure.RichTonePlane(level, light, docked));

    [Fact]
    public void Classic_defines_a_stronger_Rich_pair_for_every_level_dependent_wash_under_the_legibility_ceiling()
    {
        var wash = ClassicRoles.Table.Wash;
        var rich = Assert.IsType<WashStrength>(wash.Rich);
        foreach (var (subtle, strong) in new[]
        {
            (wash.HeroAlpha, rich.HeroAlpha), (wash.ShelfAlpha, rich.ShelfAlpha),
            (wash.PageTonePlaneAlpha, rich.PageTonePlaneAlpha), (wash.ShellTintAlpha, rich.ShellTintAlpha),
        })
        {
            Assert.True(strong.Light > subtle.Light && strong.Dark > subtle.Dark, "Rich is stronger than Subtle in both kinds");
            Assert.True(strong.Light < 0.5f && strong.Dark < 0.5f, "under the 0.5 that keeps body text above AA on the plane");
        }
        Assert.Equal(new ModePair<float>(0.45f, 0.30f), rich.PageTonePlaneAlpha);
        Assert.Equal(new ModePair<float>(0.10f, 0.22f), rich.ShellTintAlpha);
    }

    // ── the policy the core reads ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_artwork_toggles_reach_the_core_as_one_bundle_per_combination()
    {
        var structure = new ClassicStructure();
        Assert.Same(ClassicPolicies.Bundle, structure.Policies(OptionValues.Empty, StyleMetrics.Unset));   // both on: the default bundle
        Assert.Same(ArtworkColorPolicy.Default, ClassicPolicies.Bundle.Artwork);

        var accentOff = structure.Policies(OptionValues.Empty.With("accentFromArtwork", false), StyleMetrics.Unset);
        Assert.Equal(new ArtworkColorPolicy(AccentFromArtwork: false, NowPlayingColors: true), accentOff.Artwork);
        var nowPlayingOff = structure.Policies(OptionValues.Empty.With("nowPlayingColors", false), StyleMetrics.Unset);
        Assert.Equal(new ArtworkColorPolicy(AccentFromArtwork: true, NowPlayingColors: false), nowPlayingOff.Artwork);
        var bothOff = structure.Policies(OptionValues.Empty.With("accentFromArtwork", false).With("nowPlayingColors", false), StyleMetrics.Unset);
        Assert.Equal(new ArtworkColorPolicy(false, false), bothOff.Artwork);

        // republishing an unchanged option is not a change, and the other decisions ride along untouched
        Assert.Same(accentOff, structure.Policies(OptionValues.Empty.With("accentFromArtwork", false), StyleMetrics.Unset));
        var hiddenArtAccentOff = structure.Policies(OptionValues.Empty.With("accentFromArtwork", false).With("hideTrackArt", true), StyleMetrics.Unset);
        Assert.False(hiddenArtAccentOff.Lists.TrackArtwork);
        Assert.False(hiddenArtAccentOff.Artwork.AccentFromArtwork);
        Assert.Equal(ClassicPolicies.Bundle.NavPane, bothOff.NavPane);
    }

    [Fact]
    public void Every_combination_is_its_own_valid_bundle()
    {
        var seen = new HashSet<ThemePolicies>(ReferenceEqualityComparer.Instance);
        foreach (bool accent in new[] { true, false })
            foreach (bool nowPlaying in new[] { true, false })
                foreach (bool hideArt in new[] { true, false })
                    foreach (bool covers in new[] { true, false })
                    {
                        var p = ClassicPolicies.For(SidebarLayoutId.Classic, SidebarDensity.Default, hideArt, showCovers: covers,
                            accentFromArtwork: accent, nowPlayingColors: nowPlaying);
                        Assert.Empty(p.Validate());
                        Assert.Equal((accent, nowPlaying), (p.Artwork.AccentFromArtwork, p.Artwork.NowPlayingColors));
                        Assert.Equal(!hideArt, p.Lists.TrackArtwork);
                        Assert.Equal(covers, p.NavPane.ShowCovers);
                        Assert.True(seen.Add(p), "one instance per combination");
                    }
    }

    [Fact]
    public void A_structure_that_declares_nothing_follows_the_artwork_and_a_missing_policy_is_invalid()
    {
        Assert.Equal(new ArtworkColorPolicy(true, true), ThemePolicies.Default.Artwork);
        Assert.Contains("Artwork is missing", (ThemePolicies.Default with { Artwork = null! }).Validate());
    }
}

/// <summary>What the core derives off the published policy: the page accent ladder (U2) and the stage's cover colours (U3). `ThemeHost`
/// is process state, so these run in the platform collection and reset it on both sides.</summary>
[Collection(PlatformCollection.Name)]
public sealed class ClassicArtworkColourHostTests : IDisposable
{
    const string GradedImageId = "ab67616d0000b273c79c9c0000000000000c009";
    static readonly Scheme s_scheme = new(0xFF191414, 0xFF1DB954, 0xFFFFFFFF, 0xFFB3B3B3, 0xFFFFFFFF);
    readonly MemoryAppSettings _settings = new();

    public ClassicArtworkColourHostTests()
    {
        ThemeHost.Reset();
        Platform.UseSettings(_settings);
        ThemeHost.Install(ClassicTheme.Create());
    }

    public void Dispose()
    {
        ThemeHost.Reset();
        Platform.UseSettings(null);
    }

    static void Set(string option, bool on) => ThemeHost.Apply(new OptionChange(ClassicTheme.Id, "structure." + option, on));

    [Fact]
    public void Accent_from_artwork_off_makes_every_page_accent_the_system_accent()
    {
        TestScope.Fresh();
        Palette.SetGraded(GradedImageId.AsSpan(), dark: s_scheme, light: s_scheme, hasLight: true, bestFitIsLight: false);
        var graded = Design.Palette.Floor(Design.Palette.ChromeAccent(s_scheme));
        var payload = Design.Palette.Floor(Design.Palette.ChromeFromPayload(0xFF6A1B9Au));
        Assert.Equal(graded, Detail.AccentFor(GradedImageId, 0xFF6A1B9Au));
        Assert.Equal(payload, Detail.AccentFor(null, 0xFF6A1B9Au));

        Set("accentFromArtwork", false);
        Assert.False(ShellPolicy.Now.Artwork.AccentFromArtwork);
        Assert.Equal(Tok.AccentDefault, Detail.AccentFor(GradedImageId, 0xFF6A1B9Au));   // the cover's rung
        Assert.Equal(Tok.AccentDefault, Detail.AccentFor(null, 0xFF6A1B9Au));            // the payload rung
        Assert.Null(Detail.HeldAccent);                                                    // no held artwork colour seeds a page

        Set("accentFromArtwork", true);
        Assert.Equal(graded, Detail.AccentFor(GradedImageId, 0xFF6A1B9Au));
    }

    [Fact]
    public void Now_playing_colours_off_gives_the_stage_the_system_accent_and_the_neutral_stand_in()
    {
        TestScope.Fresh();
        Palette.SetGraded(GradedImageId.AsSpan(), dark: s_scheme, light: s_scheme, hasLight: true, bestFitIsLight: false);
        ColorF fromCover = StageInk.Accent(GradedImageId);
        Assert.NotEqual(Tok.AccentDefault, fromCover);
        Assert.True(StageInk.NowPlayingColors);

        Set("nowPlayingColors", false);
        Assert.False(StageInk.NowPlayingColors);
        Assert.Equal(Tok.AccentDefault, StageInk.Accent(GradedImageId));
        Assert.Equal(StageInk.ArtStandIn(""), StageInk.ArtStandIn(GradedImageId));   // the neutral tile, whatever the cover
        Assert.True(ShellPolicy.Now.Artwork.AccentFromArtwork);                        // the two toggles are independent

        Set("nowPlayingColors", true);
        Assert.Equal(fromCover, StageInk.Accent(GradedImageId));
    }
}
