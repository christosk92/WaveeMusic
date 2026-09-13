using System;
using Wavee;
using Xunit;

namespace Wavee.Tests;

// The category-bound WaveeLogger facade: default no-op, level-gated interpolation (no build when filtered), and the
// structured Event() array materialization gate.
public class WaveeLoggerTests
{
    [Fact]
    public void Default_IsNoOp_NeverThrows()
    {
        WaveeLogger log = default;
        log.Info("hi");
        log.Warn("uh oh", new InvalidOperationException());
        log.Debug($"interp {41 + 1}");
        log.Event(WaveeLogLevel.Error, "ev", "msg");
        Assert.False(log.IsEnabled(WaveeLogLevel.Critical));
        Assert.Equal("app", log.Category);
    }

    [Fact]
    public void Enabled_CapturesExactLevelCategoryAndMessage()
    {
        var sink = new CapturingWaveeLog();
        var log = new WaveeLogger(sink, "connect");

        log.Info($"dealer up n={3}");

        var e = sink.Last;
        Assert.Equal(WaveeLogLevel.Info, e.Level);
        Assert.Equal("connect", e.Category);
        Assert.Equal("dealer up n=3", e.Message);
    }

    [Fact]
    public void Filtered_DoesNotBuildTheMessage_NorCallSink()
    {
        var sink = new CapturingWaveeLog { MinLevel = WaveeLogLevel.Warning };
        var log = new WaveeLogger(sink, "audio");
        var probe = new CountingFormattable();

        log.Debug($"expensive {probe}");   // Debug < Warning → filtered

        Assert.Equal(0, probe.Count);      // interpolation never ran
        Assert.Equal(0, sink.Count);       // no sink call
    }

    [Fact]
    public void Enabled_BuildsTheInterpolatedMessage()
    {
        var sink = new CapturingWaveeLog { MinLevel = WaveeLogLevel.Trace };
        var log = new WaveeLogger(sink, "audio");
        var probe = new CountingFormattable();

        log.Info($"value {probe}");

        Assert.Equal(1, probe.Count);
        Assert.Equal("value built", sink.Last.Message);
    }

    [Fact]
    public void ExceptionOverloads_PassTheException()
    {
        var sink = new CapturingWaveeLog();
        var log = new WaveeLogger(sink, "x");
        var ex = new InvalidOperationException("boom");

        log.Error("failed", ex);

        Assert.Equal(WaveeLogLevel.Error, sink.Last.Level);
        Assert.Same(ex, sink.Last.Ex);
    }

    [Fact]
    public void Event_MaterializesFieldsOnlyWhenEnabled()
    {
        var sink = new CapturingWaveeLog { MinLevel = WaveeLogLevel.Error };
        var log = new WaveeLogger(sink, "audio");

        log.Event(WaveeLogLevel.Info, "ev", "msg", fields: [WaveeLogField.Of("k", "v")]);   // Info < Error → dropped
        Assert.Equal(0, sink.Count);

        log.Event(WaveeLogLevel.Error, "ev", "kept", fields: [WaveeLogField.Of("k", "v")]);
        Assert.Equal(1, sink.Count);
        Assert.Equal("kept", sink.Last.Message);
        Assert.Equal("ev", sink.Last.EventId);
    }

    [Fact]
    public void With_RebindsTheCategory()
    {
        var sink = new CapturingWaveeLog();
        var log = new WaveeLogger(sink, "connect").With("lyrics");

        log.Info("hi");

        Assert.Equal("lyrics", sink.Last.Category);
    }
}
