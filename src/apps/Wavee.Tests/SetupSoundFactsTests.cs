using Wavee.Sdk.Streams;
using Xunit;

namespace Wavee.Tests;

// The Sound & storage page's pure arithmetic (App/SetupSoundFacts.cs): streaming kbps/GB-per-hour readouts (pinned
// against the literal copy baked into settings.playback.quality*Sub so the two can never drift apart), the cache
// budget share the stage's ProgressBar draws, and the crossfade-duration / metadata-budget combo index lookups.
public class SetupSoundFactsTests
{
    // ── streaming readout: FormatGb(GbPerHour(kbps)) must equal the en-US.json quality*Sub copy ──────────────────────

    [Theory]
    [InlineData(320, "0.14")]   // settings.playback.quality*Sub: "320 kbps - about 0.14 GB/hour"
    [InlineData(160, "0.07")]   // "160 kbps - about 0.07 GB/hour"
    [InlineData(96, "0.04")]    // "96 kbps - about 0.04 GB/hour"
    public void FormatGb_OfGbPerHour_MatchesTheQualitySubCopy(int kbps, string expected)
    {
        Assert.Equal(expected, SetupSoundFacts.FormatGb(SetupSoundFacts.GbPerHour(kbps)));
    }

    [Theory]
    [InlineData(0, 96)]
    [InlineData(1, 160)]
    [InlineData(2, 320)]
    [InlineData(3, 320)]    // clamps past the enabled range (Lossless has no kbps figure here)
    [InlineData(-1, 96)]
    public void Kbps_ClampsToTheThreeEnabledTiers(int quality, int expectedKbps)
    {
        Assert.Equal(expectedKbps, SetupSoundFacts.Kbps(quality));
    }

    // ── EffectivePercent: 0 (stored "Auto") reads as 10%, everything else clamps to 1..90 ──────────────────────────────

    [Theory]
    [InlineData(0, 10)]
    [InlineData(-5, 10)]
    [InlineData(1, 1)]
    [InlineData(45, 45)]
    [InlineData(90, 90)]
    [InlineData(150, 90)]
    public void EffectivePercent_ZeroIsAuto_ElseClampsOneToNinety(int stored, int expected)
    {
        Assert.Equal(expected, SetupSoundFacts.EffectivePercent(stored));
    }

    // ── CacheShare: the stage ProgressBar's 0..1 fraction per budget mode ───────────────────────────────────────────────

    [Fact]
    public void CacheShare_DriveShare_IsEffectivePercentAsAFraction()
    {
        Assert.Equal(0.10, SetupSoundFacts.CacheShare(AudioCacheBudgetMode.DriveShare, 0, 0, 500_000_000_000L), 3);
    }

    [Fact]
    public void CacheShare_Fixed_IsBytesOverDriveTotal()
    {
        long fixedBytes = 32L << 30;              // 32 GiB
        long driveTotal = 500_000_000_000L;       // 500 GB (decimal)
        double share = SetupSoundFacts.CacheShare(AudioCacheBudgetMode.FixedBytes, fixedBytes, 0, driveTotal);
        Assert.Equal(0.0687, share, 4);
    }

    [Fact]
    public void CacheShare_Unlimited_IsAlwaysFull()
    {
        Assert.Equal(1.0, SetupSoundFacts.CacheShare(AudioCacheBudgetMode.Unlimited, 0, 0, null));
        Assert.Equal(1.0, SetupSoundFacts.CacheShare(AudioCacheBudgetMode.Unlimited, 0, 0, 500_000_000_000L));
    }

    [Fact]
    public void CacheShare_Fixed_WithUnknownDriveTotal_IsZero()
    {
        Assert.Equal(0.0, SetupSoundFacts.CacheShare(AudioCacheBudgetMode.FixedBytes, 32L << 30, 0, null));
    }

    // ── DriveLabel: a drive letter for a local path, "" for anything else ───────────────────────────────────────────────

    [Theory]
    [InlineData(@"C:\Users\foo\AppData\Local\Wavee\Cache\audio", "C:")]
    [InlineData(@"D:\Wavee\audio", "D:")]
    [InlineData(@"\\server\share\folder", "")]
    [InlineData("relative/path", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void DriveLabel_IsTheLocalDriveLetterOrEmpty(string? dir, string expected)
    {
        Assert.Equal(expected, SetupSoundFacts.DriveLabel(dir));
    }

    // ── ShortPath: verbatim up to three segments, else root + ellipsis + last two ───────────────────────────────────────

    [Theory]
    [InlineData(@"C:\Wavee\audio", @"C:\Wavee\audio")]                       // 2 segments -> verbatim
    [InlineData(@"C:\a\b\c", @"C:\a\b\c")]                                   // 3 segments -> verbatim
    [InlineData(@"C:\Users\foo\AppData\Local\Wavee\Cache\audio", @"C:\…\Cache\audio")]   // 7 segments -> collapsed
    public void ShortPath_CollapsesOnlyPastThreeSegments(string dir, string expected)
    {
        Assert.Equal(expected, SetupSoundFacts.ShortPath(dir));
    }

    [Fact]
    public void ShortPath_OfEmpty_IsEmpty()
    {
        Assert.Equal("", SetupSoundFacts.ShortPath(""));
        Assert.Equal("", SetupSoundFacts.ShortPath(null));
    }

    // ── the two combo-index lookups ──────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(2.0, 0)]
    [InlineData(5.0, 1)]
    [InlineData(8.0, 2)]
    [InlineData(12.0, 3)]
    [InlineData(6.0, 1)]     // nearest to 5s
    [InlineData(0.0, 0)]     // nearest to 2s
    public void SecondsIndex_PicksTheNearestOfferedDuration(double seconds, int expected)
    {
        Assert.Equal(expected, SetupSoundFacts.SecondsIndex(seconds));
    }

    [Theory]
    [InlineData(32L << 20, 0)]
    [InlineData(64L << 20, 1)]
    [InlineData(128L << 20, 2)]
    [InlineData(256L << 20, 3)]
    [InlineData(999L, 1)]     // unknown value -> falls back to index 1
    public void MetaBudgetIndex_MatchesTheTable_OrFallsBackToOne(long bytes, int expected)
    {
        Assert.Equal(expected, SetupSoundFacts.MetaBudgetIndex(bytes));
    }
}
