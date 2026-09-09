using Xunit;

namespace Wavee.Tests;

// W1 — the search host's morph logic is decided by LibraryV3SearchRules (System-only), never by the component that
// renders it, so the Escape ladder / blur-close / shape rules are pinned here without an EditableText, a signal or a
// frame. The width arithmetic (OpenWidth/TitleReserve/TrailingControlsWidth) is GONE — the open narrow field now
// takes the whole row (LibraryV3HeaderRules.Resolve decides that, and this file's Resolve just reads its Shape).
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

    // ── shape (delegates to LibraryV3HeaderRules) ────────────────────────────────────────────────────────────────────

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
    public void Resolve_AtTheBoundary_319IsNarrow_320IsInline()
    {
        var below = LibraryV3SearchRules.Resolve(319f, openedByUser: false, hasText: false);
        Assert.False(below.Inline);

        var at = LibraryV3SearchRules.Resolve(320f, openedByUser: false, hasText: false);
        Assert.True(at.Inline);
        Assert.True(at.Expanded);
    }
}
