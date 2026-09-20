using System;
using System.Collections.Generic;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// ── "When does this album actually drop?" (PreReleaseDerivation) ─────────────────────────────────────────────────────
// Three scattered signals collapse into the ONE instant the rail card counts down to, and the ladder is strictly
// weakening: PreReleaseEnd ▸ earliest FUTURE track AvailableAt ▸ a FUTURE parsed ReleaseDate ▸ null. Every rung is
// consulted only when it is genuinely ahead of `now`, which is what stops a shipped album acquiring a countdown from a
// stale flag on a record nobody has re-read.
public class PreReleaseDerivationTests
{
    static readonly DateTimeOffset Now = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset Soon = Now.AddDays(7);
    static readonly DateTimeOffset Later = Now.AddDays(37);
    static readonly DateTimeOffset Gone = Now.AddDays(-9);

    static Track Row(string id, DateTimeOffset? availableAt) =>
        new(id, "spotify:track:" + id, "T " + id, Array.Empty<ArtistRef>(),
            new AlbumRef("al", "spotify:album:al", "Al"), 200_000, false, null,
            Availability: availableAt is null ? (Availability?)null : Availability.Unavailable, AvailableAt: availableAt);

    static Album Rec(DateTimeOffset? preReleaseEnd = null, string? releaseDate = null,
                     IReadOnlyList<Track>? tracks = null) =>
        new("al", "spotify:album:al", "ARE YOU EVER COMING BACK?", null,
            Array.Empty<ArtistRef>(), 2026, tracks?.Count ?? 0,
            Tracks: tracks, ReleaseDate: releaseDate, PreReleaseEnd: preReleaseEnd);

    // ── the ladder ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FuturePreReleaseEnd_OutranksEveryWeakerSignal()
    {
        var a = Rec(preReleaseEnd: Later,
                    releaseDate: "2026-08-15",
                    tracks: new[] { Row("t1", Soon) });

        Assert.Equal(Later, PreReleaseDerivation.UpcomingAt(a, Now));
    }

    [Fact]
    public void PastPreReleaseEnd_IsIgnored_AndTheNextRungAnswers()
    {
        // The stale-flag case: IsPreRelease/PreReleaseEnd are frozen at fetch time, so a lapsed one must not win.
        var a = Rec(preReleaseEnd: Gone, tracks: new[] { Row("t1", Soon) });

        Assert.Equal(Soon, PreReleaseDerivation.UpcomingAt(a, Now));
    }

    [Fact]
    public void PartlyReleasedAlbum_CountsDownToTheEARLIESTPendingRow()
    {
        // The waterfall shape: no album-level flag at all, some rows already out, the rest scheduled. The next one to
        // land is the moment worth announcing — and the rows already out must not drag the answer backwards.
        var a = Rec(tracks: new[]
        {
            Row("out1", Gone),
            Row("late", Later),
            Row("next", Soon),
            Row("out2", Now.AddDays(-1)),
        });

        Assert.Equal(Soon, PreReleaseDerivation.UpcomingAt(a, Now));
    }

    [Fact]
    public void AllRowsAlreadyOut_FallsThroughToAFutureReleaseDate()
    {
        var a = Rec(releaseDate: "2026-09-04", tracks: new[] { Row("out1", Gone), Row("out2", Now.AddDays(-1)) });

        Assert.Equal(new DateTimeOffset(2026, 9, 4, 0, 0, 0, TimeSpan.Zero), PreReleaseDerivation.UpcomingAt(a, Now));
    }

    [Fact]
    public void AReleaseDateInThePast_IsNotACountdown()
        => Assert.Null(PreReleaseDerivation.UpcomingAt(Rec(releaseDate: "2020-01-01"), Now));

    [Fact]
    public void AnOrdinaryReleasedAlbum_HasNoInstantAtAll()
    {
        // The zero-behaviour-change guarantee for every normal album page: nothing upcoming ⇒ null ⇒ no rail card, no
        // "Releases" tile label flip, no pre-save heart.
        var a = Rec(releaseDate: "2019-06-14", tracks: new[] { Row("t1", null), Row("t2", Gone) });

        Assert.Null(PreReleaseDerivation.UpcomingAt(a, Now));
    }

    [Fact]
    public void NoSignalsAtAll_IsNull()
        => Assert.Null(PreReleaseDerivation.UpcomingAt(Rec(), Now));

    [Fact]
    public void ANullTracklist_DoesNotThrow()
        => Assert.Null(PreReleaseDerivation.UpcomingAt(Rec(tracks: null), Now));

    [Fact]
    public void TheInstantIsRelativeToTheSUPPLIEDNow_NotTheWallClock()
    {
        // UpcomingAt takes `now` so the mapper can be tested (and, in production, so one render's clock reading drives
        // every derived value consistently).
        var a = Rec(preReleaseEnd: Later);

        Assert.Equal(Later, PreReleaseDerivation.UpcomingAt(a, Now));
        Assert.Null(PreReleaseDerivation.UpcomingAt(a, Later.AddSeconds(1)));
    }

    // ── ReleaseInstant ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FullIsoDate_ParsesAsMidnightUtc()
        => Assert.Equal(new DateTimeOffset(2026, 9, 4, 0, 0, 0, TimeSpan.Zero),
                        PreReleaseDerivation.ReleaseInstant("2026-09-04"));

    [Fact]
    public void FullIsoTimestamp_KeepsItsTime()
        => Assert.Equal(new DateTimeOffset(2026, 9, 4, 7, 0, 0, TimeSpan.Zero),
                        PreReleaseDerivation.ReleaseInstant("2026-09-04T07:00:00Z"));

    [Fact]
    public void MonthPrecision_ParsesAsTheFirst()
        => Assert.Equal(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
                        PreReleaseDerivation.ReleaseInstant("2026-09"));

    [Fact]
    public void YearPrecision_DoesNotParse()
    {
        // PINNED BEHAVIOUR, and a doc-comment drift worth knowing about: PreReleaseDerivation's summary says a
        // precision-reduced "2026" still parses, but DateTimeOffset.TryParse rejects a bare four-digit year under the
        // invariant culture. It costs nothing today — SpotifyExportMapper normalises YEAR precision to "yyyy-01-01"
        // before it ever reaches here (IsoDate), so no live value has this shape — but a hand-written or legacy
        // ReleaseDate of "2026" yields no countdown rather than a January one.
        Assert.Null(PreReleaseDerivation.ReleaseInstant("2026"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-date")]
    [InlineData("TBA")]
    public void AbsentOrUnparseable_IsNull(string? iso)
        => Assert.Null(PreReleaseDerivation.ReleaseInstant(iso));

    [Fact]
    public void ADateWithNoZone_IsReadAsUtc_NotLocal()
    {
        // AssumeUniversal: this is a wire value. Without it the same document would resolve to a different instant on
        // every machine, and a countdown would be off by the tester's offset.
        Assert.Equal(TimeSpan.Zero, PreReleaseDerivation.ReleaseInstant("2026-09-04")!.Value.Offset);
    }
}

// ── The three IsUpcoming polarities ──────────────────────────────────────────────────────────────────────────────────
// Two of these records treat a null date as "announced, date unknown" (upcoming); the pin treats it as "ordinary promo"
// (not upcoming). The difference is deliberate and load-bearing — almost every pin is a released album carrying no
// preReleaseEndDateTime, so the pin's polarity is the only thing keeping a countdown off every artist page.
public class PreReleaseUpcomingPolarityTests
{
    static readonly DateTimeOffset Future = DateTimeOffset.UtcNow.AddDays(30);
    static readonly DateTimeOffset Past = DateTimeOffset.UtcNow.AddDays(-30);

    static PinnedItem Pin(DateTimeOffset? at) =>
        new("Pinned", "T", "Single", "", null, "spotify:album:p", ReleaseAt: at);

    static ArtistPreRelease Announce(DateTimeOffset? at) =>
        new("spotify:album:a", "N", null, at);

    static PreReleaseLink Link(DateTimeOffset? at) =>
        new("spotify:prerelease:p", "spotify:album:a", at);

    [Fact]
    public void NullDate_TheTwoAnnouncementRecordsAreUpcoming_ThePinIsNot()
    {
        Assert.True(Announce(null).IsUpcoming);
        Assert.True(Link(null).IsUpcoming);
        Assert.False(Pin(null).IsUpcoming);      // the deliberate odd one out
    }

    [Fact]
    public void FutureDate_AllThreeAgree()
    {
        Assert.True(Announce(Future).IsUpcoming);
        Assert.True(Link(Future).IsUpcoming);
        Assert.True(Pin(Future).IsUpcoming);
    }

    [Fact]
    public void PastDate_AllThreeHaveLapsed()
    {
        // The 30-day offline TTL on the kind-138 payload means a cached link outlives its own release; every announce
        // surface gates on this wall-clock test rather than on the record merely existing.
        Assert.False(Announce(Past).IsUpcoming);
        Assert.False(Link(Past).IsUpcoming);
        Assert.False(Pin(Past).IsUpcoming);
    }

    [Fact]
    public void TargetUri_PrefersTheItem_ThenThePinsOwnUri()
    {
        Assert.Equal("spotify:album:p", Pin(null).TargetUri);
        Assert.Equal("spotify:album:item",
            (Pin(null) with { ItemUri = "spotify:album:item" }).TargetUri);
        // An EMPTY ItemUri is not a target — the wire's absent-field shape is "" as often as it is null.
        Assert.Equal("spotify:album:p", (Pin(null) with { ItemUri = "" }).TargetUri);
    }
}

// The two schemes one release answers to. Their ids DIFFER, so nothing may synthesise one from the other; these helpers
// exist so every caller asks "which scheme is this?" the same way instead of open-coding a StartsWith.
public class PreReleaseUriTests
{
    [Theory]
    [InlineData("spotify:prerelease:0iqKCCqFwlqzSnJgV22Nmh", true, false)]
    [InlineData("spotify:album:0qi1ztU4S08zA1FsP1DUaY", false, true)]
    [InlineData("spotify:track:x", false, false)]
    [InlineData("spotify:prerelease:", true, false)]     // the bare scheme still IS the scheme
    [InlineData("prerelease:spotify:prerelease:x", false, false)]   // a ROUTE key is not a uri
    [InlineData("", false, false)]
    [InlineData(null, false, false)]
    public void SchemeTests(string? uri, bool isPreRelease, bool isAlbum)
    {
        Assert.Equal(isPreRelease, PreReleaseUris.IsPreRelease(uri));
        Assert.Equal(isAlbum, PreReleaseUris.IsAlbum(uri));
    }

    [Fact]
    public void TheTwoSchemesAreNeverInterchangeable()
    {
        // The documented invariant, asserted so a future "just swap the prefix" shortcut fails here first.
        const string pre = "spotify:prerelease:0iqKCCqFwlqzSnJgV22Nmh";
        const string album = "spotify:album:0qi1ztU4S08zA1FsP1DUaY";

        Assert.NotEqual(pre.Substring(PreReleaseUris.PreReleaseScheme.Length),
                        album.Substring(PreReleaseUris.AlbumScheme.Length));
    }
}
