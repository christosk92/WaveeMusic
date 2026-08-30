using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using Wavee.Backend.Realtime;
using Xunit;

namespace Wavee.Tests;

// The dealer WebSocket JSON frame parser (type / uri / base64 payload, with gzip). The live socket is unverifiable here;
// the frame decode is not.
public class DealerFrameParserTests
{
    static byte[] J(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void Parses_Ping() => Assert.Equal(DealerFrameType.Ping, DealerFrameParser.Parse(J("{\"type\":\"ping\"}")).Type);

    [Fact]
    public void Parses_Message_WithBase64Payload()
    {
        var payload = new byte[] { 1, 2, 3, 4 };
        string b64 = Convert.ToBase64String(payload);
        var f = DealerFrameParser.Parse(J("{\"type\":\"message\",\"uri\":\"hm://playlist/v2/playlist/p\",\"payloads\":[\"" + b64 + "\"]}"));
        Assert.Equal(DealerFrameType.Message, f.Type);
        Assert.Equal("hm://playlist/v2/playlist/p", f.Uri);
        Assert.Equal(payload, f.Payload);
    }

    [Fact]
    public void Parses_Message_NoPayload()
    {
        var f = DealerFrameParser.Parse(J("{\"type\":\"message\",\"uri\":\"hm://presence2/user/x\"}"));
        Assert.Equal(DealerFrameType.Message, f.Type);
        Assert.Equal("hm://presence2/user/x", f.Uri);
        Assert.Empty(f.Payload);
    }

    [Fact]
    public void Parses_GzipMessage_Decompresses()
    {
        var raw = new byte[] { 9, 8, 7, 6, 5 };
        byte[] gz;
        using (var ms = new MemoryStream())
        {
            using (var g = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true)) g.Write(raw, 0, raw.Length);
            gz = ms.ToArray();
        }
        string b64 = Convert.ToBase64String(gz);
        var f = DealerFrameParser.Parse(J("{\"type\":\"message\",\"uri\":\"hm://collection/x\",\"headers\":{\"Transfer-Encoding\":\"gzip\"},\"payloads\":[\"" + b64 + "\"]}"));
        Assert.Equal(DealerFrameType.Message, f.Type);
        Assert.Equal(raw, f.Payload);   // gunzipped back to the original bytes
    }

    [Fact]
    public void Malformed_IsUnknown() => Assert.Equal(DealerFrameType.Unknown, DealerFrameParser.Parse(J("{not json")).Type);
}
