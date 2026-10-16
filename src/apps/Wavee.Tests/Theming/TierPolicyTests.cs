// ── Wavee.Tests/Theming/TierPolicyTests.cs — the tier policy, Classic's ladders and the core's tier rules (themes plan 3.5) ──
//
// `TierPolicy` is data (ladders, floors, rungs); `WindowTiers` is the core rule that feeds it sizes. Nothing here mounts a
// frame: the policy and the pure rules are driven with plain numbers (the live resize is `Wavee.ShellTests\TierResizeTests`).
//
//   · every shipped structure's policy is bindable, ascending, has hysteresis on every present edge and covers the compact
//     floor to infinity;
//   · Classic's ladders are the board's (`C-Breakpoints`, TC1): Compact < 528, Small 528-1007, Standard 1008-1919, Huge >= 1920,
//     held 40 on widening at 528 and 1008 and on narrowing at 1920 (567 and 1047 from below, 1879 from above), no Huge 4K band;
//     the content ladder is the page gutter's (600 / 880 held 24 either way, Huge from 1600), the height ladder Short below 680
//     and Regular from 720. The frame's and the page's tier records (the card's state, the gutter) are pinned per tier. The player
//     bar's levels are `PlayerBarPolicy`'s (T3), pinned by `ThemePoliciesTests`; the named-ladder mechanism is tested with a
//     synthetic ladder;
//   · a five-band policy with narrowing holds (the Zune shape) puts the board sizes in the board's band and holds per direction;
//   · rungs resolve with the same holds on their own axis; the clip declaration of a tier layout record is checkable;
//   · edge cases: exact edge sizes, oscillation around an edge, tiny and unusable sizes, a different policy after a switch.

using System.Collections.Immutable;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Themes.Classic;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.Tests.Theming;

public sealed class TierPolicyTests
{
    // ── the shipped structures ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Every shipped structure's policy (Classic today; WinUI, Zune and Panorama add theirs).</summary>
    public static IEnumerable<object[]> Shipped()
    {
        yield return ["Classic", ClassicTierFixture.Tiers];
    }

    [Theory]
    [MemberData(nameof(Shipped))]
    public void A_shipped_policy_can_be_bound(string name, TierPolicy policy)
    {
        Assert.Empty(policy.Validate());
        Assert.Equal(300f, policy.CompactFloor);
        Assert.InRange(policy.ShortFloor, 1f, 330f);
        Assert.True(name.Length > 0);
    }

    [Theory]
    [MemberData(nameof(Shipped))]
    public void Every_present_edge_has_a_hold_and_the_edges_ascend(string name, TierPolicy policy)
    {
        foreach (TierLadder ladder in new[] { policy.Window, policy.Content, policy.Height })
        {
            float last = float.NegativeInfinity;
            for (int i = 0; i < ladder.EdgeCount; i++)
            {
                TierEdge edge = ladder.EdgeAt(i);
                if (!edge.IsPresent) continue;
                Assert.True(edge.At > last, name + ": edges must ascend");
                Assert.True(edge.NarrowHold > 0f || edge.WidenHold > 0f, name + ": every present edge needs hysteresis in some direction");
                last = edge.At;
            }
        }
    }

    [Theory]
    [MemberData(nameof(Shipped))]
    public void The_ladders_cover_the_compact_floor_to_infinity_and_never_step_down_as_the_window_grows(string name, TierPolicy policy)
    {
        Assert.Equal(0, policy.Window.Resolve(policy.CompactFloor, -1));        // the floor is Compact
        Assert.Equal(0, policy.Window.Resolve(120f, -1));                       // below the floor is still Compact, never an error
        int last = 0;
        for (float w = policy.CompactFloor; w <= 6000f; w += 1f)
        {
            int tier = policy.Window.Resolve(w, -1);
            Assert.True(tier >= last, $"{name}: the window tier fell from {last} to {tier} at {w}");
            last = tier;
        }
        int top = policy.Window.Resolve(float.MaxValue, -1);                    // and the widest window is still a defined band
        Assert.InRange(top, 0, (int)WidthTier.Huge4K);
        Assert.Equal(top, policy.Window.Resolve(1_000_000f, -1));
    }

    [Fact]
    public void Classic_resolves_a_composition_to_one_policy_instance()
    {
        var composition = ClassicTheme.Create();
        var first = composition.Policies(ThemeOptionValues.Empty).Tiers;
        var second = composition.Policies(ThemeOptionValues.Empty).Tiers;
        Assert.Same(first, second);                                             // facts the structure republishes compare equal
        Assert.Same(ClassicTierFixture.Tiers, first);
    }

    // ── Classic's window ladder: the board's (C-Breakpoints) ───────────────────────────────────────────────────────────────

    [Fact]
    public void Classics_policy_is_the_boards_ladder()
    {
        Assert.Same(ClassicTiers.Policy, ClassicTierFixture.Tiers);
        TierLadder w = ClassicTierFixture.Tiers.Window;
        Assert.Equal(new TierEdge(528f, NarrowHold: 0f, WidenHold: 40f), w.EdgeAt(0));
        Assert.Equal(new TierEdge(1008f, NarrowHold: 0f, WidenHold: 40f), w.EdgeAt(1));
        Assert.Equal(new TierEdge(1920f, NarrowHold: 40f, WidenHold: 0f), w.EdgeAt(2));
        Assert.True(w.EdgeCount < 4 || !w.EdgeAt(3).IsPresent);                // no Huge 4K band: Huge covers 3840
        Assert.Equal(210f, ClassicTierFixture.Tiers.ShortFloor);               // 330 - the 48 title row - the 72 player bar
        Assert.Equal(330f, TierPolicy.WindowMinHeight);                        // F8 (#197): the window's minimum height
    }

    [Theory]
    [InlineData(300f, WidthTier.Compact)]
    [InlineData(400f, WidthTier.Compact)]     // the Compact board
    [InlineData(527f, WidthTier.Compact)]
    [InlineData(528f, WidthTier.Small)]       // an exact edge lands in the upper tier on a fresh resolve (NQ12)
    [InlineData(567f, WidthTier.Small)]
    [InlineData(800f, WidthTier.Small)]       // the Small board
    [InlineData(1007f, WidthTier.Small)]
    [InlineData(1008f, WidthTier.Standard)]
    [InlineData(1047f, WidthTier.Standard)]
    [InlineData(1440f, WidthTier.Standard)]   // the Standard boards
    [InlineData(1879f, WidthTier.Standard)]
    [InlineData(1919f, WidthTier.Standard)]
    [InlineData(1920f, WidthTier.Huge)]       // Auto zoom on a 4K monitor at 100 %
    [InlineData(2048f, WidthTier.Huge)]       // Auto zoom on 1440p at 100 % / 4K at 150 %
    [InlineData(2560f, WidthTier.Huge)]       // the Huge board
    [InlineData(3840f, WidthTier.Huge)]       // "Huge 4K uses the same rules at 3840"
    public void Classics_window_ladder_on_a_fresh_resolve(float width, WidthTier expected)
        => Assert.Equal(expected, ClassicTierFixture.FreshWindow(width));

    [Theory]
    [InlineData(WidthTier.Compact, 567f, WidthTier.Compact)]     // widening holds 40 at 528: Small only at 568
    [InlineData(WidthTier.Compact, 568f, WidthTier.Small)]
    [InlineData(WidthTier.Compact, 1047f, WidthTier.Small)]      // a jump over a band crosses each edge at its widening width
    [InlineData(WidthTier.Compact, 1048f, WidthTier.Standard)]
    [InlineData(WidthTier.Small, 1047f, WidthTier.Small)]        // widening holds 40 at 1008: Standard only at 1048
    [InlineData(WidthTier.Small, 1048f, WidthTier.Standard)]
    [InlineData(WidthTier.Small, 528f, WidthTier.Small)]
    [InlineData(WidthTier.Small, 527f, WidthTier.Compact)]       // narrowing has no hold at 528 ...
    [InlineData(WidthTier.Standard, 1008f, WidthTier.Standard)]
    [InlineData(WidthTier.Standard, 1007f, WidthTier.Small)]     // ... nor at 1008
    [InlineData(WidthTier.Standard, 1919f, WidthTier.Standard)]
    [InlineData(WidthTier.Standard, 1920f, WidthTier.Huge)]      // widening has no hold at 1920
    [InlineData(WidthTier.Huge, 1880f, WidthTier.Huge)]          // narrowing holds 40 at 1920: Standard only below 1880
    [InlineData(WidthTier.Huge, 1879f, WidthTier.Standard)]
    [InlineData(WidthTier.Huge, 1007f, WidthTier.Small)]
    [InlineData(WidthTier.Standard, 300f, WidthTier.Compact)]
    public void Classics_window_ladder_holds_per_direction(WidthTier from, float width, WidthTier expected)
        => Assert.Equal(expected, (WidthTier)ClassicTierFixture.Tiers.Window.Resolve(width, (int)from));

    [Fact]
    public void Oscillating_around_an_edge_never_flips_between_tiers()
    {
        // From Standard, narrow across 1008 once, then wiggle inside the 1008..1048 hold band: the tier stays Small until 1048.
        var ladder = ClassicTierFixture.Tiers.Window;
        int tier = (int)WidthTier.Standard;
        var seen = new List<int>();
        foreach (float w in new[] { 1000f, 1040f, 1010f, 1047f, 1008f, 1009f, 1007f, 1030f, 1048f, 1040f, 1009f, 1007f })
        {
            tier = ladder.Resolve(w, tier);
            seen.Add(tier);
        }
        // 1000 Small | 1040 .. 1030 Small (the hold band) | 1048 Standard | 1040 and 1009 stay Standard (no narrowing hold) | 1007 Small
        Assert.Equal(new[] { 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 1 }, seen.ToArray());
        int crossings = 0;
        for (int i = 1; i < seen.Count; i++) if (seen[i] != seen[i - 1]) crossings++;
        Assert.Equal(2, crossings);

        // And at the Huge edge the hold is on the narrowing side: wiggling between 1880 and 1919 after a Huge window stays Huge.
        tier = (int)WidthTier.Huge;
        foreach (float w in new[] { 1919f, 1880f, 1900f, 1881f })
        {
            tier = ladder.Resolve(w, tier);
            Assert.Equal((int)WidthTier.Huge, tier);
        }
        Assert.Equal((int)WidthTier.Standard, ladder.Resolve(1879f, tier));
    }

    [Fact]
    public void The_height_ladder_is_short_below_680_and_regular_from_720()
    {
        Assert.Equal((int)HeightTier.Short, ClassicTierFixture.Tiers.Height.Resolve(330f, -1));
        Assert.Equal((int)HeightTier.Short, ClassicTierFixture.Tiers.Height.Resolve(679f, -1));
        Assert.Equal((int)HeightTier.Regular, ClassicTierFixture.Tiers.Height.Resolve(680f, -1));
        Assert.Equal((int)HeightTier.Short, ClassicTierFixture.Tiers.Height.Resolve(719f, (int)HeightTier.Short));    // leaves Short at 720
        Assert.Equal((int)HeightTier.Regular, ClassicTierFixture.Tiers.Height.Resolve(720f, (int)HeightTier.Short));
        Assert.Equal((int)HeightTier.Short, ClassicTierFixture.Tiers.Height.Resolve(679f, (int)HeightTier.Regular));  // no narrowing hold
    }

    // ── Classic's content ladder: the page gutter (F3) ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(352f, WidthTier.Compact, 16f)]      // a 400 window with the drawer (no column): its whole width
    [InlineData(592f, WidthTier.Compact, 16f)]      // C-Frame-Narrow 640: the 48 rail leaves 592, "Gutter 16"
    [InlineData(599f, WidthTier.Compact, 16f)]
    [InlineData(600f, WidthTier.Small, 32f)]
    [InlineData(752f, WidthTier.Small, 32f)]        // C-Shell-Small / C-Shell-Short 800: the rail leaves 752, gutter 32
    [InlineData(879f, WidthTier.Small, 32f)]
    [InlineData(880f, WidthTier.Standard, 36f)]
    [InlineData(1120f, WidthTier.Standard, 36f)]    // the Standard boards: 1440 less the 320 pane
    [InlineData(1599f, WidthTier.Standard, 36f)]
    [InlineData(1600f, WidthTier.Huge, 36f)]        // a 1920 window beside the 320 pane
    [InlineData(2240f, WidthTier.Huge, 36f)]        // C-Shell-Huge 2560 less the pane
    public void The_content_ladder_is_the_gutter_ladder(float contentWidth, WidthTier tier, float gutter)
    {
        var facts = WindowTiers.Resolve(ClassicTierFixture.Tiers, 1440f, 900f, contentWidth);
        Assert.Equal(tier, facts.Content);
        Assert.Equal(gutter, ClassicTiers.PageOf(facts).Gutter);
        Assert.Equal(gutter, ClassicTiers.Page(tier).Gutter);
        Assert.Equal(tier, ClassicTiers.Page(tier).Tier);
    }

    [Theory]
    [InlineData(WidthTier.Compact, 623f, WidthTier.Compact)]     // widening holds 24 at 600 ...
    [InlineData(WidthTier.Compact, 624f, WidthTier.Small)]
    [InlineData(WidthTier.Small, 576f, WidthTier.Small)]         // ... and so does narrowing
    [InlineData(WidthTier.Small, 575f, WidthTier.Compact)]
    [InlineData(WidthTier.Small, 903f, WidthTier.Small)]         // the same 24 either way at 880
    [InlineData(WidthTier.Small, 904f, WidthTier.Standard)]
    [InlineData(WidthTier.Standard, 856f, WidthTier.Standard)]
    [InlineData(WidthTier.Standard, 855f, WidthTier.Small)]
    [InlineData(WidthTier.Standard, 1600f, WidthTier.Huge)]      // Huge at 1600, held 40 on narrowing
    [InlineData(WidthTier.Huge, 1560f, WidthTier.Huge)]
    [InlineData(WidthTier.Huge, 1559f, WidthTier.Standard)]
    public void The_gutter_ladder_holds_24_either_way(WidthTier from, float width, WidthTier expected)
        => Assert.Equal(expected, (WidthTier)ClassicTierFixture.Tiers.Content.Resolve(width, (int)from));

    [Fact]
    public void A_view_mounted_with_no_frame_reads_the_standard_records()
    {
        Assert.Equal(36f, ClassicTiers.PageOf(TierFacts.Unknown).Gutter);
        Assert.Equal(WidthTier.Standard, ClassicTiers.FrameOf(TierFacts.Unknown).Tier);
        Assert.False(ClassicTiers.FrameOf(TierFacts.Unknown).SquareCard);
    }

    [Fact]
    public void The_frame_rhythm_is_one_pane_inset_and_one_head_rhythm()
    {
        Assert.Equal(16f, ClassicTiers.PaneInset);
        Assert.Equal(4f, ClassicTiers.HeadMetaGap);
        Assert.Equal(24f, ClassicTiers.HeadTop);
        Assert.Equal(new Edges4(16f, 24f, 8f, 24f), ClassicTiers.RailPadding);   // 24 / 8 / 24 / 16, top right bottom left
        Assert.Equal(16f, ClassicDetailLayout.SheetPad);
    }

    // ── Classic's frame records per window tier ────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(WidthTier.Compact, true)]       // C-Shell-Compact / C-Frame-Narrow 420: square, flush, the top hairline alone
    [InlineData(WidthTier.Small, false)]
    [InlineData(WidthTier.Standard, false)]
    [InlineData(WidthTier.Huge, false)]
    [InlineData(WidthTier.Huge4K, false)]       // Huge 4K reads Huge's record
    public void The_content_card_is_square_only_at_compact(WidthTier tier, bool square)
    {
        var frame = ClassicTiers.Frame(tier);
        Assert.Equal(square, frame.SquareCard);
        Assert.Equal(square ? 0f : Radii.Card, frame.CardCorners.TopLeft);
        Assert.Equal(0f, frame.CardCorners.TopRight);
        Assert.Empty(TierClip.Validate(frame));
        Assert.Equal(tier == WidthTier.Huge4K ? WidthTier.Huge : tier, frame.Tier);
    }

    [Fact]
    public void A_frame_record_clips_only_what_its_tier_cannot_fit()
    {
        // The Standard record keeps the rail and the 480 content floor; a resized frame narrower than both clips the card, never the pane.
        var standard = ClassicTiers.Frame(WidthTier.Standard);
        Assert.Empty(TierClip.ClippedAt(standard, 1008f, 600f));
        Assert.Equal(new[] { "content.card" }, TierClip.ClippedAt(standard, 500f, 600f).ToArray());
        // Compact has no pane region: the card is the window from the 300 floor.
        var compact = ClassicTiers.Frame(WidthTier.Compact);
        Assert.Empty(TierClip.ClippedAt(compact, 300f, 330f));
        Assert.DoesNotContain(compact.Regions, r => r.Id == "nav.pane");
    }

    // ── the nav pane's mode by tier (ClassicPolicies.NavPane over the window tier) ───────────────────────────────────────

    [Theory]
    [InlineData(400f, SidebarPaneMode.Minimal)]    // Compact: the drawer, no rail
    [InlineData(527f, SidebarPaneMode.Minimal)]
    [InlineData(528f, SidebarPaneMode.Compact)]    // Small: the forced 48 rail ...
    [InlineData(700f, SidebarPaneMode.Compact)]    // ... over the WHOLE band (production forced it only 528-660)
    [InlineData(1007f, SidebarPaneMode.Compact)]
    [InlineData(1008f, SidebarPaneMode.Expanded)]  // Standard: the resizable pane
    [InlineData(2560f, SidebarPaneMode.Expanded)]  // Huge
    public void The_nav_pane_mode_follows_the_window_tier(float width, SidebarPaneMode mode)
    {
        var band = SidebarPaneModeRules.BandOf(ClassicTierFixture.FreshWindow(width));
        var nav = ClassicPolicies.NavPane;
        Assert.Equal(mode, SidebarPaneModeRules.Resolve(band, userCollapsed: false, editing: false, nav));
        Assert.Equal(mode == SidebarPaneMode.Expanded, SidebarPaneModeRules.SeamVisible(band, nav));          // resizable only when docked
        Assert.Equal(mode != SidebarPaneMode.Expanded, SidebarPaneModeRules.HasOverlay(band, nav));           // the rail and the drawer open the overlay pane
    }

    // ── the title bar's allocation by tier (ClassicPolicies.Chrome) ────────────────────────────────────────────────────

    [Fact]
    public void The_title_bar_budget_is_the_tiers()
    {
        var chrome = ClassicPolicies.Chrome;
        Assert.Empty(chrome.Validate());
        Assert.True(chrome.BudgetFor(WidthTier.Compact).TabsAsCount);                // Compact: one tab-count button
        Assert.False(chrome.BudgetFor(WidthTier.Small).TabsAsCount);
        Assert.Same(chrome.Budget, chrome.BudgetFor(WidthTier.Small));
        Assert.Same(chrome.Budget, chrome.BudgetFor(WidthTier.Standard));
        Assert.Equal(480f, chrome.BudgetFor(WidthTier.Huge).SearchMaxW);              // C-TitleBar's Huge search
        Assert.Equal(240f, chrome.BudgetFor(WidthTier.Huge).TabMaxW);
        Assert.Same(chrome.BudgetFor(WidthTier.Huge), chrome.BudgetFor(WidthTier.Huge4K));
        Assert.Same(chrome.Budget, chrome.BudgetFor(TierFacts.Unknown));               // no frame resolved the tiers: the base budget
        foreach (var budget in new[] { chrome.BudgetFor(WidthTier.Compact), chrome.Budget, chrome.BudgetFor(WidthTier.Huge) })
            Assert.Equal(520f, budget.ForwardEnterW);                                  // Forward hides below 520 at every tier
    }

    [Theory]
    [InlineData(400f)]
    [InlineData(527f)]
    public void The_compact_row_is_back_the_count_and_the_search_icon(float width)
    {
        var b = ClassicPolicies.Chrome.BudgetFor(WidthTier.Compact);
        var row = Shell.Chrome.Resolve(width, 300f, budget: b);
        Assert.True(row.TabCount);
        Assert.False(row.ShowNewTab);                                                 // no strip, so no "+"
        Assert.Equal(Shell.MergedSearchMode.Icon, row.SearchMode);
        Assert.Equal(width > 520f, row.ShowForward);
        Assert.True(row.ShowBack);
        Assert.True(row.TabIslandWidthFor(b) >= b.NavButtonW + (row.ShowBack ? b.NavButtonW : 0f), "the count button fits its island");
        Assert.True(row.FixedBudgetFor(b) + row.SearchWidth + row.LeadClusterW <= width + 0.5f, "the row seats what it allocated");
    }

    [Fact]
    public void The_huge_row_seats_a_wider_search_and_wider_tabs()
    {
        var b = ClassicPolicies.Chrome.BudgetFor(WidthTier.Huge);
        var row = Shell.Chrome.Resolve(2560f, 600f, budget: b);
        Assert.Equal(Shell.MergedSearchMode.Field, row.SearchMode);
        Assert.Equal(480f, row.SearchWidth);
        Assert.Equal(240f, row.TabMaxW);
        Assert.False(row.TabCount);
        var standard = Shell.Chrome.Resolve(1440f, 600f, budget: ClassicPolicies.Chrome.Budget);
        Assert.True(standard.SearchWidth <= 420f);
        Assert.Equal(200f, standard.TabMaxW);
    }

    // ── the named-ladder mechanism (Classic declares the detail page's three, TC5; its player bar's levels are PlayerBarPolicy's) ──

    [Fact]
    public void A_structure_may_declare_a_named_ladder_a_core_model_reads_by_id()
    {
        TierLadder ladder = TierLadder.Of(new TierEdge(100f, NarrowHold: 10f), new TierEdge(200f, NarrowHold: 10f));
        TierPolicy policy = FiveBands() with { Ladders = ImmutableArray.Create(new NamedLadder("test.bar", ladder)) };
        Assert.Empty(policy.Validate());
        Assert.True(policy.TryGetLadder("test.bar", out TierLadder? found));
        Assert.Same(ladder, found);
        Assert.Same(ladder, policy.Ladder("test.bar"));
        Assert.False(policy.TryGetLadder("no.such.ladder", out _));
        Assert.Throws<InvalidOperationException>(() => policy.Ladder("no.such.ladder"));
        // Classic names the detail page's mode, table and height ladders (Tiers.Detail.cs), the artist hero's and the reader's (Tiers.Artist.cs, TC6) and the
        // profile list's (Tiers.Profile.cs) and nothing else: its player bar is PlayerBarPolicy.
        Assert.Equal(6, ClassicTierFixture.Tiers.Ladders.Length);
        Assert.Same(Wavee.Themes.Classic.ClassicArtistTiers.Hero, ClassicTierFixture.Tiers.Ladder(ArtistLadders.Hero));
        Assert.Same(Wavee.Themes.Classic.ClassicArtistTiers.Reader, ClassicTierFixture.Tiers.Ladder(ArtistLadders.Reader));
        Assert.Same(Wavee.Themes.Classic.ClassicProfileTiers.List, ClassicTierFixture.Tiers.Ladder(ArtistLadders.ProfileList));
        Assert.Same(Wavee.Themes.Classic.ClassicDetailTiers.Mode, ClassicTierFixture.Tiers.Ladder(DetailLadders.Mode));
        Assert.Same(Wavee.Themes.Classic.ClassicDetailTiers.Table, ClassicTierFixture.Tiers.Ladder(DetailLadders.Table));
        Assert.Same(Wavee.Themes.Classic.ClassicDetailTiers.Height, ClassicTierFixture.Tiers.Ladder(DetailLadders.Height));
        Assert.False(ClassicTierFixture.Tiers.TryGetLadder("player.bar", out _));
    }

    // ── a five-band policy with narrowing holds (the Zune shape) ───────────────────────────────────────────────────────

    private static TierLadder ZuneWidth() => TierLadder.Of(
        new TierEdge(640f, NarrowHold: 24f), new TierEdge(1008f, NarrowHold: 24f),
        new TierEdge(1920f, NarrowHold: 24f), new TierEdge(3200f, NarrowHold: 24f));

    private static TierPolicy FiveBands(params NamedRung[] rungs) => new(
        ZuneWidth(), ZuneWidth(), TierLadder.Of(new TierEdge(700f, NarrowHold: 24f)),
        CompactFloor: 300f, ShortFloor: 126f,
        Rungs: ImmutableArray.Create(rungs), Ladders: ImmutableArray<NamedLadder>.Empty);

    [Theory]
    [InlineData(300f, WidthTier.Compact)]
    [InlineData(400f, WidthTier.Compact)]
    [InlineData(639f, WidthTier.Compact)]
    [InlineData(640f, WidthTier.Small)]
    [InlineData(800f, WidthTier.Small)]
    [InlineData(1007f, WidthTier.Small)]
    [InlineData(1008f, WidthTier.Standard)]
    [InlineData(1440f, WidthTier.Standard)]
    [InlineData(1919f, WidthTier.Standard)]
    [InlineData(1920f, WidthTier.Huge)]
    [InlineData(2560f, WidthTier.Huge)]
    [InlineData(3199f, WidthTier.Huge)]
    [InlineData(3200f, WidthTier.Huge4K)]
    [InlineData(3840f, WidthTier.Huge4K)]
    public void Five_bands_put_the_board_sizes_in_the_boards_band(float width, WidthTier expected)
    {
        var facts = WindowTiers.Resolve(FiveBands(), width, 900f, width);
        Assert.Equal(expected, facts.Window);
        Assert.Equal(expected, facts.Content);
    }

    [Theory]
    [InlineData(WidthTier.Huge4K, 3176f, WidthTier.Huge4K)]      // narrowing holds 24 below the edge ...
    [InlineData(WidthTier.Huge4K, 3175f, WidthTier.Huge)]
    [InlineData(WidthTier.Huge, 1896f, WidthTier.Huge)]
    [InlineData(WidthTier.Huge, 1895f, WidthTier.Standard)]
    [InlineData(WidthTier.Standard, 984f, WidthTier.Standard)]
    [InlineData(WidthTier.Standard, 983f, WidthTier.Small)]
    [InlineData(WidthTier.Small, 616f, WidthTier.Small)]
    [InlineData(WidthTier.Small, 615f, WidthTier.Compact)]
    [InlineData(WidthTier.Compact, 640f, WidthTier.Small)]       // ... and widening has none
    [InlineData(WidthTier.Small, 1008f, WidthTier.Standard)]
    [InlineData(WidthTier.Standard, 1920f, WidthTier.Huge)]
    [InlineData(WidthTier.Huge, 3200f, WidthTier.Huge4K)]
    public void Narrowing_holds_and_widening_commits(WidthTier from, float width, WidthTier expected)
    {
        TierPolicy policy = FiveBands();
        TierFacts start = WindowTiers.Resolve(policy, from switch
        {
            WidthTier.Compact => 400f, WidthTier.Small => 800f, WidthTier.Standard => 1440f, WidthTier.Huge => 2560f, _ => 3840f,
        }, 900f, 900f);
        Assert.Equal(from, start.Window);
        TierFacts next = WindowTiers.Resolve(policy, width, 900f, width, previous: start);
        Assert.Equal(expected, next.Window);
    }

    [Fact]
    public void A_fresh_resolve_at_a_hold_edge_uses_the_edge_itself()
    {
        TierPolicy policy = FiveBands();
        // A launch at 1900 (inside Huge's 24-DIP hold under 1920) is Standard: a fresh resolve has no tier to hold.
        Assert.Equal(WidthTier.Standard, WindowTiers.Resolve(policy, 1900f, 900f, 1900f).Window);
        // The same window reached by narrowing from Huge holds Huge.
        TierFacts huge = WindowTiers.Resolve(policy, 2560f, 900f, 2560f);
        Assert.Equal(WidthTier.Huge, WindowTiers.Resolve(policy, 1900f, 900f, 1900f, previous: huge).Window);
    }

    [Fact]
    public void The_height_and_content_axes_resolve_on_their_own_values()
    {
        TierPolicy policy = FiveBands();
        TierFacts tall = WindowTiers.Resolve(policy, 1440f, 900f, 1100f);
        Assert.Equal(HeightTier.Regular, tall.Height);
        Assert.Equal(WidthTier.Standard, tall.Window);
        Assert.Equal(WidthTier.Standard, tall.Content);
        TierFacts short_ = WindowTiers.Resolve(policy, 1440f, 330f, 500f);
        Assert.Equal(HeightTier.Short, short_.Height);
        Assert.Equal(WidthTier.Compact, short_.Content);           // the content region is narrower than the window
        Assert.Equal(WidthTier.Standard, short_.Window);
        Assert.Equal(500f, short_.ContentWidth);
        Assert.Equal(1440f, short_.ViewportWidth);
        Assert.Equal(330f, short_.ViewportHeight);
        Assert.Same(policy, short_.Policy);
    }

    [Fact]
    public void Unusable_sizes_keep_the_previous_tier_and_a_tiny_window_is_compact()
    {
        TierPolicy policy = FiveBands();
        TierFacts big = WindowTiers.Resolve(policy, 2560f, 1440f, 2000f);
        // A minimised window reports 0 x 0: nothing moves, so a restore returns to the tier the window left.
        TierFacts minimised = WindowTiers.Resolve(policy, 0f, 0f, 0f, previous: big);
        Assert.Equal(big.Window, minimised.Window);
        Assert.Equal(big.Height, minimised.Height);
        Assert.Equal(big.Content, minimised.Content);
        TierFacts nan = WindowTiers.Resolve(policy, float.NaN, float.NaN, float.NaN, previous: big);
        Assert.Equal(big.Window, nan.Window);
        // A window below the compact floor (a headless host, a corrupt size) is Compact and Short, never an error.
        TierFacts tiny = WindowTiers.Resolve(policy, 120f, 90f, 80f);
        Assert.Equal(WidthTier.Compact, tiny.Window);
        Assert.Equal(HeightTier.Short, tiny.Height);
    }

    [Fact]
    public void A_different_policy_after_a_theme_switch_resolves_fresh()
    {
        TierPolicy zune = FiveBands();
        TierFacts onZune = WindowTiers.Resolve(zune, 1900f, 900f, 1900f, previous: WindowTiers.Resolve(zune, 2560f, 900f, 2560f));
        Assert.Equal(WidthTier.Huge, onZune.Window);                 // held by Zune's hold
        TierFacts onClassic = WindowTiers.Resolve(ClassicTierFixture.Tiers, 1900f, 900f, 1900f, previous: onZune);
        Assert.Equal(WidthTier.Standard, onClassic.Window);          // the old tier of another structure is not a state to hold
        Assert.Same(ClassicTierFixture.Tiers, onClassic.Policy);
    }

    // ── rungs ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static TierPolicy WithRungs() => FiveBands(
        new NamedRung("pane.second", new TierEdge(1920f, NarrowHold: 24f)),
        new NamedRung("pane.third", new TierEdge(3200f, NarrowHold: 24f)),
        new NamedRung("content.line", new TierEdge(860f, NarrowHold: 24f), RungAxis.Content),
        new NamedRung("height.mini", new TierEdge(520f, WidenHold: 40f), RungAxis.Height));

    [Fact]
    public void Rungs_resolve_on_their_own_axis()
    {
        TierPolicy policy = WithRungs();
        TierFacts facts = WindowTiers.Resolve(policy, 2560f, 900f, 1000f);
        Assert.True(facts.Rung("pane.second"));
        Assert.False(facts.Rung("pane.third"));
        Assert.True(facts.Rung("content.line"));                    // the content region (1000) is past 860
        Assert.True(facts.Rung("height.mini"));                     // 900 tall is past the 520 edge
        TierFacts short_ = WindowTiers.Resolve(policy, 1440f, 400f, 600f);
        Assert.False(short_.Rung("pane.second"));
        Assert.False(short_.Rung("content.line"));
        Assert.False(short_.Rung("height.mini"));
    }

    [Fact]
    public void Rungs_hold_per_direction_like_tiers()
    {
        TierPolicy policy = WithRungs();
        TierFacts on = WindowTiers.Resolve(policy, 2000f, 900f, 1000f);
        Assert.True(on.Rung("pane.second"));
        Assert.True(WindowTiers.Resolve(policy, 1900f, 900f, 1000f, previous: on).Rung("pane.second"));    // 1900 >= 1920 - 24
        Assert.False(WindowTiers.Resolve(policy, 1895f, 900f, 1000f, previous: on).Rung("pane.second"));
        // widening hold on the height rung: off until 560 when it was off
        TierFacts off = WindowTiers.Resolve(policy, 2000f, 400f, 1000f);
        Assert.False(off.Rung("height.mini"));
        Assert.False(WindowTiers.Resolve(policy, 2000f, 559f, 1000f, previous: off).Rung("height.mini"));
        Assert.True(WindowTiers.Resolve(policy, 2000f, 560f, 1000f, previous: off).Rung("height.mini"));
    }

    [Fact]
    public void An_unknown_rung_is_a_programming_error_and_unknown_facts_have_none()
    {
        TierFacts facts = WindowTiers.Resolve(WithRungs(), 1440f, 900f, 1000f);
        Assert.Throws<ArgumentException>(() => facts.Rung("no.such.rung"));
        Assert.False(TierFacts.Unknown.Rung("anything"));
        Assert.Equal(-1, WithRungs().IndexOfRung("no.such.rung"));
        Assert.Equal(2, WithRungs().IndexOfRung("content.line"));
    }

    // ── the standard policy and validation ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_structure_with_no_tiers_is_standard_at_every_size()
    {
        TierPolicy policy = ThemePolicies.Default.Tiers;
        Assert.Same(TierPolicy.Standard, policy);
        Assert.Empty(policy.Validate());
        foreach (float w in new[] { 1f, 120f, 300f, 640f, 1008f, 1920f, 3840f, 100000f })
        {
            TierFacts facts = WindowTiers.Resolve(policy, w, 800f, w);
            Assert.Equal(WidthTier.Standard, facts.Window);
            Assert.Equal(WidthTier.Standard, facts.Content);
            Assert.Equal(HeightTier.Regular, facts.Height);
        }
        Assert.Equal(WidthTier.Compact, WindowTiers.Resolve(TierPolicy.Fixed(WidthTier.Compact), 3840f, 800f, 3840f).Window);
        Assert.Equal(WidthTier.Huge4K, WindowTiers.Resolve(TierPolicy.Fixed(WidthTier.Huge4K, HeightTier.Short), 300f, 2000f, 300f).Window);
        Assert.Equal(HeightTier.Short, WindowTiers.Resolve(TierPolicy.Fixed(WidthTier.Huge4K, HeightTier.Short), 300f, 2000f, 300f).Height);
    }

    [Fact]
    public void Validation_names_what_cannot_be_bound()
    {
        TierLadder six = TierLadder.Of(new TierEdge(100f, 1f), new TierEdge(200f, 1f), new TierEdge(300f, 1f), new TierEdge(400f, 1f),
            new TierEdge(500f, 1f));
        TierPolicy tooMany = FiveBands() with { Window = six };
        Assert.Contains(tooMany.Validate(), p => p.Contains("window ladder"));

        TierPolicy badFloor = FiveBands() with { CompactFloor = 0f };
        Assert.Contains(badFloor.Validate(), p => p.Contains("CompactFloor"));

        TierPolicy dupe = FiveBands(new NamedRung("a", new TierEdge(100f, 1f)), new NamedRung("a", new TierEdge(200f, 1f)));
        Assert.Contains(dupe.Validate(), p => p.Contains("more than once"));

        TierPolicy noEdge = FiveBands(new NamedRung("a", TierEdge.None));
        Assert.Contains(noEdge.Validate(), p => p.Contains("no edge"));

        TierPolicy tallHeight = FiveBands() with { Height = TierLadder.Of(new TierEdge(100f, 1f), new TierEdge(200f, 1f)) };
        Assert.Contains(tallHeight.Validate(), p => p.Contains("height ladder"));

        var ladders = ImmutableArray.Create(new NamedLadder("x", TierLadder.Empty), new NamedLadder("x", TierLadder.Empty));
        Assert.Contains((FiveBands() with { Ladders = ladders }).Validate(), p => p.Contains("'x'"));

        Assert.Throws<ArgumentException>(() => new WindowTiers.Binding(tooMany, new FluentGpu.Signals.Signal<Size2>(new Size2(1000f, 800f))));
    }

    // ── the content-width estimate and the size guard ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1180f, 320f, 0f, 860f)]                     // no panel: the window less the pane
    [InlineData(1180f, 320f, 348f, 512f)]                   // a docked panel: less its 8-DIP gap and its width
    [InlineData(500f, 0f, 0f, 500f)]                        // Tiny: the drawer takes no column
    [InlineData(300f, 320f, 0f, 0f)]                        // never negative
    public void The_content_width_is_the_window_less_the_pane_and_the_docked_panels(float window, float pane, float panels, float expected)
        => Assert.Equal(expected, WindowTiers.EstimateContentWidth(window, pane, panels));

    [Theory]
    [InlineData(WidthTier.Standard, 1180f, true, false, 348f)]     // Standard docks one: its gap and width
    [InlineData(WidthTier.Standard, 1180f, true, true, 348f)]      // the queue beside it is not shown where one docks
    [InlineData(WidthTier.Small, 800f, true, true, 0f)]            // Small floats: nothing reserved
    [InlineData(WidthTier.Compact, 400f, true, false, 0f)]         // Compact is a sheet: nothing reserved
    [InlineData(WidthTier.Huge, 2560f, true, false, 388f)]         // Huge: the 380 floor
    [InlineData(WidthTier.Huge, 2560f, true, true, 776f)]          // Huge with the queue beside: two 380 panes and two gaps
    [InlineData(WidthTier.Huge, 3840f, true, true, 856f)]          // at 3840 both are 420
    [InlineData(WidthTier.Huge, 2560f, false, true, 388f)]         // the queue alone in its pane
    public void The_docked_panels_follow_the_window_tier(WidthTier window, float width, bool railOpen, bool queueBeside, float expected)
        => Assert.Equal(expected, WindowTiers.PanelsReserved(ClassicPolicies.Panel, window, width, railOpen, Shell.RailMode.Lyrics, 340f, queueBeside));

    [Fact]
    public void The_content_width_of_an_unusable_window_is_not_a_number()
    {
        Assert.True(float.IsNaN(WindowTiers.EstimateContentWidth(0f, 320f, 0f)));
        Assert.True(float.IsNaN(WindowTiers.EstimateContentWidth(-5f, 320f, 348f)));
        Assert.True(float.IsNaN(WindowTiers.EstimateContentWidth(float.NaN, 320f, 0f)));
        Assert.Equal(900f, WindowTiers.EstimateContentWidth(900f, float.NaN, 0f));   // a broken pane width counts as none
    }

    [Theory]
    [InlineData(1180f, 1180f)]
    [InlineData(0.5f, 0.5f)]
    public void A_usable_size_is_itself(float value, float expected) => Assert.Equal(expected, WindowTiers.UsableSize(value));

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void An_unusable_size_is_not_a_number(float value) => Assert.True(float.IsNaN(WindowTiers.UsableSize(value)));

    // ── the clip declaration of a tier layout record ───────────────────────────────────────────────────────────────────

    private sealed record TestLayout(WidthTier Tier, ImmutableArray<TierRegion> Regions) : ITierLayoutRecord;

    [Fact]
    public void A_region_host_never_squeezes_below_its_minimum_and_clips_in_its_ancestor()
    {
        var pane = new TierRegion("nav.pane", 180f, 120f);
        BoxEl host = pane.Host(new BoxEl());
        Assert.Equal("tier.region:nav.pane", host.Key);
        Assert.Equal("tier.region:nav.pane", pane.NodeKey);
        Assert.Equal(180f, host.MinWidth);
        Assert.Equal(120f, host.MinHeight);
        Assert.Equal(1f, host.Shrink);                              // it shrinks with the flex pass, down to its minimum
        Assert.Single(host.Children);

        BoxEl ancestor = TierClip.Ancestor(row: true, host);
        Assert.True(ancestor.ClipToBounds);                         // what overflows is clipped here
        Assert.Equal(0f, ancestor.MinWidth);                        // the ancestor takes the room it is given, never more
        Assert.Equal(0f, ancestor.MinHeight);
        Assert.Equal(0, (int)ancestor.Direction);
        Assert.Equal(1, (int)TierClip.Ancestor(row: false, host).Direction);
    }

    [Fact]
    public void A_shrinking_frame_clips_only_the_declared_regions_that_no_longer_fit()
    {
        var record = new TestLayout(WidthTier.Standard, ImmutableArray.Create(
            new TierRegion("nav.pane", 180f), new TierRegion("content", 480f), new TierRegion("rail", 200f)));
        Assert.Empty(TierClip.Validate(record));
        Assert.Empty(TierClip.ClippedAt(record, 1180f, 760f));                       // everything fits
        Assert.Empty(TierClip.ClippedAt(record, 860f, 760f));                        // exactly the sum of the minimums
        Assert.Equal(new[] { "rail" }, TierClip.ClippedAt(record, 700f, 760f).ToArray());              // the end of the order clips first
        Assert.Equal(new[] { "content", "rail" }, TierClip.ClippedAt(record, 500f, 760f).ToArray());
        Assert.Equal(new[] { "nav.pane", "content", "rail" }, TierClip.ClippedAt(record, 100f, 760f).ToArray());
        Assert.True(new TierRegion("hero", 100f, 300f).ClipsIn(500f, 200f));         // a height floor clips too
        Assert.False(new TierRegion("hero", 100f, 300f).ClipsIn(500f, 300f));
    }

    [Fact]
    public void A_clip_declaration_with_a_repeated_or_empty_id_or_a_bad_minimum_is_reported()
    {
        var bad = new TestLayout(WidthTier.Compact, ImmutableArray.Create(
            new TierRegion("a", 10f), new TierRegion("a", 20f), new TierRegion("", 5f), new TierRegion("b", -1f)));
        var problems = TierClip.Validate(bad);
        Assert.Contains(problems, p => p.Contains("more than once"));
        Assert.Contains(problems, p => p.Contains("empty id"));
        Assert.Contains(problems, p => p.Contains("minimum"));
    }
}
