// The audio path across the process boundary: a scripted module serves bytes over stream/open|read|close through a
// REAL JsonRpcConnection pair, and `Modules.ModuleByteStream` reads them back — sequentially, after a seek, and through
// deliberately short reads (ported from 0.2.9 Wavee.Tests/Modules/ModuleByteStreamTests.cs).

using Wavee.Sdk;
using Xunit;
using static Wavee.Modules;

namespace Wavee.Tests;

public class ModuleByteStreamTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static byte[] Payload(int length)
    {
        var bytes = new byte[length];
        for (int i = 0; i < length; i++) bytes[i] = (byte)(i * 31 % 251);
        return bytes;
    }

    static (ModuleHost Host, FakeModule Script) HostServing(byte[] bytes, int maxRead = 0, bool seekable = true, bool reportLength = true,
        string? contentType = null)
    {
        var script = new FakeModule { MaxReadBytes = maxRead, Seekable = seekable, ReportLength = reportLength, ContentType = contentType };
        script.Streams["s1"] = bytes;
        (ModuleHost host, _) = ModuleFixtures.HostOver(script);
        return (host, script);
    }

    static byte[] ReadAll(Stream stream)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            int n = stream.Read(chunk, 0, chunk.Length);
            if (n <= 0) break;
            buffer.Write(chunk, 0, n);
        }
        return buffer.ToArray();
    }

    [Fact]
    public async Task ReadsTheWholeStream_ThroughRealBinaryFrames()
    {
        byte[] payload = Payload(300_000);
        (ModuleHost host, _) = HostServing(payload);
        using (host)
        {
            using ModuleByteStream stream = await ModuleByteStream.OpenAsync(host.Process(host.Installed[0]), "s1", Ct);
            Assert.Equal(payload.Length, stream.KnownSize);
            Assert.Equal(payload.Length, stream.Length);
            Assert.True(stream.CanSeek);
            Assert.Equal(payload, ReadAll(stream));
        }
    }

    [Fact]
    public async Task ShortReads_AreNormal_AndTheFillLoopKeepsAsking()
    {
        byte[] payload = Payload(70_000);
        (ModuleHost host, _) = HostServing(payload, maxRead: 997);   // nothing like the 64 KiB the host asked for
        using (host)
        {
            using ModuleByteStream stream = await ModuleByteStream.OpenAsync(host.Process(host.Installed[0]), "s1", Ct);
            Assert.Equal(payload, ReadAll(stream));
        }
    }

    [Fact]
    public async Task Seek_ReadsFromTheNewOffset()
    {
        byte[] payload = Payload(200_000);
        (ModuleHost host, _) = HostServing(payload);
        using (host)
        {
            using ModuleByteStream stream = await ModuleByteStream.OpenAsync(host.Process(host.Installed[0]), "s1", Ct);
            stream.ReadExactly(new byte[16]);

            stream.Seek(123_456, SeekOrigin.Begin);
            Assert.Equal(123_456, stream.Position);
            var mid = new byte[32];
            stream.ReadExactly(mid);
            Assert.Equal(payload.AsSpan(123_456, 32).ToArray(), mid);

            stream.Seek(-32, SeekOrigin.End);
            var tail = new byte[32];
            stream.ReadExactly(tail);
            Assert.Equal(payload.AsSpan(payload.Length - 32, 32).ToArray(), tail);
        }
    }

    [Fact]
    public async Task ForwardOnlyStream_RefusesASeek()
    {
        (ModuleHost host, _) = HostServing(Payload(4096), seekable: false);
        using (host)
        {
            using ModuleByteStream stream = await ModuleByteStream.OpenAsync(host.Process(host.Installed[0]), "s1", Ct);
            Assert.False(stream.CanSeek);
            Assert.Throws<NotSupportedException>(() => stream.Seek(10, SeekOrigin.Begin));
        }
    }

    [Fact]
    public async Task LengthIsLearnedFromEof_WhenTheModuleDidNotDeclareOne()
    {
        byte[] payload = Payload(5_000);
        (ModuleHost host, _) = HostServing(payload, reportLength: false);
        using (host)
        {
            using ModuleByteStream stream = await ModuleByteStream.OpenAsync(host.Process(host.Installed[0]), "s1", Ct);
            Assert.Equal(0, stream.KnownSize);
            Assert.Equal(payload, ReadAll(stream));
            Assert.Equal(payload.Length, stream.KnownSize);
        }
    }

    [Fact]
    public async Task ContentType_IsCarriedFromTheOpenAnswer()
    {
        (ModuleHost host, _) = HostServing(Payload(64), contentType: "audio/ogg");
        using (host)
        {
            using ModuleByteStream stream = await ModuleByteStream.OpenAsync(host.Process(host.Installed[0]), "s1", Ct);
            Assert.Equal("audio/ogg", stream.ContentType);
        }
    }

    [Fact]
    public async Task Open_OfAnUnknownStreamId_IsATypedFailure()
    {
        (ModuleHost host, _) = HostServing(Payload(64));
        using (host)
        {
            var ex = await Assert.ThrowsAsync<ModuleException>(() => ModuleByteStream.OpenAsync(host.Process(host.Installed[0]), "nope", Ct));
            Assert.Equal(ModuleErrorCode.Unavailable, ex.Code);
        }
    }

    [Fact]
    public async Task AnOpenHandle_KeepsTheProcessOutOfTheIdleStop()
    {
        (ModuleHost host, _) = HostServing(Payload(64));
        using (host)
        {
            ModuleProcess process = host.Process(host.Installed[0]);
            ModuleByteStream stream = await ModuleByteStream.OpenAsync(process, "s1", Ct);
            Assert.True(process.HasOpenStreams);

            stream.Dispose();
            Assert.False(process.HasOpenStreams);
        }
    }

    [Fact]
    public async Task TheDemoModulesSilentMp3_RoundTripsOverTheInProcessWire()
    {
        // The `--fake` module's body, read back through the same host path the pump uses.
        using var host = new ModuleHost(ModuleCatalog.From([DemoModule.Installed]), spawn: DemoModule.SpawnAsync, startIdleTimer: false);
        using ModuleByteStream stream = await ModuleByteStream.OpenAsync(host.Process(host.Installed[0]), "silence:2", Ct);

        byte[] all = ReadAll(stream);
        Assert.Equal(new SilentMp3(2).Length, all.Length);
        Assert.Equal(0, all.Length % SilentMp3.FrameBytes);
        for (int f = 0; f < all.Length; f += SilentMp3.FrameBytes)
            Assert.Equal(new byte[] { 0xFF, 0xFB, 0x90, 0xC0 }, all.AsSpan(f, 4).ToArray());
        Assert.Equal("audio/mpeg", stream.ContentType);
    }
}
