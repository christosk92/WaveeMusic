namespace Wavee.AudioHost.NativeDeps;

/// <summary>
/// Outcome of a <see cref="NativeLibraryProvisioner.EnsureAvailableAsync"/> call.
/// </summary>
internal enum NativeLibraryProvisioningOutcome
{
    /// <summary>Current platform does not need this native library. No action taken.</summary>
    NotRequired,

    /// <summary>A previously cached copy was found and passed integrity verification.</summary>
    CachedHealthy,

    /// <summary>The native library was just downloaded, verified, and cached.</summary>
    Downloaded,

    /// <summary>Provisioning failed. Inspect <see cref="NativeLibraryProvisioningResult.FailureReason"/>.</summary>
    Failed
}

/// <summary>
/// Result returned by <see cref="NativeLibraryProvisioner.EnsureAvailableAsync"/>.
/// The provisioner never throws — callers inspect this record and decide how to react.
/// </summary>
internal sealed record NativeLibraryProvisioningResult(
    NativeLibraryProvisioningOutcome Outcome,
    string? CachedPath,
    string? FailureReason,
    Exception? FailureException)
{
    public static NativeLibraryProvisioningResult NotRequired() =>
        new(NativeLibraryProvisioningOutcome.NotRequired, null, null, null);

    public static NativeLibraryProvisioningResult CachedHealthy(string path) =>
        new(NativeLibraryProvisioningOutcome.CachedHealthy, path, null, null);

    public static NativeLibraryProvisioningResult Downloaded(string path) =>
        new(NativeLibraryProvisioningOutcome.Downloaded, path, null, null);

    public static NativeLibraryProvisioningResult Failed(string reason, Exception? ex = null) =>
        new(NativeLibraryProvisioningOutcome.Failed, null, reason, ex);
}
