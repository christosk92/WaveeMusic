// ── Wavee.Tests/PageHeadPlanTests.cs — a head's kind and height are data-independent ─────────────────────────────────────
//
// Platform/Page.UI.cs (PageHead.PlanOf). Only Hoisted, Above and Views-null decide the kind: meta, actions, the views'
// labels, the trailing control, the placeholder, the scroll flag and the gutter never move the body.

using System.Collections.Generic;
using FluentGpu.Dsl;
using Xunit;

namespace Wavee.Tests;

public sealed class PageHeadPlanTests
{
    static PageHeadSpec Base() => new("Title") { Views = [] };

    static void AssertSame(PageHeadSpec spec)
    {
        var baseline = PageHead.PlanOf(Base());
        Assert.Equal(baseline, PageHead.PlanOf(spec));
        Assert.Equal(PageHeadRules.Extent(baseline), PageHeadRules.Extent(PageHead.PlanOf(spec)));
    }

    public static IEnumerable<object?[]> Metas()
    {
        yield return new object?[] { null };
        yield return new object?[] { "" };
        yield return new object?[] { new string('x', 400) };
    }

    [Theory, MemberData(nameof(Metas))]
    public void Meta_IsNotAnInput(string? meta) => AssertSame(Base() with { Meta = meta });

    [Fact] public void Actions_AreNotAnInput()
    {
        AssertSame(Base() with { Actions = null });
        AssertSame(Base() with { Actions = new BoxEl() });
    }

    [Fact] public void ViewsTrailing_IsNotAnInput()
    {
        AssertSame(Base() with { ViewsTrailing = null });
        AssertSame(Base() with { ViewsTrailing = new BoxEl() });
    }

    [Fact] public void ViewsPlaceholder_IsNotAnInput()
    {
        AssertSame(Base() with { ViewsPlaceholder = null });
        AssertSame(Base() with { ViewsPlaceholder = new BoxEl() });
    }

    [Fact] public void ViewsScroll_IsNotAnInput()
    {
        AssertSame(Base() with { ViewsScroll = true });
        AssertSame(Base() with { ViewsScroll = false });
    }

    [Fact] public void ViewsLabelsArriving_DoNotChangeTheKind()
    {
        AssertSame(Base() with { Views = [] });
        AssertSame(Base() with { Views = ["a", "b", "c"] });
    }

    [Fact] public void Gutter_IsNotAnInput()
    {
        AssertSame(Base() with { Gutter = 16f });
        AssertSame(Base() with { Gutter = 36f });
    }

    [Fact] public void OnlyHoistedAboveAndViewsNull_ChangeTheKind()
    {
        Assert.Equal(PageHeadKind.TitleViews, PageHead.PlanOf(Base()));
        Assert.Equal(PageHeadKind.Title, PageHead.PlanOf(Base() with { Views = null }));
        Assert.Equal(PageHeadKind.CrumbTitleViews, PageHead.PlanOf(Base() with { Above = new BoxEl() }));
        Assert.Equal(PageHeadKind.CrumbTitle, PageHead.PlanOf(Base() with { Above = new BoxEl(), Views = null }));
        Assert.Equal(PageHeadKind.Hoisted, PageHead.PlanOf(Base() with { Hoisted = true, Above = new BoxEl() }));
    }

    [Fact] public void Hoisted_IsTheEmptyHeadTopStrip()
    {
        var head = (BoxEl)PageHead.Create(Base() with { Hoisted = true, Meta = "42 songs", Actions = new BoxEl(), ViewsTrailing = new BoxEl() });
        Assert.Equal(PageGeometry.HeadTop, head.Height.Value);
        Assert.Equal(PageHead.Reflow, head.Animate);
        Assert.Empty(head.Children);
    }

    [Fact] public void Create_SetsTheKindsHeightAndTheReflow()
    {
        var spec = Base() with { Meta = "42 songs", Gutter = 32f };
        var head = (BoxEl)PageHead.Create(spec);
        Assert.Equal(PageHeadRules.Extent(PageHeadKind.TitleViews), head.Height.Value);
        Assert.Equal(PageHead.Reflow, head.Animate);
        // the same head with the data absent is the same height
        var bare = (BoxEl)PageHead.Create(Base());
        Assert.Equal(head.Height.Value, bare.Height.Value);
    }

    [Fact] public void Create_WithoutAMetaLine_ReservesNoMetaRow()
    {
        var withMeta = (BoxEl)PageHead.Create(Base());
        var noMeta = (BoxEl)PageHead.Create(Base() with { HasMeta = false, Meta = "ignored" });
        Assert.Equal(PageHeadRules.Extent(PageHeadKind.TitleViews, hasMeta: false), noMeta.Height.Value);
        Assert.Equal(withMeta.Height.Value - 20f, noMeta.Height.Value);
        // the head-top spacer, the title row and the views gap + row are all that is left (no meta spacer, no meta slot)
        Assert.Equal(withMeta.Children.Length - 2, noMeta.Children.Length);
    }
}
