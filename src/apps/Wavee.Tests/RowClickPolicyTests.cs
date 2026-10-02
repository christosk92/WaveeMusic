using FluentGpu.Controls;
using FluentGpu.Foundation;
using Xunit;

namespace Wavee.Tests;

public class RowClickPolicyTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void SingleClick_IsATap_WhateverTheModifiers(int clicks)
    {
        foreach (var mods in AllCombos())
            Assert.Equal(ItemContainerTrigger.Tap, RowClickPolicy.TriggerOf(clicks, mods));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    public void MultiClick_IsADoubleTap_ExactlyWhenNeitherCtrlNorShiftIsHeld(int clicks)
    {
        foreach (var mods in AllCombos())
        {
            bool selectionChord = (mods & (KeyModifiers.Ctrl | KeyModifiers.Shift)) != 0;
            var expected = selectionChord ? ItemContainerTrigger.Tap : ItemContainerTrigger.DoubleTap;
            Assert.Equal(expected, RowClickPolicy.TriggerOf(clicks, mods));
        }
    }

    [Fact]
    public void NamedCases()
    {
        Assert.Equal(ItemContainerTrigger.DoubleTap, RowClickPolicy.TriggerOf(2, KeyModifiers.None));
        Assert.Equal(ItemContainerTrigger.Tap, RowClickPolicy.TriggerOf(2, KeyModifiers.Ctrl));
        Assert.Equal(ItemContainerTrigger.Tap, RowClickPolicy.TriggerOf(2, KeyModifiers.Shift));
        Assert.Equal(ItemContainerTrigger.Tap, RowClickPolicy.TriggerOf(2, KeyModifiers.Ctrl | KeyModifiers.Shift));
        Assert.Equal(ItemContainerTrigger.DoubleTap, RowClickPolicy.TriggerOf(2, KeyModifiers.Alt));
        Assert.Equal(ItemContainerTrigger.DoubleTap, RowClickPolicy.TriggerOf(2, KeyModifiers.Win));
        Assert.Equal(ItemContainerTrigger.Tap, RowClickPolicy.TriggerOf(2, KeyModifiers.Alt | KeyModifiers.Ctrl));
        Assert.Equal(ItemContainerTrigger.Tap, RowClickPolicy.TriggerOf(1, KeyModifiers.None));
    }

    /// <summary>Every subset of {Shift, Ctrl, Alt, Win}.</summary>
    static IEnumerable<KeyModifiers> AllCombos()
    {
        var bits = new[] { KeyModifiers.Shift, KeyModifiers.Ctrl, KeyModifiers.Alt, KeyModifiers.Win };
        for (int m = 0; m < 1 << bits.Length; m++)
        {
            var mods = KeyModifiers.None;
            for (int b = 0; b < bits.Length; b++)
                if ((m & (1 << b)) != 0) mods |= bits[b];
            yield return mods;
        }
    }
}
