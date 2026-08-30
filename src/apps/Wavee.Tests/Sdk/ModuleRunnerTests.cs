using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Wavee.Sdk;
using Wavee.Sdk.Protocol;
using Xunit;

namespace Wavee.Tests.Sdk;

/// <summary>
/// Drives a real <see cref="WaveeModule"/> through the real <see cref="ModuleRunner"/> loop over in-memory pipes —
/// the same code path the app's <c>ModuleProcess</c> speaks to, minus the process.
/// </summary>
public class ModuleRunnerTests
{
    private static readonly byte[] Payload = BuildPayload(700);

    [Fact]
    public async Task Initialize_NegotiatesTheProtocolAndAnswersCapabilities()
    {
        await using var rig = await ModuleRig.StartAsync(new FakeModule());

        InitializeResult result = await rig.InitializeAsync();

        Assert.Equal(ModuleProtocol.Version, result.ProtocolVersion);
        Assert.NotEmpty(result.Capabilities);
    }

    [Fact]
    public async Task Initialize_IsRejectedWhenNoVersionIsInTheHostsRange()
    {
        await using var rig = await ModuleRig.StartAsync(new FakeModule());

        ModuleException error = await Assert.ThrowsAsync<ModuleException>(
            async () => await rig.InitializeAsync(minProtocol: ModuleProtocol.Version + 5,
                maxProtocol: ModuleProtocol.Version + 9));

        Assert.Equal(ModuleErrorCode.Unsupported, error.Code);
    }

    [Fact]
    public async Task Match_And_Resolve_TravelOverTheWire()
    {
        var module = new FakeModule();
        await using var rig = await ModuleRig.StartAsync(module);
        await rig.InitializeAsync();

        MatchResult match = await rig.Host.RequestAsync(ModuleMethods.Match,
            new MatchParams("https://demo.example/live/42"), SdkJsonContext.Default.MatchParams,
            SdkJsonContext.Default.MatchResult, TestContext.Current.CancellationToken);

        Assert.Equal("42", match.PlayableId);
        Assert.Equal(MediaForm.Audio, match.Form);
        Assert.True(match.IsLive);

        ResolvedPlayable resolved = await rig.Host.RequestAsync(ModuleMethods.Resolve,
            new ResolveParams("42", new ResolvePreferences("lossless", false, 0)),
            SdkJsonContext.Default.ResolveParams, SdkJsonContext.Default.ResolvedPlayable,
            TestContext.Current.CancellationToken);

        Assert.Equal("42", resolved.PlayableId);
        Assert.Equal(MediaLocator.KindStream, resolved.Media.Kind);
        Assert.Equal("demo", resolved.Media.StreamId);
        Assert.Equal("lossless", module.LastPrefs?.Quality);
        Assert.Equal(-3.5f, resolved.GainDb);
        Assert.NotNull(resolved.Wire);
        Assert.Equal(320, resolved.Wire!.BitrateKbps);
        Assert.Equal(new byte[] { 1, 2, 3 }, resolved.Wire.MediaId);
    }

    [Fact]
    public async Task Match_ThatFindsNothing_AnswersNull()
    {
        await using var rig = await ModuleRig.StartAsync(new FakeModule());
        await rig.InitializeAsync();

        MatchResult? match = await rig.Host.RequestAsync(ModuleMethods.Match, new MatchParams("nope"),
            SdkJsonContext.Default.MatchParams, SdkJsonContext.Default.MatchResult,
            TestContext.Current.CancellationToken);

        Assert.Null(match);
    }

    [Fact]
    public async Task StreamOpenReadClose_ServesTheBytesAsBinaryFrames()
    {
        var module = new FakeModule();
        await using var rig = await ModuleRig.StartAsync(module);
        await rig.InitializeAsync();

        StreamOpenResult open = await rig.Host.RequestAsync(ModuleMethods.StreamOpen, new StreamOpenParams("demo"),
            SdkJsonContext.Default.StreamOpenParams, SdkJsonContext.Default.StreamOpenResult,
            TestContext.Current.CancellationToken);

        Assert.Equal(Payload.Length, (int)open.Length!.Value);
        Assert.True(open.Seekable);
        Assert.Equal("audio/ogg", open.ContentType);

        var received = new List<byte>();
        long offset = 0;
        bool eof = false;
        while (!eof && offset < Payload.Length)
        {
            BinaryPayload chunk = await rig.Host.RequestBinaryAsync(ModuleMethods.StreamRead,
                new StreamReadParams(open.Handle, offset, 256), SdkJsonContext.Default.StreamReadParams,
                TestContext.Current.CancellationToken);
            received.AddRange(chunk.Bytes.ToArray());
            offset += chunk.Bytes.Length;
            eof = chunk.Eof;
        }

        Assert.True(eof);
        Assert.Equal(Payload, received.ToArray());

        // a ranged read straight out of the middle, which is what a seek becomes
        BinaryPayload middle = await rig.Host.RequestBinaryAsync(ModuleMethods.StreamRead,
            new StreamReadParams(open.Handle, 100, 8), SdkJsonContext.Default.StreamReadParams,
            TestContext.Current.CancellationToken);
        Assert.Equal(Payload[100..108], middle.Bytes.ToArray());

        await rig.Host.RequestAsync(ModuleMethods.StreamClose, new StreamCloseParams(open.Handle),
            SdkJsonContext.Default.StreamCloseParams, SdkJsonContext.Default.RpcUnit,
            TestContext.Current.CancellationToken);

        Assert.True(module.LastStream!.Disposed);
    }

    [Fact]
    public async Task StreamOpen_ForAnUnknownId_IsATypedError()
    {
        await using var rig = await ModuleRig.StartAsync(new FakeModule());
        await rig.InitializeAsync();

        ModuleException error = await Assert.ThrowsAsync<ModuleException>(async () =>
            await rig.Host.RequestAsync(ModuleMethods.StreamOpen, new StreamOpenParams("missing"),
                SdkJsonContext.Default.StreamOpenParams, SdkJsonContext.Default.StreamOpenResult,
                TestContext.Current.CancellationToken));

        Assert.Equal(ModuleErrorCode.Unavailable, error.Code);
    }

    [Fact]
    public async Task ModuleNotifications_ReachTheHost()
    {
        var module = new FakeModule();
        await using var rig = await ModuleRig.StartAsync(module);
        await rig.InitializeAsync();

        await rig.Host.RequestAsync(ModuleMethods.Resolve, new ResolveParams("42", null),
            SdkJsonContext.Default.ResolveParams, SdkJsonContext.Default.ResolvedPlayable,
            TestContext.Current.CancellationToken);

        MetadataUpdate update = await rig.NextMetadataAsync();

        Assert.Equal("42", update.PlayableId);
        Assert.Equal("Demo stream", update.Title);
    }

    [Fact]
    public async Task AHandlerThatThrows_BecomesAnError_NotACrash()
    {
        var module = new FakeModule();
        await using var rig = await ModuleRig.StartAsync(module);
        await rig.InitializeAsync();

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await rig.Host.RequestAsync(ModuleMethods.Resolve, new ResolveParams("boom", null),
                SdkJsonContext.Default.ResolveParams, SdkJsonContext.Default.ResolvedPlayable,
                TestContext.Current.CancellationToken));

        // the loop survived: the next request still answers
        MatchResult match = await rig.Host.RequestAsync(ModuleMethods.Match,
            new MatchParams("https://demo.example/live/7"), SdkJsonContext.Default.MatchParams,
            SdkJsonContext.Default.MatchResult, TestContext.Current.CancellationToken);

        Assert.Equal("7", match.PlayableId);
    }

    [Fact]
    public async Task Shutdown_AnswersThenEndsTheLoop()
    {
        var module = new FakeModule();
        await using var rig = await ModuleRig.StartAsync(module);
        await rig.InitializeAsync();

        await rig.Host.RequestAsync(ModuleMethods.Shutdown, RpcUnit.Value, SdkJsonContext.Default.RpcUnit,
            SdkJsonContext.Default.RpcUnit, TestContext.Current.CancellationToken);

        int exitCode = await rig.Runner.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.True(module.ShutdownCalled);
    }

    [Fact]
    public async Task Diagnostics_AndActions_AreServedByTheDefaultSurface()
    {
        var module = new FakeModule();
        await using var rig = await ModuleRig.StartAsync(module);
        await rig.InitializeAsync();

        DiagnosticsReport report = await rig.Host.RequestAsync(ModuleMethods.Diagnostics, RpcUnit.Value,
            SdkJsonContext.Default.RpcUnit, SdkJsonContext.Default.DiagnosticsReport,
            TestContext.Current.CancellationToken);

        Assert.Single(report.Sections);
        Assert.Equal("Fake", report.Sections[0].Title);

        ModuleActionResult action = await rig.Host.RequestAsync(ModuleMethods.Action, new ModuleActionParams("retry"),
            SdkJsonContext.Default.ModuleActionParams, SdkJsonContext.Default.ModuleActionResult,
            TestContext.Current.CancellationToken);

        Assert.True(action.Ok);
        Assert.Equal("retry", module.LastAction);
    }

    [Fact]
    public async Task TestHost_DrivesTheSameModuleWithoutAnyTransport()
    {
        var module = new FakeModule();
        var host = new ModuleTestHost(module);

        await host.InitializeAsync(ct: TestContext.Current.CancellationToken);
        MatchResult? match = await host.MatchAsync("https://demo.example/live/9",
            TestContext.Current.CancellationToken);
        ResolvedPlayable resolved = await host.ResolveAsync("9", TestContext.Current.CancellationToken);

        Assert.Equal("9", match!.PlayableId);
        Assert.Equal("Demo stream", resolved.Title);
        Assert.Single(host.Metadata);
        Assert.Equal("9", host.Metadata[0].PlayableId);
        Assert.Contains(host.Logs, l => l.Level == ModuleLogLevel.Info);

        using IModuleStream? stream = await host.OpenStreamAsync("demo", TestContext.Current.CancellationToken);
        var buffer = new byte[16];
        int n = await stream!.ReadAsync(4, buffer, TestContext.Current.CancellationToken);

        Assert.Equal(16, n);
        Assert.Equal(Payload[4..20], buffer);
    }

    private static byte[] BuildPayload(int length)
    {
        var bytes = new byte[length];
        for (int i = 0; i < length; i++) bytes[i] = (byte)(i * 7 % 251);
        return bytes;
    }

    /// <summary>A small but complete module: matches a url shape, resolves to a module-served stream, logs, pushes metadata.</summary>
    private sealed class FakeModule : WaveeModule
    {
        public ResolvePreferences? LastPrefs { get; private set; }

        public string? LastAction { get; private set; }

        public ByteArrayModuleStream? LastStream { get; private set; }

        public bool ShutdownCalled { get; private set; }

        public override ValueTask InitializeAsync(ModuleContext ctx, CancellationToken ct)
        {
            Host.Log(ModuleLogLevel.Info, $"initialized for {ctx.HostVersion} at protocol {ctx.ProtocolVersion}");
            return default;
        }

        public override ValueTask<MatchResult?> MatchAsync(string input, CancellationToken ct)
        {
            const string marker = "demo.example/live/";
            int at = input.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0) return new ValueTask<MatchResult?>((MatchResult?)null);

            string id = input[(at + marker.Length)..];
            return new ValueTask<MatchResult?>(new MatchResult(id, "Demo stream", MediaForm.Audio, true, 1.0));
        }

        public override ValueTask<ResolvedPlayable> ResolveAsync(string playableId, ResolvePreferences? prefs,
            CancellationToken ct)
        {
            LastPrefs = prefs;
            return ResolveAsync(playableId, ct);
        }

        public override ValueTask<ResolvedPlayable> ResolveAsync(string playableId, CancellationToken ct)
        {
            if (playableId == "boom") throw new InvalidOperationException("resolve blew up");

            Host.PublishMetadata(new MetadataUpdate(playableId, "Demo stream", ["Demo artist"], null));
            return new ValueTask<ResolvedPlayable>(new ResolvedPlayable(
                playableId,
                "Demo stream",
                ["Demo artist"],
                null,
                0,
                true,
                MediaForm.Audio,
                MediaLocator.FromStream("demo", "audio/ogg"),
                null,
                ["preparedNext", "wireMeta"],
                -3.5f,
                new WireMeta([1, 2, 3], [4, 5], 320, "OGG_VORBIS_320", 0)));
        }

        public override ValueTask<IModuleStream?> OpenStreamAsync(string streamId, CancellationToken ct)
        {
            if (streamId != "demo") return new ValueTask<IModuleStream?>((IModuleStream?)null);

            LastStream = new ByteArrayModuleStream(Payload, "audio/ogg");
            return new ValueTask<IModuleStream?>(LastStream);
        }

        public override ValueTask<DiagnosticsReport> GetDiagnosticsAsync(CancellationToken ct)
            => new(new DiagnosticsReport([new DiagnosticsSection("Fake", [["state", "ready"]])]));

        public override ValueTask InvokeActionAsync(string actionId, CancellationToken ct)
        {
            LastAction = actionId;
            return default;
        }

        public override ValueTask ShutdownAsync(CancellationToken ct)
        {
            ShutdownCalled = true;
            return default;
        }
    }

    /// <summary>The module under <see cref="ModuleRunner"/> plus a host-side <see cref="JsonRpcConnection"/>.</summary>
    private sealed class ModuleRig : IAsyncDisposable
    {
        private readonly MemoryPipe _hostToModule = new();
        private readonly MemoryPipe _moduleToHost = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Channel<MetadataUpdate> _metadata =
            Channel.CreateUnbounded<MetadataUpdate>();

        private Task _hostLoop = Task.CompletedTask;

        private ModuleRig(WaveeModule module)
        {
            Host = new JsonRpcConnection(_moduleToHost, _hostToModule);
            Host.OnNotification(ModuleMethods.Metadata, SdkJsonContext.Default.MetadataUpdate,
                u => _metadata.Writer.TryWrite(u));
            Runner = ModuleRunner.RunAsync(module, _hostToModule, _moduleToHost, _cts.Token);
        }

        public JsonRpcConnection Host { get; }

        public Task<int> Runner { get; }

        public static Task<ModuleRig> StartAsync(WaveeModule module)
        {
            var rig = new ModuleRig(module);
            rig.Start();
            return Task.FromResult(rig);
        }

        public Task<InitializeResult> InitializeAsync(int minProtocol = ModuleProtocol.MinSupported,
            int maxProtocol = ModuleProtocol.Version)
            => Host.RequestAsync(ModuleMethods.Initialize,
                new InitializeParams("test-host", minProtocol, maxProtocol, "C:\\wavee-test", "en-US", 0, null),
                SdkJsonContext.Default.InitializeParams, SdkJsonContext.Default.InitializeResult, _cts.Token);

        public async Task<MetadataUpdate> NextMetadataAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            return await _metadata.Reader.ReadAsync(timeout.Token);
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _hostToModule.CompleteWriting();
            _moduleToHost.CompleteWriting();
            await Host.DisposeAsync();
            try
            {
                await Task.WhenAll(_hostLoop, Runner).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
                // teardown races are not test failures
            }

            _cts.Dispose();
        }

        private void Start() => _hostLoop = Host.RunAsync(_cts.Token);
    }
}
