using System;
using System.Text.Json;
using Wavee.Backend.Persistence;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// ── Widening PinnedItem must NOT be a cache migration ────────────────────────────────────────────────────────────────
// ArtistOverviewDoc persists the pin through STJ positional-record binding (EntityJson, the source-gen context), so a
// document written by an older build carries only the original SIX values. Every field added past those six is nullable
// WITH a null default for exactly that reason: nullable-with-default is what lets a legacy document bind the missing
// parameters to default(T) instead of failing. These pin that property so the next field added here cannot quietly
// become a migration.
public class ArtistOverviewDocTests
{
    const string PinUri = "spotify:album:4xT3ryrqfutVzV1cJN79Ww";

    // The exact shape a build before the widening wrote: the six original members and nothing else.
    const string LegacyDoc = """
    {
      "TopAlbums": [ { "Uri": "spotify:album:al1", "Kind": 2, "Name": "Latest", "Year": 2026, "CoverUrl": null } ],
      "Pinned": {
        "Eyebrow": "Pinned",
        "Title": "Heroine",
        "Subtitle": "Single",
        "Comment": "\"HEROINE\" OUT NOW ! ",
        "Cover": { "Url": "https://i.scdn.co/image/legacy" },
        "Uri": "spotify:album:4xT3ryrqfutVzV1cJN79Ww"
      },
      "Bio": "a band",
      "AlbumsTotal": 7
    }
    """;

    [Fact]
    public void ALegacyDocument_StillBinds_WithTheOptionalFieldsNull()
    {
        var doc = JsonSerializer.Deserialize<ArtistOverviewDoc>(LegacyDoc, EntityJson.Default.Options);

        Assert.NotNull(doc);
        var pin = doc!.Pinned;
        Assert.NotNull(pin);

        // The six that were always there.
        Assert.Equal("Pinned", pin!.Eyebrow);
        Assert.Equal("Heroine", pin.Title);
        Assert.Equal("Single", pin.Subtitle);
        Assert.Equal("\"HEROINE\" OUT NOW ! ", pin.Comment);
        Assert.Equal("https://i.scdn.co/image/legacy", pin.Cover?.Url);
        Assert.Equal(PinUri, pin.Uri);

        // The optional fields that were not — bound to their defaults, not a bind failure.
        Assert.Null(pin.ItemUri);
        Assert.Null(pin.ItemType);
        Assert.Null(pin.ItemTypename);
        Assert.Null(pin.ReleaseAt);
        Assert.Null(pin.BackgroundImage);

        // …and the derived reads degrade to exactly today's behaviour.
        Assert.False(pin.IsUpcoming);
        Assert.Equal(PinUri, pin.TargetUri);

        // The rest of the document is intact (a partial bind must not have swallowed the siblings).
        Assert.Equal("a band", doc.Bio);
        Assert.Equal(7, doc.AlbumsTotal);
        Assert.Equal("Latest", doc.TopAlbums![0].Name);
    }

    [Fact]
    public void TheOverviewStamp_RidesTheCore_AndALegacyBlobBindsToNever()
    {
        // OverviewFetchedAt lives on the Artist CORE (ArtistSplit.Core nulls the fat facets and nothing else), so the
        // cold tier carries it for free — and an artist blob written before the field existed must bind to default,
        // which reads as "never" and simply re-fetches the overview once. That is the whole migration.
        var stamped = new Artist("ar1", "spotify:artist:ar1", "Ar1", null,
            TopAlbums: [new Album("al1", "spotify:album:al1", "Al1", null, Array.Empty<ArtistRef>(), 2020, 0)],
            FetchedAt: new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
            OverviewFetchedAt: new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.Zero));

        var core = ArtistSplit.Core(stamped);
        Assert.Null(core.TopAlbums);                                  // the split still strips the fat facets…
        Assert.Equal(stamped.OverviewFetchedAt, core.OverviewFetchedAt);   // …and keeps both clocks

        var round = JsonSerializer.Deserialize<Artist>(
            JsonSerializer.Serialize(core, EntityJson.Default.Artist), EntityJson.Default.Artist)!;
        Assert.Equal(stamped.OverviewFetchedAt, round.OverviewFetchedAt);
        Assert.Equal(stamped.FetchedAt, round.FetchedAt);

        var legacy = JsonSerializer.Deserialize<Artist>(
            """{"Id":"ar1","Uri":"spotify:artist:ar1","Name":"Ar1","Image":null,"FetchedAt":"2026-08-01T00:00:00+00:00"}""",
            EntityJson.Default.Artist)!;
        Assert.Equal(default, legacy.OverviewFetchedAt);
        Assert.Equal(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), legacy.FetchedAt);
    }

    [Fact]
    public void ARefattenedArtist_KeepsTheCoresOverviewStamp()
    {
        var core = new Artist("ar1", "spotify:artist:ar1", "Ar1", null,
            OverviewFetchedAt: new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.Zero));
        var doc = new ArtistOverviewDoc(TopAlbums: [new ArtistAlbumStub("spotify:album:al1", 0, "Al1", 2020, null)]);

        var fat = ArtistSplit.Refatten(core, doc, _ => null);

        Assert.Equal(core.OverviewFetchedAt, fat.OverviewFetchedAt);
        Assert.Single(fat.TopAlbums!);
    }

    [Fact]
    public void AWidenedPin_SurvivesTheRoundTrip()
    {
        var due = new DateTimeOffset(2026, 9, 4, 7, 0, 0, TimeSpan.Zero);
        var doc = new ArtistOverviewDoc(Pinned: new PinnedItem(
            "Pinned", "ARE YOU EVER COMING BACK?", "Album", "pre-save now",
            new Image("https://i.scdn.co/image/pre", 640, 640), PinUri,
            ItemUri: "spotify:album:0qi1ztU4S08zA1FsP1DUaY",
            ItemType: "ALBUM",
            ItemTypename: "Album",
            ReleaseAt: due,
            BackgroundImage: new Image("https://i.scdn.co/image/pick-background", 1200, 600)));

        var json = JsonSerializer.Serialize(doc, EntityJson.Default.ArtistOverviewDoc);
        var back = JsonSerializer.Deserialize<ArtistOverviewDoc>(json, EntityJson.Default.Options);

        var pin = back!.Pinned;
        Assert.NotNull(pin);
        Assert.Equal("spotify:album:0qi1ztU4S08zA1FsP1DUaY", pin!.ItemUri);
        Assert.Equal("ALBUM", pin.ItemType);
        Assert.Equal("Album", pin.ItemTypename);
        Assert.Equal(due, pin.ReleaseAt);
        Assert.Equal("https://i.scdn.co/image/pre", pin.Cover?.Url);
        Assert.Equal(640, pin.Cover!.Width!.Value);
        Assert.Equal("https://i.scdn.co/image/pick-background", pin.BackgroundImage?.Url);
        Assert.Equal(1200, pin.BackgroundImage!.Width!.Value);
        Assert.Equal("spotify:album:0qi1ztU4S08zA1FsP1DUaY", pin.TargetUri);
    }

    [Fact]
    public void AReleasedPin_WritesNoneOfTheNewKeys()
    {
        // DefaultIgnoreCondition = WhenWritingNull, so the overwhelmingly common case (an ordinary promo pin) costs the
        // persisted document nothing at all — the widening is free on disk for every artist without an announcement.
        var doc = new ArtistOverviewDoc(Pinned: new PinnedItem("Pinned", "Heroine", "Single", "", null, PinUri));

        var json = JsonSerializer.Serialize(doc, EntityJson.Default.ArtistOverviewDoc);

        Assert.DoesNotContain("ItemUri", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ItemType", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ReleaseAt", json, StringComparison.Ordinal);
        Assert.DoesNotContain("BackgroundImage", json, StringComparison.Ordinal);
        Assert.Contains("\"Uri\"", json, StringComparison.Ordinal);

        // …and it reads back identical.
        var back = JsonSerializer.Deserialize<ArtistOverviewDoc>(json, EntityJson.Default.Options);
        Assert.Equal(doc.Pinned, back!.Pinned);      // records compare by value
    }

    [Fact]
    public void AnUpcomingPin_SurvivesTheProjectRefattenRoundTrip()
    {
        // The real cold-persist path: Project cuts the fat facets off the hot record, Refatten rebuilds them.
        var due = DateTimeOffset.UtcNow.AddDays(30);
        var pin = new PinnedItem("Pinned", "ARE YOU EVER COMING BACK?", "Album", "", null, PinUri,
            ItemUri: "spotify:album:0qi1ztU4S08zA1FsP1DUaY", ItemType: "ALBUM", ItemTypename: "Album", ReleaseAt: due);
        var artist = new Artist("a1", "spotify:artist:a1", "A1", null, Pinned: pin);

        var doc = ArtistSplit.Project(artist);
        Assert.Null(ArtistSplit.Core(artist).Pinned);                 // the core carries no pin…
        Assert.True(ArtistSplit.HasContent(doc));                     // …a pin alone is enough to be worth writing

        var json = JsonSerializer.Serialize(doc, EntityJson.Default.ArtistOverviewDoc);
        var stored = JsonSerializer.Deserialize<ArtistOverviewDoc>(json, EntityJson.Default.Options);
        var refattened = ArtistSplit.Refatten(ArtistSplit.Core(artist), stored!, _ => null);

        Assert.True(refattened.Pinned!.IsUpcoming);
        Assert.Equal("spotify:album:0qi1ztU4S08zA1FsP1DUaY", refattened.Pinned.TargetUri);
        Assert.Equal(due.ToUnixTimeSeconds(), refattened.Pinned.ReleaseAt!.Value.ToUnixTimeSeconds());
    }

    [Fact]
    public void ADocumentMerge_KeepsTheStoredPinOnAThinIncoming()
    {
        // ArtistSplit.Merge mirrors StoreEntityMerge.Artist at the facet level: `Pinned = incoming ?? stored`.
        var stored = new ArtistOverviewDoc(Pinned: new PinnedItem("Pinned", "Heroine", "Single", "", null, PinUri));
        var thin = new ArtistOverviewDoc(Bio: "a band");

        var merged = ArtistSplit.Merge(stored, thin);

        Assert.Equal("Heroine", merged.Pinned!.Title);
        Assert.Equal("a band", merged.Bio);
    }

    [Fact]
    public void ThePreReleaseAnnouncement_RoundTripsInsideExtras()
    {
        // The other half of the persisted announcement: ArtistExtras.PreRelease rides the same document.
        var due = new DateTimeOffset(2026, 9, 4, 7, 0, 0, TimeSpan.Zero);
        var doc = new ArtistOverviewDoc(Extras: new ArtistExtras(
            PreRelease: new ArtistPreRelease("spotify:album:0qi1ztU4S08zA1FsP1DUaY", "ARE YOU EVER COMING BACK?",
                                             new Image("https://i.scdn.co/image/pre"), due, "ALBUM")));

        var json = JsonSerializer.Serialize(doc, EntityJson.Default.ArtistOverviewDoc);
        var back = JsonSerializer.Deserialize<ArtistOverviewDoc>(json, EntityJson.Default.Options);

        var pre = back!.Extras?.PreRelease;
        Assert.NotNull(pre);
        Assert.Equal(due, pre!.ReleaseAt);
        Assert.Equal("ALBUM", pre.Type);
        Assert.Equal("https://i.scdn.co/image/pre", pre.Cover?.Url);
    }

    [Fact]
    public void AnExtrasBundleWrittenBeforePreReleaseExisted_StillBinds()
    {
        var legacy = JsonSerializer.Deserialize<ArtistOverviewDoc>(
            """{"Extras":{"Tour":{"Eyebrow":"ON TOUR","Headline":"h","Subline":"s","IsLive":true}}}""",
            EntityJson.Default.Options);

        Assert.NotNull(legacy!.Extras);
        Assert.Null(legacy.Extras!.PreRelease);
        Assert.Null(legacy.Extras.WatchFeed);
        Assert.Equal("ON TOUR", legacy.Extras.Tour!.Eyebrow);
    }
}
