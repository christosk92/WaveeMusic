using FluentGpu.Localization;
using Wavee.Features.Browse;
using Wavee.Features.Concerts;
using Xunit;

namespace Wavee.Tests;

public class ShellMastheadRegistryTests
{
    [Fact]
    public void BrowseHome_Resolves()
    {
        Assert.True(ShellMastheadRegistry.TryResolve(BrowseRoutes.Home, null, null, null, out var title, out _));
        Assert.Equal(Loc.Get(Strings.Browse.Title), title);
    }

    [Fact]
    public void BrowseCategory_Resolves()
    {
        Assert.True(ShellMastheadRegistry.TryResolve(BrowseRoutes.Page("spotify:page:x"), "Music", null, null, out var title, out var trail));
        Assert.Equal("Music", title);
        Assert.Equal(2, trail.Count);
    }

    [Fact]
    public void BrowseCategory_WhitespaceLiveTitleFallsBackToArg()
    {
        Assert.True(ShellMastheadRegistry.TryResolve(
            BrowseRoutes.Page("spotify:page:x"), "Self-Help", " ", null, out var title, out var trail));
        Assert.Equal("Self-Help", title);
        Assert.Equal(2, trail.Count);
        Assert.Equal(Loc.Get(Strings.Browse.HomeTitle), trail[0].Label);
        Assert.Equal("Self-Help", trail[1].Label);
    }

    [Fact]
    public void BrowseSection_Resolves()
    {
        Assert.True(ShellMastheadRegistry.TryResolve(BrowseSectionRoutes.Page("spotify:section:1"), null, "Chill", null, out var title, out _));
        Assert.Equal("Chill", title);
    }

    [Fact]
    public void HomeSection_Resolves()
    {
        Assert.True(ShellMastheadRegistry.TryResolve(HomeSectionRoutes.Page("spotify:section:1"), null, "Made For You", null, out var title, out _));
        Assert.Equal("Made For You", title);
    }

    [Fact]
    public void ConcertsHub_ResolvesWithoutPagePublisher()
    {
        Assert.True(ShellMastheadRegistry.TryResolve(ConcertRoutes.Hub, null, null, null, out var title, out var trail));
        Assert.Equal(Loc.Get(Strings.Concerts.Title), title);
        Assert.Equal(2, trail.Count);
    }

    [Fact]
    public void Unknown_DoesNotResolve()
    {
        Assert.False(ShellMastheadRegistry.TryResolve("album:x", "RAM", null, null, out var title, out var trail));
        Assert.Null(title);
        Assert.Empty(trail);
    }

    [Fact]
    public void Playlist_DoesNotResolve()
    {
        Assert.False(ShellMastheadRegistry.TryResolve("pl:spotify:playlist:x", "Calming Classical", null, null, out var title, out var trail));
        Assert.Null(title);
        Assert.Empty(trail);
    }
}

public class NavOriginStoreTests
{
    [Fact]
    public void LatestArrivalWins_IncludingNullClear()
    {
        var s = new NavOriginStore();
        s.Write("browse:x", "A", new NavOrigin("one", "search", "q"));
        s.Write("browse:x", "A", new NavOrigin("two", "browse", null));
        Assert.Equal("two", s.Peek("browse:x", "A")?.Label);
        s.Write("browse:x", "A", null);
        Assert.Null(s.Peek("browse:x", "A"));
    }

    [Fact]
    public void LruBound()
    {
        var s = new NavOriginStore();
        for (int i = 0; i < NavOriginStore.Capacity + 5; i++)
            s.Write("r" + i, null, new NavOrigin("L" + i, "browse", null));
        Assert.Null(s.Peek("r0", null));
        Assert.Equal("L" + (NavOriginStore.Capacity + 4), s.Peek("r" + (NavOriginStore.Capacity + 4), null)?.Label);
    }

    [Fact]
    public void For_SubscribesVersion_PeekDoesNotNeedWrite()
    {
        var s = new NavOriginStore();
        s.Restore("a", "b", new NavOrigin("keep", "search", "q"));
        Assert.Equal("keep", s.Peek("a", "b")?.Label);
        Assert.Equal("keep", s.For("a", "b")?.Label);
    }
}
