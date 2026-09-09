using Wavee;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// THE PINNED BAND'S DROP GEOMETRY (<see cref="PinBandSlots"/>) — the fix for "dragging a pinnable item over a pinned
/// band that already has ≥1 pin shows no drop-zone card". The RCA (pin-dragdrop-cue-implementation.md §0) is that
/// pinned rows already mount the insertion line; <c>SlotFor</c> just never published <c>Before</c>/<c>After</c> for
/// them, so the line never lit. This drives the REAL rule the pane's <c>ResourceDropSpec</c> now calls, over the SAME
/// <see cref="RootlistSlotResolver"/> the rootlist tree uses — so the two surfaces can never disagree about where an
/// edge is.
/// </summary>
public class PinBandSlotsTests
{
    const int PlanIndex = 7;
    const int Depth = 0;

    [Theory]
    [InlineData(32f)]
    [InlineData(40f)]
    [InlineData(48f)]
    public void TopEdgeBand_PublishesBeforeAtTheRowsDepth(float rowHeight)
    {
        float edge = RootlistSlotResolver.EdgeFor(rowHeight);
        float t = (edge * 0.5f) / rowHeight;   // safely inside the top band
        var slot = PinBandSlots.Resolve(PlanIndex, t, rowHeight, Depth, centerDeposits: false);

        Assert.Equal(SidebarDropKind.Before, slot.Kind);
        Assert.Equal(Depth, slot.Depth);
        Assert.Equal(PlanIndex, slot.PlanIndex);
        Assert.Equal(SidebarDropRefusal.None, slot.Refusal);
        Assert.True(slot.DrawsLine);
        Assert.False(slot.DrawsPlate);
    }

    [Theory]
    [InlineData(32f)]
    [InlineData(40f)]
    [InlineData(48f)]
    public void BottomEdgeBand_PublishesAfterAtTheRowsDepth(float rowHeight)
    {
        float edge = RootlistSlotResolver.EdgeFor(rowHeight);
        float t = 1f - (edge * 0.5f) / rowHeight;   // safely inside the bottom band
        var slot = PinBandSlots.Resolve(PlanIndex, t, rowHeight, Depth, centerDeposits: false);

        Assert.Equal(SidebarDropKind.After, slot.Kind);
        Assert.Equal(Depth, slot.Depth);
        Assert.True(slot.DrawsLine);
        Assert.False(slot.DrawsPlate);
    }

    [Fact]
    public void Centre_WithNoDeposit_SplitsBeforeAndAfterAtTheMidpoint()
    {
        const float h = 44f;
        // Just under the midpoint → Before; just over → After. No dead centre when the row cannot deposit.
        var before = PinBandSlots.Resolve(PlanIndex, 0.49f, h, Depth, centerDeposits: false);
        var after = PinBandSlots.Resolve(PlanIndex, 0.51f, h, Depth, centerDeposits: false);

        Assert.Equal(SidebarDropKind.Before, before.Kind);
        Assert.Equal(SidebarDropKind.After, after.Kind);
    }

    [Fact]
    public void Centre_WithDeposit_PublishesInto()
    {
        const float h = 44f;
        var slot = PinBandSlots.Resolve(PlanIndex, 0.5f, h, Depth, centerDeposits: true);

        Assert.Equal(SidebarDropKind.Into, slot.Kind);
        Assert.True(slot.DrawsPlate);
        Assert.False(slot.DrawsLine);
    }

    [Fact]
    public void Centre_WithDeposit_EdgesStillPinBeforeAndAfter()
    {
        const float h = 44f;
        float edge = RootlistSlotResolver.EdgeFor(h);
        float topT = (edge * 0.5f) / h;
        float bottomT = 1f - (edge * 0.5f) / h;

        Assert.Equal(SidebarDropKind.Before, PinBandSlots.Resolve(PlanIndex, topT, h, Depth, centerDeposits: true).Kind);
        Assert.Equal(SidebarDropKind.After, PinBandSlots.Resolve(PlanIndex, bottomT, h, Depth, centerDeposits: true).Kind);
    }

    [Fact]
    public void DegenerateRowHeight_RefusesUnavailable_AndDrawsNeitherCue()
    {
        var slot = PinBandSlots.Resolve(PlanIndex, 0.5f, rowHeight: 0f, Depth, centerDeposits: false);

        Assert.Equal(SidebarDropKind.None, slot.Kind);
        Assert.Equal(SidebarDropRefusal.Unavailable, slot.Refusal);
        Assert.False(slot.DrawsLine);
        Assert.False(slot.DrawsPlate);
        Assert.False(slot.IsArmed);
    }

    [Theory]
    [InlineData(SidebarDropKind.Before, 3, 3)]
    [InlineData(SidebarDropKind.After, 3, 4)]
    [InlineData(SidebarDropKind.EndOfList, 3, 5)]   // EndOfList ignores the row's own slot — it always means "append"
    [InlineData(SidebarDropKind.Into, 3, -1)]        // a pin-band Into is a DEPOSIT, never a pin — callers must not ask
    [InlineData(SidebarDropKind.None, 3, -1)]
    public void InsertIndex_MapsEachKindToItsPinStoreIndex(SidebarDropKind kind, int bandSlot, int expected)
    {
        var slot = new SidebarDropSlot(PlanIndex, kind, Depth, SidebarDropRefusal.None);
        Assert.Equal(expected, PinBandSlots.InsertIndex(in slot, bandSlot, bandCount: 5));
    }

    [Fact]
    public void EndSlot_IsArmedAndDrawsTheLine_AtDepthZero()
    {
        var slot = PinBandSlots.EndSlot(PlanIndex);

        Assert.Equal(PlanIndex, slot.PlanIndex);
        Assert.Equal(SidebarDropKind.EndOfList, slot.Kind);
        Assert.Equal(0, slot.Depth);
        Assert.Equal(SidebarDropRefusal.None, slot.Refusal);
        Assert.True(slot.IsArmed);
        Assert.True(slot.DrawsLine);
        Assert.False(slot.DrawsPlate);
    }

    [Fact]
    public void Facts_NeverAFolder_AndTheDepthLadderIsCollapsed()
    {
        // A pinned folder is a PIN, not a filing target, for a pin payload — the resolver must never treat this row
        // as a folder (which would map the bottom band to "first child" instead of After).
        var facts = PinBandSlots.Facts(depth: 2, centerDeposits: true);

        Assert.False(facts.IsFolder);
        Assert.False(facts.FolderExpanded);
        Assert.False(facts.FolderHasChildren);
        Assert.Equal(2, facts.Depth);
        // NextVisibleDepth == Depth: no outdent ladder — After always stays at the row's own depth.
        Assert.Equal(facts.Depth, facts.NextVisibleDepth);
        Assert.True(facts.CenterAccepts);
        Assert.False(facts.SourceIsSelf);
        Assert.False(facts.SortedNonCustom);
        Assert.True(facts.RootlistLoaded);
    }

    [Fact]
    public void Resolve_ReadsTheSameEdgeGeometryAsTheRootlistResolver()
    {
        // The whole point of delegating to RootlistSlotResolver.Resolve: the two surfaces cannot disagree about where
        // an edge is. Cross-check a few row heights directly against EdgeFor.
        foreach (float h in new[] { 24f, 32f, 44f, 56f, 88f })
        {
            float edge = RootlistSlotResolver.EdgeFor(h);
            float justInsideTop = (edge - 0.5f) / h;
            if (justInsideTop <= 0f) continue;
            Assert.Equal(SidebarDropKind.Before, PinBandSlots.Resolve(PlanIndex, justInsideTop, h, Depth, false).Kind);
        }
    }
}
