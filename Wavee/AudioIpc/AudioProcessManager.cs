using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Wavee.Playback.Contracts;

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
    /// <summary>
    /// When true, the audio host process is started with <c>--verbose</c> so its Serilog
    /// minimum level drops to Verbose. Set by the UI process from the user's settings.
    /// Picked up on the next StartAsync (process restart required for child to re-read it).
    /// </summary>
    public static bool UseVerboseLogging { get; set; }

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
    private int _restartInProgress; // 0 = idle, 1 = restarting (guards against double-trigger)
    private Timer? _heartbeatTimer;

    // Credentials cached for auto-restart
    private string? _username;
    private byte[]? _storedCredential;
    private string? _deviceId;
    private int _initialVolumePercent;
    private string? _audioCacheDirectory;

    // ── Resilience configuration ──
    private const int MaxRestartAttempts = 5;

    /// <summary>
    /// Exit code the child AudioHost uses to signal a deterministic native-dependency
    /// provisioning failure (e.g. offline first-run on Windows ARM64 without portaudio.dll).
    /// Keep in sync with Wavee.AudioHost/Program.cs (Environment.ExitCode = 3) and with
    /// Wavee.AudioHost/NativeDeps/NativeLibraryProvisioner.cs.
    /// </summary>
    private const int ProvisioningFailedExitCode = 3;
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

        // Locate Wavee.AudioHost executable by parsing the WinUI output layout.
        // Expected: {solutionDir}/Wavee.UI.WinUI/bin/[{Platform}/]{Config}/{Tfm}/AppX/
        // (Note: .NET 10 WinUI output has NO RID subfolder between Tfm and AppX.)
        var baseDir = AppContext.BaseDirectory;
        var (solutionRoot, platform, config) = ResolveBuildLayout(baseDir);
        _logger?.LogDebug(
            "AudioHost layout resolved: solutionRoot={Root} platform={Platform} config={Config}",
            solutionRoot ?? "<none>", platform ?? "<none>", config ?? "<none>");

        var candidates = new List<string>
        {
            // Same directory (deployed side by side, e.g. packaged AppX)
            Path.Combine(baseDir, "Wavee.AudioHost.exe"),
        };

        if (solutionRoot is not null)
        {
            var audioHostBin = Path.Combine(solutionRoot, "Wavee.AudioHost", "bin");

            // AudioHost is x64-only (loads Spotify.dll directly for PlayPlay).
            // Build output is always bin/x64/{Config}/net10.0/win-x64/ (or
            // bin/{Config}/... when invoked without -p:Platform). Don't probe
            // bin/<host-platform>/ — on ARM64 builds that path can contain a
            // half-built output from a stale build pipeline.
            var configs = config is not null
                ? new[] { config, config == "Debug" ? "Release" : "Debug" }
                : new[] { "Debug", "Release" };

            foreach (var cfg in configs)
            {
                candidates.Add(Path.Combine(audioHostBin, "x64", cfg, "net10.0", "win-x64", "Wavee.AudioHost.exe"));
                candidates.Add(Path.Combine(audioHostBin, cfg, "net10.0", "win-x64", "Wavee.AudioHost.exe"));
            }
        }

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
    /// Parses the WinUI build output layout to recover the solution root, MSBuild Platform,
    /// and Configuration from <see cref="AppContext.BaseDirectory"/>.
    /// Expected layout: {solutionDir}/Wavee.UI.WinUI/bin/[{Platform}/]{Config}/{Tfm}/AppX/
    /// Returns (null, null, null) if the layout is unrecognized (e.g., a packaged install).
    /// </summary>
    private static (string? solutionRoot, string? platform, string? config) ResolveBuildLayout(string baseDir)
    {
        // Walk up until we find a directory named "bin".
        var dir = new DirectoryInfo(baseDir);
        while (dir is not null && !string.Equals(dir.Name, "bin", StringComparison.OrdinalIgnoreCase))
        {
            dir = dir.Parent;
        }
        if (dir?.Parent?.Parent is null)
        {
            return (null, null, null);
        }

        var binDir = dir.FullName;
        var solutionRoot = dir.Parent.Parent.FullName; // bin/.. = project dir, ../.. = solution dir

        // Path segments between bin/ and baseDir are: [Platform?] [Config] [Tfm] [AppX?]
        var relative = Path.GetRelativePath(binDir, baseDir);
        var parts = relative.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        string? platform = null;
        string? config = null;
        if (parts.Length >= 1)
        {
            // If parts[0] is a known Config, no Platform layer is present (AnyCPU).
            if (IsKnownConfig(parts[0]))
            {
                config = parts[0];
            }
            else if (parts.Length >= 2)
            {
                platform = parts[0];
                config = parts[1];
            }
        }

        return (solutionRoot, platform, config);

        static bool IsKnownConfig(string s) =>
            string.Equals(s, "Debug", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(s, "Release", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Launches the audio host process and establishes IPC.
    /// Credentials are cached for auto-restart.
    /// </summary>
    public async Task<AudioPipelineProxy> StartAsync(
        string username, byte[] storedCredential, string deviceId,
        int initialVolumePercent = 0,
        string? audioCacheDirectory = null,
        CancellationToken ct = default)
    {
        // Cache config for auto-restart (credentials no longer sent to AudioHost)
        _username = username;
        _storedCredential = storedCredential;
        _deviceId = deviceId;
        _initialVolumePercent = initialVolumePercent;
        _audioCacheDirectory = audioCacheDirectory;
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
            Arguments = UseVerboseLogging
                ? $"--pipe {_pipeName} --verbose"
                : $"--pipe {_pipeName}",
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
        var success = await _proxy.ConfigureAsync(_deviceId!, normalizationEnabled: true,
            initialVolumePercent: _initialVolumePercent,
            audioCacheDirectory: _audioCacheDirectory,
            ct: ct);
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

        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;

        // Exit code 3 is a deterministic native-dependency provisioning failure
        // (e.g. offline first-run on Windows ARM64 with missing portaudio.dll).
        // The AudioHost drops a JSON marker under %LOCALAPPDATA%\Wavee\NativeDeps\ before exiting.
        // Retrying won't help — surface a specific, actionable error and stop the restart loop.
        if (exitCode == ProvisioningFailedExitCode)
        {
            var friendlyMessage = TryReadProvisioningFailureMessage()
                ?? "Audio engine needs first-run setup. Check your connection and click Retry.";
            SetState(AudioProcessState.Failed, friendlyMessage);
            return;
        }

        // Directly trigger restart — don't rely solely on proxy disconnect detection,
        // because the pipe read may not immediately fail on Windows when the server exits.
        _ = TryRestartAsync();
    }

    /// <summary>
    /// Scans %LOCALAPPDATA%\Wavee\NativeDeps\ for any *.failure.json marker files produced by
    /// Wavee.AudioHost/NativeDeps/NativeLibraryFailureMarker and returns a user-facing message
    /// describing the failure. The marker file is deleted after reading so a subsequent
    /// successful retry does not re-trigger this path. Returns null if no marker is present.
    /// </summary>
    private string? TryReadProvisioningFailureMessage()
    {
        try
        {
            var markerDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Wavee", "NativeDeps");
            if (!Directory.Exists(markerDir)) return null;

            var markers = Directory.GetFiles(markerDir, "*.failure.json");
            if (markers.Length == 0) return null;

            // Pick the newest marker (typically only one exists).
            Array.Sort(markers, (a, b) => File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a)));
            var markerPath = markers[0];

            string displayName = "Audio engine";
            string reason = "First-run setup failed";
            try
            {
                var json = File.ReadAllText(markerPath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("displayName", out var d) && d.ValueKind == JsonValueKind.String)
                {
                    var v = d.GetString();
                    if (!string.IsNullOrWhiteSpace(v)) displayName = v!;
                }
                if (doc.RootElement.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String)
                {
                    var v = r.GetString();
                    if (!string.IsNullOrWhiteSpace(v)) reason = v!;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Failed to parse native-deps failure marker {Path}", markerPath);
            }

            // Delete all markers we saw so a successful retry doesn't re-trigger.
            foreach (var path in markers)
            {
                try { File.Delete(path); } catch { /* best effort */ }
            }

            return $"{displayName} setup failed: {reason}. Check your connection and click Retry.";
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Error scanning native-deps failure markers");
            return null;
        }
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
        // Guard against concurrent restart from both OnProcessExited and OnProxyDisconnected
        if (Interlocked.CompareExchange(ref _restartInProgress, 1, 0) != 0)
            return;

        if (_disposed || _username == null || _storedCredential == null || _deviceId == null)
        {
            Interlocked.Exchange(ref _restartInProgress, 0);
            SetState(AudioProcessState.Failed, "Cannot restart — no cached credentials");
            return;
        }

        if (_restartCount >= MaxRestartAttempts)
        {
            Interlocked.Exchange(ref _restartInProgress, 0);
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
            Interlocked.Exchange(ref _restartInProgress, 0);
            return;
        }

        // Restart
        try
        {
            await LaunchAndConnectAsync(_cts.Token);
            Interlocked.Exchange(ref _restartInProgress, 0);
            _logger?.LogInformation("Audio host restarted successfully (attempt {N})", _restartCount);

            // Re-wire the proxy into the executor (it has a new proxy instance)
            ProxyRestarted?.Invoke(_proxy!);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Audio host restart failed (attempt {N})", _restartCount);
            // Will retry or give up if at max
            if (_restartCount < MaxRestartAttempts)
            {
                SetState(AudioProcessState.Reconnecting,
                    $"Restart failed, retrying... ({_restartCount}/{MaxRestartAttempts})");
                // Reset guard before recursive retry
                Interlocked.Exchange(ref _restartInProgress, 0);
                _ = TryRestartAsync();
            }
            else
            {
                Interlocked.Exchange(ref _restartInProgress, 0);
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
