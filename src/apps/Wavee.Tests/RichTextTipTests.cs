// ── Wavee.Tests/RichTextTipTests.cs — the detail description's full-text tooltip ───────────────────────────────────────
//
// The owner's report (2026-09-30): a clamped playlist description ("…updated every Monday. You'll …") left the rest
// unreachable. The paragraph now carries the inline "… More" (the engine's OverflowSuffix, shown only when the body is
// clipped) and, while collapsed, a tooltip with its whole text — `RichTextTip.For` is that tooltip's pure decision.
// Engine-free: string in, string out (CLAUDE.md "no source-text tests").

using Xunit;

namespace Wavee.Tests;

public class RichTextTipTests
{
    [Fact]
    public void Collapsed_ShowsTheWholeText_Trimmed()
        => Assert.Equal("Your weekly mixtape of fresh music. Updated every Monday.",
                        RichTextTip.For(expanded: false, "  Your weekly mixtape of fresh music. Updated every Monday. \n"));

    [Fact]
    public void Expanded_HasNoTip_ThePageAlreadyShowsEverything()
        => Assert.Null(RichTextTip.For(expanded: true, "Your weekly mixtape of fresh music."));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\n  ")]
    public void BlankBody_HasNoTip(string? plain)
        => Assert.Null(RichTextTip.For(expanded: false, plain));

    /// <summary>Paragraph breaks survive (the tip reads like the description), but a run of blank lines folds to one.</summary>
    [Fact]
    public void BlankLineRuns_FoldToOneParagraphBreak()
        => Assert.Equal("First paragraph.\n\nSecond.", RichTextTip.For(expanded: false, "First paragraph.\n\n\n\n\nSecond."));

    /// <summary>A real playlist description (Spotify caps them at 300 characters) always fits whole.</summary>
    [Fact]
    public void DescriptionLength_FitsWhole()
    {
        string plain = new string('a', 60) + " " + new string('b', 120) + " " + new string('c', 118);
        Assert.Equal(300, plain.Length);
        Assert.Equal(plain, RichTextTip.For(expanded: false, plain));
    }

    /// <summary>A show-notes-length body is capped at a WORD boundary with an ellipsis — a tooltip is a glance, and the
    /// inline "More" is the way to the rest.</summary>
    [Fact]
    public void LongBody_IsCappedAtAWordBoundary()
    {
        string plain = string.Join(' ', Enumerable.Repeat("lorem", 400));
        string tip = RichTextTip.For(expanded: false, plain)!;
        Assert.EndsWith("…", tip);
        Assert.True(tip.Length <= RichTextTip.MaxChars + 1, $"tip is {tip.Length} characters");
        string body = tip[..^1];
        Assert.StartsWith(body, plain);
        Assert.EndsWith("lorem", body);   // never a torn word
    }
}
