using Wavee.Backend;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// THE ACCOUNT MENU SHOWING A RAW SPOTIFY ID. <c>LiveSessionHost</c> constructs the live session from the bare account
/// id and fetches the real profile (display name + avatar) in the BACKGROUND, deliberately off the go-live path — so
/// that fetch routinely resolves BEFORE <c>Services.GoLive</c> swaps the session in. The publish used to write only
/// <c>PlaybackBridge.User</c>, behind a guard that the session had already gone live; pre-swap the guard dropped the
/// name outright, and post-swap the swap's own <c>CurrentUser</c> re-publish would have overwritten it with the id.
/// Either way the chip settled on <c>31unjfmo…</c>. The enrichment therefore belongs on the SESSION, which is what the
/// swap publishes.
/// </summary>
public class LiveSessionProfileTests
{
    const string Account = "31unjfmo3oefvlz36ef3eb6kj5tq";

    [Fact]
    public void ABareSessionDisplaysTheAccountId_UntilTheProfileLands()
    {
        var s = new LiveSpotifySession(Account, isPremium: true);
        Assert.Equal(Account, s.CurrentUser!.DisplayName);

        s.UpdateProfile("Christos", "https://i.scdn.co/image/abc");

        Assert.Equal(Account, s.CurrentUser!.Id);       // identity never moves
        Assert.Equal("Christos", s.CurrentUser.DisplayName);
        Assert.Equal("https://i.scdn.co/image/abc", s.CurrentUser.AvatarUrl);
        Assert.True(s.CurrentUser.IsPremium);
    }

    [Fact]
    public void TheEnrichedProfileSurvivesTheSwitchableSessionSwap()
    {
        var live = new LiveSpotifySession(Account, isPremium: true);
        live.UpdateProfile("Christos", null);           // the fetch beat go-live, as it usually does

        var sw = new SwitchableSession(new FakeSpotifySession());
        sw.SetInner(live);                              // …what Services.GoLive does

        Assert.Equal("Christos", sw.CurrentUser!.DisplayName);
    }

    [Fact]
    public void ABlankAnswerNeverDowngradesAResolvedProfile()
    {
        var s = new LiveSpotifySession(Account, isPremium: true);
        s.UpdateProfile("Christos", "https://i.scdn.co/image/abc");

        s.UpdateProfile("", null);
        s.UpdateProfile("   ", "");

        Assert.Equal("Christos", s.CurrentUser!.DisplayName);
        Assert.Equal("https://i.scdn.co/image/abc", s.CurrentUser.AvatarUrl);
    }
}
