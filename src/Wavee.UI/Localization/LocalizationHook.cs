using System;
using System.Globalization;

namespace Wavee.UI.Localization;

/// <summary>
/// Static hook the framework-neutral <c>Wavee.UI</c> layer uses to localize
/// strings without depending on the platform's resource API. The platform
/// shell (e.g. <c>Wavee.UI.WinUI</c>) wires <see cref="GetString"/> and
/// <see cref="Format"/> at startup; until then both fall back to the key
/// itself (English-only).
///
/// Keep this surface tiny — DTOs / formatters should call these helpers for
/// any user-facing string. Do NOT add reactive / async patterns here; the
/// callers expect a synchronous string.
/// </summary>
public static class LocalizationHook
{
    private static Func<string, string>? _getString;
    private static Func<string, object?[], string>? _format;

    public static void Configure(
        Func<string, string> getString,
        Func<string, object?[], string> format)
    {
        _getString = getString;
        _format = format;
    }

    public static string GetString(string key)
        => _getString?.Invoke(key) ?? key;

    public static string Format(string key, params object?[] args)
    {
        if (_format != null)
            return _format(key, args);
        // Fallback: return the format key with args substituted via invariant culture
        var fmt = _getString?.Invoke(key) ?? key;
        return args.Length == 0 ? fmt : string.Format(CultureInfo.CurrentUICulture, fmt, args);
    }
}
