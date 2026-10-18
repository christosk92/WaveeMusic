// ── Wavee.ShellTests/ZuneTierResizeTests.cs — Zune's tiers under a resize, and the band's tier rules alone (themes plan 3.3; P5 ZT1) ──
//
// The app under Zune resized across and inside its edges, and the band mounted alone at the sizes its tier rules turn on:
//
//   · a resize that crosses an edge keeps the old tier on the resized frame and commits the new one on the first frame that is not resized;
//     a resize that stays inside a hold (1440 -> 990, 2560 -> 1900, 3840 -> 3180, 960 -> 680 tall) crosses nothing at all;
//   · the band at a Short Huge window (2560 x 660) collapses to row 1 on the first scroll down and comes back on the way up, easing; at a
//     Regular Huge window it never collapses (Z-Shell-Short, Z-Frame-Huge);
//   · the band at Huge 4K sets its pivots at 40 / 48 and stands 112 tall with row 2, 72 collapsed (Z-Part-Band 4k).

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;
using Wavee;
using Wavee.Themes.Zune;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed class ZuneTierResizeTests(ShellHost host)
{
    static TierFacts Tiers => ThemeHost.Facts.Peek().Tiers;

    // ── the resize ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1440f, 960f, 990f, 960f, WidthTier.Standard, HeightTier.Regular)]    // inside the 1008 hold
    [InlineData(2560f, 1440f, 1900f, 1440f, WidthTier.Huge, HeightTier.Regular)]     // inside the 1920 hold
    [InlineData(3840f, 2160f, 3180f, 2160f, WidthTier.Huge4K, HeightTier.Regular)]   // inside the 3200 hold
    [InlineData(1440f, 960f, 1440f, 680f, WidthTier.Standard, HeightTier.Regular)]   // inside the 700 hold
    [InlineData(800f, 960f, 620f, 960f, WidthTier.Small, HeightTier.Regular)]        // inside the 640 hold
    public void A_resize_inside_a_hold_crosses_nothing(float fromW, float fromH, float toW, float toH, WidthTier window, HeightTier tall)
        => ZuneHost.Mounted(host, shell =>
        {
            shell.RunFrames(20);
            shell.Resize(toW, toH);
            shell.Host.Paint(0, keepAlive: true);
            shell.RunFrames(10);
            Assert.Equal(window, Tiers.Window);
            Assert.Equal(tall, Tiers.Height);
        }, fromW, fromH);

    [Theory]
    [InlineData(800f, 960f, 615f, 960f, WidthTier.Small, WidthTier.Compact, HeightTier.Regular, HeightTier.Regular)]
    [InlineData(800f, 960f, 1008f, 960f, WidthTier.Small, WidthTier.Standard, HeightTier.Regular, HeightTier.Regular)]
    [InlineData(1440f, 960f, 983f, 960f, WidthTier.Standard, WidthTier.Small, HeightTier.Regular, HeightTier.Regular)]
    [InlineData(1440f, 960f, 1920f, 960f, WidthTier.Standard, WidthTier.Huge, HeightTier.Regular, HeightTier.Regular)]
    [InlineData(2560f, 1440f, 1895f, 1440f, WidthTier.Huge, WidthTier.Standard, HeightTier.Regular, HeightTier.Regular)]
    [InlineData(2560f, 1440f, 3200f, 1440f, WidthTier.Huge, WidthTier.Huge4K, HeightTier.Regular, HeightTier.Regular)]
    [InlineData(3840f, 2160f, 3175f, 2160f, WidthTier.Huge4K, WidthTier.Huge, HeightTier.Regular, HeightTier.Regular)]
    [InlineData(1440f, 960f, 1440f, 675f, WidthTier.Standard, WidthTier.Standard, HeightTier.Regular, HeightTier.Short)]
    [InlineData(1440f, 600f, 1440f, 700f, WidthTier.Standard, WidthTier.Standard, HeightTier.Short, HeightTier.Regular)]
    public void A_crossing_commits_on_the_first_frame_that_is_not_resized(float fromW, float fromH, float toW, float toH,
        WidthTier oldWindow, WidthTier newWindow, HeightTier oldTall, HeightTier newTall)
        => ZuneHost.Mounted(host, shell =>
        {
            shell.RunFrames(20);
            Assert.Equal(oldWindow, Tiers.Window);
            Assert.Equal(oldTall, Tiers.Height);
            shell.Resize(toW, toH);
            shell.Host.Paint(0, keepAlive: true);          // the resized frame: the old tier at the new size
            Assert.Equal(oldWindow, Tiers.Window);
            Assert.Equal(oldTall, Tiers.Height);
            shell.Host.RunFrame();                         // the first frame that is not resized: the crossing commits
            Assert.Equal(newWindow, Tiers.Window);
            Assert.Equal(newTall, Tiers.Height);
            shell.RunFrames(40);
            Assert.Equal(newWindow, Tiers.Window);
            // The frame's records follow: the title bar settles at the new tier's height.
            Assert.Equal(ZuneTiers.Frame(newWindow).TitleBarH, shell.Host.Scene.AbsoluteRect(Node(shell, ZuneFrameLayout.TitleBarKey)).H, 0.5f);
        }, fromW, fromH);

    // ── the band alone ───────────────────────────────────────────────────────────────────────────────────────────────────

    static (NavPaneProps Props, Signal<PageScroll> Scroll) BandFixture()
    {
        var scroll = new Signal<PageScroll>(PageScroll.Top);
        var views = new PageView[]
        {
            new("liked", "Liked", Current: true, Go: static () => { }),
            new("albums", "Albums", Current: false, Go: static () => { }),
        };
        var chrome = new Signal<PageChrome?>(new PageChrome("Library", [new PageCrumb("Library", null)]) { Views = views });
        IReadOnlyList<BandDestination> destinations =
        [
            new("home", "Home", "", static () => { }), new("browse", "Browse", "", static () => { }),
            new("library", "Library", "", static () => { }), new("recents", "Recents", "", static () => { }),
        ];
        var props = new NavPaneProps
        {
            InDrawer = false,
            Mount = static _ => new BoxEl(),
            Band = new BandProps
            {
                Destinations = () => destinations,
                Current = static () => 2,
                Pins = static () => [],
                Chrome = chrome,
                Scroll = scroll,
                Hub = () => destinations,
            },
        };
        return (props, scroll);
    }

    [Theory]
    [InlineData(2560f, 660f, true)]     // Huge, Short: collapses on the first scroll down (Z-Shell-Short)
    [InlineData(800f, 330f, true)]      // Small, Short: the board's own window
    [InlineData(2560f, 1440f, false)]   // Huge, Regular: never (there is room)
    [InlineData(3840f, 2160f, false)]   // Huge 4K, Regular: never
    public void The_band_collapses_on_the_first_scroll_wherever_the_window_is_short(float width, float height, bool collapses)
    {
        var (props, scroll) = BandFixture();
        ViewHarness.With(host, ViewKeys.ShellNavPane, props, harness =>
        {
            harness.RunFrames(10);
            var band = ZuneBandRules.BandOf(harness.Facts.Peek().Tiers);
            float Height() => harness.AbsoluteBounds(harness.Node(ZuneBandRules.BandKey)).H;
            // A Short window rests with row 2 folded away (Z-Shell-Short); a Regular one rests expanded.
            Assert.Equal(collapses ? band.CollapsedH : band.ExpandedH, Height(), 0.5f);

            scroll.Value = new PageScroll(120f, PageScrollDirection.Down);
            harness.RunFrames(60);
            Assert.Equal(collapses ? band.CollapsedH : band.ExpandedH, Height(), 0.5f);

            scroll.Value = new PageScroll(60f, PageScrollDirection.Up);
            harness.RunFrames(60);
            Assert.Equal(band.ExpandedH, Height(), 0.5f);
        }, new ViewHarnessOptions { Composition = ZuneTheme.Create(), WidthDip = width, HeightDip = height });
    }

    [Fact]
    public void The_band_at_huge_4k_sets_its_pivots_at_40_48_and_stands_112_or_72()
    {
        var (props, scroll) = BandFixture();
        ViewHarness.With(host, ViewKeys.ShellNavPane, props, harness =>
        {
            harness.RunFrames(10);
            var tiers = harness.Facts.Peek().Tiers;
            Assert.Equal(WidthTier.Huge4K, tiers.Window);
            Assert.Equal(112f, harness.AbsoluteBounds(harness.Node(ZuneBandRules.BandKey)).H, 0.5f);
            Assert.Equal(64f, harness.AbsoluteBounds(harness.Node(ZuneBandRules.Row1Key)).H, 0.5f);
            // The first pivot stands on the page column's lead (T1's A1): the 2400 column centred in the band, 720 in at 3840.
            var bandRect = harness.AbsoluteBounds(harness.Node(ZuneBandRules.BandKey));
            Assert.Equal(bandRect.X + ZuneFrameRules.ColumnLead(bandRect.W, ZuneTiers.Frame(WidthTier.Huge4K).Gutter),
                harness.AbsoluteBounds(harness.Node(ZuneBandRules.PivotKeyPrefix + "home")).X, 1f);

            // The pivot words are set at the board's 40: the word's own text node is at least the 48 line tall.
            var pivot = harness.Node(ZuneBandRules.PivotKeyPrefix + "home");
            float tallest = 0f;
            foreach (var n in harness.Subtree(pivot)) if (!n.Equals(pivot)) tallest = MathF.Max(tallest, harness.AbsoluteBounds(n).H);
            Assert.True(tallest >= ZuneBandTiers.Type(WidthTier.Huge4K).PivotLine - 0.5f, $"the pivot's text is {tallest} tall, under the 48 line");

            // No row 2 on the page: row 1 alone, 72.
            ((Signal<PageChrome?>)props.Band!.Chrome).Value = new PageChrome("Recents", [new PageCrumb("Recents", null)]);
            harness.RunFrames(60);
            Assert.Equal(72f, harness.AbsoluteBounds(harness.Node(ZuneBandRules.BandKey)).H, 0.5f);
            _ = scroll;
        }, new ViewHarnessOptions { Composition = ZuneTheme.Create(), WidthDip = 3840f, HeightDip = 2160f });
    }
}
