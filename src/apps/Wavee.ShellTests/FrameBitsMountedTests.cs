// ── Wavee.ShellTests/FrameBitsMountedTests.cs — the frame's small views, mounted (themes plan 6.1, T4c) ───────────────────
//
// The masthead, the material and its cover leaves, the narrow drawer, the command palette, the history menu, the file-drop cue,
// the drag chip, the setup scrim and the frame-rate HUD are the structure's since T4c; the core hosts keep their state machines
// and decisions (`Wavee.Tests\FrameBitsTests.cs` pins the pure ones). What only a mounted tree can say:
//
//   registered      Classic registers every key, and the real shell draws the masthead and the material through them
//   masthead        the title row stands on the browse surfaces' frame; a band that is not live draws no head (a leaving band
//                   finishes its exit as an orphan, ClassicMotionMountedTests); a trail without a parent has no prefix
//   material        the tint node stays the SAME node across a change (the brush fades; nothing flashes), the washes are keyed on
//                   their artwork, and the tint binder leaf still claims the shell material through its port
//   drawer          the pane stays mounted, closed included, while the shell is narrow, and nothing is mounted otherwise; in the real
//                   shell Escape still closes the open overlay through the host's key-preview chain
//   palette         the card asks the session for the empty query first and draws every row it answers
//   small ones      the history menu draws one row per item, the HUD exists only while its rule holds, and the setup scrim fades on
//                   the edge and ticks nothing while the cover holds still

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Themes.Classic;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed class FrameBitsMountedTests(ShellHost host)
{
    static ViewHarnessOptions UnderClassic() => new() { Composition = ClassicTheme.Create() };

    /// <summary>Classic with its Surfaces option (M6: the colour washes) set to Off.</summary>
    static ViewHarnessOptions WashesOff() => new()
    {
        Composition = ClassicTheme.Create(),
        Options = new Dictionary<string, object> { ["structure.surfaces"] = SurfaceWash.Off },
    };

    static NodeHandle Root(ViewHarness harness) => harness.Shell.Host.Scene.Root;

    static int Count(ViewHarness harness) => CountUnder(harness.Shell, Root(harness));

    static int TrackedNodes(ViewHarness harness)
        => Subtree(harness.Shell.Host.Scene, Root(harness)).Count(harness.Shell.Host.Animation.HasTracks);

    // ── registered ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Classic_is_the_registered_template_of_every_frame_bit()
        => host.Run(() =>
        {
            (string Id, string Template)[] expected =
            [
                (ViewKeys.ShellMasthead.Id, "classic.shell.masthead"),
                (ViewKeys.PageHead.Id, "classic.page.head"),
                (ViewKeys.ShellMaterialLayer.Id, "classic.shell.material"),
                (ViewKeys.ShellCoverTone.Id, "classic.shell.material.page-tone"),
                (ViewKeys.ShellCoverBlend.Id, "classic.shell.material.artist-blend"),
                (ViewKeys.ShellCoverVeil.Id, "classic.shell.material.artist-veil"),
                (ViewKeys.ShellCoverTint.Id, "classic.shell.material.tint-binder"),
                (ViewKeys.ShellNarrowDrawer.Id, "classic.shell.narrow-drawer"),
                (ViewKeys.ShellCommandPalette.Id, "classic.shell.command-palette"),
                (ViewKeys.ShellHistoryMenu.Id, "classic.shell.history-menu"),
                (ViewKeys.ShellFileDrop.Id, "classic.shell.file-drop"),
                (ViewKeys.ShellDragChip.Id, "classic.shell.drag-chip"),
                (ViewKeys.ShellCoverScrim.Id, "classic.shell.cover-scrim"),
                (ViewKeys.ShellFps.Id, "common.fps"),
            ];
            foreach (var (id, template) in expected)
                Assert.Equal(template, ThemeHost.Registry.TemplateIdOf(id));
        });

    [Fact]
    public void The_real_shell_draws_the_material_and_the_masthead_through_the_registered_views()
        => host.Mounted(shell =>
        {
            shell.RunFrames(5);
            Assert.False(shell.Find("shell.material.tint").IsNull, "the material layer's tint stands under the chrome column");

            Shell.GoTo(new Shell.Route(Shell.RouteKind.Browse));
            Until(shell, () => !shell.Find("masthead-current").IsNull, "the masthead band's title");
            Shell.GoTo(new Shell.Route(Shell.RouteKind.Home));
        });

    // ── the masthead ────────────────────────────────────────────────────────────────────────────────────────────────────────

    static PageChrome Chrome(params string[] trail)
    {
        var crumbs = new PageCrumb[trail.Length];
        for (int i = 0; i < crumbs.Length; i++)
            crumbs[i] = new PageCrumb(trail[i], i < crumbs.Length - 1 ? static () => { } : null);
        return new PageChrome(trail[^1], crumbs);
    }

    [Fact]
    public void The_masthead_draws_the_trail_as_a_title_row_on_the_browse_surfaces_frame()
        => ViewHarness.With(host, ViewKeys.ShellMasthead, new MastheadProps { Held = Chrome("Browse", "Pop"), Live = true },
            harness =>
            {
                var prefix = harness.AbsoluteBounds(harness.Node("masthead-prefix:Browse"));
                Assert.Equal(36f, prefix.X, 0.5f);   // the band's frame: the page's own column
                Assert.True(prefix.Y >= 32f, "the band's top inset sits above the title row");
                var current = harness.AbsoluteBounds(harness.Node("masthead-current"));
                Assert.True(current.X > prefix.X + prefix.W - 0.5f, "the current title follows its parent crumbs");
            }, UnderClassic());

    [Fact]
    public void A_trail_without_a_parent_has_no_prefix_and_the_title_starts_on_the_frame()
        => ViewHarness.With(host, ViewKeys.ShellMasthead, new MastheadProps { Held = Chrome("Browse"), Live = true },
            harness =>
            {
                Assert.Empty(harness.FindPrefix("masthead-prefix:"));
                Assert.Equal(36f, harness.AbsoluteBounds(harness.Node("masthead-current")).X, 0.5f);
            }, UnderClassic());

    [Fact]
    public void A_band_that_is_not_live_draws_no_head()
        => ViewHarness.With(host, ViewKeys.ShellMasthead, new MastheadProps { Held = Chrome("Browse", "Pop"), Live = false },
            harness => Assert.False(harness.Contains("masthead-current"), "the head leaves with its page (tuning F7)"),
            UnderClassic());

    [Fact]
    public void A_chrome_that_was_never_shown_draws_no_title()
        => ViewHarness.With(host, ViewKeys.ShellMasthead, new MastheadProps(),
            harness => Assert.False(harness.Contains("masthead-current")), UnderClassic());

    [Fact]
    public void The_show_all_tool_is_drawn_only_when_the_page_offers_it()
    {
        int without = 0, with = 0;
        var trail = Chrome("Browse", "Pop");
        ViewHarness.With(host, ViewKeys.ShellMasthead, new MastheadProps { Held = trail, Live = true },
            harness => without = Count(harness), UnderClassic());
        ViewHarness.With(host, ViewKeys.ShellMasthead, new MastheadProps { Held = trail with { ToolsVisible = true }, Live = true },
            harness => with = Count(harness), UnderClassic());
        Assert.True(with > without, $"the button is mounted beside the title row: {without} -> {with}");
    }

    // ── the material ────────────────────────────────────────────────────────────────────────────────────────────────────────

    static readonly ColorF Tint = new(0.2f, 0.3f, 0.8f, 0.14f);
    static readonly ColorF OtherTint = new(0.8f, 0.3f, 0.2f, 0.14f);

    [Fact]
    public void The_tint_node_stays_the_same_node_when_the_material_changes_so_nothing_flashes()
    {
        var state = new Signal<ShellMaterialState>(default);
        ViewHarness.With(host, ViewKeys.ShellMaterialLayer, new MaterialProps { State = state },
            harness =>
            {
                var tint = harness.Node("shell.material.tint");   // mounted even at the neutral ground: the brush has a colour to fade FROM

                state.Value = new ShellMaterialState(new object(), Tint, null);
                harness.RunFrames(5);
                Assert.Equal(tint, harness.Node("shell.material.tint"));

                state.Value = new ShellMaterialState(new object(), OtherTint, null);
                harness.RunFrames(5);
                Assert.Equal(tint, harness.Node("shell.material.tint"));
            }, UnderClassic());
    }

    [Fact]
    public void A_wash_is_keyed_on_its_artwork_so_a_new_cover_cross_fades_by_mount()
    {
        var state = new Signal<ShellMaterialState>(new ShellMaterialState(new object(), null,
            new HomeWash(new WashLayer(Tint, "art-1"), null, null)));
        ViewHarness.With(host, ViewKeys.ShellMaterialLayer, new MaterialProps { State = state },
            harness =>
            {
                Assert.True(harness.Contains("shell.wash.hero:art-1"));
                Assert.Empty(harness.FindPrefix("shell.wash.weekly"));   // a leg the page did not publish is not drawn

                state.Value = new ShellMaterialState(new object(), null, new HomeWash(new WashLayer(OtherTint, "art-2"), null, null));
                harness.RunFrames(60);   // the old leg's exit has run
                Assert.True(harness.Contains("shell.wash.hero:art-2"));
                Assert.False(harness.Contains("shell.wash.hero:art-1"));
            }, UnderClassic());
    }

    [Fact]
    public void The_tint_binder_leaf_claims_the_shell_material_through_its_port()
    {
        var slot = new Signal<ShellMaterialState>(default);
        var owner = new object();
        ViewHarness.With(host, ViewKeys.ShellCoverTint,
            new CoverTintProps(null, null, Ready: true, Apply: false, owner, MaterialPort.Of(slot)),
            harness =>
            {
                harness.RunFrames(3);
                Assert.Same(owner, slot.Peek().Owner);   // a page that decided it has no colour claims the neutral ground
                Assert.Null(slot.Peek().Tint);
            }, UnderClassic());
    }

    [Fact]
    public void Surfaces_off_makes_the_tint_binder_claim_the_neutral_ground_like_a_page_with_no_tint()
    {
        var slot = new Signal<ShellMaterialState>(default);
        var owner = new object();
        ViewHarness.With(host, ViewKeys.ShellCoverTint,
            new CoverTintProps(null, null, Ready: true, Apply: true, owner, MaterialPort.Of(slot), PayloadAccent: 0xFF336699u),
            harness =>
            {
                harness.RunFrames(3);
                Assert.Same(owner, slot.Peek().Owner);
                Assert.Null(slot.Peek().Tint);   // the payload accent would tint it were the washes on
            }, WashesOff());
        var onSlot = new Signal<ShellMaterialState>(default);
        ViewHarness.With(host, ViewKeys.ShellCoverTint,
            new CoverTintProps(null, null, Ready: true, Apply: true, owner, MaterialPort.Of(onSlot), PayloadAccent: 0xFF336699u),
            harness =>
            {
                harness.RunFrames(3);
                Assert.NotNull(onSlot.Peek().Tint);   // Subtle (the default) draws today's tint
            }, UnderClassic());
    }

    [Fact]
    public void Surfaces_off_draws_the_neutral_ground_whatever_the_pages_published()
    {
        var published = new ShellMaterialState(new object(), Tint, new HomeWash(new WashLayer(OtherTint, "art-1"), null, null));
        ViewHarness.With(host, ViewKeys.ShellMaterialLayer, new MaterialProps { State = new Signal<ShellMaterialState>(published) },
            harness =>
            {
                Assert.Empty(harness.FindPrefix("shell.wash."));
                Assert.True(harness.Contains("shell.material.tint"));   // the node stays mounted: it is what fades to neutral
            }, WashesOff());
        ViewHarness.With(host, ViewKeys.ShellMaterialLayer, new MaterialProps { State = new Signal<ShellMaterialState>(published) },
            harness => Assert.True(harness.Contains("shell.wash.hero:art-1")), UnderClassic());
    }

    // ── the drawer ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    static NarrowDrawerParts Parts(Signal<bool> open) => new()
    {
        IsOpen = () => open.Value,
        IsPinned = static () => false,
        Width = static viewportW => MathF.Min(320f, viewportW),
        WidthNow = static viewportW => MathF.Min(320f, viewportW),
        RestingOpacity = static isOpen => isOpen ? 1f : 0f,
        RestingTranslateX = static (isOpen, width) => isOpen ? 0f : -width,
        Close = () => open.Value = false,
        PaneBody = static () => new BoxEl { Key = "test:pane-body", Grow = 1f },
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_mounted_drawer_keeps_its_pane_mounted_closed_or_open(bool isOpen)
        => ViewHarness.With(host, ViewKeys.ShellNarrowDrawer,
            new NarrowDrawerProps { Mounted = true, Open = isOpen, Parts = Parts(new Signal<bool>(isOpen)) },
            harness => Assert.Equal(320f, harness.AbsoluteBounds(harness.Node("test:pane-body")).W, 0.5f),
            UnderClassic());

    [Fact]
    public void A_drawer_that_is_not_mounted_draws_no_pane()
        => ViewHarness.With(host, ViewKeys.ShellNarrowDrawer,
            new NarrowDrawerProps { Mounted = false, Open = false, Parts = Parts(new Signal<bool>(false)) },
            harness => Assert.False(harness.Contains("test:pane-body")), UnderClassic());

    [Fact]
    public void In_the_real_shell_escape_closes_the_open_overlay_through_the_hosts_key_preview_chain()
        => host.Mounted(shell =>
        {
            shell.RunFrames(20);
            try
            {
                Sidebar.OverlayOpen.Value = true;
                shell.RunFrames(5);
                Assert.True(Sidebar.OverlayOpen.Peek(), "the overlay is open in the narrow band");

                Key(shell, Keys.Escape);
                shell.RunFrames(3);
                Assert.False(Sidebar.OverlayOpen.Peek(), "Escape belongs to the drawer while it is open and the pane is not editing");
            }
            finally { Sidebar.OverlayOpen.Value = false; }
        }, 600f, 760f);

    // ── the palette ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    sealed class FixtureSession(params string[] labels) : IPaletteSession
    {
        public List<string> Queries { get; } = [];
        public List<int> Drawn { get; } = [];

        public int Filter(string query)
        {
            Queries.Add(query);
            return labels.Length;
        }

        public string GlyphAt(int index) => "G" + index;

        public string LabelAt(int index)
        {
            Drawn.Add(index);
            return labels[index];
        }

        public void Invoke(int index) { }
    }

    [Fact]
    public void The_palette_card_asks_for_the_empty_query_first_and_draws_every_row_the_session_answers()
    {
        var session = new FixtureSession("Home", "Browse", "Settings");
        var props = new PaletteProps
        {
            // The popup control needs an overlay host; the card alone is what is under test, so the fixture places it inline.
            Popup = static (anchor, content) => new BoxEl { Direction = 1, Children = [anchor, content()] },
            NewSession = () => session,
            Close = static () => { },
        };
        ViewHarness.With(host, ViewKeys.ShellCommandPalette, props,
            harness =>
            {
                Assert.Equal("", session.Queries[0]);   // the empty query: the builtin table in declaration order
                Assert.Equal(new[] { 0, 1, 2 }, session.Drawn.Distinct().Order().ToArray());
            }, UnderClassic());
    }

    // ── the small ones ────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_history_menu_draws_one_row_per_item()
    {
        int one = 0, three = 0;
        MenuFlyoutItem Item(string label) => new(label, default, true, null);
        ViewHarness.With(host, ViewKeys.ShellHistoryMenu, new HistoryMenuProps { Items = [Item("A")], Close = static () => { } },
            harness => one = Count(harness), UnderClassic());
        ViewHarness.With(host, ViewKeys.ShellHistoryMenu, new HistoryMenuProps { Items = [Item("A"), Item("B"), Item("C")], Close = static () => { } },
            harness => three = Count(harness), UnderClassic());
        Assert.True(three > one, $"the menu grows with its items: {one} -> {three}");
    }

    [Fact]
    public void The_file_drop_cue_is_the_pill_and_its_hint()
        => ViewHarness.With(host, ViewKeys.ShellFileDrop, new FileDropProps { Over = new Signal<bool>(false) },
            harness => Assert.True(Count(harness) >= 3, "the layer, the pill and the hint are mounted even while no drag is over"),
            UnderClassic());

    [Fact]
    public void The_frame_rate_hud_exists_only_while_its_rule_holds()
    {
        var on = new Signal<bool>(false);
        ViewHarness.With(host, ViewKeys.ShellFps, new FpsProps { Shown = () => on.Value },
            harness =>
            {
                int off = Count(harness);

                on.Value = true;
                harness.RunFrames(3);
                Assert.True(Count(harness) > off, "the pill is mounted while the rule holds");

                on.Value = false;
                harness.RunFrames(3);
                Assert.Equal(off, Count(harness));
            }, UnderClassic());
    }

    [Fact]
    public void The_cover_scrim_fades_on_the_edge_and_ticks_nothing_while_the_cover_holds_still()
    {
        var dim = new Signal<bool>(false);
        ViewHarness.With(host, ViewKeys.ShellCoverScrim, new CoverScrimProps { Dim = () => dim.Value },
            harness =>
            {
                harness.RunFrames(40);   // past the mount's own 0 → 0 seed (a 250 ms track on the scrim)
                int resting = TrackedNodes(harness);

                dim.Value = true;
                harness.RunFrames(2);
                Assert.True(TrackedNodes(harness) > resting, "the dim fades in on the edge");

                harness.RunFrames(40);   // longer than the fade
                Assert.Equal(resting, TrackedNodes(harness));
            }, UnderClassic());
    }
}
