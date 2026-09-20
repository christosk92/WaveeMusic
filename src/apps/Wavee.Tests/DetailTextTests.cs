// ── Wavee.Tests/DetailTextTests.cs — the identity header's strings: eyebrow, collaborators gate, byline, meta lines ──
//
// NEW in 0.3. 0.2.9 composed these inside engine-bound code (`DetailRail.EyebrowText`/`ShowCollaborators`,
// `DetailVerticalHero`'s band byline, `DetailPage.MapAlbum/MapPlaylist/MapShow/MapLiked`) and never tested them; they
// are `Detail.Text` now. Every expected string is built from the SAME `Loc.Get(Strings.*)` / typed `Strings.*` call the
// rule makes — never an English literal — so a translation cannot break a fact, only a changed rule can. The facts that
// matter are the SHAPES: which rung the byline falls to (W12), which segment a thin album drops, that visibility unknown
// never reads as "Private", and that the eyebrow answers for every kind (W27's asymmetry is the rail's, not this rule's).

using FluentGpu.Localization;
using Xunit;
using Config = Wavee.Detail.Config;
using Text = Wavee.Detail.Text;

namespace Wavee.Tests;

public class DetailTextTests
{
    const long Ms1h14 = 74 * 60_000L;   // "1 hr 14 min"

    // ── the eyebrow (DetailRail.EyebrowText, ch 03 W27) ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(AlbumKind.Album, Strings.Detail.Badge.Album)]
    [InlineData(AlbumKind.EP, Strings.Detail.Badge.Ep)]
    [InlineData(AlbumKind.Single, Strings.Detail.Badge.Single)]
    [InlineData(AlbumKind.Compilation, Strings.Detail.Badge.Compilation)]
    public void KindLabel_IsTheReleaseBadge(AlbumKind kind, string key)
        => Assert.Equal(Loc.Get(key), Text.KindLabel(kind));

    /// <summary>TypeYear: "ALBUM · 2013"; the kind alone when the year is unknown. An EP shares the Album CONFIG but keeps
    /// its own "EP" badge in the eyebrow.</summary>
    [Fact]
    public void Eyebrow_TypeYear_IsKindDotYear_OrTheKindAlone()
    {
        Assert.Equal(Loc.Get(Strings.Detail.Badge.Album) + " · 2013",
            Text.Eyebrow(BadgeStyle.TypeYear, AlbumKind.Album, 2013, collaborative: false, isPublic: true, visibilityKnown: true));
        Assert.Equal(Loc.Get(Strings.Detail.Badge.Ep) + " · 2019",
            Text.Eyebrow(BadgeStyle.TypeYear, AlbumKind.EP, 2019, collaborative: false, isPublic: true, visibilityKnown: true));
        Assert.Equal(Loc.Get(Strings.Detail.Badge.Single),
            Text.Eyebrow(BadgeStyle.TypeYear, AlbumKind.Single, 0, collaborative: false, isPublic: true, visibilityKnown: true));
    }

    /// <summary>OwnerRow: Collaborative wins the one slot (a collaborative list is private by default, so "Private" there
    /// says less); Private only once visibility is KNOWN; a public solo list says just "Playlist".</summary>
    [Theory]
    [InlineData(true, false, true, Strings.Nav.PlaylistCollaborative)]
    [InlineData(true, true, true, Strings.Nav.PlaylistCollaborative)]
    [InlineData(true, false, false, Strings.Nav.PlaylistCollaborative)]
    [InlineData(false, false, true, Strings.Nav.PlaylistPrivate)]
    [InlineData(false, false, false, Strings.Nav.Playlist)]   // unknown visibility must never read as private (ch 03 §7)
    [InlineData(false, true, true, Strings.Nav.Playlist)]
    public void Eyebrow_OwnerRow_CarriesTheAccessWhenThereIsSomethingToCarry(
        bool collaborative, bool isPublic, bool visibilityKnown, string key)
        => Assert.Equal(Loc.Get(key),
            Text.Eyebrow(BadgeStyle.OwnerRow, AlbumKind.Album, 0, collaborative, isPublic, visibilityKnown));

    [Fact]
    public void Eyebrow_NoBadges_IsYourLibrary()
        => Assert.Equal(Loc.Get(Strings.Nav.YourLibrary),
            Text.Eyebrow(BadgeStyle.None, AlbumKind.Album, 2013, collaborative: true, isPublic: false, visibilityKnown: true));

    /// <summary>The rule answers for EVERY kind — the vertical hero shows it unconditionally and reserves its row off the
    /// same answer. Only the two-column rail drops it for non-TypeYear kinds (W27), and that is the rail's choice.</summary>
    [Fact]
    public void Eyebrow_AnswersForEveryConfig()
    {
        foreach (var cfg in new[] { Config.Playlist, Config.Album, Config.Single, Config.Compilation, Config.Liked, Config.Show })
            Assert.False(string.IsNullOrEmpty(
                Text.Eyebrow(cfg.Kind, cfg.Badges, AlbumKind.Album, 0, collaborative: false, isPublic: true, visibilityKnown: false)),
                $"no eyebrow for {cfg.Kind}");
    }

    /// <summary>A show's TypeYear badge is "Podcast", not an album kind; every other kind forwards unchanged.</summary>
    [Fact]
    public void Eyebrow_KindAware_AShowIsAPodcast()
    {
        Assert.Equal(Loc.Get(Strings.Podcast.Show),
            Text.Eyebrow(DetailKind.Show, BadgeStyle.TypeYear, AlbumKind.Album, 0, false, true, true));
        Assert.Equal(Text.Eyebrow(BadgeStyle.TypeYear, AlbumKind.EP, 2019, false, true, true),
            Text.Eyebrow(DetailKind.Album, BadgeStyle.TypeYear, AlbumKind.EP, 2019, false, true, true));
        Assert.Equal(Text.Eyebrow(BadgeStyle.OwnerRow, AlbumKind.Album, 0, true, false, true),
            Text.Eyebrow(DetailKind.Playlist, BadgeStyle.OwnerRow, AlbumKind.Album, 0, true, false, true));
    }

    // ── the collaborator pile's gate (DetailRail.ShowCollaborators) ─────────────────────────────────────────────────

    [Theory]
    [InlineData(0, true, false)]    // no members: the owner row, whatever the flag
    [InlineData(0, false, false)]
    [InlineData(1, true, true)]     // a collaborative list with one member
    [InlineData(1, false, false)]   // one member on a solo list is just the owner
    [InlineData(2, false, true)]    // two or more always earn the pile
    [InlineData(5, true, true)]
    public void ShowCollaborators_NeedsMembersAndEitherTheFlagOrTwo(int count, bool collaborative, bool expected)
        => Assert.Equal(expected, Text.ShowCollaborators(count, collaborative));

    // ── the band byline (DetailVerticalHero, ch 03 W12): four rungs ─────────────────────────────────────────────────

    [Fact]
    public void Byline_Rung1_OwnerAndMeta()
        => Assert.Equal("Spotify · 50 songs", Text.Byline("Spotify", "50 songs", "Playlist"));

    [Fact]
    public void Byline_Rung2_OwnerAlone()
    {
        Assert.Equal("Spotify", Text.Byline("Spotify", null, "Playlist"));
        Assert.Equal("Spotify", Text.Byline("Spotify", "", "Playlist"));
    }

    /// <summary>An album has no owner, so its byline is its meta line alone.</summary>
    [Fact]
    public void Byline_Rung3_MetaAlone()
    {
        Assert.Equal("13 songs · 2013", Text.Byline(null, "13 songs · 2013", "ALBUM · 2013"));
        Assert.Equal("13 songs · 2013", Text.Byline("", "13 songs · 2013", "ALBUM · 2013"));
    }

    [Fact]
    public void Byline_Rung4_TheEyebrow_ThenNothing()
    {
        Assert.Equal("Your Library", Text.Byline(null, null, "Your Library"));
        Assert.Null(Text.Byline(null, null, ""));
        Assert.Null(Text.Byline("", "", ""));
    }

    // ── the meta lines (DetailPage.Map*) ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AlbumMeta_DurationsKnown_IsCountDurationYear()
    {
        string meta = Text.AlbumMeta(13, Ms1h14, durationsKnown: true, year: 2013)!;
        Assert.Equal(Strings.Detail.MetaLineYear(Strings.Detail.SongCount(13), Track.Format.TotalTime(Ms1h14), 2013), meta);
        // The known and the thin phrasing are two different lines (not one line with a blank segment).
        Assert.NotEqual(Text.AlbumMeta(13, Ms1h14, durationsKnown: false, year: 2013), meta);
    }

    /// <summary>While any row is thin its zero duration would make the sum a lie ("39 songs · 1 min" for a 2 hr 41 min
    /// record), so the phrase DROPS the duration segment rather than assert a number we know is wrong.</summary>
    [Fact]
    public void AlbumMeta_DurationsUnknown_DropsTheDurationSegment()
    {
        string meta = Text.AlbumMeta(39, 60_000, durationsKnown: false, year: 2027)!;
        Assert.Equal(Strings.Detail.MetaLineYearPending(Strings.Detail.SongCount(39), 2027), meta);
        Assert.DoesNotContain(Track.Format.TotalTime(60_000), meta);
    }

    [Fact]
    public void AlbumMeta_UnknownYear_DropsTheYear()
    {
        Assert.Equal(Strings.Detail.MetaLine(Strings.Detail.SongCount(13), Track.Format.TotalTime(Ms1h14)),
            Text.AlbumMeta(13, Ms1h14, durationsKnown: true, year: 0));
        Assert.Equal(Strings.Detail.SongCount(13), Text.AlbumMeta(13, Ms1h14, durationsKnown: false, year: 0));
    }

    [Fact]
    public void PlaylistMeta_WithSaves_IsCountSavesDuration()
        => Assert.Equal(
            Strings.Detail.MetaLineSaved(Strings.Detail.SongCount(50), Text.SaveCountText(812), Track.Format.TotalTime(Ms1h14)),
            Text.PlaylistMeta(50, Ms1h14, durationsKnown: true, saves: 812));

    /// <summary>Zero saves omits the segment — a brand-new private list never reads "0 saves".</summary>
    [Fact]
    public void PlaylistMeta_NoSaves_IsCountDuration()
    {
        string meta = Text.PlaylistMeta(50, Ms1h14, durationsKnown: true, saves: 0)!;
        Assert.Equal(Strings.Detail.MetaLine(Strings.Detail.SongCount(50), Track.Format.TotalTime(Ms1h14)), meta);
        Assert.DoesNotContain(Strings.Detail.SaveCount(0), meta);
    }

    [Fact]
    public void PlaylistMeta_DurationsUnknown_DropsTheDurationSegment()
    {
        Assert.Equal(Strings.Detail.SongCount(50), Text.PlaylistMeta(50, Ms1h14, durationsKnown: false, saves: 0));
        Assert.Equal(Strings.Detail.SongCount(50) + " · " + Text.SaveCountText(812),
            Text.PlaylistMeta(50, Ms1h14, durationsKnown: false, saves: 812));
    }

    /// <summary>A MIXED membership names both kinds; the songs half is the total minus the episodes.</summary>
    [Fact]
    public void PlaylistMeta_Mixed_StatesSongsAndEpisodes()
        => Assert.Equal(
            Strings.Detail.MetaLine(Strings.Detail.SongCount(48) + " · " + Strings.Podcast.EpisodeCount(3),
                                    Track.Format.TotalTime(Ms1h14)),
            Text.PlaylistMeta(51, Ms1h14, durationsKnown: true, saves: 0, episodes: 3));

    [Fact]
    public void LikedMeta_WithAndWithoutDurations()
    {
        Assert.Equal(Strings.Detail.MetaLine(Strings.Detail.SongCount(1204), Track.Format.TotalTime(Ms1h14)),
            Text.LikedMeta(1204, Ms1h14, durationsKnown: true));
        Assert.Equal(Strings.Detail.SongCount(1204), Text.LikedMeta(1204, Ms1h14, durationsKnown: false));
    }

    /// <summary>The count is what the show HAS; a missing publisher never leaves a dangling " · ".</summary>
    [Fact]
    public void ShowMeta_IsPublisherDotEpisodeCount()
    {
        Assert.Equal("The Daily · " + Strings.Podcast.EpisodeCount(700), Text.ShowMeta("The Daily", 700));
        Assert.Equal(Strings.Podcast.EpisodeCount(700), Text.ShowMeta(null, 700));
        Assert.Equal(Strings.Podcast.EpisodeCount(700), Text.ShowMeta("", 700));
    }

    /// <summary>Compact above 999 (a wall of digits at caption size is noise), the plural key below it.</summary>
    [Fact]
    public void SaveCountText_IsCompactAboveNineNineNine()
    {
        Assert.Equal(Strings.Detail.SaveCount(999L), Text.SaveCountText(999));
        Assert.Equal(Strings.Detail.SaveCount(1L), Text.SaveCountText(1));
        Assert.Equal(Strings.Detail.SaveCountCompact(Text.CompactCount(18_713_647)), Text.SaveCountText(18_713_647));
        Assert.EndsWith("M", Text.CompactCount(18_713_647));
        Assert.EndsWith("K", Text.CompactCount(1_500));
        Assert.EndsWith("B", Text.CompactCount(2_100_000_000));
    }
}
