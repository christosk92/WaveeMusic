using Xunit;

namespace Wavee.Tests;

// W1 — the search host's morph logic is decided by LibraryV3SearchRules (System-only), never by the component that
// renders it, so the Escape ladder / blur-close / open-width arithmetic are pinned here without an EditableText, a
// signal or a frame.
public sealed class LibraryV3SearchRulesTests
{
    // ── Escape ladder ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Escape_WithText_Clears()
    {
        Assert.Equal(LibraryV3SearchRules.EscapeAction.Clear, LibraryV3SearchRules.OnEscape("blue"));
    }

    [Fact]
    public void Escape_WhenEmpty_Closes()
    {
        Assert.Equal(LibraryV3SearchRules.EscapeAction.Close, LibraryV3SearchRules.OnEscape(""));
    }

    // ── blur ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Blur_WhenEmpty_Closes()
    {
        Assert.True(LibraryV3SearchRules.ClosesOnBlur(""));
    }

    [Fact]
    public void Blur_WithQuery_StaysOpen()
    {
        Assert.False(LibraryV3SearchRules.ClosesOnBlur("blue"));
    }

    // ── open width ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void OpenWidth_IsThePaneMinusPaddingMinusThePillAndGap()
    {
        // 320 pane, 43 toolbar padding (LeadInset 27 + ContentLaneEnd 16) -> 320 - 43 - 28 - 4 = 245.
        float width = LibraryV3SearchRules.OpenWidth(320f, 43f);
        Assert.Equal(245f, width);
    }

    [Fact]
    public void OpenWidth_NeverGoesBelowClosedWidth()
    {
        // A pane too narrow to fit the pill+gap must not yield a negative or shrinking host.
        float width = LibraryV3SearchRules.OpenWidth(40f, 43f);
        Assert.Equal(LibraryV3SearchRules.ClosedWidth, width);
    }

    [Fact]
    public void Resolve_WidePane_IsInlineAndExpanded_WithALabelledPill()
    {
        var wide = LibraryV3SearchRules.Resolve(LibraryV3SearchRules.InlineWidth, openedByUser: false, hasText: false);
        Assert.True(wide.Inline);
        Assert.True(wide.Expanded);
        Assert.False(wide.SortIconOnly);
    }

    [Fact]
    public void Resolve_NarrowPane_IsAButtonUntilOpenedOrTyped()
    {
        float narrow = LibraryV3SearchRules.InlineWidth - 1f;
        var closed = LibraryV3SearchRules.Resolve(narrow, openedByUser: false, hasText: false);
        Assert.False(closed.Inline);
        Assert.False(closed.Expanded);
        Assert.False(closed.SortIconOnly);            // 299 ≥ 280: the pill keeps its label while the field is a button

        var opened = LibraryV3SearchRules.Resolve(narrow, openedByUser: true, hasText: false);
        Assert.True(opened.Expanded);
        Assert.True(opened.SortIconOnly);             // the field owns the row

        // A query typed while wide survives a drag past the threshold: text alone keeps the field expanded.
        var typed = LibraryV3SearchRules.Resolve(narrow, openedByUser: false, hasText: true);
        Assert.True(typed.Expanded);
    }

    [Fact]
    public void Resolve_VeryNarrowPane_DropsThePillLabelEvenWhenClosed()
    {
        var tiny = LibraryV3SearchRules.Resolve(240f, openedByUser: false, hasText: false);
        Assert.False(tiny.Expanded);
        Assert.True(tiny.SortIconOnly);
    }

    [Fact]
    public void OpenWidth_AtTheFloorBoundary_IsExact()
    {
        // paneWidth - padH - pill - gap == ClosedWidth exactly: the floor must not clip a legitimate value.
        float width = LibraryV3SearchRules.OpenWidth(
            LibraryV3SearchRules.ClosedWidth + 43f + LibraryV3SearchRules.SortIconOnlyWidth + LibraryV3SearchRules.Gap,
            43f);
        Assert.Equal(LibraryV3SearchRules.ClosedWidth, width);
    }
}
