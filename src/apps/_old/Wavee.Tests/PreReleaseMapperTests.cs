using System;
using System.Text.Json;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// ── The hero pin, widened (SpotifyExportMapper.MapPinned) ────────────────────────────────────────────────────────────
// The pin used to carry six display fields and nothing about WHAT it points at, so an announcement pin and an ordinary
// promo pin were indistinguishable. These drive the real mapper through its public entry point (ArtistFromOverview) on
// the exact wire shape the shipped assets/spotify/artist-maroon5.json carries — inlined here rather than read from the
// asset, so the fixture can grow a future date without mutating a file the fake backend also serves.
public class PreReleaseMapperTests
{
    const string PinUri = "spotify:album:4xT3ryrqfutVzV1cJN79Ww";
    const string Thumb = "https://image-cdn-fa.spotifycdn.com/image/ab67616d000075a0e26ed70ca976a8e72ac89dab";
    const string ItemCover = "https://i.scdn.co/image/ab67616d0000b273e26ed70ca976a8e72ac89dab";
    const string Background = "https://image-cdn-fa.spotifycdn.com/image/ab67617000005910c660587dbf11c555dfd63443";

    // Field-for-field the maroon5 `profile.pinnedItem` node: a RELEASED pin (itemV2 present, preReleaseEndDateTime null).
    // `preReleaseEnd` is the one hole — "null" reproduces the shipped asset byte-for-byte.
    static string MaroonPin(string preReleaseEnd = "null", string itemUri = PinUri) => $$"""
    {
      "backgroundImageV2": {
        "data": {
          "__typename": "ImageV2",
          "sources": [ { "url": "https://image-cdn-fa.spotifycdn.com/image/ab67617000005910c660587dbf11c555dfd63443" } ]
        }
      },
      "comment": "\"HEROINE\" OUT NOW ! ",
      "itemV2": {
        "__typename": "AlbumResponseWrapper",
        "data": {
          "__typename": "Album",
          "coverArt": {
            "sources": [
              { "height": 640, "url": "{{ItemCover}}", "width": 640 }
            ]
          },
          "name": "Heroine",
          "preReleaseEndDateTime": {{preReleaseEnd}},
          "type": "SINGLE",
          "uri": "{{itemUri}}"
        }
      },
      "subtitle": "Single • New Release",
      "thumbnailImage": {
        "data": {
          "sources": [ { "url": "{{Thumb}}" } ]
        }
      },
      "title": "Heroine",
      "type": "ALBUM",
      "uri": "{{PinUri}}"
    }
    """;

    // The pin shape every payload captured BEFORE itemV2 was mapped hands back: no wrapper at all.
    static string BarePin() => """
    {
      "comment": "listen now",
      "itemV2": null,
      "subtitle": "Single",
      "thumbnailImage": {
        "data": {
          "sources": [ { "url": "https://i.scdn.co/image/thumb" } ]
        }
      },
      "title": "Heroine",
      "type": "ALBUM",
      "uri": "spotify:album:pinOnly"
    }
    """;

    static string Overview(string pinnedItem = "null", string preReleaseV2 = "null") => $$"""
    {
      "data": {
        "artistUnion": {
          "uri": "spotify:artist:04gDigrS5kc9YWfZHwBETP",
          "profile": {
            "name": "Maroon 5",
            "pinnedItem": {{pinnedItem}}
          },
          "preReleaseV2": {{preReleaseV2}}
        }
      }
    }
    """;

    static Artist Map(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var artist = SpotifyExportMapper.ArtistFromOverview(doc.RootElement);
        Assert.NotNull(artist);
        return artist!;
    }

    // Wire form Spotify uses for these instants (ISO-8601 Z). Invariant + UTC: this is a server value, not user input.
    static string Iso(DateTimeOffset t) => t.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    // ── MapPinned ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ReleasedPin_CarriesTheWrappedItemsIdentity_AndIsNotUpcoming()
    {
        var pin = Map(Overview(MaroonPin())).Pinned;

        Assert.NotNull(pin);
        // The original six, unchanged — a released pin must render byte-identically to before the widening.
        Assert.Equal("Pinned", pin!.Eyebrow);            // the wire has no eyebrow; the literal is deliberate
        Assert.Equal("Heroine", pin.Title);
        Assert.Equal("Single • New Release", pin.Subtitle);   // the subtitle was DROPPED before the widening
        Assert.Equal("\"HEROINE\" OUT NOW ! ", pin.Comment);
        Assert.Equal(PinUri, pin.Uri);
        Assert.Equal(Thumb, pin.Cover?.Url);             // thumbnailImage is preferred over the item's coverArt

        // …plus the item identity and the artist-authored background used by the rich Artist Pick.
        Assert.Equal(PinUri, pin.ItemUri);
        Assert.Equal("SINGLE", pin.ItemType);
        Assert.Equal("Album", pin.ItemTypename);
        Assert.Null(pin.ReleaseAt);
        Assert.Equal(Background, pin.BackgroundImage?.Url);

        Assert.False(pin.IsUpcoming);                    // no date ⇒ an ordinary promo (the pin's polarity)
        Assert.Equal(PinUri, pin.TargetUri);
    }

    [Fact]
    public void FuturePreReleaseEnd_MakesThePinAnAnnouncement()
    {
        var due = DateTimeOffset.UtcNow.AddDays(30);
        var pin = Map(Overview(MaroonPin($"\"{Iso(due)}\""))).Pinned;

        Assert.NotNull(pin);
        Assert.NotNull(pin!.ReleaseAt);
        Assert.Equal(due.ToUnixTimeSeconds(), pin.ReleaseAt!.Value.ToUnixTimeSeconds());
        Assert.True(pin.IsUpcoming);
    }

    [Fact]
    public void PastPreReleaseEnd_RevertsToAnOrdinaryPromo_WithNoRefetch()
    {
        // The release-drop transition: the mapper only carries the instant across, so the wall-clock test on the record
        // flips the card back the next time it renders — nothing has to re-read the artist.
        var pin = Map(Overview(MaroonPin($"\"{Iso(DateTimeOffset.UtcNow.AddDays(-2))}\""))).Pinned;

        Assert.NotNull(pin);
        Assert.NotNull(pin!.ReleaseAt);                  // the fact is still carried…
        Assert.False(pin.IsUpcoming);                    // …it has simply lapsed
    }

    [Fact]
    public void ItemUriDifferentFromThePinUri_IsWhatNavigationFollows()
    {
        // The pin's own uri and the pinned entity's uri are not always the same node, which is the whole reason
        // TargetUri exists instead of callers reading either field.
        const string other = "spotify:album:0qi1ztU4S08zA1FsP1DUaY";
        var pin = Map(Overview(MaroonPin(itemUri: other))).Pinned;

        Assert.NotNull(pin);
        Assert.Equal(PinUri, pin!.Uri);
        Assert.Equal(other, pin.ItemUri);
        Assert.Equal(other, pin.TargetUri);
    }

    [Fact]
    public void ItemV2Null_LeavesTheOptionalFieldsNull_AndBehavesExactlyAsBefore()
    {
        var pin = Map(Overview(BarePin())).Pinned;

        Assert.NotNull(pin);
        Assert.Null(pin!.ItemUri);
        Assert.Null(pin.ItemType);
        Assert.Null(pin.ItemTypename);
        Assert.Null(pin.ReleaseAt);
        Assert.Null(pin.BackgroundImage);
        Assert.False(pin.IsUpcoming);
        Assert.Equal("spotify:album:pinOnly", pin.TargetUri);   // falls back to the pin's own uri
        Assert.Equal("https://i.scdn.co/image/thumb", pin.Cover?.Url);
        Assert.Equal("Single", pin.Subtitle);
    }

    [Fact]
    public void NoThumbnail_FallsBackToTheWrappedItemsCoverArt()
    {
        var noThumb = MaroonPin().Replace("\"thumbnailImage\"", "\"thumbnailImageDisabled\"", StringComparison.Ordinal);
        var pin = Map(Overview(noThumb)).Pinned;

        Assert.NotNull(pin);
        Assert.Equal(ItemCover, pin!.Cover?.Url);
    }

    [Fact]
    public void NoPinnedItem_MapsToNull()
        => Assert.Null(Map(Overview()).Pinned);

    // ── MapPreRelease (artistUnion.preReleaseV2.data) — deliberately UNCHANGED by the widening ────────────────────────

    static string PreReleaseV2(string releaseEnd, string name = "\"ARE YOU EVER COMING BACK?\"") => $$"""
    {
      "data": {
        "uri": "spotify:album:0qi1ztU4S08zA1FsP1DUaY",
        "name": {{name}},
        "type": "ALBUM",
        "preReleaseEndDateTime": {{releaseEnd}},
        "coverArt": {
          "sources": [ { "height": 640, "url": "https://i.scdn.co/image/pre", "width": 640 } ]
        }
      }
    }
    """;

    [Fact]
    public void PreReleaseV2_WithAFutureDate_IsUpcoming()
    {
        var due = DateTimeOffset.UtcNow.AddDays(37);
        var pre = Map(Overview(preReleaseV2: PreReleaseV2($"\"{Iso(due)}\""))).Extras?.PreRelease;

        Assert.NotNull(pre);
        Assert.Equal("spotify:album:0qi1ztU4S08zA1FsP1DUaY", pre!.Uri);
        Assert.Equal("ARE YOU EVER COMING BACK?", pre.Name);
        Assert.Equal("ALBUM", pre.Type);
        Assert.Equal("https://i.scdn.co/image/pre", pre.Cover?.Url);
        Assert.Equal(due.ToUnixTimeSeconds(), pre.ReleaseAt!.Value.ToUnixTimeSeconds());
        Assert.True(pre.IsUpcoming);
    }

    [Fact]
    public void PreReleaseV2_WithoutADate_IsStillUpcoming()
    {
        // The OPPOSITE polarity to PinnedItem: an announcement record only exists because something is upcoming, so an
        // undated one is "announced, date unknown" — it just cannot count down.
        var pre = Map(Overview(preReleaseV2: PreReleaseV2("null"))).Extras?.PreRelease;

        Assert.NotNull(pre);
        Assert.Null(pre!.ReleaseAt);
        Assert.True(pre.IsUpcoming);
    }

    [Fact]
    public void PreReleaseV2_WhoseDateHasPassed_HasLapsed()
    {
        var pre = Map(Overview(preReleaseV2: PreReleaseV2($"\"{Iso(DateTimeOffset.UtcNow.AddDays(-1))}\""))).Extras?.PreRelease;

        Assert.NotNull(pre);
        Assert.False(pre!.IsUpcoming);
    }

    [Fact]
    public void PreReleaseV2_WithoutAName_IsDropped()
    {
        // A node with no name cannot be labelled, so it would render as a dead card.
        var pre = Map(Overview(preReleaseV2: PreReleaseV2("null", name: "null"))).Extras?.PreRelease;
        Assert.Null(pre);
    }

    [Fact]
    public void NoPreReleaseV2_MapsToNull()
        => Assert.Null(Map(Overview()).Extras?.PreRelease);
}
