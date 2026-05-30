using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Wavee.UI.WinUI.Helpers.Application;
using Windows.System;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Packages local diagnostic artifacts into a single zip the user can drag onto a
/// GitHub issue or attach to an email. No remote upload — the user explicitly attaches
/// the file themselves.
///
/// <para>The bundle deliberately spans <b>both</b> data roots: the roaming app logs
/// (<see cref="AppPaths.LogsDirectory"/>) <i>and</i> the local-only AudioHost logs +
/// native-dependency failure markers (<see cref="AppPaths.AudioHostLogsDirectory"/>,
/// <see cref="AppPaths.NativeDepsDirectory"/>). The AudioHost log is the single most
/// useful artifact for "cannot connect to the audio engine" reports, and it lives in a
/// different folder (redirected differently under MSIX) than the rest.</para>
/// </summary>
public static class CrashReportPackager
{
    private const string GitHubIssuesNewUrl = "https://github.com/christosk92/WaveeMusic/issues/new";
    private const string MaintainerEmail = "christos@isoplanner.app";
    private const int MaxLogFiles = 5;
    private const long MaxIndividualFileBytes = 5 * 1024 * 1024;

    /// <summary>
    /// Writes a zip containing recent diagnostic files to the user's temp folder
    /// and returns its path. Returns <c>null</c> if nothing diagnostic exists
    /// (a fresh install with no logs at all).
    /// </summary>
    public static async Task<string?> CreateZipAsync()
    {
        var sources = CollectSourceFiles();
        if (sources.Count == 0) return null;

        var zipName = $"wavee-logs-{DateTime.Now:yyyyMMdd-HHmmss}.zip";
        var zipPath = Path.Combine(Path.GetTempPath(), zipName);

        // Snapshot AudioHost diagnostics on the UI thread before the Task.Run hop.
        var audio = AudioHostSnapshot.Capture();

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
                WriteManifest(writer, sources, audio);
            }
            catch
            {
                // Manifest is nice-to-have; never abort the zip for it.
            }
        }).ConfigureAwait(false);

        return zipPath;
    }

    private static void WriteManifest(
        TextWriter writer,
        List<(string SourcePath, string EntryName)> sources,
        AudioHostSnapshot audio)
    {
        writer.WriteLine($"Wavee diagnostic bundle generated {DateTime.Now:O}");
        writer.WriteLine($"App version: {GetAppVersion()}");
        // Report real architectures: Is64Bit* mislabels an ARM64 machine (and the
        // x64-emulated AudioHost) as "x64", which hides the actual platform on reports
        // like issue #4 (Surface Pro 11 / Snapdragon).
        writer.WriteLine($"OS: {Environment.OSVersion} ({RuntimeInformation.OSArchitecture})");
        writer.WriteLine($"Process arch: {RuntimeInformation.ProcessArchitecture}");
        writer.WriteLine($"CLR: {Environment.Version}");
        writer.WriteLine($"Culture: {CultureInfo.CurrentUICulture.Name}");

        writer.WriteLine();
        writer.WriteLine("AudioHost:");
        writer.WriteLine($"  exe: {audio.Path}");
        writer.WriteLine($"  exists: {audio.Exists}");
        writer.WriteLine($"  last exit code: {audio.LastExitCode?.ToString(CultureInfo.InvariantCulture) ?? "n/a (running or never started)"}");
        writer.WriteLine($"  state: {audio.State}");
        writer.WriteLine($"  bass.dll cached: {File.Exists(Path.Combine(AppPaths.NativeDepsDirectory, "win-x64", "bass.dll"))}");
        writer.WriteLine($"  portaudio.dll (arm64) cached: {File.Exists(Path.Combine(AppPaths.NativeDepsDirectory, "win-arm64", "portaudio.dll"))}");

        writer.WriteLine();
        writer.WriteLine("Files included:");
        foreach (var (_, entryName) in sources)
            writer.WriteLine($"  - {entryName}");
    }

    /// <summary>
    /// Builds the suggested GitHub issue body — version + OS info. Don't include the zip
    /// path: the user attaches the file manually in the browser.
    /// </summary>
    public static string BuildIssueBodyTemplate(string? context = null)
        => $"""
            ## Describe the bug
            {context ?? "<!-- A clear and concise description of what the bug is. -->"}

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
            - **OS:** {Environment.OSVersion} ({RuntimeInformation.OSArchitecture})
            - **Arch:** {RuntimeInformation.ProcessArchitecture}
            - **Culture:** {CultureInfo.CurrentUICulture.Name}
            """;

    /// <summary>Plain-text body for a mailto: draft. The user attaches the revealed zip.</summary>
    public static string BuildEmailBody(string? context = null)
        => $"""
            Describe the problem:
            {(context is null ? "" : context + "\n")}

            Please attach the wavee-logs zip that File Explorer just opened.

            Environment
            - Wavee: {GetAppVersion()}
            - OS: {Environment.OSVersion} ({RuntimeInformation.OSArchitecture})
            - Process arch: {RuntimeInformation.ProcessArchitecture}
            - Culture: {CultureInfo.CurrentUICulture.Name}
            """;

    /// <summary>Reveal the bundle in File Explorer (selected) so the user can drag/attach it.</summary>
    public static void RevealInExplorer(string? zipPath)
    {
        if (string.IsNullOrEmpty(zipPath) || !File.Exists(zipPath)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{zipPath}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Explorer /select for {zipPath} failed: {ex}");
        }
    }

    /// <summary>Open a pre-filled new GitHub issue in the browser.</summary>
    public static async Task OpenGitHubIssueAsync(string? body = null)
    {
        body ??= BuildIssueBodyTemplate();
        var url = $"{GitHubIssuesNewUrl}?body={WebUtility.UrlEncode(body)}";
        await Launcher.LaunchUriAsync(new Uri(url));
    }

    /// <summary>Open a pre-filled email draft to the maintainer (mailto can't auto-attach).</summary>
    public static async Task OpenEmailDraftAsync(string? body = null)
    {
        body ??= BuildEmailBody();
        var subject = $"WaveeMusic diagnostics ({GetAppVersion()})";
        var uri = $"mailto:{MaintainerEmail}?subject={Uri.EscapeDataString(subject)}&body={Uri.EscapeDataString(body)}";
        await Launcher.LaunchUriAsync(new Uri(uri));
    }

    /// <summary>Copy the bundle's file path to the clipboard.</summary>
    public static void CopyZipPath(string zipPath)
    {
        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(zipPath);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Copy zip path failed: {ex}");
        }
    }

    /// <summary>
    /// Quick "build → reveal → open GitHub issue" path. Used by the crash-recovery page,
    /// where a chooser dialog would be awkward. The Settings entry uses
    /// <see cref="DiagnosticsReporter.ReportAsync"/> (GitHub / Email / Open folder chooser).
    /// </summary>
    public static async Task OpenIssueReportAsync(string? context = null)
    {
        string? zipPath = null;
        try
        {
            zipPath = await CreateZipAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"CrashReportPackager.CreateZipAsync failed: {ex}");
        }

        if (zipPath is not null) RevealInExplorer(zipPath);
        await OpenGitHubIssueAsync(BuildIssueBodyTemplate(context));
    }

    private static List<(string SourcePath, string EntryName)> CollectSourceFiles()
    {
        var result = new List<(string, string)>();

        // Roaming: crash log + app/DRM rolling logs.
        if (File.Exists(AppPaths.CrashLogPath))
            result.Add((AppPaths.CrashLogPath, Path.GetFileName(AppPaths.CrashLogPath)));
        if (File.Exists(AppPaths.PendingCrashReportPath))
            result.Add((AppPaths.PendingCrashReportPath, Path.GetFileName(AppPaths.PendingCrashReportPath)));

        TryAddRecent(result, AppPaths.LogsDirectory, "wavee-*.log", "logs");
        TryAddRecent(result, AppPaths.LogsDirectory, "drm-*.log", "logs");

        // Local-only: the out-of-process AudioHost log — the key artifact for
        // "cannot connect to the audio engine" — plus native-dep failure markers.
        TryAddRecent(result, AppPaths.AudioHostLogsDirectory, "audiohost-*.log", "audiohost");
        TryAddRecent(result, AppPaths.NativeDepsDirectory, "*.failure.json", "nativedeps");

        // Latest memory-diagnostics sample (one file).
        TryAddRecent(result, AppPaths.DiagnosticsDirectory, "memory-*.csv", "diag", max: 1);

        return result;
    }

    private static void TryAddRecent(
        List<(string, string)> sink, string directory, string searchPattern, string entryPrefix, int max = MaxLogFiles)
    {
        try
        {
            if (!Directory.Exists(directory)) return;
            var files = Directory.EnumerateFiles(directory, searchPattern)
                .Select(p => new FileInfo(p))
                .OrderByDescending(fi => fi.LastWriteTimeUtc)
                .Take(max);
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

    /// <summary>UI-thread snapshot of AudioHost diagnostics for the (background) manifest writer.</summary>
    private readonly record struct AudioHostSnapshot(string Path, bool Exists, int? LastExitCode, string State)
    {
        public static AudioHostSnapshot Capture()
        {
            var apm = AppLifecycleHelper.AudioProcessManager;
            if (apm is null)
                return new AudioHostSnapshot("<audio process manager not initialized>", false, null, "n/a");
            return new AudioHostSnapshot(apm.AudioHostPath, apm.AudioHostExists, apm.LastExitCode, apm.State.ToString());
        }
    }
}
