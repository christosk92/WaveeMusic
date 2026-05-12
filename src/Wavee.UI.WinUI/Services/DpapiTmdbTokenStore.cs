using System;
using System.IO;
using System.Reactive.Subjects;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wavee.Local.Enrichment;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Helpers.Application;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Windows DPAPI-backed <see cref="ITmdbTokenStore"/>. Token bytes live at
/// <c>%APPDATA%\Wavee\credentials\tmdb.dat</c> (same directory as the
/// Spotify auth blob in <c>CredentialsCache</c>) under
/// <see cref="DataProtectionScope.CurrentUser"/> — encrypted at rest,
/// inaccessible to other Windows user accounts.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiTmdbTokenStore : ITmdbTokenStore
{
    private const string FileName = "tmdb.dat";

    private readonly string _filePath;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Subject<bool> _changes = new();
    private bool _hasToken;

    public DpapiTmdbTokenStore(ILogger<DpapiTmdbTokenStore>? logger = null)
    {
        _logger = logger;
        var dir = Path.Combine(AppPaths.AppDataDirectory, "credentials");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, FileName);
        // Prime HasToken so UI gating doesn't have to touch disk per binding
        // evaluation. The file's existence is a sufficient signal — decrypt
        // only happens on GetTokenAsync.
        _hasToken = File.Exists(_filePath);
    }

    public bool HasToken => _hasToken;
    public IObservable<bool> HasTokenChanged => _changes;

    public async Task<string?> GetTokenAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(_filePath))
            {
                if (_hasToken) { _hasToken = false; _changes.OnNext(false); }
                return null;
            }

            byte[] encrypted;
            try
            {
                encrypted = await File.ReadAllBytesAsync(_filePath, ct);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to read TMDB token blob — treating as absent");
                return null;
            }

            try
            {
                var plain = ProtectedData.Unprotect(encrypted, optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            catch (CryptographicException ex)
            {
                // Blob is corrupt or was written under a different Windows
                // user account (e.g. profile migration). Treat as no token
                // and let the user re-paste — don't crash.
                _logger?.LogWarning(ex, "TMDB token blob could not be unprotected — clearing");
                try { File.Delete(_filePath); } catch { /* best effort */ }
                if (_hasToken) { _hasToken = false; _changes.OnNext(false); }
                return null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetTokenAsync(string? token, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                if (File.Exists(_filePath))
                {
                    try { File.Delete(_filePath); }
                    catch (Exception ex) { _logger?.LogWarning(ex, "Failed to delete TMDB token blob"); }
                }
                if (_hasToken) { _hasToken = false; _changes.OnNext(false); }
                return;
            }

            var plain = Encoding.UTF8.GetBytes(token.Trim());
            byte[] encrypted;
            try
            {
                encrypted = ProtectedData.Protect(plain, optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to DPAPI-encrypt TMDB token");
                throw;
            }

            // Atomic write: write to a temp file then move into place so a
            // crash mid-write never leaves a half-written blob.
            var tmpPath = _filePath + ".tmp";
            await File.WriteAllBytesAsync(tmpPath, encrypted, ct);
            File.Move(tmpPath, _filePath, overwrite: true);

            if (!_hasToken) { _hasToken = true; _changes.OnNext(true); }
        }
        finally
        {
            _gate.Release();
        }
    }
}
