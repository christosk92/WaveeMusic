using Microsoft.Extensions.Logging;
using Serilog;
using Wavee.AudioHost;
using Wavee.AudioHost.NativeDeps;

// Parse arguments: --pipe <name>
var pipeName = "WaveeAudio";

for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--pipe")
        pipeName = args[++i];
}

// Configure Serilog for the audio process
var logPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "Wavee", "Logs", "audiohost-.log");

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console()
    .WriteTo.File(
        logPath,
        rollingInterval: Serilog.RollingInterval.Day,
        retainedFileCountLimit: 7,
        fileSizeLimitBytes: 10 * 1024 * 1024)
    .CreateLogger();

var loggerFactory = LoggerFactory.Create(builder =>
    builder.AddSerilog(Log.Logger, dispose: false));
var logger = loggerFactory.CreateLogger<AudioHostService>();

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

    await using var host = new AudioHostService(pipeName, logger);
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
