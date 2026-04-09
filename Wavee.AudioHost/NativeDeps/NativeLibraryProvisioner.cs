using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Wavee.AudioHost.NativeDeps;

/// <summary>
/// Downloads, verifies, and installs a native library that the current managed wrapper
/// ships with, but which the NuGet-supplied native payload does not cover for the
/// current platform (e.g. PortAudioSharp2 1.0.6 has no win-arm64 binary).
///
/// Flow (see Wavee.AudioHost/NativeDeps plan):
///   1. Platform gate via <see cref="NativeLibraryDescriptor.AppliesTo"/>.
///   2. Cache probe under %LOCALAPPDATA%\Wavee\{CacheSubfolder}\{CacheFileName} + SHA-256 verify.
///   3. Otherwise download .nupkg, extract the embedded DLL, verify SHA-256, atomic rename.
///   4. Register a <see cref="NativeLibrary.SetDllImportResolver(Assembly,DllImportResolver)"/>
///      on the managed wrapper assembly so subsequent P/Invokes find the cached binary.
///
/// The provisioner never throws from <see cref="EnsureAvailableAsync"/>; failures are
/// reported via <see cref="NativeLibraryProvisioningResult"/>. The caller decides whether
/// to exit the process (Program.cs uses <c>Environment.ExitCode = 3</c> + a failure marker
/// so the UI can show a specific toast instead of retrying five times).
///
/// AOT-safe: no reflection, no dynamic code, no DI container. Trim-compatible.
/// </summary>
internal sealed class NativeLibraryProvisioner
{
    private readonly ILogger _logger;
    private readonly HttpClient _http;
    private readonly NativeLibraryDescriptor _descriptor;

    public NativeLibraryProvisioner(ILogger logger, HttpClient http, NativeLibraryDescriptor descriptor)
    {
        _logger = logger;
        _http = http;
        _descriptor = descriptor;
    }

    /// <summary>
    /// Ensures the native library is present on disk and wired into the DLL import resolver.
    /// Safe to call on every startup; no-ops on platforms that do not need provisioning.
    /// </summary>
    public async Task<NativeLibraryProvisioningResult> EnsureAvailableAsync(CancellationToken ct)
    {
        // 1. Platform gate.
        if (!_descriptor.AppliesTo())
        {
            _logger.LogDebug("Native dependency '{Name}' not required on current platform", _descriptor.DisplayName);
            return NativeLibraryProvisioningResult.NotRequired();
        }

        _logger.LogInformation(
            "Provisioning native dependency '{Name}' (library={Library})",
            _descriptor.DisplayName, _descriptor.LibraryName);

        // 2. Resolve cache path.
        var cachePath = ResolveCachePath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create cache directory for native dependency");
            return NativeLibraryProvisioningResult.Failed($"Cache directory error: {ex.GetType().Name}", ex);
        }

        // 3. Cache hit + integrity check. On tampering, delete and re-download.
        if (File.Exists(cachePath))
        {
            var cachedHash = TryComputeSha256(cachePath);
            if (cachedHash is not null && HashEquals(cachedHash, _descriptor.ExpectedSha256))
            {
                _logger.LogDebug("Cached native dependency verified at {Path}", cachePath);
                if (TryInstallResolver(cachePath, out var resolverError))
                {
                    _logger.LogInformation("Native dependency ready (cached): {Path}", cachePath);
                    return NativeLibraryProvisioningResult.CachedHealthy(cachePath);
                }
                return NativeLibraryProvisioningResult.Failed(resolverError ?? "Resolver install failed", null);
            }

            _logger.LogWarning(
                "Cached native dependency at {Path} failed integrity check (got {Got}, expected {Expected}). Deleting.",
                cachePath, cachedHash ?? "<unreadable>", _descriptor.ExpectedSha256);
            try { File.Delete(cachePath); } catch { /* will fail later if the file is locked */ }
        }

        // 4-7. Download + extract + verify, with one retry on hash mismatch.
        const int maxAttempts = 2;
        Exception? lastException = null;
        string? lastReason = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var (ok, reason, ex) = await TryDownloadAndExtractAsync(cachePath, ct).ConfigureAwait(false);
            if (ok)
            {
                if (TryInstallResolver(cachePath, out var resolverError))
                {
                    _logger.LogInformation("Native dependency ready (downloaded): {Path}", cachePath);
                    return NativeLibraryProvisioningResult.Downloaded(cachePath);
                }
                return NativeLibraryProvisioningResult.Failed(resolverError ?? "Resolver install failed", null);
            }

            lastException = ex;
            lastReason = reason;

            // Only retry on integrity-mismatch (deterministic enough that one more pull might help
            // if the CDN served a stale response). Network errors get one attempt.
            if (attempt < maxAttempts && reason == FailureReasons.IntegrityMismatch)
            {
                _logger.LogWarning("Retrying native dependency download after integrity failure (attempt {N}/{Max})",
                    attempt + 1, maxAttempts);
                continue;
            }
            break;
        }

        _logger.LogError(lastException,
            "Native dependency provisioning failed: {Reason}", lastReason ?? "unknown");
        return NativeLibraryProvisioningResult.Failed(lastReason ?? "Unknown failure", lastException);
    }

    // ----------------------------------------------------------------------
    // Internals
    // ----------------------------------------------------------------------

    private static class FailureReasons
    {
        public const string NetworkError = "Download failed (network)";
        public const string HttpError = "Download failed (HTTP error)";
        public const string ArchiveError = "Archive could not be opened";
        public const string LayoutChanged = "Package layout changed";
        public const string ExtractError = "Extract failed";
        public const string IntegrityMismatch = "Integrity check failed";
        public const string MoveError = "Install failed (file locked)";
    }

    private string ResolveCachePath()
    {
        var baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Wavee", _descriptor.CacheSubfolder);
        return Path.Combine(baseDir, _descriptor.CacheFileName);
    }

    private async Task<(bool ok, string? reason, Exception? ex)> TryDownloadAndExtractAsync(
        string cachePath, CancellationToken ct)
    {
        var tmpNupkg = Path.Combine(Path.GetTempPath(), $"wavee-nupkg-{Guid.NewGuid():N}.nupkg");
        var stagingPath = cachePath + ".tmp";

        try
        {
            _logger.LogInformation("Downloading native dependency from {Url}", _descriptor.DownloadUrl);

            // --- Download ---
            try
            {
                using var resp = await _http.GetAsync(
                    _descriptor.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);

                if (!resp.IsSuccessStatusCode)
                {
                    return (false, $"{FailureReasons.HttpError}: HTTP {(int)resp.StatusCode}", null);
                }

                await using var fs = new FileStream(
                    tmpNupkg, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await resp.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                return (false, FailureReasons.NetworkError, ex);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                // Timeout (not user cancellation).
                return (false, FailureReasons.NetworkError, ex);
            }
            catch (IOException ex)
            {
                return (false, FailureReasons.NetworkError, ex);
            }

            // --- Extract ---
            ZipArchive? zip = null;
            try
            {
                try
                {
                    zip = ZipFile.OpenRead(tmpNupkg);
                }
                catch (InvalidDataException ex)
                {
                    return (false, FailureReasons.ArchiveError, ex);
                }

                var entry = zip.GetEntry(_descriptor.ArchiveEntryPath);
                if (entry is null)
                {
                    // Fallback walk in case the package moved the DLL within runtimes/.
                    foreach (var candidate in zip.Entries)
                    {
                        if (candidate.FullName.StartsWith("runtimes/", StringComparison.OrdinalIgnoreCase)
                            && candidate.FullName.EndsWith("/" + _descriptor.CacheFileName, StringComparison.OrdinalIgnoreCase))
                        {
                            entry = candidate;
                            break;
                        }
                    }
                }

                if (entry is null)
                {
                    _logger.LogError(
                        "Expected archive entry {Path} not found. Archive contents: {Entries}",
                        _descriptor.ArchiveEntryPath,
                        string.Join(", ", zip.Entries.Select(static e => e.FullName)));
                    return (false, FailureReasons.LayoutChanged, null);
                }

                try
                {
                    if (File.Exists(stagingPath)) File.Delete(stagingPath);
                }
                catch { /* overwritten below */ }

                try
                {
                    await using var src = entry.Open();
                    await using var dst = new FileStream(
                        stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    await src.CopyToAsync(dst, ct).ConfigureAwait(false);
                }
                catch (IOException ex)
                {
                    // Likely a concurrent AudioHost already staged it; let caller re-probe cache.
                    return (false, FailureReasons.MoveError, ex);
                }
            }
            finally
            {
                zip?.Dispose();
            }

            // --- Verify ---
            var stagedHash = TryComputeSha256(stagingPath);
            if (stagedHash is null || !HashEquals(stagedHash, _descriptor.ExpectedSha256))
            {
                _logger.LogWarning(
                    "Downloaded native dependency failed integrity check (got {Got}, expected {Expected})",
                    stagedHash ?? "<unreadable>", _descriptor.ExpectedSha256);
                try { File.Delete(stagingPath); } catch { }
                return (false, FailureReasons.IntegrityMismatch, null);
            }

            // --- Atomic install ---
            try
            {
                File.Move(stagingPath, cachePath, overwrite: true);
            }
            catch (IOException ex)
            {
                return (false, FailureReasons.MoveError, ex);
            }

            _logger.LogInformation("Native dependency cached at {Path}", cachePath);
            return (true, null, null);
        }
        finally
        {
            TryDeleteQuietly(tmpNupkg);
            TryDeleteQuietly(stagingPath);
        }
    }

    private bool TryInstallResolver(string nativePath, out string? error)
    {
        error = null;
        Assembly asm;
        try
        {
            asm = _descriptor.TargetAssembly();
        }
        catch (Exception ex)
        {
            error = "Target assembly resolve failed";
            _logger.LogError(ex, "{Error}", error);
            return false;
        }

        try
        {
            NativeLibrary.SetDllImportResolver(asm, (libraryName, _, _) =>
            {
                if (string.Equals(libraryName, _descriptor.LibraryName, StringComparison.Ordinal))
                {
                    if (NativeLibrary.TryLoad(nativePath, out var handle))
                        return handle;
                }
                return IntPtr.Zero; // fall through to default resolver
            });
            _logger.LogInformation(
                "Native library resolver installed: {Name} -> {Path}",
                _descriptor.LibraryName, nativePath);
            return true;
        }
        catch (InvalidOperationException)
        {
            // A resolver was already registered (e.g. caller invoked the provisioner twice).
            // The existing resolver still handles "portaudio", so treat as success.
            _logger.LogDebug("DllImportResolver already installed for {Assembly} — skipping",
                asm.GetName().Name);
            return true;
        }
        catch (Exception ex)
        {
            error = "Resolver registration failed";
            _logger.LogError(ex, "{Error}", error);
            return false;
        }
    }

    private static string? TryComputeSha256(string path)
    {
        try
        {
            using var fs = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> hash = stackalloc byte[32];
            SHA256.HashData(fs, hash);
            return Convert.ToHexStringLower(hash);
        }
        catch
        {
            return null;
        }
    }

    private static bool HashEquals(string actual, string expected) =>
        string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    private static void TryDeleteQuietly(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }
}
