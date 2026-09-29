// ── Wavee.Tests/HomeUi/FacetRouteTests.cs — the deep-link/history arg round trip ───────────────────────────────────

using Wavee.HomeUi;
using Xunit;

namespace Wavee.Tests.HomeUi;

public class FacetRouteTests
{
    static readonly ChipInput Music = new("music-chip", "Music", "music-following-chip", "Following");
    static readonly ChipInput Podcasts = new("podcasts-chip", "Podcasts", "podcasts-following-chip", "Following");
    static readonly ChipInput Audiobooks = new("audiobooks-chip", "Audiobooks", null, null);
    static FacetWord[] Words() => FacetPivot.Words(new[] { Music, Podcasts, Audiobooks }, "All");

    [Fact]
    public void All_arg_is_the_empty_string()
    {
        Assert.Equal("", FacetRoute.ArgOf(""));
    }

    [Fact]
    public void Plain_chip_round_trips()
    {
        string arg = FacetRoute.ArgOf("podcasts-chip");
        Assert.Equal("podcasts-chip", FacetRoute.FacetOf(arg, Words()));
    }

    [Fact]
    public void Sub_chip_round_trips()
    {
        string arg = FacetRoute.ArgOf("music-following-chip");
        Assert.Equal("music-following-chip", FacetRoute.FacetOf(arg, Words()));
    }

    [Fact]
    public void Null_arg_is_All()
    {
        Assert.Equal("", FacetRoute.FacetOf(null, Words()));
    }

    [Fact]
    public void Empty_arg_is_All()
    {
        Assert.Equal("", FacetRoute.FacetOf("", Words()));
    }

    [Fact]
    public void Unknown_arg_falls_back_to_All()
    {
        Assert.Equal("", FacetRoute.FacetOf("some-removed-chip", Words()));
    }
}
