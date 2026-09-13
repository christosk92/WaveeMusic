using Wavee.Backend.Audio;

namespace Wavee.SpotifyLive.Audio.Runtime;

/// <summary>Result of verifying a candidate PlayPlay runtime directory.</summary>
public sealed record PlayPlayRuntimeVerifyResult(
    bool Ok,
    ProvisioningOutcome Outcome,
    string? Detail,
    string? RuntimeDir,
    string? DllPath,
    PlayPlayRuntimeManifest? Manifest,
    SignatureTrust SignatureTrust = SignatureTrust.Unknown,
    bool NeedsUntrustedConfirmation = false,
    DigitalSignatureInfo? SignatureInfo = null,
    bool TrustedByPinnedFingerprint = false)
{
    public static PlayPlayRuntimeVerifyResult Fail(ProvisioningOutcome outcome, string detail) =>
        new(false, outcome, detail, null, null, null);
}

public enum PlayPlayDownloadStage { Fetching, Downloading, Verifying }

/// <summary>Progress for the network-provisioning flow. <see cref="TotalBytes"/> is the compressed payload size when
/// known (Content-Length or the catalog hint), else null (indeterminate).</summary>
public readonly record struct PlayPlayDownloadProgress(
    PlayPlayDownloadStage Stage,
    long BytesReceived,
    long? TotalBytes);
