// ── Wavee.Tests/LibraryRowsTests.cs — the navigator's column readers over a slot the table does not hold ──────────────
//
// The 2026-09-19 crash (IndexOutOfRange in the artists navigator, every a–z tap): the library navigator's slots are
// binds over ONE item source shared by every mount of the list, and for the flush in which the a–z projection flips
// under a still-mounted list, a ROW slot built without letters is handed the letter HEADER item — the flat projection
// mints it as `Slot = -(letter + 1)` — and its art bind read `Artists.Image[-1]`. `LibraryRows.*Of` are the readers that
// overran; these facts pin that a slot the table does not hold (negative, or past `Count`) reads as the BLANK row in
// every one of them, and that a row the table does hold still reads its own columns. Everything runs against the real
// in-memory graph (`Entities.Boot` + `Entities.SeedFake`) — no source text.

using FluentGpu.Foundation;
using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class LibraryRowsTests
{
    const long Now0 = 1_788_000_000;   // Platform.Clock.FixedSeedEpoch, restated (as the other seed tests do)

    static readonly EntityKind[] NavKinds = [EntityKind.Artist, EntityKind.Album, EntityKind.Show, EntityKind.Track];

    static void Seed()
    {
        Entities.Boot(CatalogScope.Fake());
        Entities.SeedFake(Now0);
    }

    /// <summary>The header item's slot exactly as the flat projection encodes it: <c>-(letter + 1)</c>, over the same
    /// <see cref="LibraryLetters.Of"/> the a–z grouping bands by. "The Beatles" files under B (letter 2) → slot -3.</summary>
    static int HeaderSlotOf(string title) => -(LibraryLetters.Of(title) + 1);

    [Fact]
    public void A_letter_header_item_reads_as_the_blank_row_in_every_reader()
    {
        Seed();
        int header = HeaderSlotOf("The Beatles");
        Assert.True(header < Table.None);
        foreach (var kind in NavKinds)
        {
            Assert.Equal(StringId.Empty, LibraryRows.ImageOf(kind, header));
            Assert.Equal("", LibraryRows.TitleOf(kind, header));
            Assert.Equal("", LibraryRows.SubtitleOf(kind, header));
            Assert.True(LibraryRows.IdOf(kind, header).IsEmpty);
        }
        Assert.Equal(0, LibraryRows.YearOf(EntityKind.Album, header));
    }

    [Fact]
    public void Every_letter_band_header_is_a_blank_row_not_an_overrun()
    {
        Seed();
        for (int letter = 0; letter < LibraryLetters.Count; letter++)
        {
            int header = -(letter + 1);
            foreach (var kind in NavKinds)
            {
                Assert.Equal(StringId.Empty, LibraryRows.ImageOf(kind, header));
                Assert.Equal("", LibraryRows.TitleOf(kind, header));
            }
        }
    }

    [Fact]
    public void A_slot_past_the_table_reads_as_the_blank_row()
    {
        Seed();
        foreach (var kind in NavKinds)
        {
            int past = Entities.TableFor(kind)!.Count + 4096;   // past Count AND past any doubled column capacity
            Assert.Equal(StringId.Empty, LibraryRows.ImageOf(kind, past));
            Assert.Equal("", LibraryRows.TitleOf(kind, past));
            Assert.Equal("", LibraryRows.SubtitleOf(kind, past));
            Assert.True(LibraryRows.IdOf(kind, past).IsEmpty);
        }
        Assert.Equal(0, LibraryRows.YearOf(EntityKind.Album, Entities.Current.Albums.Count + 4096));
    }

    [Fact]
    public void A_row_the_table_holds_still_reads_its_own_columns()
    {
        Seed();
        var a = Entities.Artist(EntityUri.Parse("spotify:artist:ar0"));
        Assert.True(a.IsValid);
        Assert.True(a.Knows(ArtistFields.Name));
        Assert.Equal(a.Name, LibraryRows.TitleOf(EntityKind.Artist, a.Slot));
        Assert.Equal(a.ImageId, LibraryRows.ImageOf(EntityKind.Artist, a.Slot));
        Assert.Equal(a.Id, LibraryRows.IdOf(EntityKind.Artist, a.Slot));
    }

    [Fact]
    public void The_none_row_is_still_readable_as_blank()
    {
        Seed();
        foreach (var kind in NavKinds)
        {
            Assert.Equal(StringId.Empty, LibraryRows.ImageOf(kind, Table.None));
            Assert.Equal("", LibraryRows.TitleOf(kind, Table.None));
        }
    }
}
