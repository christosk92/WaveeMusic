// ── Platform/InstanceIdRules.cs — which single instance a launch belongs to (evidence-diagnostics §A.7) ─────────────
//
// The default profile is THE Wavee: one instance per user session ("Wavee"), and it owns the process-global OS
// integration — the wavee:// / spotify: protocol handlers, start-on-login, the toast activator and the taskbar jump
// list. A `--profile <dir>` run (a verify / scratch profile) is its OWN instance ("Wavee." + a hash of the folder): it
// runs beside the user's Wavee instead of handing its launch to it, a second launch of the SAME profile still redirects
// to it, and it never rewrites the user's OS registrations. Pure (engine-free) so the decision is a unit test.

using System.Globalization;

namespace Wavee;

public static class InstanceIdRules
{
    /// <summary>The default profile's instance id (the single-instance mutex name).</summary>
    public const string DefaultId = "Wavee";

    /// <summary>The instance id of a launch whose profile folder is <paramref name="profileRoot"/> ("" / null = the
    /// default profile): <see cref="DefaultId"/>, or <c>Wavee.&lt;fnv32 of the normalized folder&gt;</c>.</summary>
    public static string For(string? profileRoot)
    {
        if (string.IsNullOrWhiteSpace(profileRoot)) return DefaultId;
        return DefaultId + "." + Fnv32(Normalize(profileRoot)).ToString("x8", CultureInfo.InvariantCulture);
    }

    /// <summary>Only the default profile registers process-global OS integration (protocol handlers, start-on-login,
    /// the toast activator, the jump list) — a scratch profile must never re-point the user's.</summary>
    public static bool OwnsOsIntegration(string? profileRoot) => string.IsNullOrWhiteSpace(profileRoot);

    /// <summary>One spelling per folder: full path, no trailing separator, upper-case invariant (the file system is
    /// case-insensitive, so <c>C:\wavee\p</c> and <c>c:\WAVEE\p\</c> are the same instance).</summary>
    public static string Normalize(string profileRoot)
    {
        string full;
        try { full = Path.GetFullPath(profileRoot.Trim()); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { full = profileRoot.Trim(); }
        return full.TrimEnd('\\', '/').ToUpperInvariant();
    }

    static uint Fnv32(string s)
    {
        uint h = 2166136261u;
        foreach (char c in s) { h ^= c; h *= 16777619u; }
        return h;
    }
}
