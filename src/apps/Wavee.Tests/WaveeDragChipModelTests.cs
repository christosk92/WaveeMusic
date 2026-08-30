using System;
using System.Collections.Generic;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

/// <summary>The drag CHIP's data resolution — the only part of the drag preview Wavee still owns (the card itself is
/// framework-rendered). Engine-free by construction, which is why it can be tested at all.</summary>
public class WaveeDragChipModelTests
{
    static readonly AlbumRef Album = new("al1", "spotify:album:al1", "Album");

    static Track Song(string id, string title, string? artist = "Artist", string? art = null)
        => new(id, "spotify:track:" + id, title,
            artist is null ? Array.Empty<ArtistRef>() : new[] { new ArtistRef("ar" + id, "spotify:artist:ar" + id, artist) },
            Album, 1000, false, art is null ? null : new Image(art));

    [Fact]
    public void Entity_NamesItselfAndCountsOne()
    {
        var chip = WaveeDragChipModel.For("Chill Mix", "https://i/cover", tracks: null);
        Assert.Equal("Chill Mix", chip.Title);
        Assert.Null(chip.Subtitle);
        Assert.Equal("https://i/cover", chip.ArtUrl);
        Assert.Equal(1, chip.Count);
    }

    [Fact]
    public void SingleTrack_ShowsTitleArtistAndItsOwnArt()
    {
        var chip = WaveeDragChipModel.For("Song A", null, new[] { Song("t1", "Song A", "Nils", "https://i/t1") });
        Assert.Equal("Song A", chip.Title);
        Assert.Equal("Nils", chip.Subtitle);
        Assert.Equal("https://i/t1", chip.ArtUrl);
        Assert.Equal(1, chip.Count);           // one track ⇒ no count badge, no stack
    }

    [Fact]
    public void MultiSelect_NamesTheFirstTrackAndCountsThemAll()
    {
        // The payload NAME for a multi-select is the "3 songs" label; the chip must not degrade into it — the corner
        // count badge is what communicates the rest of the selection.
        var chip = WaveeDragChipModel.For("3 songs", null,
            new[] { Song("t1", "First", "Nils"), Song("t2", "Second"), Song("t3", "Third") });
        Assert.Equal("First", chip.Title);
        Assert.Equal("Nils", chip.Subtitle);
        Assert.Equal(3, chip.Count);
    }

    [Fact]
    public void PayloadArt_WinsOverTheFirstTrackArt()
    {
        var chip = WaveeDragChipModel.For("Album", "https://i/album", new[] { Song("t1", "First", art: "https://i/t1") });
        Assert.Equal("https://i/album", chip.ArtUrl);
    }

    [Fact]
    public void MissingPieces_CollapseToNullRatherThanEmptyStrings()
    {
        var chip = WaveeDragChipModel.For("", "", new[] { Song("t1", "", artist: null) });
        Assert.Null(chip.Title);               // no track title AND no payload name
        Assert.Null(chip.Subtitle);            // no artists ⇒ a one-line chip
        Assert.Null(chip.ArtUrl);              // ⇒ the kind glyph tile
    }

    [Fact]
    public void EmptyTrackList_IsTreatedAsAnEntity()
    {
        var chip = WaveeDragChipModel.For("Chill Mix", null, Array.Empty<Track>());
        Assert.Equal("Chill Mix", chip.Title);
        Assert.Equal(1, chip.Count);
    }

    [Fact]
    public void RootlistMultiSelect_CountsTheSelection_SoTheBadgeAndTheStackAppear()
    {
        // A sidebar multi-select carries no tracks at all — several playlists/folders being re-filed. The count badge
        // and the stacked backdrop are the ONE visual that says "five things", so it rides the same Count a song
        // selection does; the title is the payload's own "{n} items" label.
        var chip = WaveeDragChipModel.For("3 items", "https://i/cover", tracks: null, rootlistCount: 3);
        Assert.Equal("3 items", chip.Title);
        Assert.Null(chip.Subtitle);
        Assert.Equal(3, chip.Count);

        // One rootlist item is one item: no badge, no stack (and the default keeps every non-rootlist caller at 1).
        Assert.Equal(1, WaveeDragChipModel.For("Chill Mix", null, null, rootlistCount: 1).Count);
        Assert.Equal(1, WaveeDragChipModel.For("Chill Mix", null, null, rootlistCount: 0).Count);
        Assert.Equal(1, WaveeDragChipModel.For("Chill Mix", null, null).Count);
    }

    [Fact]
    public void Art_FallsBackFromCoverToFirstMosaicTile()
    {
        Assert.Null(WaveeDragChipModel.ArtOf(null));
        Assert.Equal("https://i/cover", WaveeDragChipModel.ArtOf(new Image("https://i/cover")));
        var mosaic = new Image("", MosaicTiles: new List<string>
            { "https://i/a", "https://i/b", "https://i/c", "https://i/d" });
        Assert.Equal("https://i/a", WaveeDragChipModel.ArtOf(mosaic));
    }
}
