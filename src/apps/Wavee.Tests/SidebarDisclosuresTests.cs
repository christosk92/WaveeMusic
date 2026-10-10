// ── Wavee.Tests/SidebarDisclosuresTests.cs — the sidebar's concurrent, reversible disclosure bookkeeping ───────────────
//
// Pure: no scope, no engine. Pins Shell/Sidebar.Disclosures.cs (fluent-gpu smooth-reveal plan §12).

using Xunit;

namespace Wavee.Tests;

public sealed class SidebarDisclosuresTests
{
    [Fact]
    public void AFirstToggleBegins()
    {
        var d = new SidebarDisclosures();
        Assert.Equal(SidebarDisclosures.Toggle.Begin, d.Start("section:pinned", "pinned", folder: false, open: false));
        Assert.Equal(1, d.Count);
        Assert.False(d.IsOpen("pinned", folder: false, fallback: true));
    }

    [Fact]
    public void TheSameDirectionAgainIsIgnored()
    {
        var d = new SidebarDisclosures();
        d.Start("folder:f1", "f1", folder: true, open: true);
        Assert.Equal(SidebarDisclosures.Toggle.Ignore, d.Start("folder:f1", "f1", folder: true, open: true));
        Assert.Equal(1, d.Count);
    }

    [Fact]
    public void TheOppositeDirectionReversesInPlace()
    {
        var d = new SidebarDisclosures();
        d.Start("section:recent", "recent", folder: false, open: false);
        Assert.Equal(SidebarDisclosures.Toggle.Reverse, d.Start("section:recent", "recent", folder: false, open: true));
        Assert.Equal(1, d.Count);
        Assert.True(d.IsOpen("recent", folder: false, fallback: false));
    }

    [Fact]
    public void DifferentKeysRunConcurrently()
    {
        var d = new SidebarDisclosures();
        Assert.Equal(SidebarDisclosures.Toggle.Begin, d.Start("section:pinned", "pinned", false, open: false));
        Assert.Equal(SidebarDisclosures.Toggle.Begin, d.Start("section:library", "library", false, open: true));
        Assert.Equal(SidebarDisclosures.Toggle.Begin, d.Start("folder:f1", "f1", true, open: true));
        Assert.Equal(3, d.Count);
        Assert.False(d.IsOpen("pinned", false, fallback: true));
        Assert.True(d.IsOpen("library", false, fallback: false));
    }

    [Fact]
    public void ASectionAndAFolderWithTheSameIdAreDistinct()
    {
        var d = new SidebarDisclosures();
        d.Start("section:x", "x", folder: false, open: true);
        Assert.True(d.IsOpen("x", folder: false, fallback: false));
        Assert.False(d.IsOpen("x", folder: true, fallback: false));   // the folder falls back
    }

    [Fact]
    public void SettledForgetsTheKeyAndALateSettleIsANoOp()
    {
        var d = new SidebarDisclosures();
        d.Start("section:pinned", "pinned", false, open: false);
        Assert.True(d.Settled("section:pinned"));
        Assert.Equal(0, d.Count);
        Assert.False(d.Settled("section:pinned"));
        Assert.True(d.IsOpen("pinned", false, fallback: true));      // back to the persisted state
        Assert.False(d.TryGet("section:pinned", out _));
    }
}
