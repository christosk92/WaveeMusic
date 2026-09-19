using System.Text;
using Wavee.Backend.Audio;
using Xunit;

namespace Wavee.Tests.Audio;

/// <summary>The head parser is hand-rolled for one reason: SHOUTcast answers <c>ICY 200 OK</c>, which the BCL's HTTP
/// parser rejects outright. These tests pin that plus the redirect and casing behaviour every Icecast fork varies on.</summary>
public class IcyHttpConnectorTests
{
    static byte[] Wire(string text) => Encoding.Latin1.GetBytes(text.Replace("\n", "\r\n"));

    [Fact]
    public void ParsesShoutcastIcyStatusLine()
    {
        var wire = Wire("ICY 200 OK\nicy-name:Radio Paradise\nicy-genre:Eclectic\nicy-br:128\nicy-metaint:16000\ncontent-type:audio/mpeg\n\nAUDIO");

        Assert.True(IcyHttpConnector.TryParseHead(wire, out int status, out var headers, out int consumed));

        Assert.Equal(200, status);
        Assert.Equal("Radio Paradise", headers["icy-name"]);
        Assert.Equal("Eclectic", headers["icy-genre"]);
        Assert.Equal("128", headers["icy-br"]);
        Assert.Equal("16000", headers["icy-metaint"]);
        Assert.Equal("audio/mpeg", headers["content-type"]);
        Assert.Equal("AUDIO", Encoding.Latin1.GetString(wire, consumed, wire.Length - consumed));
    }

    [Fact]
    public void ParsesHttp11StatusLine_AndLowerCasesHeaderNames()
    {
        var wire = Wire("HTTP/1.1 200 OK\nContent-Type: audio/aacp\nICY-Name: Some Station\n\n");

        Assert.True(IcyHttpConnector.TryParseHead(wire, out int status, out var headers, out _));

        Assert.Equal(200, status);
        Assert.Equal("audio/aacp", headers["content-type"]);
        Assert.Equal("Some Station", headers["icy-name"]);
    }

    [Fact]
    public void ParsesRedirectWithLocation()
    {
        var wire = Wire("HTTP/1.0 302 Found\nLocation: http://cdn.example/stream.mp3\n\n");

        Assert.True(IcyHttpConnector.TryParseHead(wire, out int status, out var headers, out _));

        Assert.Equal(302, status);
        Assert.Equal("http://cdn.example/stream.mp3", headers["location"]);
    }

    [Fact]
    public void IncompleteHead_ReturnsFalse()
    {
        Assert.False(IcyHttpConnector.TryParseHead(Wire("ICY 200 OK\nicy-name:Half"), out _, out _, out _));
        Assert.False(IcyHttpConnector.TryParseHead([], out _, out _, out _));
    }

    [Fact]
    public void UnknownProtocol_IsRejected()
    {
        Assert.False(IcyHttpConnector.TryParseHead(Wire("RTSP/1.0 200 OK\n\n"), out _, out _, out _));
    }

    [Fact]
    public void ToleratesBareLineFeedSeparators()
    {
        var wire = Encoding.Latin1.GetBytes("ICY 200 OK\nicy-br:64\n\nBODY");

        Assert.True(IcyHttpConnector.TryParseHead(wire, out int status, out var headers, out int consumed));

        Assert.Equal(200, status);
        Assert.Equal("64", headers["icy-br"]);
        Assert.Equal("BODY", Encoding.Latin1.GetString(wire, consumed, wire.Length - consumed));
    }

    [Fact]
    public void HeaderValuesKeepInternalColons()
    {
        var wire = Wire("ICY 200 OK\nicy-url:http://example.com:8000/live\n\n");

        Assert.True(IcyHttpConnector.TryParseHead(wire, out _, out var headers, out _));

        Assert.Equal("http://example.com:8000/live", headers["icy-url"]);
    }
}
