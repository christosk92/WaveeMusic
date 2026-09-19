using Wavee;
using Xunit;
namespace Wavee.Tests;
public class ListPushRefreshGuardTests
{
    [Fact] public void Repeated_head_does_not_create_a_read_push_read_loop()
    {
        var gate = new ListPushRefreshGuard();
        Assert.True(gate.Accept("list", "head", 100));
        Assert.False(gate.Accept("list", "head", 110));
        Assert.True(gate.Accept("list", "new-head", 120));
        Assert.True(gate.Accept("list", "new-head", 30_120));
    }
    [Fact] public void No_revision_pushes_are_bounded_and_other_lists_still_refresh()
    {
        var gate = new ListPushRefreshGuard();
        Assert.True(gate.Accept("one", null, 100));
        Assert.False(gate.Accept("one", null, 110));
        Assert.True(gate.Accept("two", null, 110));
    }
}
