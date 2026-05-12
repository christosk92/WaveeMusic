using FluentAssertions;
using Wavee.Local.Classification;

namespace Wavee.Local.Tests.Classification;

/// <summary>
/// Golden test cases driven by the user's actual filenames from the design-doc
/// screenshot. Each one is documented with the expected classification rationale.
/// </summary>
public class LocalContentClassifierTests
{
    [Theory]
    // The user's actual library — these are the cases the redesign exists to fix.
    [InlineData(@"D:\Videos\Person.of.Interest.S01E01.1080p.BluRay.x265-RARBG.mkv",
                LocalContentKind.TvEpisode)]
    [InlineData(@"D:\Videos\Person.of.Interest.S01E15.1080p.BluRay.x265-RARBG.mkv",
                LocalContentKind.TvEpisode)]
    [InlineData(@"D:\Videos\MusicVideos\Once We Were Us 2025 1080p Korean WEB-DL HEVC x265 BONE.mkv",
                LocalContentKind.Movie)]
    [InlineData(@"D:\Music\01 - Pink Floyd - Speak to Me.mp3",
                LocalContentKind.Music)]
    [InlineData(@"D:\Music\song.flac",
                LocalContentKind.Music)]
    [InlineData(@"D:\Other\readme.txt",
                LocalContentKind.Other)]
    public void Classify_returns_expected_kind_for_well_formed_filenames(string path, LocalContentKind expected)
    {
        // Long-runtime video (movies/episodes) vs short clip (music video):
        var duration = expected switch
        {
            LocalContentKind.Movie     => 110L * 60 * 1000,  // 110 min
            LocalContentKind.TvEpisode => 44L * 60 * 1000,   // 44 min
            LocalContentKind.MusicVideo => 5L * 60 * 1000,   // 5 min
            LocalContentKind.Music     => 3L * 60 * 1000,    // 3 min
            _ => 0,
        };
        var kind = LocalContentClassifier.Classify(path, hasArtistTag: false, hasAlbumTag: false, durationMs: duration);
        kind.Should().Be(expected);
    }

    [Fact]
    public void TV_misfiled_in_a_MusicVideos_folder_is_still_a_movie_when_signals_are_strong()
    {
        // Korean movie inside \MusicVideos\ — per design finding, the per-folder
        // soft hint must NOT override clear year+HEVC+long-runtime signals.
        const string path = @"D:\Videos\MusicVideos\Once We Were Us 2025 1080p Korean WEB-DL HEVC x265 BONE.mkv";
        var kind = LocalContentClassifier.Classify(path, false, false, durationMs: 100L * 60 * 1000,
                                                   expectedKind: LocalContentKind.MusicVideo);
        kind.Should().Be(LocalContentKind.Movie);
    }

    [Fact]
    public void Music_video_with_short_runtime_and_MV_marker_is_a_music_video()
    {
        const string path = @"D:\Videos\장범준 - 흔들리는 꽃들 속에서 네 샴푸향이 느껴진거야 MV - Stone Music Entertainment.mp4";
        var kind = LocalContentClassifier.Classify(path, false, false, durationMs: 3L * 60 * 1000);
        kind.Should().Be(LocalContentKind.MusicVideo);
    }

    [Fact]
    public void DavidLaid_slowed_reverb_is_a_music_video()
    {
        const string path = @"D:\Videos\DAVID LAID - BAD ROMANCE (slowed + reverb) GYM MOTIVATION - Aesthetic (1080p).mkv";
        var kind = LocalContentClassifier.Classify(path, false, false, durationMs: 4L * 60 * 1000);
        kind.Should().Be(LocalContentKind.MusicVideo);
    }
}
