using FluentAssertions;
using Wavee.Controls.Lyrics.Models.Lyrics;
using Wavee.UI.Services;

namespace Wavee.UI.Tests.Services;

public sealed class LyricsProviderSelectorTests
{
    [Fact]
    public void SelectBestExternalResult_PrefersAnyNonMusixmatchProvider()
    {
        var qqMusic = new LyricsSelectionCandidate(
            "QQMusic",
            CreateLyricsData(lineCount: 51, hasSyllable: true),
            ContentScore: 0.72,
            PreferenceBonus: 0);

        var musixmatch = new LyricsSelectionCandidate(
            "Musixmatch",
            CreateLyricsData(lineCount: 50, hasSyllable: true),
            ContentScore: 1.0,
            PreferenceBonus: 500);

        var selected = LyricsProviderSelector.SelectBestExternalResult([musixmatch, qqMusic]);

        selected.Should().NotBeNull();
        selected!.Provider.Should().Be("QQMusic");
        selected.Reason.Should().NotContain("Fallback-only source");
    }

    [Fact]
    public void SelectBestExternalResult_UsesMusixmatchWhenItIsTheOnlyCandidate()
    {
        var musixmatch = new LyricsSelectionCandidate(
            "Musixmatch",
            CreateLyricsData(lineCount: 50, hasSyllable: true),
            ContentScore: 0.91,
            PreferenceBonus: 0);

        var selected = LyricsProviderSelector.SelectBestExternalResult([musixmatch]);

        selected.Should().NotBeNull();
        selected!.Provider.Should().Be("Musixmatch");
        selected.Reason.Should().Contain("Fallback-only source");
    }

    [Fact]
    public void SelectBestExternalResult_IgnoresTimingUnreliableCandidates()
    {
        var qqMusic = new LyricsSelectionCandidate(
            "QQMusic",
            CreateLyricsData(lineCount: 51, hasSyllable: true),
            ContentScore: 1.0,
            PreferenceBonus: 500,
            IsTimingReliable: false,
            TimingReason: "drift");

        var netease = new LyricsSelectionCandidate(
            "Netease",
            CreateLyricsData(lineCount: 45, hasSyllable: false),
            ContentScore: 0.6,
            PreferenceBonus: 0);

        var selected = LyricsProviderSelector.SelectBestExternalResult([qqMusic, netease]);

        selected.Should().NotBeNull();
        selected!.Provider.Should().Be("Netease");
    }

    [Fact]
    public void SelectBestExternalResult_ReturnsNullWhenEveryExternalCandidateHasUnreliableTiming()
    {
        var qqMusic = new LyricsSelectionCandidate(
            "QQMusic",
            CreateLyricsData(lineCount: 51, hasSyllable: true),
            ContentScore: 1.0,
            PreferenceBonus: 0,
            IsTimingReliable: false,
            TimingReason: "drift");

        var musixmatch = new LyricsSelectionCandidate(
            "Musixmatch",
            CreateLyricsData(lineCount: 50, hasSyllable: true),
            ContentScore: 1.0,
            PreferenceBonus: 0,
            IsTimingReliable: false,
            TimingReason: "drift");

        var selected = LyricsProviderSelector.SelectBestExternalResult([qqMusic, musixmatch]);

        selected.Should().BeNull();
    }

    private static LyricsData CreateLyricsData(int lineCount, bool hasSyllable)
    {
        var lines = Enumerable.Range(0, lineCount)
            .Select(i => new LyricsLine
            {
                PrimaryText = $"line {i}",
                IsPrimaryHasRealSyllableInfo = hasSyllable,
            })
            .ToList();

        return new LyricsData(lines);
    }
}
