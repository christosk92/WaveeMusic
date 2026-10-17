// ── Wavee.Tests/Theming/PanelArrangementTests.cs — the panel's pane arrangement per window tier (contract 1.29; P5 ZT2) ──────────────
//
// Pure rules and the real panel host, driven with numbers:
//
//   CLASSIC REGRESSION   Classic's rules dock exactly what they docked before the arrangement existed (one pane at Standard, the queue
//                        right of the primary at Huge and Huge 4K, at most two), the generic pane rule answers what the queue rule always
//                        answered for every input, the content estimate counts the same widths, and the two-pane verbs are unchanged.
//   THE ARRANGEMENT      the default (none declared) is Classic's queue; MaxDocked caps it; a body never shows twice (Classic hides the
//                        pane, a linked arrangement steps the primary aside); the policy refuses what the host cannot keep.
//   LINKED PANES         Zune's: the panes open and close with the panel, a door on screen closes it, the beside close closes the panel,
//                        and a remembered Classic queue pane folds into the panel.
//   THE FLOOR            honoured above the policy's own bounds (Zune's 340-only panel docks at 360), never above the shell's baseline.

using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Themes.Classic;
using Wavee.Themes.Zune;
using Xunit;

namespace Wavee.Tests.Theming;

public sealed class PanelArrangementTests
{
    // ── Classic regression: Classic's two panes unchanged ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Classic_docks_one_pane_at_standard_and_the_queue_right_of_it_at_huge_and_4k()
    {
        var panel = ClassicPolicies.Panel;
        Assert.Empty(panel.Validate());
        foreach (var tier in new[] { WidthTier.Compact, WidthTier.Small, WidthTier.Standard })
        {
            Assert.Equal(0, panel.For(tier).Arrangement.Count);
            Assert.False(panel.For(tier).DocksTwo);
        }
        foreach (var tier in new[] { WidthTier.Huge, WidthTier.Huge4K })
        {
            var rule = panel.For(tier);
            Assert.Equal(PanelArrangement.QueueBeside, rule.Arrangement);              // the queue, independent, right of the primary
            Assert.Equal(new DockedPane(PanelMode.Queue, PaneSide.After), rule.Arrangement.Second);
            Assert.False(rule.Arrangement.Linked);
            Assert.True(rule.DocksTwo);
            Assert.False(rule.DocksThree);                                              // never a third
            Assert.Equal(2, rule.MaxDocked);
        }
        Assert.Equal(new DockedPane(PanelMode.Queue), panel.PaneAt(0));                 // the pane a crossing slides out keeps the queue
    }

    [Fact]
    public void The_pane_rule_answers_what_the_queue_rule_always_answered()
    {
        var classic = PanelArrangement.QueueBeside;
        foreach (bool beside in new[] { false, true })
        foreach (bool allowed in new[] { false, true })
        foreach (bool open in new[] { false, true })
        foreach (Shell.RailMode mode in Enum.GetValues<Shell.RailMode>())
        {
            var panes = allowed ? classic : default;
            Assert.Equal(Shell.FrameRules.SecondOpen(beside, allowed, open, mode), Shell.FrameRules.PaneOpen(in panes, 0, beside, open, mode));
            Assert.False(Shell.FrameRules.PaneOpen(in panes, 1, beside, open, mode));   // Classic has no third pane
        }
    }

    [Theory]
    [InlineData(1440f, WidthTier.Standard, true, Shell.RailMode.Lyrics, 340f, true)]
    [InlineData(1920f, WidthTier.Huge, true, Shell.RailMode.Lyrics, 340f, true)]
    [InlineData(2560f, WidthTier.Huge, true, Shell.RailMode.Lyrics, 340f, false)]
    [InlineData(2560f, WidthTier.Huge, true, Shell.RailMode.Queue, 460f, true)]
    [InlineData(3840f, WidthTier.Huge4K, false, Shell.RailMode.Lyrics, 340f, true)]
    [InlineData(800f, WidthTier.Small, true, Shell.RailMode.Lyrics, 340f, true)]
    public void Classic_reserves_the_same_widths_for_the_content_estimate(float width, WidthTier tier, bool open, Shell.RailMode mode,
        float railWidth, bool queueBeside)
    {
        // The formula `WindowTiers.PanelsReserved` used before the arrangement: the primary and the queue's second pane.
        var rule = ClassicPolicies.Panel.For(tier);
        float floor = rule.FloorFor(width);
        bool second = Shell.FrameRules.SecondOpen(queueBeside, rule.DocksTwo, open, mode);
        float before = Shell.FrameRules.PanelsReservedWidth(open, rule.Docks, MathF.Max(railWidth, floor), second, Shell.FrameRules.SecondWidth(floor));
        Assert.Equal(before, Shell.FrameRules.PanelsReserved(rule, width, open, mode, railWidth, queueBeside));
    }

    [Fact]
    public void Classic_floors_stay_inside_the_shells_bounds_so_its_widths_do_not_move()
    {
        foreach (float w in new[] { 1920f, 2560f, 3200f, 3840f })
        {
            float floor = ClassicPolicies.Panel.For(WidthTier.Huge).FloorFor(w);
            Assert.Equal(Shell.ClampRailWidth(floor, ClassicPolicies.Panel), Shell.FrameRules.FloorWidth(floor));
        }
        Assert.Equal(0f, Shell.FrameRules.FloorWidth(0f));
    }

    // ── the arrangement ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Max_docked_caps_the_arrangement_and_a_rule_that_docks_one_has_none()
    {
        var zune4K = new PanelArrangement(new DockedPane(PanelMode.Queue, PaneSide.Before), new DockedPane(PanelMode.Friends), Linked: true);
        Assert.Equal(2, new PanelTierRule(PanelPlacement.Dock, MaxDocked: 3) { Panes = zune4K }.Arrangement.Count);
        Assert.Equal(1, new PanelTierRule(PanelPlacement.Dock, MaxDocked: 2) { Panes = zune4K }.Arrangement.Count);
        Assert.Null(new PanelTierRule(PanelPlacement.Dock, MaxDocked: 2) { Panes = zune4K }.Arrangement.Third);
        Assert.Equal(0, new PanelTierRule(PanelPlacement.Dock, MaxDocked: 1) { Panes = zune4K }.Arrangement.Count);
        Assert.Equal(0, new PanelTierRule(PanelPlacement.Float, MaxDocked: 3) { Panes = zune4K }.Arrangement.Count);
        Assert.True(new PanelTierRule(PanelPlacement.Dock, MaxDocked: 3) { Panes = zune4K }.DocksThree);
        Assert.Equal(PanelArrangement.QueueBeside, new PanelTierRule(PanelPlacement.Dock, MaxDocked: 2).Arrangement);   // none declared: the queue
    }

    [Fact]
    public void A_body_is_never_on_screen_twice()
    {
        // Classic: the queue's pane hides while the primary shows the queue.
        var classic = PanelArrangement.QueueBeside;
        Assert.False(Shell.FrameRules.PaneOpen(in classic, 0, queueBeside: true, railOpen: true, Shell.RailMode.Queue));
        // Linked: the pane stays, and the primary steps aside to the fallback.
        var linked = ZunePanelTiers.HugePanes;
        Assert.True(Shell.FrameRules.PaneOpen(in linked, 0, queueBeside: false, railOpen: true, Shell.RailMode.Queue));
        Assert.Equal(PanelMode.Lyrics, linked.Presented(PanelMode.Queue));
        Assert.Equal(PanelMode.Friends, linked.Presented(PanelMode.Friends));
        Assert.Equal(PanelMode.Lyrics, ZunePanelTiers.Huge4KPanes.Presented(PanelMode.Friends));
        Assert.True(ZunePanelTiers.Huge4KPanes.Modes.Contains(PanelMode.Queue));
        Assert.True(ZunePanelTiers.Huge4KPanes.Modes.Contains(PanelMode.Friends));
        Assert.False(ZunePanelTiers.Huge4KPanes.Modes.Contains(PanelMode.Lyrics));
        Assert.False(Shell.FrameRules.PaneOpen(in linked, 0, queueBeside: true, railOpen: false, Shell.RailMode.Lyrics));   // closes with the panel
    }

    [Fact]
    public void The_policy_refuses_what_the_host_cannot_keep()
    {
        PanelPolicy With(PanelTierRule rule) => ClassicPolicies.Panel with { Tiers = [rule] };
        Assert.Contains(With(new PanelTierRule(PanelPlacement.Dock, MaxDocked: 4)).Validate(), p => p.Contains("MaxDocked"));
        Assert.Contains(With(new PanelTierRule(PanelPlacement.Dock, MaxDocked: 3)).Validate(), p => p.Contains("MaxDocked"));   // the queue alone: two at most
        Assert.Contains(With(new PanelTierRule(PanelPlacement.Dock, MaxDocked: 2) { Panes = new(new DockedPane(PanelMode.Friends)) }).Validate(),
            p => p.Contains("independent"));
        Assert.Contains(With(new PanelTierRule(PanelPlacement.Dock, MaxDocked: 2) { Panes = new(new DockedPane(PanelMode.Video), Linked: true) }).Validate(),
            p => p.Contains("video"));
        Assert.Contains(With(new PanelTierRule(PanelPlacement.Dock, MaxDocked: 2) { Panes = new(new DockedPane(PanelMode.Lyrics), Linked: true) }).Validate(),
            p => p.Contains("fallback"));
        Assert.Contains(With(new PanelTierRule(PanelPlacement.Dock, MaxDocked: 3)
            { Panes = new(new DockedPane(PanelMode.Queue), new DockedPane(PanelMode.Queue, PaneSide.Before), Linked: true) }).Validate(), p => p.Contains("twice"));
        Assert.Contains(With(new PanelTierRule(PanelPlacement.Dock, MaxDocked: 2) { Panes = new(Third: new DockedPane(PanelMode.Friends), Linked: true) }).Validate(),
            p => p.Contains("no second"));
        Assert.Empty(With(new PanelTierRule(PanelPlacement.Dock, MaxDocked: 3) { Panes = ZunePanelTiers.Huge4KPanes }).Validate());
    }

    // ── linked panes in the real host ────────────────────────────────────────────────────────────────────────────────────

    static (Shell.PanelHost Host, Shell.PanelCells Cells) Linked(PanelArrangement panes)
    {
        var cells = Shell.PanelCells.Fresh();
        cells.SecondAllowed.Value = true;
        cells.Panes.Value = panes;
        return (new Shell.PanelHost(cells, new Shell.PanelSizes(new DictionaryPanelSizeStore()), new RecordingPanelEnv()), cells);
    }

    [Fact]
    public void Linked_panes_open_and_close_with_the_panel()
    {
        var (host, cells) = Linked(ZunePanelTiers.Huge4KPanes);
        Assert.False(host.PaneOpenNow(0));
        host.Toggle(Shell.RailMode.Queue);                           // the queue's door opens the whole panel: queue | lyrics | friends
        Assert.True(cells.Open.Peek());
        Assert.True(host.PaneOpenNow(0));
        Assert.True(host.PaneOpenNow(1));
        Assert.True(host.SecondOpenNow());

        host.Toggle(Shell.RailMode.Friends);                         // friends is on screen in its pane: its door closes the panel
        Assert.False(cells.Open.Peek());
        Assert.False(host.PaneOpenNow(0));
        Assert.False(host.PaneOpenNow(1));

        host.Toggle(Shell.RailMode.NowPlaying);                      // a body no pane shows opens in the primary
        Assert.Equal(Shell.RailMode.NowPlaying, cells.Mode.Peek());
        host.Toggle(Shell.RailMode.Lyrics);                          // and another switches it
        Assert.True(cells.Open.Peek());
        Assert.Equal(Shell.RailMode.Lyrics, cells.Mode.Peek());
        host.Toggle(Shell.RailMode.Lyrics);                          // the body on show closes it
        Assert.False(cells.Open.Peek());
    }

    [Fact]
    public void A_linked_panes_close_closes_the_panel_and_never_touches_the_queue_flag()
    {
        var (host, cells) = Linked(ZunePanelTiers.HugePanes);
        host.SetMode(Shell.RailMode.Lyrics);
        host.CloseQueuePane();
        Assert.False(cells.Open.Peek());
        Assert.False(cells.QueueBeside.Peek());
    }

    [Fact]
    public void A_remembered_classic_queue_pane_folds_into_a_linked_panel()
    {
        var (host, cells) = Linked(ZunePanelTiers.HugePanes);
        cells.QueueBeside.Value = true;                               // Classic Huge's lone queue pane, then a switch to Zune
        host.FoldSecondIntoPrimary();
        Assert.True(cells.Open.Peek());
        Assert.Equal(Shell.RailMode.Queue, cells.Mode.Peek());
        Assert.False(cells.QueueBeside.Peek());
        Assert.True(host.PaneOpenNow(0));                             // the queue on screen in its pane, lyrics in the primary
    }

    [Fact]
    public void The_floor_holds_above_the_policys_bounds_but_never_above_the_shells()
    {
        var cells = Shell.PanelCells.Fresh();
        var store = new DictionaryPanelSizeStore();
        store.Values[Shell.PanelSize.RailWidth] = 340f;
        var host = new Shell.PanelHost(cells, new Shell.PanelSizes(store), new RecordingPanelEnv());
        cells.Floor.Value = 360f;
        host.FitWidthToFloor(360f);
        Assert.Equal(360f, cells.Width.Peek());
        var zune = ZunePolicies.For(ZuneParameters.Default).Panel;   // 340 only
        host.FitWidthTo(zune);                                        // a policy publication keeps the floor
        Assert.Equal(360f, cells.Width.Peek());
        Assert.Empty(store.Writes);                                   // never persisted
        Assert.Equal(Shell.RailMaxW, Shell.FrameRules.FloorWidth(900f));
        cells.Floor.Value = 0f;
        host.FitWidthToFloor(0f);
        Assert.Equal(340f, cells.Width.Peek());
    }
}
