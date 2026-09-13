using Wavee.Backend.Audio;

namespace Wavee.SpotifyLive.Audio;

/// <summary>Typed audio-runtime status surfaced to the UI (banner + retry). No silent nulls.</summary>
public sealed class AudioRuntimeStatusService
{
    readonly object _gate = new();
    AudioKeyFailureReason _reason = AudioKeyFailureReason.None;
    string? _detail;
    ProvisioningOutcome _prov = ProvisioningOutcome.NeverAttempted;

    public event Action? Changed;

    public AudioKeyFailureReason Reason { get { lock (_gate) return _reason; } }
    public string? Detail { get { lock (_gate) return _detail; } }
    public ProvisioningOutcome Provisioning { get { lock (_gate) return _prov; } }
    public bool HasIssue { get { lock (_gate) return _reason != AudioKeyFailureReason.None || _prov is not (ProvisioningOutcome.Ready or ProvisioningOutcome.NeverAttempted); } }

    public void SetKeyFailure(AudioKeyFailureReason reason, string? detail = null)
    {
        lock (_gate) { _reason = reason; _detail = detail; }
        Changed?.Invoke();
    }

    public void ClearKeyFailure()
    {
        lock (_gate) { _reason = AudioKeyFailureReason.None; _detail = null; }
        Changed?.Invoke();
    }

    public void SetProvisioning(ProvisioningOutcome outcome, string? detail = null)
    {
        lock (_gate) { _prov = outcome; _detail = detail ?? _detail; }
        Changed?.Invoke();
    }
}
