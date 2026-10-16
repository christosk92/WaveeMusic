// ── Wavee.Tests/Theming/ClassicUnavailableBarTests.cs — the unavailable-songs bar's record per window tier (contract 1.22) ──────────
//
// Boards C-Unavailable (Standard) and C-Unavailable-Compact: the record each detail tier carries (`ClassicDetailTier.Unavailable`), the 40 target at
// Compact, the board's plate heights, which tiers word the reasons, and the band the hero arm's clip inset adds (`TrackChromeTemplates.UnavailableBarExtent`).
// The filter half (what the bar counts and what "Hide them" writes) is `TrackFilterModelTests`.

using FluentGpu.Controls;
using FluentGpu.Foundation;
using Wavee.Themes.Classic;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.Tests.Theming;

public sealed class ClassicUnavailableBarTests
{
    [Fact]
    public void Compact_draws_the_board_bar_with_its_40_button_and_the_short_sentence()
    {
        var g = ClassicDetailTiers.Compact.Unavailable;
        Assert.Same(ClassicUnavailableBar.Compact, g);
        Assert.Equal(ControlSize.Large, g.ButtonSize);                 // 40 tall: the brief's Compact target
        Assert.True(ControlMetrics.For(g.ButtonSize).MinHeight >= ClassicDetailTiers.CompactTarget);
        Assert.Equal(58f, g.Height);                                     // 8 + 40 + 8 and the 1-DIP border twice
        Assert.Equal(g.Padding.Top + ControlMetrics.For(g.ButtonSize).MinHeight + g.Padding.Bottom + 2f, g.Height);
        Assert.Equal(new Edges4(12f, 8f, 8f, 8f), g.Padding);
        Assert.Equal(12f, g.GapBelow);
        Assert.False(g.Explains);
    }

    [Fact]
    public void Standard_and_Huge_word_the_reasons_on_the_50_plate()
    {
        foreach (var record in new[] { ClassicDetailTiers.Standard, ClassicDetailTiers.Huge, ClassicDetailTiers.Huge4K })
        {
            var g = record.Unavailable;
            Assert.Same(ClassicUnavailableBar.Standard, g);
            Assert.True(g.Explains);
            Assert.Equal(50f, g.Height);
            Assert.Equal(g.Padding.Top + ControlMetrics.For(g.ButtonSize).MinHeight + g.Padding.Bottom + 2f, g.Height);
            Assert.Equal(8f, g.GapBelow);
        }
    }

    [Fact]
    public void Small_keeps_Standards_plate_with_the_short_sentence()
    {
        var g = ClassicDetailTiers.Small.Unavailable;
        Assert.Same(ClassicUnavailableBar.Small, g);
        Assert.False(g.Explains);
        Assert.Equal(ClassicUnavailableBar.Standard.Height, g.Height);
        Assert.Equal(ClassicUnavailableBar.Standard.ButtonSize, g.ButtonSize);
    }

    [Theory]
    [InlineData(WidthTier.Compact)]
    [InlineData(WidthTier.Small)]
    [InlineData(WidthTier.Standard)]
    [InlineData(WidthTier.Huge)]
    public void The_band_is_the_plate_and_the_air_under_it(WidthTier tier)
    {
        var g = ClassicUnavailableBar.For(tier);
        Assert.Same(g, ClassicDetailTiers.For(tier).Unavailable);
        Assert.Equal(g.Height + g.GapBelow, g.Extent);
        Assert.Equal(10f, g.Gap);
        Assert.Equal(16f, g.Glyph);
        Assert.Equal(14f, g.TextSize);
    }

    [Fact]
    public void The_slot_reveals_its_height_with_the_disclosure_motion()
    {
        var m = ClassicUnavailableBar.Motion;
        Assert.Equal(TransitionChannels.Size, m.Channels);
        Assert.Equal(SizeMode.FlowReveal, m.Size);
    }
}
