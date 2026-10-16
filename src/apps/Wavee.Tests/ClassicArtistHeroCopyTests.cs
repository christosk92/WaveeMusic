// ── Wavee.Tests/ClassicArtistHeroCopyTests.cs — the artist hero's copy column and veil hold (Classic tuning D18, D21) ──────────────────
//
// The tuned canvas's `Main` board, D18: at medium widths the bio ran over the bright half of the photo (copy ~95 % of the hero, the veil gone at 62 %).
// The centred photo arm takes `min(CopyMax, max(360, 0.56 w - gutter))` and the veil's hold follows the copy's right edge. Stacked tiers put no type on the
// photo and keep the full band. D21: Follow wears a person, never the Like heart.

using FluentGpu.Controls;
using FluentGpu.Foundation;
using Wavee.Themes.Classic;
using Xunit;

namespace Wavee.Tests;

public class ClassicArtistHeroCopyTests
{
    /// <summary>The tuned Standard form at <paramref name="width"/> (held from Wide) and its metrics at the content tier's gutter.</summary>
    static (ArtistHeroMetrics M, ClassicArtistHeroForm F) At(float width)
    {
        var f = ClassicArtistTiers.StandardForm(ClassicArtistTiers.HeroTierFor(width, (int)ArtistHeroTier.Wide));
        return (f.Metrics(ClassicTiers.Page((Wavee.Theming.Contracts.WidthTier)ClassicTiers.Content.Resolve(width, -1)).Gutter), f);
    }

    [Theory]
    [InlineData(752f)]    // a 1072 window with the 320 pane: Medium (was 752 - 2 x gutter, ~91 % of the hero)
    [InlineData(840f)]    // the top of Medium
    [InlineData(1120f)]   // a 1440 window with the 320 pane: Wide (was 1048, ~94 % of the hero)
    [InlineData(1600f)]
    public void On_the_photo_tiers_the_copy_ends_near_56_percent_of_the_hero(float width)
    {
        var (m, f) = At(width);
        Assert.False(m.Stacked);
        float copy = ClassicArtistRules.HeroCopyWidth(width, in m, f);
        Assert.Equal(ClassicArtistRules.HeroCopyShare * width - m.Gutter, copy, 3);
        Assert.Equal(ClassicArtistRules.HeroCopyShare, (m.Gutter + copy) / width, 3);
        Assert.Equal(ClassicArtistRules.HeroCopyShare, ClassicArtistRules.HeroVeilHold(width, in m, f), 3);
    }

    [Fact]
    public void A_narrow_Medium_hero_keeps_a_360_measure_and_a_very_wide_one_stops_at_the_tier_maximum()
    {
        var (medium, mf) = At(620f);
        Assert.Equal(ArtistHeroTier.Medium, medium.Tier);
        Assert.Equal(360f, ClassicArtistRules.HeroCopyWidth(620f, in medium, mf));
        Assert.Equal((medium.Gutter + 360f) / 620f, ClassicArtistRules.HeroVeilHold(620f, in medium, mf), 4);

        var (wide, wf) = At(2680f);
        Assert.Equal(ArtistHeroTier.Wide, wide.Tier);
        Assert.Equal(1120f, ClassicArtistRules.HeroCopyWidth(2680f, in wide, wf));
        Assert.True(ClassicArtistRules.HeroVeilHold(2680f, in wide, wf) < ClassicArtistRules.HeroCopyShare);
    }

    [Theory]
    [InlineData(500f)]
    [InlineData(340f)]
    public void Stacked_tiers_keep_the_full_band_and_have_no_side_veil(float width)
    {
        var (m, f) = At(width);
        Assert.True(m.Stacked);
        Assert.Equal(MathF.Min(m.CopyMaxWidth, width - 2f * m.Gutter), ClassicArtistRules.HeroCopyWidth(width, in m, f));
        Assert.True(float.IsNaN(ClassicArtistRules.HeroVeilHold(width, in m, f)));
    }

    [Fact]
    public void The_veil_holds_to_the_copy_edge_and_keeps_its_shoulder_after_it()
    {
        var accent = new ColorF(0.2f, 0.3f, 0.5f, 1f);
        float[] Offsets(GradientSpec g) => [.. g.Stops.Select(s => s.Offset)];

        Assert.Equal([0f, 0.30f, 0.62f, 1f], Offsets(Controls.ArtistHeroVeil(accent, vertical: false)));   // the daylist card: unchanged
        float[] held = Offsets(Controls.ArtistHeroVeil(accent, vertical: false, hold: 0.56f));
        Assert.Equal(0.56f, held[1], 4);
        Assert.Equal(0.88f, held[2], 4);
        float[] clamped = Offsets(Controls.ArtistHeroVeil(accent, vertical: false, hold: 0.71f));
        Assert.True(clamped[2] < 1f);
        Assert.Equal([0.96f, 0.92f, 0.35f, 0f], Controls.ArtistHeroVeil(accent, vertical: false, hold: 0.56f).Stops.Select(s => s.Color.A));
    }

    [Fact]
    public void Follow_wears_a_person_and_Pre_save_keeps_the_heart()
    {
        Assert.Equal(SegoeGlyphs.AddFriend, Controls.FollowToggle.Glyph(Controls.FollowVerb.Follow, on: false));
        Assert.Equal(Icons.Contact, Controls.FollowToggle.Glyph(Controls.FollowVerb.Follow, on: true));
        Assert.NotEqual(Icons.Heart, Controls.FollowToggle.Glyph(Controls.FollowVerb.Follow, on: false));
        Assert.NotEqual(Icons.HeartFill, Controls.FollowToggle.Glyph(Controls.FollowVerb.Follow, on: true));
        Assert.Equal(Icons.Heart, Controls.FollowToggle.Glyph(Controls.FollowVerb.PreSave, on: false));
        Assert.Equal(Icons.HeartFill, Controls.FollowToggle.Glyph(Controls.FollowVerb.PreSave, on: true));
    }
}
