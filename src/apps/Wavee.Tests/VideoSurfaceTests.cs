// ── Wavee.Tests/VideoSurfaceTests.cs — the video cards' pure facts (plan wave 2e) ───────────────────────────────────
//
// The artist page's videos shelf and the watch page's shelf are `Controls.Surface(data, Shape.Video, w)` with the 16:9
// cover stated as DATA (`CardData.CoverAspect`). Two things there are decisions rather than looks, and they are pinned
// here against the shared rules the renderer and the estimator both read (`SurfaceGeometry`, `SurfaceRules`): the
// virtual shelf's extent for a 16:9 card (the artist shelf is virtualized, so an estimate that disagrees with the
// rendered height makes the strip re-pin its anchor), and "a play FAB at rest only when there is something to play".
//
// No window, no element is rendered.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class VideoSurfaceTests
{
    const float Aspect = 16f / 9f;

    [Theory]
    [InlineData(208f, 174f)]   // inner 192 → a 108 thumb, + the 66 chrome (4 gutter · 8 pad · 8 gap · 20 title · 18 caption · 8 pad)
    [InlineData(150f, 141f)]   // the shelf's narrowest card: inner 134 → 75.4 rounds to a 75 thumb
    public void Video_shelf_extent_is_the_16_9_thumb_plus_the_one_caption_chrome(float cardW, float extent)
        => Assert.Equal(extent, SurfaceGeometry.StackExtent(Shape.Video, cardW, Aspect));

    [Fact]
    public void Video_card_is_shorter_than_the_square_card_at_the_same_width()
    {
        // The artist shelf's default estimator is the square card's (w + 66): handing it a video would leave every plate
        // 100 DIP taller than its card, which is why the videos shelf states its own.
        Assert.True(SurfaceGeometry.StackExtent(Shape.Video, 208f, Aspect) < SurfaceGeometry.ShelfHeight(208f));
    }

    [Fact]
    public void Video_shape_reserves_one_caption_line_for_the_duration()
    {
        Assert.Equal(1, Shape.Video.CaptionLines);
        Assert.False(Shape.Video.MetaLine);
        Assert.Equal(SurfaceGeometry.ShelfHeight(208f, Aspect, captionLines: 1, metaLine: false),
                     SurfaceGeometry.StackExtent(Shape.Video, 208f, Aspect));
    }

    [Fact]
    public void Watch_shelf_two_line_title_adds_exactly_one_title_line()
    {
        var watch = Shape.Video with { TitleLines = 2 };
        Assert.Equal(SurfaceGeometry.StackExtent(Shape.Video, 220f, Aspect) + SurfaceGeometry.CardTitleLineH,
                     SurfaceGeometry.StackExtent(watch, 220f, Aspect));
    }

    [Theory]
    [InlineData(true, true)]     // a video with a playable: the FAB is mounted at rest
    [InlineData(false, false)]   // a module item that names no playable grows no dead FAB
    public void Video_fab_shows_at_rest_only_when_there_is_something_to_play(bool hasPlay, bool mounted)
        => Assert.Equal(mounted, SurfaceRules.ChromeMounted(hot: false, relates: false, play: Shape.Video.Play, hasPlay: hasPlay));

    [Fact]
    public void Video_card_puts_the_menu_in_the_corner()
        => Assert.True(SurfaceRules.ShowsMenuCorner(Shape.Video, hasMenu: true, showMenu: true));
}
