// ── Wavee.Tests/PreReleaseModelTests.cs — "when does this album actually drop?" (ch 05 W13, §7, §8 row 2) ─────────────
//
// Ported from 0.2.9 `Wavee.Tests/PreReleaseModelTests.cs`. `PreReleaseDerivationTests` keeps its name and every
// assertion — the ladder PreReleaseEnd ▸ earliest FUTURE row AvailableAt ▸ a FUTURE release instant ▸ none, each rung
// wall-clock gated — over `Album.Upcoming.At` (instants are unix seconds, 0 = none; a track's instant is its column).
// `PreReleaseUpcomingPolarityTests` keeps the LINK's polarity (`Upcoming.LinkUpcoming`); its announce-record and pin
// arms are the artist page's records (owner N) and are not ported here. `PreReleaseUriTests` keeps its data rows over
// `EntityUri`. The kind-138 gates the page adds (NeedsLink, SaveTarget, ResolvePreRelease, PreSaveTarget) and the
// countdown's Breakdown are new facts at the bottom.

using System.Globalization;
using Wavee;
using Xunit;
using Upcoming = Wavee.Album.Upcoming;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class PreReleaseDerivationTests
{
    const long Day = 86_400;
    static readonly long Now = new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
    static readonly int Soon = (int)(Now + 7 * Day);
    static readonly int Later = (int)(Now + 37 * Day);
    static readonly int Gone = (int)(Now - 9 * Day);
    static int s_next;

    /// <summary>A row with a release instant (0 = none) — ruled Unavailable when it has one, unruled otherwise.</summary>
    internal static Track Row(int availableAt)
    {
        string uri = "spotify:track:pre" + (s_next++).ToString(CultureInfo.InvariantCulture);
        var s = Staging.Rent();
        ref var row = ref s.Tracks.RowFor(s.Text(uri), Authority.Full,
            (uint)(availableAt == 0 ? TrackFields.Identity : TrackFields.Identity | TrackFields.Availability));
        row.Title = s.Text("T");
        row.DurationMs = 200_000;
        row.AvailableAt = availableAt;
        row.Flags = availableAt == 0 ? 0 : (uint)TrackFlags.Unavailable;
        TestScope.CommitAndPublish(s);
        return Entities.Track(EntityUri.Parse(uri));
    }

    // ── the ladder ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FuturePreReleaseEnd_OutranksEveryWeakerSignal()
    {
        TestScope.Fresh();
        Assert.Equal(Later, Upcoming.At(Later, [Row(Soon)], Upcoming.ReleaseInstant("2026-08-15"), Now));
    }

    [Fact]
    public void PastPreReleaseEnd_IsIgnored_AndTheNextRungAnswers()
    {
        // The stale-flag case: IsPreRelease/PreReleaseEnd are frozen at fetch time, so a lapsed one must not win.
        TestScope.Fresh();
        Assert.Equal(Soon, Upcoming.At(Gone, [Row(Soon)], 0, Now));
    }

    [Fact]
    public void PartlyReleasedAlbum_CountsDownToTheEARLIESTPendingRow()
    {
        // The waterfall shape: no album-level flag at all, some rows already out, the rest scheduled. The rows already
        // out must not drag the answer backwards.
        TestScope.Fresh();
        Assert.Equal(Soon, Upcoming.At(0, [Row(Gone), Row(Later), Row(Soon), Row((int)(Now - Day))], 0, Now));
    }

    [Fact]
    public void AllRowsAlreadyOut_FallsThroughToAFutureReleaseDate()
    {
        TestScope.Fresh();
        int expected = (int)new DateTimeOffset(2026, 9, 4, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        Assert.Equal(expected, Upcoming.At(0, [Row(Gone), Row((int)(Now - Day))], Upcoming.ReleaseInstant("2026-09-04"), Now));
    }

    [Fact]
    public void AReleaseDateInThePast_IsNotACountdown()
        => Assert.Equal(0, Upcoming.At(0, default, Upcoming.ReleaseInstant("2020-01-01"), Now));

    [Fact]
    public void AnOrdinaryReleasedAlbum_HasNoInstantAtAll()
    {
        // The zero-behaviour-change guarantee for every normal album page: nothing upcoming ⇒ no rail card, no
        // "Releases" caption, no pre-save heart.
        TestScope.Fresh();
        Assert.Equal(0, Upcoming.At(0, [Row(0), Row(Gone)], Upcoming.ReleaseInstant("2019-06-14"), Now));
    }

    [Fact]
    public void NoSignalsAtAll_IsNone()
        => Assert.Equal(0, Upcoming.At(0, default, 0, Now));

    [Fact]
    public void AnEmptyTracklist_DoesNotThrow()
        => Assert.Equal(0, Upcoming.At(0, ReadOnlySpan<Track>.Empty, 0, Now));

    [Fact]
    public void TheInstantIsRelativeToTheSUPPLIEDNow_NotTheWallClock()
    {
        Assert.Equal(Later, Upcoming.At(Later, default, 0, Now));
        Assert.Equal(0, Upcoming.At(Later, default, 0, Later + 1L));
    }

    // ── ReleaseInstant ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FullIsoDate_ParsesAsMidnightUtc()
        => Assert.Equal(new DateTimeOffset(2026, 9, 4, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(), Upcoming.ReleaseInstant("2026-09-04"));

    [Fact]
    public void FullIsoTimestamp_KeepsItsTime()
        => Assert.Equal(new DateTimeOffset(2026, 9, 4, 7, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
                        Upcoming.ReleaseInstant("2026-09-04T07:00:00Z"));

    [Fact]
    public void MonthPrecision_ParsesAsTheFirst()
        => Assert.Equal(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(), Upcoming.ReleaseInstant("2026-09"));

    [Fact]
    public void YearPrecision_DoesNotParse()
    {
        // PINNED BEHAVIOUR (0.2.9's own drift note): a bare four-digit year is rejected by the invariant parse, so a
        // year-only date yields no countdown rather than a January one.
        Assert.Equal(0, Upcoming.ReleaseInstant("2026"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-date")]
    [InlineData("TBA")]
    public void AbsentOrUnparseable_IsNone(string? iso)
        => Assert.Equal(0, Upcoming.ReleaseInstant(iso));

    [Fact]
    public void ADateWithNoZone_IsReadAsUtc_NotLocal()
    {
        // AssumeUniversal: a wire value. Without it the same document would resolve to a different instant on every
        // machine, and a countdown would be off by the tester's offset.
        Assert.Equal(new DateTimeOffset(2026, 9, 4, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(), Upcoming.ReleaseInstant("2026-09-04"));
    }

    // ── Of: the album's own columns ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Of_ReadsTheWindowTheRowsAndTheIsoColumn()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        ref var row = ref s.Albums.RowFor(s.Text("spotify:album:preof"), Authority.Full,
            (uint)(AlbumFields.Release | AlbumFields.Availability));
        row.ReleaseDateIso = s.Text("2026-12-24");
        row.DatePrecision = 2;
        row.PreReleaseEnd = Gone;                                     // lapsed: the next rung answers
        TestScope.CommitAndPublish(s);
        var album = Entities.Album(EntityUri.Parse("spotify:album:preof"));
        Entities.Current.Edges.AlbumTracks.ReplaceRun(album.Slot, [Row(Later).Slot, Row(Soon).Slot], default);

        Assert.Equal(Soon, Upcoming.Of(album, Now));
        Assert.True(Upcoming.NeedsLink(album, Now));
    }
}

/// <summary>The kind-138 link's polarity (0.2.9 <c>PreReleaseLink.IsUpcoming</c>): a null date is "announced, date
/// unknown"; a stated one must still be ahead, because the payload is cached up to 30 days.</summary>
public class PreReleaseUpcomingPolarityTests
{
    static readonly long Now = new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

    [Fact]
    public void NullDate_TheLinkIsUpcoming()
        => Assert.True(Upcoming.LinkUpcoming(0, Now));

    [Fact]
    public void FutureDate_Upcoming()
        => Assert.True(Upcoming.LinkUpcoming((int)(Now + 30 * 86_400), Now));

    [Fact]
    public void PastDate_HasLapsed()
        => Assert.False(Upcoming.LinkUpcoming((int)(Now - 30 * 86_400), Now));
}

/// <summary>The two schemes one release answers to. Their ids DIFFER, so nothing may synthesise one from the other.</summary>
public class PreReleaseUriTests
{
    [Theory]
    [InlineData("spotify:prerelease:0iqKCCqFwlqzSnJgV22Nmh", true, false)]
    [InlineData("spotify:album:0qi1ztU4S08zA1FsP1DUaY", false, true)]
    [InlineData("spotify:track:x", false, false)]
    [InlineData("spotify:prerelease:", true, false)]     // the bare scheme still IS the scheme
    [InlineData("prerelease:spotify:prerelease:x", false, false)]   // a ROUTE key is not a uri
    [InlineData("", false, false)]
    public void SchemeTests(string uri, bool isPreRelease, bool isAlbum)
    {
        Assert.Equal(isPreRelease, EntityUri.IsPrerelease(uri.AsSpan()));
        Assert.Equal(isAlbum, !EntityUri.IsPrerelease(uri.AsSpan()) && EntityUri.KindOf(uri.AsSpan()) == EntityKind.Album);
    }

    [Fact]
    public void TheTwoSchemesAreNeverInterchangeable()
    {
        const string pre = "spotify:prerelease:0iqKCCqFwlqzSnJgV22Nmh";
        const string album = "spotify:album:0qi1ztU4S08zA1FsP1DUaY";
        Assert.NotEqual(EntityUri.IdOf(pre.AsSpan()).ToString(), EntityUri.IdOf(album.AsSpan()).ToString());
    }
}

/// <summary>The page's kind-138 gates over the columns: the heart's target, the route's resolve, the pre-save answer.</summary>
[Collection(EntitiesCollection.Name)]
public class PreReleaseGateTests
{
    static readonly long Now = new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

    static Album Linked(string albumUri, string prereleaseUri, int end)
    {
        var s = Staging.Rent();
        ref var row = ref s.Albums.RowFor(s.Text(albumUri), Authority.Full,
            (uint)(AlbumFields.Title | AlbumFields.Availability | AlbumFields.PreReleaseLink));
        row.Title = s.Text("Soon");
        row.PreReleaseUri = s.Text(prereleaseUri);
        row.PreReleaseEnd = end;
        row.Flags = (uint)AlbumFlags.PreRelease;
        TestScope.CommitAndPublish(s);
        return Entities.Album(EntityUri.Parse(albumUri));
    }

    [Fact]
    public void The_heart_targets_the_prerelease_only_while_the_link_is_upcoming()
    {
        TestScope.Fresh();
        var ahead = Linked("spotify:album:ahead", "spotify:prerelease:ahead", (int)(Now + 86_400));
        var shipped = Linked("spotify:album:shipped", "spotify:prerelease:shipped", (int)(Now - 86_400));

        Assert.Equal("spotify:prerelease:ahead", Upcoming.SaveTarget(ahead, Now).Text);
        Assert.Equal("spotify:album:shipped", Upcoming.SaveTarget(shipped, Now).Text);    // a stale link is not a pre-save
    }

    [Fact]
    public void A_prerelease_route_resolves_to_the_album_that_links_it_else_to_its_own_titled_row()
    {
        TestScope.Fresh();
        var album = Linked("spotify:album:linked", "spotify:prerelease:linked", (int)(Now + 86_400));
        Assert.Equal(album, Upcoming.ResolvePreRelease(EntityUri.Parse("spotify:prerelease:linked")));

        Assert.False(Upcoming.ResolvePreRelease(EntityUri.Parse("spotify:prerelease:nobody")).IsValid);

        var s = Staging.Rent();
        ref var own = ref s.Albums.RowFor(s.Text("spotify:prerelease:own"), Authority.Full, (uint)AlbumFields.Title);
        own.Title = s.Text("Own row");
        TestScope.CommitAndPublish(s);
        Assert.Equal("Own row", Upcoming.ResolvePreRelease(EntityUri.Parse("spotify:prerelease:own")).Title);
    }

    [Fact]
    public void The_pre_save_answer_is_the_prerelease_uri_or_nothing()
    {
        TestScope.Fresh();
        Linked("spotify:album:ps", "spotify:prerelease:ps", (int)(Now + 86_400));
        Linked("spotify:album:out", "spotify:prerelease:out", (int)(Now - 86_400));

        Assert.Equal("spotify:prerelease:ps", Upcoming.PreSaveTarget(EntityUri.Parse("spotify:album:ps"), Now).Text);
        Assert.Equal("spotify:prerelease:ps", Upcoming.PreSaveTarget(EntityUri.Parse("spotify:prerelease:ps"), Now).Text);
        Assert.False(Upcoming.PreSaveTarget(EntityUri.Parse("spotify:album:out"), Now).IsValid);
        Assert.False(Upcoming.PreSaveTarget(EntityUri.Parse("spotify:track:x"), Now).IsValid);
    }
}

/// <summary>The countdown's per-unit remainders (0.2.9 <c>PreReleaseCountdown.Breakdown</c>, untested there).</summary>
public class PreReleaseCountdownBreakdownTests
{
    [Theory]
    [InlineData(0L)]
    [InlineData(59L)]
    [InlineData(3_661L)]
    [InlineData(9 * 86_400L + 4 * 3600 + 37 * 60 + 9)]
    [InlineData(123 * 86_400L)]
    public void Breakdown_AgreesWithTheControl(long seconds)
        => Assert.Equal(Controls.Breakdown(TimeSpan.FromSeconds(seconds)), Upcoming.Breakdown(seconds));

    [Fact]
    public void Breakdown_ClampsAPastInstantAtZero()
        => Assert.Equal((0, 0, 0, 0), Upcoming.Breakdown(-5));

    [Fact]
    public void Breakdown_DaysAreNotCapped()
        => Assert.Equal((123, 0, 0, 0), Upcoming.Breakdown(123 * 86_400L));
}
