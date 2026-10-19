// ── Wavee.ShellTests/ZuneBandMountedTests.cs — Zune's band and material, mounted (themes plan 6.3; P5 Z2b) ────────────────────────────
//
// The app under Zune at 1440 x 960 (Standard) and at 800 x 960 (Small), and the band alone in the view harness, on the facts and the records,
// never on pixels:
//
//   · the registry's templates for the band's keys are Zune's (`zune.*`), the nav pane's heads, flyouts and edit outline are Nothing;
//   · row 1 shows home, browse, library and recents in order, the first on the gutter, with home lit on Home;
//   · a click on "library" navigates (to Liked, the library's first view) and lights library;
//   · seeded pins show, capped at six, at Standard; none at Small (P5 decision 4);
//   · row 2 shows the Library kinds on a library route (the band 84 tall) and nothing on Home (52);
//   · the band collapses to 52 while the page scrolls down, easing (never a jump), and comes back on the way up.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Pal;
using FluentGpu.Signals;
using Wavee;
using Wavee.Themes.Common;
using Wavee.Themes.Zune;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed class ZuneBandMountedTests(ShellHost host)
{
    const float W = 1440f, H = 960f, SmallW = 800f;

    static readonly string[] Pivots = ["home", "browse", "library", "recents"];

    static void Click(Shell.HeadlessShell shell, NodeHandle node)
    {
        var r = shell.Host.Scene.AbsoluteRect(node);
        var p = new Point2(r.X + r.W / 2f, r.Y + r.H / 2f);
        shell.Window.QueueInput(new InputEvent(InputKind.PointerMove, p, 0, 0, TimestampMs: 2000));
        shell.Window.QueueInput(new InputEvent(InputKind.PointerDown, p, 0, 0, TimestampMs: 2010));
        shell.Window.QueueInput(new InputEvent(InputKind.PointerUp, p, 0, 0, TimestampMs: 2060));
        shell.RunFrames(3);
    }

    static float BandH(Shell.HeadlessShell shell) => shell.Host.Scene.AbsoluteRect(Node(shell, ZuneBandRules.BandKey)).H;

    /// <summary>Pins <paramref name="count"/> albums (the ZK mounted tests' seed) and returns the ids it pinned, to unpin.</summary>
    static List<string> SeedPins(int count)
    {
        var pinned = new List<string>();
        for (int i = 0; i < count; i++)
        {
            string uri = "spotify:album:z2bband" + i;
            string id = SidebarPinId.AlbumPrefix + uri;
            if (Sidebar.IsPinned(id)) continue;
            Assert.True(Sidebar.Model.Pin(id, SidebarEntryKind.Album, uri, "Band " + i), "pinned " + id);
            pinned.Add(id);
        }
        return pinned;
    }

    static void Unpin(Shell.HeadlessShell shell, List<string> pinned)
    {
        foreach (string id in pinned) Sidebar.Model.Unpin(id);
        shell.RunFrames(10);
    }

    // ── the registry ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Zune_registers_the_band_the_material_and_nothing_for_the_nav_panes_parts()
    {
        var builder = new FluentGpu.Theming.ViewRegistryBuilder();
        ZuneStructure.RegisterOwn(builder);
        var registry = builder.Build();
        Assert.Equal("zune.shell.band", registry.TemplateIdOf(ViewKeys.ShellNavPane.Id));
        foreach (string id in new[] { ViewKeys.ShellMaterialLayer.Id, ViewKeys.ShellCoverTone.Id, ViewKeys.ShellCoverBlend.Id, ViewKeys.ShellCoverVeil.Id, ViewKeys.ShellCoverTint.Id })
            Assert.True(registry.TemplateIdOf(id)?.StartsWith("zune.", StringComparison.Ordinal) == true, $"{id} is {registry.TemplateIdOf(id)}");
        foreach (string id in new[] { ViewKeys.ShellNavLibraryHead.Id, ViewKeys.ShellNavEmpty.Id, ViewKeys.ShellNavSectionFlyout.Id, ViewKeys.ShellNavFolderFlyout.Id, ViewKeys.ShellNavEdit.Id })
            Assert.Equal(CommonNothing.IdFor(id), registry.TemplateIdOf(id));
    }

    // ── the rules ────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_band_records_follow_the_board()
    {
        var standard = ZuneBandRules.Band(WidthTier.Standard);
        Assert.Equal(84f, standard.ExpandedH);
        Assert.Equal(52f, standard.CollapsedH);
        Assert.Equal(ZuneTiers.Frame(WidthTier.Standard).BandH, standard.ExpandedH);
        Assert.Equal(ZuneTiers.Frame(WidthTier.Standard).BandCollapsedH, standard.CollapsedH);
        Assert.Equal(92f, ZuneBandRules.Band(WidthTier.Compact).ExpandedH);
        // Pins by window tier (P5 decision 4).
        Assert.False(ZuneBandRules.Band(WidthTier.Compact).Pins);
        Assert.False(ZuneBandRules.Band(WidthTier.Small).Pins);
        Assert.True(ZuneBandRules.Band(WidthTier.Standard).Pins);
        Assert.True(ZuneBandRules.Band(WidthTier.Huge).Pins);
        Assert.True(ZuneBandRules.Band(WidthTier.Huge4K).Pins);
        Assert.False(ZuneBandRules.ShowPins(standard, option: false));
        // The inset is the frame's gutter at every tier.
        foreach (var tier in new[] { WidthTier.Compact, WidthTier.Small, WidthTier.Standard, WidthTier.Huge, WidthTier.Huge4K })
            Assert.Equal(ZuneTiers.Frame(tier).Gutter, ZuneBandRules.Band(tier).Inset);
        // Compact rotates the lit pivot to the front; the keys walk a tab list.
        Assert.Equal(new[] { 2, 3, 0, 1 }, ZuneBandRules.PivotOrder(4, 2, rotate: true));
        Assert.Equal(new[] { 0, 1, 2, 3 }, ZuneBandRules.PivotOrder(4, 2, rotate: false));
        Assert.Equal(new[] { 0, 1, 2, 3 }, ZuneBandRules.PivotOrder(4, -1, rotate: true));
        Assert.Equal(1, ZuneBandRules.Step(2, 4, ZuneBandRules.TabKey.Previous));
        Assert.Equal(-1, ZuneBandRules.Step(3, 4, ZuneBandRules.TabKey.Next));
        Assert.Equal(0, ZuneBandRules.Step(2, 4, ZuneBandRules.TabKey.First));
        Assert.Equal(3, ZuneBandRules.Step(0, 4, ZuneBandRules.TabKey.Last));
        Assert.Equal(-1, ZuneBandRules.Step(0, 4, ZuneBandRules.TabKey.First));
        // Row 2 on scroll (owner decision 2026-10-20): scroll-tied at Compact only, pinned from Small up; the fold is a pure function of the offset.
        Assert.True(ZuneBandRules.Band(WidthTier.Compact).Collapses);
        foreach (var tier in new[] { WidthTier.Small, WidthTier.Standard, WidthTier.Huge, WidthTier.Huge4K })
            Assert.False(ZuneBandRules.Band(tier).Collapses);
        Assert.Equal(0f, ZuneBandRules.TiedFold(0f, 40f));
        Assert.Equal(0f, ZuneBandRules.TiedFold(-12f, 40f));     // an overscroll above the top
        Assert.Equal(25f, ZuneBandRules.TiedFold(25f, 40f));
        Assert.Equal(40f, ZuneBandRules.TiedFold(400f, 40f));
        Assert.Equal(0f, ZuneBandRules.TiedFold(float.NaN, 40f));
    }

    // ── row 1 ────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Row_one_shows_the_four_pivots_in_order_with_home_lit_and_a_click_on_library_navigates()
        => ZuneHost.Mounted(host, shell =>
        {
            Shell.GoTo(new Shell.Route(Shell.RouteKind.Home));
            Until(shell, () => !shell.Find(ZuneBandRules.BandKey).IsNull, "the band under Zune");
            shell.RunFrames(20);
            Assert.Equal("zune.shell.band", ThemeHost.Registry.TemplateIdOf(ViewKeys.ShellNavPane.Id));
            var band = Sidebar.DockedPaneProps.Band!;
            Assert.Equal(Shell.BandRules.Home, band.Current());

            var scene = shell.Host.Scene;
            var slot = scene.AbsoluteRect(Node(shell, ZuneFrameRules.BandSlotKey));
            var root = scene.AbsoluteRect(Node(shell, ZuneBandRules.BandKey));
            Assert.Equal(slot.Y, root.Y, 0.5f);
            Assert.Equal(slot.H, root.H, 0.5f);   // the slot hugs the band

            float lastX = float.NegativeInfinity;
            foreach (string id in Pivots)
            {
                var r = scene.AbsoluteRect(Node(shell, ZuneBandRules.PivotKeyPrefix + id));
                Assert.True(r.X > lastX, $"{id} follows the pivot before it ({r.X} after {lastX})");
                Assert.True(r.Y >= root.Y - 0.5f && r.Y + r.H <= root.Y + ZuneBandRules.Band(WidthTier.Standard).Row1H + 0.5f, $"{id} stands in row 1 ({r})");
                lastX = r.X;
            }
            // The first pivot stands on the page's gutter.
            Assert.Equal(ZuneTiers.Frame(WidthTier.Standard).Gutter, scene.AbsoluteRect(Node(shell, ZuneBandRules.PivotKeyPrefix + "home")).X, 1f);

            try
            {
                Click(shell, Node(shell, ZuneBandRules.PivotKeyPrefix + "library"));
                Until(shell, () => Shell.Current.Peek().Kind == Shell.RouteKind.Liked, "the library pivot navigated to the library");
                Assert.Equal(Shell.BandRules.Library, band.Current());
            }
            finally
            {
                Shell.GoTo(new Shell.Route(Shell.RouteKind.Home));
                shell.RunFrames(20);
            }
        }, W, H);

    // ── pins ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Pins_show_capped_at_six_at_standard()
        => ZuneHost.Mounted(host, shell =>
        {
            shell.RunFrames(20);
            var pinned = SeedPins(8);
            try
            {
                Until(shell, () => shell.FindPrefix(ZuneBandRules.PinKeyPrefix).Count == 6, "six pins in the band");
                shell.RunFrames(10);
                Assert.Equal(6, shell.FindPrefix(ZuneBandRules.PinKeyPrefix).Count);
                // The pins stand in row 1, right of the pivots.
                var scene = shell.Host.Scene;
                var recents = scene.AbsoluteRect(Node(shell, ZuneBandRules.PivotKeyPrefix + "recents"));
                var pins = scene.AbsoluteRect(Node(shell, ZuneBandRules.PinsKey));
                Assert.True(pins.X >= recents.X + recents.W, $"the pins follow the pivots ({pins} after {recents})");
                Assert.True(pins.X + pins.W <= W + 0.5f, $"the pins stay inside the window ({pins})");
            }
            finally { Unpin(shell, pinned); }
        }, W, H);

    [Fact]
    public void Pins_are_hidden_at_small()
        => ZuneHost.Mounted(host, shell =>
        {
            shell.RunFrames(20);
            var pinned = SeedPins(3);
            try
            {
                shell.RunFrames(30);
                Assert.Equal(WidthTier.Small, ThemeHost.Facts.Peek().Tiers.Window);
                Assert.False(shell.Find(ZuneBandRules.BandKey).IsNull, "the band is drawn at Small");
                Assert.True(Sidebar.DockedPaneProps.Band!.Pins().Count > 0, "the core still carries the pins");
                Assert.Empty(shell.FindPrefix(ZuneBandRules.PinKeyPrefix));
                Assert.True(shell.Find(ZuneBandRules.PinsKey).IsNull, "no pins cluster at Small");
                foreach (string id in Pivots) Assert.False(shell.Find(ZuneBandRules.PivotKeyPrefix + id).IsNull, $"{id} is drawn at Small");
            }
            finally { Unpin(shell, pinned); }
        }, SmallW, H);

    // ── row 2 ────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Row_two_shows_the_library_kinds_on_a_library_route_and_nothing_on_history()
        => ZuneHost.Mounted(host, shell =>
        {
            shell.RunFrames(20);
            var band = Sidebar.DockedPaneProps.Band!;
            var standard = ZuneBandRules.Band(WidthTier.Standard);
            try
            {
                Shell.GoTo(new Shell.Route(Shell.RouteKind.LibraryArtists));
                // The band shows the page on screen: wait for the swap to land (the leaving page's row 2 stands until then).
                Until(shell, () => Shell.Shown.Peek().Kind == Shell.RouteKind.LibraryArtists && !shell.Find(ZuneBandRules.Row2Key).IsNull, "row 2 on a library route");
                shell.RunFrames(4);
                var chrome = band.Chrome.Peek()!;
                Assert.True(chrome.Views.Count > 0, "the library route has views");
                Until(shell, () => shell.FindPrefix(ZuneBandRules.ViewKeyPrefix).Count == chrome.Views.Count, "the leaving row's words have faded out");   // the old row fades where it stands
                Assert.Equal(chrome.Views.Count, shell.FindPrefix(ZuneBandRules.ViewKeyPrefix).Count);
                Assert.False(shell.Find(ZuneBandRules.ViewKeyPrefix + chrome.CurrentView!.Id).IsNull, "the current kind is drawn");
                Assert.Equal(Shell.BandRules.Library, band.Current());
                Until(shell, () => MathF.Abs(BandH(shell) - standard.ExpandedH) < 0.5f, "the band at its full height with row 2");

                // Home publishes its facets as row 2 (Z5) and Recents its pivots (Z7); the history page publishes none.
                Shell.GoTo(new Shell.Route(Shell.RouteKind.History));
                Until(shell, () => shell.Find(ZuneBandRules.Row2Key).IsNull, "no row 2 on History");
                Until(shell, () => MathF.Abs(BandH(shell) - standard.CollapsedH) < 0.5f, "the band is row 1 alone on History");
                Assert.Empty(shell.FindPrefix(ZuneBandRules.ViewKeyPrefix));
            }
            finally
            {
                Shell.GoTo(new Shell.Route(Shell.RouteKind.Home));
                shell.RunFrames(10);
            }
        }, W, H);

    // ── the collapse ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(400f, 860f)]     // Compact: scroll-tied
    [InlineData(800f, 860f)]     // Small: pinned
    [InlineData(1440f, 960f)]    // Standard: pinned
    [InlineData(2560f, 1440f)]   // Huge: pinned
    public void Row_two_is_scroll_tied_at_compact_and_pinned_from_small_up(float width, float height)
    {
        var scroll = new Signal<PageScroll>(PageScroll.Top);
        var views = new PageView[]
        {
            new("liked", "Liked", Current: true, Go: static () => { }),
            new("albums", "Albums", Current: false, Go: static () => { }),
            new("artists", "Artists", Current: false, Go: static () => { }),
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
        ViewHarness.With(host, ViewKeys.ShellNavPane, props, harness =>
        {
            harness.RunFrames(10);
            var band = ZuneBandRules.BandOf(harness.Facts.Peek().Tiers);
            bool tied = band.Collapses;
            Assert.Equal(width < 640f, tied);
            float Height() => harness.AbsoluteBounds(harness.Node(ZuneBandRules.BandKey)).H;
            Assert.Equal(band.ExpandedH, Height(), 0.5f);
            Assert.True(harness.Contains(ZuneBandRules.Row2Key), "row 2 is drawn");

            // Down, a partial step, further, back up a little, then the top: the height follows the position (no direction, nothing timed).
            foreach (var (offset, dir) in new[] { (12f, PageScrollDirection.None), (24f, PageScrollDirection.Down), (240f, PageScrollDirection.Down),
                         (30f, PageScrollDirection.Up), (0f, PageScrollDirection.Up) })
            {
                scroll.Value = new PageScroll(offset, dir);
                harness.RunFrames(40);
                float want = band.ExpandedH - (tied ? ZuneBandRules.TiedFold(offset, band.Row2H) : 0f);
                Assert.True(MathF.Abs(want - Height()) < 0.5f, $"at offset {offset} the band stands {want} (it stands {Height()})");
                // Pinned: it stays put frame after frame (no fade that ends in a fold, no late return).
                harness.RunFrames(60);
                Assert.True(MathF.Abs(want - Height()) < 0.5f, $"at offset {offset} the band still stands {want} a second later (it stands {Height()})");
            }
            Assert.True(harness.Contains(ZuneBandRules.Row2Key), "row 2 is drawn at the top again");
        }, new ViewHarnessOptions { Composition = ZuneTheme.Create(), WidthDip = width, HeightDip = height });
    }
}
