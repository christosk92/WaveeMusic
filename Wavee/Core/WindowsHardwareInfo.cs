using System.Runtime.Versioning;

namespace Wavee.Core;

/// <summary>
/// Best-effort host-machine identification for the gabo-receiver context
/// fragments. The desktop Spotify client puts real OEM strings here
/// (manufacturer / model from BIOS) plus the local Windows machine SID;
/// blank or fake values are an obvious anti-fraud signal server-side.
/// </summary>
/// <remarks>
/// Verified by structural decode of spot SAZ session 255_c.txt
/// (Spotify desktop 1.2.88.483, captured 2026-04-28):
///   context_device_desktop {
///     #2 device_manufacturer = "Microsoft Corporation"
///     #3 device_model        = "Microsoft Surface Laptop, 7th Edition"
///     #4 device_id           = "S-1-5-21-1221368434-4198340266-2993361159"
///   }
/// All four values come from the host's BIOS / registry, not from Spotify.
/// </remarks>
public static class WindowsHardwareInfo
{
    /// <summary>
    /// Reads the BIOS-reported system manufacturer (e.g.
    /// <c>"Microsoft Corporation"</c>). Returns an empty string on failure or
    /// non-Windows hosts.
    /// </summary>
    public static string GetManufacturer()
    {
        if (!OperatingSystem.IsWindows()) return string.Empty;
        return ReadBiosKey("SystemManufacturer") ?? string.Empty;
    }

    /// <summary>
    /// Reads the BIOS-reported system product name (e.g.
    /// <c>"Microsoft Surface Laptop, 7th Edition"</c>). Returns empty on
    /// failure or non-Windows.
    /// </summary>
    public static string GetModel()
    {
        if (!OperatingSystem.IsWindows()) return string.Empty;
        return ReadBiosKey("SystemProductName") ?? string.Empty;
    }

    /// <summary>
    /// Returns the local Windows machine SID (e.g.
    /// <c>"S-1-5-21-1221368434-4198340266-2993361159"</c>) — without the
    /// trailing per-user RID. Mirrors what the desktop client puts in
    /// <c>context_device_desktop.device_id</c>. Returns empty on failure or
    /// non-Windows.
    /// </summary>
    public static string GetMachineSid()
    {
        if (!OperatingSystem.IsWindows()) return string.Empty;
        return ReadMachineSidFromProfileList() ?? string.Empty;
    }

    /// <summary>
    /// OS version in the desktop-Spotify-canonical 3-part form
    /// (e.g. <c>10.0.26200</c>). .NET's <c>os.Version.ToString()</c> appends
    /// a trailing <c>.0</c> revision component which the real client never
    /// sends, so trim it.
    /// </summary>
    public static string GetOsVersionThreePart()
    {
        var v = Environment.OSVersion.Version;
        return $"{v.Major}.{v.Minor}.{v.Build}";
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadBiosKey(string valueName)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\BIOS");
            return key?.GetValue(valueName) as string;
        }
        catch
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadMachineSidFromProfileList()
    {
        try
        {
            using var profiles = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
            if (profiles == null) return null;

            foreach (var name in profiles.GetSubKeyNames())
            {
                if (!name.StartsWith("S-1-5-21-", StringComparison.Ordinal)) continue;
                var lastDash = name.LastIndexOf('-');
                if (lastDash <= "S-1-5-21".Length) continue;
                return name[..lastDash];
            }
        }
        catch
        {
            // Locked-down registry access — fall back to caller's default
        }
        return null;
    }
}
