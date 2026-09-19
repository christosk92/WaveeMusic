using System;
using System.Collections.Generic;
using Google.Protobuf;
using Wavee.Backend;
using Wavee.Backend.Hydration;
using Wavee.Backend.Hydration.Projectors;
using Wavee.Backend.Metadata;
using Wavee.Core;
using Xunit;
using Ca = Wavee.Protocol.ContentAgnostic;
using Xm = Wavee.Protocol.ExtendedMetadata;

// EntityKind: the ONE uri vocabulary (Wavee.Core), not Backend.Metadata's thin transport projection of it.
using EntityKind = Wavee.Core.EntityKind;

namespace Wavee.Tests;

// ── The kind-183 projector (design §2.4), ported from AlbumPublishingTests ────────────────────────────────────────────
// The two hard rules survive the move intact: the projection is ADDITIVE (a getAlbum-filled value is never overwritten,
// in either arrival order) and it NEVER MINTS an album row. The wire's two unix timestamps are decoded and deliberately
// unused — on the probe album they say 2020-11-12 (the Expanded EDITION) while the calendar date says 2014-11-18.
// Gone from here: the per-album coalescer, the two per-session memos and the request framing (the ladder fuses kind 183
// into the album's own catalogue POST now, and the pipeline owns the memo).
public class PublishingProjectorTests
{
    const string AlbumUri = "spotify:album:5wtE5aLX5r7jOosmPhTEE8";
    const string CopyrightC = "© 2020 Motion Picture Artwork and Photography 2020 Warner Bros. Entertainment Inc. and Paramount Pictures Corporation. All rights reserved.";
    const string CopyrightP = "℗ 2014 This compilation WaterTower Music as licensee for Warner Bros. Entertainment Inc.";
    const long EditionSeconds = 1605175200;   // 2020-11-12 — the Expanded Edition instant, NOT the album's date
    const long SentinelSeconds = 413146860;   // 1983-02-03 — the shared sentinel the probe found on unrelated entities

    // ── the facets ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Project_AppliesTheCopyrightBlock_TheCalendarDate_AndDayPrecision()
    {
        var (store, batch, p) = Build(Seed());
        Assert.Equal(TraitOutcome.Applied, p.Project(batch, AlbumUri, Answer(Payload())));

        var album = store.GetAlbum(AlbumUri)!;
        Assert.Equal(CopyrightC + "\n" + CopyrightP, album.Copyright);   // the © line then the ℗ line, one block
        Assert.Equal("2014-11-18", album.ReleaseDate);                   // f1, not the 2020-11-12 edition instant
        Assert.Equal("DAY", album.ReleaseDatePrecision);
    }

    [Fact]
    public void Project_YearOnlyDate_YieldsTheYear_AtYearPrecision()
    {
        var (store, batch, p) = Build(Seed());
        p.Project(batch, AlbumUri, Answer(Payload(month: 0, day: 0, copyright: false)));

        var album = store.GetAlbum(AlbumUri)!;
        Assert.Equal("2014", album.ReleaseDate);
        Assert.Equal("YEAR", album.ReleaseDatePrecision);
        Assert.Null(album.Copyright);   // a payload without ©/℗ lines leaves the field alone, never an empty string
    }

    [Fact]
    public void Project_MonthOnlyDate_YieldsTheMonth_AtMonthPrecision()
    {
        var (store, batch, p) = Build(Seed());
        p.Project(batch, AlbumUri, Answer(Payload(day: 0)));

        var album = store.GetAlbum(AlbumUri)!;
        Assert.Equal("2014-11", album.ReleaseDate);
        Assert.Equal("MONTH", album.ReleaseDatePrecision);
    }

    [Fact]
    public void UnixTimestamps_AreNeverTheReleaseDate()
    {
        // The decoder-level statement of the same rule: only `date` is read, so a payload whose ONLY dates are the two
        // unix arms has no release date at all.
        var (date, precision) = PublishingProjector.ReleaseDate(Payload(year: 0).Date);
        Assert.Null(date);
        Assert.Null(precision);
        // And an impossible calendar (month 13 / Feb 31) degrades to the coarser form rather than minting "2014-13-40".
        var impossibleMonth = PublishingProjector.ReleaseDate(Payload(month: 13, day: 40).Date);
        Assert.Equal(("2014", "YEAR"), impossibleMonth);
        var impossibleDay = PublishingProjector.ReleaseDate(Payload(month: 2, day: 31).Date);
        Assert.Equal(("2014-02", "MONTH"), impossibleDay);
    }

    // ── additive-only ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Project_NeverOverwrites_AGetAlbumFilledCopyrightOrReleaseDate()
    {
        var (store, batch, p) = Build(Seed(copyright: "© 2014 getAlbum", releaseDate: "2014-11-07T00:00:00Z", precision: "DAY"));
        Assert.Equal(TraitOutcome.Unchanged, p.Project(batch, AlbumUri, Answer(Payload())));

        var album = store.GetAlbum(AlbumUri)!;
        Assert.Equal("© 2014 getAlbum", album.Copyright);
        Assert.Equal("2014-11-07T00:00:00Z", album.ReleaseDate);
        Assert.Equal("DAY", album.ReleaseDatePrecision);
        Assert.Equal(0, batch.Writes);   // every facet already filled ⇒ no write, hence no store change signal
    }

    [Fact]
    public void Project_FillsOnlyTheMissingHalf_WhenGetAlbumSuppliedTheOther()
    {
        var (store, batch, p) = Build(Seed(copyright: "© 2014 getAlbum"));
        p.Project(batch, AlbumUri, Answer(Payload()));

        var album = store.GetAlbum(AlbumUri)!;
        Assert.Equal("© 2014 getAlbum", album.Copyright);   // theirs
        Assert.Equal("2014-11-18", album.ReleaseDate);      // ours
        Assert.Equal("DAY", album.ReleaseDatePrecision);
    }

    [Fact]
    public void Project_AGetAlbumDate_KeepsItsOwnPrecision_EvenWhenAbsent()
    {
        // Their isoString and our calendar date need not agree on granularity, so the precision only ever rides the date
        // this projector itself supplied — never stamped onto someone else's.
        var (store, batch, p) = Build(Seed(releaseDate: "2014-11-07T00:00:00Z"));
        p.Project(batch, AlbumUri, Answer(Payload()));

        var album = store.GetAlbum(AlbumUri)!;
        Assert.Equal("2014-11-07T00:00:00Z", album.ReleaseDate);
        Assert.Null(album.ReleaseDatePrecision);
    }

    [Fact]
    public void Project_NeverTouches_LabelHydrationOrTracks()
    {
        var store = new InMemoryStore();
        store.UpsertAlbum(new Album("5wtE5aLX5r7jOosmPhTEE8", AlbumUri, "Interstellar", null, [], 2014, 1,
            [new Track("t1", "spotify:track:t1", "Time", [], new AlbumRef("5wtE5aLX5r7jOosmPhTEE8", AlbumUri, "Interstellar"), 100, false, null)],
            Label: "WaterTower Music", Hydration: AlbumHydrationLevel.Tracks));
        using var batch = new TraitBatch(store, DateTimeOffset.UtcNow, TraitSurface.AlbumOpen);

        new PublishingProjector().Project(batch, AlbumUri, Answer(Payload()));

        var album = store.GetAlbum(AlbumUri)!;
        Assert.Equal("WaterTower Music", album.Label);
        Assert.Equal(AlbumHydrationLevel.Tracks, album.Hydration);
        Assert.Equal("spotify:track:t1", Assert.Single(album.Tracks!).Uri);
        Assert.Equal("2014-11-18", album.ReleaseDate);   // and it still did its own job
    }

    // ── misses, non-albums, non-resident rows ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Project_A404IsNegative_AndWritesNothing()
    {
        var (store, batch, p) = Build(Seed());
        Assert.Equal(TraitOutcome.Negative, p.Project(batch, AlbumUri, Answer(null)));

        var album = store.GetAlbum(AlbumUri)!;
        Assert.Null(album.Copyright);
        Assert.Null(album.ReleaseDate);
        Assert.Equal(0, batch.Writes);
    }

    [Fact]
    public void Project_APayloadWithNeitherFacetIsNegative()
    {
        // The artist-shaped junk (only the unix arms) and a date with no year are a miss, not an answer.
        var (_, batch, p) = Build(Seed());
        Assert.Equal(TraitOutcome.Negative, p.Project(batch, AlbumUri, Answer(Payload(year: 0, copyright: false))));
    }

    [Fact]
    public void Project_ANonResidentAlbum_IsNeverMinted_AndStaysReAskable()
    {
        // NotResident is the one outcome the pipeline never memoizes: a later open, by which time the V4 pass has put
        // the row in the store, must be free to try again.
        var (store, batch, p) = Build();
        Assert.Equal(TraitOutcome.NotResident, p.Project(batch, AlbumUri, Answer(Payload())));
        Assert.Null(store.GetAlbum(AlbumUri));
        Assert.Equal(0, batch.Writes);
    }

    [Fact]
    public void AlreadyHas_NeedsBothFacets()
    {
        var store = new InMemoryStore();
        var p = new PublishingProjector();
        var now = DateTimeOffset.UtcNow;

        Assert.False(p.AlreadyHas(store, AlbumUri, now));                       // not resident
        store.UpsertAlbum(Seed(copyright: "© x"));
        Assert.False(p.AlreadyHas(store, AlbumUri, now));                       // half filled ⇒ still something to gain
        store.UpsertAlbum(Seed(copyright: "© x", releaseDate: "2014"));
        Assert.True(p.AlreadyHas(store, AlbumUri, now));
    }

    [Fact]
    public void AppliesTo_IsAlbumsOnly()
    {
        // The probe's artist payload is junk (an 8-byte date near the probe day, not a career date) and the track
        // payload is date-only with nowhere on Track to put it.
        var p = new PublishingProjector();
        Assert.True(p.AppliesTo(EntityKind.Album));
        Assert.False(p.AppliesTo(EntityKind.Track));
        Assert.False(p.AppliesTo(EntityKind.Artist));
        Assert.False(p.AppliesTo(EntityKind.Episode));
        Assert.False(p.AppliesTo(EntityKind.Playlist));
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────────────

    static (InMemoryStore Store, TraitBatch Batch, PublishingProjector P) Build(Album? resident = null)
    {
        var store = new InMemoryStore();
        if (resident is not null) store.UpsertAlbum(resident);
        return (store, new TraitBatch(store, DateTimeOffset.UtcNow, TraitSurface.AlbumOpen), new PublishingProjector());
    }

    /// <summary>A resident album row — what the V4 tracklist pass leaves behind before any envelope lands.</summary>
    static Album Seed(string? copyright = null, string? releaseDate = null, string? precision = null)
        => new("5wtE5aLX5r7jOosmPhTEE8", AlbumUri, "Interstellar", null,
            [new ArtistRef("0YC192cP3KPCRWx8zr8MfZ", "spotify:artist:0YC192cP3KPCRWx8zr8MfZ", "Hans Zimmer")], 2014, 0,
            Copyright: copyright, ReleaseDate: releaseDate, ReleaseDatePrecision: precision);

    static Ca.PublishingMetadataTrait Payload(int year = 2014, int month = 11, int day = 18, bool copyright = true)
    {
        var msg = new Ca.PublishingMetadataTrait
        {
            // Both unix arms are always present, and always disagree with the calendar date, so every test that asserts
            // a release date is also asserting that f2/f3 were ignored.
            Published = new Ca.PublishingMetadataTrait.Types.Timestamp { Seconds = SentinelSeconds },
            Available = new Ca.PublishingMetadataTrait.Types.Timestamp { Seconds = EditionSeconds },
        };
        if (year > 0) msg.Date = new Ca.PublishingMetadataTrait.Types.Date { Year = year, Month = month, Day = day };
        if (copyright) { msg.Copyright.Add(CopyrightC); msg.Copyright.Add(CopyrightP); }
        return msg;
    }

    /// <summary>null = the 404 an album without publishing metadata gets.</summary>
    static TraitPayloads Answer(Ca.PublishingMetadataTrait? msg)
    {
        ByteString? payload = msg?.ToByteString();
        var map = new Dictionary<(string Uri, Xm.ExtensionKind Kind), CachedExtension>
        {
            [(AlbumUri, Xm.ExtensionKind.PublishingMetadataTrait)] =
                new(AlbumUri, Xm.ExtensionKind.PublishingMetadataTrait, null, 0, payload, Missing: payload is null),
        };
        return new TraitPayloads(map, AlbumUri);
    }
}
