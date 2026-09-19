// ── Wavee.Tests/PlayLinkRulesTests.cs — "Play ▸ Link…", the paste-a-link dialog's decisions ────────────────────────
//
// Ported from _old/Wavee.Tests/Actions/PlayLinkActionsTests.cs (G6) onto `Actions.PlayLinkRules`. The manifest-shaped
// facts (DeclaresMatch, FormFor, IsNotOwned) belong to owner T's module host in 0.3 — a module row arrives with its
// label already folded, and "nobody owns this" is a null match rather than an exception code — so those facts move
// with the host; everything the dialog itself decides is pinned here.

using Xunit;

namespace Wavee.Tests;

public class PlayLinkRulesTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("  https://youtu.be/abc  ", "https://youtu.be/abc")]
    [InlineData("https://youtu.be/abc\r\n", "https://youtu.be/abc")]
    public void Normalize_trims_what_the_router_will_see(string? input, string expected)
        => Assert.Equal(expected, Actions.PlayLinkRules.Normalize(input));

    [Fact]
    public void CanSubmit_is_false_for_whitespace_only()
    {
        Assert.False(Actions.PlayLinkRules.CanSubmit(null));
        Assert.False(Actions.PlayLinkRules.CanSubmit("   \r\n"));
        Assert.True(Actions.PlayLinkRules.CanSubmit("  x  "));
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=tRsQsTMvPNg", true)]
    [InlineData("http://stream.example.org:8000/live", true)]
    [InlineData("HTTPS://TWITCH.TV/somebody", true)]
    [InlineData("  https://youtu.be/abc  ", true)]
    [InlineData("youtube.com/watch?v=abc", false)]
    [InlineData(@"C:\music\track.mp3", false)]
    [InlineData("https://a b", false)]
    [InlineData("https://", false)]
    [InlineData(null, false)]
    public void LooksLikeUrl_is_the_clipboard_prefill_test(string? text, bool expected)
        => Assert.Equal(expected, Actions.PlayLinkRules.LooksLikeUrl(text));

    [Fact]
    public void PrefillFrom_seeds_only_links()
    {
        Assert.Equal("https://youtu.be/abc", Actions.PlayLinkRules.PrefillFrom("  https://youtu.be/abc "));
        Assert.Equal("", Actions.PlayLinkRules.PrefillFrom("Daft Punk – Around the World"));
        Assert.Equal("", Actions.PlayLinkRules.PrefillFrom(null));
    }

    [Fact]
    public void The_browser_escape_hatch_only_ever_opens_a_web_url()
    {
        Assert.True(Actions.PlayLinkRules.IsWebUrl("https://youtu.be/abc"));
        Assert.False(Actions.PlayLinkRules.IsWebUrl("file:///C:/Windows/notepad.exe"));
        Assert.False(Actions.PlayLinkRules.IsWebUrl("spotify:track:abc"));
        Assert.False(Actions.PlayLinkRules.IsWebUrl(""));
    }

    [Fact]
    public void MenuLabel_prefers_authored_copy_then_the_name_with_one_ellipsis()
    {
        Assert.Equal("YouTube…", Actions.PlayLinkRules.MenuLabel("YouTube…", "YouTube", "wavee.youtube"));
        Assert.Equal("Twitch…", Actions.PlayLinkRules.MenuLabel(null, "Twitch", "wavee.twitch"));
        Assert.Equal("Twitch…", Actions.PlayLinkRules.MenuLabel(null, "Twitch…", "wavee.twitch"));
        Assert.Equal("Radio…", Actions.PlayLinkRules.MenuLabel("   ", "Radio", "wavee.radio"));
        Assert.Equal("wavee.mystery…", Actions.PlayLinkRules.MenuLabel(null, "", "wavee.mystery"));
    }

    [Fact]
    public void PlaceholderFor_lets_the_named_module_speak()
    {
        const string generic = "Paste a YouTube, Twitch or radio stream link";
        Assert.Equal("Paste a YouTube link", Actions.PlayLinkRules.PlaceholderFor("Paste a YouTube link", generic));
        Assert.Equal(generic, Actions.PlayLinkRules.PlaceholderFor(null, generic));
        Assert.Equal(generic, Actions.PlayLinkRules.PlaceholderFor("  ", generic));
    }

    [Fact]
    public void MatchStatus_is_module_then_title_then_live()
        => Assert.Equal("YouTube · Claude FM · LIVE", Actions.PlayLinkRules.MatchStatus("YouTube", "Claude FM", true, "LIVE"));

    [Fact]
    public void MatchStatus_drops_the_segments_it_cannot_state()
    {
        Assert.Equal("YouTube · Claude FM", Actions.PlayLinkRules.MatchStatus("YouTube", "Claude FM", false, "LIVE"));
        Assert.Equal("YouTube · LIVE", Actions.PlayLinkRules.MatchStatus("YouTube", null, true, "LIVE"));
        Assert.Equal("YouTube · LIVE", Actions.PlayLinkRules.MatchStatus("YouTube", "   ", true, "LIVE"));
        Assert.Equal("YouTube", Actions.PlayLinkRules.MatchStatus("YouTube", "", false, "LIVE"));
        Assert.Equal("Claude FM", Actions.PlayLinkRules.MatchStatus("", "Claude FM", false, "LIVE"));
        Assert.Equal("", Actions.PlayLinkRules.MatchStatus("", null, false, "LIVE"));
        Assert.Equal("YouTube · Claude FM", Actions.PlayLinkRules.MatchStatus("  YouTube ", " Claude FM  ", false, "LIVE"));
    }

    [Fact]
    public void A_cancelled_look_up_says_nothing()
    {
        Assert.True(Actions.PlayLinkRules.IsCancelled(new OperationCanceledException()));
        Assert.Equal("", Actions.PlayLinkRules.ErrorText(new OperationCanceledException(), "fallback"));
        Assert.Equal("", Actions.PlayLinkRules.ErrorText(null, "fallback"));
    }

    [Theory]
    [InlineData("YouTube is blocking this network", "YouTube is blocking this network")]
    [InlineData("  subscriber-only  ", "subscriber-only")]
    [InlineData("", "fallback")]
    public void ErrorText_prefers_the_modules_own_words(string message, string expected)
        => Assert.Equal(expected, Actions.PlayLinkRules.ErrorText(new InvalidOperationException(message), "fallback"));

    [Fact]
    public void Every_failed_play_lane_shares_one_toast_key()
        => Assert.Equal("wavee.play.failed", Actions.PlayLinkRules.FailureToastKey);
}
