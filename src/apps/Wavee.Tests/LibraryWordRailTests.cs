// ── Wavee.Tests/LibraryWordRailTests.cs — the word rail's code table (Entities/User.cs §9, LibraryWordRail) ────────────
//
// The sort pill and its flyout are gone: the navigator's sort is a rail of WORDS, and which words a kind offers is one
// pure table (`LibraryWordRail`) rather than a list per surface. Two things make that table load-bearing rather than
// cosmetic. First, the codes are PERSISTED (`library.<kind>.sort`, shared with the sidebar's Library V3 for codes 0–3), so
// they may only be appended to — "albums" had to become 5 and nothing else could move. Second, a code a kind does not
// offer is no longer merely unlabelled, because there is no menu to show the user what is selected: it has to CLAMP at the
// read, without rewriting the other kind's persisted choice.
//
// Nothing here resolves a translation (that is the loc JSON's own business): a WordKey fact asserts the KEY — present,
// non-empty, one per code — which is what keeps the rail from rendering an empty word.
//
// The 2026-09-18 correction added the artists ROW's second line (`User.ArtistCountLine`) at the bottom: it is the same
// release count the rail's "albums" word ranks by, and it now has a songs-only arm for the artists §11 put in the list
// who have no saved album at all. Those facts compare the line to the loc ENTRIES it composes, never to English.
//
// The same day, `nAlbums` / `nSongs` / `nSongsOnly` became ICU PLURALS and the line's two `== 1` arms went away with
// them: mixing a hand-picked `oneAlbum` with a `nSongs` that had no singular branch is what rendered "1 album · 1
// songs". The facts below therefore assert WHICH KEY each count reaches for, at 1 as at 12 — the formatted result is
// the loc entries' business and this harness loads no culture to format it with.

using FluentGpu.Localization;
using Xunit;

namespace Wavee.Tests;

public class LibraryWordRailTests
{
    // ── the persisted codes ─────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, LibraryNavSort.Recents)]
    [InlineData(1, LibraryNavSort.RecentlyAdded)]
    [InlineData(2, LibraryNavSort.Alphabetical)]
    [InlineData(3, LibraryNavSort.Creator)]
    [InlineData(4, LibraryNavSort.ReleaseDate)]
    [InlineData(5, LibraryNavSort.Albums)]
    public void TheSortCodesArePersistedAndNeverMove(int code, LibraryNavSort sort) => Assert.Equal(code, (int)sort);

    [Fact]
    public void TheNewAlbumsWordIsAppendedAsFive()
    {
        // The one fact the rework added to a persisted enum. If this ever reads 3, every user who sorted by "artist"
        // silently gets "albums" (and vice versa) on their next launch.
        Assert.Equal(5, (int)LibraryNavSort.Albums);
        Assert.Equal(5, (int)Enum.GetValues<LibraryNavSort>()[^1]);   // appended, so it is also the LAST value
    }

    // ── WordsFor ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void WordsFor_Albums_IsTheFiveWordRail_InRailOrder()
    {
        // recents · a–z · artist · added · year (W8): the order the rail renders left to right, not the enum's order.
        Assert.Equal(
            new[] { LibraryNavSort.Recents, LibraryNavSort.Alphabetical, LibraryNavSort.Creator, LibraryNavSort.RecentlyAdded, LibraryNavSort.ReleaseDate },
            LibraryWordRail.WordsFor(EntityKind.Album).ToArray());
    }

    [Fact]
    public void WordsFor_Artists_HasAlbums_ButNeitherArtistNorYear()
    {
        // An artist has no release date, and sorting artists by "artist" sorts them by themselves.
        Assert.Equal(
            new[] { LibraryNavSort.Recents, LibraryNavSort.Alphabetical, LibraryNavSort.Albums },
            LibraryWordRail.WordsFor(EntityKind.Artist).ToArray());
    }

    [Fact]
    public void WordsFor_Shows_IsRecentsAlphabeticalAdded()
    {
        Assert.Equal(
            new[] { LibraryNavSort.Recents, LibraryNavSort.Alphabetical, LibraryNavSort.RecentlyAdded },
            LibraryWordRail.WordsFor(EntityKind.Show).ToArray());
    }

    [Fact]
    public void WordsFor_AnyOtherKind_FallsBackToTheAlbumsRail()
    {
        Assert.Equal(LibraryWordRail.WordsFor(EntityKind.Album).ToArray(), LibraryWordRail.WordsFor(EntityKind.Playlist).ToArray());
        Assert.Equal(LibraryWordRail.WordsFor(EntityKind.Album).ToArray(), LibraryWordRail.WordsFor(EntityKind.Unknown).ToArray());
    }

    [Fact]
    public void EveryRail_StartsWithRecents_AndOffersAlphabetical()
    {
        // Recents is the default (code 0, the persisted default) and the letters strip needs a–z to exist on every kind.
        foreach (var kind in new[] { EntityKind.Album, EntityKind.Artist, EntityKind.Show })
        {
            var words = LibraryWordRail.WordsFor(kind);
            Assert.Equal(LibraryNavSort.Recents, words[0]);
            Assert.Contains(LibraryNavSort.Alphabetical, words.ToArray());
            Assert.Equal(words.Length, new HashSet<LibraryNavSort>(words.ToArray()).Count);   // no word twice
        }
    }

    // ── Clamp ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Clamp_KeepsACodeTheRailOffers()
    {
        Assert.Equal(LibraryNavSort.ReleaseDate, LibraryWordRail.Clamp(EntityKind.Album, 4));
        Assert.Equal(LibraryNavSort.Creator, LibraryWordRail.Clamp(EntityKind.Album, 3));
        Assert.Equal(LibraryNavSort.Albums, LibraryWordRail.Clamp(EntityKind.Artist, 5));
        Assert.Equal(LibraryNavSort.RecentlyAdded, LibraryWordRail.Clamp(EntityKind.Show, 1));
    }

    [Fact]
    public void Clamp_ACodeTheRailDoesNotOffer_ReadsAsRecents()
    {
        Assert.Equal(LibraryNavSort.Recents, LibraryWordRail.Clamp(EntityKind.Album, 5));      // "albums" on the albums page
        Assert.Equal(LibraryNavSort.Recents, LibraryWordRail.Clamp(EntityKind.Artist, 3));     // "artist" on the artists page
        Assert.Equal(LibraryNavSort.Recents, LibraryWordRail.Clamp(EntityKind.Artist, 4));     // "year" on the artists page
        Assert.Equal(LibraryNavSort.Recents, LibraryWordRail.Clamp(EntityKind.Artist, 1));     // "added" is not on the rail
        Assert.Equal(LibraryNavSort.Recents, LibraryWordRail.Clamp(EntityKind.Show, 4));
    }

    [Fact]
    public void Clamp_AStaleOrNonsenseCode_ReadsAsRecents()
    {
        // A value an older build wrote, a hand-edited store.json, a byte that outgrew the enum.
        foreach (int code in new[] { -1, 6, 7, 99, int.MinValue, int.MaxValue })
        {
            Assert.Equal(LibraryNavSort.Recents, LibraryWordRail.Clamp(EntityKind.Album, code));
            Assert.Equal(LibraryNavSort.Recents, LibraryWordRail.Clamp(EntityKind.Artist, code));
            Assert.Equal(LibraryNavSort.Recents, LibraryWordRail.Clamp(EntityKind.Show, code));
        }
    }

    [Fact]
    public void Clamp_IsIdempotent_AndAlwaysLandsOnAWordTheRailHas()
    {
        foreach (var kind in new[] { EntityKind.Album, EntityKind.Artist, EntityKind.Show })
        for (int code = -2; code <= 8; code++)
        {
            var once = LibraryWordRail.Clamp(kind, code);
            Assert.Contains(once, LibraryWordRail.WordsFor(kind).ToArray());
            Assert.Equal(once, LibraryWordRail.Clamp(kind, (int)once));
        }
    }

    // ── WordKey ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void WordKey_IsNonEmptyAndDistinctForEveryCode()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (int code = 0; code <= 5; code++)
        {
            string key = LibraryWordRail.WordKey((LibraryNavSort)code);
            Assert.False(string.IsNullOrWhiteSpace(key));
            Assert.True(keys.Add(key), $"sort code {code} reuses the loc key {key}");
        }
    }

    [Fact]
    public void WordKey_IsTheRailsOwnKeySet_NotThePillsLabels()
    {
        // The rail's words are lowercase ("added", "a–z"); the sidebar's Library V3 shares `library.sort.*` for codes 0–3
        // and would have gone lowercase with them, so the rail got its own `library.rail.*` block (plan §5.9).
        for (int code = 0; code <= 5; code++)
            Assert.StartsWith("library.rail.", LibraryWordRail.WordKey((LibraryNavSort)code), StringComparison.Ordinal);
    }

    [Fact]
    public void WordKey_AnUnknownCode_ReadsAsTheRecentsWord()
    {
        Assert.Equal(LibraryWordRail.WordKey(LibraryNavSort.Recents), LibraryWordRail.WordKey((LibraryNavSort)200));
    }

    [Fact]
    public void EveryWordOnEveryRail_HasAKey()
    {
        foreach (var kind in new[] { EntityKind.Album, EntityKind.Artist, EntityKind.Show })
        {
            var words = LibraryWordRail.WordsFor(kind);
            for (int i = 0; i < words.Length; i++) Assert.False(string.IsNullOrWhiteSpace(LibraryWordRail.WordKey(words[i])));
        }
    }

    // ── the artists row's second line (User.ArtistCountLine) ────────────────────────────────────────────────────────
    //
    // The rail's "albums" word ranks artists by the SAME number this line leads with (§11's release count: saved albums
    // plus the liked-only ones), so the two live together. Every assertion compares the line to the loc entries it is
    // built from rather than to English, which is the file's rule about translations.

    [Fact]
    public void ArtistCountLine_LeadsWithTheAlbumsAndAppendsTheSongs()
    {
        Assert.Equal(Strings.Library.NAlbums(1), User.ArtistCountLine(1, 0));
        Assert.Equal(Strings.Library.NAlbums(3), User.ArtistCountLine(3, 0));
        Assert.Equal(Strings.Library.NAlbums(1) + Strings.Library.NSongs(12), User.ArtistCountLine(1, 12));
        Assert.Equal(Strings.Library.NAlbums(3) + Strings.Library.NSongs(34), User.ArtistCountLine(3, 34));
    }

    /// <summary>EVERY count goes through the plural entry, 1 included. `nAlbums` / `nSongs` / `nSongsOnly` are ICU
    /// plurals that pick their own singular, so the line must never reach for a hand-written `oneAlbum` / `oneSong` —
    /// that mixture is exactly what composed "1 album · 1 songs": one half hand-picked, the other left to a template
    /// with no singular branch. A plural rule belongs to the message, and in a language whose "one" category is not the
    /// number 1 (Russian's 21, Welsh's 2) the caller could not spell it anyway.
    /// <para>Structural, not textual, and it has to be: this harness loads NO culture, so `Loc` resolves every key to
    /// its own visible `[key]` form and no ICU template is ever formatted here. That still pins the branch — the line
    /// is compared against the same `Strings.*` calls it is built from, and `Loc.Get(oneAlbum)` and `NAlbums(1)`
    /// resolve to DIFFERENT strings whether or not a table is loaded — but the rendered "1 album · 1 song" is the loc
    /// entries' own fact, asserted by the engine's MessageFormatter tests, not by this file.</para></summary>
    [Fact]
    public void ArtistCountLine_AtOne_UsesThePluralEntry_NotAHandWrittenSingular()
    {
        Assert.Equal(Strings.Library.NAlbums(1) + Strings.Library.NSongs(1), User.ArtistCountLine(1, 1));
        Assert.Equal(Strings.Library.NAlbums(1), User.ArtistCountLine(1, 0));
        Assert.Equal(Strings.Library.NSongsOnly(1), User.ArtistCountLine(0, 1));
    }

    [Fact]
    public void ArtistCountLine_WithNoAlbumsButSomeSongs_IsTheSongsAlone()
    {
        // §11's group 2: an artist whose whole presence in the library is liked TRACKS is a row now, and reading the
        // literal word "Artist" at it was the defect — it has songs, so it says songs. `nSongsOnly` carries no " · ",
        // because there is no albums clause in front of it; `nSongs` (the continuation) does.
        Assert.Equal(Strings.Library.NSongsOnly(1), User.ArtistCountLine(0, 1));
        Assert.Equal(Strings.Library.NSongsOnly(12), User.ArtistCountLine(0, 12));
        Assert.DoesNotContain("·", User.ArtistCountLine(0, 12), StringComparison.Ordinal);
    }

    [Fact]
    public void ArtistCountLine_WithNothingKnown_IsTheBareWord()
    {
        // 0/0 is the ONLY surviving use of the old word: a followed artist nothing of whose library has answered yet.
        string word = Loc.Get(Strings.Search.TypeArtist);
        Assert.Equal(word, User.ArtistCountLine(0, 0));
        Assert.Equal(word, User.ArtistCountLine(-1, -1));      // a clamped/never-filled count is not a number to print
    }
}
