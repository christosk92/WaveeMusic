using FluentGpu.Localization;

namespace Wavee.Backend.Audio;

public enum AudioKeyFailureReason
{
    None = 0,
    NeverProvisioned,
    ProvisioningUnavailable,
    ArchUnsupported,
    SecurityBlocked,
    License403,
    RotationDrift,
    EmulationFault,
    Network,
    KeyPending,
    Prebuffering,
    ApPermanent,
    ApTimeout,
    Restricted,
}

public enum ProvisioningOutcome
{
    Ready,
    NeverAttempted,
    RuntimeUnavailable,
    NoSupportedPack,
    PackDownloadFailed,
    HashMismatch,
    SignatureInvalid,
    ArchUnsupported,
}

public readonly record struct AudioKeyResult(ReadOnlyMemory<byte> Key, AudioKeyFailureReason Reason, string? Detail = null)
{
    public bool Ok => Reason == AudioKeyFailureReason.None && !Key.IsEmpty;
    public static AudioKeyResult Success(ReadOnlyMemory<byte> key) => new(key, AudioKeyFailureReason.None);
    public static AudioKeyResult Fail(AudioKeyFailureReason reason, string? detail = null) => new(default, reason, detail);
}

public readonly record struct PlayPlayDeriveResult(
    ReadOnlyMemory<byte> Key,
    AudioKeyFailureReason Reason,
    string? Detail = null,
    ReadOnlyMemory<byte> DerivedSlab = default,
    ReadOnlyMemory<byte> NativeCdnSeed = default)
{
    public bool Ok => Reason == AudioKeyFailureReason.None && Key.Length == 16;
}

/// <summary>A surfaced local-playback failure: the typed reason + a technical detail (for the log) + the user-facing
/// message (for the toast). Lets the UX show a friendly line while the log/diagnostics keep the exact cause.</summary>
public readonly record struct PlaybackErrorInfo(AudioKeyFailureReason Reason, string UserMessage, string? Detail);

/// <summary>Thrown when a track can't be resolved/played locally. Carries the typed reason so the controller can surface
/// a specific, user-facing message instead of failing silently.</summary>
public sealed class AudioPlaybackException : Exception
{
    public AudioKeyFailureReason Reason { get; }
    public AudioPlaybackException(AudioKeyFailureReason reason, string? detail = null)
        : base(detail ?? reason.ToString()) => Reason = reason;
}

/// <summary>Maps typed audio failures to short, honest, user-facing messages (no jargon, no silent nulls).</summary>
public static class AudioFailureText
{
    public static string ToUserMessage(this AudioKeyFailureReason r) => r switch
    {
        AudioKeyFailureReason.NeverProvisioned or AudioKeyFailureReason.ProvisioningUnavailable
            => "Local playback needs a one-time setup to play tracks on this device.",
        AudioKeyFailureReason.License403 => "Spotify declined this track for your account.",
        AudioKeyFailureReason.RotationDrift => "Playback needs an update to keep working.",
        AudioKeyFailureReason.ArchUnsupported => "This track can't be played on this device.",
        AudioKeyFailureReason.SecurityBlocked or AudioKeyFailureReason.EmulationFault
            => "Couldn't start secure playback for this track.",
        AudioKeyFailureReason.Network => "Connection lost. Check your internet connection and try again.",
        AudioKeyFailureReason.ApTimeout => "Timed out reaching Spotify — try again.",
        AudioKeyFailureReason.ApPermanent or AudioKeyFailureReason.Restricted => "This track isn't available to play right now.",
        _ => "Couldn't play this track.",
    };

    /// <summary>The friendly primary sentence for a provisioning outcome, optionally followed by the technical
    /// <paramref name="detail"/> as a SECOND line. The primary sentence never changes — detail is appended, never
    /// substituted — so the copy stays honest while the exact cause stops being discarded (a generic
    /// "couldn't find a supported local Spotify.dll" with no reason is the bug this parameter exists to fix).</summary>
    public static string ToUserMessage(this ProvisioningOutcome o, string? detail = null)
    {
        string primary = o switch
        {
            ProvisioningOutcome.RuntimeUnavailable => Loc.Get(Strings.Playback.Runtime.NotFound),
            ProvisioningOutcome.NoSupportedPack => Loc.Get(Strings.Playback.Runtime.NoPack),
            ProvisioningOutcome.PackDownloadFailed => Loc.Get(Strings.Playback.Runtime.DownloadFailed),
            ProvisioningOutcome.HashMismatch or ProvisioningOutcome.SignatureInvalid => "Playback support failed verification.",
            ProvisioningOutcome.ArchUnsupported => "Playback support isn't available for this device.",
            _ => "Playback support isn't ready.",
        };
        return string.IsNullOrWhiteSpace(detail) ? primary : primary + "\n" + detail;
    }
}
