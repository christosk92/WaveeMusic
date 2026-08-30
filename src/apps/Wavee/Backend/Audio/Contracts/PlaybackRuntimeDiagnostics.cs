namespace Wavee.Backend.Audio;

/// <summary>The full "why is local playback not ready?" report, as UI-safe data.
///
/// <para>This is the SEAM CONTRACT between the public app and the private PlayPlay client: the concrete provisioner
/// (private repo) populates it from what its locate/verify pass already computed, and
/// <c>NullPlayPlayProvisioner</c> populates the "this build has no local-playback support at all" case. Nothing here
/// is a private type — <see cref="LocateCandidateInfo.Source"/> is a plain STRING mirror of the private locate-source
/// enum, and <see cref="LocateReason"/> is a plain string, precisely so no private enum ever appears in this public
/// assembly's signature. Adding a member here is a seam change; renaming/retyping one breaks the private
/// implementation.</para></summary>
public sealed record PlaybackRuntimeDiagnostics(
    bool CompiledIn,
    IReadOnlyList<LocateCandidateInfo> Candidates,
    ProvisioningOutcome LocateOutcome,
    string? LocateReason,
    ProvisioningOutcome? VerifyOutcome,
    string? VerifyDetail,
    SignatureTrust? SignatureTrust,
    DigitalSignatureInfo? SignatureInfo)
{
    /// <summary>The report for a build compiled WITHOUT the local PlayPlay client: nothing was probed and nothing
    /// could have been, so say that plainly instead of rendering an empty-looking report.</summary>
    public static PlaybackRuntimeDiagnostics NotCompiledIn(string reason) => new(
        CompiledIn: false,
        Candidates: [],
        LocateOutcome: ProvisioningOutcome.RuntimeUnavailable,
        LocateReason: reason,
        VerifyOutcome: null,
        VerifyDetail: null,
        SignatureTrust: null,
        SignatureInfo: null);
}

/// <summary>One place the provisioner looked for a runtime. <see cref="Source"/> is the string NAME of the private
/// locate-source ("EnvironmentOverride", "Settings", "CanonicalStore", "Bundled", …) — a string, not the enum, so the
/// private type never crosses the seam.</summary>
public sealed record LocateCandidateInfo(
    string Source,
    string RuntimeDir,
    bool DllPresent,
    bool ManifestPresent);
