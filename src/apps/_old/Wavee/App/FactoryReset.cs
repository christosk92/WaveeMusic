using System;
using System.Collections.Generic;
using System.IO;
using FluentGpu.WindowsApi.Storage;
using Microsoft.Win32;

namespace Wavee;

/// <summary>
/// Factory-reset: erase every local Wavee artifact (login, library, metadata, settings, caches, history) without
/// uninstalling, then restart so the next process is a first launch. The wipe runs on the <em>next</em> process, not
/// in the live one — <c>library.db</c> and the single-instance mutex are still held here.
/// </summary>
static class FactoryReset
{
    const string MarkerFileName = "Wavee.factory-reset.pending";

    /// <summary>Lives in <c>%TEMP%</c>, outside the wipe roots, so deleting <c>%LOCALAPPDATA%\Wavee</c> cannot eat it.</summary>
    public static string MarkerPath => Path.Combine(Path.GetTempPath(), MarkerFileName);

    public static IReadOnlyList<string> DefaultDataRoots() =>
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wavee"),
        Path.Combine(Path.GetTempPath(), "Wavee"),
    ];

    /// <summary>Call at the top of <c>Program.Main</c>, before settings / logs / <c>library.db</c> open. (Only the
    /// <c>--relaunch-after</c> broker arm runs ahead of it — that process is a courier and must NOT consume the marker.)</summary>
    public static void ApplyIfPending() => ApplyPending(MarkerPath, DefaultDataRoots(), wipeRegistry: true);

    /// <summary>Arm a reset and exit this process. <see cref="AppRelaunch"/>'s broker waits for this pid to die before
    /// starting the replacement, which is what beats the single-instance mutex and the <c>library.db</c> lock.</summary>
    public static void RequestAndRelaunch(IEnumerable<string>? extraRoots = null)
    {
        WriteMarker(MarkerPath, extraRoots, DefaultDataRoots());
        RelaunchAndExit();
    }

    internal static void ApplyPending(string markerPath, IReadOnlyList<string> defaultRoots, bool wipeRegistry)
    {
        if (!File.Exists(markerPath)) return;
        string[] extras;
        try { extras = File.ReadAllLines(markerPath); }
        catch { extras = []; }

        var roots = new List<string>(defaultRoots.Count + extras.Length);
        roots.AddRange(defaultRoots);
        foreach (string line in extras)
        {
            string path = line.Trim();
            if (path.Length == 0) continue;
            roots.Add(path);
        }

        WipeDirectories(roots);
        if (wipeRegistry) WipeSettingsRegistry();
        try { File.Delete(markerPath); } catch { }
    }

    internal static void WriteMarker(string markerPath, IEnumerable<string>? extraRoots, IReadOnlyList<string> defaultRoots)
    {
        var lines = new List<string>();
        if (extraRoots is not null)
        {
            foreach (string raw in extraRoots)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string path = Path.GetFullPath(raw);
                if (IsUnderAny(path, defaultRoots)) continue;
                lines.Add(path);
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
        File.WriteAllLines(markerPath, lines);
    }

    internal static void WipeDirectories(IReadOnlyList<string> roots)
    {
        foreach (string raw in roots)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            try { DeleteTree(Path.GetFullPath(raw)); }
            catch { }
        }
    }

    static void WipeSettingsRegistry()
    {
        if (!OperatingSystem.IsWindows()) return;
        try { AppDataStore.ForUnpackaged("Wavee", "Wavee").Clear(); }
        catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Wavee", throwOnMissingSubKey: false); }
        catch { }
    }

    // The relaunch is AppRelaunch's job: it spawns a broker that waits for THIS pid to exit (which is what releases the
    // single-instance mutex and unlocks library.db) and then starts a fresh Wavee, which finds the marker and wipes.
    // If the spawn fails the marker is still on disk, so a manual relaunch applies the wipe anyway.
    static void RelaunchAndExit()
    {
        AppRelaunch.RestartAfterExit();
        Environment.Exit(0);
    }

    static void DeleteTree(string path)
    {
        if (File.Exists(path))
        {
            try { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); } catch { }
            return;
        }
        if (!Directory.Exists(path)) return;

        try
        {
            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); File.Delete(file); } catch { }
            }
        }
        catch { }

        try { Directory.Delete(path, recursive: true); }
        catch { }
    }

    static bool IsUnderAny(string path, IReadOnlyList<string> roots)
    {
        foreach (string root in roots)
        {
            if (IsUnder(path, root)) return true;
        }
        return false;
    }

    static bool IsUnder(string path, string root)
    {
        string p = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (p.Equals(r, StringComparison.OrdinalIgnoreCase)) return true;
        return p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
