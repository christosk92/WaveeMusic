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
    public void OpenWidth_IsThePaneMinusPaddingMinusTitleAndTrailingControlsAndTwoGaps()
    {
        // 500 pane, 43 toolbar padding (LeadInset 27 + ContentLaneEnd 16) -> 500 - 43 - 96 - 96 - 8 = 257.
        float width = LibraryV3SearchRules.OpenWidth(500f, 43f);
        Assert.Equal(257f, width);
    }

    [Fact]
    public void OpenWidth_NeverGoesBelowClosedWidth()
    {
        // A pane too narrow to fit the title + trailing controls must not yield a negative or shrinking host.
        float width = LibraryV3SearchRules.OpenWidth(40f, 43f);
        Assert.Equal(LibraryV3SearchRules.ClosedWidth, width);
    }

    [Fact]
    public void Resolve_WidePane_IsInlineAndExpanded()
    {
        var wide = LibraryV3SearchRules.Resolve(LibraryV3SearchRules.InlineWidth, openedByUser: false, hasText: false);
        Assert.True(wide.Inline);
        Assert.True(wide.Expanded);
    }

    [Fact]
    public void Resolve_NarrowPane_IsAButtonUntilOpenedOrTyped()
    {
        float narrow = LibraryV3SearchRules.InlineWidth - 1f;
        var closed = LibraryV3SearchRules.Resolve(narrow, openedByUser: false, hasText: false);
        Assert.False(closed.Inline);
        Assert.False(closed.Expanded);

        var opened = LibraryV3SearchRules.Resolve(narrow, openedByUser: true, hasText: false);
        Assert.False(opened.Inline);
        Assert.True(opened.Expanded);

        // A query typed while wide survives a drag past the threshold: text alone keeps the field expanded.
        var typed = LibraryV3SearchRules.Resolve(narrow, openedByUser: false, hasText: true);
        Assert.True(typed.Expanded);
    }

    [Fact]
    public void OpenWidth_AtTheFloorBoundary_IsExact()
    {
        // paneWidth - padH - title - trailing - 2*gap == ClosedWidth exactly: the floor must not clip a legitimate value.
        float width = LibraryV3SearchRules.OpenWidth(
            LibraryV3SearchRules.ClosedWidth + 43f + LibraryV3SearchRules.TitleReserve
                + LibraryV3SearchRules.TrailingControlsWidth + LibraryV3SearchRules.Gap * 2f,
            43f);
        Assert.Equal(LibraryV3SearchRules.ClosedWidth, width);
    }
}
