using Microsoft.Extensions.Logging;
using Serilog;
using Wavee.AudioHost;

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
