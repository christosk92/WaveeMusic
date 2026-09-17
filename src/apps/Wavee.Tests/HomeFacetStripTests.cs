// ── Wavee.Tests/HomeFacetStripTests.cs — the facet strip's ORDER (Wave 5, owner P; ported verbatim from 0.2.9) ─────────
//
// Where a second-level chip appears, when it folds into its parent, and what a tap on each position writes back. These are
// the rules the eye reads — "Following" belongs next to Music, not after Audiobooks — pinned away from the renderer.

using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public sealed class HomeFacetStripTests
{
    // The shape home.json actually sends: two chips carrying a "Following" second level, one that carries none.
    static HomeChip Music => new("music-chip", "Music", [new HomeChip("music-following-chip", "Following", [])]);
    static HomeChip Podcasts => new("podcasts-chip", "Podcasts", [new HomeChip("podcasts-following-chip", "Following", [])]);
    static HomeChip Audiobooks => new("audiobooks-chip", "Audiobooks", []);

    static HomeChip[] Chips() => [Music, Podcasts, Audiobooks];

    [Fact]
    public void Nothing_Selected_AllIsSelected_NoSubs()
    {
        var chips = Chips();
        var slots = HomeFacetStrip.Slots(chips, null);

        Assert.Equal(4, slots.Count);
        Assert.Equal(FacetSlotKind.All, slots[0].Kind);
        Assert.True(slots[0].Selected);
        Assert.Equal("facet-all", slots[0].Key);
        Assert.Null(slots[0].Select);

        Assert.All(slots.GetRange(1, 3), s => Assert.Equal(FacetSlotKind.Tab, s.Kind));
        Assert.All(slots.GetRange(1, 3), s => Assert.False(s.Selected));
        Assert.Equal("music-chip", slots[1].Select);
        Assert.Equal("podcasts-chip", slots[2].Select);
        Assert.Equal("audiobooks-chip", slots[3].Select);
        Assert.DoesNotContain(slots, s => s.Kind == FacetSlotKind.Sub);
        Assert.DoesNotContain(slots, s => s.Kind == FacetSlotKind.Fused);
    }

    [Fact]
    public void Music_Selected_FollowingFollowsMusic_NotAudiobooks()
    {
        var chips = Chips();
        var slots = HomeFacetStrip.Slots(chips, "music-chip");

        Assert.Equal(5, slots.Count);

        Assert.Equal(FacetSlotKind.All, slots[0].Kind);
        Assert.False(slots[0].Selected);

        Assert.Equal(FacetSlotKind.Tab, slots[1].Kind);
        Assert.Same(chips[0], slots[1].Chip);
        Assert.True(slots[1].Selected);
        Assert.Equal("facet-pill:music-chip", slots[1].Key);
        Assert.Equal("music-chip", slots[1].Select);

        Assert.Equal(FacetSlotKind.Sub, slots[2].Kind);
        Assert.Same(chips[0], slots[2].Chip);
        Assert.Equal("music-following-chip", slots[2].Sub!.Id);
        Assert.False(slots[2].Selected);
        Assert.Equal("facet-sub:music-following-chip", slots[2].Key);
        Assert.Equal("music-following-chip", slots[2].Select);

        Assert.Equal(FacetSlotKind.Tab, slots[3].Kind);
        Assert.Same(chips[1], slots[3].Chip);
        Assert.False(slots[3].Selected);

        Assert.Equal(FacetSlotKind.Tab, slots[4].Kind);
        Assert.Same(chips[2], slots[4].Chip);
        Assert.False(slots[4].Selected);
    }

    [Fact]
    public void MusicFollowing_Selected_FusesIntoMusic_SameKeyAsTab_SelectStepsBackToMusic()
    {
        var chips = Chips();
        var slots = HomeFacetStrip.Slots(chips, "music-following-chip");

        Assert.Equal(4, slots.Count);
        Assert.DoesNotContain(slots, s => s.Kind == FacetSlotKind.Sub);

        var fused = slots[1];
        Assert.Equal(FacetSlotKind.Fused, fused.Kind);
        Assert.Same(chips[0], fused.Chip);
        Assert.Equal("music-following-chip", fused.Sub!.Id);
        Assert.True(fused.Selected);
        Assert.False(slots[0].Selected);

        Assert.Equal("facet-pill:music-chip", fused.Key);
        Assert.Equal(HomeFacetStrip.Slots(chips, "music-chip")[1].Key, fused.Key);

        Assert.Equal("music-chip", fused.Select);

        Assert.Equal(FacetSlotKind.Tab, slots[2].Kind);
        Assert.Equal(FacetSlotKind.Tab, slots[3].Kind);
    }

    [Fact]
    public void Podcasts_Selected_MusicHasNoSpilledSub()
    {
        var chips = Chips();
        var slots = HomeFacetStrip.Slots(chips, "podcasts-chip");

        Assert.Equal(5, slots.Count);
        Assert.Equal(FacetSlotKind.Tab, slots[1].Kind);
        Assert.False(slots[1].Selected);

        Assert.Equal(FacetSlotKind.Tab, slots[2].Kind);
        Assert.Same(chips[1], slots[2].Chip);
        Assert.True(slots[2].Selected);

        var sub = Assert.Single(slots, s => s.Kind == FacetSlotKind.Sub);
        Assert.Equal("podcasts-following-chip", sub.Sub!.Id);
        Assert.Same(chips[1], sub.Chip);
        Assert.Equal(3, slots.IndexOf(sub));

        Assert.Equal(FacetSlotKind.Tab, slots[4].Kind);
        Assert.Same(chips[2], slots[4].Chip);
    }

    [Fact]
    public void Unknown_Facet_BehavesAsUnfiltered()
    {
        var chips = Chips();
        var slots = HomeFacetStrip.Slots(chips, "wat-chip");

        Assert.Equal(4, slots.Count);
        Assert.True(slots[0].Selected);
        Assert.All(slots.GetRange(1, 3), s => Assert.Equal(FacetSlotKind.Tab, s.Kind));
        Assert.DoesNotContain(slots, s => s.Selected && s.Kind != FacetSlotKind.All);
        Assert.DoesNotContain(slots, s => s.Kind == FacetSlotKind.Sub);

        var (parent, sub) = HomeFacetStrip.Resolve(chips, "wat-chip");
        Assert.Null(parent);
        Assert.Null(sub);
    }
}
