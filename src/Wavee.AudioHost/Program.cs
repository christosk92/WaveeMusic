using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Serilog;
using Wavee.AudioHost;
using Wavee.AudioHost.NativeDeps;
using ILogger = Microsoft.Extensions.Logging.ILogger;

// Parse arguments: --pipe <name> [--verbose]
var pipeName = "WaveeAudio";
var verbose = false;
var standaloneDev = false;
int parentPid = 0;
string? sessionId = null;
string? launchToken = null;

for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--pipe" && i + 1 < args.Length)
        pipeName = args[++i];
    else if (args[i] == "--parent-pid" && i + 1 < args.Length && int.TryParse(args[++i], out var pid))
        parentPid = pid;
    else if (args[i] == "--session-id" && i + 1 < args.Length)
        sessionId = args[++i];
    else if (args[i] == "--launch-token" && i + 1 < args.Length)
        launchToken = args[++i];
    else if (args[i] == "--verbose")
        verbose = true;
    else if (args[i] == "--standalone-dev")
        standaloneDev = true;
}

// Configure Serilog for the audio process
var logPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "Wavee", "Logs", "audiohost-.log");

var loggerCfg = new LoggerConfiguration();
if (verbose)
    loggerCfg = loggerCfg.MinimumLevel.Verbose();
else
    loggerCfg = loggerCfg.MinimumLevel.Information();

const string fileTemplate =
    "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

Log.Logger = loggerCfg
    .WriteTo.Console()
    .WriteTo.File(
        logPath,
        rollingInterval: Serilog.RollingInterval.Day,
        retainedFileCountLimit: 7,
        fileSizeLimitBytes: 10 * 1024 * 1024,
        outputTemplate: fileTemplate)
    .CreateLogger();

var loggerFactory = LoggerFactory.Create(builder =>
    builder.AddSerilog(Log.Logger, dispose: false));
var logger = loggerFactory.CreateLogger<AudioHostService>();

SetWaveeAppUserModelId();

if (!standaloneDev
    && (parentPid <= 0 || string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(launchToken)))
{
    logger.LogCritical(
        "AudioHost refused standalone startup. Required args: --parent-pid, --session-id, --launch-token. Use --standalone-dev for manual diagnostics.");
    Environment.ExitCode = 4;
    Log.CloseAndFlush();
    return;
}

logger.LogInformation("Wavee AudioHost starting — PID={Pid}, pipe={Pipe}",
    Environment.ProcessId, pipeName);

// Suppress Gen2 compacting GC for the entire audio process
System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

using var parentMonitorRegistration = StartParentMonitor(parentPid, standaloneDev, logger, cts);

try
{
    // Ensure native audio dependencies exist on this platform. Each descriptor's AppliesTo
    // gate makes non-applicable entries a near-instant no-op:
    //   - PortAudio ARM64: downloads portaudio.dll on Windows ARM64 (PortAudioSharp2 1.0.6 has no win-arm64 binary)
    //   - BASS x64: downloads bass.dll on Windows x64 (ManagedBass 4.0.2 is managed-only, no native payload)
    // On failure we write a marker file and exit with code 3 so the UI can distinguish
    // a deterministic "first-run setup" failure from a transient crash.
    var descriptors = new NativeLibraryDescriptor[]
    {
        PortAudioWinArm64Descriptor.Create(),
        BassWinX64Descriptor.Create(),
    };

    using (var bootstrapHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) })
    {
        bootstrapHttp.DefaultRequestHeaders.UserAgent.ParseAdd(
            "WaveeMusic-AudioHost/1.0 (+NativeDepsProvisioner)");

        foreach (var descriptor in descriptors)
        {
            var provisioner = new NativeLibraryProvisioner(logger, bootstrapHttp, descriptor);
            var result = await provisioner.EnsureAvailableAsync(cts.Token);

            if (result.Outcome == NativeLibraryProvisioningOutcome.Failed)
            {
                logger.LogCritical(result.FailureException,
                    "Native dependency provisioning failed: {Reason}", result.FailureReason);
                NativeLibraryFailureMarker.Write(descriptor, result);
                // Exit code 3 is a contract with Wavee.AudioIpc.AudioProcessManager which reads the
                // marker file to surface a specific toast instead of running its 5-retry backoff loop.
                Environment.ExitCode = 3;
                return;
            }

            // Clear any stale marker from a previous failed run so the UI's next detection cycle
            // does not misinterpret an orphan file as a current failure.
            NativeLibraryFailureMarker.TryDelete(descriptor);
        }
    }

    await using var host = new AudioHostService(
        pipeName,
        logger,
        expectedParentProcessId: parentPid,
        expectedSessionId: sessionId,
        expectedLaunchToken: launchToken,
        standaloneDevMode: standaloneDev);
    await host.RunAsync(cts.Token);
}
catch (OperationCanceledException)
{
    logger.LogInformation("AudioHost shutting down");
}
catch (Exception ex)
{
    logger.LogCritical(ex, "AudioHost fatal error");
    Environment.ExitCode = 1;
}
finally
{
    Log.CloseAndFlush();
}

static IDisposable? StartParentMonitor(
    int parentPid,
    bool standaloneDev,
    ILogger logger,
    CancellationTokenSource shutdownCts)
{
    if (standaloneDev || parentPid <= 0) return null;

    try
    {
        var parent = Process.GetProcessById(parentPid);
        _ = Task.Run(async () =>
        {
            try
            {
                await parent.WaitForExitAsync(shutdownCts.Token);
                if (!shutdownCts.IsCancellationRequested)
                {
                    logger.LogWarning("Parent process {ParentPid} exited; shutting down AudioHost", parentPid);
                    shutdownCts.Cancel();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Parent process monitor failed; shutting down AudioHost");
                shutdownCts.Cancel();
            }
        });
        return parent;
    }
    catch (Exception ex)
    {
        logger.LogCritical(ex, "Parent process {ParentPid} is not available; refusing AudioHost startup", parentPid);
        shutdownCts.Cancel();
        return null;
    }
}

static void SetWaveeAppUserModelId()
{
    if (!OperatingSystem.IsWindows()) return;
    try { _ = SetCurrentProcessExplicitAppUserModelID("WaveeMusic"); }
    catch { /* best effort only */ }
}

[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
