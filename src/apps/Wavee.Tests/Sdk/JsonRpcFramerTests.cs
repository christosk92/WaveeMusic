using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Sdk.Protocol;
using Xunit;

namespace Wavee.Tests.Sdk;

public class JsonRpcFramerTests
{
    [Fact]
    public async Task JsonFrame_RoundTrips()
    {
        var buffer = new MemoryStream();
        byte[] body = Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","method":"ping"}""");
        JsonRpcFramer.WriteJson(buffer, body);

        Assert.StartsWith("Content-Length: 33\r\n\r\n", Encoding.ASCII.GetString(buffer.ToArray()),
            StringComparison.Ordinal);

        buffer.Position = 0;
        var framer = new JsonRpcFramer(buffer);
        JsonRpcFrame? frame = await framer.ReadAsync(CancellationToken.None);

        Assert.NotNull(frame);
        Assert.False(frame!.IsBinary);
        Assert.Equal(body, frame.Payload);
        Assert.Null(await framer.ReadAsync(CancellationToken.None));   // clean EOF
    }

    [Fact]
    public async Task Reader_ReassemblesAFrameSplitAcrossManyReads()
    {
        byte[] body = Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":7,"method":"playback/match"}""");
        byte[] wire = Wire(body);

        // one byte per ReadAsync: the header, the separator and the body all arrive in pieces
        var chunks = new List<byte[]>();
        foreach (byte b in wire) chunks.Add([b]);

        var stream = new ScriptedReadStream(chunks);
        var framer = new JsonRpcFramer(stream);
        JsonRpcFrame? frame = await framer.ReadAsync(CancellationToken.None);

        Assert.NotNull(frame);
        Assert.Equal(body, frame!.Payload);
        Assert.Equal(wire.Length, stream.ReadCalls);
    }

    [Fact]
    public async Task Reader_YieldsTwoFramesThatArrivedInOneRead()
    {
        byte[] first = Encoding.UTF8.GetBytes("""{"id":1}""");
        byte[] second = Encoding.UTF8.GetBytes("""{"id":2}""");

        var both = new MemoryStream();
        both.Write(Wire(first));
        both.Write(Wire(second));

        var stream = new ScriptedReadStream([both.ToArray()]);
        var framer = new JsonRpcFramer(stream);

        JsonRpcFrame? a = await framer.ReadAsync(CancellationToken.None);
        JsonRpcFrame? b = await framer.ReadAsync(CancellationToken.None);

        Assert.Equal(first, a!.Payload);
        Assert.Equal(second, b!.Payload);
        Assert.Equal(1, stream.ReadCalls);   // both frames came out of a single read
    }

    [Fact]
    public async Task BinaryFrame_CarriesRequestIdAndEof_AndIsNotParsedAsJson()
    {
        var buffer = new MemoryStream();
        byte[] payload = [0xFF, 0x00, 0x7B, 0x0D, 0x0A];   // deliberately not valid JSON
        JsonRpcFramer.WriteBinary(buffer, 42, eof: true, payload);

        string header = Encoding.ASCII.GetString(buffer.ToArray());
        Assert.Contains("Content-Type: application/octet-stream", header, StringComparison.Ordinal);
        Assert.Contains("X-Wavee-Request: 42", header, StringComparison.Ordinal);
        Assert.Contains("X-Wavee-Eof: 1", header, StringComparison.Ordinal);

        buffer.Position = 0;
        JsonRpcFrame? frame = await new JsonRpcFramer(buffer).ReadAsync(CancellationToken.None);

        Assert.NotNull(frame);
        Assert.True(frame!.IsBinary);
        Assert.True(frame.Eof);
        Assert.Equal(42L, frame.RequestId);
        Assert.Equal(payload, frame.Payload);
    }

    [Fact]
    public async Task BinaryFrame_WithoutEof_ReportsEofFalse()
    {
        var buffer = new MemoryStream();
        JsonRpcFramer.WriteBinary(buffer, -3, eof: false, [1, 2, 3]);
        buffer.Position = 0;

        JsonRpcFrame? frame = await new JsonRpcFramer(buffer).ReadAsync(CancellationToken.None);

        Assert.False(frame!.Eof);
        Assert.Equal(-3L, frame.RequestId);
    }

    [Fact]
    public async Task Reader_ThrowsWhenTheStreamEndsMidFrame()
    {
        byte[] wire = Wire(Encoding.UTF8.GetBytes("""{"id":1}"""));
        var truncated = new ScriptedReadStream([wire[..(wire.Length - 2)]]);

        await Assert.ThrowsAsync<EndOfStreamException>(
            async () => await new JsonRpcFramer(truncated).ReadAsync(CancellationToken.None));
    }

    [Fact]
    public void TryParse_NeedsMoreBytesUntilTheWholeBodyArrived()
    {
        byte[] wire = Wire(Encoding.UTF8.GetBytes("""{"id":1}"""));

        Assert.False(JsonRpcFramer.TryParse(wire.AsSpan(0, wire.Length - 1), out JsonRpcFrame? partial, out int none));
        Assert.Null(partial);
        Assert.Equal(0, none);

        Assert.True(JsonRpcFramer.TryParse(wire, out JsonRpcFrame? full, out int consumed));
        Assert.NotNull(full);
        Assert.Equal(wire.Length, consumed);
    }

    [Fact]
    public void TryParse_RejectsAHeaderBlockWithoutContentLength()
    {
        byte[] wire = Encoding.ASCII.GetBytes("X-Wavee-Eof: 1\r\n\r\n");

        Assert.Throws<InvalidDataException>(() => { JsonRpcFramer.TryParse(wire, out _, out _); });
    }

    private static byte[] Wire(byte[] body)
    {
        var ms = new MemoryStream();
        JsonRpcFramer.WriteJson(ms, body);
        return ms.ToArray();
    }
}
