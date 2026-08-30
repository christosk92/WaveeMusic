using System;
using System.Globalization;
using FluentGpu.Localization;
using Wavee.Core;
using Wavee.Core.ReleaseNotes;

namespace Wavee;

/// <summary>
/// The ENGINE-FREE half of <see cref="ReleaseNotesText"/>: the repository, the URLs every surface links to, and the
/// one date format. Split into its own file so the app-update service can share these without dragging the control
/// kit in behind them (and so the test assembly can compile them without an engine reference).
///
/// <para>This file is the SINGLE OWNER of "which GitHub page does this open?". Three surfaces had grown private
/// copies of the release-page rule — the updater, the notification bridge and Settings › About — and one of them had
/// already drifted to the bare <c>/releases</c> listing, so a user chasing a failed 0.3.0 update landed on a page
/// that never mentioned 0.3.0.</para>
/// </summary>
static partial class ReleaseNotesText
{
    /// <summary>The product repository every bare <c>#123</c> belongs to. A reference that named its own
    /// <c>owner/repo</c> keeps it (<see cref="InlineToken.Repo"/>).</summary>
    public const string Repo = "christosk92/WaveeMusic";

    public const string RepoUrl = "https://github.com/" + Repo;

    /// <summary>The canonical web address of one issue/PR reference. GitHub redirects <c>/issues/N</c> to
    /// <c>/pull/N</c> when N is a PR (and the reverse), so a misclassified reference still lands on the right page.</summary>
    public static string IssueUrl(string? repo, int number, bool pr)
        => "https://github.com/" + (string.IsNullOrEmpty(repo) ? Repo : repo)
         + (pr ? "/pull/" : "/issues/") + number.ToString(CultureInfo.InvariantCulture);

    /// <summary>The GitHub release page for a semver ("0.3.0" → <c>…/releases/tag/wavee-v0.3.0</c>).</summary>
    public static string ReleaseTagUrl(string? semver)
        => RepoUrl + "/releases/tag/wavee-v" + (semver ?? "");

    /// <summary>The release page an update SNAPSHOT is talking about: its tag when we can name the version (the index
    /// gave us a semver, or the quad reduces to one), else the releases listing.
    /// <para>The ONE owner of this. Three surfaces open it — the updater when the build is unpackaged, the toast's
    /// "Open release page" action, and About's failed-update card — and each had grown its own copy, one of which had
    /// already drifted to the bare <c>/releases</c> listing (so a user chasing a specific failed version landed on a
    /// page that did not mention it).</para></summary>
    public static string ReleasePageUrl(AppUpdateSnapshot? snapshot)
    {
        if (snapshot is null) return RepoUrl + "/releases";
        string semver = snapshot.TargetSemVer is { Length: > 0 } s
            ? s
            : AppUpdateVersion.ReleaseTagVersion(snapshot.TargetQuad ?? "");
        return semver.Length > 0 ? ReleaseTagUrl(semver) : RepoUrl + "/releases";
    }

    /// <summary>A release date ("2026-08-29") as the page prints it ("29 Aug 2026"). Unparseable input is echoed
    /// verbatim rather than blanked — the document is hand-authored and the reader can still act on the raw string.
    /// <para>INVARIANT culture on both sides, deliberately. Wavee publishes with <c>InvariantGlobalization=true</c>, so
    /// <c>CurrentCulture</c> IS the invariant culture at runtime and asking for it only hides that fact from the reader
    /// of this code. The visible ordering comes from the <c>whatsNew.dateFormat</c> loc key instead, which is what a
    /// translated culture table can actually change.</para></summary>
    public static string Date(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return "";
        return DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d)
            ? d.ToString(Loc.Get(Strings.WhatsNew.DateFormat), CultureInfo.InvariantCulture)
            : iso;
    }
}
