// ── Wavee.Tests/Theming/TierPolicyTests.cs — the tier policy, Classic's ladders and the core's tier rules (themes plan 3.5) ──
//
// `TierPolicy` is data (ladders, floors, rungs); `WindowTiers` is the core rule that feeds it sizes. Nothing here mounts a
// frame: the policy and the pure rules are driven with plain numbers (the live resize is `Wavee.ShellTests\TierResizeTests`).
//
//   · every shipped structure's policy is bindable, ascending, has hysteresis on every present edge and covers the compact
//     floor to infinity;
//   · Classic's window ladder is the production numbers, moved: the sidebar's window band (528 / 568, 660 / 700) resolves
//     EXACTLY as the code it replaced, proved against a transcription of that code over a width sweep from every starting
//     tier (the oracle below is the old code, kept only here). The player bar's levels are `PlayerBarPolicy`'s (T3), pinned by
//     `ThemePoliciesTests`; the named-ladder mechanism is tested with a synthetic ladder;
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

    // ── Classic's window ladder: the production numbers ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(300f, WidthTier.Compact)]
    [InlineData(400f, WidthTier.Compact)]
    [InlineData(527f, WidthTier.Compact)]
    [InlineData(528f, WidthTier.Small)]      // an exact edge lands in the upper tier on a fresh resolve
    [InlineData(659f, WidthTier.Small)]
    [InlineData(660f, WidthTier.Standard)]
    [InlineData(1440f, WidthTier.Standard)]
    [InlineData(3840f, WidthTier.Standard)]  // Classic has no Huge band until TC1 (Auto zoom folds it into Standard today)
    public void Classics_window_ladder_on_a_fresh_resolve(float width, WidthTier expected)
        => Assert.Equal(expected, ClassicTierFixture.FreshWindow(width));

    [Theory]
    [InlineData(WidthTier.Compact, 567f, WidthTier.Compact)]     // leaves Compact only at 568
    [InlineData(WidthTier.Compact, 568f, WidthTier.Small)]
    [InlineData(WidthTier.Compact, 699f, WidthTier.Small)]
    [InlineData(WidthTier.Compact, 700f, WidthTier.Standard)]    // a jump over a band crosses both edges at their widening widths
    [InlineData(WidthTier.Small, 699f, WidthTier.Small)]         // leaves Small only at 700
    [InlineData(WidthTier.Small, 700f, WidthTier.Standard)]
    [InlineData(WidthTier.Small, 528f, WidthTier.Small)]
    [InlineData(WidthTier.Small, 527f, WidthTier.Compact)]       // narrowing has no hold
    [InlineData(WidthTier.Standard, 660f, WidthTier.Standard)]
    [InlineData(WidthTier.Standard, 659f, WidthTier.Small)]
    [InlineData(WidthTier.Standard, 300f, WidthTier.Compact)]
    public void Classics_window_ladder_holds_per_direction(WidthTier from, float width, WidthTier expected)
        => Assert.Equal(expected, (WidthTier)ClassicTierFixture.Tiers.Window.Resolve(width, (int)from));

    [Fact]
    public void Oscillating_around_an_edge_never_flips_between_tiers()
    {
        // From Standard, narrow across 660 once, then wiggle inside the 660..700 hold band: the tier stays Small until 700.
        var ladder = ClassicTierFixture.Tiers.Window;
        int tier = (int)WidthTier.Standard;
        var seen = new List<int>();
        foreach (float w in new[] { 650f, 690f, 655f, 699f, 660f, 661f, 659f, 680f, 700f, 690f, 661f, 659f })
        {
            tier = ladder.Resolve(w, tier);
            seen.Add(tier);
        }
        // 650 Small | 690 .. 680 Small (the hold band) | 700 Standard | 690 and 661 stay Standard (no narrowing hold) | 659 Small
        Assert.Equal(new[] { 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 1 }, seen.ToArray());
        int crossings = 0;
        for (int i = 1; i < seen.Count; i++) if (seen[i] != seen[i - 1]) crossings++;
        Assert.Equal(2, crossings);
    }

    [Fact]
    public void The_content_and_height_ladders_are_resolvable_data()
    {
        Assert.Equal((int)HeightTier.Short, ClassicTierFixture.Tiers.Height.Resolve(330f, -1));
        Assert.Equal((int)HeightTier.Short, ClassicTierFixture.Tiers.Height.Resolve(679f, -1));
        Assert.Equal((int)HeightTier.Regular, ClassicTierFixture.Tiers.Height.Resolve(680f, -1));
        Assert.Equal((int)HeightTier.Short, ClassicTierFixture.Tiers.Height.Resolve(700f, (int)HeightTier.Short));    // leaves Short at 720
        Assert.Equal((int)HeightTier.Regular, ClassicTierFixture.Tiers.Height.Resolve(720f, (int)HeightTier.Short));
        Assert.Equal((int)WidthTier.Small, ClassicTierFixture.Tiers.Content.Resolve(600f, -1));
    }

    // ── the moved ladders equal the code they replaced ─────────────────────────────────────────────────────────────────

    /// <summary>The sidebar's old window band (<c>SidebarPaneModeRules.BandOf</c>), transcribed. The oracle exists only here.</summary>
    private static SidebarWindowBand LegacyBand(float viewportW, SidebarWindowBand current)
    {
        if (!float.IsFinite(viewportW) || viewportW <= 0f) return current;
        return current switch
        {
            SidebarWindowBand.Wide => viewportW >= 660f ? SidebarWindowBand.Wide
                : viewportW >= 528f ? SidebarWindowBand.Narrow : SidebarWindowBand.Tiny,
            SidebarWindowBand.Narrow => viewportW >= 700f ? SidebarWindowBand.Wide
                : viewportW >= 528f ? SidebarWindowBand.Narrow : SidebarWindowBand.Tiny,
            _ => viewportW >= 700f ? SidebarWindowBand.Wide
                : viewportW >= 568f ? SidebarWindowBand.Narrow : SidebarWindowBand.Tiny,
        };
    }

    [Theory]
    [InlineData(SidebarWindowBand.Wide)]
    [InlineData(SidebarWindowBand.Narrow)]
    [InlineData(SidebarWindowBand.Tiny)]
    public void The_window_ladder_is_the_old_sidebar_band_at_every_width(SidebarWindowBand from)
    {
        for (float w = -20f; w <= 3000f; w += 0.25f)
            Assert.Equal(LegacyBand(w, from), ClassicTierFixture.BandOf(w, from));
        foreach (float odd in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, 0f, -0f })
            Assert.Equal(from, ClassicTierFixture.BandOf(odd, from));            // an unusable size never moves the band
    }

    [Fact]
    public void A_fresh_window_resolve_is_the_old_initial_band()
    {
        for (float w = 1f; w <= 3000f; w += 0.25f)
            Assert.Equal(LegacyBand(w, SidebarWindowBand.Wide), SidebarPaneModeRules.BandOf(ClassicTierFixture.FreshWindow(w)));
    }

    // ── the named-ladder mechanism (Classic declares none: its player bar's levels are PlayerBarPolicy's) ──────────────────

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
        Assert.True(ClassicTierFixture.Tiers.Ladders.IsDefaultOrEmpty);   // Classic's player bar is PlayerBarPolicy, not a ladder
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
    [InlineData(1180f, 320f, false, 340f, 860f)]            // no rail: the window less the pane
    [InlineData(1180f, 320f, true, 340f, 512f)]             // an open rail that fits (320 + 340 + 480 <= 1180): less its 8-DIP gap and its width
    [InlineData(1100f, 320f, true, 340f, 780f)]             // an open rail that does not fit floats over the page: no reservation
    [InlineData(1180f, 48f, true, 340f, 784f)]              // the forced rail pane leaves room for the docked rail
    [InlineData(500f, 0f, false, 340f, 500f)]               // Tiny: the drawer takes no column
    [InlineData(300f, 320f, false, 340f, 0f)]               // never negative
    public void The_content_width_is_the_window_less_the_pane_and_an_inline_rail(float window, float pane, bool railOpen, float railWidth, float expected)
        => Assert.Equal(expected, WindowTiers.EstimateContentWidth(window, pane, railOpen, railWidth));

    [Fact]
    public void The_content_width_of_an_unusable_window_is_not_a_number()
    {
        Assert.True(float.IsNaN(WindowTiers.EstimateContentWidth(0f, 320f, false, 340f)));
        Assert.True(float.IsNaN(WindowTiers.EstimateContentWidth(-5f, 320f, true, 340f)));
        Assert.True(float.IsNaN(WindowTiers.EstimateContentWidth(float.NaN, 320f, false, 340f)));
        Assert.Equal(900f, WindowTiers.EstimateContentWidth(900f, float.NaN, false, 340f));   // a broken pane width counts as none
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
