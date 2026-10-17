// ── Wavee.ShellTests/ZuneArtistMountedTests.cs — Zune's artist page, lightbox and discography, mounted under Zune (themes plan 6.3; P5 Z6a2) ──
//
// The app mounted under Zune (`ZuneHost`) at 1440 x 960 (Standard), against the fake dataset:
//
//   registered   Zune's own registration draws the artist page, the lightbox and the discography page (zune.*), and the Discography style
//   artist       the fake artist `ar0` renders Zune's page (its column, its hero with the panorama name, never Classic's `artist-page:` column),
//                the hero stands at the tier record's height, the popular band mounts, and the discography facets mount under their headings
//   bleed        the artist's header image is published as the page's `Backdrop.Bleed` and the band's row 2 carries the pivot and the actions
//   discography  the standalone page mounts Zune's column with its release-type words (the route's facet current)
//   tuning (T2)  one home per verb (audit A4: from Small up play / shuffle / follow are the band's, the hero keeps radio and "..."), the system page
//                column at every tier (A1), the Huge chart cap, and the hero HUGS its copy: it never overlaps the first section at any tier
//
// Anything that loads waits on the wall clock (`UntilBackground`), never a frame budget. Every fact puts Classic back alone (`ZuneHost.Mounted`).

using FluentGpu.Dsl;
using Wavee;
using Wavee.Themes.Zune;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed class ZuneArtistMountedTests(ShellHost host)
{
    const float W = 1440f, H = 960f;

    static PageChrome? Chrome() => Sidebar.DockedPaneProps.Band!.Chrome.Peek();

    static void GoHome(Shell.HeadlessShell shell)
    {
        Shell.GoTo(new Shell.Route(Shell.RouteKind.Home));
        shell.RunFrames(10);
    }

    // ── the registry ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Zune_draws_the_artist_family()
    {
        var builder = new FluentGpu.Theming.ViewRegistryBuilder();
        ZuneStructure.RegisterOwn(builder);
        var registry = builder.Build();
        Assert.Equal("zune.artist.page", registry.TemplateIdOf(ViewKeys.ArtistPage.Id));
        Assert.Equal("zune.artist.lightbox", registry.TemplateIdOf(ViewKeys.ArtistLightbox.Id));
        Assert.Equal("zune.discography.page", registry.TemplateIdOf(ViewKeys.Discography.Id));
        Assert.True(registry.TryGetStyle(StyleKeys.Discography, out var style) && style is not null, "Zune registers the Discography style");
    }

    // ── the artist page ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_artist_page_renders_zunes_hero_popular_band_and_discography()
        => ZuneHost.Mounted(host, shell =>
        {
            shell.RunFrames(10);
            try
            {
                Shell.GoTo(Shell.For(EntityUri.Parse("spotify:artist:ar0"), "Artist"));
                UntilBackground(shell, () => !shell.Find(ZuneArtistRules.HeroKey).IsNull, "Zune's artist hero");
                Assert.Equal("zune.artist.page", ThemeHost.Registry.TemplateIdOf(ViewKeys.ArtistPage.Id));
                Assert.True(shell.FindPrefix("artist-page:").Count == 0, "Classic's artist column is not drawn under Zune");
                Assert.Single(shell.FindPrefix(ZuneArtistRules.PagePrefix));

                // The hero stands at least at the tier record's height (it hugs a longer copy), inside the page.
                var scene = shell.Host.Scene;
                var lay = ZuneArtistTiers.For(WidthTier.Standard);
                var hero = scene.AbsoluteRect(Node(shell, ZuneArtistRules.HeroKey));
                Assert.True(hero.H >= lay.HeroH - 0.5f, $"the hero is at least the record's {lay.HeroH} tall ({hero})");
                Assert.False(shell.Find(ZuneArtistRules.NameKey).IsNull, "the panorama name");

                // The popular band (the chart and its rail) and the discography facets mount once the page has loaded.
                UntilBackground(shell, () => !shell.Find(ZuneArtistRules.PopularKey).IsNull, "the popular band");
                UntilBackground(shell, () => shell.FindPrefix(ZuneArtistRules.FacetHeadPrefix).Count > 0, "a discography facet's heading");
                Assert.False(shell.FindPrefix(ZuneArtistRules.DiscographyLinkPrefix).Count == 0, "the discography link ends the heading row");
            }
            finally { GoHome(shell); }
        }, W, H);

    [Fact]
    public void The_artist_publishes_its_bleed_and_its_row_two()
        => ZuneHost.Mounted(host, shell =>
        {
            shell.RunFrames(10);
            try
            {
                Shell.GoTo(Shell.For(EntityUri.Parse("spotify:artist:ar0"), "Artist"));
                UntilBackground(shell, () => Chrome() is { Backdrop.Bleed.Length: > 0 }, "the artist's header published as the bleed");
                UntilBackground(shell, () => Chrome() is { Views.Count: > 0 } && !shell.Find(ZuneBandRules.Row2Key).IsNull, "the pivot in the band's row 2");
                var chrome = Chrome()!;
                Assert.Contains(chrome.Actions, a => a.Kind == PageActionKind.Play);
                Assert.Contains(chrome.Actions, a => a.Kind == PageActionKind.Shuffle);
                Assert.Contains(chrome.Actions, a => a.Id == "follow");
                Until(shell, () => shell.FindPrefix(ZuneBandRules.ViewKeyPrefix).Count == Chrome()!.Views.Count, "every pivot word drawn in row 2");
            }
            finally { GoHome(shell); }
        }, W, H);

    // ── the tuning (lane T2) ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_artist_records_give_each_verb_one_home_and_the_page_the_system_column()
    {
        Assert.Equal(ZuneArtistActions.BelowHero, ZuneArtistTiers.For(WidthTier.Compact).Actions);
        Assert.Equal(ZuneArtistSecondary.None, ZuneArtistTiers.For(WidthTier.Compact).Secondary);
        foreach (var tier in new[] { WidthTier.Small, WidthTier.Standard, WidthTier.Huge, WidthTier.Huge4K })
        {
            var lay = ZuneArtistTiers.For(tier);
            Assert.Equal(ZuneArtistActions.Band, lay.Actions);
            Assert.True(ZuneBandRules.Band(tier).TitleAndActions, $"{tier}: the band carries play, shuffle and follow");
            // A1: the page column is the system's, 2400 between the gutters, at every tier.
            Assert.Equal(ZuneFrameRules.PageCap + 2f * lay.Gutter, lay.PageMax);
        }
        Assert.Equal(ZuneArtistSecondary.Corner, ZuneArtistTiers.For(WidthTier.Small).Secondary);
        Assert.Equal(ZuneArtistSecondary.Row, ZuneArtistTiers.For(WidthTier.Huge).Secondary);
        Assert.Equal(ZuneArtistSecondary.Row, ZuneArtistTiers.For(WidthTier.Huge4K).Secondary);
        Assert.Equal(1200f, ZuneArtistTiers.For(WidthTier.Huge).ChartMaxW);
        // Huge 4K draws the board's chart beside its 440 rail on the capped column, so it carries no cap of its own (ZT6).
        Assert.Equal(0f, ZuneArtistTiers.For(WidthTier.Huge4K).ChartMaxW);
        Assert.Equal(520f, ZuneArtistTiers.For(WidthTier.Huge4K).HeroH);
        Assert.Equal(0f, ZuneArtistTiers.For(WidthTier.Standard).ChartMaxW);
    }

    [Theory]
    [InlineData(400f)]
    [InlineData(800f)]
    [InlineData(1440f)]
    [InlineData(2560f)]
    public void The_artist_hero_never_overlaps_the_first_section_and_draws_no_band_verb_from_small_up(float width)
        => ZuneHost.Mounted(host, shell =>
        {
            shell.RunFrames(10);
            try
            {
                Shell.GoTo(Shell.For(EntityUri.Parse("spotify:artist:ar0"), "Artist"));
                UntilBackground(shell, () => !shell.Find(ZuneArtistRules.HeroKey).IsNull, "Zune's artist hero");
                UntilBackground(shell, () => !shell.Find(ZuneArtistRules.PopularKey).IsNull, "the popular band");
                shell.RunFrames(60);
                var scene = shell.Host.Scene;
                var hero = scene.AbsoluteRect(Node(shell, ZuneArtistRules.HeroKey));
                var popular = scene.AbsoluteRect(Node(shell, ZuneArtistRules.PopularKey));
                Assert.True(hero.Y + hero.H <= popular.Y + 0.5f, $"the hero ({hero}) ends above the first section ({popular})");
                var caption = scene.AbsoluteRect(Node(shell, ZuneArtistRules.CaptionKey));
                Assert.True(caption.Y + caption.H <= hero.Y + hero.H + 0.5f, $"the caption ({caption}) stays inside the hero ({hero})");
                if (width >= 640f)
                {
                    // The band's row 2 carries play, shuffle and follow: the hero draws none of them (only its own radio and "...").
                    Assert.True(shell.Find(ZuneArtistRules.ActionsKey).IsNull, "no primary action row in the hero");
                    Assert.True(shell.Find(ZuneArtistRules.ActionsKey + ":below").IsNull, "no Compact row");
                    Assert.Empty(shell.FindPrefix("zune-artist-follow:"));
                }
            }
            finally { GoHome(shell); }
        }, width, H);

    // ── the discography page ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_discography_page_mounts_zunes_column_and_its_release_type_words()
        => ZuneHost.Mounted(host, shell =>
        {
            shell.RunFrames(10);
            try
            {
                Shell.GoTo(Shell.Parse("disco:0:spotify:artist:ar0"));
                UntilBackground(shell, () => !shell.Find(ZuneArtistRules.DiscoPageKey).IsNull, "Zune's discography page");
                Assert.Equal("zune.discography.page", ThemeHost.Registry.TemplateIdOf(ViewKeys.Discography.Id));
                Assert.True(shell.Find("discography-page").IsNull, "Classic's discography column is not drawn under Zune");
                Assert.Equal(3, shell.FindPrefix("zune-discography-tab:").Count);
                Assert.False(shell.Find(ZuneArtistRules.DiscoHeadKey).IsNull, "the head: the artist crumb, the title and the words");
            }
            finally { GoHome(shell); }
        }, W, H);
}
