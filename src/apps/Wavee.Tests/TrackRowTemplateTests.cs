using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// The bound track-row template's engine-free surface (Operation ultra-fast, P5 slice 2 — see
// docs/plans/wavee/operation-ultra-fast-app-progress.md). TrackRowTemplate.Build itself is NOT exercised here: it
// returns a FluentGpu Element tree (GridEl/BoxEl/ImageEl/Marquee/WaveeEqualizer), and Wavee.Tests deliberately does
// not reference Wavee.dll — this project already source-includes ~200 engine-free files out of the app rather than
// referencing the app assembly (see the many `<Compile Include="..\Wavee\...">` lines in Wavee.Tests.csproj), and a
// wholesale Wavee.dll reference would collide with every one of those (each type would exist in both the
// locally-compiled copy AND the referenced assembly — CS0433). Untangling that is out of scope for this slice; the
// "channel kinds stable per cell key / Visible bound on exactly the presence nodes / no Embed.Comp outside
// ShowWhen" structural assertions the plan calls for are deferred to whichever slice (4, most likely, since it is
// the one that actually cuts BoundRowContent over and needs the same real-tree scrutiny) takes on that
// Wavee.Tests dependency cleanup. What IS engine-free and tested here: RowHandlers (the template's callback
// bundle, Features/Detail/RowHandlers.cs) and TrackRowGlyphs.ChartText, the pure chart-glyph selection the
// number cell's chart lane binds through.
public sealed class TrackRowTemplateTests
{
    static Track Song(string id) => new(id, "spotify:track:" + id, "Title",
        [], new("album", "spotify:album:album", "Album"), 200_000, false, null);

    [Fact]
    public void RowHandlersFieldsReachTheGivenDelegates()
    {
        int played = -1;
        RowPresentation? liked = null, expanded = null, contexted = null;
        string? goUri = null, goName = null;
        bool retried = false;

        var h = new RowHandlers(
            Play: i => played = i,
            ToggleLike: p => liked = p,
            Go: (uri, name) => { goUri = uri; goName = name; },
            ToggleExpanded: p => expanded = p,
            RequestContext: p => contexted = p,
            RetryMetadata: () => retried = true);

        var row = RowPresentation.Skeleton(4) with { Track = Song("a") };

        h.Play(4);
        h.ToggleLike(row);
        h.Go("spotify:artist:x", "X");
        h.ToggleExpanded(row);
        h.RequestContext(row);
        h.RetryMetadata();

        Assert.Equal(4, played);
        Assert.Equal(row, liked);
        Assert.Equal("spotify:artist:x", goUri);
        Assert.Equal("X", goName);
        Assert.Equal(row, expanded);
        Assert.Equal(row, contexted);
        Assert.True(retried);
    }

    [Fact]
    public void RowHandlersIsAPlainValueRecord()
    {
        // RowHandlers is a readonly record struct of delegates — value equality is reference equality per-field
        // (delegates), so the SAME set of delegate instances round-trips through a `with` unchanged. This is the
        // shape the plan calls for ("resolved once from the owning TrackList"): one instance, reused across every
        // realized row's Build() call, never rebuilt per row.
        void Play(int _) { }
        void ToggleLike(RowPresentation _) { }
        void Go(string _, string? __) { }
        void ToggleExpanded(RowPresentation _) { }
        void RequestContext(RowPresentation _) { }
        void RetryMetadata() { }
        var h = new RowHandlers(Play, ToggleLike, Go, ToggleExpanded, RequestContext, RetryMetadata);
        var same = h with { };
        Assert.Equal(h, same);
    }

    [Theory]
    [InlineData(ChartEntryStatus.Up, "▲")]
    [InlineData(ChartEntryStatus.Down, "▼")]
    [InlineData(ChartEntryStatus.New, "NEW")]
    [InlineData(ChartEntryStatus.Equal, "")]
    [InlineData(ChartEntryStatus.Unknown, "")]
    public void ChartTextMapsEveryStatusExactlyLikeTheEagerCellsChartGlyph(ChartEntryStatus status, string expected)
    {
        var entry = new ChartEntry(status, CurrentPos: 3, PreviousPos: 5, Rank: 100);
        Assert.Equal(expected, TrackRowGlyphs.ChartText(entry));
    }

    [Fact]
    public void ChartTextOfNoChartEntryIsEmpty()
        => Assert.Equal("", TrackRowGlyphs.ChartText(null));
}
