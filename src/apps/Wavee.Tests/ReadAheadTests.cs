using Xunit;
using static Wavee.Spotify.Audio;

namespace Wavee.Tests;

public sealed class ReadAheadTests
{
    const int FlacRate = 125_000;
    const long FastLink = 10_000_000;      // well past 3x the file's byte rate: the big tier

    [Fact]
    public void A_lossless_track_that_has_not_been_played_long_reads_only_the_probation_window()
    {
        Assert.Equal(ReadAhead.LosslessProbationSeconds, ReadAhead.For(Format.Flac, false, FastLink, FlacRate, 0));
        Assert.Equal(ReadAhead.LosslessProbationSeconds, ReadAhead.For(Format.Flac24, false, FastLink, FlacRate, ReadAhead.ProbationMs - 1));
    }

    [Fact]
    public void A_lossless_track_past_the_probation_reads_ninety_seconds_not_the_whole_file()
    {
        Assert.Equal(ReadAhead.LosslessSeconds, ReadAhead.For(Format.Flac, false, FastLink, FlacRate, ReadAhead.ProbationMs));
        Assert.True(ReadAhead.LosslessSeconds < 600);
    }

    [Fact]
    public void A_slow_or_metered_link_keeps_its_own_smaller_tier_for_lossless()
    {
        Assert.Equal(30, ReadAhead.For(Format.Flac, false, 1, FlacRate, 60_000));
        Assert.Equal(10, ReadAhead.For(Format.Flac, true, FastLink, FlacRate, 60_000));
    }

    [Theory]
    [InlineData(Format.OggVorbis320)]
    [InlineData(Format.Mp3)]
    public void Lossy_formats_keep_the_existing_tiers(Format format)
    {
        Assert.Equal(Ring.ReadAheadSeconds(false, FastLink, 40_000), ReadAhead.For(format, false, FastLink, 40_000, 0));
        Assert.Equal(Ring.ReadAheadSeconds(false, 1, 40_000), ReadAhead.For(format, false, 1, 40_000, 0));
    }
}
