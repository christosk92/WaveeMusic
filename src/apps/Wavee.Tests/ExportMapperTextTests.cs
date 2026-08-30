using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// The two free-text helpers every Home card runs through: the HTML flattener behind a card subtitle, and the seed-chip
// splitter behind a mix's description. Both used to be lossy in ways that are invisible in a diff and glaring on screen.
public sealed class ExportMapperTextTests
{
    // ── ToPlainText ────────────────────────────────────────────────────────────────────────────────────────────
    // The regression: CardFromEntity decodes entities (HtmlText) BEFORE flattening, so "&lt;3" reaches the flattener as
    // a live '<'. The old walk treated ANY '<' as a tag opener and only '>' as a closer, so everything after it was
    // swallowed and the card rendered the single word "I".
    [Fact]
    public void ToPlainText_KeepsABareLessThan_TheDecodedEmoticonSurvives()
    {
        var decoded = SpotifyExportMapper.HtmlText("I &lt;3 this mix");
        Assert.Equal("I <3 this mix", decoded);
        Assert.Equal("I <3 this mix", SpotifyExportMapper.ToPlainText(decoded));
    }

    [Theory]
    // Real markup is still dropped, and the text inside it is still kept.
    [InlineData("<b>Bold</b> and plain", "Bold and plain")]
    [InlineData("Here's <a href=\"spotify:playlist:1\">puppy love</a> for you", "Here's puppy love for you")]
    [InlineData("<!-- a comment -->kept", "kept")]
    [InlineData("</b>closing tags too", "closing tags too")]
    // '<' followed by anything that cannot start a tag name is prose.
    [InlineData("Kids <3 & bops", "Kids <3 & bops")]
    [InlineData("a < b and b < c", "a < b and b < c")]
    // A stray '>' is text now as well; it used to be eaten as a phantom tag terminator.
    [InlineData("3 > 2 and <b>true</b>", "3 > 2 and true")]
    // An opener that never closes: the remainder is the only content there is, so it is emitted rather than swallowed.
    [InlineData("Great mix <b", "Great mix <b")]
    [InlineData("Truncated <a href=\"spotify:playlist:1\" rel", "Truncated <a href=\"spotify:playlist:1\" rel")]
    // Whitespace still collapses, and the result is still right-trimmed.
    [InlineData("<b>spaced   out\n\ttext  </b>", "spaced out text")]
    public void ToPlainText_FlattensMarkupWithoutSwallowingProse(string html, string expected)
        => Assert.Equal(expected, SpotifyExportMapper.ToPlainText(html));

    [Fact]
    public void ToPlainText_PassesNullAndEmptyThrough()
    {
        Assert.Null(SpotifyExportMapper.ToPlainText(null));
        Assert.Equal("", SpotifyExportMapper.ToPlainText(""));
    }

    // ── DropTrailingConjunction (reached through ParseSeeds, its only caller) ───────────────────────────────────
    // The regression: the trailer was removed by TOKEN COUNT — "drop the last two tokens whenever there are three" —
    // which truncated every 3+-token artist name that happened to land in the final comma segment.
    [Theory]
    // The real trailer is still dropped (these two are the shapes the server actually sends).
    [InlineData("D.O., Wonstein, KIMMUSEUM and more", new[] { "D.O.", "Wonstein", "KIMMUSEUM" })]
    [InlineData("With LE SSERAFIM, NewJeans, Daniel Seavey en meer", new[] { "LE SSERAFIM", "NewJeans", "Daniel Seavey" })]
    // …and a multi-token NAME in that same trailing position is left whole. Each of these used to lose its last two
    // tokens: "Rage Against", "Wind", "Simon".
    [InlineData("Nine Inch Nails, Rage Against the Machine",
        new[] { "Nine Inch Nails", "Rage Against the Machine" })]
    [InlineData("Wonstein, Earth, Wind & Fire", new[] { "Wonstein", "Earth", "Wind & Fire" })]
    // "and" IS a conjunction here, but "Garfunkel" is a proper noun, not a quantity word — both halves must match.
    [InlineData("KIMMUSEUM, Simon and Garfunkel", new[] { "KIMMUSEUM", "Simon and Garfunkel" })]
    [InlineData("NewJeans, Florence and the Machine", new[] { "NewJeans", "Florence and the Machine" })]
    // A two-token trailing name was already safe and stays safe.
    [InlineData("ILLIT, Shawn Mendes", new[] { "ILLIT", "Shawn Mendes" })]
    public void ParseSeeds_DropsOnlyARealTrailer_NeverAMultiTokenName(string description, string[] expected)
        => Assert.Equal(expected, SpotifyExportMapper.ParseSeeds(description));
}
