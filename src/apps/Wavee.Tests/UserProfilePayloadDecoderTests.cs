using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using Wavee.Backend.Hydration;
using Xunit;

namespace Wavee.Tests;

// Kind 15 (USER_PROFILE) shipped protobuf where the fetch assumed JSON, and every owner on every playlist page went to
// the one-request-per-user REST arm instead. The decoder is pure, so the wire shapes are pinned here from bytes
// captured out of the cold extension cache (2026-08-30) rather than inferred from a log line.
public class UserProfilePayloadDecoderTests
{
    // spotify:user:spotify — 220 bytes, verbatim from localized_extension_cache (extension_kind = 15).
    const string SpotifyAccountHex =
        "0A090A0773706F7469667912090A0753706F746966791A46084010401A4068747470733A2F2F692E7363646E2E636F2F696D6167652F" +
        "616236373735373030303030336238323535633235393838613661633331343339346433666266351A4808AC0210AC021A406874747073" +
        "3A2F2F692E7363646E2E636F2F696D6167652F61623637373537303030303065653835353563323539383861366163333134333934643366" +
        "626635220208012A00320208013A0042004A020801520208015A0508A0E7D50762009201009A0100C2010C0A0A7938346B584E794F7162";

    // spotify:user:f13l66oxfkogx8haz4a6wl6xr — the production log's "0x1B at byte 0": 0A 1B is field 1 with a 27-byte
    // wrapper, and the display name carries a 4-byte emoji.
    const string CatherineHex =
        "0A1B0A196631336C36366F78666B6F67783868617A346136776C36787212100A0E23436174686572696E65F09F8C9E1A46084010401A40" +
        "68747470733A2F2F692E7363646E2E636F2F696D6167652F616236373735373030303030336238323235383432383064383863666461626462" +
        "3834316230373 71A4808AC0210AC021A4068747470733A2F2F692E7363646E2E636F2F696D6167652F6162363737353730303030306565" +
        "38353235383432383064383863666461626462383431623037372200 2A0032003A0042004A020801520208015A0508F5B7C2026200920100" +
        "9A0100C2010C0A0A4A72733850374776374D";

    static byte[] Hex(string hex) => Convert.FromHexString(hex.Replace(" ", ""));
    static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void CapturedProto_YieldsUsernameNameAndTheLargestImage()
    {
        var p = UserProfilePayloadDecoder.Decode(Hex(SpotifyAccountHex));

        Assert.NotNull(p);
        Assert.Equal("spotify", p.Username);
        Assert.Equal("Spotify", p.Name);
        // Two images ride along (64×64 first); the owner row wants the 300×300 one.
        Assert.Equal("https://i.scdn.co/image/ab6775700000ee8555c25988a6ac314394d3fbf5", p.ImageUrl);
        Assert.True(p.IsRenderable);
    }

    [Fact]
    public void CapturedProto_TheProductionFailure_DecodesWithItsEmoji()
    {
        var p = UserProfilePayloadDecoder.Decode(Hex(CatherineHex));

        Assert.NotNull(p);
        Assert.Equal("f13l66oxfkogx8haz4a6wl6xr", p.Username);
        Assert.Equal("#Catherine\U0001F31E", p.Name);
        Assert.Equal("https://i.scdn.co/image/ab6775700000ee852584280d88cfdabdb841b077", p.ImageUrl);
    }

    [Fact]
    public void PlainJson_SpclientShape()
    {
        var p = UserProfilePayloadDecoder.Decode(Utf8("""{"uri":"spotify:user:alice","name":"Alice","image_url":"https://i/a"}"""));

        Assert.NotNull(p);
        Assert.Equal("Alice", p.Name);
        Assert.Equal("https://i/a", p.ImageUrl);
        Assert.Null(p.Username);
    }

    [Fact]
    public void PlainJson_WebApiShape_WithLeadingWhitespace()
    {
        // "\n{" must be read as JSON even though 0x0A is also the protobuf tag of field 1.
        var p = UserProfilePayloadDecoder.Decode(Utf8("\n  {\"display_name\":\"Bob\",\"images\":[{\"url\":\"https://i/b\"}]}"));

        Assert.NotNull(p);
        Assert.Equal("Bob", p.Name);
        Assert.Equal("https://i/b", p.ImageUrl);
    }

    [Fact]
    public void PlainJson_WithNothingRenderable_IsDecodedButNotRenderable()
    {
        var p = UserProfilePayloadDecoder.Decode(Utf8("""{"uri":"spotify:user:ghost"}"""));

        Assert.NotNull(p);
        Assert.False(p.IsRenderable);
    }

    [Fact]
    public void ZstdWrappedJson_IsUnwrappedFirst()
    {
        using var comp = new ZstdSharp.Compressor();
        var zstd = comp.Wrap(Utf8("""{"name":"Zed","image_url":"https://i/z"}""")).ToArray();
        Assert.Equal(new byte[] { 0x28, 0xB5, 0x2F, 0xFD }, zstd[..4]);   // the frame magic the sniff keys on

        var p = UserProfilePayloadDecoder.Decode(zstd);

        Assert.NotNull(p);
        Assert.Equal("Zed", p.Name);
        Assert.Equal("https://i/z", p.ImageUrl);
    }

    [Fact]
    public void ZstdWrappedProto_IsUnwrappedThenWalked()
    {
        using var comp = new ZstdSharp.Compressor();
        var p = UserProfilePayloadDecoder.Decode(comp.Wrap(Hex(SpotifyAccountHex)).ToArray());

        Assert.NotNull(p);
        Assert.Equal("Spotify", p.Name);
    }

    [Fact]
    public void GzipWrappedJson_IsUnwrappedFirst()
    {
        var gz = Gzip(Utf8("""{"name":"Gee","image_url":"https://i/g"}"""));
        Assert.Equal(new byte[] { 0x1F, 0x8B }, gz[..2]);

        var p = UserProfilePayloadDecoder.Decode(gz);

        Assert.NotNull(p);
        Assert.Equal("Gee", p.Name);
        Assert.Equal("https://i/g", p.ImageUrl);
    }

    [Fact]
    public void EmptyBody_IsNull()
        => Assert.Null(UserProfilePayloadDecoder.Decode(ReadOnlySpan<byte>.Empty));

    [Fact]
    public void UnknownHead_ThrowsWithTheHexInTheMessage()
    {
        // 0x1B alone is field 3 / wire type 3 (start-group): not JSON, not a length-delimited tag, not a frame magic.
        var ex = Assert.Throws<FormatException>(() => UserProfilePayloadDecoder.Decode(new byte[] { 0x1B, 0x00, 0xFF, 0x10, 0x42 }));

        Assert.Contains("1B00FF1042", ex.Message);
        Assert.Contains("5 bytes", ex.Message);
    }

    [Fact]
    public void ProtoWithNoneOfTheKnownFields_IsUndecodableNotAnonymous()
    {
        // Field 4 { 1: true } only — a valid message, but nothing that identifies a profile. Bytes that merely start
        // with a plausible tag must surface in the log, not vanish as a quiet null.
        var ex = Assert.Throws<FormatException>(() => UserProfilePayloadDecoder.Decode(new byte[] { 0x22, 0x02, 0x08, 0x01 }));

        Assert.Contains("22020801", ex.Message);
    }

    [Fact]
    public void TruncatedProto_IsUndecodable()
    {
        // The wrapper claims 9 bytes and the body has 3: the walk must fail loudly, not return a half-read name.
        var ex = Assert.Throws<FormatException>(() => UserProfilePayloadDecoder.Decode(new byte[] { 0x0A, 0x09, 0x0A, 0x07, 0x73 }));

        Assert.Contains("0A090A0773", ex.Message);
    }

    [Fact]
    public void CorruptZstdFrame_IsUndecodableWithTheHead()
    {
        var ex = Assert.Throws<FormatException>(() => UserProfilePayloadDecoder.Decode(new byte[] { 0x28, 0xB5, 0x2F, 0xFD, 0x00, 0x01, 0x02 }));

        Assert.Contains("28B52FFD", ex.Message);
    }

    [Fact]
    public void HeadHex_IsTheFirstEightBytesUpperCase()
    {
        Assert.Equal("0A090A0773706F74", UserProfilePayloadDecoder.HeadHex(Hex(SpotifyAccountHex)));
        Assert.Equal("1B", UserProfilePayloadDecoder.HeadHex(new byte[] { 0x1B }));
    }

    static byte[] Gzip(byte[] plain)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true)) gz.Write(plain);
        return ms.ToArray();
    }
}
