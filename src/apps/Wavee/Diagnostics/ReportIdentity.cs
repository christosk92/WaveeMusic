using System.Globalization;
using System.Text;
using Wavee.Core;

namespace Wavee;

/// <summary>The identity fields every report channel prefills — version, install source, architecture, Windows
/// build — computed once per report so <see cref="IssueFormUrl"/> and <see cref="ReportBundle"/> never re-derive
/// them differently. <see cref="Quad"/>, <see cref="Commit"/> and <see cref="Channel"/> ride along for the crash
/// bundle's header even though they are not separate GitHub form fields.</summary>
/// <param name="VersionLine">The one-line build stamp shown to the reporter, e.g.
/// <c>"0.2.5 Breaker (0.2.5.6) · 7e209e37"</c>.</param>
/// <param name="InstallSource">One of <see cref="ReportChannels.InstallSources"/>.</param>
/// <param name="Architecture">One of <see cref="ReportChannels.Architectures"/>.</param>
/// <param name="WindowsVersion">e.g. <c>"Windows 11 (build 26100)"</c>.</param>
/// <param name="Quad">The MSIX identity version, or "" for a dev build.</param>
/// <param name="Commit">The short build commit, or "".</param>
/// <param name="Channel">"stable" | "beta" | "dev" | "store".</param>
public sealed record ReportIdentity(string VersionLine, string InstallSource, string Architecture, string WindowsVersion, string Quad, string Commit, string Channel)
{
    /// <summary>Builds the identity from the app's own build stamp plus the few OS facts a crash header always
    /// wants. <paramref name="isPackaged"/> and <paramref name="osArch"/>/<paramref name="osBuild"/> are passed in
    /// rather than read here so this stays testable without a real packaged/OS environment.</summary>
    public static ReportIdentity From(WaveeVersionInfo me, bool isPackaged, string osArch, int osBuild)
    {
        var sb = new StringBuilder(64);
        sb.Append(me.SemVer);
        if (me.Codename.Length > 0) sb.Append(' ').Append(me.Codename);
        if (me.Quad.Length > 0) sb.Append(" (").Append(me.Quad).Append(')');
        if (me.Commit.Length > 0) sb.Append(" · ").Append(me.Commit);

        string install = me.IsStore ? ReportChannels.InstallSources[0]
                        : isPackaged && !me.IsDev ? ReportChannels.InstallSources[1]
                        : ReportChannels.InstallSources[2];

        string arch = osArch switch
        {
            "X64" => "x64",
            "Arm64" => "ARM64",
            _ => "Not sure",
        };

        string win = (osBuild >= 22000 ? "Windows 11" : "Windows 10") + " (build " + osBuild.ToString(CultureInfo.InvariantCulture) + ")";

        return new ReportIdentity(sb.ToString(), install, arch, win, me.Quad, me.Commit, me.Channel);
    }

    /// <summary>The best-effort architecture label (<c>labels=</c> only applies for reporters with triage rights,
    /// so this is a courtesy, not something the form enforces). Empty for "Not sure" — there is no such label.</summary>
    public string ArchLabel => Architecture switch
    {
        "x64" => "arch: x64",
        "ARM64" => "arch: arm64",
        _ => "",
    };

    /// <summary>The best-effort install-source label. Empty for "Built from source" — there is no
    /// <c>install: source</c> label in the repo's label set.</summary>
    public string InstallLabel => InstallSource == ReportChannels.InstallSources[0] ? "install: store"
                                 : InstallSource == ReportChannels.InstallSources[1] ? "install: sideload"
                                 : "";
}
