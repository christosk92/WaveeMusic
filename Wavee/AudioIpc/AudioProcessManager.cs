using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Wavee.AudioIpc;

/// <summary>
/// Connection state of the audio process.
/// </summary>
public enum AudioProcessState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    Failed
}

/// <summary>
/// Manages the lifecycle of the Wavee.AudioHost child process.
/// Launches the process, establishes Named Pipe IPC, monitors health via
/// heartbeats, auto-restarts on crash with exponential backoff, and surfaces
/// connection state changes for UI notifications.
/// </summary>
public sealed class AudioProcessManager : IAsyncDisposable
{
    private readonly ILogger? _logger;
    private readonly string _audioHostPath;
    private readonly CancellationTokenSource _cts = new();

    private Process? _process;
    private AudioPipelineProxy? _proxy;

    /// <summary>
    /// PID of the audio host process (0 if not running).
    /// </summary>
    public int ProcessId => _process?.Id ?? 0;
    private string _pipeName = "";
    private bool _disposed;
    private int _restartCount;
    private Timer? _heartbeatTimer;

    // Credentials cached for auto-restart
    private string? _username;
    private byte[]? _storedCredential;
    private string? _deviceId;

    // ── Resilience configuration ──
    private const int MaxRestartAttempts = 5;
    private static readonly TimeSpan[] BackoffDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30)
    ];
    private const int HeartbeatIntervalMs = 5000;
    private const int HeartbeatTimeoutMs = 10000;
    private long _lastPongTimestamp;

    // ── Events for UI layer ──

    /// <summary>
    /// Fires when the audio process connection state changes.
    /// UI should subscribe to show notifications and activity items.
    /// </summary>
    public event Action<AudioProcessState, string>? StateChanged;

    /// <summary>
    /// Current connection state.
    /// </summary>
    public AudioProcessState State { get; private set; } = AudioProcessState.Disconnected;

    /// <summary>
    /// Number of times the process has been restarted in the current session.
    /// </summary>
    public int RestartCount => _restartCount;

    /// <summary>
    /// The IPlaybackEngine proxy connected to the audio process.
    /// </summary>
    public AudioPipelineProxy? Proxy => _proxy;

    /// <summary>
    /// True if the audio process is running and pipe is connected.
    /// </summary>
    public bool IsRunning => _process is { HasExited: false } && (_proxy?.IsConnected ?? false);

    public AudioProcessManager(ILogger? logger = null)
    {
        _logger = logger;

        // Locate Wavee.AudioHost executable — try multiple search paths
        var baseDir = AppContext.BaseDirectory;
        // WinUI packaged output: .../Wavee/Wavee.UI.WinUI/bin/x64/Debug/net10.0-.../win-x64/AppX/
        // Count up: AppX(1)/win-x64(2)/net10.0-...(3)/Debug(4)/x64(5)/bin(6)/Wavee.UI.WinUI(7) = solution dir
        var solutionRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "..", ".."));
        _logger?.LogDebug("AudioHost solutionRoot resolved to: {Root}", solutionRoot);
        var candidates = new[]
        {
            // Same directory (deployed side by side)
            Path.Combine(baseDir, "Wavee.AudioHost.exe"),
            // Solution-relative: Debug build
            Path.Combine(solutionRoot, "Wavee.AudioHost", "bin", "Debug", "net10.0", "Wavee.AudioHost.exe"),
            // Solution-relative: Release build
            Path.Combine(solutionRoot, "Wavee.AudioHost", "bin", "Release", "net10.0", "Wavee.AudioHost.exe"),
            // 4 dirs up (non-packaged layout)
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..",
                "Wavee.AudioHost", "bin", "Debug", "net10.0", "Wavee.AudioHost.exe")),
        };

        _audioHostPath = "";
        foreach (var candidate in candidates)
        {
            _logger?.LogDebug("AudioHost search: {Path} exists={Exists}", candidate, File.Exists(candidate));
            if (File.Exists(candidate))
            {
                _audioHostPath = candidate;
                break;
            }
        }

        if (string.IsNullOrEmpty(_audioHostPath))
        {
            _audioHostPath = candidates[0];
            _logger?.LogWarning("AudioHost executable not found in any search path. BaseDir={BaseDir}", baseDir);
        }
        else
        {
            _logger?.LogInformation("AudioHost found at: {Path}", _audioHostPath);
        }
    }

    /// <summary>
    /// Launches the audio host process and establishes IPC.
    /// Credentials are cached for auto-restart.
    /// </summary>
    public async Task<AudioPipelineProxy> StartAsync(
        string username, byte[] storedCredential, string deviceId,
        CancellationToken ct = default)
    {
        // Cache credentials for auto-restart
        _username = username;
        _storedCredential = storedCredential;
        _deviceId = deviceId;
        _restartCount = 0;

        return await LaunchAndConnectAsync(ct);
    }

    private async Task<AudioPipelineProxy> LaunchAndConnectAsync(CancellationToken ct)
    {
        SetState(AudioProcessState.Connecting, "Starting audio engine...");

        _pipeName = $"WaveeAudio_{Environment.ProcessId}_{Guid.NewGuid():N}";
        _logger?.LogInformation("Launching audio host: {Path} --pipe {Pipe}", _audioHostPath, _pipeName);

        if (!File.Exists(_audioHostPath))
        {
            var msg = $"Audio host not found: {_audioHostPath}";
            _logger?.LogError(msg);
            SetState(AudioProcessState.Failed, msg);
            throw new FileNotFoundException(msg);
        }

        // Start process
        var psi = new ProcessStartInfo
        {
            FileName = _audioHostPath,
            Arguments = $"--pipe {_pipeName}",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        _process = Process.Start(psi);
        if (_process == null)
        {
            SetState(AudioProcessState.Failed, "Failed to start audio process");
            throw new InvalidOperationException("Failed to start audio host process");
        }

        // Assign to a Windows Job Object so Task Manager groups parent + child
        // and the child is automatically killed if the parent crashes.
        AssignToJobObject(_process);

        _logger?.LogInformation("Audio host started — PID={Pid}", _process.Id);

        // Monitor process exit for crash detection
        _process.EnableRaisingEvents = true;
        _process.Exited += OnProcessExited;

        _process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null) _logger?.LogDebug("[AudioHost] {Line}", e.Data);
        };
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) _logger?.LogWarning("[AudioHost-err] {Line}", e.Data);
        };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        // Connect via Named Pipe
        var pipeClient = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        try
        {
            _logger?.LogDebug("Connecting to audio host pipe...");
            await pipeClient.ConnectAsync((int)TimeSpan.FromSeconds(10).TotalMilliseconds, ct);
        }
        catch (TimeoutException)
        {
            _logger?.LogError("Pipe connection timed out after 10s");
            SetState(AudioProcessState.Failed, "Audio engine connection timed out");
            await KillProcessAsync();
            throw;
        }

        var transport = new IpcPipeTransport(pipeClient, _logger);
        _proxy = new AudioPipelineProxy(transport, _logger);

        // Subscribe to disconnection for auto-restart
        _proxy.Disconnected += OnProxyDisconnected;
        _proxy.PongReceived += () => _lastPongTimestamp = Stopwatch.GetTimestamp();

        // Handshake
        var success = await _proxy.HandshakeAsync(_username!, _storedCredential!, _deviceId!, ct);
        if (!success)
        {
            SetState(AudioProcessState.Failed, "Audio engine handshake failed");
            await StopAsync();
            throw new InvalidOperationException("Audio host handshake failed");
        }

        // Start receiving state updates
        _proxy.StartReceiving();

        // Start heartbeat timer
        _lastPongTimestamp = Stopwatch.GetTimestamp();
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = new Timer(HeartbeatTick, null, HeartbeatIntervalMs, HeartbeatIntervalMs);

        SetState(AudioProcessState.Connected, "Audio engine connected");
        _logger?.LogInformation("Audio process ready and connected (PID={Pid})", _process.Id);
        return _proxy;
    }

    // ── Health monitoring ──

    private void HeartbeatTick(object? state)
    {
        if (_disposed || _proxy == null || !_proxy.IsConnected) return;

        // Check if last pong is too old
        var elapsed = Stopwatch.GetElapsedTime(_lastPongTimestamp);
        if (elapsed.TotalMilliseconds > HeartbeatTimeoutMs)
        {
            _logger?.LogWarning("Audio host heartbeat timeout ({ElapsedMs:F0}ms since last pong)", elapsed.TotalMilliseconds);
        }

        // Always send ping — even if timed out, to allow recovery
        try
        {
            _ = _proxy.SendPingAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to send heartbeat ping");
        }
    }

    // ── Crash detection & auto-restart ──

    private void OnProcessExited(object? sender, EventArgs e)
    {
        if (_disposed) return;

        var exitCode = -1;
        try { exitCode = _process?.ExitCode ?? -1; } catch { }

        _logger?.LogWarning("Audio host process exited (code={ExitCode}, restarts={Restarts}/{Max})",
            exitCode, _restartCount, MaxRestartAttempts);

        // The proxy disconnected event will trigger restart via OnProxyDisconnected
    }

    private void OnProxyDisconnected(string reason)
    {
        if (_disposed) return;

        _logger?.LogWarning("Audio proxy disconnected: {Reason}", reason);
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;

        // Attempt auto-restart
        _ = TryRestartAsync();
    }

    private async Task TryRestartAsync()
    {
        if (_disposed || _username == null || _storedCredential == null || _deviceId == null)
        {
            SetState(AudioProcessState.Failed, "Cannot restart — no cached credentials");
            return;
        }

        if (_restartCount >= MaxRestartAttempts)
        {
            _logger?.LogError("Audio host exceeded max restart attempts ({Max})", MaxRestartAttempts);
            SetState(AudioProcessState.Failed,
                $"Audio engine failed after {MaxRestartAttempts} restart attempts. Restart the app to try again.");
            return;
        }

        var delay = BackoffDelays[Math.Min(_restartCount, BackoffDelays.Length - 1)];
        _restartCount++;

        _logger?.LogInformation("Auto-restarting audio host in {Delay}s (attempt {N}/{Max})",
            delay.TotalSeconds, _restartCount, MaxRestartAttempts);
        SetState(AudioProcessState.Reconnecting,
            $"Audio engine restarting (attempt {_restartCount}/{MaxRestartAttempts})...");

        // Clean up old process
        await CleanupCurrentAsync();

        // Wait with backoff
        try
        {
            await Task.Delay(delay, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // Restart
        try
        {
            await LaunchAndConnectAsync(_cts.Token);
            _logger?.LogInformation("Audio host restarted successfully (attempt {N})", _restartCount);

            // Re-wire the proxy into the executor (it has a new proxy instance)
            ProxyRestarted?.Invoke(_proxy!);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Audio host restart failed (attempt {N})", _restartCount);
            // Will retry via the next disconnect event or give up if at max
            if (_restartCount < MaxRestartAttempts)
            {
                SetState(AudioProcessState.Reconnecting,
                    $"Restart failed, retrying... ({_restartCount}/{MaxRestartAttempts})");
                _ = TryRestartAsync();
            }
            else
            {
                SetState(AudioProcessState.Failed,
                    "Audio engine failed to restart. Restart the app to try again.");
            }
        }
    }

    /// <summary>
    /// Fires when the proxy is replaced after a successful restart.
    /// The UI layer should re-wire the new proxy into ConnectCommandExecutor and PlaybackStateManager.
    /// </summary>
    public event Action<AudioPipelineProxy>? ProxyRestarted;

    // ── Helpers ──

    private void SetState(AudioProcessState state, string message)
    {
        State = state;
        _logger?.LogInformation("AudioProcess state: {State} — {Message}", state, message);
        StateChanged?.Invoke(state, message);
    }

    private async Task CleanupCurrentAsync()
    {
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;

        if (_proxy != null)
        {
            _proxy.Disconnected -= OnProxyDisconnected;
            await _proxy.DisposeAsync();
            _proxy = null;
        }

        await KillProcessAsync();
    }

    private async Task KillProcessAsync()
    {
        if (_process is { HasExited: false })
        {
            _logger?.LogDebug("Killing audio host PID={Pid}", _process.Id);
            try { _process.Kill(entireProcessTree: true); }
            catch { /* already exited */ }

            // Wait briefly for exit
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await _process.WaitForExitAsync(cts.Token);
            }
            catch { /* timeout, already killed */ }
        }
        _process?.Dispose();
        _process = null;
    }

    public async Task StopAsync()
    {
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;

        if (_proxy != null)
        {
            _proxy.Disconnected -= OnProxyDisconnected;
            try
            {
                await _proxy.ShutdownAsync(CancellationToken.None);
                await Task.Delay(500);
            }
            catch { /* best-effort */ }
            await _proxy.DisposeAsync();
            _proxy = null;
        }

        await KillProcessAsync();
        SetState(AudioProcessState.Disconnected, "Audio engine stopped");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        await StopAsync();
        _jobHandle?.Dispose();
        _cts.Dispose();
    }

    // ── Windows Job Object (groups child process under parent in Task Manager) ──

    private SafeHandle? _jobHandle;

    private void AssignToJobObject(Process process)
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            var job = CreateJobObjectW(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return;

            // Configure: kill child when job (parent) closes
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = 0x2000 // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                }
            };
            var size = Marshal.SizeOf(info);
            var ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                SetInformationJobObject(job, 9 /* ExtendedLimitInformation */, ptr, (uint)size);
            }
            finally { Marshal.FreeHGlobal(ptr); }

            AssignProcessToJobObject(job, process.Handle);
            _jobHandle = new JobSafeHandle(job);
            _logger?.LogDebug("Child process assigned to Job Object (grouped in Task Manager)");
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to assign Job Object (non-fatal)");
        }
    }

    private sealed class JobSafeHandle : SafeHandle
    {
        public JobSafeHandle(IntPtr handle) : base(IntPtr.Zero, true) => SetHandle(handle);
        public override bool IsInvalid => handle == IntPtr.Zero;
        protected override bool ReleaseHandle() => CloseHandle(handle);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr hObject);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int jobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public long Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }
}
