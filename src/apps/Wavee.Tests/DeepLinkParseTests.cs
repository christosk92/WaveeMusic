using Xunit;

namespace Wavee.Tests;

// The bare-`spotify:` half of the deep-link parser (the opt-in scheme handler shares ONE activation path with
// `wavee://`). What is pinned here is WHICH kind becomes which verb — the P4 rule that a PLAYABLE is a track OR an
// episode, and that everything the shell has no route for is refused rather than guessed at.
public class DeepLinkParseTests
{
    [Fact]
    public void TrackUri_BecomesPlay()
    {
        Assert.True(DeepLink.TryParse("spotify:track:4cOdK2wGLETKBW3PvgPWqT", out var verb));
        Assert.Equal(DeepLinkKind.Play, verb.Kind);
        Assert.Equal("spotify:track:4cOdK2wGLETKBW3PvgPWqT", verb.Context);
    }

    // The P4 widening: an episode is a playable, so a shared podcast link plays instead of doing nothing at all (it
    // used to miss the Track gate, find no page route, and be refused).
    [Fact]
    public void EpisodeUri_BecomesPlay()
    {
        Assert.True(DeepLink.TryParse("spotify:episode:512ojhOuo1ktJprKbVcKyQ", out var verb));
        Assert.Equal(DeepLinkKind.Play, verb.Kind);
        Assert.Equal("spotify:episode:512ojhOuo1ktJprKbVcKyQ", verb.Context);
    }

    // The CONTAINER a podcast episode belongs to is still a page, not a play.
    [Fact]
    public void ShowUri_BecomesOpen_OnTheShowRoute()
    {
        Assert.True(DeepLink.TryParse("spotify:show:5as3aKmN2k11yfDDDSrvaZ", out var verb));
        Assert.Equal(DeepLinkKind.Open, verb.Kind);
        Assert.Equal("show", verb.Route);
        Assert.Equal("spotify:show:5as3aKmN2k11yfDDDSrvaZ", verb.Arg);
    }

    [Theory]
    [InlineData("spotify:user:someone")]                       // no page route
    [InlineData("spotify:user:someone:playlist:37i9dQ")]       // the nested form is refused, never guessed at
    [InlineData("https://open.spotify.com/episode/512ojh")]    // a web link belongs to the browser
    public void UnroutableForms_AreRefused(string raw)
        => Assert.False(DeepLink.TryParse(raw, out _));

    // The wavee:// verbs are untouched by the widening — the play verb's context is opaque to the parser.
    [Fact]
    public void WaveePlayVerb_CarriesItsContextVerbatim()
    {
        Assert.True(DeepLink.TryParse("wavee://play?ctx=spotify%3Aepisode%3Ae1", out var verb));
        Assert.Equal(DeepLinkKind.Play, verb.Kind);
        Assert.Equal("spotify:episode:e1", verb.Context);
    }

    /// <summary>`play?link=` is the module intake (YouTube / Twitch / radio) — it never touches Context, and a bare
    /// scheme or a non-url is refused like any other malformed verb.</summary>
    [Fact]
    public void PlayLink_IsTheModulePath()
    {
        Assert.True(DeepLink.TryParse("wavee://play?link=https%3A%2F%2Fwww.youtube.com%2Fwatch%3Fv%3DtRsQsTMvPNg", out var verb));
        Assert.Equal(DeepLinkKind.Play, verb.Kind);
        Assert.Equal("https://www.youtube.com/watch?v=tRsQsTMvPNg", verb.Link);
        Assert.Equal("", verb.Context);

        Assert.False(DeepLink.TryParse("wavee://play?link=https%3A%2F%2F", out _));
        Assert.False(DeepLink.TryParse("wavee://play?link=not%20a%20link", out _));
        Assert.False(DeepLink.TryParse("wavee://play", out _));
    }
}
