using System;
using System.Globalization;
using System.Text;

namespace Wavee;

// Production observability, the app-side seam. AOT-safe (no reflection, no Newtonsoft). The engine's `Diag` is
// debug-gated (compiled out in Release); THIS log is always-on, structured, ring-buffered, and surfaced in the
// in-app Diagnostics page. Wire `Diag.Sink = WaveeLog.DiagSink` in dev to fold engine diagnostics into the same stream.

public enum WaveeLogLevel : byte { Trace, Debug, Info, Warning, Error, Critical }

/// <summary>One bounded key/value attached to a log event. Values are already rendered and safe to store.</summary>
public readonly record struct WaveeLogField(string Name, string Value)
{
    public static WaveeLogField Of(string name, string? value) => new(name, value ?? "");
    public static WaveeLogField Of(string name, int value) => new(name, value.ToString(CultureInfo.InvariantCulture));
    public static WaveeLogField Of(string name, long value) => new(name, value.ToString(CultureInfo.InvariantCulture));
    public static WaveeLogField Of(string name, double value) => new(name, value.ToString(CultureInfo.InvariantCulture));
    public static WaveeLogField Of(string name, bool value) => new(name, value ? "true" : "false");
    public static WaveeLogField Secret(string name) => new(name, "***");

    public void AppendTo(StringBuilder sb)
    {
        sb.Append(Name).Append('=');
        AppendValue(sb, Value);
    }

    static void AppendValue(StringBuilder sb, string value)
    {
        bool quote = value.Length == 0;
        for (int i = 0; i < value.Length && !quote; i++)
        {
            char c = value[i];
            quote = char.IsWhiteSpace(c) || c is '"' or '=' or '|';
        }

        if (!quote) { sb.Append(value); return; }

        sb.Append('"');
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c is '"' or '\\') sb.Append('\\');
            sb.Append(c);
        }
        sb.Append('"');
    }
}

/// <summary>One structured log record kept in the UI ring and the local file sink.</summary>
public readonly record struct WaveeLogEntry(
    long Sequence,
    long UnixMs,
    WaveeLogLevel Level,
    string Category,
    string EventId,
    string Message,
    string? OperationId,
    int ThreadId,
    long ElapsedMs,
    WaveeLogField[]? Fields,
    string? Exception)
{
    public int FieldCount => Fields?.Length ?? 0;

    public string Format()
    {
        char l = Level switch
        {
            WaveeLogLevel.Trace => 'T', WaveeLogLevel.Debug => 'D', WaveeLogLevel.Info => 'I',
            WaveeLogLevel.Warning => 'W', WaveeLogLevel.Error => 'E', _ => 'C',
        };

        var sb = new StringBuilder(96 + Message.Length);
        sb.Append(l).Append(" [").Append(Category).Append(']');
        if (EventId.Length > 0) sb.Append(' ').Append(EventId);
        if (OperationId is { Length: > 0 }) sb.Append(" op=").Append(OperationId);
        if (ElapsedMs >= 0) sb.Append(" elapsed=").Append(ElapsedMs).Append("ms");
        bool hasEventHeader = EventId.Length > 0 || OperationId is { Length: > 0 } || ElapsedMs >= 0;
        if (Message.Length > 0) sb.Append(hasEventHeader ? " - " : " ").Append(Message);

        if (Fields is { Length: > 0 } fields)
        {
            for (int i = 0; i < fields.Length; i++)
            {
                sb.Append(' ');
                fields[i].AppendTo(sb);
            }
        }

        if (Exception is { Length: > 0 } ex) sb.Append(" | ").Append(ex);
        return sb.ToString();
    }
}

public interface IWaveeLog
{
    void Log(WaveeLogLevel level, string category, string message, Exception? ex = null);
    void Event(WaveeLogLevel level, string category, string eventId, string message,
        string? operationId = null, long elapsedMs = -1, Exception? ex = null, params WaveeLogField[] fields);
    // The master level gate the WaveeLogger facade consults before building any message. Default true so existing test
    // fakes keep compiling; WaveeLog returns level >= MinLevel (its Write() short-circuit).
    bool IsEnabled(WaveeLogLevel level) => true;
}

public static class WaveeLogExtensions
{
    public static void Trace(this IWaveeLog l, string cat, string msg) => l.Log(WaveeLogLevel.Trace, cat, msg);
    public static void Debug(this IWaveeLog l, string cat, string msg) => l.Log(WaveeLogLevel.Debug, cat, msg);
    public static void Info(this IWaveeLog l, string cat, string msg) => l.Log(WaveeLogLevel.Info, cat, msg);
    public static void Warn(this IWaveeLog l, string cat, string msg, Exception? ex = null) => l.Log(WaveeLogLevel.Warning, cat, msg, ex);
    public static void Error(this IWaveeLog l, string cat, string msg, Exception? ex = null) => l.Log(WaveeLogLevel.Error, cat, msg, ex);
    public static void Critical(this IWaveeLog l, string cat, string msg, Exception? ex = null) => l.Log(WaveeLogLevel.Critical, cat, msg, ex);

    public static void Info(this IWaveeLog l, string cat, string eventId, string msg, params WaveeLogField[] fields) =>
        l.Event(WaveeLogLevel.Info, cat, eventId, msg, fields: fields);
    public static void Debug(this IWaveeLog l, string cat, string eventId, string msg, params WaveeLogField[] fields) =>
        l.Event(WaveeLogLevel.Debug, cat, eventId, msg, fields: fields);
    public static void Warn(this IWaveeLog l, string cat, string eventId, string msg, params WaveeLogField[] fields) =>
        l.Event(WaveeLogLevel.Warning, cat, eventId, msg, fields: fields);
    public static void Error(this IWaveeLog l, string cat, string eventId, string msg, params WaveeLogField[] fields) =>
        l.Event(WaveeLogLevel.Error, cat, eventId, msg, fields: fields);
    public static void Error(this IWaveeLog l, string cat, string eventId, string msg, Exception? ex, params WaveeLogField[] fields) =>
        l.Event(WaveeLogLevel.Error, cat, eventId, msg, ex: ex, fields: fields);
}

/// <summary>A PII wrapper whose <see cref="ToString"/> never reveals the value (tokens, credentials). Call
/// <see cref="Reveal"/> only at the point of use; logging it prints <c>***</c>.</summary>
public readonly struct Secret(string value)
{
    readonly string _value = value;
    public string Reveal() => _value;
    public override string ToString() => "***";
}

public static class WaveeLogRedaction
{
    public static string MaskUserId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        if (value.Length <= 6) return "***";
        return value[..3] + "***" + value[^2..];
    }

    public static string UrlHost(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        return Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "";
    }

    public static string HashLike(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.Length <= 12 ? value : value[..8] + "...";
    }
}
