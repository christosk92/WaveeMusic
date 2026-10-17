// ── Wavee.Tests/VideoContractTests.cs — the video surfaces' published keys (themes plan 6.0, T7c) ──────────────────────────────
//
// The video surfaces' pictures are the structure's since T7c: six keys (`Video.Docked`, `.Pip`, `.PopOut`, `.Fullscreen`, `.JoinPoster`,
// `.PlacementMenu`). What is pure about the contract: the ids (a published id never changes), Classic registering every one under its template
// id, and the one number the core and the theme must agree on (the pop-out's caption band). The mounted facts are
// `Wavee.ShellTests\VideoMountedTests.cs`; the placement and PiP rules are `VideoRulesTests.cs`.

using Wavee.Themes.Classic;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.Tests;

public class VideoContractTests
{
    [Fact]
    public void The_published_ids_are_the_plans()
    {
        Assert.Equal("Video.Docked", ViewKeys.VideoDocked.Id);
        Assert.Equal("Video.Pip", ViewKeys.VideoPip.Id);
        Assert.Equal("Video.PopOut", ViewKeys.VideoPopOut.Id);
        Assert.Equal("Video.Fullscreen", ViewKeys.VideoFullscreen.Id);
        Assert.Equal("Video.JoinPoster", ViewKeys.VideoJoinPoster.Id);
        Assert.Equal("Video.PlacementMenu", ViewKeys.VideoPlacementMenu.Id);
    }

    [Fact]
    public void Classic_registers_the_six_video_views()
    {
        var classic = ClassicTheme.Create();

        Assert.Equal("classic.video.docked", classic.Registry.TemplateIdOf(ViewKeys.VideoDocked.Id));
        Assert.Equal("classic.video.pip", classic.Registry.TemplateIdOf(ViewKeys.VideoPip.Id));
        Assert.Equal("classic.video.pop-out", classic.Registry.TemplateIdOf(ViewKeys.VideoPopOut.Id));
        Assert.Equal("classic.video.fullscreen", classic.Registry.TemplateIdOf(ViewKeys.VideoFullscreen.Id));
        Assert.Equal("classic.video.join-poster", classic.Registry.TemplateIdOf(ViewKeys.VideoJoinPoster.Id));
        Assert.Equal("classic.video.placement-menu", classic.Registry.TemplateIdOf(ViewKeys.VideoPlacementMenu.Id));
        Assert.Empty(classic.Validate());
    }

    [Fact]
    public void The_caption_region_and_the_title_band_share_one_height()
    {
        // The band's own layout height is the region the OS answers HTCAPTION for: the core owns the number, the theme's band reads it.
        Assert.Equal(44f, Video.PopOutBand.HeightDip);
    }
}
