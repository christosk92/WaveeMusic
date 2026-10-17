// ── Wavee.ShellTests/ZuneDetailMountedTests.cs — Zune's detail frame and album, mounted under Zune (themes plan 6.3; P5 Z6a1) ─────────────────
//
// The app mounted under Zune (`ZuneHost`) against the fake dataset (al2: the rich album, six "more by", two billed artists):
//
//   registered   Zune's own registration draws every detail-frame and album key (zune.*), and the hero's loading band is Zune's style
//   frame        at 1440 x 960 an album draws Zune's frame (the panorama band, the identity column) with Zune's track table under its "songs" heading
//   row 2        the album publishes its row 2: "songs" (current) and "more by <artist>" as views, play / shuffle / save as actions, and the band draws them
//   more by      the album's "more by" stack mounts (in the aside where the content tier docks one, else under the rows)
//   hero         at 400 x 860 (Compact) the vertical arm draws Zune's hero (the panorama, the cover row, the ring row)
//   ladders      the detail ladders Zune's policy names: the vertical arm below 640, the 24 hold, the record per content tier
//
// Anything that loads waits on the wall clock (`UntilBackground`), never a frame budget. Every fact puts Classic back alone (`ZuneHost.Mounted`).

using FluentGpu.Foundation;
using Wavee;
using Wavee.Themes.Zune;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;
using static Wavee.ShellTests.ShellProbe;
using FluentGpu.Theming;

namespace Wavee.ShellTests;

public sealed class ZuneDetailMountedTests(ShellHost host)
{
    const float W = 1440f, H = 960f;

    static readonly Shell.Route s_album = Shell.For(EntityUri.Parse("spotify:album:al2"), "Album");

    const string ListHeaderKey = "zune.list.header", RowDropKey = "zune.row.drop";

    static PageChrome? Chrome() => Sidebar.DockedPaneProps.Band!.Chrome.Peek();

    static void OpenAlbum(Shell.HeadlessShell shell)
    {
        Shell.GoTo(s_album);
        UntilBackground(shell, () => !shell.Find(ZuneDetailRules.FrameKey).IsNull, "Zune's detail frame on al2");
    }

    // ── the registry ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Zune_draws_every_detail_frame_and_album_key()
    {
        var builder = new FluentGpu.Theming.ViewRegistryBuilder();
        ZuneStructure.RegisterOwn(builder);
        var registry = builder.Build();
        string[] ids =
        [
            ViewKeys.DetailFrame.Id, ViewKeys.DetailRail.Id, ViewKeys.DetailCompactRail.Id, ViewKeys.DetailShowHeader.Id, ViewKeys.DetailHero.Id,
            ViewKeys.DetailInsights.Id, ViewKeys.DetailInsightsToggle.Id, ViewKeys.DetailNotice.Id, ViewKeys.DetailVerticalTrailing.Id,
            ViewKeys.AlbumArtistLine.Id, ViewKeys.AlbumArtistFlyout.Id, ViewKeys.AlbumCountdown.Id, ViewKeys.AlbumReleasePanel.Id,
            ViewKeys.AlbumTrailing.Id, ViewKeys.AlbumSections.Id, ViewKeys.AlbumAside.Id, ViewKeys.AlbumTrailStack.Id, ViewKeys.AlbumUnresolved.Id,
        ];
        var templates = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in ids)
        {
            string template = registry.TemplateIdOf(id) ?? "";
            Assert.True(template.StartsWith("zune.", StringComparison.Ordinal), $"{id} is drawn by '{template}'");
            Assert.True(templates.Add(template), "template id registered twice: " + template);
        }
        Assert.True(registry.TryGetStyle(StyleKeys.DetailHero, out var hero));
        var own = new ViewRegistryBuilder();   // Zune's own registration (ZuneDetailHero is internal to the app)
        ZuneStructure.RegisterOwn(own);
        Assert.True(own.Build().TryGetStyle(StyleKeys.DetailHero, out var zuneHero));
        Assert.Same(zuneHero, hero);
    }

    // ── the ladders and the records (pure) ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Zunes_policy_names_the_detail_ladders_with_the_vertical_arm_below_640()
    {
        var policy = ZuneTiers.Policy;
        Assert.True(policy.TryGetLadder(DetailLadders.Mode, out _), "the mode ladder");
        Assert.True(policy.TryGetLadder(DetailLadders.Table, out _), "the table ladder");
        Assert.True(policy.TryGetLadder(DetailLadders.Height, out _), "the height ladder");

        // A fresh resolve uses the line; a narrowing one holds 24 below it.
        Assert.Equal(Detail.Breakpoints.VerticalMode, ZuneDetailRules.ModeFor(639f, 0, initialized: false));
        Assert.NotEqual(Detail.Breakpoints.VerticalMode, ZuneDetailRules.ModeFor(640f, 0, initialized: false));
        int wide = ZuneDetailRules.ModeFor(1200f, 0, initialized: false);
        Assert.NotEqual(Detail.Breakpoints.VerticalMode, ZuneDetailRules.ModeFor(620f, ZuneDetailRules.ModeFor(700f, wide)));
        Assert.Equal(Detail.Breakpoints.VerticalMode, ZuneDetailRules.ModeFor(615f, ZuneDetailRules.ModeFor(700f, wide)));

        // One record per content tier; the identity column widens and the aside docks from Standard; no tier squeezes the list under its floor.
        Assert.Equal(0f, ZuneDetailTiers.For(WidthTier.Compact).RailW);
        Assert.Equal(160f, ZuneDetailTiers.For(WidthTier.Small).RailW);
        Assert.Equal(240f, ZuneDetailTiers.For(WidthTier.Standard).RailW);
        Assert.Equal(260f, ZuneDetailTiers.For(WidthTier.Standard).AsideW);
        Assert.Equal(0f, ZuneDetailTiers.For(WidthTier.Small).AsideW);
        Assert.True(ZuneDetailTiers.For(WidthTier.Compact).RingSize >= 40f, "Compact rings are 40 targets");
        Assert.True(ZuneListRules.DetailTiers.For(WidthTier.Standard).AsideDocked, "the core docks the aside from Standard");
        Assert.False(ZuneListRules.DetailTiers.For(WidthTier.Small).AsideDocked);
    }

    // ── the frame, the table, row 2 and "more by" ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void An_album_draws_zunes_frame_with_zunes_track_table()
        => ZuneHost.Mounted(host, shell =>
        {
            shell.RunFrames(10);
            OpenAlbum(shell);
            Assert.Equal("zune.detail.frame", ThemeHost.Registry.TemplateIdOf(ViewKeys.DetailFrame.Id));
            Assert.Equal("zune.detail.hero", ThemeHost.Registry.TemplateIdOf(ViewKeys.DetailHero.Id));
            // Classic's two-column scaffold is not drawn under Zune.
            Assert.True(shell.Find("detail:two-column").IsNull, "Classic's two-column page is not drawn");
            Assert.False(shell.Find(ZuneDetailRules.PanoramaKey).IsNull, "the panorama band");
            UntilBackground(shell, () => !shell.Find(ZuneDetailRules.RailKey).IsNull, "the identity column");
            Assert.False(shell.Find(ZuneDetailRules.SongsHeadingKey).IsNull, "the songs heading over the list");
            UntilBackground(shell, () => shell.FindPrefix(RowDropKey).Count > 0 && !shell.Find(ListHeaderKey).IsNull, "Zune's track table on al2");

            // The page stays inside the content card: no horizontal overflow of the frame.
            var scene = shell.Host.Scene;
            var frame = scene.AbsoluteRect(Node(shell, ZuneDetailRules.FrameKey));
            var content = scene.AbsoluteRect(Node(shell, ZuneFrameLayout.ContentKey));
            Assert.True(frame.X >= content.X - 0.5f && frame.X + frame.W <= content.X + content.W + 0.5f, $"the frame fits the page ({frame} in {content})");
        }, W, H);

    [Fact]
    public void The_album_publishes_its_row_two_and_the_band_draws_its_views_and_actions()
        => ZuneHost.Mounted(host, shell =>
        {
            shell.RunFrames(10);
            OpenAlbum(shell);
            UntilBackground(shell, () => Chrome() is { Views.Count: > 1 }, "al2's row 2 with its \"more by\" view");
            var chrome = Chrome()!;
            Assert.Equal("songs", chrome.CurrentView?.Id);
            Assert.Contains(chrome.Views, v => v.Id == "moreby");
            Assert.Contains(chrome.Actions, a => a.Kind == PageActionKind.Play);
            Assert.Contains(chrome.Actions, a => a.Kind == PageActionKind.Shuffle);

            UntilBackground(shell, () => !shell.Find(ZuneBandRules.Row2Key).IsNull, "the band's row 2");
            Assert.False(shell.Find(ZuneBandRules.ViewKeyPrefix + "songs").IsNull, "the songs view in the band");
            Assert.False(shell.Find(ZuneBandRules.ViewKeyPrefix + "moreby").IsNull, "the more-by view in the band");
            Assert.False(shell.Find(ZuneBandRules.ActionKeyPrefix + "play").IsNull, "the play action in the band");
            Assert.False(shell.Find(ZuneBandRules.Row2TitleKey).IsNull, "the album's title leads row 2");
        }, W, H);

    [Fact]
    public void The_albums_more_by_stack_mounts()
        => ZuneHost.Mounted(host, shell =>
        {
            shell.RunFrames(10);
            OpenAlbum(shell);
            UntilBackground(shell, () => shell.FindPrefix(ZuneDetailRules.StackKey).Count > 0, "al2's \"more by\" stack");
        }, W, H);

    // ── the vertical arm ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_compact_window_draws_zunes_vertical_hero()
        => ZuneHost.Mounted(host, shell =>
        {
            shell.RunFrames(10);
            Shell.GoTo(s_album);
            UntilBackground(shell, () => !shell.Find(ZuneDetailRules.HeroKey).IsNull, "Zune's vertical hero on al2 at Compact");
            Assert.True(shell.Find(ZuneDetailRules.RailKey).IsNull, "no identity column in the vertical arm");
            Assert.False(shell.Find(ZuneDetailRules.PanoramaKey).IsNull, "the hero's panorama");
            UntilBackground(shell, () => !shell.Find(ZuneDetailRules.RingPrefix + "play").IsNull, "the hero's play ring");
            var ring = shell.Host.Scene.AbsoluteRect(Node(shell, ZuneDetailRules.RingPrefix + "play"));
            Assert.True(ring.H >= 40f - 0.5f, $"the Compact play ring is a 40 target ({ring})");
        }, 400f, 860f);
}
