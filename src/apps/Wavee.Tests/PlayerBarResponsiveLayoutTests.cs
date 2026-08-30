using System;
using Xunit;

namespace Wavee.Tests;

public class PlayerBarResponsiveLayoutTests
{
    [Theory]
    [InlineData(300f, PlayerBarTier.Minimal)]
    [InlineData(439f, PlayerBarTier.Minimal)]
    [InlineData(440f, PlayerBarTier.Compact)]
    [InlineData(759f, PlayerBarTier.Compact)]
    [InlineData(760f, PlayerBarTier.Medium)]
    [InlineData(899f, PlayerBarTier.Medium)]
    [InlineData(900f, PlayerBarTier.Comfortable)]
    [InlineData(1099f, PlayerBarTier.Comfortable)]
    [InlineData(1100f, PlayerBarTier.Wide)]
    [InlineData(1239f, PlayerBarTier.Wide)]
    [InlineData(1240f, PlayerBarTier.Full)]
    public void NominalTier_UsesTheDocumentedBoundaries(float width, PlayerBarTier expected)
        => Assert.Equal(expected, PlayerBarResponsiveLayout.Nominal(width));

    [Theory]
    [InlineData(PlayerBarResponsiveLayout.CompactW, PlayerBarTier.Minimal, PlayerBarTier.Compact)]
    [InlineData(PlayerBarResponsiveLayout.MediumW, PlayerBarTier.Compact, PlayerBarTier.Medium)]
    [InlineData(PlayerBarResponsiveLayout.ComfortableW, PlayerBarTier.Medium, PlayerBarTier.Comfortable)]
    [InlineData(PlayerBarResponsiveLayout.WideW, PlayerBarTier.Comfortable, PlayerBarTier.Wide)]
    [InlineData(PlayerBarResponsiveLayout.FullW, PlayerBarTier.Wide, PlayerBarTier.Full)]
    public void Tier_WidensImmediatelyAndNarrowsAfterThe24DipBand(
        float threshold,
        PlayerBarTier lower,
        PlayerBarTier upper)
    {
        Assert.Equal(upper, PlayerBarResponsiveLayout.Resolve(threshold, lower, initialized: true));
        Assert.Equal(upper, PlayerBarResponsiveLayout.Resolve(
            threshold - PlayerBarResponsiveLayout.NarrowHysteresis,
            upper,
            initialized: true));
        Assert.Equal(lower, PlayerBarResponsiveLayout.Resolve(
            threshold - PlayerBarResponsiveLayout.NarrowHysteresis - 1f,
            upper,
            initialized: true));
    }

    [Theory]
    [InlineData(PlayerBarTier.Minimal)]
    [InlineData(PlayerBarTier.Compact)]
    [InlineData(PlayerBarTier.Medium)]
    [InlineData(PlayerBarTier.Comfortable)]
    [InlineData(PlayerBarTier.Wide)]
    [InlineData(PlayerBarTier.Full)]
    public void EveryTier_PreservesIdentityHeartAndDeviceRoute(PlayerBarTier tier)
    {
        var layout = PlayerBarLayout.ForTier(tier);

        Assert.True(layout.ShowSubtitle);
        Assert.True(layout.ShowLikeSlot);
        Assert.True(layout.ShowDevices);
    }

    [Fact]
    public void Compact_KeepsRemainingTimeButMovesSecondaryCommandsToOverflow()
    {
        var layout = PlayerBarLayout.ForTier(PlayerBarTier.Compact);

        Assert.True(layout.ShowTimesRemaining);
        Assert.False(layout.ShowTimesElapsed);
        Assert.False(layout.ShowPrevNext);
        Assert.False(layout.ShowVolumeButton);
        Assert.False(layout.ShowLyrics);
        Assert.False(layout.ShowShuffleRepeat);
    }

    [Fact]
    public void Minimal_RetainsOnlyTheEssentialTransportSurface()
    {
        var layout = PlayerBarLayout.ForTier(PlayerBarTier.Minimal);

        Assert.False(layout.ShowTimesElapsed);
        Assert.False(layout.ShowTimesRemaining);
        Assert.False(layout.ShowPrevNext);
        Assert.False(layout.ShowVolumeButton);
        Assert.False(layout.ShowLyrics);
        Assert.False(layout.ShowShuffleRepeat);
        Assert.False(layout.ShowQueue);
        Assert.False(layout.ShowExpand);
    }

    /// <summary>THE TRANSPORT FLOOR TABLE. A player-bar button is a pointer and touch target before it is a glyph, and
    /// WinUI's icon-button rung is 32 × 32 with a 16-DIP glyph; the primary transport keeps the roomy 40/20 rung
    /// wherever the row has space and never drops below 36/18. The tier table used to ramp secondaries 32/30/28/26 and
    /// the primary 36/36/34/30, so the three NARROWEST tiers — where a mis-click costs most — shipped sub-minimum
    /// targets. Pressure is absorbed by dropped commands and by the spacing rungs, never by the buttons.</summary>
    [Theory]
    [InlineData(PlayerBarTier.Minimal)]
    [InlineData(PlayerBarTier.Compact)]
    [InlineData(PlayerBarTier.Medium)]
    [InlineData(PlayerBarTier.Comfortable)]
    [InlineData(PlayerBarTier.Wide)]
    [InlineData(PlayerBarTier.Full)]
    public void EveryTier_MeetsTheTransportHitTargetFloors(PlayerBarTier tier)
    {
        var layout = PlayerBarLayout.ForTier(tier);

        Assert.True(layout.ButtonBox >= PlayerBarLayout.MinButtonBox,
            $"{tier}: secondary transport box {layout.ButtonBox} < {PlayerBarLayout.MinButtonBox}");
        Assert.True(layout.ButtonGlyph >= PlayerBarLayout.MinButtonGlyph,
            $"{tier}: secondary transport glyph {layout.ButtonGlyph} < {PlayerBarLayout.MinButtonGlyph}");
        Assert.True(layout.PrimaryBox >= PlayerBarLayout.MinPrimaryBox,
            $"{tier}: primary box {layout.PrimaryBox} < {PlayerBarLayout.MinPrimaryBox}");
        Assert.True(layout.PrimaryGlyph >= PlayerBarLayout.MinPrimaryGlyph,
            $"{tier}: primary glyph {layout.PrimaryGlyph} < {PlayerBarLayout.MinPrimaryGlyph}");
        // The primary is the bar's focal control: it is never SMALLER than a secondary, and its glyph never smaller either.
        Assert.True(layout.PrimaryBox >= layout.ButtonBox);
        Assert.True(layout.PrimaryGlyph >= layout.ButtonGlyph);
    }

    /// <summary>The floors are FLAT: the secondaries are the same target at the 300-DIP floor as on a 4K desktop, and
    /// the primary takes the roomy rung from Medium up. (A regression that re-introduced a per-tier ramp would still
    /// pass the floor theory above if it ramped 40→36→32; this pins the actual table.)</summary>
    [Fact]
    public void TransportMetrics_AreFlatAcrossTheLadder()
    {
        foreach (PlayerBarTier tier in Enum.GetValues<PlayerBarTier>())
        {
            var layout = PlayerBarLayout.ForTier(tier);
            Assert.Equal(PlayerBarLayout.MinButtonBox, layout.ButtonBox);
            Assert.Equal(PlayerBarLayout.MinButtonGlyph, layout.ButtonGlyph);

            bool roomy = tier >= PlayerBarTier.Medium;
            Assert.Equal(roomy ? PlayerBarLayout.PrimaryBoxRoomy : PlayerBarLayout.MinPrimaryBox, layout.PrimaryBox);
            Assert.Equal(roomy ? PlayerBarLayout.PrimaryGlyphRoomy : PlayerBarLayout.MinPrimaryGlyph, layout.PrimaryGlyph);
        }
    }

    /// <summary>The 300-DIP floor still balances after the floors landed: the Minimal row's FIXED furniture (padding,
    /// identity block, row gaps, primary, cluster gap, devices, overflow) must leave the SeekBar real width to grow
    /// into. If a future tier adds furniture at Minimal, this is the gate that says the seek line vanished.</summary>
    [Fact]
    public void MinimalTier_LeavesTheSeekBarRoomAtThe300DipFloor()
    {
        var l = PlayerBarLayout.ForTier(PlayerBarTier.Minimal);

        float fixedWidth =
            2f * l.RowPad                       // the row's horizontal padding
            + l.LeftW                           // art + metadata + heart
            + 2f * l.RowGap                     // left|centre and centre|right
            + l.PrimaryBox                      // play/pause
            + l.ClusterGap                      // transport group|seek row
            + 2f * l.ButtonBox;                 // devices + the "⋯" overflow (everything else is in the menu)

        Assert.False(l.ShowTimesElapsed);       // …which is why no time label is in the sum
        Assert.False(l.ShowTimesRemaining);
        Assert.True(300f - fixedWidth >= 40f,
            $"the Minimal row leaves the SeekBar only {300f - fixedWidth} DIP");
    }

    [Fact]
    public void Full_ExposesEveryDesktopPlayerCommand()
    {
        var layout = PlayerBarLayout.ForTier(PlayerBarTier.Full);

        Assert.True(layout.ShowExpand);
        Assert.True(layout.ShowQueue);
        Assert.True(layout.ShowVolumeSlider);
        Assert.True(layout.ShowShuffleRepeat);
        Assert.True(layout.ShowVolumeButton);
        Assert.True(layout.ShowLyrics);
        Assert.True(layout.ShowRemoteDeviceLine);
        Assert.True(layout.ShowTimesElapsed);
        Assert.True(layout.ShowTimesRemaining);
        Assert.True(layout.ShowPrevNext);
    }
}
