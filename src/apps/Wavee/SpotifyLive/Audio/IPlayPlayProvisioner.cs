using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Audio;
using Wavee.SpotifyLive.Audio.Runtime;

namespace Wavee.SpotifyLive.Audio;

/// <summary>Seam over the PlayPlay runtime provisioner so the UI + live-session host compile with the concrete client
/// physically absent. The real PlayPlayRuntimeProvisioner implements it when the private client is present;
/// otherwise <see cref="NullPlayPlayProvisioner"/> reports "unavailable" for every operation.</summary>
public interface IPlayPlayProvisioner
{
    PlaybackRuntimeStatus GetSnapshot();
    /// <summary>The full locate/verify report behind <see cref="GetSnapshot"/> — every place a runtime was looked for,
    /// why the search ended where it did, and the verification result if one was reached. Cheap and side-effect-free:
    /// it exposes what the last provisioning pass already computed, so the diagnostics page can call it on render.</summary>
    PlaybackRuntimeDiagnostics GetDiagnostics();
    RuntimeAsset? CurrentAsset { get; }
    Task<RuntimeAsset?> EnsureRuntimeAsync(CancellationToken ct = default, bool allowUntrustedSignature = false);
    bool TryRegisterRuntime(string sourceDir, bool allowUntrustedSignature = false);
    Task<PlayPlayRuntimeCatalog?> FetchCatalogAsync(CancellationToken ct = default);
    (PlayPlayRuntimeCatalogEntry? Best, bool AnyForOtherArch) SelectBest(PlayPlayRuntimeCatalog catalog);
    IReadOnlyList<PlayPlayRuntimeCatalogEntry> SupportedPacks(PlayPlayRuntimeCatalog catalog);
    Task<PlayPlayRuntimeVerifyResult> DownloadAndInstallAsync(
        PlayPlayRuntimeCatalogEntry entry,
        bool allowUntrustedSignature,
        IProgress<PlayPlayDownloadProgress>? progress,
        CancellationToken ct = default);
    void ClearActivePointer();
}

/// <summary>The "client absent" provisioner: every operation reports the runtime as unavailable. Used when Wavee is built
/// without the local PlayPlay client (<c>WAVEE_PLAYPLAY_LOCAL</c> undefined).</summary>
public sealed class NullPlayPlayProvisioner : IPlayPlayProvisioner
{
    public static readonly NullPlayPlayProvisioner Instance = new();

    /// <summary>The one honest thing this provisioner knows, and the exact case the generic "couldn't find a supported
    /// local Spotify.dll" message used to hide: there is no local-playback code in this binary to probe with. Shared by
    /// the snapshot detail and the diagnostics report so both say the same sentence.</summary>
    public const string NotCompiledInDetail =
        "This build doesn't include local-playback support (WAVEE_PLAYPLAY_LOCAL was not compiled in), so no Spotify.dll was looked for.";

    NullPlayPlayProvisioner() { }

    public PlaybackRuntimeStatus GetSnapshot() =>
        new(ProvisioningOutcome.RuntimeUnavailable, Detail: NotCompiledInDetail);
    public PlaybackRuntimeDiagnostics GetDiagnostics() =>
        PlaybackRuntimeDiagnostics.NotCompiledIn(NotCompiledInDetail);
    public RuntimeAsset? CurrentAsset => null;
    public Task<RuntimeAsset?> EnsureRuntimeAsync(CancellationToken ct = default, bool allowUntrustedSignature = false)
        => Task.FromResult<RuntimeAsset?>(null);
    public bool TryRegisterRuntime(string sourceDir, bool allowUntrustedSignature = false) => false;
    public Task<PlayPlayRuntimeCatalog?> FetchCatalogAsync(CancellationToken ct = default)
        => Task.FromResult<PlayPlayRuntimeCatalog?>(null);
    public (PlayPlayRuntimeCatalogEntry? Best, bool AnyForOtherArch) SelectBest(PlayPlayRuntimeCatalog catalog)
        => (null, false);
    public IReadOnlyList<PlayPlayRuntimeCatalogEntry> SupportedPacks(PlayPlayRuntimeCatalog catalog)
        => Array.Empty<PlayPlayRuntimeCatalogEntry>();
    public Task<PlayPlayRuntimeVerifyResult> DownloadAndInstallAsync(
        PlayPlayRuntimeCatalogEntry entry,
        bool allowUntrustedSignature,
        IProgress<PlayPlayDownloadProgress>? progress,
        CancellationToken ct = default)
        => Task.FromResult(PlayPlayRuntimeVerifyResult.Fail(
            ProvisioningOutcome.RuntimeUnavailable, "install the local PlayPlay package"));
    public void ClearActivePointer() { }
}
