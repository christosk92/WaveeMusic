using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Modules;
using Wavee.Sdk;
using Wavee.Sdk.Protocol;
using Xunit;

namespace Wavee.Tests.Modules;

/// <summary>
/// The lifecycle state machine, driven through an injectable transport: lazy start, the handshake, a crash and its
/// restart, the Faulted latch after three failed starts, Retry, idle stop, and the stream lease that blocks it.
/// No real process is ever spawned.
/// </summary>
public class ModuleProcessLifecycleTests
{
    static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    sealed class Harness : IModuleHostSink, IAsyncDisposable
    {
        readonly List<FakeModuleChannel> _channels = [];
        DateTimeOffset _now = DateTimeOffset.UnixEpoch;

        public Harness(FakeModule script, InstalledModule? module = null)
        {
            Script = script;
            Module = module ?? ModuleFixtures.Installed(ModuleFixtures.Manifest());
            Process = new ModuleProcess(Module, this, default,
                (_, _, _) =>
                {
                    if (FailSpawn) throw new IOException("the executable is missing");
                    var channel = new FakeModuleChannel(script);
                    channel.Start();
                    _channels.Add(channel);
                    return Task.FromResult<IModuleChannel>(channel);
                }, "1.0.0-test", "en-US", null, () => _now);
        }

        public FakeModule Script { get; }

        public InstalledModule Module { get; }

        public ModuleProcess Process { get; }

        public bool FailSpawn { get; set; }

        public IReadOnlyList<FakeModuleChannel> Channels => _channels;

        public FakeModuleChannel Last => _channels[^1];

        public List<MetadataUpdate> Metadata { get; } = [];

        public List<string> Expired { get; } = [];

        public void Advance(TimeSpan by) => _now += by;

        public void OnMetadata(string moduleId, MetadataUpdate update) => Metadata.Add(update);

        public void OnExpired(string moduleId, string playableId) => Expired.Add(playableId);

        public void OnStatus(string moduleId, ModuleStatus status) { }

        public void OnProgress(string moduleId, ProgressNotification progress) { }

        public void OnLog(string moduleId, LogNotification line) { }

        public void RegisterHostServices(InstalledModule module, JsonRpcConnection connection) { }

        public async ValueTask DisposeAsync()
        {
            await Process.DisposeAsync();
            foreach (FakeModuleChannel c in _channels) await c.DisposeAsync();
        }
    }

    static FakeModule Resolvable() => new()
    {
        Resolve = p => ModuleFixtures.Resolved(p.PlayableId),
    };

    static Task<ResolvedPlayable> Resolve(ModuleProcess p, string id = "p1") =>
        p.RequestAsync(ModuleMethods.Resolve, new ResolveParams(id, null),
            SdkJsonContext.Default.ResolveParams, SdkJsonContext.Default.ResolvedPlayable,
            ModuleTimeouts.Resolve, Ct);

    [Fact]
    public async Task NothingStartsUntilTheFirstRequest()
    {
        await using var h = new Harness(Resolvable());
        Assert.Equal(ModuleProcessState.Stopped, h.Process.State);
        Assert.Empty(h.Channels);

        await Resolve(h.Process);

        Assert.Equal(ModuleProcessState.Ready, h.Process.State);
        Assert.Equal(1, h.Script.InitializeCalls);
        Assert.Equal(4242, h.Process.ProcessId);
    }

    [Fact]
    public async Task Handshake_PublishesTheEffectiveCapabilities()
    {
        var script = Resolvable();
        script.Initialize = _ => new InitializeResult(1, ["playback"], null);
        await using var h = new Harness(script);

        await Resolve(h.Process);

        Assert.Equal(1, h.Process.NegotiatedProtocol);
        Assert.True(h.Process.Declares("playback"));
        // The EFFECTIVE list narrows the manifest's — the module said it cannot match this run.
        Assert.False(h.Process.Declares("match"));
    }

    [Fact]
    public async Task AProtocolOutsideTheHostRange_IsRefused()
    {
        var script = Resolvable();
        script.Initialize = _ => new InitializeResult(ModuleCatalog.MaxProtocol + 9, ["playback"], null);
        await using var h = new Harness(script);

        var ex = await Assert.ThrowsAsync<ModuleException>(() => Resolve(h.Process));
        Assert.Equal(ModuleErrorCode.Unsupported, ex.Code);
        Assert.Equal(ModuleProcessState.Crashed, h.Process.State);
    }

    [Fact]
    public async Task ACrashWhileReady_MovesToCrashed_AndTheNextRequestRestarts()
    {
        await using var h = new Harness(Resolvable());
        await Resolve(h.Process);
        Assert.Equal(ModuleProcessState.Ready, h.Process.State);

        h.Last.Crash();
        await WaitForStateAsync(h.Process, ModuleProcessState.Crashed);

        // The backoff is clock-driven, so advancing the harness clock past it is what lets the restart run.
        h.Advance(TimeSpan.FromSeconds(5));
        await Resolve(h.Process);

        Assert.Equal(ModuleProcessState.Ready, h.Process.State);
        Assert.Equal(2, h.Script.InitializeCalls);
        Assert.Equal(1, h.Process.Stats.Snapshot().Restarts);
    }

    [Fact]
    public async Task ThreeFailedStarts_Fault_AndRetryClearsTheLatch()
    {
        await using var h = new Harness(Resolvable()) { FailSpawn = true };

        for (int i = 0; i < ModuleTimeouts.MaxConsecutiveFailedStarts; i++)
        {
            await Assert.ThrowsAsync<ModuleException>(() => Resolve(h.Process));
            h.Advance(TimeSpan.FromMinutes(1));
        }

        Assert.Equal(ModuleProcessState.Faulted, h.Process.State);
        Assert.NotNull(h.Process.LastError);

        // While Faulted, a request fails FAST (transient) without another spawn attempt…
        var ex = await Assert.ThrowsAsync<ModuleException>(() => Resolve(h.Process));
        Assert.Equal(ModuleErrorCode.Transient, ex.Code);

        // …until Retry (the diagnostics page's button) clears it.
        h.FailSpawn = false;
        h.Process.Retry();
        Assert.Equal(ModuleProcessState.Stopped, h.Process.State);
        await Resolve(h.Process);
        Assert.Equal(ModuleProcessState.Ready, h.Process.State);
    }

    [Fact]
    public async Task TheFaultCooldown_AllowsOneMoreAttempt()
    {
        await using var h = new Harness(Resolvable()) { FailSpawn = true };
        for (int i = 0; i < ModuleTimeouts.MaxConsecutiveFailedStarts; i++)
        {
            await Assert.ThrowsAsync<ModuleException>(() => Resolve(h.Process));
            h.Advance(TimeSpan.FromMinutes(1));
        }

        Assert.Equal(ModuleProcessState.Faulted, h.Process.State);

        h.FailSpawn = false;
        h.Advance(ModuleTimeouts.FaultCooldown + TimeSpan.FromMinutes(1));
        await Resolve(h.Process);

        Assert.Equal(ModuleProcessState.Ready, h.Process.State);
    }

    [Fact]
    public async Task IdleSweep_StopsAModuleThatHasBeenSilentForTenMinutes()
    {
        await using var h = new Harness(Resolvable());
        await Resolve(h.Process);

        h.Advance(TimeSpan.FromMinutes(5));
        await h.Process.IdleSweepAsync(Ct);
        Assert.Equal(ModuleProcessState.Ready, h.Process.State);

        h.Advance(TimeSpan.FromMinutes(6));
        await h.Process.IdleSweepAsync(Ct);
        Assert.Equal(ModuleProcessState.Stopped, h.Process.State);
    }

    [Fact]
    public async Task AnOpenStreamLease_BlocksTheIdleStop()
    {
        await using var h = new Harness(Resolvable());
        await Resolve(h.Process);
        using IDisposable lease = h.Process.AcquireStreamLease();

        h.Advance(TimeSpan.FromMinutes(30));
        await h.Process.IdleSweepAsync(Ct);

        Assert.Equal(ModuleProcessState.Ready, h.Process.State);
    }

    [Fact]
    public async Task ModuleNotifications_ReachTheHostSink()
    {
        await using var h = new Harness(Resolvable());
        await Resolve(h.Process);

        h.Last.Module.Notify(ModuleMethods.Metadata,
            new MetadataUpdate("p1", "Now playing", ["Someone"], null), SdkJsonContext.Default.MetadataUpdate);
        h.Last.Module.Notify(ModuleMethods.Expired,
            new ExpiredNotification("p1"), SdkJsonContext.Default.ExpiredNotification);

        await WaitAsync(() => h.Metadata.Count > 0 && h.Expired.Count > 0);
        Assert.Equal("Now playing", h.Metadata[0].Title);
        Assert.Equal("p1", h.Expired[0]);
    }

    [Fact]
    public async Task AModuleErrorIsRaisedTyped_AndCounted()
    {
        var script = new FakeModule
        {
            Resolve = _ => throw new ModuleException(ModuleErrorCode.GeoBlocked, "not in your country")
            {
                RetryAfterMs = 5_000,
                Detail = "geo",
            },
        };
        await using var h = new Harness(script);

        var ex = await Assert.ThrowsAsync<ModuleException>(() => Resolve(h.Process));

        Assert.Equal(ModuleErrorCode.GeoBlocked, ex.Code);
        Assert.Equal(5_000, ex.RetryAfterMs);
        Assert.Equal("geo", ex.Detail);
        Assert.Equal(1, h.Process.Stats.Snapshot().Failures);
    }

    [Fact]
    public async Task AnUnimplementedMethod_ReadsAsCapabilityAbsent_NotAFailureOfTheModule()
    {
        // The fake registers no handler for module/action, so the peer answers -32601.
        await using var h = new Harness(Resolvable());
        await Resolve(h.Process);

        var ex = await Assert.ThrowsAsync<ModuleException>(() => h.Process.RequestAsync(
            ModuleMethods.Action, new ModuleActionParams("x"), SdkJsonContext.Default.ModuleActionParams,
            SdkJsonContext.Default.ModuleActionResult, ModuleTimeouts.Diagnostics, Ct));

        Assert.Equal(ModuleErrorCode.Unsupported, ex.Code);
        Assert.Equal(ModuleProcessState.Ready, h.Process.State);   // the module is fine; the capability is absent
    }

    [Fact]
    public async Task StopAsync_AsksForAShutdownThenTearsTheChannelDown()
    {
        await using var h = new Harness(Resolvable());
        await Resolve(h.Process);

        await h.Process.StopAsync("test", Ct);

        Assert.Equal(ModuleProcessState.Stopped, h.Process.State);
        Assert.Null(h.Process.ProcessId);
    }

    static async Task WaitForStateAsync(ModuleProcess process, ModuleProcessState state)
        => await WaitAsync(() => process.State == state);

    static async Task WaitAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200; i++)
        {
            if (condition()) return;
            await Task.Delay(10, Ct);
        }

        Assert.Fail("the condition never became true");
    }
}
