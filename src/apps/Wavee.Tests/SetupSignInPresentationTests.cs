using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// SetupSignInPresentation: the pure facts the setup wizard's sign-in page reads off SetupSignInPhase instead of
// re-deriving the split inline. Pinned per phase so a future edit to the page can't silently change which phase
// shows the two Idle option cards without a red test.
public class SetupSignInPresentationTests
{
    [Theory]
    [InlineData(SetupSignInPhase.Idle, true)]
    [InlineData(SetupSignInPhase.Busy, false)]
    [InlineData(SetupSignInPhase.Done, false)]
    [InlineData(SetupSignInPhase.Failed, true)]     // retries in place, under the error InfoBar
    [InlineData(SetupSignInPhase.Expired, true)]
    [InlineData(SetupSignInPhase.Premium, true)]
    public void ShowsIdleCards_IdleAndTheThreeRetryFacets(SetupSignInPhase phase, bool expected) =>
        Assert.Equal(expected, SetupSignInPresentation.ShowsIdleCards(phase));

    [Fact]
    public void EveryPhase_IsCoveredByShowsIdleCards()
    {
        // Exhaustiveness: no SetupSignInPhase value falls through to a caller-supplied fallback.
        foreach (SetupSignInPhase phase in System.Enum.GetValues<SetupSignInPhase>())
            _ = SetupSignInPresentation.ShowsIdleCards(phase);
    }

    // ── DisplayNameFor (§ the "raw Spotify id" bug — the wizard used to read the frozen LoginSnapshot.User, whose
    // DisplayName defaults to the account id until the real profile lands, instead of the live PlaybackBridge.User) ──

    static WaveeUser User(string id, string name) => new(id, name, null, false);

    [Fact]
    public void DisplayNameFor_LiveNameWins_EvenWithAGoodSnapshot()
    {
        var live = User("31unjfmo", "Christos");
        var snapshot = User("31unjfmo", "Snapshot Name");
        Assert.Equal("Christos", SetupSignInPresentation.DisplayNameFor(live, snapshot));
    }

    [Fact]
    public void DisplayNameFor_IdAsLiveName_FallsBackToASnapshotName()
    {
        var live = User("31unjfmo", "31unjfmo");       // live profile hasn't resolved a real name yet
        var snapshot = User("31unjfmo", "Christos");    // the frozen auth-complete snapshot already has one
        Assert.Equal("Christos", SetupSignInPresentation.DisplayNameFor(live, snapshot));
    }

    [Fact]
    public void DisplayNameFor_NoLiveUser_FallsBackToASnapshotName()
    {
        var snapshot = User("31unjfmo", "Christos");
        Assert.Equal("Christos", SetupSignInPresentation.DisplayNameFor(null, snapshot));
    }

    [Fact]
    public void DisplayNameFor_BothIdAsName_IsNull()
    {
        var live = User("31unjfmo", "31unjfmo");
        var snapshot = User("31unjfmo", "31unjfmo");
        Assert.Null(SetupSignInPresentation.DisplayNameFor(live, snapshot));
    }

    [Fact]
    public void DisplayNameFor_BothNull_IsNull()
        => Assert.Null(SetupSignInPresentation.DisplayNameFor(null, null));
}
