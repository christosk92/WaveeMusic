using System;
using System.IO;
using Wavee.Backend.Lyrics;
using Wavee.Backend.Lyrics.Sources;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests.Lyrics;

public class NpvLyricsPeekTests
{
    const string TrackId = "4JEylZNW8SbO4zUyfVrpb7";
    const string Title = "Caribbean Queen (No More Love On the Run)";
    const string Artist = "Billy Ocean";

    static string Fixture(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Lyrics", "Fixtures", name));

    static LyricsDocument Spotify() => LyricsClean.Apply(
        SpotifyNativeLyricsSource.Parse(Fixture("spotify-colorlyrics-caribbean-queen.json"), TrackId)!,
        Title, Artist);

    [Fact]
    public void CleanedSpotify_IsLineSyncedWithRealLines()
    {
        var doc = Spotify();
        Assert.True(LyricsPeekClock.ShouldShow(doc));
        Assert.Equal(LyricsSyncKind.Line, doc.Sync);
        Assert.Equal(39, doc.Lines.Count);
        Assert.Equal("She's simply awesome", doc.Lines[0].Text);
        Assert.Equal(12120, doc.Lines[0].StartMs);
        Assert.DoesNotContain(doc.Lines, l => LyricsClean.IsSymbolOnly(l.Text));
    }

    [Fact]
    public void PreRoll_ShowsFirstLineAsPeekOnly()
    {
        var doc = Spotify();
        var (active, peek) = LyricsPeekClock.ActiveAndPeek(doc, 0);
        Assert.Equal(-1, active);
        Assert.Equal(0, peek);
    }

    [Fact]
    public void LeadMs_PromotesTheFirstLineBeforeItsStart()
    {
        var doc = Spotify();
        long start = doc.Lines[0].StartMs;
        var before = LyricsPeekClock.ActiveAndPeek(doc, start - LyricsPeekClock.LeadMs - 1);
        Assert.Equal(-1, before.Active);
        Assert.Equal(0, before.Peek);

        var onLead = LyricsPeekClock.ActiveAndPeek(doc, start - LyricsPeekClock.LeadMs);
        Assert.Equal(0, onLead.Active);
        Assert.Equal(1, onLead.Peek);
    }

    [Fact]
    public void MidSong_ActiveAndNextPeek()
    {
        var doc = Spotify();
        int i = 0;
        while (i < doc.Lines.Count && !doc.Lines[i].Text.Equals("Caribbean Queen", StringComparison.Ordinal)) i++;
        Assert.True(i > 0 && i + 1 < doc.Lines.Count);

        var (active, peek) = LyricsPeekClock.ActiveAndPeek(doc, doc.Lines[i].StartMs);
        Assert.Equal(i, active);
        Assert.Equal(i + 1, peek);
        Assert.Equal("Now we're sharing the same dream", doc.Lines[peek].Text);
    }

    [Fact]
    public void LastLine_HoldsWithNoPeek()
    {
        var doc = Spotify();
        int last = doc.Lines.Count - 1;
        var atStart = LyricsPeekClock.ActiveAndPeek(doc, doc.Lines[last].StartMs);
        Assert.Equal(last, atStart.Active);
        Assert.Equal(-1, atStart.Peek);

        var after = LyricsPeekClock.ActiveAndPeek(doc, doc.Lines[last].StartMs + 60_000);
        Assert.Equal(last, after.Active);
        Assert.Equal(-1, after.Peek);
    }

    [Fact]
    public void UnsyncedOrEmpty_Hides()
    {
        var timed = Spotify();
        var unsynced = timed with { Sync = LyricsSyncKind.Unsynced, IsSynced = false };
        Assert.False(LyricsPeekClock.ShouldShow(unsynced));
        Assert.Equal((-1, -1), LyricsPeekClock.ActiveAndPeek(unsynced, 50_000));

        var empty = timed with { Lines = Array.Empty<LyricLine>() };
        Assert.False(LyricsPeekClock.ShouldShow(empty));
        Assert.Equal((-1, -1), LyricsPeekClock.ActiveAndPeek(empty, 50_000));

        Assert.False(LyricsPeekClock.ShouldShow(null));
    }

    [Fact]
    public void SyllableDocument_StillShowsAsLines()
    {
        var syl = Spotify() with { Sync = LyricsSyncKind.Syllable };
        Assert.True(LyricsPeekClock.ShouldShow(syl));
        var (active, peek) = LyricsPeekClock.ActiveAndPeek(syl, syl.Lines[0].StartMs);
        Assert.Equal(0, active);
        Assert.Equal(1, peek);
    }
}
