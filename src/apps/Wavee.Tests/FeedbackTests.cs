// ── Wavee.Tests/FeedbackTests.cs — the report dialog's CORE (`Screens/Feedback.cs`) ────────────────────────────────────
//
// Ported from 0.2.9's `Wavee.Tests/Feedback/{ReportChannelsTests, ReportKindIndexTests, ReportIdentityTests,
// ReportBundleTests, IssueFormUrlTests, ReportRedactorTests}` against the 0.3 names (everything is nested in `Feedback`),
// plus `ReportFormTests` for the answers / URL-field / label rules 0.3 pulled out of the dialog card. The channel table
// is pinned twice: once by hard-coded lists, once against the issue-form YAML under `.github/ISSUE_TEMPLATE/` read as
// DATA (GitHub drops a prefilled dropdown value that is not one of the form's options — the YAML is the authority).
// Pure: no Platform statics, no entities, no engine.

using System.Text;
using Wavee;
using Xunit;
using static Wavee.Feedback;

namespace Wavee.Tests;

public class ReportChannelsTests
{
    [Fact]
    public void InstallSources_MatchTheIssueFormDropdown()
        => Assert.Equal(new[] { "Microsoft Store", "Sideloaded (.appinstaller or .msix from GitHub)", "Built from source" },
            ReportChannels.InstallSources);

    [Fact]
    public void Architectures_MatchTheIssueFormDropdown()
        => Assert.Equal(new[] { "x64", "ARM64", "Not sure" }, ReportChannels.Architectures);

    [Fact]
    public void When_MatchesTheIssueFormDropdown()
        => Assert.Equal(new[]
        {
            "On launch", "During playback", "When switching video on or off", "When navigating pages", "After an update", "Randomly", "Other",
        }, ReportChannels.When);

    [Fact]
    public void Reproduces_MatchesTheIssueFormDropdown()
        => Assert.Equal(new[] { "Every time", "Sometimes", "Once so far" }, ReportChannels.Reproduces);

    [Fact]
    public void Areas_MatchTheIssueFormDropdown()
        => Assert.Equal(new[]
        {
            "playback", "video", "lyrics", "player", "connect", "library", "playlists", "search", "home", "browse", "concerts",
            "detail-pages", "sidebar", "shell", "auth", "setup", "updates", "store", "release-tooling", "diagnostics", "modules",
            "i18n", "engine", "Not sure",
        }, ReportChannels.Areas);

    [Fact]
    public void Crash_Channel_HasTheDocumentedShape()
    {
        var ch = ReportChannels.Crash;
        Assert.Equal(ReportKind.Crash, ch.Kind);
        Assert.Equal("/issues/new", ch.Path);
        Assert.Equal("[Crash]: ", ch.TitlePrefix);
        Assert.Equal("crash_report.yml", ch.Template);
        Assert.Null(ch.Category);
        Assert.Equal(new[] { "version", "install-source", "architecture", "windows-version", "when", "reproduces", "what-were-you-doing" }, ch.FieldIds);
        Assert.Equal(new[] { "what-were-you-doing" }, ch.TruncationOrder);
        Assert.Equal("Crash report", ch.PasteBox);
    }

    [Fact]
    public void Bug_Channel_HasTheDocumentedShape()
    {
        var ch = ReportChannels.Bug;
        Assert.Equal(ReportKind.Bug, ch.Kind);
        Assert.Equal("bug_report.yml", ch.Template);
        Assert.Equal(new[] { "version", "install-source", "architecture", "windows-version", "what-happened", "steps-to-reproduce", "expected-behaviour" }, ch.FieldIds);
        Assert.Equal(new[] { "expected-behaviour", "steps-to-reproduce", "what-happened" }, ch.TruncationOrder);
        Assert.Equal("Relevant log lines", ch.PasteBox);
    }

    [Fact]
    public void Feature_Channel_HasTheDocumentedShape()
    {
        var ch = ReportChannels.Feature;
        Assert.Equal("feature_request.yml", ch.Template);
        Assert.Equal(new[] { "problem", "proposal", "area", "alternatives" }, ch.FieldIds);
        Assert.Equal(new[] { "alternatives", "proposal", "problem" }, ch.TruncationOrder);
        Assert.Equal("Proposal", ch.PasteBox);
    }

    [Fact]
    public void Question_Channel_IsADiscussionWithNoTemplate()
    {
        var ch = ReportChannels.Question;
        Assert.Equal("/discussions/new", ch.Path);
        Assert.Null(ch.Template);
        Assert.Equal("q-a", ch.Category);
        Assert.Equal(new[] { "body" }, ch.FieldIds);
        Assert.Equal("", ch.TitlePrefix);
    }

    [Fact]
    public void Idea_Channel_IsADiscussionWithNoTemplate()
    {
        var ch = ReportChannels.Idea;
        Assert.Equal("/discussions/new", ch.Path);
        Assert.Null(ch.Template);
        Assert.Equal("ideas", ch.Category);
        Assert.Equal(new[] { "body" }, ch.FieldIds);
    }

    [Theory]
    [InlineData(ReportKind.Crash)]
    [InlineData(ReportKind.Bug)]
    [InlineData(ReportKind.Feature)]
    [InlineData(ReportKind.Question)]
    [InlineData(ReportKind.Idea)]
    public void For_RoundTripsEveryKind(ReportKind kind) => Assert.Equal(kind, ReportChannels.For(kind).Kind);

    [Theory]
    [InlineData("crash", ReportKind.Crash)]
    [InlineData("BUG", ReportKind.Bug)]
    [InlineData("Feature", ReportKind.Feature)]
    [InlineData("question", ReportKind.Question)]
    [InlineData("idea", ReportKind.Idea)]
    public void TryParseKind_ParsesCaseInsensitively(string arg, ReportKind expected)
    {
        Assert.True(ReportChannels.TryParseKind(arg, out var kind));
        Assert.Equal(expected, kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nonsense")]
    public void TryParseKind_FallsBackToBug_AndReportsFailure(string? arg)
    {
        Assert.False(ReportChannels.TryParseKind(arg, out var kind));
        Assert.Equal(ReportKind.Bug, kind);
    }

    [Fact]
    public void EveryChannel_FieldIdsContainNoDuplicates()
    {
        foreach (var kind in new[] { ReportKind.Crash, ReportKind.Bug, ReportKind.Feature, ReportKind.Question, ReportKind.Idea })
        {
            var ch = ReportChannels.For(kind);
            Assert.Equal(ch.FieldIds.Distinct().Count(), ch.FieldIds.Length);
        }
    }

    // ── the issue-form YAML, read as data ──────────────────────────────────────────────────────────────────────────────

    /// <summary>The repository's issue-form folder, found by walking up from the test binary.</summary>
    static string TemplateDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, ".github", "ISSUE_TEMPLATE");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new InvalidOperationException(".github/ISSUE_TEMPLATE not found above " + AppContext.BaseDirectory);
    }

    static string[] Template(string file)
    {
        string path = Path.Combine(TemplateDir(), file);
        Assert.True(File.Exists(path), path + " is missing — the report channels prefill a form that no longer exists");
        return File.ReadAllLines(path);
    }

    /// <summary>The <c>options:</c> list under the element whose <c>id:</c> is <paramref name="id"/>.</summary>
    static List<string> DropdownOptions(string[] lines, string id)
    {
        int i = Array.FindIndex(lines, l => l.Trim() == "id: " + id);
        Assert.True(i >= 0, "no element with id " + id);
        while (++i < lines.Length && lines[i].Trim() != "options:")
            Assert.False(lines[i].TrimStart().StartsWith("- type:", StringComparison.Ordinal), id + " has no options list");
        var options = new List<string>();
        while (++i < lines.Length && lines[i].TrimStart() is var t && t.StartsWith("- ", StringComparison.Ordinal)
               && !t.StartsWith("- type:", StringComparison.Ordinal))
            options.Add(t[2..].Trim().Trim('"'));
        return options;
    }

    [Fact]
    public void When_And_Reproduces_MatchTheCrashFormYaml()
    {
        var crash = Template("crash_report.yml");
        Assert.Equal(ReportChannels.When, DropdownOptions(crash, "when"));
        Assert.Equal(ReportChannels.Reproduces, DropdownOptions(crash, "reproduces"));
    }

    [Fact]
    public void Areas_MatchTheFeatureFormYaml()
        => Assert.Equal(ReportChannels.Areas, DropdownOptions(Template("feature_request.yml"), "area"));

    [Theory]
    [InlineData(ReportKind.Crash)]
    [InlineData(ReportKind.Bug)]
    [InlineData(ReportKind.Feature)]
    public void EveryIssueChannel_FieldIdAndPasteBox_ExistInItsTemplate(ReportKind kind)
    {
        var ch = ReportChannels.For(kind);
        var lines = Template(ch.Template!);
        foreach (var id in ch.FieldIds)
            Assert.Contains(lines, l => l.Trim() == "id: " + id);
        Assert.Contains(lines, l => l.Trim() == "label: " + ch.PasteBox);
    }

    [Fact]
    public void InstallSources_And_Architectures_AreNamedByTheForms()
    {
        foreach (var file in new[] { "crash_report.yml", "bug_report.yml" })
        {
            string text = string.Join('\n', Template(file));
            foreach (var source in ReportChannels.InstallSources) Assert.Contains(source, text);
            Assert.Contains("x64", text);
            Assert.Contains("ARM64", text);
        }
    }
}

public class ReportKindIndexTests
{
    [Fact]
    public void Segments_IsBugFeatureQuestionIdea_InThatOrder()
        => Assert.Equal(new[] { ReportKind.Bug, ReportKind.Feature, ReportKind.Question, ReportKind.Idea }, ReportKindIndex.Segments);

    [Theory]
    [InlineData(ReportKind.Bug, 0)]
    [InlineData(ReportKind.Feature, 1)]
    [InlineData(ReportKind.Question, 2)]
    [InlineData(ReportKind.Idea, 3)]
    public void IndexOf_MatchesTheSegmentedItemOrder(ReportKind kind, int expected) => Assert.Equal(expected, ReportKindIndex.IndexOf(kind));

    [Fact]
    public void IndexOf_Crash_FallsBackToBug() => Assert.Equal(0, ReportKindIndex.IndexOf(ReportKind.Crash));

    [Theory]
    [InlineData(ReportKind.Bug)]
    [InlineData(ReportKind.Feature)]
    [InlineData(ReportKind.Question)]
    [InlineData(ReportKind.Idea)]
    public void RoundTrips_EveryVisibleKind(ReportKind kind) => Assert.Equal(kind, ReportKindIndex.KindAt(ReportKindIndex.IndexOf(kind)));

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(int.MaxValue)]
    public void KindAt_OutOfRange_FallsBackToBug(int index) => Assert.Equal(ReportKind.Bug, ReportKindIndex.KindAt(index));
}

public class ReportIdentityTests
{
    /// <summary>0.2.9's <c>WaveeVersionInfo</c> facts, folded the way it folds them: IsStore = channel "store",
    /// IsDev = channel "dev" or no quad.</summary>
    static ReportIdentity From(string channel, bool isPackaged, string osArch = "X64", int osBuild = 26100,
        string semVer = "0.2.5", string quad = "0.2.5.6", string codename = "Breaker", string commit = "7e209e37")
        => ReportIdentity.From(semVer, codename, quad, commit, channel, isStore: channel == "store",
            isDev: channel == "dev" || quad.Length == 0, isPackaged, osArch, osBuild);

    [Fact]
    public void DevBuild_IsBuiltFromSource_RegardlessOfPackaged()
        => Assert.Equal(ReportChannels.InstallSources[2], From("dev", isPackaged: true).InstallSource);

    [Fact]
    public void StoreBuild_IsMicrosoftStore_RegardlessOfPackaged()
    {
        var id = From("store", isPackaged: false);
        Assert.Equal(ReportChannels.InstallSources[0], id.InstallSource);
        Assert.Equal("install: store", id.InstallLabel);
    }

    [Fact]
    public void PackagedNonDevNonStoreBuild_IsSideloaded()
    {
        var id = From("stable", isPackaged: true);
        Assert.Equal(ReportChannels.InstallSources[1], id.InstallSource);
        Assert.Equal("install: sideload", id.InstallLabel);
    }

    [Fact]
    public void UnpackagedNonDevNonStoreBuild_IsBuiltFromSource()
    {
        var id = From("stable", isPackaged: false);
        Assert.Equal(ReportChannels.InstallSources[2], id.InstallSource);
        Assert.Equal("", id.InstallLabel);
    }

    [Theory]
    [InlineData("X64", "x64", "arch: x64")]
    [InlineData("Arm64", "ARM64", "arch: arm64")]
    [InlineData("X86", "Not sure", "")]
    public void Architecture_MapsTheKnownRuntimeArchitectures(string osArch, string expectedArch, string expectedLabel)
    {
        var id = From("stable", isPackaged: true, osArch: osArch);
        Assert.Equal(expectedArch, id.Architecture);
        Assert.Equal(expectedLabel, id.ArchLabel);
    }

    [Theory]
    [InlineData(19045, "Windows 10 (build 19045)")]
    [InlineData(21999, "Windows 10 (build 21999)")]
    [InlineData(22000, "Windows 11 (build 22000)")]
    [InlineData(26100, "Windows 11 (build 26100)")]
    public void WindowsVersion_FlipsToWindows11AtBuild22000(int build, string expected)
        => Assert.Equal(expected, From("stable", isPackaged: true, osBuild: build).WindowsVersion);

    [Fact]
    public void VersionLine_CombinesSemverCodenameQuadAndCommit()
    {
        var id = From("stable", isPackaged: true);
        Assert.Equal("0.2.5 Breaker (0.2.5.6) · 7e209e37", id.VersionLine);
        Assert.Equal("0.2.5.6", id.Quad);
        Assert.Equal("7e209e37", id.Commit);
        Assert.Equal("stable", id.Channel);
    }

    [Fact]
    public void VersionLine_DropsMissingPartsCleanly()
    {
        var id = From("dev", isPackaged: false, semVer: "0.3.0-dev", quad: "", codename: "", commit: "");
        Assert.Equal("0.3.0-dev", id.VersionLine);
        Assert.Equal("", id.Quad);
        Assert.Equal("", id.Commit);
    }
}

public class ReportBundleTests
{
    static ReportIdentity Id() => new("0.2.5 Breaker (0.2.5.6) · 7e209e37", ReportChannels.InstallSources[0],
        "x64", "Windows 11 (build 26100)", "0.2.5.6", "7e209e37", "stable");

    [Fact]
    public void Constants_MatchTheDocumentedBudget()
    {
        Assert.Equal(60 * 1024, ReportBundle.MaxBytes);
        Assert.Equal(300, ReportBundle.CrashLogLines);
        Assert.Equal(200, ReportBundle.ManualLogLines);
        Assert.Equal(12_000, ReportBundle.PreviewChars);
    }

    [Fact]
    public void FileName_FormatsTheStamp()
        => Assert.Equal("wavee-report-20260901-101500.txt", ReportBundle.FileName(new DateTimeOffset(2026, 9, 1, 10, 15, 0, TimeSpan.Zero)));

    [Fact]
    public void Preview_ReturnsTheBundleUnchanged_WhenUnderTheCharLimit()
    {
        string bundle = new('a', ReportBundle.PreviewChars);
        Assert.Equal(bundle, ReportBundle.Preview(bundle));
    }

    [Fact]
    public void Preview_TruncatesWithATrailingKbCount_WhenOverTheLimit()
    {
        string bundle = new('a', ReportBundle.PreviewChars + 5000);
        int moreKb = (bundle.Length - ReportBundle.PreviewChars) / 1024;
        Assert.Equal(bundle[..ReportBundle.PreviewChars] + "\n… (" + moreKb + " KB more in the copied report)", ReportBundle.Preview(bundle));
    }

    [Fact]
    public void Build_MentionsTheKind_AndTheIdentityFacts()
    {
        var bundle = ReportBundle.Build(ReportKind.Crash, Id(), answers: Array.Empty<(string Label, string Text)>(),
            diagnostics: "", crashHead: null, logLines: Array.Empty<string>(), logSource: "this session",
            includeLogs: false, now: DateTimeOffset.UtcNow);
        Assert.Contains("Crash", bundle);
        Assert.Contains("0.2.5.6", bundle);
    }

    [Fact]
    public void Build_IncludesEachNonEmptyAnswer_AsLabelThenText()
    {
        var answers = new[] { ("What happened", "The app closed with no error"), ("Steps", "") };
        var bundle = ReportBundle.Build(ReportKind.Bug, Id(), answers, "", null, Array.Empty<string>(), "this session",
            includeLogs: false, now: DateTimeOffset.UtcNow);
        Assert.Contains("What happened", bundle);
        Assert.Contains("The app closed with no error", bundle);
        Assert.DoesNotContain("Steps:\n\n", bundle);   // an empty answer leaves no dangling section
    }

    [Fact]
    public void Build_IncludesTheCrashHead_WhenProvided()
    {
        var bundle = ReportBundle.Build(ReportKind.Crash, Id(), Array.Empty<(string, string)>(), "",
            "Exception\n---------\nSystem.InvalidOperationException: boom", Array.Empty<string>(), "this session",
            includeLogs: true, now: DateTimeOffset.UtcNow);
        Assert.Contains("InvalidOperationException", bundle);
    }

    [Fact]
    public void Build_IncludeLogsFalse_OmitsTheFencedLogExcerpt()
    {
        var bundle = ReportBundle.Build(ReportKind.Bug, Id(), Array.Empty<(string, string)>(), "some diagnostics text",
            null, new[] { "log line one", "log line two" }, "this session", includeLogs: false, now: DateTimeOffset.UtcNow);
        Assert.DoesNotContain("```", bundle);
        Assert.DoesNotContain("log line one", bundle);
        Assert.DoesNotContain("some diagnostics text", bundle);
    }

    [Fact]
    public void Build_IncludeLogsTrue_FencesTheExcerptAndNamesTheSource()
    {
        var bundle = ReportBundle.Build(ReportKind.Bug, Id(), Array.Empty<(string, string)>(), "", null,
            new[] { "log line one", "log line two" }, "this session", includeLogs: true, now: DateTimeOffset.UtcNow);
        Assert.Contains("```", bundle);
        Assert.Contains("this session", bundle);
        Assert.Contains("log line one", bundle);
        Assert.Contains("log line two", bundle);
    }

    [Fact]
    public void Build_2000LinesOf100Bytes_StaysUnderMaxBytes_AndKeepsTheNewestLine()
    {
        var lines = new List<string>(2000);
        for (int i = 0; i < 2000; i++) lines.Add($"line-{i:D4} " + new string('x', 90));
        var bundle = ReportBundle.Build(ReportKind.Crash, Id(), Array.Empty<(string, string)>(), "", null,
            lines, "this session", includeLogs: true, now: DateTimeOffset.UtcNow);
        Assert.True(Encoding.UTF8.GetByteCount(bundle) <= ReportBundle.MaxBytes);
        Assert.Contains("line-1999 ", bundle);
        Assert.DoesNotContain("line-0000 ", bundle);
        Assert.Contains("truncated", bundle, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_FewShortLines_NeedsNoTruncation()
    {
        var bundle = ReportBundle.Build(ReportKind.Bug, Id(), Array.Empty<(string, string)>(), "", null,
            new[] { "line one", "line two", "line three" }, "this session", includeLogs: true, now: DateTimeOffset.UtcNow);
        Assert.Contains("line three", bundle);
        Assert.DoesNotContain("truncated", bundle, StringComparison.OrdinalIgnoreCase);
    }

    static string CrashHead() => string.Join('\n', new[]
    {
        "Wavee crash report", "=================", "version=0.2.5.6", "commit=7e209e37", "",
        "Exception", "---------", "System.InvalidOperationException: boom", "   at Wavee!<BaseAddress>+0x7b1fc6", "",
        "Frames (RVA)", "------------", "0x7b1fc6",
    });

    [Fact]
    public void SplitCrashReport_KeepsFramesInTheHead_AndTheLast300OfA700LineTail()
    {
        var tailLines = Enumerable.Range(0, 700).Select(i => $"line {i}").ToArray();
        var (head, tail) = ReportBundle.SplitCrashReport(CrashHead() + "\nwavee.log tail\n--------------\n" + string.Join('\n', tailLines));

        Assert.Contains("Frames (RVA)", head);
        Assert.Contains("System.InvalidOperationException: boom", head);
        Assert.DoesNotContain("wavee.log tail", head);
        Assert.Equal(300, tail.Length);
        Assert.Equal("line 400", tail[0]);
        Assert.Equal("line 699", tail[^1]);
    }

    [Fact]
    public void SplitCrashReport_TailShorterThanTheCap_KeepsItWhole()
    {
        var tailLines = Enumerable.Range(0, 5).Select(i => $"line {i}").ToArray();
        var (_, tail) = ReportBundle.SplitCrashReport("Wavee crash report\nversion=0.2.5.6\nwavee.log tail\n--------------\n" + string.Join('\n', tailLines));
        Assert.Equal(tailLines, tail);
    }

    [Fact]
    public void SplitCrashReport_WithoutATailSection_IsAllHead()
    {
        var (head, tail) = ReportBundle.SplitCrashReport("Wavee crash report\nversion=0.2.5.6\n\n");
        Assert.Equal("Wavee crash report\nversion=0.2.5.6", head);
        Assert.Empty(tail);
    }

    [Fact]
    public void ExceptionSummary_IsTheFirstLineAfterTheExceptionMarker()
        => Assert.Equal("System.InvalidOperationException: boom", ReportBundle.ExceptionSummary(CrashHead()));

    [Theory]
    [InlineData("Wavee crash report\nversion=0.2.5.6")]
    [InlineData("Wavee crash report\nException\n---------\n")]
    public void ExceptionSummary_WithoutAnExceptionLine_IsEmpty(string head) => Assert.Equal("", ReportBundle.ExceptionSummary(head));
}

public class IssueFormUrlTests
{
    static string E(string s) => Uri.EscapeDataString(s);
    static KeyValuePair<string, string> F(string id, string value) => new(id, value);
    static ReportIdentity Plain() => new("0.2.5", ReportChannels.InstallSources[0], "x64", "Windows 11 (build 26100)", "0.2.5.0", "", "stable");

    [Fact]
    public void Build_Crash_ArmSideload_ProducesTheExactUrl()
    {
        var id = new ReportIdentity("0.2.5 Breaker (0.2.5.6) · 7e209e37", ReportChannels.InstallSources[1], "ARM64",
            "Windows 11 (build 26100)", "0.2.5.6", "7e209e37", "stable");
        const string title = "App freezes when switching output device";
        const string doing = "Trying to switch outputs while a track is playing.";
        var labels = new[] { id.ArchLabel, id.InstallLabel };

        var url = IssueFormUrl.Build(ReportKind.Crash, id, title,
            new[] { F("when", "During playback"), F("reproduces", "Every time"), F("what-were-you-doing", doing) }, labels);

        Assert.Equal("https://github.com/christosk92/WaveeMusic/issues/new?template=crash_report.yml"
            + "&title=" + E("[Crash]: " + title) + "&version=" + E(id.VersionLine) + "&install-source=" + E(id.InstallSource)
            + "&architecture=" + E(id.Architecture) + "&windows-version=" + E(id.WindowsVersion)
            + "&labels=" + E(string.Join(",", labels)) + "&when=" + E("During playback") + "&reproduces=" + E("Every time")
            + "&what-were-you-doing=" + E(doing), url);
        Assert.Equal("arch: arm64", id.ArchLabel);
        Assert.Equal("install: sideload", id.InstallLabel);
    }

    [Fact]
    public void Build_Bug_X64Store_ProducesTheExactUrl()
    {
        var id = new ReportIdentity("0.2.4 Breaker (0.2.4.5)", ReportChannels.InstallSources[0], "x64", "Windows 10 (build 19045)", "0.2.4.5", "", "store");
        var labels = new[] { id.ArchLabel, id.InstallLabel, "area: playback" };

        var url = IssueFormUrl.Build(ReportKind.Bug, id, "Cover art missing",
            new[] { F("what-happened", "Cover art does not load"), F("steps-to-reproduce", "Scroll fast"), F("expected-behaviour", "Covers load") }, labels);

        Assert.Equal("https://github.com/christosk92/WaveeMusic/issues/new?template=bug_report.yml"
            + "&title=" + E("[Bug]: Cover art missing") + "&version=" + E(id.VersionLine) + "&install-source=" + E(id.InstallSource)
            + "&architecture=" + E(id.Architecture) + "&windows-version=" + E(id.WindowsVersion)
            + "&labels=" + E(string.Join(",", labels)) + "&what-happened=" + E("Cover art does not load")
            + "&steps-to-reproduce=" + E("Scroll fast") + "&expected-behaviour=" + E("Covers load"), url);
    }

    [Fact]
    public void Build_Question_HasNoTemplateNoIdentityAndNoLabels()
    {
        var url = IssueFormUrl.Build(ReportKind.Question, Plain(), "Gapless playback question",
            new[] { F("body", "Does Wavee support gapless playback") }, Array.Empty<string>());
        Assert.Equal("https://github.com/christosk92/WaveeMusic/discussions/new?category=q-a"
            + "&title=" + E("Gapless playback question") + "&body=" + E("Does Wavee support gapless playback"), url);
        Assert.DoesNotContain("labels=", url);
        Assert.DoesNotContain("template=", url);
        Assert.DoesNotContain("version=", url);
    }

    [Fact]
    public void Build_Idea_UsesTheIdeasCategory()
        => Assert.StartsWith("https://github.com/christosk92/WaveeMusic/discussions/new?category=ideas&title=",
            IssueFormUrl.Build(ReportKind.Idea, Plain(), "A dark AMOLED theme", new[] { F("body", "Pure black") }, Array.Empty<string>()));

    [Fact]
    public void Build_OverBudget_NeverExceedsTheBudget_AndKeepsIdentityIntact()
    {
        var id = new ReportIdentity("0.2.5 Breaker (0.2.5.6) · 7e209e37", ReportChannels.InstallSources[1], "ARM64",
            "Windows 11 (build 26100)", "0.2.5.6", "7e209e37", "stable");
        string big = new('a', 5000);

        var url = IssueFormUrl.Build(ReportKind.Bug, id, "Everything is slow",
            new[] { F("what-happened", big), F("steps-to-reproduce", big), F("expected-behaviour", big) }, new[] { id.ArchLabel, id.InstallLabel });

        Assert.True(url.Length <= IssueFormUrl.Budget, $"url was {url.Length} chars");
        Assert.Contains("version=" + E(id.VersionLine), url);
        Assert.Contains("install-source=" + E(id.InstallSource), url);
        Assert.Contains("architecture=" + E(id.Architecture), url);
        Assert.Contains("windows-version=" + E(id.WindowsVersion), url);
        Assert.DoesNotContain(big, url);
    }

    [Fact]
    public void Build_WellUnderBudget_LeavesEveryFieldWhole()
    {
        var url = IssueFormUrl.Build(ReportKind.Feature, Plain(), "Add a queue filter",
            new[] { F("problem", "Hard to find a track"), F("proposal", "Add a search box") }, Array.Empty<string>());
        Assert.Contains("problem=" + E("Hard to find a track"), url);
        Assert.Contains("proposal=" + E("Add a search box"), url);
        Assert.DoesNotContain("…", url);
    }

    [Fact]
    public void Build_ThrowsOnAnInvalidArchitectureValue()
        => Assert.Throws<ArgumentException>(() =>
            IssueFormUrl.Build(ReportKind.Crash, Plain(), "title", new[] { F("architecture", "arm64") }, Array.Empty<string>()));

    [Fact]
    public void Build_ThrowsOnAnInvalidReproducesValue_TrailingWhitespaceIsNotAMatch()
        => Assert.Throws<ArgumentException>(() =>
            IssueFormUrl.Build(ReportKind.Crash, Plain(), "title", new[] { F("reproduces", "Sometimes ") }, Array.Empty<string>()));

    [Fact]
    public void Build_AcceptsTheExactDropdownStrings()
    {
        var url = IssueFormUrl.Build(ReportKind.Crash, Plain(), "title", new[] { F("reproduces", "Sometimes"), F("when", "Randomly") }, Array.Empty<string>());
        Assert.Contains("when=Randomly", url);
        Assert.Contains("reproduces=Sometimes", url);
    }
}

public class ReportRedactorTests
{
    [Theory]
    [InlineData(@"C:\Users\bob\AppData\Local\Wavee\logs\wavee.log", @"C:\Users\<user>\AppData\Local\Wavee\logs\wavee.log")]
    [InlineData("c:/users/Bob/x", "c:/users/<user>/x")]
    [InlineData(@"%USERPROFILE%\x", @"<user-profile>\x")]
    [InlineData("/Users/bob/Library", "/Users/<user>/Library")]
    [InlineData(@"\\NAS01\share", @"\\<host>\share")]
    [InlineData("spotify:user:abc123", "spotify:user:<id>")]
    [InlineData("bob@example.com", "<email>")]
    [InlineData("Authorization: Bearer eyJhbGciOiJIUzI1NiJ9", "Authorization: Bearer <token>")]
    [InlineData("access_token=abc&refresh_token=def", "access_token=<redacted>&refresh_token=<redacted>")]
    [InlineData("country=NL product=premium", "country=<redacted> product=<redacted>")]
    [InlineData("ip=192.168.1.20", "ip=<ip>")]
    [InlineData("fe80::1%12", "<ip6>")]
    [InlineData("2001:db8::ff00:42:8329", "<ip6>")]
    [InlineData("AA-BB-CC-DD-EE-FF", "<mac>")]
    [InlineData("deviceId=0123456789abcdef0123", "deviceId=<device-id>")]
    public void Redact_AppliesEachRule(string before, string after) => Assert.Equal(after, ReportRedactor.Redact(before, RedactionRules.None));

    [Fact]
    public void Redact_EmptyOrNull_ReturnsEmptyString()
    {
        Assert.Equal("", ReportRedactor.Redact("", RedactionRules.None));
        Assert.Equal("", ReportRedactor.Redact(null!, RedactionRules.None));
    }

    [Fact]
    public void Redact_InjectedLiteral_MatchesWholeWordsOnly()
    {
        var rules = RedactionRules.None with { DisplayName = "christos" };
        Assert.Equal("<display-name> · https://github.com/christosk92/WaveeMusic/releases",
            ReportRedactor.Redact("christos · https://github.com/christosk92/WaveeMusic/releases", rules));
        Assert.Equal("(<display-name>)", ReportRedactor.Redact("(Christos)", rules));
    }

    /// <summary>0.2.9 lost the text in front of the first BOUNDED hit whenever an unbounded one came before it (its one
    /// cursor moved past the unbounded hit before a builder existed).</summary>
    [Fact]
    public void Redact_InjectedLiteral_KeepsTheTextBeforeAnUnboundedHit()
    {
        var rules = RedactionRules.None with { DisplayName = "christos" };
        Assert.Equal("https://github.com/christosk92/WaveeMusic by <display-name>",
            ReportRedactor.Redact("https://github.com/christosk92/WaveeMusic by christos", rules));
    }

    [Fact]
    public void Redact_DeviceNamedAfterTheApp_IsNeverRedacted()
    {
        var rules = RedactionRules.None with { DeviceNames = ["Wavee", "Kitchen speaker"] };
        Assert.Equal("Wavee 0.2.5-dev on <device> · WaveeMusic", ReportRedactor.Redact("Wavee 0.2.5-dev on Kitchen speaker · WaveeMusic", rules));
    }

    [Fact]
    public void Redact_InjectedUserName_IsReplaced_CaseInsensitive()
        => Assert.Equal("Logged in as <user> today", ReportRedactor.Redact("Logged in as CHRIS today", RedactionRules.None with { UserName = "chris" }));

    [Fact]
    public void Redact_InjectedMachineName_IsReplaced_CaseInsensitive()
        => Assert.Equal("host=<machine>", ReportRedactor.Redact("host=desktop1", RedactionRules.None with { MachineName = "DESKTOP1" }));

    [Fact]
    public void Redact_InjectedSpotifyUserId_IsReplaced()
        => Assert.Equal("owner: <spotify-user>", ReportRedactor.Redact("owner: abc123spotify", RedactionRules.None with { SpotifyUserId = "abc123spotify" }));

    [Fact]
    public void Redact_InjectedDisplayName_IsReplaced()
        => Assert.Equal("signed in as <display-name>", ReportRedactor.Redact("signed in as Bob Smith", RedactionRules.None with { DisplayName = "Bob Smith" }));

    [Fact]
    public void Redact_InjectedDeviceNames_AreEachReplaced()
        => Assert.Equal("active: <device>, idle: <device>",
            ReportRedactor.Redact("active: Kitchen Speaker, idle: Office PC", RedactionRules.None with { DeviceNames = new List<string> { "Kitchen Speaker", "Office PC" } }));

    [Fact]
    public void Redact_InjectedLiteral_UnderThreeChars_IsSkipped()
        => Assert.Equal("Alright then", ReportRedactor.Redact("Alright then", RedactionRules.None with { UserName = "Al" }));

    [Fact]
    public void Redact_InjectedLiteral_ExactlyThreeChars_IsReplaced()
        => Assert.Equal("hi <user>", ReportRedactor.Redact("hi BOB", RedactionRules.None with { UserName = "bob" }));

    [Theory]
    [InlineData("spotify:track:4uLU6hMCjMI75M1A2tKUQC")]
    [InlineData("version=0.2.5.6")]
    [InlineData("0.2.5 Breaker (0.2.5.6)")]
    [InlineData("t=12:34:56.789")]
    [InlineData("seq=1 tid=5")]
    [InlineData("Wavee!<BaseAddress>+0x7b1fc6")]
    [InlineData("user")]
    [InlineData("key of C")]
    public void Redact_NeverTouchesTheseFalsePositives(string text) => Assert.Equal(text, ReportRedactor.Redact(text, RedactionRules.None));

    [Fact]
    public void Redact_IsIdempotent_OverASyntheticLog()
    {
        var rules = new RedactionRules("chris", "DESKTOP-CHRIS", "31abc123xyz", "Chris K", new List<string> { "Living Room Speaker" });
        var sb = new StringBuilder();
        string[] seeds =
        [
            @"C:\Users\chris\AppData\Local\Wavee\logs\wavee.log opened", "user=CHRIS machine=desktop-chris",
            "spotify:user:31abc123xyz signed in", "contact chris@example.com for support",
            "Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.abcdef", "access_token=zzz&refresh_token=yyy",
            "country=NL product=premium tier=free", "connected to 192.168.1.20 via fe80::1%12",
            "peer 2001:db8::ff00:42:8329 mac AA-BB-CC-DD-EE-FF", "deviceId=0123456789abcdef0123 name=Living Room Speaker",
            "display name: Chris K", @"\\NAS01\share\music", "spotify:track:4uLU6hMCjMI75M1A2tKUQC playing",
            "version=0.2.5.6 quad=0.2.5.6", "t=12:34:56.789 seq=1 tid=5",
        ];
        for (int i = 0; i < 40; i++) sb.AppendLine(i < seeds.Length ? seeds[i] : $"line {i}: nothing interesting here");

        string once = ReportRedactor.Redact(sb.ToString(), rules);
        Assert.Equal(once, ReportRedactor.Redact(once, rules));
    }
}

public class ReportFormTests
{
    static readonly ReportLabels L = new("Title", "When", "Reproduces", "Doing", "Happened", "Steps", "Expected", "Area",
        "Problem", "Proposal", "Alternatives", "Details");

    static readonly ReportIdentity Id = new("0.3.0 Breaker (0.3.0.1)", ReportChannels.InstallSources[1], "ARM64",
        "Windows 11 (build 26100)", "0.3.0.1", "", "stable");

    static int AreaIndex(string slug) => Array.IndexOf(ReportChannels.Areas, slug);
    static readonly int NotSure = ReportChannels.Areas.Length - 1;

    [Theory]
    [InlineData(ReportKind.Bug, null, false, ReportKind.Bug)]
    [InlineData(ReportKind.Feature, null, false, ReportKind.Feature)]
    [InlineData(ReportKind.Crash, null, false, ReportKind.Crash)]          // wavee://open?route=report&arg=crash
    [InlineData(ReportKind.Bug, null, true, ReportKind.Crash)]             // the relaunch prompt
    [InlineData(ReportKind.Question, "C:\\logs\\crash-report-1.txt", false, ReportKind.Crash)]   // a listed file's "Report…"
    [InlineData(ReportKind.Idea, "", false, ReportKind.Idea)]
    public void EffectiveKind_ForcesCrashFromAnyCrashSignal(ReportKind kind, string? path, bool prompt, ReportKind expected)
        => Assert.Equal(expected, ReportForm.EffectiveKind(kind, path, prompt));

    [Theory]
    [InlineData(ReportKind.Crash, true)]
    [InlineData(ReportKind.Bug, true)]
    [InlineData(ReportKind.Feature, false)]
    [InlineData(ReportKind.Question, false)]
    [InlineData(ReportKind.Idea, false)]
    public void IncludesLogsByDefault_OnlyForCrashAndBug(ReportKind kind, bool expected) => Assert.Equal(expected, ReportForm.IncludesLogsByDefault(kind));

    [Fact]
    public void AreaLine_IsEmptyForNotSure_AndALabelledLineOtherwise()
    {
        Assert.Equal("", ReportForm.AreaLine("Area", NotSure));
        Assert.Equal("Area: lyrics\n\n", ReportForm.AreaLine("Area", AreaIndex("lyrics")));
    }

    [Fact]
    public void DropdownIndices_OutOfRange_ClampToTheFirstOption()
    {
        Assert.Equal("On launch", ReportForm.WhenAt(99));
        Assert.Equal("Every time", ReportForm.ReproducesAt(-1));
        Assert.Equal("playback", ReportForm.AreaAt(ReportChannels.Areas.Length));
    }

    [Fact]
    public void Labels_AddAnAreaLabelOnlyForABugWithAPickedArea()
    {
        Assert.Equal(new[] { "arch: arm64", "install: sideload", "area: video" }, ReportForm.Labels(ReportKind.Bug, Id, AreaIndex("video")));
        Assert.Equal(new[] { "arch: arm64", "install: sideload" }, ReportForm.Labels(ReportKind.Bug, Id, NotSure));
        Assert.Equal(new[] { "arch: arm64", "install: sideload" }, ReportForm.Labels(ReportKind.Feature, Id, AreaIndex("video")));
        Assert.Empty(ReportForm.Labels(ReportKind.Question, Id, AreaIndex("video")));
        Assert.Empty(ReportForm.Labels(ReportKind.Idea, Id, NotSure));
    }

    [Fact]
    public void Answers_SkipAnEmptyTitle_AndFollowTheKindsFieldOrder()
    {
        var answers = ReportForm.Answers(ReportKind.Feature, L, RedactionRules.None,
            new ReportDraft("  ", "p", "q", "r", 0, 0, AreaIndex("home")));
        Assert.Equal(new[] { "Problem", "Proposal", "Area", "Alternatives" }, answers.Select(a => a.Label).ToArray());
        Assert.Equal("home", answers[2].Text);
    }

    [Fact]
    public void Answers_RedactTheFreeTextWithTheGivenRules()
    {
        var answers = ReportForm.Answers(ReportKind.Bug, L, RedactionRules.None with { UserName = "chris" },
            new ReportDraft("Crash on seek", @"C:\Users\chris\x", "", "", 0, 0, NotSure));
        Assert.Equal(("Title", "Crash on seek"), answers[0]);
        Assert.Equal(@"C:\Users\<user>\x", answers[1].Text);
    }

    [Fact]
    public void UrlFields_Crash_WritesTheDropdownAnswersIntoTheTextField()
    {
        var fields = ReportForm.UrlFields(ReportKind.Crash, L, Id, RedactionRules.None, new ReportDraft("", "seeking", "", "", 1, 2, NotSure));
        Assert.Equal(new[] { "when", "reproduces", "what-were-you-doing" }, fields.Select(f => f.Key).ToArray());
        Assert.Equal("During playback", fields[0].Value);
        Assert.Equal("Once so far", fields[1].Value);
        Assert.Equal("When: During playback \u00b7 Reproduces: Once so far\n\nseeking", fields[2].Value);
    }

    [Fact]
    public void UrlFields_Bug_PrefixesThePickedArea_AndDiscussionsCarryTheIdentity()
    {
        var bug = ReportForm.UrlFields(ReportKind.Bug, L, Id, RedactionRules.None, new ReportDraft("", "it broke", "", "", 0, 0, AreaIndex("connect")));
        Assert.Equal("Area: connect\n\nit broke", bug[0].Value);

        var idea = ReportForm.UrlFields(ReportKind.Idea, L, Id, RedactionRules.None, new ReportDraft("", "pure black", "", "", 0, 0, NotSure));
        Assert.Equal("pure black\n\n0.3.0 Breaker (0.3.0.1) · ARM64 · Windows 11 (build 26100)", Assert.Single(idea).Value);
    }

    [Fact]
    public void UrlFields_AreAcceptedByTheUrlValidator_ForEveryKind()
    {
        foreach (var kind in new[] { ReportKind.Crash, ReportKind.Bug, ReportKind.Feature, ReportKind.Question, ReportKind.Idea })
        {
            var draft = new ReportDraft("t", "a", "b", "c", 3, 1, AreaIndex("sidebar"));
            string url = IssueFormUrl.Build(kind, Id, "t", ReportForm.UrlFields(kind, L, Id, RedactionRules.None, draft), ReportForm.Labels(kind, Id, draft.Area));
            Assert.StartsWith(ReportChannels.Repo, url);
        }
    }

    [Theory]
    [InlineData(" typed ", "System.Exception: x", "typed")]
    [InlineData("", "System.Exception: x", "System.Exception: x")]
    [InlineData("   ", "", "Wavee closed unexpectedly")]
    public void UrlTitle_FallsBackToTheCrashSummaryThenTheCrashTitle(string title, string summary, string expected)
        => Assert.Equal(expected, ReportForm.UrlTitle(title, summary, "Wavee closed unexpectedly"));
}
