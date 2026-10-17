// ── Wavee.Tests/Theming/ClassicPlayerPanelTierTests.cs — the player bar and the side panels per window tier (themes plan 3.5; TC2) ──────
//
// Pure records and rules, driven with numbers: Classic's bar per window tier (the form, the level range, the height; the sub-ladder's
// clamp at the board sizes and the edges), the panel per window tier (placement, how many dock, the Huge floor's fill rule), the panel
// host's two-pane verbs (D3), the docked floor, and the Compact title bar (the chip in the drawer, the tab-count button whole or out).

using FluentGpu.Controls;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Themes.Classic;
using Xunit;

namespace Wavee.Tests.Theming;

public sealed class ClassicPlayerPanelTierTests
{
    // ── the bar per window tier ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Each_window_tier_has_its_bar_record()
    {
        Assert.Equal(ClassicBarForm.Mini, ClassicPlayerTiers.Bar(WidthTier.Compact).Form);
        Assert.Equal(56f, ClassicPlayerTiers.Bar(WidthTier.Compact).Height);
        Assert.Equal(ClassicBarForm.Regular, ClassicPlayerTiers.Bar(WidthTier.Small).Form);
        Assert.Equal(ClassicBarForm.Regular, ClassicPlayerTiers.Bar(WidthTier.Standard).Form);
        Assert.Equal(ClassicBarForm.Huge, ClassicPlayerTiers.Bar(WidthTier.Huge).Form);
        Assert.Same(ClassicPlayerTiers.HugeBar, ClassicPlayerTiers.Bar(WidthTier.Huge4K));   // 3840: the Huge rule
        foreach (var t in new[] { WidthTier.Small, WidthTier.Standard, WidthTier.Huge }) Assert.Equal(72f, ClassicPlayerTiers.Bar(t).Height);
        Assert.Same(ClassicPlayerTiers.StandardBar, ClassicPlayerTiers.BarOf(default));      // no frame: Standard's
        Assert.Same(ClassicPlayerTiers.Policy, ClassicPolicies.PlayerBar);
        Assert.Empty(ClassicPolicies.PlayerBar.Validate());
    }

    [Theory]
    [InlineData(300f, WidthTier.Compact, Shell.PlayerBarTier.Minimal)]       // the mini bar draws its own parts; the level is the floor
    [InlineData(527f, WidthTier.Compact, Shell.PlayerBarTier.Minimal)]
    [InlineData(528f, WidthTier.Small, Shell.PlayerBarTier.Compact)]         // the Medium row does not fit below 760 (flagged)
    [InlineData(800f, WidthTier.Small, Shell.PlayerBarTier.Medium)]          // C-Shell-Small: the Medium bar
    [InlineData(1000f, WidthTier.Small, Shell.PlayerBarTier.Medium)]         // capped at Medium for the whole Small band (the board)
    [InlineData(1008f, WidthTier.Standard, Shell.PlayerBarTier.Comfortable)] // Standard: the production ladder
    [InlineData(1180f, WidthTier.Standard, Shell.PlayerBarTier.Wide)]
    [InlineData(1440f, WidthTier.Standard, Shell.PlayerBarTier.Full)]
    [InlineData(1920f, WidthTier.Huge, Shell.PlayerBarTier.Full)]
    [InlineData(3840f, WidthTier.Huge4K, Shell.PlayerBarTier.Full)]
    public void The_level_is_the_sub_ladder_clamped_to_the_window_tier(float width, WidthTier window, Shell.PlayerBarTier expected)
    {
        var raw = Shell.PlayerBar.Nominal(width, ClassicPolicies.PlayerBar);
        Assert.Equal(expected, Shell.PlayerBar.ForWindow(raw, (int)window, ClassicPolicies.PlayerBar));
    }

    [Fact]
    public void No_known_tier_clamps_nothing()
    {
        Assert.Equal(Shell.PlayerBarTier.Compact, Shell.PlayerBar.ForWindow(Shell.PlayerBarTier.Compact, -1, ClassicPolicies.PlayerBar));
        Assert.Equal(Shell.PlayerBarTier.Comfortable, Shell.PlayerBar.ForWindow(Shell.PlayerBarTier.Comfortable, (int)WidthTier.Small, PlayerBarPolicy.Default));
    }

    [Fact]
    public void A_bad_window_rule_is_reported()
    {
        var bad = ClassicPolicies.PlayerBar with { Windows = [new PlayerBarWindowRule(4, 2)] };
        Assert.Contains(bad.Validate(), p => p.Contains("Windows"));
    }

    // ── the panel per window tier ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Each_window_tier_places_the_panel_as_the_boards_do()
    {
        Assert.Equal(PanelPlacement.Sheet, ClassicPanelTiers.For(WidthTier.Compact).Rule.Placement);
        Assert.Equal(PanelPlacement.Float, ClassicPanelTiers.For(WidthTier.Small).Rule.Placement);
        Assert.Equal(340f, ClassicPanelTiers.For(WidthTier.Small).FloatWidth);
        Assert.Equal(8f, ClassicPanelTiers.For(WidthTier.Small).Inset);
        Assert.True(ClassicPanelTiers.For(WidthTier.Standard).Rule.Docks);
        Assert.Equal(1, ClassicPanelTiers.For(WidthTier.Standard).Rule.MaxDocked);
        Assert.Equal(2, ClassicPanelTiers.For(WidthTier.Huge).Rule.MaxDocked);
        Assert.Same(ClassicPanelTiers.Huge, ClassicPanelTiers.For(WidthTier.Huge4K));
        Assert.Equal(ClassicPanelTiers.Rules, ClassicPolicies.Panel.Tiers);
        Assert.Empty(ClassicPolicies.Panel.Validate());
    }

    [Theory]
    [InlineData(1920f, 380f)]
    [InlineData(2048f, 380f)]
    [InlineData(2560f, 380f)]   // C-Shell-Huge: two panes of 380
    [InlineData(3200f, 400f)]
    [InlineData(3840f, 420f)]   // C-Home-Huge4K / C-Album-Huge4K: 420
    [InlineData(5120f, 420f)]
    public void The_huge_floor_grows_from_the_2560_board_to_the_3840_board(float width, float floor)
        => Assert.Equal(floor, ClassicPanelTiers.Huge.Rule.FloorFor(width));

    [Fact]
    public void Only_a_docked_rule_has_a_floor()
    {
        Assert.Equal(0f, ClassicPanelTiers.Standard.Rule.FloorFor(1440f));
        Assert.Equal(0f, new PanelTierRule(PanelPlacement.Float, FloorW: 300f).FloorFor(800f));
        Assert.Contains((ClassicPolicies.Panel with { Tiers = [new PanelTierRule(PanelPlacement.Dock, MaxDocked: 3)] }).Validate(), p => p.Contains("MaxDocked"));
    }

    // ── the frame's rules for two panes ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_second_pane_shows_only_where_two_dock_and_never_twice()
    {
        Assert.True(Shell.FrameRules.SecondOpen(queueBeside: true, allowed: true, railOpen: true, Shell.RailMode.Lyrics));
        Assert.False(Shell.FrameRules.SecondOpen(queueBeside: true, allowed: false, railOpen: true, Shell.RailMode.Lyrics));
        Assert.False(Shell.FrameRules.SecondOpen(queueBeside: true, allowed: true, railOpen: true, Shell.RailMode.Queue));   // already the primary
        Assert.True(Shell.FrameRules.SecondOpen(queueBeside: true, allowed: true, railOpen: false, Shell.RailMode.Queue));
        Assert.Equal(380f, Shell.FrameRules.SecondWidth(380f));
        Assert.Equal(Shell.RailDefaultW, Shell.FrameRules.SecondWidth(0f));
        Assert.Equal(8f + 340f + 8f + 380f, Shell.FrameRules.PanelsReservedWidth(true, true, 340f, true, 380f));
        Assert.Equal(1200f - 388f - 340f - Shell.FrameRules.SeamStripW, Shell.FrameRules.RailSeamX(1200f, 340f, Shell.FrameRules.SecondOffset(true, 380f)));
        Assert.Equal(0f, Shell.FrameRules.RailSeamWidth(open: true, docked: false));   // a floating panel and a sheet have no grip
    }

    // ── the host's two-pane verbs (D3) ──────────────────────────────────────────────────────────────────────────────

    static (Shell.PanelHost Host, Shell.PanelCells Cells, DictionaryPanelSizeStore Store) Make(bool twoPanes)
    {
        var cells = Shell.PanelCells.Fresh();
        cells.SecondAllowed.Value = twoPanes;
        var store = new DictionaryPanelSizeStore();
        return (new Shell.PanelHost(cells, new Shell.PanelSizes(store), new RecordingPanelEnv()), cells, store);
    }

    [Fact]
    public void Where_two_dock_the_queue_opens_beside_the_open_panel()
    {
        var (host, cells, _) = Make(twoPanes: true);
        host.Toggle(Shell.RailMode.Lyrics);
        host.Toggle(Shell.RailMode.Queue);
        Assert.True(cells.Open.Peek());
        Assert.Equal(Shell.RailMode.Lyrics, cells.Mode.Peek());
        Assert.True(host.SecondOpenNow());                          // Lyrics + Queue (C-Shell-Huge)

        host.Toggle(Shell.RailMode.Queue);                          // the queue's door closes its own pane
        Assert.False(host.SecondOpenNow());
        Assert.True(cells.Open.Peek());
    }

    [Fact]
    public void Where_two_dock_switching_the_primary_away_from_the_queue_moves_the_queue_beside()
    {
        var (host, cells, _) = Make(twoPanes: true);
        host.Toggle(Shell.RailMode.Queue);                          // the queue alone: the primary shows it
        Assert.Equal(Shell.RailMode.Queue, cells.Mode.Peek());
        Assert.False(host.SecondOpenNow());
        host.Toggle(Shell.RailMode.NowPlaying);                     // Now playing + Queue (C-Home-Huge4K)
        Assert.Equal(Shell.RailMode.NowPlaying, cells.Mode.Peek());
        Assert.True(host.SecondOpenNow());

        host.Close();                                               // the primary's ✕ leaves the queue's pane
        Assert.False(cells.Open.Peek());
        Assert.True(host.SecondOpenNow());
        host.CloseQueuePane();
        Assert.False(host.SecondOpenNow());
    }

    [Fact]
    public void Where_one_docks_the_panel_toggles_as_before_and_closing_forgets_the_queue_beside()
    {
        var (host, cells, _) = Make(twoPanes: false);
        cells.QueueBeside.Value = true;                             // remembered from a Huge window
        host.Toggle(Shell.RailMode.Lyrics);
        Assert.False(host.SecondOpenNow());                         // never shown where one docks
        host.Toggle(Shell.RailMode.Lyrics);                         // the mode on show closes it
        Assert.False(cells.Open.Peek());
        Assert.False(cells.QueueBeside.Peek());                     // closing never uncovers a second panel later
    }

    [Fact]
    public void A_crossing_to_one_pane_folds_a_lone_queue_pane_into_the_panel()
    {
        var (host, cells, _) = Make(twoPanes: true);
        host.Toggle(Shell.RailMode.Lyrics);
        host.Toggle(Shell.RailMode.Queue);
        host.Close();                                               // only the queue's pane is left
        cells.SecondAllowed.Value = false;                          // the window narrows out of Huge
        host.FoldSecondIntoPrimary();
        Assert.True(cells.Open.Peek());
        Assert.Equal(Shell.RailMode.Queue, cells.Mode.Peek());
        Assert.False(cells.QueueBeside.Peek());
    }

    [Fact]
    public void The_huge_floor_lifts_the_committed_width_without_persisting_it_and_leaving_huge_restores_it()
    {
        var (host, cells, store) = Make(twoPanes: true);
        store.Values[Shell.PanelSize.RailWidth] = 340f;
        host.FitWidthToFloor(380f);
        Assert.Equal(380f, cells.Width.Peek());
        Assert.Equal(380f, cells.DragWidth.Peek());
        Assert.Empty(store.Writes);
        host.FitWidthToFloor(0f);
        Assert.Equal(340f, cells.Width.Peek());
        store.Values[Shell.PanelSize.RailWidth] = 460f;              // a user who widened it keeps it wider
        host.FitWidthToFloor(380f);
        Assert.Equal(460f, cells.Width.Peek());
    }

    [Fact]
    public void A_drag_at_huge_never_commits_under_the_floor()
    {
        var (host, cells, store) = Make(twoPanes: true);
        cells.Floor.Value = 380f;
        cells.Width.Value = 380f;
        cells.DragWidth.Value = 250f;
        host.ResizePanel();
        Assert.Equal(380f, cells.Width.Peek());
        Assert.Equal(450f, Shell.FrameRules.RailDragCommit(450f, 380f, 380f));
        Assert.Null(Shell.FrameRules.RailDragCommit(250f, 380f, 380f));      // floored to 380: nothing to commit
        Assert.Empty(store.Writes);
    }

    // ── the Compact title bar (TC1 follow-ups) ──────────────────────────────────────────────────────────────────────

    static ChromeBudget CompactBudget => ClassicPolicies.Chrome.BudgetFor(WidthTier.Compact);

    [Fact]
    public void The_compact_row_carries_no_identity_chip()
    {
        Assert.True(CompactBudget.TrailingInDrawer);
        Assert.False(ClassicPolicies.Chrome.BudgetFor(WidthTier.Small).TrailingInDrawer);
        var row = Shell.Chrome.Resolve(400f, 2, null, Shell.FrameRules.ChipForm.Profile, CompactBudget);
        Assert.False(row.ShowTrailing);
    }

    [Theory]
    [InlineData(300f)]
    [InlineData(330f)]
    [InlineData(400f)]
    [InlineData(527f)]
    public void The_tab_count_button_is_whole_or_out_never_clipped(float width)
    {
        var b = CompactBudget;
        var row = Shell.Chrome.Resolve(width, 3, null, Shell.FrameRules.ChipForm.Profile, b);
        Assert.True(row.TabCount);
        float island = row.TabIslandWidthFor(b) - (row.ShowBack ? b.NavButtonW : 0f) - (row.ShowForward ? b.NavButtonW : 0f);
        if (row.TabsHidden) Assert.Equal(0f, island);
        else Assert.Equal(b.NavButtonW, island);                         // the count button's own width, never a 32 sliver
        // Whatever the row seats fits the window beside the fixed parts and the search icon.
        float used = Shell.Chrome.FixedBudget(row.ShowName, row.ShowActions, row.ShowForward, row.ShowBack, row.ShowNewTab, row.ShowTrailing,
            row.Chip, b) - 2f * b.GutterMinW + b.SearchIconW + (row.TabsHidden ? 0f : b.NavButtonW);
        Assert.True(used <= width + 0.5f, $"{width}: the row seats {used}");   // the gutter floors are drag space, not elements
        if (width >= 400f) Assert.False(row.TabsHidden);                 // the board's 400: ≡, Back, Search and the count
        Assert.False(row.TabsHidden, $"{width}: the Compact spacers seat the count from 288, below the 300 floor");
    }

    [Theory]
    [InlineData(300f, false)]
    [InlineData(331f, false)]
    [InlineData(332f, true)]        // 46 lead + 3 x 44 + the 16 strip + the 138 captions
    [InlineData(400f, true)]
    public void The_compact_row_narrows_its_spacers_so_the_count_always_stands_and_Back_joins_it_from_332(float width, bool back)
    {
        var b = CompactBudget;
        Assert.Equal(ClassicPolicies.Chrome.Budget.BarLeadW - TitleBarSpacing.Default.LeadPad, b.BarLeadW);   // no lead pad after the pane toggle
        Assert.Equal(16f, b.MinDragStripW);
        Assert.Equal(0f, b.GutterMinW);
        Assert.Equal(TitleBarSpacing.Default.MinDragStrip, ClassicPolicies.Chrome.BudgetFor(WidthTier.Small).MinDragStripW);   // every other tier: WinUI's 48
        var row = Shell.Chrome.Resolve(width, 3, null, Shell.FrameRules.ChipForm.Profile, b);
        Assert.False(row.TabsHidden);
        Assert.Equal(back, row.ShowBack);
    }

    // ── the now-playing sheet's column and the shared sheet drag (the Compact overlays fix) ─────────────────────────────

    [Theory]
    [InlineData(400f, 860f, 320f)]     // C-NowPlaying-Compact: the board's 320 art, room to spare
    [InlineData(400f, 640f, 232f)]     // the art gives so the identity, the transport and the doors stay in the window
    [InlineData(300f, 860f, 268f)]     // never wider than the sheet less its 16 insets
    [InlineData(400f, 330f, 96f)]      // a Short window: the floor, and the column scrolls
    public void The_sheets_art_gives_way_to_the_fixed_column(float w, float h, float side)
    {
        float head = ClassicPanelTiers.SheetHeadH;
        Assert.Equal(side, ClassicPanelTiers.SheetArtSide(w, h, head));
        float bottom = ClassicSheetRules.ChromeRow + ClassicPanelTiers.Gap + head + ClassicPanelTiers.SheetArtTop + side + ClassicPanelTiers.SheetBelowArt;
        if (side > ClassicPanelTiers.SheetArtMin) Assert.True(bottom <= h, $"{w} x {h}: the doors end at {bottom}");
    }

    [Fact]
    public void The_sheets_doors_keep_the_boards_spacing()
    {
        // C-NowPlaying-Compact: the doors are a three-column grid 8 apart, `margin-top: 14px` under the transport row (it was 8), the lyrics peek 16 below
        // (the row's 8 and the peek's own 8).
        Assert.Equal(14f, ClassicPanelTiers.SheetDoorTop);
        Assert.Equal(8f, ClassicPanelTiers.SheetDoorGap);
        Assert.Equal(8f, ClassicPanelTiers.SheetDoorBottom);
        Assert.Equal(16f, ClassicPanelTiers.SheetDoorInset);
        // The fixed column the art gives way to counts the doors' 14 + 48 + 8.
        float expected = ClassicPanelTiers.SheetMetaTop + ClassicPanelTiers.SheetTitleLine + ClassicPanelTiers.SheetMetaGap + ClassicPanelTiers.SheetArtistLine
            + ClassicPanelTiers.SheetSeekTop + ClassicPanelTiers.SheetSeekRailH + ClassicPanelTiers.SheetSeekGap + ClassicPanelTiers.SheetTimesH
            + ClassicPanelTiers.SheetTransportTop + ClassicPanelTiers.SheetTransportH + 14f + ClassicPanelTiers.SheetDoorH + 8f;
        Assert.Equal(expected, ClassicPanelTiers.SheetBelowArt);
    }

    [Fact]
    public void The_now_playing_sheets_header_is_the_boards_and_its_targets_are_40()
    {
        // C-NowPlaying-Compact: the 16 grab strip over a 48-tall header inset 8, its parts 4 apart: the chevron 40, the source (12/16 twice), the "⋯" 40.
        Assert.Equal(48f, ClassicPanelTiers.SheetHeaderH);
        Assert.Equal(8f, ClassicPanelTiers.SheetHeaderInset);
        Assert.Equal(4f, ClassicPanelTiers.SheetHeaderGap);
        Assert.Equal(16f, ClassicPanelTiers.SheetHeaderGlyph);
        Assert.Equal(12f, ClassicPanelTiers.SheetSourceSize);
        Assert.Equal(16f, ClassicPanelTiers.SheetSourceLine);
        Assert.True(ClassicPanelTiers.SheetHeaderButton >= 40f, "the header's buttons are Compact targets");
        Assert.True(2f * ClassicPanelTiers.SheetSourceLine <= ClassicPanelTiers.SheetHeaderH, "the source's two lines fit the header");
        Assert.Equal(ClassicPanelTiers.GrabStripH + ClassicPanelTiers.SheetHeaderH, ClassicPanelTiers.SheetHeadH);
        // The doors (Lyrics, Devices, Queue) stay 48: the Devices door joins the row, it adds no height under the art.
        Assert.Equal(48f, ClassicPanelTiers.SheetDoorH);
    }

    [Fact]
    public void The_sheets_source_names_no_kind_without_a_name()
    {
        Assert.False(new PanelSourceFacts("", "", null).HasSource);
        Assert.False(default(PanelSourceFacts).HasSource);
        Assert.True(new PanelSourceFacts("Playing from album", "Random Access Memories", null).HasSource);
    }

    [Theory]
    [InlineData(0f, 0f, 800f, false)]          // a tap
    [InlineData(60f, 180f, 800f, false)]       // a slow, short drag springs back
    [InlineData(170f, 300f, 800f, true)]       // past the 160 travel
    [InlineData(100f, 300f, 300f, true)]       // a short sheet: 30 % of its height
    [InlineData(40f, 1200f, 800f, true)]       // a downward fling
    [InlineData(8f, 1200f, 800f, false)]       // ... but not a tap's jitter
    [InlineData(200f, -1200f, 800f, false)]    // flicked back up: it stays
    [InlineData(-20f, 0f, 800f, false)]        // above the rest
    public void A_release_dismisses_past_the_travel_or_the_speed(float offset, float velocity, float height, bool dismisses)
        => Assert.Equal(dismisses, ClassicSheetRules.Dismisses(offset, velocity, height));

    [Fact]
    public void The_sheet_follows_down_one_to_one_and_rubber_bands_up_within_its_reach()
    {
        Assert.Equal(120f, ClassicSheetRules.Rubber(120f, 8f));
        float last = 0f;
        foreach (float raw in new[] { -1f, -10f, -50f, -200f, -2000f })
        {
            float y = ClassicSheetRules.Rubber(raw, 8f);
            Assert.True(y < last && y > -8f, $"{raw} gives {y}");
            last = y;
        }
        Assert.Equal(1f, ClassicSheetRules.ScrimShare(0f, 800f));
        Assert.Equal(0.5f, ClassicSheetRules.ScrimShare(400f, 800f), 3);
        Assert.Equal(0f, ClassicSheetRules.ScrimShare(900f, 800f));
    }
}
