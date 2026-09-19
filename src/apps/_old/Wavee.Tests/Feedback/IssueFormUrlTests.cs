using System;
using System.Collections.Generic;
using Xunit;

namespace Wavee.Tests;

// IssueFormUrl.Build (Diagnostics/IssueFormUrl.cs): the exact query-string assembly per channel (template/category,
// title, identity fields, best-effort labels, then the channel's own fields in FieldIds order), the Budget-KB
// truncation ladder, and field validation against the ReportChannels dropdown lists. Expected URLs are assembled
// with the SAME System.Uri.EscapeDataString the production code uses (never hand-transcribed percent-escapes), so
// these are exact golden-URL tests without risking a manual-encoding arithmetic mistake.
public class IssueFormUrlTests
{
    static string E(string s) => Uri.EscapeDataString(s);

    static KeyValuePair<string, string> F(string id, string value) => new(id, value);

    [Fact]
    public void Build_Crash_ArmSideload_ProducesTheExactUrl()
    {
        var id = new ReportIdentity(
            VersionLine: "0.2.5 Breaker (0.2.5.6) · 7e209e37",
            InstallSource: ReportChannels.InstallSources[1],   // "Sideloaded (.appinstaller or .msix from GitHub)"
            Architecture: "ARM64",
            WindowsVersion: "Windows 11 (build 26100)",
            Quad: "0.2.5.6", Commit: "7e209e37", Channel: "stable");

        const string title = "App freezes when switching output device";
        const string when = "During playback";
        const string reproduces = "Every time";
        const string whatWereYouDoing = "Trying to switch outputs while a track is playing.";
        var labels = new[] { id.ArchLabel, id.InstallLabel };   // "arch: arm64", "install: sideload"

        var url = IssueFormUrl.Build(ReportKind.Crash, id, title,
            new[] { F("when", when), F("reproduces", reproduces), F("what-were-you-doing", whatWereYouDoing) },
            labels);

        string expected = "https://github.com/christosk92/WaveeMusic/issues/new"
            + "?template=crash_report.yml"
            + "&title=" + E("[Crash]: " + title)
            + "&version=" + E(id.VersionLine)
            + "&install-source=" + E(id.InstallSource)
            + "&architecture=" + E(id.Architecture)
            + "&windows-version=" + E(id.WindowsVersion)
            + "&labels=" + E(string.Join(",", labels))
            + "&when=" + E(when)
            + "&reproduces=" + E(reproduces)
            + "&what-were-you-doing=" + E(whatWereYouDoing);

        Assert.Equal(expected, url);
        Assert.Equal("arch: arm64", id.ArchLabel);
        Assert.Equal("install: sideload", id.InstallLabel);
    }

    [Fact]
    public void Build_Bug_X64Store_ProducesTheExactUrl()
    {
        var id = new ReportIdentity(
            VersionLine: "0.2.4 Breaker (0.2.4.5)",
            InstallSource: ReportChannels.InstallSources[0],   // "Microsoft Store"
            Architecture: "x64",
            WindowsVersion: "Windows 10 (build 19045)",
            Quad: "0.2.4.5", Commit: "", Channel: "store");

        const string title = "Cover art missing";
        const string whatHappened = "Cover art does not load on some albums";
        const string steps = "Open any playlist, scroll fast";
        const string expectedBehaviour = "Covers should always load";
        var labels = new[] { id.ArchLabel, id.InstallLabel, "area: playback" };

        var url = IssueFormUrl.Build(ReportKind.Bug, id, title,
            new[] { F("what-happened", whatHappened), F("steps-to-reproduce", steps), F("expected-behaviour", expectedBehaviour) },
            labels);

        string expected = "https://github.com/christosk92/WaveeMusic/issues/new"
            + "?template=bug_report.yml"
            + "&title=" + E("[Bug]: " + title)
            + "&version=" + E(id.VersionLine)
            + "&install-source=" + E(id.InstallSource)
            + "&architecture=" + E(id.Architecture)
            + "&windows-version=" + E(id.WindowsVersion)
            + "&labels=" + E(string.Join(",", labels))
            + "&what-happened=" + E(whatHappened)
            + "&steps-to-reproduce=" + E(steps)
            + "&expected-behaviour=" + E(expectedBehaviour);

        Assert.Equal(expected, url);
    }

    [Fact]
    public void Build_Question_HasNoTemplateNoIdentityAndNoLabels()
    {
        var id = new ReportIdentity("0.2.5 Breaker (0.2.5.6)", ReportChannels.InstallSources[0], "x64",
            "Windows 11 (build 26100)", "0.2.5.6", "7e209e37", "stable");

        const string title = "Gapless playback question";
        const string body = "Does Wavee support gapless playback for classical box sets";

        var url = IssueFormUrl.Build(ReportKind.Question, id, title, new[] { F("body", body) }, Array.Empty<string>());

        string expected = "https://github.com/christosk92/WaveeMusic/discussions/new"
            + "?category=q-a"
            + "&title=" + E(title)   // Question's TitlePrefix is "" -- no [Question]: prefix
            + "&body=" + E(body);

        Assert.Equal(expected, url);
        Assert.DoesNotContain("labels=", url);
        Assert.DoesNotContain("template=", url);
        Assert.DoesNotContain("version=", url);
    }

    [Fact]
    public void Build_Idea_UsesTheIdeasCategory()
    {
        var id = new ReportIdentity("0.2.5", ReportChannels.InstallSources[0], "x64", "Windows 11 (build 26100)", "0.2.5.0", "", "stable");

        var url = IssueFormUrl.Build(ReportKind.Idea, id, "A dark AMOLED theme", new[] { F("body", "Pure black background for OLED screens") }, Array.Empty<string>());

        Assert.StartsWith("https://github.com/christosk92/WaveeMusic/discussions/new?category=ideas&title=", url);
    }

    // ── Budget: the URL is always trimmed to fit, identity fields are never touched, and the truncation ladder
    //    starts with the channel's own least-essential field.  Exact percent-for-percent arithmetic is an
    //    implementation detail; what's pinned here is the CONTRACT the truncation loop must uphold. ─────────────────

    [Fact]
    public void Build_OverBudget_NeverExceedsTheBudget_AndKeepsIdentityIntact()
    {
        var id = new ReportIdentity(
            VersionLine: "0.2.5 Breaker (0.2.5.6) · 7e209e37",
            InstallSource: ReportChannels.InstallSources[1],
            Architecture: "ARM64",
            WindowsVersion: "Windows 11 (build 26100)",
            Quad: "0.2.5.6", Commit: "7e209e37", Channel: "stable");

        string big = new string('a', 5000);   // ~5 KB of plain (unescaped) filler per field
        var labels = new[] { id.ArchLabel, id.InstallLabel };

        var url = IssueFormUrl.Build(ReportKind.Bug, id, "Everything is slow",
            new[] { F("what-happened", big), F("steps-to-reproduce", big), F("expected-behaviour", big) }, labels);

        Assert.True(url.Length <= IssueFormUrl.Budget, $"url was {url.Length} chars, over the {IssueFormUrl.Budget} budget");
        // The identity fields are declared "never truncated" -- their full escaped values must survive somewhere
        // in the URL even after the free-text fields were cut down to fit.
        Assert.Contains("version=" + E(id.VersionLine), url);
        Assert.Contains("install-source=" + E(id.InstallSource), url);
        Assert.Contains("architecture=" + E(id.Architecture), url);
        Assert.Contains("windows-version=" + E(id.WindowsVersion), url);
        // At least one of the three 5 KB fields must have actually been cut down -- the full run of 5000 'a's
        // cannot possibly still be present once the URL fits under the budget.
        Assert.DoesNotContain(new string('a', 5000), url);
    }

    [Fact]
    public void Build_WellUnderBudget_LeavesEveryFieldWhole()
    {
        var id = new ReportIdentity("0.2.5", ReportChannels.InstallSources[0], "x64", "Windows 11 (build 26100)", "0.2.5.0", "", "stable");

        var url = IssueFormUrl.Build(ReportKind.Feature, id, "Add a queue filter",
            new[] { F("problem", "Hard to find a track in a long queue"), F("proposal", "Add a search box above the queue") },
            Array.Empty<string>());

        Assert.Contains("problem=" + E("Hard to find a track in a long queue"), url);
        Assert.Contains("proposal=" + E("Add a search box above the queue"), url);
        Assert.DoesNotContain("…", url);
        Assert.True(url.Length <= IssueFormUrl.Budget);
    }

    // ── Validate: an unknown dropdown value must fail loudly (GitHub silently drops it otherwise) ───────────────────

    [Fact]
    public void Build_ThrowsOnAnInvalidArchitectureValue()
    {
        var id = new ReportIdentity("0.2.5", ReportChannels.InstallSources[0], "x64", "Windows 11 (build 26100)", "0.2.5.0", "", "stable");

        Assert.Throws<ArgumentException>(() =>
            IssueFormUrl.Build(ReportKind.Crash, id, "title", new[] { F("architecture", "arm64") }, Array.Empty<string>()));
    }

    [Fact]
    public void Build_ThrowsOnAnInvalidReproducesValue_TrailingWhitespaceIsNotAMatch()
    {
        var id = new ReportIdentity("0.2.5", ReportChannels.InstallSources[0], "x64", "Windows 11 (build 26100)", "0.2.5.0", "", "stable");

        Assert.Throws<ArgumentException>(() =>
            IssueFormUrl.Build(ReportKind.Crash, id, "title", new[] { F("reproduces", "Sometimes ") }, Array.Empty<string>()));
    }

    [Fact]
    public void Build_AcceptsTheExactDropdownStrings()
    {
        var id = new ReportIdentity("0.2.5", ReportChannels.InstallSources[0], "x64", "Windows 11 (build 26100)", "0.2.5.0", "", "stable");

        // Must not throw: exact-case, exact-text values from the option lists ("when"/"reproduces" are not identity
        // ids, so -- unlike "architecture" -- their passed value actually reaches the assembled URL).
        var url = IssueFormUrl.Build(ReportKind.Crash, id, "title",
            new[] { F("reproduces", "Sometimes"), F("when", "Randomly") }, Array.Empty<string>());

        Assert.Contains("when=Randomly", url);
        Assert.Contains("reproduces=Sometimes", url);
    }
}
