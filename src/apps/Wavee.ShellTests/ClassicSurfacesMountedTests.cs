// ── Wavee.ShellTests/ClassicSurfacesMountedTests.cs — Classic's Surfaces levels and artwork colours, mounted (tuned Classic CT9) ──
//
// The pure facts (the options, the F2 rule, the policy bundles, the accent ladder and the stage accent off the policy) are
// `Wavee.Tests\Theming\ClassicSurfacesTests.cs`. What only a mounted tree can say:
//
//   tone plane   Rich paints the style's Rich plane alpha; in the real frame, light Rich beside the docked pane stays at Subtle (F2) and
//                a window too narrow for a docked pane takes Rich again; a grading that lands after the page opened still colours it,
//                and Surfaces Off with the page open clears it
//   tint         the tint binder publishes the Rich tint alpha
//   veil         Surfaces Off keeps the photo veil but on the ladder's neutral rung (no hue)
//   Settings     the Surfaces row is three preview cards of the picker tile's size; the strip carries the sample hue at Subtle's 25 % and
//                more at Rich, none at Off; the two artwork toggles are generated rows in the same group

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using Wavee.Themes.Classic;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed class ClassicSurfacesMountedTests(ShellHost host)
{
    static ViewHarnessOptions At(SurfaceWash level, ThemeKind? kind = null) => new()
    {
        Composition = ClassicTheme.Create(),
        Options = new Dictionary<string, object> { ["structure.surfaces"] = level },
        Kind = kind,
    };

    /// <summary>The painted alpha of the tone plane under <paramref name="root"/> (0 when it paints nothing).</summary>
    static float PlaneAlpha(SceneStore scene, IEnumerable<NodeHandle> subtree)
    {
        foreach (var n in subtree)
        {
            float a = scene.Paint(n).Fill.A;
            if (a > 0f && a < 0.5f) return a;
        }
        return 0f;
    }

    // ── the tone plane ───────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(ThemeKind.Light)]
    [InlineData(ThemeKind.Dark)]
    public void Rich_paints_the_style_s_stronger_tone_plane_where_no_pane_is_docked(ThemeKind kind)
    {
        float subtle = 0f, rich = 0f, expectedSubtle = 0f, expectedRich = 0f;
        // The payload rung colours the plane without a network grading, as a detail page does first.
        var props = new CoverToneProps(null, null, 0f, 0f, HeroOnly: false, PayloadAccent: 0xFF3366CCu);
        ViewHarness.With(host, ViewKeys.ShellCoverTone, props, harness =>
        {
            harness.RunFrames(30);
            subtle = PlaneAlpha(harness.Shell.Host.Scene, harness.Subtree(harness.Shell.Host.Scene.Root));
            expectedSubtle = R.Wash.PageTonePlaneAlphaAt(rich: false);
        }, At(SurfaceWash.Subtle, kind));
        ViewHarness.With(host, ViewKeys.ShellCoverTone, props, harness =>
        {
            harness.RunFrames(30);
            rich = PlaneAlpha(harness.Shell.Host.Scene, harness.Subtree(harness.Shell.Host.Scene.Root));
            expectedRich = R.Wash.PageTonePlaneAlphaAt(rich: true);
        }, At(SurfaceWash.Rich, kind));

        Assert.Equal(expectedSubtle, subtle, 3);
        Assert.Equal(expectedRich, rich, 3);   // a view mounted alone has no frame, so no docked pane caps it
        Assert.True(rich > subtle);
    }

    /// <summary>What <see cref="AlbumPlaneUnderRich"/> read off the mounted album page.</summary>
    readonly record struct AlbumPlane(float Alpha, bool Docked, float Subtle, float Rich, float BeforeGrading, float AfterOff);

    /// <summary>Mounts the real shell at <paramref name="width"/>, sets Surfaces to Rich in <paramref name="kind"/>, opens an album and reads its
    /// tone plane's alpha once the cover's colour has landed. <paramref name="late"/>: the grading lands AFTER the page mounted (as a network
    /// grading does), and Surfaces is then turned Off with the page still mounted; otherwise it is cached before the page opens.</summary>
    AlbumPlane AlbumPlaneUnderRich(ThemeKind kind, float width, bool late = false)
    {
        AlbumPlane result = default;
        host.Mounted(shell =>
        {
            ThemeKind before = Tok.Theme;
            var level = ThemeHost.Values.Structure.Get(ClassicStructure.Surfaces.Key);
            try
            {
                Design.SeedPalette(kind);
                _ = ThemeHost.Apply(new OptionChange(ClassicTheme.Id, "structure.surfaces", SurfaceWash.Rich));
                shell.RunFrames(60);
                // The fake album carries no payload accent, so only the cover's grading colours its plane; the headless shell grades no
                // artwork itself. The palette table is process-wide: a fresh NEGATIVE first makes the late arm start from "no colour".
                var album = EntityUri.Parse("spotify:album:al2");
                string cover = Assert.IsType<string>(Controls.ArtUrl(Entities.Album(album).ImageId));
                var scheme = new Scheme(0xFF191414, 0xFF1DB954, 0xFFFFFFFF, 0xFFB3B3B3, 0xFFFFFFFF);
                if (late) Palette.SetNegative(cover.AsSpan());
                else Palette.SetGraded(cover.AsSpan(), dark: scheme, light: scheme, hasLight: true, bestFitIsLight: false);
                Shell.GoTo(Shell.For(album, "Album"));
                UntilBackground(shell, () => shell.FindPrefix("detail-tone:").Count == 1, "the album page");
                float Plane() => PlaneAlpha(shell.Host.Scene, Subtree(shell.Host.Scene, shell.FindPrefix("detail-tone:")[0].Node));
                float beforeGrading = 0f;
                if (late)
                {
                    shell.RunFrames(40);
                    beforeGrading = Plane();
                    Palette.SetGraded(cover.AsSpan(), dark: scheme, light: scheme, hasLight: true, bestFitIsLight: false);
                }
                Until(shell, () => Plane() > 0f, "the album's tone plane");
                shell.RunFrames(40);   // any brush ramp has settled
                float alpha = Plane();
                bool docked = Sidebar.Mode.Peek() != SidebarPaneMode.Minimal;
                float afterOff = 0f;
                if (late)
                {
                    _ = ThemeHost.Apply(new OptionChange(ClassicTheme.Id, "structure.surfaces", SurfaceWash.Off));
                    shell.RunFrames(40);
                    afterOff = Plane();
                }
                result = new AlbumPlane(alpha, docked, R.Wash.PageTonePlaneAlphaAt(rich: false), R.Wash.PageTonePlaneAlphaAt(rich: true),
                                        beforeGrading, afterOff);
            }
            finally
            {
                _ = ThemeHost.Apply(new OptionChange(ClassicTheme.Id, "structure.surfaces", level));
                Design.SeedPalette(before);
                shell.RunFrames(20);
            }
        }, width, 760f);
        return result;
    }

    [Fact]
    public void Light_Rich_beside_the_docked_pane_keeps_the_card_at_Subtle_so_it_stays_lighter_than_the_chrome()
    {
        var plane = AlbumPlaneUnderRich(ThemeKind.Light, 1180f);
        Assert.True(plane.Docked, "the pane is docked at 1180 DIP");
        Assert.True(plane.Rich > plane.Subtle);
        Assert.Equal(plane.Subtle, plane.Alpha, 3);   // F2
    }

    [Fact]
    public void Light_Rich_with_no_docked_pane_takes_the_Rich_plane()
    {
        var plane = AlbumPlaneUnderRich(ThemeKind.Light, 400f);
        Assert.False(plane.Docked, "a 400 DIP window has no docked pane");
        Assert.Equal(plane.Rich, plane.Alpha, 3);
    }

    [Fact]
    public void A_grading_that_lands_after_the_album_opened_colours_its_tone_plane_and_Off_clears_it()
    {
        var plane = AlbumPlaneUnderRich(ThemeKind.Light, 400f, late: true);
        Assert.Equal(0f, plane.BeforeGrading);           // no colour known yet: the ground is neutral
        Assert.Equal(plane.Rich, plane.Alpha, 3);         // the late grading reached the mounted plane
        Assert.Equal(0f, plane.AfterOff);                 // Surfaces Off with the page still open takes the tone away
    }

    // ── the tint and the veil ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Rich_publishes_the_style_s_stronger_chrome_tint()
    {
        var slot = new Signal<ShellMaterialState>(default);
        float expected = 0f;
        ViewHarness.With(host, ViewKeys.ShellCoverTint,
            new CoverTintProps(null, null, Ready: true, Apply: true, new object(), MaterialPort.Of(slot), PayloadAccent: 0xFF336699u),
            harness =>
            {
                harness.RunFrames(3);
                expected = R.Wash.ShellTintAlphaAt(rich: true);
            }, At(SurfaceWash.Rich));
        Assert.NotNull(slot.Peek().Tint);
        Assert.Equal(expected, slot.Peek().Tint!.Value.A, 3);
    }

    /// <summary>The ladder rung the mounted veil was keyed on (`artist-veil-tone:{rung}:{colour}`).</summary>
    static AccentLadder.Rung VeilRung(ViewHarness harness)
    {
        var (_, key) = Assert.Single(harness.FindPrefix("artist-veil-tone:"));
        return (AccentLadder.Rung)byte.Parse(key.Split(':')[1], System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public void Surfaces_off_keeps_the_photo_veil_but_takes_away_its_hue()
    {
        var props = new CoverVeilProps(null, Vertical: true, Width: 400f, Height: 300f, PayloadAccent: 0xFF336699u);
        ViewHarness.With(host, ViewKeys.ShellCoverVeil, props,
            harness => Assert.Equal(AccentLadder.Rung.Payload, VeilRung(harness)), At(SurfaceWash.Subtle));
        ViewHarness.With(host, ViewKeys.ShellCoverVeil, props,
            harness => Assert.Equal(AccentLadder.Rung.Default, VeilRung(harness)),   // still mounted: it darkens the photo for the title
            At(SurfaceWash.Off));
    }

    // ── Settings ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The board's sample hue on the strip of a Surfaces card, as its alpha (0 when the card draws none).</summary>
    static float SampleAlpha(Shell.HeadlessShell shell, NodeHandle card)
    {
        var scene = shell.Host.Scene;
        foreach (var n in Subtree(scene, card))
        {
            var f = scene.Paint(n).Fill;
            if (f.A > 0f && MathF.Abs(f.R - 176f / 255f) < 0.01f && MathF.Abs(f.G - 110f / 255f) < 0.01f && MathF.Abs(f.B - 190f / 255f) < 0.01f)
                return f.A;
        }
        return 0f;
    }

    [Fact]
    public void Settings_previews_the_three_levels_as_cards_and_offers_the_two_artwork_toggles()
        => host.Mounted(shell =>
        {
            Settings.Open(Settings.Tab.Appearance);
            shell.RunFrames(60);
            foreach (string id in new[] { "structure.surfaces", "structure.accentFromArtwork", "structure.nowPlayingColors" })
                Assert.False(shell.Find("opt:" + id).IsNull, "generated row '" + id + "' is mounted");

            var cards = shell.FindPrefix("opt:card:structure.surfaces:").OrderBy(c => c.Key, StringComparer.Ordinal).ToArray();
            Assert.Equal(3, cards.Length);
            foreach (var (node, _) in cards)
            {
                var b = shell.Host.Scene.Bounds(node);
                Assert.Equal(116f, b.W, 0.5f);   // the picker tile (board C-Settings: 116 x 84 cards)
            }
            Assert.Equal(0f, SampleAlpha(shell, cards[0].Node));          // Off: no hue
            Assert.Equal(0.25f, SampleAlpha(shell, cards[1].Node), 3);    // Subtle: the board's 25 %
            Assert.True(SampleAlpha(shell, cards[2].Node) > 0.25f);       // Rich: the style's own step up
        });
}
