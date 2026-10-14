// ── Wavee.Tests/LyricsViewContractTests.cs — the lyrics surface's published key and style (themes plan 6.0, T7b) ───────────────
//
// The lyrics reading surface is the structure's since T7b: `Lyrics.View` (the layer stack, the overlays, the stage's column) and the
// `Lyrics.Lines` style (the state line, the loading bars, the unsynced block, the synced list and its rows, the podcast peek). What is
// pure about the contract: the ids (a published id never changes), Classic registering both under its template id, every member of
// the style being present, and the props' value equality (a render that decided only a message skips the template). The mounted facts
// are `Wavee.ShellTests\LyricsViewMountedTests.cs`; the model's own rules are `LyricsModelTests.cs`, `LyricsRulesTests.cs`.

using Wavee.Themes.Classic;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.Tests;

public class LyricsViewContractTests
{
    [Fact]
    public void The_published_ids_are_the_plans()
    {
        Assert.Equal("Lyrics.View", ViewKeys.LyricsView.Id);
        Assert.Equal("Lyrics.Lines", StyleKeys.LyricsLines.Id);
    }

    [Fact]
    public void Classic_registers_the_view_and_the_lines_style()
    {
        var classic = ClassicTheme.Create();

        Assert.Equal("classic.lyrics.view", classic.Registry.TemplateIdOf(ViewKeys.LyricsView.Id));
        Assert.True(classic.Registry.TryGetStyle(StyleKeys.LyricsLines, out var style));
        Assert.NotNull(style.Message);
        Assert.NotNull(style.Shimmer);
        Assert.NotNull(style.Skeleton);
        Assert.NotNull(style.Unsynced);
        Assert.NotNull(style.Lines);
        Assert.NotNull(style.Peek);
        Assert.Empty(classic.Validate());
    }

    [Fact]
    public void The_lines_style_is_one_object_the_rail_the_stage_and_the_reader_share()
    {
        var first = ClassicTheme.Create();
        var second = ClassicTheme.Create();

        Assert.True(first.Registry.TryGetStyle(StyleKeys.LyricsLines, out var a));
        Assert.True(second.Registry.TryGetStyle(StyleKeys.LyricsLines, out var b));
        Assert.Same(a, b);
    }

    [Fact]
    public void A_peek_gate_compares_by_value()
    {
        Action play = static () => { };

        Assert.Equal(new LyricsPeekGate("58 min · EN", play), new LyricsPeekGate("58 min · EN", play));
        Assert.NotEqual(new LyricsPeekGate("58 min · EN", play), new LyricsPeekGate("", play));
    }
}
