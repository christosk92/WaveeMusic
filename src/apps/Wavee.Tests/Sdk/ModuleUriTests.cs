using Wavee.Sdk;
using Xunit;

namespace Wavee.Tests.Sdk;

public class ModuleUriTests
{
    [Fact]
    public void Prefix_IsSchemePlusIdPlusColon()
    {
        Assert.Equal("wavee:module:", ModuleUri.Scheme);
        Assert.Equal("wavee:module:wavee.youtube:", ModuleUri.Prefix("wavee.youtube"));
    }

    [Theory]
    [InlineData("wavee.youtube", "tRsQsTMvPNg")]
    [InlineData("wavee.radio", "https://stream.example.com/live?x=1&y=2")]
    [InlineData("wavee.spotify", "spotify:track:4cOdK2wGLETKBW3PvgPWqT")]
    [InlineData("wavee.twitch", "channel/with spaces/and:colons")]
    [InlineData("wavee.demo", "unicode ✓ ünïcödé")]
    public void Encode_RoundTripsThroughTryDecode(string moduleId, string playableId)
    {
        string uri = ModuleUri.Encode(moduleId, playableId);

        Assert.StartsWith(ModuleUri.Prefix(moduleId), uri, System.StringComparison.Ordinal);
        Assert.True(ModuleUri.TryDecode(uri, out string decodedModule, out string decodedPlayable));
        Assert.Equal(moduleId, decodedModule);
        Assert.Equal(playableId, decodedPlayable);
    }

    [Fact]
    public void Payload_IsColonFreeSoTheUriSplitsUnambiguously()
    {
        string uri = ModuleUri.Encode("wavee.radio", "https://a:b/c:d");
        string payload = uri[ModuleUri.Prefix("wavee.radio").Length..];

        Assert.DoesNotContain(':', payload);
        Assert.DoesNotContain('=', payload);   // padding stripped
        Assert.DoesNotContain('+', payload);
        Assert.DoesNotContain('/', payload);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("spotify:track:x")]
    [InlineData("wavee:module:")]
    [InlineData("wavee:module:wavee.youtube")]
    [InlineData("wavee:module:wavee.youtube:")]
    [InlineData("wavee:module::abcd")]
    [InlineData("wavee:module:wavee.youtube:not base64!")]
    public void TryDecode_RejectsAnythingElse(string? uri)
    {
        Assert.False(ModuleUri.TryDecode(uri, out string moduleId, out string playableId));
        Assert.Equal(string.Empty, moduleId);
        Assert.Equal(string.Empty, playableId);
    }
}
