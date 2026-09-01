using System.Linq;
using Xunit;

namespace Wavee.Tests;

// ReportChannels (Diagnostics/ReportKinds.cs): the routing table. The dropdown option arrays are copied verbatim
// from .github/ISSUE_TEMPLATE/*.yml -- GitHub silently drops a prefilled value that doesn't match one of these
// strings exactly, so the expected lists here are HARD-CODED (never read from the YAML -- no source-text tests).
public class ReportChannelsTests
{
    [Fact]
    public void InstallSources_MatchTheIssueFormDropdown()
    {
        Assert.Equal(
            new[] { "Microsoft Store", "Sideloaded (.appinstaller or .msix from GitHub)", "Built from source" },
            ReportChannels.InstallSources);
    }

    [Fact]
    public void Architectures_MatchTheIssueFormDropdown()
    {
        Assert.Equal(new[] { "x64", "ARM64", "Not sure" }, ReportChannels.Architectures);
    }

    [Fact]
    public void When_MatchesTheIssueFormDropdown()
    {
        Assert.Equal(
            new[]
            {
                "On launch", "During playback", "When switching video on or off", "When navigating pages",
                "After an update", "Randomly", "Other",
            },
            ReportChannels.When);
    }

    [Fact]
    public void Reproduces_MatchesTheIssueFormDropdown()
    {
        Assert.Equal(new[] { "Every time", "Sometimes", "Once so far" }, ReportChannels.Reproduces);
    }

    [Fact]
    public void Areas_MatchTheIssueFormDropdown()
    {
        Assert.Equal(
            new[]
            {
                "playback", "video", "lyrics", "player", "connect", "library", "playlists", "search", "home",
                "browse", "concerts", "detail-pages", "sidebar", "shell", "auth", "setup", "updates", "store",
                "release-tooling", "diagnostics", "modules", "i18n", "engine", "Not sure",
            },
            ReportChannels.Areas);
    }

    [Fact]
    public void Crash_Channel_HasTheDocumentedShape()
    {
        var ch = ReportChannels.Crash;
        Assert.Equal(ReportKind.Crash, ch.Kind);
        Assert.Equal("/issues/new", ch.Path);
        Assert.Equal("[Crash]: ", ch.TitlePrefix);
        Assert.Equal("crash_report.yml", ch.Template);
        Assert.Null(ch.Category);
        Assert.Equal(
            new[] { "version", "install-source", "architecture", "windows-version", "when", "reproduces", "what-were-you-doing" },
            ch.FieldIds);
        Assert.Equal(new[] { "what-were-you-doing" }, ch.TruncationOrder);
        Assert.Equal("Crash report", ch.PasteBox);
    }

    [Fact]
    public void Bug_Channel_HasTheDocumentedShape()
    {
        var ch = ReportChannels.Bug;
        Assert.Equal(ReportKind.Bug, ch.Kind);
        Assert.Equal("bug_report.yml", ch.Template);
        Assert.Equal(
            new[] { "version", "install-source", "architecture", "windows-version", "what-happened", "steps-to-reproduce", "expected-behaviour" },
            ch.FieldIds);
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
    public void For_RoundTripsEveryKind(ReportKind kind)
        => Assert.Equal(kind, ReportChannels.For(kind).Kind);

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
}
