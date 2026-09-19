// ── Wavee.Tests/ShellMastheadsTests.cs — the masthead publications store ──────────────────────────────────────────
//
// Wave 4 stage B's gate for `Shell.Mastheads` (Shell/Shell.cs §13). The band is mounted once above the keep-alive
// boundary and re-renders on `Version`, so the store's one job is to bump it at NAVIGATION rate:
//
//   A DELEGATE IS BEHAVIOUR, NOT DATA. A page that re-publishes on every render hands a fresh "Show all" closure each
//   time; bumping the version for that re-rendered the band per page render. The click resolves the latest delegate
//   through `Peek` instead.

using Xunit;

namespace Wavee.Tests;

public class ShellMastheadsTests
{
    static Shell.Route Route(string arg) => Shell.Parse("search", arg);

    [Fact]
    public void A_first_publication_bumps_the_version()
    {
        var r = Route("mh-first");
        int before = Shell.Mastheads.Version.Peek();
        Assert.True(Shell.Mastheads.Publish(r, new Shell.MastheadPublication("Jazz")));
        Assert.Equal(before + 1, Shell.Mastheads.Version.Peek());
        Assert.Equal("Jazz", Shell.Mastheads.Peek(r)?.Title);
    }

    [Fact]
    public void A_new_delegate_alone_updates_silently()
    {
        var r = Route("mh-delegate");
        int hits = 0;
        Shell.Mastheads.Publish(r, new Shell.MastheadPublication("Jazz", ToolsVisible: true, ToolsAction: () => hits += 1));
        int before = Shell.Mastheads.Version.Peek();
        Assert.False(Shell.Mastheads.Publish(r, new Shell.MastheadPublication("Jazz", ToolsVisible: true, ToolsAction: () => hits += 10)));
        Assert.Equal(before, Shell.Mastheads.Version.Peek());
        Shell.Mastheads.Peek(r)?.ToolsAction?.Invoke();
        Assert.Equal(10, hits);   // the click reaches the LATEST delegate
    }

    [Fact]
    public void Title_and_tool_state_changes_re_render_the_band()
    {
        var r = Route("mh-data");
        Shell.Mastheads.Publish(r, new Shell.MastheadPublication("Jazz"));
        Assert.True(Shell.Mastheads.Publish(r, new Shell.MastheadPublication("Jazz & Blues")));
        Assert.True(Shell.Mastheads.Publish(r, new Shell.MastheadPublication("Jazz & Blues", ToolsVisible: true)));
        Assert.True(Shell.Mastheads.Publish(r, new Shell.MastheadPublication("Jazz & Blues", ToolsVisible: true, ToolsLoading: true)));
    }

    [Fact]
    public void A_publication_belongs_to_its_route_and_arg()
    {
        Shell.Mastheads.Publish(Route("mh-owner-a"), new Shell.MastheadPublication("A"));
        Assert.Null(Shell.Mastheads.Peek(Route("mh-owner-b")));
        Assert.Equal("A", Shell.Mastheads.For(Route("mh-owner-a"))?.Title);
    }

    [Fact]
    public void The_store_is_a_bounded_lru()
    {
        for (int i = 0; i <= Shell.Mastheads.Capacity; i++)
            Shell.Mastheads.Publish(Route("mh-lru-" + i), new Shell.MastheadPublication("T" + i));
        Assert.Null(Shell.Mastheads.Peek(Route("mh-lru-0")));
        Assert.Equal("T" + Shell.Mastheads.Capacity, Shell.Mastheads.Peek(Route("mh-lru-" + Shell.Mastheads.Capacity))?.Title);
    }
}
