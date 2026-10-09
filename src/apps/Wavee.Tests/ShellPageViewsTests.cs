// ── Wavee.Tests/ShellPageViewsTests.cs — the page views publications store ────────────────────────────────────────────
//
// Shell/Shell.cs (Shell.PageViews). Under Zune the band draws a page's views; the store bumps Version only when the
// visible data changes (labels or the selected signal), so the band re-renders at navigation rate, not per page render.

using System;
using FluentGpu.Dsl;
using FluentGpu.Signals;
using Xunit;

namespace Wavee.Tests;

public sealed class ShellPageViewsTests
{
    public ShellPageViewsTests() => Shell.PageViews.Clear();

    static Shell.PageViewsPublication Pub(string[] labels, Signal<int> sel, Func<Element>? trailing = null)
        => new(labels, sel, static _ => { }, trailing);

    [Fact] public void Publish_ThenFor_ReturnsIt()
    {
        var sel = new Signal<int>(0);
        Assert.True(Shell.PageViews.Publish("home", Pub(["A", "B"], sel)));
        Assert.Equal(new[] { "A", "B" }, Shell.PageViews.For("home")!.Labels);
        Assert.Same(sel, Shell.PageViews.Peek("home")!.Selected);
    }

    [Fact] public void EqualLabels_SameSignal_DoNotBump()
    {
        var sel = new Signal<int>(0);
        Shell.PageViews.Publish("home", Pub(["A", "B"], sel));
        int before = Shell.PageViews.Version.Peek();
        Assert.False(Shell.PageViews.Publish("home", Pub(["A", "B"], sel)));
        Assert.Equal(before, Shell.PageViews.Version.Peek());
    }

    [Fact] public void NewLabels_Bump()
    {
        var sel = new Signal<int>(0);
        Shell.PageViews.Publish("home", Pub(["A", "B"], sel));
        int before = Shell.PageViews.Version.Peek();
        Assert.True(Shell.PageViews.Publish("home", Pub(["A", "B", "C"], sel)));
        Assert.Equal(before + 1, Shell.PageViews.Version.Peek());
    }

    [Fact] public void ADifferentSelectedSignal_Bumps()
    {
        Shell.PageViews.Publish("home", Pub(["A"], new Signal<int>(0)));
        int before = Shell.PageViews.Version.Peek();
        Assert.True(Shell.PageViews.Publish("home", Pub(["A"], new Signal<int>(0))));
        Assert.Equal(before + 1, Shell.PageViews.Version.Peek());
    }

    [Fact] public void ADifferentTrailingFactoryAlone_DoesNotBump_ButIsStored()
    {
        var sel = new Signal<int>(0);
        Func<Element> first = static () => new BoxEl(), second = static () => new BoxEl();
        Shell.PageViews.Publish("home", Pub(["A"], sel, first));
        int before = Shell.PageViews.Version.Peek();
        Assert.False(Shell.PageViews.Publish("home", Pub(["A"], sel, second)));
        Assert.Equal(before, Shell.PageViews.Version.Peek());
        Assert.Same(second, Shell.PageViews.Peek("home")!.Trailing);
    }

    [Fact] public void For_AnotherRoute_IsNull()
    {
        Shell.PageViews.Publish("home", Pub(["A"], new Signal<int>(0)));
        Assert.Null(Shell.PageViews.For("recents"));
    }

    [Fact] public void Eviction_KeepsTheNewestSixteen()
    {
        for (int i = 0; i < Shell.PageViews.Capacity + 3; i++)
            Shell.PageViews.Publish("r" + i, Pub(["A"], new Signal<int>(0)));
        Assert.Null(Shell.PageViews.Peek("r0"));
        Assert.Null(Shell.PageViews.Peek("r2"));
        Assert.NotNull(Shell.PageViews.Peek("r3"));
        Assert.NotNull(Shell.PageViews.Peek("r" + (Shell.PageViews.Capacity + 2)));
    }

    [Fact] public void Clear_DropsEverything()
    {
        Shell.PageViews.Publish("home", Pub(["A"], new Signal<int>(0)));
        Shell.PageViews.Clear();
        Assert.Null(Shell.PageViews.Peek("home"));
    }
}
