using Xunit;

namespace Wavee.Tests;

// ReportKindIndex (Features/Feedback/ReportKindIndex.cs): the Segmented ↔ ReportKind round trip that used to live as
// two private switches on ReportDialogBody. Segments order must track the Segmented items ReportDialogBody.Render
// builds from Strings.Report.KindBug/KindFeature/KindQuestion/KindIdea.
public class ReportKindIndexTests
{
    [Fact]
    public void Segments_IsBugFeatureQuestionIdea_InThatOrder()
    {
        Assert.Equal(
            new[] { ReportKind.Bug, ReportKind.Feature, ReportKind.Question, ReportKind.Idea },
            ReportKindIndex.Segments);
    }

    [Theory]
    [InlineData(ReportKind.Bug, 0)]
    [InlineData(ReportKind.Feature, 1)]
    [InlineData(ReportKind.Question, 2)]
    [InlineData(ReportKind.Idea, 3)]
    public void IndexOf_MatchesTheSegmentedItemOrder(ReportKind kind, int expected)
        => Assert.Equal(expected, ReportKindIndex.IndexOf(kind));

    [Fact]
    public void IndexOf_Crash_FallsBackToBug()
        => Assert.Equal(0, ReportKindIndex.IndexOf(ReportKind.Crash));

    [Theory]
    [InlineData(ReportKind.Bug)]
    [InlineData(ReportKind.Feature)]
    [InlineData(ReportKind.Question)]
    [InlineData(ReportKind.Idea)]
    public void RoundTrips_EveryVisibleKind(ReportKind kind)
        => Assert.Equal(kind, ReportKindIndex.KindAt(ReportKindIndex.IndexOf(kind)));

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(int.MaxValue)]
    public void KindAt_OutOfRange_FallsBackToBug(int index)
        => Assert.Equal(ReportKind.Bug, ReportKindIndex.KindAt(index));
}
