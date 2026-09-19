// ── Wavee.Tests/AlbumReleaseFactsRulesTests.cs — "About this release" as data (ch 05 W11, §8 row 1) ────────────────
//
// Ported from 0.2.9 `Wavee.Tests/AlbumReleaseFactsRulesTests.cs` (17 facts): the composition is FIXED — Songs, Length
// and Released are tiles that refine in place, Label is a note, the notes are courtesy then copyright, and `now` is
// always the injected value. Only the call shape changed (`Album.ReleaseFactsRules.For` over track HANDLES, unix
// seconds) — EXCEPT the loc drift ch 05 §6 names (05-album.md:994-1009): 0.2.9 pinned "2 of 3" built by concatenation
// and a hard-coded English "47 min"; 0.3 carries PARTS and formats once through the loc runtime, so those facts assert
// the parts and compare the text against the same `Strings.*` / `Track.Format` call the view makes — never an English
// literal (the DetailTextTests precedent).

using System.Globalization;
using System.Text;
using FluentGpu.Localization;
using Wavee;
using Xunit;
using Facts = Wavee.Album.ReleaseFacts;
using ReleaseDate = Wavee.Album.ReleaseDate;
using Rules = Wavee.Album.ReleaseFactsRules;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class AlbumReleaseFactsRulesTests
{
    static readonly long Now = new DateTimeOffset(2026, 3, 15, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
    const long Day = 86_400;
    static int s_next;

    static Track Released(int durationMs) => MakeTrack(durationMs, notYetOut: false);
    static Track NotYetOut() => MakeTrack(0, notYetOut: true);

    // IsNotYetOut is "ruled Unavailable AND no AvailableAt that has already passed" — a ruling with no instant is enough.
    static Track MakeTrack(int durationMs, bool notYetOut)
    {
        string uri = "spotify:track:facts" + (s_next++).ToString(CultureInfo.InvariantCulture);
        var s = Staging.Rent();
        ref var row = ref s.Tracks.RowFor(s.Text(uri), Authority.Full, (uint)(TrackFields.Identity | TrackFields.Availability));
        row.Title = s.Text("Title");
        row.DurationMs = durationMs;
        row.Flags = notYetOut ? (uint)TrackFlags.Unavailable : 0;
        TestScope.CommitAndPublish(s);
        return Entities.Track(EntityUri.Parse(uri));
    }

    static Facts For(Track[] tracks, string? iso = null, byte precision = 2, ushort year = 0, int releaseAt = 0,
                     string? label = null, string? courtesy = null, string? copyright = null)
        => Rules.For(tracks, Now, iso, precision, year, releaseAt, label, courtesy, copyright);

    // ── Songs ────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Songs_CountsOnlyReleasedTracks_AsNOfTotal()
    {
        TestScope.Fresh();
        var facts = For([Released(180_000), Released(200_000), NotYetOut()]);
        Assert.Equal(2, facts.SongsOut);
        Assert.Equal(3, facts.SongsTotal);
        // ch 05 §6 (05-album.md:998): 0.2.9 concatenated "2 of 3"; 0.3 spells it through `detail.factOfCount`.
        Assert.Equal(Strings.Detail.FactOfCount(2, 3), Rules.SongsText(facts));
    }

    [Fact]
    public void Songs_PlainCount_WhenEveryTrackIsOut()
    {
        TestScope.Fresh();
        var facts = For([Released(180_000), Released(200_000)]);
        Assert.Equal("2", Rules.SongsText(facts));
    }

    [Fact]
    public void Songs_Null_WhenNoTracks()
    {
        TestScope.Fresh();
        var facts = For([]);
        Assert.Null(Rules.SongsText(facts));
        Assert.False(facts.HasSongs);
    }

    // ── Songs caption (defect C: the Songs tile reads the singular on a 1-track album) ──────────────────────────────
    //
    // `SongsCaption` lives on `Album` in Entities/Album.Page.cs, not on `ReleaseFactsRules` itself — that class
    // (Entities/Album.cs) isn't declared `partial`, so a second file cannot add a member to it.

    [Fact]
    public void SongsCaption_Singular_WhenExactlyOneTrack()
    {
        TestScope.Fresh();
        var facts = For([Released(180_000)]);
        Assert.Equal(1, facts.SongsTotal);
        Assert.Equal(Loc.Get(Strings.Detail.FactSong), Wavee.Album.SongsCaption(facts));
    }

    [Fact]
    public void SongsCaption_Plural_WhenMoreThanOneTrack()
    {
        TestScope.Fresh();
        var facts = For([Released(180_000), Released(200_000)]);
        Assert.Equal(2, facts.SongsTotal);
        Assert.Equal(Loc.Get(Strings.Detail.FactSongs), Wavee.Album.SongsCaption(facts));
    }

    [Fact]
    public void SongsCaption_Plural_WhenNoTracks()
    {
        TestScope.Fresh();
        var facts = For([]);
        Assert.Equal(0, facts.SongsTotal);
        Assert.Equal(Loc.Get(Strings.Detail.FactSongs), Wavee.Album.SongsCaption(facts));
    }

    // ── Length ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Length_NullWhenNoDurations()
    {
        // Two OUT tracks, both duration-less: the count exists, the length must not lie "0 min".
        TestScope.Fresh();
        var facts = For([Released(0), Released(0)]);
        Assert.Null(Rules.LengthText(facts));
        Assert.NotNull(Rules.SongsText(facts));
    }

    [Fact]
    public void Length_SumsOnlyOutTracks()
    {
        // 47 min out + a not-yet-out track whose unknown duration must NOT be counted.
        TestScope.Fresh();
        var facts = For([Released(47 * 60_000), NotYetOut()]);
        Assert.Equal(47 * 60_000L, facts.LengthMs);
        Assert.Equal((0, 47), Rules.LengthParts(facts.LengthMs));
        // ch 05 §6 (05-album.md:1001-1007): 0.2.9 pinned the English literal "47 min" (TotalTimeLiteral); 0.3 spells the
        // tile through the SAME formatter the meta line uses.
        Assert.Equal(Track.Format.TotalTime(47 * 60_000), Rules.LengthText(facts));
    }

    [Theory]
    [InlineData(59_000L, 0, 1)]              // a sub-minute total floors UP to one minute, never "0 min"
    [InlineData(47 * 60_000L, 0, 47)]
    [InlineData(72 * 60_000L, 1, 12)]        // an hour-plus total reads hours + minutes
    public void LengthParts_FloorsSubMinuteUp_AndSplitsHours(long ms, int hours, int minutes)
        => Assert.Equal((hours, minutes), Rules.LengthParts(ms));

    [Fact]
    public void Length_IsSpelledOnce_TheTileAndTheMetaLineAgree()
    {
        // The worst of the loc drifts (ch 05 §6, parity 72): one album stated its duration two ways in any non-English
        // locale. Both arms now read Track.Format.TotalTime.
        TestScope.Fresh();
        var facts = For([Released(40 * 60_000), Released(32 * 60_000)]);
        string meta = Detail.Text.AlbumMeta(2, facts.LengthMs, durationsKnown: true, year: 2013)!;
        // Loc-agnostic: the tile's text IS the one formatter's, and the meta line is the template over that same text.
        Assert.Equal(Track.Format.TotalTime(72 * 60_000), Rules.LengthText(facts));
        Assert.Equal(Strings.Detail.MetaLineYear(2, Rules.LengthText(facts)!, 2013), meta);
    }

    // ── Released ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData((byte)0, "yyyy")]
    [InlineData((byte)1, "MMMM yyyy")]
    [InlineData((byte)2, "MMMM d, yyyy")]
    [InlineData((byte)255, "MMMM d, yyyy")]   // an unstated precision reads as full day (0.2.9's null default)
    public void Released_ByPrecision_YearMonthDay(byte precision, string format)
    {
        TestScope.Fresh();
        var facts = For([], "2014-11-04", precision);
        Assert.Equal(new ReleaseDate(2014, 11, 4, precision), facts.Released);
        // ch 05 §6: 0.2.9 formatted InvariantCulture beside a CurrentCulture ShortDate; 0.3 formats the parts once, in
        // the current culture.
        Assert.Equal(new DateTime(2014, 11, 4).ToString(format, CultureInfo.CurrentCulture), Rules.ReleasedText(facts));
    }

    [Fact]
    public void Released_FallsBackToYear_BeforeRichRung()
    {
        // Open rung: no release date yet, only the year the tracklist carries.
        TestScope.Fresh();
        var facts = For([], year: 2014);
        Assert.Equal(new ReleaseDate(2014, 0, 0, 0), facts.Released);
        Assert.Equal(new DateTime(2014, 1, 1).ToString("yyyy", CultureInfo.CurrentCulture), Rules.ReleasedText(facts));
    }

    [Fact]
    public void Released_PrefersFormattedDate_OverYear()
    {
        TestScope.Fresh();
        var facts = For([], "2025-02-07", 2, 2014);
        Assert.Equal(new ReleaseDate(2025, 2, 7, 2), facts.Released);
    }

    [Fact]
    public void Released_Null_WhenNothingKnown()
    {
        TestScope.Fresh();
        var facts = For([], year: 0);
        Assert.Null(facts.Released);
        Assert.Null(Rules.ReleasedText(facts));
    }

    [Theory]
    [InlineData("TBA")]
    [InlineData("2014")]                      // a bare year is not a date this block can state (the year fallback is)
    [InlineData("")]
    public void Released_AnUnparseableIso_IsNull_TheRawEchoIsDropped(string iso)
        => Assert.Null(Rules.ParseReleaseDate(iso, 2));

    // ── ReleasesInFuture ─────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1, true)]     // a day ahead of the injected clock
    [InlineData(-1, false)]   // a day behind it
    [InlineData(0, false)]    // exactly now is not "in the future"
    public void ReleasesInFuture_UsesInjectedNow(int daysFromNow, bool expected)
    {
        TestScope.Fresh();
        var facts = For([], releaseAt: (int)(Now + daysFromNow * Day));
        Assert.Equal(expected, facts.ReleasesInFuture);
        Assert.Equal(Loc.Get(expected ? Strings.Detail.FactReleases : Strings.Detail.FactReleased), Rules.ReleasedCaption(facts));
    }

    // ── Label ────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Label_IsANote_NotATile()
    {
        // A label with nothing else known must not make HasTiles true — Label never reshapes the tile grid.
        TestScope.Fresh();
        var facts = For([], label: "BLØF");
        Assert.Equal("BLØF", facts.Label);
        Assert.False(facts.HasTiles);
        Assert.False(facts.IsEmpty);   // Label alone is still something to show
    }

    [Fact]
    public void Label_Null_WhenEmpty()
    {
        TestScope.Fresh();
        Assert.Null(For([], label: "").Label);
    }

    // ── Notes ────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Notes_CourtesyBeforeCopyright_SkipsEmpty()
    {
        TestScope.Fresh();
        var facts = For([], courtesy: "Courtesy of Some Label", copyright: "(P) 2025 Some Label\n(C) 2025 Some Label");
        Assert.Equal(new[] { "Courtesy of Some Label", "(P) 2025 Some Label\n(C) 2025 Some Label" }, facts.Notes);
    }

    [Fact]
    public void Notes_SkipsCourtesy_WhenAbsent()
    {
        TestScope.Fresh();
        Assert.Equal(new[] { "(C) 2025" }, For([], copyright: "(C) 2025").Notes);
    }

    [Fact]
    public void Notes_Empty_WhenBothAbsent()
    {
        TestScope.Fresh();
        Assert.Empty(For([]).Notes);
    }

    // ── Empty / HasTiles ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Empty_WhenNothingKnown()
    {
        TestScope.Fresh();
        var facts = For([]);
        Assert.True(facts.IsEmpty);
        Assert.Equal(Facts.Empty, facts);
    }

    [Fact]
    public void HasTiles_FalseUntilOpen()
    {
        // Before the tracklist lands there is no Songs/Length/Released at all.
        TestScope.Fresh();
        Assert.False(For([]).HasTiles);

        // The Open rung: tracks land, Songs/Length populate — the grid may now mount.
        Assert.True(For([Released(180_000)]).HasTiles);
    }

    // ── Of: the album's own columns ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Of_ReadsThePublishingColumnsAndTheTracklist()
    {
        TestScope.Fresh();
        var one = Released(200_000);
        var two = NotYetOut();

        var s = Staging.Rent();
        ref var row = ref s.Albums.RowFor(s.Text("spotify:album:factsof"), Authority.Full,
            (uint)(AlbumFields.Release | AlbumFields.Publishing));
        row.ReleaseDateIso = s.Text("2027-05-17");
        row.DatePrecision = 2;
        row.Label = s.Text("Columbia");
        row.Courtesy = s.Text("℗ 2027 Columbia");
        row.Copyright = s.Text("© 2027 Columbia");
        TestScope.CommitAndPublish(s);

        var album = Entities.Album(EntityUri.Parse("spotify:album:factsof"));
        Entities.Current.Edges.AlbumTracks.ReplaceRun(album.Slot, [one.Slot, two.Slot], default);

        var facts = Rules.Of(album, Now);
        Assert.Equal(1, facts.SongsOut);
        Assert.Equal(2, facts.SongsTotal);
        Assert.Equal(200_000L, facts.LengthMs);
        Assert.Equal(new ReleaseDate(2027, 5, 17, 2), facts.Released);
        Assert.True(facts.ReleasesInFuture);                        // the ISO instant is after the injected now
        Assert.Equal("Columbia", facts.Label);
        Assert.Equal(new[] { "℗ 2027 Columbia", "© 2027 Columbia" }, facts.Notes);
    }

    // ── decode integration (G-231 follow-up): the reordered `tracksV2`-before-`artists` document, through the real
    // facts pipeline — AlbumDecodeTests asserts the EDGES this document lands; this asserts what the "About this
    // release" panel actually PAINTS off them: Songs "1" and a length from the one real track, never "3 Songs" with
    // two billed artists reinterpreted as empty ghost tracks. ──────────────────────────────────────────────────────

    const string OneTrackReorderedJson = """
        { "data": { "albumUnion": {
            "tracksV2": { "totalCount": 1, "items": [ { "track": {
                "uri": "spotify:track:FACTS_ORDER", "name": "Solo", "duration": { "totalMilliseconds": 200000 } } } ] },
            "uri": "spotify:album:FACTS_ORDER", "name": "Facts Order Test", "type": "ALBUM",
            "artists": { "items": [
                { "uri": "spotify:artist:FACTS_A1", "profile": { "name": "Artist One" } },
                { "uri": "spotify:artist:FACTS_A2", "profile": { "name": "Artist Two" } }
            ] }
        } } }
        """;

    [Fact]
    public void Decode_TracksV2BeforeArtists_FactsSeeOneRealSong_NotThreeGhostRows()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        Spotify.Decode.AlbumAnswer(Encoding.UTF8.GetBytes(OneTrackReorderedJson), 0, s);
        TestScope.CommitAndPublish(s);

        var album = Entities.Album(EntityUri.Parse("spotify:album:FACTS_ORDER"));
        var facts = Rules.Of(album, Now);
        Assert.Equal(1, facts.SongsOut);
        Assert.Equal(1, facts.SongsTotal);
        Assert.Equal("1", Rules.SongsText(facts));
        Assert.Equal(Track.Format.TotalTime(200_000), Rules.LengthText(facts));
        Assert.Equal(Loc.Get(Strings.Detail.FactSong), Wavee.Album.SongsCaption(facts));
    }
}
