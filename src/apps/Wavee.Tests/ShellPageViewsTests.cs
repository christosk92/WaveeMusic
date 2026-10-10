// ── Wavee.Tests/ShellPageViewsTests.cs — the page views publications store ────────────────────────────────────────────
//
// Shell/Shell.cs (Shell.PageViews). Under Zune the band draws a page's views; the store bumps Version only when the
// visible data changes (labels or the selected signal), so the band re-renders at navigation rate, not per page render.

using System;
using FluentGpu.Dsl;
using FluentGpu.Signals;
using Xunit;

namespace Wavee.Tests;

[Collection(ShellStaticsCollection.Name)]
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

/// <summary>The entity band publications (Shell.PageBands): the same idiom as the views store. Version moves only on DATA
/// (the title, the pivot labels, the active signal), never on a new delegate or actions factory.</summary>
public sealed class ShellPageBandsTests
{
    public ShellPageBandsTests() => Shell.PageBands.Clear();

    static Shell.PageBandPublication Pub(string title, string[] pivots, IReadSignal<int> active, Action<int>? onPivot = null,
        Func<Element>? actions = null)
        => new(title, pivots, active, onPivot ?? (static _ => { }), actions);

    [Fact] public void AFirstPublish_BumpsTheVersion_AndIsReadable()
    {
        int before = Shell.PageBands.Version.Peek();
        var active = new Signal<int>(0);
        Assert.True(Shell.PageBands.Publish("artist:x", Pub("X", ["a", "b"], active)));
        Assert.Equal(before + 1, Shell.PageBands.Version.Peek());
        Assert.Equal("X", Shell.PageBands.For("artist:x")!.Title);
        Assert.Same(active, Shell.PageBands.Peek("artist:x")!.Active);
    }

    [Fact] public void TheSameTitleLabelsAndSignal_DoNotBump_NorDoesANewDelegateAlone()
    {
        var active = new Signal<int>(0);
        Shell.PageBands.Publish("artist:x", Pub("X", ["a", "b"], active));
        int before = Shell.PageBands.Version.Peek();
        Assert.False(Shell.PageBands.Publish("artist:x", Pub("X", ["a", "b"], active)));
        Func<Element> factory = static () => new BoxEl();
        Action<int> next = static _ => { };
        Assert.False(Shell.PageBands.Publish("artist:x", Pub("X", ["a", "b"], active, next, factory)));
        Assert.Equal(before, Shell.PageBands.Version.Peek());
        Assert.Same(next, Shell.PageBands.Peek("artist:x")!.OnPivot);          // stored, so a click resolves the latest
        Assert.Same(factory, Shell.PageBands.Peek("artist:x")!.Actions);
    }

    [Fact] public void AChangedTitleLabelOrSignal_Bumps()
    {
        var active = new Signal<int>(0);
        Shell.PageBands.Publish("artist:x", Pub("X", ["a", "b"], active));
        int v = Shell.PageBands.Version.Peek();
        Assert.True(Shell.PageBands.Publish("artist:x", Pub("Y", ["a", "b"], active)));
        Assert.True(Shell.PageBands.Publish("artist:x", Pub("Y", ["a", "c"], active)));
        Assert.True(Shell.PageBands.Publish("artist:x", Pub("Y", ["a", "c"], new Signal<int>(0))));
        Assert.Equal(v + 3, Shell.PageBands.Version.Peek());
    }

    [Fact] public void Eviction_IsLeastRecentlyPublished()
    {
        for (int i = 0; i < Shell.PageBands.Capacity; i++)
            Shell.PageBands.Publish("r" + i, Pub("T", ["a"], new Signal<int>(0)));
        Shell.PageBands.Publish("r0", Pub("T", ["a"], new Signal<int>(0)));       // r0 is now the newest
        Shell.PageBands.Publish("new", Pub("T", ["a"], new Signal<int>(0)));       // evicts r1, the oldest
        Assert.NotNull(Shell.PageBands.Peek("r0"));
        Assert.Null(Shell.PageBands.Peek("r1"));
        Assert.NotNull(Shell.PageBands.Peek("new"));
    }

    [Fact] public void Clear_EmptiesTheStore()
    {
        Shell.PageBands.Publish("artist:x", Pub("X", ["a"], new Signal<int>(0)));
        Shell.PageBands.Clear();
        Assert.Null(Shell.PageBands.Peek("artist:x"));
    }
}
