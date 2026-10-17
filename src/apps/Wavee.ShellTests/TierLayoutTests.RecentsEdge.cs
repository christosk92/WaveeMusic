// ── Wavee.ShellTests/TierLayoutTests.RecentsEdge.cs — Recents across its Small <-> Standard CONTENT edge glides (C-Recents-Small, C-Recents) ──────────────
//
// Recents' record changes at the CONTENT ladder's edge (880), not the window's: at Small the day rail is flush against the list (the body keeps no right pad, no gap, the rail 4 from
// the card's edge) and at Standard the body keeps its 36 pad and the rail its 12 gap. A window between the shell's 1008 edge and the 1920 one keeps the SAME window tier (so the
// content card does not move and does not carry the crossing for its descendants): 1100 wide leaves a Small page, 1440 a Standard one. The list column and the rail column resize
// and move on that commit, so each must glide (the list on the shared reflow, the rail on its position channel) and be the same node; after it settles the geometry is the
// record's numbers.

using FluentGpu.Foundation;
using Wavee;
using Wavee.Themes.Classic;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed partial class TierLayoutTests
{
    public static TheoryData<float, float> RecentsContentEdgeCrossings() => new() { { 1100f, 1440f }, { 1440f, 1100f } };

    [Theory, MemberData(nameof(RecentsContentEdgeCrossings))]
    public void Recents_glides_its_list_and_rail_across_the_small_standard_content_edge(float from, float to)
        => OnPage(RecentsPage.Route, RecentsPage.ReadyKeyPrefix, from, shell =>
        {
            bool narrowing = to < from;
            string what = $"Recents {from} -> {to}";
            var scene = shell.Host.Scene;
            Until(shell, () => !shell.Find("recents-rail-col").IsNull && !shell.Find("recents-list-col").IsNull, "the loaded list and its day rail");
            shell.RunFrames(30);
            var before = Tiers;
            Assert.Equal(WidthTier.Standard, before.Window);   // the window tier never changes here: the content card does not move
            var parts = new List<Part> { PartOf(shell, "recents-list-col"), PartOf(shell, "recents-rail-col") };

            ResizedFrame(shell, to, narrowing);
            Assert.Equal(before.Content, Tiers.Content);   // the resized frame keeps the old tier
            parts = Restate(shell, parts);
            CrossingFrame(shell, narrowing);

            var after = Tiers;
            Assert.Equal(WidthTier.Standard, after.Window);
            Assert.NotEqual(before.Content, after.Content);
            // The list is the same node, and the list and the rail glide (a part that moved or resized on the commit holds animation tracks).
            AssertKeptAndGliding(shell, parts, what);
            Assert.True(parts.Any(p => !scene.Bounds(p.Node).Equals(p.Local)), $"{what}: neither the list nor the rail changed its box across the edge");

            // Settled: the record's numbers. Flush (Small): the rail's left edge is the list's right edge and it stands 4 from the card's edge; otherwise the 12 gap.
            shell.RunFrames(60);
            var layout = ClassicRecentsTiers.Of(Tiers);
            var card = scene.AbsoluteRect(Node(shell, ClassicFrameLayout.ContentCardKey));
            var list = scene.AbsoluteRect(Node(shell, "recents-list-col"));
            var rail = scene.AbsoluteRect(Node(shell, "recents-rail-col"));
            if (layout.FlushRail)
            {
                Assert.True(MathF.Abs(list.X + list.W - rail.X) <= 0.5f, $"{what}: the rail is not flush against the list ({list} / {rail})");
                Assert.True(MathF.Abs(card.X + card.W - (rail.X + rail.W) - ClassicRecentsRules.RailMarginRight) <= 0.5f, $"{what}: the rail is not 4 from the card's edge ({rail} in {card})");
            }
            else
            {
                Assert.True(MathF.Abs(rail.X - (list.X + list.W) - 12f) <= 0.5f, $"{what}: the rail is not 12 from the list ({list} / {rail})");
                float gutter = ClassicTiers.PageOf(Tiers).Gutter;
                Assert.True(MathF.Abs(card.X + card.W - gutter - (rail.X + rail.W)) <= 1.5f, $"{what}: the rail does not end at the page gutter ({rail} in {card}, gutter {gutter})");
            }
        });
}
