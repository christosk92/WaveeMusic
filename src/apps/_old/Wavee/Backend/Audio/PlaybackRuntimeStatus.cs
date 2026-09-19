using System.Runtime.InteropServices;

namespace Wavee.Backend.Audio;

/// <summary>Advisory Authenticode trust for a PlayPlay runtime DLL (v1: surfaced, not a hard gate).</summary>
public enum SignatureTrust
{
    Unknown = 0,
    Trusted,
    Untrusted,
    UnsupportedPlatform,
}

public readonly record struct DigitalSignatureInfo(
    string FilePath,
    string Subject,
    string Issuer,
    string Thumbprint,
    DateTimeOffset ValidFrom,
    DateTimeOffset ValidTo,
    SignatureTrust Trust,
    string Reason);

/// <summary>UI-facing projection of runtime provisioning — derives copy from <see cref="ProvisioningOutcome"/> via <see cref="AudioFailureText"/>.
///
/// <para><see cref="Detail"/> is the technical second line: WHY this outcome happened, in the provisioner's own words
/// (a locate reason, a verify detail, or "this build has no local-playback support at all"). It rides on the snapshot —
/// not on <c>AudioRuntimeStatusService</c> — so a provisioner with no status-service reference (the null one) can still
/// say something specific, and every consumer of a snapshot gets the same detail without a second lookup.</para></summary>
public readonly record struct PlaybackRuntimeStatus(
    ProvisioningOutcome Outcome,
    string? PackId = null,
    string? SpotifyVersion = null,
    Architecture? Arch = null,
    string? RuntimePath = null,
    SignatureTrust SignatureTrust = SignatureTrust.Unknown,
    bool NeedsUntrustedConfirmation = false,
    DigitalSignatureInfo? SignatureInfo = null,
    bool TrustedByPinnedFingerprint = false,
    string? Detail = null)
{
    public static PlaybackRuntimeStatus NotApplicable { get; } = new(ProvisioningOutcome.NeverAttempted);

    public bool IsReady => Outcome == ProvisioningOutcome.Ready;
    public bool ShowBanner => Outcome is not (ProvisioningOutcome.Ready or ProvisioningOutcome.NeverAttempted);
}
