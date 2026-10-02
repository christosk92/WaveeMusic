using FluentGpu.Controls;
using Xunit;

namespace Wavee.Tests;

public class SelectionDragTests
{
    static SelectionModel Model(int count)
        => new() { Mode = ItemsSelectionMode.Extended, ItemCount = count };

    [Fact]
    public void PressedUnselected_CarriesOnlyThePressedRow()
    {
        var sel = Model(10);
        sel.SelectRange(2, 4);
        var into = new List<int> { 99 };
        SelectionDrag.Indices(sel, 7, into);
        Assert.Equal([7], into);
    }

    [Fact]
    public void PressedSelected_CarriesTheWholeSelectionAscending()
    {
        var sel = Model(10);
        sel.SelectRange(2, 4);
        sel.Select(8);
        var into = new List<int>();
        SelectionDrag.Indices(sel, 3, into);
        Assert.Equal([2, 3, 4, 8], into);
    }

    [Fact]
    public void NothingSelected_CarriesThePressedRow()
    {
        var sel = Model(5);
        var into = new List<int>();
        SelectionDrag.Indices(sel, 0, into);
        Assert.Equal([0], into);
    }

    [Fact]
    public void SingleSelectedRow_CarriesThatRow()
    {
        var sel = Model(5);
        sel.Select(2);
        var into = new List<int>();
        SelectionDrag.Indices(sel, 2, into);
        Assert.Equal([2], into);
    }

    [Fact]
    public void TheListIsClearedFirst()
    {
        var sel = Model(5);
        sel.SelectAll();
        var into = new List<int> { 40, 41 };
        SelectionDrag.Indices(sel, 1, into);
        Assert.Equal([0, 1, 2, 3, 4], into);
    }
}
