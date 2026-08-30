using System.Linq;
using Wavee.Core.ReleaseNotes;
using Xunit;

namespace Wavee.Tests;

// The inline tokenizer behind every release-notes paragraph. Everything here is a rendering decision the user sees:
// a reference that fails to lift becomes literal "#412" text with no chip, and a mis-scoped bold marker eats the rest
// of the sentence. The tokenizer is also the one component that must NEVER throw — its input is hand-authored prose.
public class MarkdownLiteTests
{
    static InlineToken[] T(string s) => MarkdownLite.Tokenize(s);
    static string Plain(string s) => string.Concat(T(s).Select(t => t.Text));

    [Fact]
    public void Empty_YieldsNothing()
    {
        Assert.Empty(MarkdownLite.Tokenize(""));
        Assert.Empty(MarkdownLite.Tokenize(null!));
    }

    [Fact]
    public void PlainProse_IsOneTextRun()
    {
        var tokens = T("Just a sentence with no markup at all.");
        var only = Assert.Single(tokens);
        Assert.Equal(InlineKind.Text, only.Kind);
        Assert.Equal("Just a sentence with no markup at all.", only.Text);
    }

    // ── emphasis and code ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Bold_Code_AndEmphasis_Split()
    {
        var tokens = T("**Docked video** rides in `MediaPlayerElement`, *finally*.");

        Assert.Equal(InlineKind.Bold, tokens[0].Kind);
        Assert.Equal("Docked video", tokens[0].Text);
        Assert.Equal(InlineKind.Text, tokens[1].Kind);
        Assert.Equal(" rides in ", tokens[1].Text);
        Assert.Equal(InlineKind.Code, tokens[2].Kind);
        Assert.Equal("MediaPlayerElement", tokens[2].Text);
        Assert.Equal(InlineKind.Bold, tokens[4].Kind);      // single-asterisk emphasis renders as weight
        Assert.Equal("finally", tokens[4].Text);
    }

    [Theory]
    [InlineData("**bold with no close")]
    [InlineData("`code with no close")]
    [InlineData("[link with no target")]
    [InlineData("*")]
    [InlineData("**")]
    [InlineData("]([)")]
    public void UnbalancedMarkers_FallThroughAsText(string s)
    {
        var tokens = T(s);
        Assert.All(tokens, t => Assert.Equal(InlineKind.Text, t.Kind));
        Assert.Equal(s, Plain(s));
    }

    [Fact]
    public void BackslashEscapes_TheNextCharacter()
    {
        var only = Assert.Single(T(@"\#412 is not a reference"));
        Assert.Equal(InlineKind.Text, only.Kind);
        Assert.Equal("#412 is not a reference", only.Text);
    }

    // ── links ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MarkdownLink_CarriesTextAndTarget()
    {
        var tokens = T("See the [release notes](https://example.com/notes) for more.");
        var link = Assert.Single(tokens, t => t.Kind == InlineKind.Link);
        Assert.Equal("release notes", link.Text);
        Assert.Equal("https://example.com/notes", link.Target);
    }

    [Fact]
    public void BareUrl_BecomesItsOwnTarget()
    {
        var tokens = T("Docs live at https://example.com/a/b, honest.");
        var url = Assert.Single(tokens, t => t.Kind == InlineKind.Url);
        Assert.Equal("https://example.com/a/b", url.Text);
        Assert.Equal("https://example.com/a/b", url.Target);   // the trailing comma is prose, not the URL
    }

    // ── issue / PR references ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BareIssueRef_Lifts()
    {
        var tokens = T("Fixed in #412 at last.");
        var issue = Assert.Single(tokens, t => t.Kind == InlineKind.Issue);
        Assert.Equal("#412", issue.Text);
        Assert.Equal(412, issue.Number);
        Assert.Null(issue.Repo);
        Assert.Equal("Fixed in ", tokens[0].Text);
        Assert.Equal(" at last.", tokens[2].Text);
    }

    [Fact]
    public void BangRef_IsAPullRequest()
    {
        var pr = Assert.Single(T("Landed as !430."), t => t.Kind == InlineKind.Pr);
        Assert.Equal("!430", pr.Text);
        Assert.Equal(430, pr.Number);
    }

    [Fact]
    public void RefGroup_InParentheses_Lifts_AndKeepsTheSeparators()
    {
        var tokens = T("(#12, !34)");
        Assert.Equal(InlineKind.Text, tokens[0].Kind);
        Assert.Equal("(", tokens[0].Text);
        Assert.Equal(InlineKind.Issue, tokens[1].Kind);
        Assert.Equal(12, tokens[1].Number);
        Assert.Equal(", ", tokens[2].Text);
        Assert.Equal(InlineKind.Pr, tokens[3].Kind);
        Assert.Equal(34, tokens[3].Number);
        Assert.Equal(")", tokens[4].Text);
    }

    [Fact]
    public void QualifiedRef_TakesTheRepoPrefixBackOffTheTextRun()
    {
        var tokens = T("Tracked as christosk92/WaveeMusic#412 upstream.");

        Assert.Equal(InlineKind.Text, tokens[0].Kind);
        Assert.Equal("Tracked as ", tokens[0].Text);            // the prefix is NOT left dangling in the text run
        Assert.Equal(InlineKind.Issue, tokens[1].Kind);
        Assert.Equal("christosk92/WaveeMusic#412", tokens[1].Text);
        Assert.Equal("christosk92/WaveeMusic", tokens[1].Repo);
        Assert.Equal(412, tokens[1].Number);
        Assert.Equal(" upstream.", tokens[2].Text);
    }

    [Theory]
    [InlineData("C# is a language")]              // '#' not preceded by whitespace/'('/','
    [InlineData("C#14 shipped")]                  // ditto, even with digits behind it
    [InlineData("Wow!5 things")]                  // '!' mid-word is punctuation
    [InlineData("Nothing to see # here")]         // no digits
    [InlineData("#abc is not a number")]
    [InlineData("#12abc is not a reference")]     // a reference does not run into a word
    [InlineData("a/b/c#3 is not a repo slug")]    // two slashes
    public void NonReferences_StayText(string s)
    {
        Assert.DoesNotContain(T(s), t => t.Kind is InlineKind.Issue or InlineKind.Pr);
        Assert.Equal(s, Plain(s));
    }

    // ── mentions ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Mention_KeepsTheAtForDisplay_AndTheLoginAsTarget()
    {
        var tokens = T("Thanks @ChristosKarapasias-1 for the fix.");
        var mention = Assert.Single(tokens, t => t.Kind == InlineKind.Mention);
        Assert.Equal("@ChristosKarapasias-1", mention.Text);
        Assert.Equal("ChristosKarapasias-1", mention.Target);
        Assert.Equal(" for the fix.", tokens[^1].Text);
    }

    [Theory]
    [InlineData("mail me at name@example.com")]   // '@' preceded by a letter is not a mention
    [InlineData("@ alone")]                       // nothing to mention
    public void NonMentions_StayText(string s)
    {
        Assert.DoesNotContain(T(s), t => t.Kind == InlineKind.Mention);
        Assert.Equal(s, Plain(s));
    }

    // ── the invariant: display text is lossless for everything except the markers themselves ────────────────────────

    [Fact]
    public void TokenizingIsTotal_OverAWholeChangelogBullet()
    {
        const string Bullet =
            "**Developer mode** — an explicit Settings toggle that gates the diagnostic surfaces. See " +
            "christosk92/WaveeMusic#388 and !401, thanks @someone, docs at https://example.com/dev.";

        var tokens = T(Bullet);
        Assert.Contains(tokens, t => t.Kind == InlineKind.Bold);
        Assert.Contains(tokens, t => t.Kind == InlineKind.Issue && t.Repo == "christosk92/WaveeMusic");
        Assert.Contains(tokens, t => t.Kind == InlineKind.Pr && t.Number == 401);
        Assert.Contains(tokens, t => t.Kind == InlineKind.Mention && t.Target == "someone");
        Assert.Contains(tokens, t => t.Kind == InlineKind.Url);
        Assert.All(tokens, t => Assert.False(string.IsNullOrEmpty(t.Text)));
    }

    [Fact]
    public void Bold_TokenizesItsInside_SoCodeInsideBoldIsCode()
    {
        // The shipping changelog writes "**dismissed with `Esc`,**": the code span must survive inside the bold run
        // (rendered mono + bold), never as literal backticks.
        var t = MarkdownLite.Tokenize("**dismissed with `Esc`,** dropping");
        Assert.Equal(InlineKind.Bold, t[0].Kind); Assert.Equal("dismissed with ", t[0].Text);
        Assert.Equal(InlineKind.Code, t[1].Kind); Assert.Equal("Esc", t[1].Text); Assert.True(t[1].Bold);
        Assert.Equal(InlineKind.Bold, t[2].Kind); Assert.Equal(",", t[2].Text);
        Assert.Equal(InlineKind.Text, t[3].Kind); Assert.Equal(" dropping", t[3].Text); Assert.False(t[3].Bold);
    }
}
