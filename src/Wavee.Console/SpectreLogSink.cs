using Serilog.Core;
using Serilog.Events;

namespace Wavee.Console;

/// <summary>
/// Custom Serilog sink that routes log events to the SpectreUI log panel.
/// </summary>
internal sealed class SpectreLogSink : ILogEventSink
{
    private readonly SpectreUI _ui;

    public SpectreLogSink(SpectreUI ui)
    {
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
    }

    public void Emit(LogEvent logEvent)
    {
        var level = logEvent.Level switch
        {
            LogEventLevel.Verbose => "VRB",
            LogEventLevel.Debug => "DBG",
            LogEventLevel.Information => "INF",
            LogEventLevel.Warning => "WRN",
            LogEventLevel.Error => "ERR",
            LogEventLevel.Fatal => "FTL",
            _ => "???"
        };

        var message = logEvent.RenderMessage();

        // Include exception info if present
        if (logEvent.Exception != null)
        {
            message += $" - {logEvent.Exception.GetType().Name}: {logEvent.Exception.Message}";
        }

        _ui.AddLog(level, message);
    }
}

/// <summary>
/// Extension methods for configuring SpectreLogSink.
/// </summary>
internal static class SpectreLogSinkExtensions
{
    /// <summary>
    /// Adds the SpectreUI sink to the Serilog configuration.
    /// </summary>
    public static Serilog.LoggerConfiguration SpectreUI(
        this Serilog.Configuration.LoggerSinkConfiguration sinkConfiguration,
        SpectreUI ui,
        LogEventLevel restrictedToMinimumLevel = LogEventLevel.Verbose)
    {
        return sinkConfiguration.Sink(new SpectreLogSink(ui), restrictedToMinimumLevel);
    }
}
