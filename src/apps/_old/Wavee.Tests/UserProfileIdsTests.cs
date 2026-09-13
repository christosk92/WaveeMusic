using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

public class UserProfileIdsTests
{
    [Theory]
    [InlineData("alice", "spotify:user:alice")]
    [InlineData(" spotify:user:alice ", "spotify:user:alice")]
    [InlineData("Spotify", "spotify:user:spotify")]
    [InlineData("spotify:user:Spotify", "spotify:user:spotify")]
    public void Normalize_AcceptsBareIdsAndCanonicalUris(string input, string expected)
    {
        Assert.Equal(expected, UserProfileIds.Normalize(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("two words")]
    [InlineData("spotify:user:")]
    [InlineData("spotify:user:alice:extra")]
    [InlineData("spotify:playlist:x")]
    public void Normalize_RejectsBlankOrInvalidIds(string input)
    {
        Assert.Null(UserProfileIds.Normalize(input));
    }
}
