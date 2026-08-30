using Wavee;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// The sidebar row's navbar-customization extras (Move up / Move down / Remove). Pure over
/// <see cref="SidebarNavLayout.Decide"/> — the closures that actually move a pin or dispatch <c>RemoveItem</c> stay
/// engine-bound in the pane.
/// </summary>
public class SidebarNavLayoutTests
{
    [Fact]
    public void AMiddleItem_CanMoveBothWays()
    {
        var layout = SidebarNavLayout.Decide(orderIndex: 1, orderCount: 3, removable: false);
        Assert.True(layout.MoveUp);
        Assert.True(layout.MoveDown);
        Assert.False(layout.Remove);
        Assert.False(layout.IsEmpty);
    }

    [Fact]
    public void TheFirstItem_CanOnlyMoveDown()
    {
        var layout = SidebarNavLayout.Decide(0, 3, removable: false);
        Assert.False(layout.MoveUp);
        Assert.True(layout.MoveDown);
    }

    [Fact]
    public void TheLastItem_CanOnlyMoveUp()
    {
        var layout = SidebarNavLayout.Decide(2, 3, removable: false);
        Assert.True(layout.MoveUp);
        Assert.False(layout.MoveDown);
    }

    [Fact]
    public void ALoneItem_CannotMove()
    {
        var layout = SidebarNavLayout.Decide(0, 1, removable: false);
        Assert.True(layout.IsEmpty);
    }

    [Fact]
    public void AProjectedLeaf_WithNoOrder_OffersNothing()
    {
        var layout = SidebarNavLayout.Decide(-1, 0, removable: false);
        Assert.True(layout.IsEmpty);
    }

    [Fact]
    public void AnAuthoredItem_CanBeRemovedEvenWhenItCannotMove()
    {
        var layout = SidebarNavLayout.Decide(0, 1, removable: true);
        Assert.False(layout.MoveUp);
        Assert.False(layout.MoveDown);
        Assert.True(layout.Remove);
        Assert.False(layout.IsEmpty);
    }
}
