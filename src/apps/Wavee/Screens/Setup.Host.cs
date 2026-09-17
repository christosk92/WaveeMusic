// ── Screens/Setup.Host.cs ──────────────────────────────────────────────────────────────────────────────────────────
// the runtime provisioning half — §2 had no file for it; plus the install bootstrap's disk probe (gap batch B5, G-095)
//
// Role: SHELL
// Owner: R
// Wave: 6
// Budget: ~450 lines (UNVERIFIED)
// Spec: ch 19 §9.5 (proposes the file, states no number)
//
// The runtime provisioning HOST (a `Setup.RuntimeHost` over the local-playback runtime provisioner: catalog fetch,
// download, verify, install) is still NOT here — it is blocked on decision D3 (the provisioner is fenced). With
// `Setup.Runtime.Host` null the wizard's Local playback page renders its model-null arm and stays skippable. What IS
// here is the SHELL half of `Setup.Bootstrap` (gap batch B5): the read-only disk witnesses of "this app has run
// before" and the one call the composition root makes. The bootstrap decisions are `Setup.Bootstrap` / `Setup.Gating`
// in `Setup.cs` (CORE) — as is the wizard's pure rules region (WP-6.R: `Layout`, `Commands`, `WizardRules`,
// `SignInPresentation`, `Recolor`, `Cover`), moved there verbatim (batch R2).

namespace Wavee;

public static partial class Setup
{
    // ══ REGION 2 — THE INSTALL BOOTSTRAP'S DISK PROBE (gap batch B5, G-095) ═════════════════════════════════════════

    /// <summary>Arm (or suppress) the first-run wizard for this install. Call ONCE per launch from the composition root,
    /// after <c>Platform.Boot</c> (it needs the settings store and the profile path) and BEFORE <c>Store.Use</c> opens
    /// <c>library.db</c> or the shell writes its history — either would make every install look existing. Writes settings
    /// only; the wizard itself is shown later, gated on <c>setup.pending</c>.</summary>
    public static void BootstrapInstall()
    {
        InstallWitnesses disk = ProbeInstall(Platform.LocalFolder);
        Bootstrap.Run(Platform.Settings, in disk);
    }

    /// <summary>The disk witnesses under <paramref name="dataRoot"/> (0.2.9's paths): <c>library.db</c>, a credential entry
    /// in <c>store.json</c> (read raw — its presence is the witness, whether or not this machine can decrypt it), and the
    /// navigation log <c>WaveeMusic\history.json</c>. READ-ONLY: nothing is created, opened for write or decrypted, and a
    /// witness that cannot be inspected reads as absent (logged).</summary>
    public static InstallWitnesses ProbeInstall(string dataRoot)
    {
        bool library = Exists(Path.Combine(dataRoot, "library.db"), "library.db");
        bool history = Exists(Path.Combine(dataRoot, "WaveeMusic", "history.json"), "history");
        bool credential = false;
        string store = Path.Combine(dataRoot, "store.json");
        if (Exists(store, "store.json"))
        {
            try { credential = new FileLocalStore(store).Get(Platform.CredentialKey) is { Length: > 0 }; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn("setup", "the credential witness could not be inspected (" + ex.GetType().Name + ")");
            }
        }
        return new InstallWitnesses(library, credential, history);
    }

    static bool Exists(string path, string witness)
    {
        try { return File.Exists(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Warn("setup", "install witness " + witness + " could not be inspected (" + ex.GetType().Name + ")");
            return false;
        }
    }
}
