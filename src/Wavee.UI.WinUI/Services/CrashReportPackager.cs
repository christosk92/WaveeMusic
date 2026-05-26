using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Wavee.UI.WinUI.Helpers.Application;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Packages local diagnostic artifacts (crash logs, rolling Serilog logs,
/// DRM logs) into a single zip the user can drag onto a GitHub issue. No
/// remote upload — the user explicitly attaches the file themselves.
/// </summary>
public static class CrashReportPackager
{
    private const int MaxLogFiles = 5;
    private const long MaxIndividualFileBytes = 5 * 1024 * 1024;

    /// <summary>
    /// Writes a zip containing recent diagnostic files to the user's temp folder
    /// and returns its path. Returns <c>null</c> if nothing diagnostic exists
    /// (a fresh install with no crashes).
    /// </summary>
    public static async Task<string?> CreateZipAsync()
    {
        var sources = CollectSourceFiles();
        if (sources.Count == 0) return null;

        var zipName = $"wavee-logs-{DateTime.Now:yyyyMMdd-HHmmss}.zip";
        var zipPath = Path.Combine(Path.GetTempPath(), zipName);

        await Task.Run(() =>
        {
            using var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var archive = new ZipArchive(fs, ZipArchiveMode.Create);

            foreach (var (sourcePath, entryName) in sources)
            {
                try
                {
                    var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                    using var entryStream = entry.Open();
                    using var src = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    var copyLength = Math.Min(src.Length, MaxIndividualFileBytes);
                    if (copyLength < src.Length)
                        src.Seek(src.Length - copyLength, SeekOrigin.Begin);
                    var buffer = new byte[81920];
                    long remaining = copyLength;
                    while (remaining > 0)
                    {
                        var toRead = (int)Math.Min(buffer.Length, remaining);
                        var read = src.Read(buffer, 0, toRead);
                        if (read == 0) break;
                        entryStream.Write(buffer, 0, read);
                        remaining -= read;
                    }
                }
                catch
                {
                    // Best-effort: a locked or missing file should not abort the
                    // whole report. Skip it and keep going.
                }
            }

            try
            {
                var manifestEntry = archive.CreateEntry("manifest.txt", CompressionLevel.Optimal);
                using var entryStream = manifestEntry.Open();
                using var writer = new StreamWriter(entryStream);
                writer.WriteLine($"Wavee diagnostic bundle generated {DateTime.Now:O}");
                writer.WriteLine($"App version: {GetAppVersion()}");
                writer.WriteLine($"OS: {Environment.OSVersion} ({(Environment.Is64BitOperatingSystem ? "x64" : "x86")})");
                writer.WriteLine($"Process arch: {(Environment.Is64BitProcess ? "x64" : "x86")}");
                writer.WriteLine($"CLR: {Environment.Version}");
                writer.WriteLine($"Culture: {System.Globalization.CultureInfo.CurrentUICulture.Name}");
                writer.WriteLine();
                writer.WriteLine("Files included:");
                foreach (var (_, entryName) in sources)
                    writer.WriteLine($"  - {entryName}");
            }
            catch
            {
                // Manifest is nice-to-have; never abort the zip for it.
            }
        }).ConfigureAwait(false);

        return zipPath;
    }

    /// <summary>
    /// Builds the suggested issue body — version + OS info — for the GitHub
    /// new-issue URL. Don't include the zip path: the user attaches the file
    /// manually in the browser.
    /// </summary>
    public static string BuildIssueBodyTemplate()
        => $"""
            ## Describe the bug
            <!-- A clear and concise description of what the bug is. -->

            ## Steps to reproduce
            1.
            2.
            3.

            ## Expected behavior
            <!-- What you expected to happen. -->

            ## Actual behavior
            <!-- What actually happened. -->

            ## Diagnostic logs
            <!-- File Explorer opened a zip — drag it onto this issue to attach. -->

            ## Environment
            - **Wavee:** {GetAppVersion()}
            - **OS:** {Environment.OSVersion}
            - **Arch:** {(Environment.Is64BitProcess ? "x64" : "x86")}
            - **Culture:** {System.Globalization.CultureInfo.CurrentUICulture.Name}
            """;

    private static List<(string SourcePath, string EntryName)> CollectSourceFiles()
    {
        var result = new List<(string, string)>();

        if (File.Exists(AppPaths.CrashLogPath))
            result.Add((AppPaths.CrashLogPath, Path.GetFileName(AppPaths.CrashLogPath)));

        TryAddRecent(result, AppPaths.LogsDirectory, "wavee-*.log", "logs");
        TryAddRecent(result, AppPaths.LogsDirectory, "drm-*.log", "logs");

        return result;
    }

    private static void TryAddRecent(List<(string, string)> sink, string directory, string searchPattern, string entryPrefix)
    {
        try
        {
            if (!Directory.Exists(directory)) return;
            var files = Directory.EnumerateFiles(directory, searchPattern)
                .Select(p => new FileInfo(p))
                .OrderByDescending(fi => fi.LastWriteTimeUtc)
                .Take(MaxLogFiles);
            foreach (var fi in files)
                sink.Add((fi.FullName, Path.Combine(entryPrefix, fi.Name).Replace('\\', '/')));
        }
        catch
        {
            // Permission denied / directory disappeared — skip silently.
        }
    }

    private static string GetAppVersion()
    {
        try
        {
            var pkg = Windows.ApplicationModel.Package.Current;
            var v = pkg.Id.Version;
            return $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
        }
        catch
        {
            // Unpackaged fallback.
            var asm = Assembly.GetExecutingAssembly().GetName().Version;
            return asm?.ToString() ?? "unknown";
        }
    }
}
