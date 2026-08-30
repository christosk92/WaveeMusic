using System.Text.Json;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// Recent-cards cover + accent mapping from Pathfinder home/recents entity shapes.
public class RecentCardsTests
{
    static JsonElement Root(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void RecentCards_EntityWithVisualIdentity_ReturnsCoverAndAccent()
    {
        var cards = SpotifyExportMapper.RecentCards(Root("""
        {
          "data": {
            "lists": [{
              "items": {
                "items": [{
                  "entity": {
                    "_uri": "spotify:album:41b0hsQwhVkMc3NQcvB0NF",
                    "data": {
                      "entityTypeTrait": { "type": "ENTITY_TYPE_ALBUM" },
                      "identityTrait": {
                        "name": "Easy",
                        "type": "Single",
                        "contributors": { "items": [ { "name": "SHAUN", "uri": "spotify:artist:1" } ] }
                      },
                      "uri": "spotify:album:41b0hsQwhVkMc3NQcvB0NF",
                      "visualIdentityTrait": {
                        "squareCoverImage": {
                          "image": {
                            "data": {
                              "sources": [
                                { "url": "https://image-cdn-ak.spotifycdn.com/image/ab67616d000075a0", "maxWidth": 640, "maxHeight": 640 }
                              ]
                            }
                          },
                          "extractedColorSet": {
                            "higherContrast": {
                              "backgroundTintedBase": { "red": 92, "green": 84, "blue": 84, "alpha": 255 }
                            }
                          }
                        }
                      }
                    }
                  }
                }]
              }
            }]
          }
        }
        """));

        Assert.Single(cards);
        Assert.NotNull(cards[0].Image?.Url);
        Assert.Contains("spotifycdn.com", cards[0].Image!.Url);
    }

    [Fact]
    public void CardFromEntity_OriginalInstancesOnly_PicksScdnUrl()
    {
        var card = SpotifyExportMapper.CardFromEntity(Root("""
        {
          "__typename": "Album", "uri": "spotify:album:A", "name": "Test",
          "visualIdentityTrait": {
            "squareCoverImage": {
              "originalInstances": [
                { "flatFile": { "cdnUrl": "https://i.scdn.co/image/small" }, "size": "IMAGE_SIZE_SMALL" },
                { "flatFile": { "cdnUrl": "https://i.scdn.co/image/large" }, "size": "IMAGE_SIZE_LARGE" }
              ]
            }
          },
          "artists": { "items": [ { "uri": "spotify:artist:X", "profile": { "name": "A" } } ] }
        }
        """));

        Assert.NotNull(card);
        Assert.Equal("https://i.scdn.co/image/large", card!.Image?.Url);
    }

    [Fact]
    public void MapArtist_ReleaseWithVisualIdentityTrait_HasCover()
    {
        var artist = SpotifyExportMapper.MapArtist(Root("""
        {
          "uri": "spotify:artist:x", "profile": { "name": "X" },
          "discography": {
            "albums": {
              "items": [{
                "releases": {
                  "items": [{
                    "uri": "spotify:album:y", "name": "Disc", "type": "ALBUM",
                    "date": { "year": 2024 }, "tracks": { "totalCount": 8 },
                    "visualIdentityTrait": {
                      "squareCoverImage": {
                        "originalInstances": [
                          { "flatFile": { "cdnUrl": "https://i.scdn.co/disc-cover" }, "size": "IMAGE_SIZE_LARGE" }
                        ],
                        "extractedColorSet": {
                          "higherContrast": {
                            "backgroundTintedBase": { "red": 20, "green": 13, "blue": 13, "alpha": 255 }
                          }
                        }
                      }
                    }
                  }]
                }
              }]
            }
          }
        }
        """));

        Assert.NotNull(artist.TopAlbums);
        Assert.Single(artist.TopAlbums);
        var al = artist.TopAlbums[0];
        Assert.Equal("https://i.scdn.co/disc-cover", al.Cover?.Url);
    }

    [Fact]
    public void MapArtist_PrefersWideVisualsHeaderOverLegacyHeader()
    {
        var artist = SpotifyExportMapper.MapArtist(Root("""
        {
          "uri": "spotify:artist:x",
          "profile": { "name": "X" },
          "visuals": {
            "avatarImage": { "sources": [
              { "url": "https://i.scdn.co/avatar", "width": 640, "height": 640 }
            ] },
            "headerImage": { "sources": [
              { "url": "https://i.scdn.co/real-wide-header", "width": 2660, "height": 1140 }
            ] }
          },
          "headerImage": { "data": { "sources": [
            { "url": "https://i.scdn.co/legacy-header", "maxWidth": 1280, "maxHeight": 720 }
          ] } }
        }
        """));

        Assert.Equal("https://i.scdn.co/real-wide-header", artist.HeaderImage?.Url);
        Assert.Equal("https://i.scdn.co/avatar", artist.Image?.Url);
    }

    [Fact]
    public void RecentCards_MapsCoverFromRecentsShape()
    {
        // icedamericano.json recents entity: accent from visualIdentityTrait.squareCoverImage.extractedColorSet
        var cards = SpotifyExportMapper.RecentCards(Root("""
        {
          "data": {
            "lists": [{
              "items": {
                "items": [{
                  "entity": {
                    "data": {
                      "entityTypeTrait": { "type": "ENTITY_TYPE_TRACK" },
                      "identityTrait": {
                        "name": "Cold Brew Chapters",
                        "type": "Song",
                        "contributors": { "items": [ { "name": "roti.", "uri": "spotify:artist:1" } ] }
                      },
                      "uri": "spotify:track:7idegBIikag5rTZP4WZihP",
                      "visualIdentityTrait": {
                        "squareCoverImage": {
                          "image": {
                            "data": {
                              "sources": [ { "url": "https://image-cdn.example/cover", "maxWidth": 300, "maxHeight": 300 } ]
                            }
                          },
                          "extractedColorSet": {
                            "higherContrast": {
                              "backgroundTintedBase": { "red": 92, "green": 84, "blue": 84, "alpha": 255 }
                            }
                          }
                        }
                      }
                    }
                  }
                }]
              }
            }]
          }
        }
        """));

        Assert.Single(cards);
    }
}
