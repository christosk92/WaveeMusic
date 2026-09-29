// ── Wavee.Tests/FeedBaselineLookupDecodeTests.cs — Spotify.Decode.FeedBaselineLookup (D2, F32) ───────────────────────
//
// Synthetic only (CLAUDE.md: never the owner's real data). The shape below is the desktop client's own
// `feedBaselineLookup` answer: `data.lookup[]` of `{__typename, _uri, data:{__typename, previewItems:{items:[...]}}}`,
// each item a `TrackResponseWrapper` around a thin `Track` node. Two of the map's (section, card) pairs name the SAME
// card uri — the "a card sits in more than one baseline section" case the fold's whole reason for resolving sections
// at COMMIT (via the map) rather than at decode exists for — and the third `data.lookup[]` entry is an
// `EpisodeOrChapterResponseWrapper`, which must be skipped outright (episodes carry no previews).

using System.Text;
using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class FeedBaselineLookupDecodeTests
{
    const string Json = """
    {
      "data": {
        "lookup": [
          {
            "__typename": "PlaylistResponseWrapper",
            "_uri": "spotify:playlist:fbl-p1",
            "data": {
              "__typename": "Playlist",
              "previewItems": {
                "items": [
                  {
                    "__typename": "TrackResponseWrapper",
                    "data": {
                      "__typename": "Track",
                      "uri": "spotify:track:fbl-t1",
                      "name": "Track One",
                      "albumOfTrack": {
                        "coverArt": {
                          "sources": [
                            { "url": "https://img/fbl-300.jpg", "width": 300 },
                            { "url": "https://img/fbl-640.jpg", "width": 640 }
                          ]
                        }
                      }
                    }
                  },
                  {
                    "__typename": "TrackResponseWrapper",
                    "data": { "__typename": "Track", "uri": "spotify:track:fbl-t2", "name": "Track Two" }
                  }
                ]
              }
            }
          },
          {
            "__typename": "AlbumResponseWrapper",
            "_uri": "spotify:album:fbl-a1",
            "data": {
              "__typename": "Album",
              "previewItems": {
                "items": [
                  { "__typename": "TrackResponseWrapper", "data": { "__typename": "Track", "uri": "spotify:track:fbl-t3", "name": "Track Three" } }
                ]
              }
            }
          },
          {
            "__typename": "EpisodeOrChapterResponseWrapper",
            "_uri": "spotify:episode:fbl-e1",
            "data": {
              "__typename": "Episode",
              "previewItems": { "items": [ { "__typename": "TrackResponseWrapper", "data": { "__typename": "Track", "uri": "spotify:track:fbl-should-not-land", "name": "Ghost" } } ] }
            }
          }
        ]
      }
    }
    """;

    [Fact]
    public void Previews_land_ranked_and_a_shared_card_closes_every_owning_section()
    {
        TestScope.Fresh();
        HomePreviewLink[] map =
        [
            new HomePreviewLink("spotify:section:fbl-sec1", "spotify:playlist:fbl-p1"),
            new HomePreviewLink("spotify:section:fbl-sec2", "spotify:playlist:fbl-p1"),   // shares fbl-p1 with sec1
            new HomePreviewLink("spotify:section:fbl-sec3", "spotify:album:fbl-a1"),
        ];

        var s = Staging.Rent();
        Spotify.Decode.FeedBaselineLookup(Encoding.UTF8.GetBytes(Json), map, s);
        TestScope.CommitAndPublish(s);

        var sec1 = Entities.Section("spotify:section:fbl-sec1".AsSpan());
        var sec2 = Entities.Section("spotify:section:fbl-sec2".AsSpan());
        var sec3 = Entities.Section("spotify:section:fbl-sec3".AsSpan());

        Assert.Equal(2, sec1.PreviewSlots.Length);
        Assert.Equal(2, sec2.PreviewSlots.Length);                 // the SAME card closed both sections
        Assert.Equal(1, sec3.PreviewSlots.Length);

        Assert.Equal((byte)0, sec1.PreviewRanks[0].Rank);
        Assert.Equal((byte)1, sec1.PreviewRanks[1].Rank);

        var t1 = Entities.Track(EntityUri.Parse("spotify:track:fbl-t1".AsSpan()));
        Assert.Equal("Track One", t1.Title);
        Assert.Contains("fbl-300.jpg", Entities.Strings.Resolve(t1.ImageId));

        var t3 = Entities.Track(EntityUri.Parse("spotify:track:fbl-t3".AsSpan()));
        Assert.Equal("Track Three", t3.Title);
    }

    [Fact]
    public void An_episode_wrapper_is_skipped()
    {
        TestScope.Fresh();
        HomePreviewLink[] map = [new HomePreviewLink("spotify:section:fbl-esec", "spotify:episode:fbl-e1")];

        var s = Staging.Rent();
        Spotify.Decode.FeedBaselineLookup(Encoding.UTF8.GetBytes(Json), map, s);
        TestScope.CommitAndPublish(s);

        var sec = Entities.Section("spotify:section:fbl-esec".AsSpan());
        Assert.Equal(0, sec.PreviewSlots.Length);
    }

    [Fact]
    public void An_unmapped_card_closes_nothing()
    {
        TestScope.Fresh();
        // No map entry names fbl-p1/fbl-a1 at all: the answer decodes tracks but no section is ever resolved for
        // them, so nothing lands anywhere (and nothing throws).
        HomePreviewLink[] map = [new HomePreviewLink("spotify:section:fbl-unrelated", "spotify:playlist:not-in-this-answer")];

        var s = Staging.Rent();
        Spotify.Decode.FeedBaselineLookup(Encoding.UTF8.GetBytes(Json), map, s);
        TestScope.CommitAndPublish(s);

        var sec = Entities.Section("spotify:section:fbl-unrelated".AsSpan());
        Assert.Equal(0, sec.PreviewSlots.Length);
    }
}
