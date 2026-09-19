using Wavee;
using Xunit;

namespace Wavee.Tests;

public class SpotifyAccountRequestTests
{
    [Theory]
    [InlineData("alice", "alice", 7u, 7u, true, true)]
    [InlineData("alice", "bob", 7u, 7u, true, false)]
    [InlineData("alice", "", 7u, 7u, true, false)]
    [InlineData("alice", "alice", 7u, 8u, true, false)]
    [InlineData("alice", "alice", 7u, 7u, false, false)]
    [InlineData("alice", "Alice", 7u, 7u, true, false)]
    public void Queued_requests_require_the_captured_account_session_and_scope(
        string expectedAccount, string currentAccount, uint expectedEpoch, uint currentEpoch,
        bool sameScope, bool allowed)
        => Assert.Equal(allowed, Spotify.Api.AccountRequestAllowed(expectedAccount, currentAccount,
            expectedEpoch, currentEpoch, sameScope));
}
