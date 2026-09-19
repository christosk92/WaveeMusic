using Wavee;
using Xunit;

namespace Wavee.Tests;

// The customizer's contribution-pick list is in REGISTRATION order (ch 26 W3, parity 73) — which a dictionary's
// enumeration does not promise, so the table keeps its own order.
public class SidebarDataSourceTableOrderTests
{
    sealed class FakeSource(string id) : SidebarDataSourceBase(id)
    {
        public override int Fill(List<SidebarLibraryEntry> into, in SidebarSourceRequest request) => 0;
    }

    [Fact]
    public void RegistrationOrderIsKept()
    {
        var table = new SidebarDataSourceTable();
        foreach (string id in SidebarContributions.FirstParty) table.Add(new FakeSource(id));
        var ids = new List<string>();
        foreach (var s in table.Ordered) ids.Add(s.Id);
        Assert.Equal(SidebarContributions.FirstParty, ids);
    }

    [Fact]
    public void AReRegistrationReplacesInPlace()
    {
        var table = new SidebarDataSourceTable();
        table.Add(new FakeSource("wavee.a"));
        table.Add(new FakeSource("wavee.b"));
        var replacement = new FakeSource("wavee.a");
        table.Add(replacement);
        Assert.Equal(2, table.Ordered.Count);
        Assert.Same(replacement, table.Ordered[0]);
    }

    [Fact]
    public void TryGetFindsADisabledSourceThatResolveRefuses()
    {
        var table = new SidebarDataSourceTable();
        table.Add(new FakeSource("wavee.a"));
        table.SetEnabled("wavee.a", false);
        Assert.True(table.TryGet("wavee.a", out var found));
        Assert.Equal("wavee.a", found!.Id);
        Assert.Null(table.Resolve("wavee.a", out var availability));
        Assert.Equal(SidebarContributionAvailability.Disabled, availability);
        Assert.False(table.TryGet("wavee.missing", out _));
        Assert.False(table.TryGet(null, out _));
    }
}
