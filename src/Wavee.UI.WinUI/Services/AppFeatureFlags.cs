using System;

namespace Wavee.UI.WinUI.Services;

public static class AppFeatureFlags
{
    public const string LocalFilesEnvironmentVariable = "WAVEE_ENABLE_LOCAL_FILES";

#if DEBUG
    public const bool DiagnosticsEnabled = true;
#else
    public const bool DiagnosticsEnabled = false;
#endif

#if WAVEE_ENABLE_LOCAL_FILES
    private const bool LocalFilesBuildDefault = true;
#else
    private const bool LocalFilesBuildDefault = false;
#endif

    private static readonly Lazy<bool> LocalFilesEnabledValue = new(() =>
    {
        var value = Environment.GetEnvironmentVariable(LocalFilesEnvironmentVariable);
        return TryParseFlag(value, out var enabled) ? enabled : LocalFilesBuildDefault;
    });

    public static bool LocalFilesEnabled => LocalFilesEnabledValue.Value;

    private static bool TryParseFlag(string? value, out bool enabled)
    {
        enabled = false;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        switch (value.Trim().ToLowerInvariant())
        {
            case "1":
            case "true":
            case "yes":
            case "y":
            case "on":
            case "enabled":
                enabled = true;
                return true;

            case "0":
            case "false":
            case "no":
            case "n":
            case "off":
            case "disabled":
                enabled = false;
                return true;

            default:
                return false;
        }
    }
}
