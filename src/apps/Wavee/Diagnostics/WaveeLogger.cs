using System;
using System.Runtime.CompilerServices;

namespace Wavee;

// The category-bound logging facade the whole app emits through. `default` (== WaveeLogger.Null) is a safe no-op, so an
// unwired seam or a test double never NREs. The interpolated-handler overloads gate on level BEFORE building the message:
// when the level is filtered no interpolation runs at all (no ToString, no builder), so a `_log.Debug($"...")` at a hot
// site costs a level compare when off. Plain-string overloads carry constant/pre-built messages and structured Event().

/// <summary>Category-bound logger facade over <see cref="IWaveeLog"/>. <c>default</c> is a safe no-op.</summary>
public readonly struct WaveeLogger
{
    readonly IWaveeLog? _log;
    readonly string? _category;

    public WaveeLogger(IWaveeLog? log, string category) { _log = log; _category = category; }

    public static WaveeLogger Null => default;
    public string Category => _category ?? "app";
    public IWaveeLog? Sink => _log;                       // root plumbing / child construction
    public WaveeLogger With(string category) => new(_log, category);
    public bool IsEnabled(WaveeLogLevel level) => _log is { } l && l.IsEnabled(level);

    // Plain-string overloads (constant or pre-built messages).
    public void Trace(string message) => _log?.Log(WaveeLogLevel.Trace, Category, message);
    public void Debug(string message) => _log?.Log(WaveeLogLevel.Debug, Category, message);
    public void Info(string message) => _log?.Log(WaveeLogLevel.Info, Category, message);
    public void Warn(string message, Exception? ex = null) => _log?.Log(WaveeLogLevel.Warning, Category, message, ex);
    public void Error(string message, Exception? ex = null) => _log?.Log(WaveeLogLevel.Error, Category, message, ex);
    public void Critical(string message, Exception? ex = null) => _log?.Log(WaveeLogLevel.Critical, Category, message, ex);

    // Interpolated-handler overloads — the message is NOT built when the level is filtered.
    public void Trace([InterpolatedStringHandlerArgument("")] ref TraceHandler message)
        { if (message.Core.Enabled) _log!.Log(WaveeLogLevel.Trace, Category, message.Core.ToStringAndClear()); }
    public void Debug([InterpolatedStringHandlerArgument("")] ref DebugHandler message)
        { if (message.Core.Enabled) _log!.Log(WaveeLogLevel.Debug, Category, message.Core.ToStringAndClear()); }
    public void Info([InterpolatedStringHandlerArgument("")] ref InfoHandler message)
        { if (message.Core.Enabled) _log!.Log(WaveeLogLevel.Info, Category, message.Core.ToStringAndClear()); }
    public void Warn([InterpolatedStringHandlerArgument("")] ref WarnHandler message, Exception? ex = null)
        { if (message.Core.Enabled) _log!.Log(WaveeLogLevel.Warning, Category, message.Core.ToStringAndClear(), ex); }
    public void Error([InterpolatedStringHandlerArgument("")] ref ErrorHandler message, Exception? ex = null)
        { if (message.Core.Enabled) _log!.Log(WaveeLogLevel.Error, Category, message.Core.ToStringAndClear(), ex); }
    public void Critical([InterpolatedStringHandlerArgument("")] ref CriticalHandler message, Exception? ex = null)
        { if (message.Core.Enabled) _log!.Log(WaveeLogLevel.Critical, Category, message.Core.ToStringAndClear(), ex); }

    // Structured events. The span overload materializes the array ONLY when enabled (C# 14 params-span).
    public void Event(WaveeLogLevel level, string eventId, string message,
        string? operationId = null, long elapsedMs = -1, Exception? ex = null,
        params ReadOnlySpan<WaveeLogField> fields)
    {
        if (_log is not { } l || !l.IsEnabled(level)) return;
        l.Event(level, Category, eventId, message, operationId, elapsedMs, ex, fields.ToArray());
    }

    /// <summary>Adapt this logger to the SDK's transport-agnostic <see cref="Wavee.Sdk.Streams.StreamLogger"/> so the
    /// shared byte-stream building blocks (<c>RangedHttpSource</c>, <c>ChunkDiskCache</c>) log into WaveeLog without the
    /// SDK depending on the app. <c>default</c> stays a no-op on both sides.</summary>
    public static implicit operator Wavee.Sdk.Streams.StreamLogger(WaveeLogger log) =>
        log._log is null ? default : new(new WaveeStreamLogSink(log));
}

/// <summary>The <see cref="WaveeLogger"/> side of the SDK stream-logging seam. Level checks stay upstream, so a
/// filtered-out trace never builds its message.</summary>
sealed class WaveeStreamLogSink(WaveeLogger log) : Wavee.Sdk.Streams.IStreamLogSink
{
    public bool IsEnabled(Wavee.Sdk.Streams.StreamLogLevel level) => log.IsEnabled(Map(level));

    public void Log(Wavee.Sdk.Streams.StreamLogLevel level, string message) => log.Sink?.Log(Map(level), log.Category, message);

    public void Event(Wavee.Sdk.Streams.StreamLogLevel level, string eventId, string message, long elapsedMs,
        ReadOnlySpan<Wavee.Sdk.Streams.StreamLogField> fields)
    {
        var mapped = new WaveeLogField[fields.Length];
        for (int i = 0; i < fields.Length; i++) mapped[i] = new WaveeLogField(fields[i].Name, fields[i].Value);
        log.Event(Map(level), eventId, message, elapsedMs: elapsedMs, fields: mapped);
    }

    static WaveeLogLevel Map(Wavee.Sdk.Streams.StreamLogLevel level) => level switch
    {
        Wavee.Sdk.Streams.StreamLogLevel.Trace => WaveeLogLevel.Trace,
        Wavee.Sdk.Streams.StreamLogLevel.Debug => WaveeLogLevel.Debug,
        Wavee.Sdk.Streams.StreamLogLevel.Warning => WaveeLogLevel.Warning,
        Wavee.Sdk.Streams.StreamLogLevel.Error => WaveeLogLevel.Error,
        Wavee.Sdk.Streams.StreamLogLevel.Critical => WaveeLogLevel.Critical,
        _ => WaveeLogLevel.Info,
    };
}

/// <summary>Shared handler core: check the level up front; build over <see cref="DefaultInterpolatedStringHandler"/>
/// only when enabled.</summary>
public ref struct WaveeLogHandlerCore
{
    DefaultInterpolatedStringHandler _inner;
    public bool Enabled;

    public WaveeLogHandlerCore(int literalLength, int formattedCount, WaveeLogger logger, WaveeLogLevel level, out bool enabled)
    {
        Enabled = enabled = logger.IsEnabled(level);
        _inner = enabled ? new DefaultInterpolatedStringHandler(literalLength, formattedCount) : default;
    }

    public void AppendLiteral(string s) => _inner.AppendLiteral(s);
    public void AppendFormatted<T>(T value) => _inner.AppendFormatted(value);
    public void AppendFormatted<T>(T value, string? format) => _inner.AppendFormatted(value, format);
    public void AppendFormatted<T>(T value, int alignment) => _inner.AppendFormatted(value, alignment);
    public void AppendFormatted<T>(T value, int alignment, string? format) => _inner.AppendFormatted(value, alignment, format);
    public void AppendFormatted(ReadOnlySpan<char> value) => _inner.AppendFormatted(value);
    public void AppendFormatted(string? value) => _inner.AppendFormatted(value);
    public string ToStringAndClear() => _inner.ToStringAndClear();
}

[InterpolatedStringHandler]
public ref struct TraceHandler
{
    public WaveeLogHandlerCore Core;
    public TraceHandler(int literalLength, int formattedCount, WaveeLogger logger, out bool enabled)
        => Core = new WaveeLogHandlerCore(literalLength, formattedCount, logger, WaveeLogLevel.Trace, out enabled);
    public void AppendLiteral(string s) => Core.AppendLiteral(s);
    public void AppendFormatted<T>(T value) => Core.AppendFormatted(value);
    public void AppendFormatted<T>(T value, string? format) => Core.AppendFormatted(value, format);
    public void AppendFormatted<T>(T value, int alignment) => Core.AppendFormatted(value, alignment);
    public void AppendFormatted<T>(T value, int alignment, string? format) => Core.AppendFormatted(value, alignment, format);
    public void AppendFormatted(ReadOnlySpan<char> value) => Core.AppendFormatted(value);
    public void AppendFormatted(string? value) => Core.AppendFormatted(value);
}

[InterpolatedStringHandler]
public ref struct DebugHandler
{
    public WaveeLogHandlerCore Core;
    public DebugHandler(int literalLength, int formattedCount, WaveeLogger logger, out bool enabled)
        => Core = new WaveeLogHandlerCore(literalLength, formattedCount, logger, WaveeLogLevel.Debug, out enabled);
    public void AppendLiteral(string s) => Core.AppendLiteral(s);
    public void AppendFormatted<T>(T value) => Core.AppendFormatted(value);
    public void AppendFormatted<T>(T value, string? format) => Core.AppendFormatted(value, format);
    public void AppendFormatted<T>(T value, int alignment) => Core.AppendFormatted(value, alignment);
    public void AppendFormatted<T>(T value, int alignment, string? format) => Core.AppendFormatted(value, alignment, format);
    public void AppendFormatted(ReadOnlySpan<char> value) => Core.AppendFormatted(value);
    public void AppendFormatted(string? value) => Core.AppendFormatted(value);
}

[InterpolatedStringHandler]
public ref struct InfoHandler
{
    public WaveeLogHandlerCore Core;
    public InfoHandler(int literalLength, int formattedCount, WaveeLogger logger, out bool enabled)
        => Core = new WaveeLogHandlerCore(literalLength, formattedCount, logger, WaveeLogLevel.Info, out enabled);
    public void AppendLiteral(string s) => Core.AppendLiteral(s);
    public void AppendFormatted<T>(T value) => Core.AppendFormatted(value);
    public void AppendFormatted<T>(T value, string? format) => Core.AppendFormatted(value, format);
    public void AppendFormatted<T>(T value, int alignment) => Core.AppendFormatted(value, alignment);
    public void AppendFormatted<T>(T value, int alignment, string? format) => Core.AppendFormatted(value, alignment, format);
    public void AppendFormatted(ReadOnlySpan<char> value) => Core.AppendFormatted(value);
    public void AppendFormatted(string? value) => Core.AppendFormatted(value);
}

[InterpolatedStringHandler]
public ref struct WarnHandler
{
    public WaveeLogHandlerCore Core;
    public WarnHandler(int literalLength, int formattedCount, WaveeLogger logger, out bool enabled)
        => Core = new WaveeLogHandlerCore(literalLength, formattedCount, logger, WaveeLogLevel.Warning, out enabled);
    public void AppendLiteral(string s) => Core.AppendLiteral(s);
    public void AppendFormatted<T>(T value) => Core.AppendFormatted(value);
    public void AppendFormatted<T>(T value, string? format) => Core.AppendFormatted(value, format);
    public void AppendFormatted<T>(T value, int alignment) => Core.AppendFormatted(value, alignment);
    public void AppendFormatted<T>(T value, int alignment, string? format) => Core.AppendFormatted(value, alignment, format);
    public void AppendFormatted(ReadOnlySpan<char> value) => Core.AppendFormatted(value);
    public void AppendFormatted(string? value) => Core.AppendFormatted(value);
}

[InterpolatedStringHandler]
public ref struct ErrorHandler
{
    public WaveeLogHandlerCore Core;
    public ErrorHandler(int literalLength, int formattedCount, WaveeLogger logger, out bool enabled)
        => Core = new WaveeLogHandlerCore(literalLength, formattedCount, logger, WaveeLogLevel.Error, out enabled);
    public void AppendLiteral(string s) => Core.AppendLiteral(s);
    public void AppendFormatted<T>(T value) => Core.AppendFormatted(value);
    public void AppendFormatted<T>(T value, string? format) => Core.AppendFormatted(value, format);
    public void AppendFormatted<T>(T value, int alignment) => Core.AppendFormatted(value, alignment);
    public void AppendFormatted<T>(T value, int alignment, string? format) => Core.AppendFormatted(value, alignment, format);
    public void AppendFormatted(ReadOnlySpan<char> value) => Core.AppendFormatted(value);
    public void AppendFormatted(string? value) => Core.AppendFormatted(value);
}

[InterpolatedStringHandler]
public ref struct CriticalHandler
{
    public WaveeLogHandlerCore Core;
    public CriticalHandler(int literalLength, int formattedCount, WaveeLogger logger, out bool enabled)
        => Core = new WaveeLogHandlerCore(literalLength, formattedCount, logger, WaveeLogLevel.Critical, out enabled);
    public void AppendLiteral(string s) => Core.AppendLiteral(s);
    public void AppendFormatted<T>(T value) => Core.AppendFormatted(value);
    public void AppendFormatted<T>(T value, string? format) => Core.AppendFormatted(value, format);
    public void AppendFormatted<T>(T value, int alignment) => Core.AppendFormatted(value, alignment);
    public void AppendFormatted<T>(T value, int alignment, string? format) => Core.AppendFormatted(value, alignment, format);
    public void AppendFormatted(ReadOnlySpan<char> value) => Core.AppendFormatted(value);
    public void AppendFormatted(string? value) => Core.AppendFormatted(value);
}
