using FluentAssertions;
using Wavee.Local.Classification;

namespace Wavee.Local.Tests.Classification;

public class LocalFilenameParserTests
{
    [Theory]
    [InlineData("Person.of.Interest.S01E01.1080p.BluRay.x265-RARBG.mkv", 1, 1)]
    [InlineData("Person.of.Interest.S01E18.1080p.BluRay.x265-RARBG.mkv", 1, 18)]
    [InlineData("Show.Name.S02E10.HDTV.x264.mkv", 2, 10)]
    [InlineData("ShowName 1x05 Title.mp4", 1, 5)]
    public void TryParseEpisode_extracts_season_and_episode(string filename, int s, int e)
    {
        var ok = LocalFilenameParser.TryParseEpisode(filename, out var series, out var season, out var ep, out _);
        ok.Should().BeTrue();
        season.Should().Be(s);
        ep.Should().Be(e);
        series.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("Person.of.Interest.S01E01.1080p.BluRay.x265-RARBG.mkv", "Person of Interest")]
    [InlineData("Some.Show.Name.S01E01.mkv", "Some Show Name")]
    public void TryParseEpisode_cleans_series_name(string filename, string expectedSeriesContains)
    {
        LocalFilenameParser.TryParseEpisode(filename, out var series, out _, out _, out _);
        series.Should().Contain(expectedSeriesContains);
    }

    [Theory]
    [InlineData("Once We Were Us 2025 1080p Korean WEB-DL HEVC x265 BONE.mkv", "Once We Were Us", 2025)]
    [InlineData("Movie Title (2023) BluRay.mkv", "Movie Title", 2023)]
    [InlineData("The Big Movie 1999.mp4", "The Big Movie", 1999)]
    public void TryParseMovie_extracts_title_and_year(string filename, string expectedTitle, int year)
    {
        var ok = LocalFilenameParser.TryParseMovie(filename, out var title, out var y);
        ok.Should().BeTrue();
        title.Should().Contain(expectedTitle);
        y.Should().Be(year);
    }

    [Fact]
    public void TryParseMovie_rejects_episode_shaped_filenames()
    {
        var ok = LocalFilenameParser.TryParseMovie("Show.S01E01.1080p.mkv", out _, out _);
        ok.Should().BeFalse();
    }

    [Theory]
    [InlineData("DAVID LAID - BAD ROMANCE (slowed + reverb) GYM MOTIVATION - Aesthetic (1080p).mkv")]
    [InlineData("Person.of.Interest.S01E01.mkv")]
    [InlineData("Movie Name 2020 1080p WEB-DL.mkv")]
    public void HasReleaseGroupMarkers_detects_video_markers(string filename)
    {
        LocalFilenameParser.HasReleaseGroupMarkers(filename).Should().BeTrue();
    }

    [Theory]
    [InlineData("plain_track.mp3")]
    [InlineData("01 Song Title.flac")]
    public void HasReleaseGroupMarkers_returns_false_for_clean_audio(string filename)
    {
        LocalFilenameParser.HasReleaseGroupMarkers(filename).Should().BeFalse();
    }

    [Theory]
    [InlineData("Artist - Song MV.mp4")]
    [InlineData("Track (Official Video).mp4")]
    [InlineData("Title (slowed + reverb).mp3")]
    public void HasMusicVideoSignals_detects_MV_signals(string filename)
    {
        LocalFilenameParser.HasMusicVideoSignals(filename).Should().BeTrue();
    }

    [Fact]
    public void StripReleaseGroupMarkers_is_idempotent()
    {
        const string filename = "Once We Were Us 2025 1080p Korean WEB-DL HEVC x265 BONE";
        var once = LocalFilenameParser.StripReleaseGroupMarkers(filename);
        var twice = LocalFilenameParser.StripReleaseGroupMarkers(once);
        twice.Should().Be(once);
    }

    [Theory]
    [InlineData("01 - Pink Floyd - Speak to Me.mp3", "Pink Floyd", "Speak to Me", 1)]
    [InlineData("Artist - Title.mp3", "Artist", "Title", null)]
    [InlineData("01_Song.mp3", null, "Song", 1)]
    public void TryParseMusicTrack_parses_common_patterns(string filename, string? expectedArtist, string expectedTitle, int? expectedTrack)
    {
        var hints = LocalFilenameParser.TryParseMusicTrack(filename);
        hints.Title.Should().Contain(expectedTitle);
        if (expectedArtist is not null) hints.Artist.Should().Be(expectedArtist);
        if (expectedTrack is not null) hints.TrackNumber.Should().Be(expectedTrack);
    }

    [Fact]
    public void TryParseMusicTrack_uses_folder_hints_for_artist_and_album()
    {
        var hints = LocalFilenameParser.TryParseMusicTrack(
            filename: "01 Song.mp3",
            parentFolder: "Dark Side of the Moon",
            grandparentFolder: "Pink Floyd");
        hints.Artist.Should().Be("Pink Floyd");
        hints.Album.Should().Be("Dark Side of the Moon");
        hints.TrackNumber.Should().Be(1);
    }

    [Fact]
    public void TryParseMusicTrack_recognises_disc_subfolder()
    {
        var hints = LocalFilenameParser.TryParseMusicTrack(
            filename: "01 Song.flac",
            parentFolder: "CD2",
            grandparentFolder: "Album Name");
        hints.DiscNumber.Should().Be(2);
        hints.Album.Should().Be("Album Name");
    }
}
