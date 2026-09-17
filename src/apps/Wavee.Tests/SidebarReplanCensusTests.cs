// ── Wavee.Tests/SidebarReplanCensusTests.cs — the sidebar re-plan census fields ─────────────────────────────────────
//
// Gate for `Shell/Sidebar.Census.cs`: the counters the scroll rollup drains to name WHY the sidebar re-planned during a
// burst (the `RailHost×1 a=305K` every fifth frame of the 2026-09-16 traces). Pure: counters in, one log fragment out.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class SidebarReplanCensusTests
{
    [Fact]
    public void A_quiet_burst_adds_nothing()
    {
        Assert.Equal("", Shell.SidebarReplanCensus.Describe(0, 0, 0, 0, Shell.SidebarReplanCause.None));
    }

    [Fact]
    public void The_fragment_names_every_moved_input_in_bit_order()
    {
        string s = Shell.SidebarReplanCensus.Describe(24, 24, 24, 0,
            Shell.SidebarReplanCause.Input | Shell.SidebarReplanCause.Entries | Shell.SidebarReplanCause.Search);
        Assert.Equal(" sidebarReplans=24 sidebarPublishes=24 railBumps=24 wholesale=0 causes=Entries|Input|Search", s);
    }

    [Fact]
    public void A_publish_without_a_build_still_prints_with_no_cause()
    {
        Assert.Equal(" sidebarReplans=0 sidebarPublishes=1 railBumps=1 wholesale=1 causes=none",
            Shell.SidebarReplanCensus.Describe(0, 1, 1, 1, Shell.SidebarReplanCause.None));
    }

    [Fact]
    public void Drain_returns_the_accumulated_counters_once()
    {
        Shell.SidebarReplanCensus.Reset();
        Shell.SidebarReplanCensus.NoteBuild(Shell.SidebarReplanCause.Pins);
        Shell.SidebarReplanCensus.NoteBuild(Shell.SidebarReplanCause.Input);
        Shell.SidebarReplanCensus.NotePublish(railChanged: true, wholesale: false);
        Shell.SidebarReplanCensus.NotePublish(railChanged: false, wholesale: false);
        Assert.Equal(" sidebarReplans=2 sidebarPublishes=2 railBumps=1 wholesale=0 causes=Pins|Input", Shell.SidebarReplanCensus.Drain());
        Assert.Equal("", Shell.SidebarReplanCensus.Drain());
    }
}
