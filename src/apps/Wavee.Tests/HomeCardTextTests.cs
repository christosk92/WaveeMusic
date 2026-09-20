// ── Wavee.Tests/HomeCardTextTests.cs — the card vocabulary's number / duration / blurb formats (Wave 5, owner P) ────
//
// 0.2.9 `HomeCards.Duration/Hours/CompactNumber/FirstSentence` + `SpotifyExportMapper.ToPlainText`, which had no tests.
// Every expected duration string is built from the SAME typed `Strings.*` call the rule uses (no hardcoded English), and
// every culture-dependent fact pins a culture CLONED from invariant (the repo builds with InvariantGlobalization, where a
// named culture cannot be constructed) and restores the previous one.

using System.Globalization;
using Wavee;
using Xunit;

namespace Wavee.Tests;

public class HomeCardTextTests
{
    static T WithCulture<T>(CultureInfo culture, Func<T> read)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = culture;
        try { return read(); }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    static CultureInfo Comma()
    {
        var comma = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        comma.NumberFormat.NumberDecimalSeparator = ",";
        comma.NumberFormat.NumberGroupSeparator = ".";
        return comma;
    }

    // ── Duration ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void No_duration_is_an_empty_string()
    {
        Assert.Equal("", HomeCardText.Duration(0));
        Assert.Equal("", HomeCardText.Duration(-5_000));
    }

    [Fact]
    public void Under_an_hour_is_minutes_and_never_zero_minutes()
    {
        Assert.Equal(Strings.Detail.DurationMin(45), HomeCardText.Duration(45 * 60_000L));
        Assert.Equal(Strings.Detail.DurationMin(1), HomeCardText.Duration(10_000L));      // rounds to 0 → at least 1
    }

    [Fact]
    public void An_hour_or_more_is_hours_and_minutes()
    {
        Assert.Equal(Strings.Detail.DurationHrMin(1, 5), HomeCardText.Duration(65 * 60_000L));
        Assert.Equal(Strings.Detail.DurationHrMin(1, 0), HomeCardText.Duration(60 * 60_000L));
        Assert.Equal(Strings.Detail.DurationHrMin(2, 1), HomeCardText.Duration(121 * 60_000L + 20_000L));   // minutes ROUND
    }

    // ── Hours (the prototype's hrs()) ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Hours_reads_tenths_past_the_hour_and_whole_minutes_under_it()
    {
        Assert.Equal("1.3 h", WithCulture(CultureInfo.InvariantCulture, () => HomeCardText.Hours(4_680_000)));
        Assert.Equal("1.0 h", WithCulture(CultureInfo.InvariantCulture, () => HomeCardText.Hours(3_600_000)));
        Assert.Equal("45 m", WithCulture(CultureInfo.InvariantCulture, () => HomeCardText.Hours(2_700_000)));
        Assert.Equal("", HomeCardText.Hours(0));
    }

    [Fact]
    public void Hours_is_culture_aware_on_the_value()
        => Assert.Equal("1,3 h", WithCulture(Comma(), () => HomeCardText.Hours(4_680_000)));

    // ── CompactNumber ───────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1_234_567_890L, "1.2B")]
    [InlineData(3_400_000L, "3.4M")]
    [InlineData(5_600L, "5.6K")]
    [InlineData(1_000L, "1K")]
    [InlineData(999L, "999")]
    [InlineData(0L, "0")]
    public void CompactNumber_truncates_to_one_decimal_and_a_suffix(long n, string expected)
        => Assert.Equal(expected, WithCulture(CultureInfo.InvariantCulture, () => HomeCardText.CompactNumber(n)));

    [Fact]
    public void CompactNumber_uses_the_cultures_own_separators()
    {
        Assert.Equal("1,5M", WithCulture(Comma(), () => HomeCardText.CompactNumber(1_500_000)));
        Assert.Equal("5,6K", WithCulture(Comma(), () => HomeCardText.CompactNumber(5_600)));
    }

    // ── FirstSentence + PlainText ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FirstSentence_strips_markup_first_then_cuts_at_the_first_sentence()
    {
        const string html = "<a href=\"https://open.spotify.com/playlist/x\">Chill</a> vibes for a long evening. "
                          + "A second sentence that restates it.";
        Assert.Equal("Chill vibes for a long evening.", HomeCardText.FirstSentence(html));
    }

    [Fact]
    public void FirstSentence_keeps_a_short_lead_whole_rather_than_cutting_it()
    {
        // The cut only applies past index 20 — "Hi." is not a sentence worth a whole card.
        Assert.Equal("Hi. There is a lot more to say here.", HomeCardText.FirstSentence("Hi. There is a lot more to say here."));
    }

    [Fact]
    public void FirstSentence_of_nothing_is_empty()
    {
        Assert.Equal("", HomeCardText.FirstSentence(null));
        Assert.Equal("", HomeCardText.FirstSentence("   "));
        Assert.Equal("", HomeCardText.FirstSentence("<b></b>"));
    }

    [Fact]
    public void PlainText_drops_real_tags_keeps_their_text_and_collapses_whitespace()
    {
        Assert.Equal("Bold and spaced", HomeCardText.PlainText("<b>Bold</b>  and \n <i>spaced</i>"));
        Assert.Equal("One Two", HomeCardText.PlainText("<p>One</p>\n\n<p>Two</p>"));
    }

    [Fact]
    public void PlainText_a_less_than_that_does_not_open_a_tag_is_prose()
    {
        // The HTML5 tag-open rule: '<' followed by a letter, '/' or '!'. "I <3 this mix" must not collapse to "I".
        Assert.Equal("I <3 this mix", HomeCardText.PlainText("I <3 this mix"));
        Assert.Equal("a < b", HomeCardText.PlainText("a < b"));
        // An opener that never finds its '>' is emitted as text rather than dropped.
        Assert.Equal("<unclosed tail", HomeCardText.PlainText("<unclosed tail"));
    }

    [Fact]
    public void PlainText_without_markup_returns_the_input_itself()
    {
        const string plain = "No markup at all";
        Assert.Same(plain, HomeCardText.PlainText(plain));
        Assert.Null(HomeCardText.PlainText(null));
        Assert.Equal("", HomeCardText.PlainText(""));
    }
}
