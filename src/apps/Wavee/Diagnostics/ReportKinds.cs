using System;

namespace Wavee;

/// <summary>The five channels an in-app report can land in: two issue-form kinds that also cover crashes and bugs,
/// a feature-proposal issue form, and two GitHub Discussions categories for softer, non-actionable input.</summary>
public enum ReportKind : byte { Crash, Bug, Feature, Question, Idea }

/// <summary>Everything <see cref="IssueFormUrl"/> and <see cref="ReportBundle"/> need to know about one channel:
/// where it lives on GitHub, which field ids it prefills (in the order the URL should carry them), which of those
/// fields give up their content first when the URL must be trimmed to fit, and which box the toast tells the
/// reporter to paste the clipboard report into.</summary>
/// <param name="Kind">Which <see cref="ReportKind"/> this channel serves.</param>
/// <param name="Path">The GitHub path — <c>/issues/new</c> for a template, <c>/discussions/new</c> for a category.</param>
/// <param name="TitlePrefix">Prepended to the reporter's title, e.g. <c>"[Crash]: "</c>; empty for Discussions.</param>
/// <param name="Template">The issue-form YAML file name, or null for a Discussions channel.</param>
/// <param name="Category">The Discussions category slug, or null for an issue-form channel.</param>
/// <param name="FieldIds">The field ids to prefill, in channel order.</param>
/// <param name="TruncationOrder">Which of <see cref="FieldIds"/> gives up characters first when the assembled URL
/// is over budget — longest, least-essential fields first.</param>
/// <param name="PasteBox">The label on GitHub's form the toast tells the reporter to paste the clipboard report into.</param>
public sealed record ReportChannel(ReportKind Kind, string Path, string TitlePrefix, string? Template, string? Category,
    string[] FieldIds, string[] TruncationOrder, string PasteBox);

/// <summary>The routing table for every report channel: URLs, prefilled field ids, and the exact dropdown option
/// strings copied verbatim from the issue-form YAML (<c>.github/ISSUE_TEMPLATE/crash_report.yml</c>,
/// <c>bug_report.yml</c>, <c>feature_request.yml</c>) — GitHub silently drops a prefilled value that doesn't match
/// one of these strings exactly, so <see cref="ReportChannelsTests"/> pins them against the YAML.</summary>
static class ReportChannels
{
    public const string Repo = "https://github.com/christosk92/WaveeMusic";

    public static readonly string[] InstallSources =
        ["Microsoft Store", "Sideloaded (.appinstaller or .msix from GitHub)", "Built from source"];

    public static readonly string[] Architectures = ["x64", "ARM64", "Not sure"];

    public static readonly string[] When =
        ["On launch", "During playback", "When switching video on or off", "When navigating pages", "After an update", "Randomly", "Other"];

    public static readonly string[] Reproduces = ["Every time", "Sometimes", "Once so far"];

    public static readonly string[] Areas =
    [
        "playback", "video", "lyrics", "player", "connect", "library", "playlists", "search", "home", "browse",
        "concerts", "detail-pages", "sidebar", "shell", "auth", "setup", "updates", "store", "release-tooling",
        "diagnostics", "modules", "i18n", "engine", "Not sure"
    ];

    public static readonly ReportChannel Crash = new(ReportKind.Crash, "/issues/new", "[Crash]: ", "crash_report.yml", null,
        ["version", "install-source", "architecture", "windows-version", "when", "reproduces", "what-were-you-doing"],
        ["what-were-you-doing"], "Crash report");

    public static readonly ReportChannel Bug = new(ReportKind.Bug, "/issues/new", "[Bug]: ", "bug_report.yml", null,
        ["version", "install-source", "architecture", "windows-version", "what-happened", "steps-to-reproduce", "expected-behaviour"],
        ["expected-behaviour", "steps-to-reproduce", "what-happened"], "Relevant log lines");

    public static readonly ReportChannel Feature = new(ReportKind.Feature, "/issues/new", "[Feature]: ", "feature_request.yml", null,
        ["problem", "proposal", "area", "alternatives"], ["alternatives", "proposal", "problem"], "Proposal");

    public static readonly ReportChannel Question = new(ReportKind.Question, "/discussions/new", "", null, "q-a", ["body"], ["body"], "Body");

    public static readonly ReportChannel Idea = new(ReportKind.Idea, "/discussions/new", "", null, "ideas", ["body"], ["body"], "Body");

    public static ReportChannel For(ReportKind kind) => kind switch
    {
        ReportKind.Crash => Crash,
        ReportKind.Bug => Bug,
        ReportKind.Feature => Feature,
        ReportKind.Question => Question,
        _ => Idea,
    };

    /// <summary>Parses a deep-link argument (<c>wavee://open?route=report&amp;arg=bug|feature|crash|question|idea</c>)
    /// case-insensitively. An unrecognized or missing argument is not an error here — the caller falls back to
    /// <see cref="ReportKind.Bug"/> — so this only reports whether the parse succeeded.</summary>
    public static bool TryParseKind(string? arg, out ReportKind kind)
    {
        if (!string.IsNullOrEmpty(arg))
        {
            if (string.Equals(arg, "crash", StringComparison.OrdinalIgnoreCase)) { kind = ReportKind.Crash; return true; }
            if (string.Equals(arg, "bug", StringComparison.OrdinalIgnoreCase)) { kind = ReportKind.Bug; return true; }
            if (string.Equals(arg, "feature", StringComparison.OrdinalIgnoreCase)) { kind = ReportKind.Feature; return true; }
            if (string.Equals(arg, "question", StringComparison.OrdinalIgnoreCase)) { kind = ReportKind.Question; return true; }
            if (string.Equals(arg, "idea", StringComparison.OrdinalIgnoreCase)) { kind = ReportKind.Idea; return true; }
        }
        kind = ReportKind.Bug;
        return false;
    }
}
