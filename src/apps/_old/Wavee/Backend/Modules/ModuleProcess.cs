using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Sdk;
using Wavee.Sdk.Protocol;

namespace Wavee.Backend.Modules;

// ── THE MODULE PROCESS — one child process, one JSON-RPC peer, one lifecycle ─────────────────────────────────────────
// A module is an out-of-process executable speaking JSON-RPC 2.0 over stdio (the app is NativeAOT + TrimMode full, so a
// managed plugin assembly cannot be loaded at all). This class owns exactly one of them:
//
//   Stopped → Starting (spawn + module/initialize, 5 s) → Ready → (idle 10 min → module/shutdown, 2 s, kill) → Stopped
//   any exit / broken pipe while Ready → Crashed → next request restarts with backoff 1 s / 4 s / 16 s
//   3 consecutive failed starts → Faulted (no auto-restart until "Retry" on the diagnostics page, or 10 minutes)
//
// A process holding an open stream handle is NEVER idle-stopped (that is the audio path). The transport is behind
// <see cref="IModuleChannel"/> so the whole state machine is unit-testable with an in-memory JsonRpcConnection pair and
// no real process anywhere.

/// <summary>Where a module process is in its lifecycle.</summary>
public enum ModuleProcessState
{
    /// <summary>Not running; the next request starts it.</summary>
    Stopped,
    /// <summary>Spawned; the <c>module/initialize</c> handshake is in flight. Requests queue behind it.</summary>
    Starting,
    /// <summary>Handshake complete; requests are served.</summary>
    Ready,
    /// <summary>It exited or the pipe broke. The next request restarts it after the backoff.</summary>
    Crashed,
    /// <summary>Three consecutive failed starts. No auto-restart until <see cref="ModuleProcess.Retry"/> or the cooldown.</summary>
    Faulted,
}

/// <summary>The transport to one running module: a JSON-RPC peer plus the process handle behind it.</summary>
public interface IModuleChannel : IAsyncDisposable
{
    /// <summary>The peer. The host assigns positive request ids; the module's own are negative.</summary>
    JsonRpcConnection Connection { get; }

    /// <summary>OS process id, or 0 for an in-memory test channel.</summary>
    int ProcessId { get; }

    /// <summary>True once the process is gone.</summary>
    bool HasExited { get; }

    /// <summary>Terminate immediately (the end of the shutdown grace, or a request that never came back).</summary>
    void Kill();

    /// <summary>Wait up to <paramref name="grace"/> for a clean exit; returns false on timeout.</summary>
    /// <param name="grace">How long to wait.</param>
    Task<bool> WaitForExitAsync(TimeSpan grace);
}

/// <summary>How a channel is produced for one module. Injected so tests never spawn a process.</summary>
/// <param name="module">The module to launch.</param>
/// <param name="stderr">Sink for the module's stderr lines (one call per line).</param>
/// <param name="ct">Cancels the launch.</param>
public delegate Task<IModuleChannel> ModuleSpawn(InstalledModule module, Action<string> stderr, CancellationToken ct);

/// <summary>Every host-side timeout and interval, in one place, so the process and its tests cannot disagree.</summary>
public static class ModuleTimeouts
{
    /// <summary>The <c>module/initialize</c> handshake budget.</summary>
    public static readonly TimeSpan Initialize = TimeSpan.FromSeconds(5);
    /// <summary><c>playback/match</c>.</summary>
    public static readonly TimeSpan Match = TimeSpan.FromSeconds(5);
    /// <summary><c>playback/resolve</c>.</summary>
    public static readonly TimeSpan Resolve = TimeSpan.FromSeconds(20);
    /// <summary><c>stream/open</c>.</summary>
    public static readonly TimeSpan StreamOpen = TimeSpan.FromSeconds(10);
    /// <summary><c>stream/read</c>.</summary>
    public static readonly TimeSpan StreamRead = TimeSpan.FromSeconds(10);
    /// <summary><c>module/diagnostics</c> and <c>module/action</c>.</summary>
    public static readonly TimeSpan Diagnostics = TimeSpan.FromSeconds(10);
    /// <summary>How long a module gets to answer <c>module/shutdown</c> before it is killed.</summary>
    public static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(2);
    /// <summary>How long a timed-out request's <c>$/cancelRequest</c> gets before the process is killed.</summary>
    public static readonly TimeSpan CancelGrace = TimeSpan.FromSeconds(2);
    /// <summary>Idle before a module with no open stream handle is shut down.</summary>
    public static readonly TimeSpan Idle = TimeSpan.FromMinutes(10);
    /// <summary>How long <see cref="ModuleProcessState.Faulted"/> lasts before one more automatic attempt.</summary>
    public static readonly TimeSpan FaultCooldown = TimeSpan.FromMinutes(10);
    /// <summary>Restart backoff, indexed by consecutive failed starts.</summary>
    public static readonly TimeSpan[] Backoff =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(16)];
    /// <summary>Consecutive failed starts that make a module <see cref="ModuleProcessState.Faulted"/>.</summary>
    public const int MaxConsecutiveFailedStarts = 3;
}

/// <summary>What a module's out-of-band traffic reaches. Implemented by <see cref="ModuleHost"/>.</summary>
public interface IModuleHostSink
{
    /// <summary>A live "now playing" correction (<c>playback/metadata</c>).</summary>
    /// <param name="moduleId">The module that sent it.</param>
    /// <param name="update">The correction.</param>
    void OnMetadata(string moduleId, MetadataUpdate update);

    /// <summary>A resolved locator died (<c>playback/expired</c>).</summary>
    /// <param name="moduleId">The module that sent it.</param>
    /// <param name="playableId">The module-private playable id.</param>
    void OnExpired(string moduleId, string playableId);

    /// <summary>The module's own state changed (<c>module/status</c>).</summary>
    /// <param name="moduleId">The module that sent it.</param>
    /// <param name="status">The status card.</param>
    void OnStatus(string moduleId, ModuleStatus status);

    /// <summary>Progress of a long-running module step (<c>module/progress</c>).</summary>
    /// <param name="moduleId">The module that sent it.</param>
    /// <param name="progress">Stage + percent.</param>
    void OnProgress(string moduleId, ProgressNotification progress);

    /// <summary>One structured log line (<c>module/log</c>).</summary>
    /// <param name="moduleId">The module that sent it.</param>
    /// <param name="line">Level + message.</param>
    void OnLog(string moduleId, LogNotification line);

    /// <summary>Register the permission-gated host services on a freshly-opened connection.</summary>
    /// <param name="module">The module the connection belongs to.</param>
    /// <param name="connection">The peer to register handlers on.</param>
    void RegisterHostServices(InstalledModule module, JsonRpcConnection connection);
}

/// <summary>One module's child process, its JSON-RPC peer and its lifecycle state machine.</summary>
public sealed class ModuleProcess : IAsyncDisposable
{
    readonly InstalledModule _module;
    readonly WaveeLogger _log;
    readonly IModuleHostSink _sink;
    readonly ModuleSpawn _spawn;
    readonly string _hostVersion;
    readonly string _locale;
    readonly Func<ResolvePreferences> _prefs;
    readonly Func<DateTimeOffset> _now;
    readonly SemaphoreSlim _startGate = new(1, 1);
    readonly Lock _stateGate = new();

    IModuleChannel? _channel;
    Task? _pump;
    CancellationTokenSource? _pumpCts;
    ModuleProcessState _state = ModuleProcessState.Stopped;
    string[] _capabilities = [];
    int _negotiatedProtocol;
    string? _lastError;
    int _consecutiveFailedStarts;
    DateTimeOffset _retryNotBefore = DateTimeOffset.MinValue;
    DateTimeOffset _lastUsedUtc;
    int _openStreamLeases;
    int _inFlight;
    int _disposed;
    long _generation;

    /// <summary>Build a process wrapper. Nothing is launched until the first request.</summary>
    /// <param name="module">The module to run.</param>
    /// <param name="sink">Where the module's notifications and host-service registrations go.</param>
    /// <param name="log">The logger; stderr and module logs use the category <c>module.&lt;id&gt;</c>.</param>
    /// <param name="spawn">The transport factory; null uses a real child process.</param>
    /// <param name="hostVersion">The app's informational version, sent in the handshake.</param>
    /// <param name="locale">The app's BCP-47 locale, sent in the handshake.</param>
    /// <param name="prefs">The app's current playback preferences, sent in the handshake and on every resolve.</param>
    /// <param name="now">Clock seam (tests).</param>
    public ModuleProcess(InstalledModule module, IModuleHostSink sink, WaveeLogger log,
        ModuleSpawn? spawn = null, string? hostVersion = null, string? locale = null,
        Func<ResolvePreferences>? prefs = null, Func<DateTimeOffset>? now = null)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(sink);
        _module = module;
        _sink = sink;
        _log = log;
        _spawn = spawn ?? ChildProcessChannel.SpawnAsync;
        _hostVersion = hostVersion ?? "0.0.0";
        _locale = locale ?? "en-US";
        _prefs = prefs ?? (() => ResolvePreferences.Default);
        _now = now ?? (() => DateTimeOffset.UtcNow);
        Stats = new ModuleStats();
        _lastUsedUtc = _now();
    }

    /// <summary>The module this process runs.</summary>
    public InstalledModule Module => _module;

    /// <summary>Request/failure/latency counters for the diagnostics page.</summary>
    public ModuleStats Stats { get; }

    /// <summary>Where the lifecycle is right now.</summary>
    public ModuleProcessState State { get { lock (_stateGate) return _state; } }

    /// <summary>The OS process id while it is running, else null.</summary>
    public int? ProcessId { get { lock (_stateGate) return _channel is { HasExited: false } c ? c.ProcessId : null; } }

    /// <summary>The most recent failure message, or null.</summary>
    public string? LastError { get { lock (_stateGate) return _lastError; } }

    /// <summary>The module's EFFECTIVE capabilities, from the handshake (empty until it has run once).</summary>
    public IReadOnlyList<string> Capabilities { get { lock (_stateGate) return _capabilities; } }

    /// <summary>The protocol version both sides settled on (0 until the first handshake).</summary>
    public int NegotiatedProtocol { get { lock (_stateGate) return _negotiatedProtocol; } }

    /// <summary>The module's status card from its last <c>module/status</c> notification, or null.</summary>
    public ModuleStatus? Status { get; internal set; }

    /// <summary>Clear <see cref="ModuleProcessState.Faulted"/> so the next request tries again — the diagnostics
    /// page's "Retry" button, and what the 10-minute cooldown does on its own.</summary>
    public void Retry()
    {
        lock (_stateGate)
        {
            if (_state == ModuleProcessState.Faulted) _state = ModuleProcessState.Stopped;
            _consecutiveFailedStarts = 0;
            _retryNotBefore = DateTimeOffset.MinValue;
        }
    }

    /// <summary>Does the module declare this capability (effective list after a handshake, manifest before)?</summary>
    /// <param name="capability">The capability token, e.g. <c>match</c>.</param>
    public bool Declares(string capability)
    {
        IReadOnlyList<string> effective = Capabilities;
        IReadOnlyList<string> source = effective.Count > 0 ? effective : _module.Manifest.Capabilities ?? [];
        for (int i = 0; i < source.Count; i++)
            if (string.Equals(source[i], capability, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // ── requests ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Send a request and await its JSON result, starting the process first if it is not running.</summary>
    /// <typeparam name="TParams">Params shape.</typeparam>
    /// <typeparam name="TResult">Result shape.</typeparam>
    /// <param name="method">Wire method name.</param>
    /// <param name="p">Params value.</param>
    /// <param name="paramsInfo">Source-generated params type info.</param>
    /// <param name="resultInfo">Source-generated result type info.</param>
    /// <param name="timeout">Per-request budget (see <see cref="ModuleTimeouts"/>).</param>
    /// <param name="ct">Cancels the request.</param>
    public async Task<TResult> RequestAsync<TParams, TResult>(string method, TParams p,
        JsonTypeInfo<TParams> paramsInfo, JsonTypeInfo<TResult> resultInfo, TimeSpan timeout, CancellationToken ct)
    {
        IModuleChannel channel = await EnsureReadyAsync(ct).ConfigureAwait(false);
        long started = Environment.TickCount64;
        Interlocked.Increment(ref _inFlight);
        try
        {
            TResult r = await channel.Connection
                .RequestAsync(method, p, paramsInfo, resultInfo, timeout, ct).ConfigureAwait(false);
            Stats.NoteRequest(Environment.TickCount64 - started);
            Touch();
            return r;
        }
        catch (Exception ex)
        {
            throw Fail(channel, method, ex);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    /// <summary>Send a request whose answer is a raw binary frame (the <c>stream/read</c> audio path).</summary>
    /// <typeparam name="TParams">Params shape.</typeparam>
    /// <param name="method">Wire method name.</param>
    /// <param name="p">Params value.</param>
    /// <param name="paramsInfo">Source-generated params type info.</param>
    /// <param name="timeout">Per-request budget.</param>
    /// <param name="ct">Cancels the request.</param>
    public async Task<BinaryPayload> RequestBinaryAsync<TParams>(string method, TParams p,
        JsonTypeInfo<TParams> paramsInfo, TimeSpan timeout, CancellationToken ct)
    {
        IModuleChannel channel = await EnsureReadyAsync(ct).ConfigureAwait(false);
        long started = Environment.TickCount64;
        Interlocked.Increment(ref _inFlight);
        try
        {
            BinaryPayload r = await channel.Connection
                .RequestBinaryAsync(method, p, paramsInfo, timeout, ct).ConfigureAwait(false);
            Stats.NoteRequest(Environment.TickCount64 - started);
            Touch();
            return r;
        }
        catch (Exception ex)
        {
            throw Fail(channel, method, ex);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    /// <summary>Fire-and-forget notification to a module that is ALREADY running (never a reason to start one).</summary>
    /// <typeparam name="T">Params shape.</typeparam>
    /// <param name="method">Wire method name.</param>
    /// <param name="p">Params value.</param>
    /// <param name="paramsInfo">Source-generated params type info.</param>
    public void NotifyIfRunning<T>(string method, T p, JsonTypeInfo<T> paramsInfo)
    {
        IModuleChannel? c;
        lock (_stateGate) c = _state == ModuleProcessState.Ready ? _channel : null;
        if (c is null) return;
        try { c.Connection.Notify(method, p, paramsInfo); }
        catch (Exception ex) { _log.Info("notify " + method + " failed: " + ex.Message); }
    }

    /// <summary>Re-install the host services on a connection that is ALREADY up — a go-live install must reach a module
    /// the user started before signing in. Registering the same method again replaces the handler in place.</summary>
    /// <param name="services">The registry to (re-)install.</param>
    public void NotifyServicesChanged(ModuleHostServices services)
    {
        IModuleChannel? c;
        lock (_stateGate) c = _state == ModuleProcessState.Ready ? _channel : null;
        if (c is not null) services.Install(_module, c.Connection);
    }

    /// <summary>Take a lease that keeps this process out of the idle stop — held for as long as a
    /// <see cref="ModuleByteStream"/> has a handle open. Disposing it releases the lease.</summary>
    public IDisposable AcquireStreamLease()
    {
        Interlocked.Increment(ref _openStreamLeases);
        Touch();
        return new StreamLease(this);
    }

    /// <summary>True while any stream handle is open (an idle stop would cut the audio).</summary>
    public bool HasOpenStreams => Volatile.Read(ref _openStreamLeases) > 0;

    // ── lifecycle ───────────────────────────────────────────────────────────────────────────────────────────────────

    async Task<IModuleChannel> EnsureReadyAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        lock (_stateGate)
        {
            if (_state == ModuleProcessState.Ready && _channel is { HasExited: false } live)
            {
                _lastUsedUtc = _now();
                return live;
            }
        }

        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_stateGate)
            {
                if (_state == ModuleProcessState.Ready && _channel is { HasExited: false } live)
                {
                    _lastUsedUtc = _now();
                    return live;
                }
            }

            await WaitOutBackoffAsync(ct).ConfigureAwait(false);
            return await StartAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _startGate.Release();
        }
    }

    async Task WaitOutBackoffAsync(CancellationToken ct)
    {
        DateTimeOffset notBefore;
        lock (_stateGate)
        {
            if (_state == ModuleProcessState.Faulted)
            {
                if (_now() < _retryNotBefore)
                    throw new ModuleException(ModuleErrorCode.Transient,
                        _lastError ?? (_module.Id + " failed to start three times in a row"));
                // The cooldown elapsed: one more automatic attempt.
                _state = ModuleProcessState.Stopped;
                _consecutiveFailedStarts = 0;
                _retryNotBefore = DateTimeOffset.MinValue;
            }

            notBefore = _retryNotBefore;
        }

        TimeSpan wait = notBefore - _now();
        if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
    }

    async Task<IModuleChannel> StartAsync(CancellationToken ct)
    {
        await TearDownAsync(kill: true).ConfigureAwait(false);

        long generation;
        lock (_stateGate)
        {
            _state = ModuleProcessState.Starting;
            generation = ++_generation;
            if (generation > 1) Stats.NoteRestart();
        }

        IModuleChannel? channel = null;
        CancellationTokenSource? cts = null;
        try
        {
            channel = await _spawn(_module, OnStderrLine, ct).ConfigureAwait(false);
            JsonRpcConnection conn = channel.Connection;
            conn.OnNotification(ModuleMethods.Metadata, SdkJsonContext.Default.MetadataUpdate,
                u => _sink.OnMetadata(_module.Id, u));
            conn.OnNotification(ModuleMethods.Expired, SdkJsonContext.Default.ExpiredNotification,
                e => _sink.OnExpired(_module.Id, e.PlayableId));
            conn.OnNotification(ModuleMethods.Status, SdkJsonContext.Default.ModuleStatus,
                s => { Status = s; _sink.OnStatus(_module.Id, s); });
            conn.OnNotification(ModuleMethods.Progress, SdkJsonContext.Default.ProgressNotification,
                pr => _sink.OnProgress(_module.Id, pr));
            conn.OnNotification(ModuleMethods.Log, SdkJsonContext.Default.LogNotification,
                l => _sink.OnLog(_module.Id, l));
            _sink.RegisterHostServices(_module, conn);

            cts = new CancellationTokenSource();
            IModuleChannel started = channel;
            Task pump = Task.Run(() => conn.RunAsync(cts.Token), CancellationToken.None);
            _ = pump.ContinueWith(_ => OnChannelDown(generation), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

            var init = new InitializeParams(_hostVersion, ModuleCatalog.MinProtocol, ModuleCatalog.MaxProtocol,
                EnsureDataDir(), _locale, 0, _prefs());
            InitializeResult result = await conn.RequestAsync(ModuleMethods.Initialize, init,
                SdkJsonContext.Default.InitializeParams, SdkJsonContext.Default.InitializeResult,
                ModuleTimeouts.Initialize, ct).ConfigureAwait(false);

            if (result.ProtocolVersion < ModuleCatalog.MinProtocol || result.ProtocolVersion > ModuleCatalog.MaxProtocol)
                throw new ModuleException(ModuleErrorCode.Unsupported,
                    _module.Id + " answered protocol " + result.ProtocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ", outside the host range");

            lock (_stateGate)
            {
                _channel = started;
                _pump = pump;
                _pumpCts = cts;
                _state = ModuleProcessState.Ready;
                _capabilities = result.Capabilities ?? [];
                _negotiatedProtocol = result.ProtocolVersion;
                _consecutiveFailedStarts = 0;
                _retryNotBefore = DateTimeOffset.MinValue;
                _lastError = null;
                _lastUsedUtc = _now();
            }

            _log.Info("module " + _module.Id + " ready (pid " + started.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ", protocol " + result.ProtocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")");
            return started;
        }
        catch (Exception ex)
        {
            if (cts is not null) { try { await cts.CancelAsync().ConfigureAwait(false); } catch { /* already gone */ } }
            if (channel is not null)
            {
                try { channel.Kill(); } catch { /* already gone */ }
                try { await channel.DisposeAsync().ConfigureAwait(false); } catch { /* already gone */ }
            }

            string message = Describe(ex);
            lock (_stateGate)
            {
                _channel = null;
                _pump = null;
                _pumpCts = null;
                _lastError = message;
                _consecutiveFailedStarts++;
                if (_consecutiveFailedStarts >= ModuleTimeouts.MaxConsecutiveFailedStarts)
                {
                    _state = ModuleProcessState.Faulted;
                    _retryNotBefore = _now() + ModuleTimeouts.FaultCooldown;
                }
                else
                {
                    _state = ModuleProcessState.Crashed;
                    _retryNotBefore = _now() + ModuleTimeouts.Backoff[
                        Math.Min(_consecutiveFailedStarts - 1, ModuleTimeouts.Backoff.Length - 1)];
                }
            }

            Stats.NoteFailure(ModuleMethods.Initialize, message);
            _log.Info("module " + _module.Id + " failed to start: " + message);
            if (ex is ModuleException or OperationCanceledException) throw;
            throw new ModuleException(ModuleErrorCode.Transient, message);
        }
    }

    void OnChannelDown(long generation)
    {
        bool wasReady;
        lock (_stateGate)
        {
            if (_generation != generation) return;   // a newer start already superseded this channel
            wasReady = _state is ModuleProcessState.Ready;
            if (_state is ModuleProcessState.Ready or ModuleProcessState.Starting)
            {
                _state = ModuleProcessState.Crashed;
                _retryNotBefore = _now() + ModuleTimeouts.Backoff[0];
            }
        }

        if (wasReady) _log.Info("module " + _module.Id + " exited or its pipe broke");
    }

    /// <summary>Idle sweep: shut a module down after 10 minutes of silence. A process with an open stream handle or an
    /// in-flight request is never touched. Driven by the host's timer so nothing here needs its own.</summary>
    /// <param name="ct">Cancels the shutdown handshake.</param>
    public async Task IdleSweepAsync(CancellationToken ct = default)
    {
        bool idle;
        lock (_stateGate)
        {
            idle = _state == ModuleProcessState.Ready
                   && Volatile.Read(ref _openStreamLeases) == 0
                   && Volatile.Read(ref _inFlight) == 0
                   && _now() - _lastUsedUtc >= ModuleTimeouts.Idle;
        }

        if (idle) await StopAsync("idle", ct).ConfigureAwait(false);
    }

    /// <summary>Ask the module to wind down (2 s grace), then kill it.</summary>
    /// <param name="reason">Why, for the log.</param>
    /// <param name="ct">Cancels the handshake (the kill still happens).</param>
    public async Task StopAsync(string reason, CancellationToken ct = default)
    {
        IModuleChannel? channel;
        lock (_stateGate)
        {
            channel = _channel;
            if (channel is null) { _state = ModuleProcessState.Stopped; return; }
            _state = ModuleProcessState.Stopped;
            _generation++;   // any in-flight pump completion belongs to a superseded generation now
        }

        _log.Info("module " + _module.Id + " stopping (" + reason + ")");
        try
        {
            await channel.Connection.RequestAsync(ModuleMethods.Shutdown, RpcUnit.Value,
                SdkJsonContext.Default.RpcUnit, SdkJsonContext.Default.RpcUnit,
                ModuleTimeouts.ShutdownGrace, ct).ConfigureAwait(false);
        }
        catch { /* a module that will not answer its own shutdown gets killed below */ }

        await TearDownAsync(kill: true).ConfigureAwait(false);
    }

    async Task TearDownAsync(bool kill)
    {
        IModuleChannel? channel;
        CancellationTokenSource? cts;
        lock (_stateGate)
        {
            channel = _channel;
            cts = _pumpCts;
            _channel = null;
            _pump = null;
            _pumpCts = null;
            _capabilities = [];
        }

        if (cts is not null)
        {
            try { await cts.CancelAsync().ConfigureAwait(false); } catch { /* already gone */ }
            cts.Dispose();
        }

        if (channel is null) return;
        if (kill && !channel.HasExited)
        {
            if (!await channel.WaitForExitAsync(ModuleTimeouts.ShutdownGrace).ConfigureAwait(false))
            {
                try { channel.Kill(); } catch { /* already gone */ }
            }
        }

        try { await channel.DisposeAsync().ConfigureAwait(false); } catch { /* already gone */ }
    }

    // ── plumbing ────────────────────────────────────────────────────────────────────────────────────────────────────

    void Touch() { lock (_stateGate) _lastUsedUtc = _now(); }

    Exception Fail(IModuleChannel channel, string method, Exception ex)
    {
        string message = Describe(ex);
        Stats.NoteFailure(method, message);
        lock (_stateGate) _lastError = message;

        // A timed-out request already sent $/cancelRequest. If the module still has not answered after the grace, the
        // process is wedged and the only honest recovery is to kill it (the next request restarts it).
        if (ex is TimeoutException) _ = KillAfterCancelGraceAsync(channel);
        if (ex is IOException or ObjectDisposedException) OnChannelDown(Volatile.Read(ref _generation));

        if (ex is ModuleException or OperationCanceledException) return ex;
        if (ex is JsonRpcException rpc)
        {
            if (rpc.Code == JsonRpcErrorCodes.MethodNotFound)
                return new ModuleException(ModuleErrorCode.Unsupported, method + " is not implemented by " + _module.Id);
            var code = rpc.ErrorData?.Kind ?? ModuleErrorCode.Transient;
            return new ModuleException(code, rpc.Message)
            {
                RetryAfterMs = rpc.ErrorData?.RetryAfterMs,
                Detail = rpc.ErrorData?.Detail,
            };
        }

        return new ModuleException(ModuleErrorCode.Transient, message);
    }

    async Task KillAfterCancelGraceAsync(IModuleChannel channel)
    {
        await Task.Delay(ModuleTimeouts.CancelGrace).ConfigureAwait(false);
        bool stillCurrent;
        lock (_stateGate) stillCurrent = ReferenceEquals(_channel, channel);
        if (!stillCurrent || channel.HasExited) return;
        _log.Info("module " + _module.Id + " did not answer $/cancelRequest — killing it");
        try { channel.Kill(); } catch { /* already gone */ }
    }

    void OnStderrLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        if (line.StartsWith("ERROR ", StringComparison.Ordinal)) _log.Error(line[6..]);
        else if (line.StartsWith("WARN ", StringComparison.Ordinal)) _log.Warn(line[5..]);
        else _log.Info(line);
    }

    string EnsureDataDir()
    {
        string dir = ModuleCatalog.DataDirFor(_module.Id);
        try { System.IO.Directory.CreateDirectory(dir); } catch { /* the module gets a path it can complain about */ }
        return dir;
    }

    static string Describe(Exception ex) => ex switch
    {
        TimeoutException => "the module did not answer in time",
        ModuleException m => m.Message,
        JsonRpcException r => r.Message,
        _ => ex.GetType().Name + ": " + ex.Message,
    };

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { await StopAsync("host shutdown").ConfigureAwait(false); }
        catch { /* teardown is best-effort */ }
        _startGate.Dispose();
    }

    sealed class StreamLease(ModuleProcess owner) : IDisposable
    {
        int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            Interlocked.Decrement(ref owner._openStreamLeases);
            owner.Touch();
        }
    }
}

// ── the real transport ───────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>The production <see cref="IModuleChannel"/>: a redirected child process whose stdout/stdin carry the
/// protocol and whose stderr streams into the app log. Every process is assigned to the host's job object, so a module
/// dies with the app even when the app is killed.</summary>
public sealed class ChildProcessChannel : IModuleChannel
{
    readonly Process _process;

    ChildProcessChannel(Process process, JsonRpcConnection connection)
    {
        _process = process;
        Connection = connection;
    }

    /// <inheritdoc/>
    public JsonRpcConnection Connection { get; }

    /// <inheritdoc/>
    public int ProcessId { get { try { return _process.Id; } catch { return 0; } } }

    /// <inheritdoc/>
    public bool HasExited { get { try { return _process.HasExited; } catch { return true; } } }

    /// <summary>The job object every module process is assigned to. Set once by <see cref="ModuleHost"/>; null means
    /// the OS refused to create one (the modules still run — they just do not die with a killed app).</summary>
    public static FluentGpu.WindowsApi.Shell.ChildProcessJob? Job { get; set; }

    /// <summary>Launch one module. <c>dotnet &lt;entry&gt;</c> for a <c>.dll</c> entry (the dev/framework-dependent
    /// layout), the executable itself otherwise (the published NativeAOT layout) — one host code path, the manifest
    /// decides.</summary>
    /// <param name="module">The module to launch.</param>
    /// <param name="stderr">Sink for the module's stderr lines.</param>
    /// <param name="ct">Cancels the launch.</param>
    public static Task<IModuleChannel> SpawnAsync(InstalledModule module, Action<string> stderr, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(module);
        ct.ThrowIfCancellationRequested();

        string entry = System.IO.Path.Combine(module.Dir, module.Manifest.Entry);
        bool managed = module.Manifest.Entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        var psi = new ProcessStartInfo
        {
            FileName = managed ? "dotnet" : entry,
            WorkingDirectory = module.Dir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
        };
        if (managed) psi.ArgumentList.Add(entry);
        psi.ArgumentList.Add("--wavee-module");
        psi.ArgumentList.Add("--protocol");
        psi.ArgumentList.Add(ModuleCatalog.MaxProtocol.ToString(System.Globalization.CultureInfo.InvariantCulture));
        psi.Environment["WAVEE_MODULE_ID"] = module.Id;
        psi.Environment["WAVEE_MODULE_DATA_DIR"] = ModuleCatalog.DataDirFor(module.Id);
        psi.Environment["WAVEE_HOST_PID"] = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        psi.Environment["WAVEE_HOST_VERSION"] = typeof(ChildProcessChannel).Assembly.GetName().Version?.ToString() ?? "0.0.0";

        Process process = Process.Start(psi)
            ?? throw new ModuleException(ModuleErrorCode.Transient, "could not start " + module.Id);
        try { Job?.Assign(process.Handle); } catch { /* the module still runs; it just is not job-bound */ }

        process.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 } line) stderr(line); };
        process.BeginErrorReadLine();

        // stdout is the protocol channel (the SDK's ModuleRunner swaps Console.Out to stderr before user code runs, so
        // a stray Console.WriteLine cannot corrupt framing); stdin carries our requests.
        var connection = new JsonRpcConnection(process.StandardOutput.BaseStream, process.StandardInput.BaseStream);
        return Task.FromResult<IModuleChannel>(new ChildProcessChannel(process, connection));
    }

    /// <inheritdoc/>
    public void Kill()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch { /* already gone */ }
    }

    /// <inheritdoc/>
    public async Task<bool> WaitForExitAsync(TimeSpan grace)
    {
        try
        {
            using var cts = new CancellationTokenSource(grace);
            await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch { return _process.HasExited; }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await Connection.DisposeAsync().ConfigureAwait(false);
        try { _process.Dispose(); } catch { /* already gone */ }
    }
}
