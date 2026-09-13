using Wavee;
using Xunit;

namespace Wavee.Tests;

// LogCapturePolicy resolves the persisted "-1 = build default" Settings › Logs level knobs — the one place Program.cs
// (startup) and LogsPanel (the Verbose toggle + Capture level / File log level submenus) agree on what "-1" means.
// No env-var path exists anymore (CLAUDE.md forbids behaviour switches via environment variables).
public class LogCapturePolicyTests
{
    const WaveeLogLevel BuildDefault = WaveeLogLevel.Info;

    [Fact]
    public void Resolve_MinusOneIsBuildDefault_ClampsToError()
    {
        Assert.Equal(BuildDefault, LogCapturePolicy.Resolve(-1, BuildDefault));
        Assert.Equal(WaveeLogLevel.Debug, LogCapturePolicy.Resolve((int)WaveeLogLevel.Debug, BuildDefault));
        // Critical (5) and anything past it is never user-selectable — clamps down to Error (4).
        Assert.Equal(WaveeLogLevel.Error, LogCapturePolicy.Resolve((int)WaveeLogLevel.Critical, BuildDefault));
        Assert.Equal(WaveeLogLevel.Error, LogCapturePolicy.Resolve(99, BuildDefault));
    }

    [Fact]
    public void ToSetting_DefaultRoundTripsToMinusOne()
    {
        Assert.Equal(-1, LogCapturePolicy.ToSetting(BuildDefault, BuildDefault));
        Assert.Equal((int)WaveeLogLevel.Trace, LogCapturePolicy.ToSetting(WaveeLogLevel.Trace, BuildDefault));

        // Round trip: Resolve(ToSetting(x)) == x for both the default and a non-default level.
        Assert.Equal(BuildDefault, LogCapturePolicy.Resolve(LogCapturePolicy.ToSetting(BuildDefault, BuildDefault), BuildDefault));
        Assert.Equal(WaveeLogLevel.Warning, LogCapturePolicy.Resolve(LogCapturePolicy.ToSetting(WaveeLogLevel.Warning, BuildDefault), BuildDefault));
    }

    [Fact]
    public void SetVerbose_On_WritesTraceAndPersists()
    {
        var log = new WaveeLog { MinLevel = BuildDefault };
        var settings = new MemoryAppSettings();

        LogCapturePolicy.SetVerbose(log, settings, on: true, BuildDefault);

        Assert.Equal(WaveeLogLevel.Trace, log.MinLevel);
        Assert.True(LogCapturePolicy.IsVerbose(log.MinLevel));
        Assert.Equal((int)WaveeLogLevel.Trace, settings.Get(WaveeSettings.LogMinLevel));
    }

    [Fact]
    public void SetVerbose_Off_RestoresDefaultAndPersistsMinusOne()
    {
        var log = new WaveeLog { MinLevel = WaveeLogLevel.Trace };
        var settings = new MemoryAppSettings();
        settings.Set(WaveeSettings.LogMinLevel, (int)WaveeLogLevel.Trace);

        LogCapturePolicy.SetVerbose(log, settings, on: false, BuildDefault);

        Assert.Equal(BuildDefault, log.MinLevel);
        Assert.False(LogCapturePolicy.IsVerbose(log.MinLevel));
        Assert.Equal(-1, settings.Get(WaveeSettings.LogMinLevel));
    }

    [Fact]
    public void EffectiveFileLevel_IsUpwardOnly()
    {
        // File level BELOW the ring's own MinLevel has no effect — the ring already dropped the entry.
        Assert.Equal(WaveeLogLevel.Info, LogCapturePolicy.EffectiveFileLevel(WaveeLogLevel.Info, WaveeLogLevel.Debug));
        // File level AT or ABOVE MinLevel applies as-is.
        Assert.Equal(WaveeLogLevel.Error, LogCapturePolicy.EffectiveFileLevel(WaveeLogLevel.Debug, WaveeLogLevel.Error));
        Assert.Equal(WaveeLogLevel.Info, LogCapturePolicy.EffectiveFileLevel(WaveeLogLevel.Info, WaveeLogLevel.Info));
    }
}
