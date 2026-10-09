// ── Wavee.Tests/ActionPickRulesTests.cs — the destination picker's pure half ─────────────────────────────────────────
//
// `Actions.PickRules` (Platform/Actions.Rules.cs): the destination picker's live filter.

using Xunit;

namespace Wavee.Tests;

public class ActionPickRulesTests
{
    [Theory]
    [InlineData(null, "Road trip", false, true)]
    [InlineData("", "Road trip", false, true)]
    [InlineData("trip", "Road trip", false, true)]
    [InlineData("  TRIP ", "Road trip", false, true)]   // trimmed, case-insensitive
    [InlineData("deep", "Road trip", false, false)]
    [InlineData("deep", "Top level", true, true)]        // a pinned row survives every query: the way back
    public void The_destination_filter_never_hides_a_pinned_row(string? query, string label, bool pinned, bool shown)
        => Assert.Equal(shown, Actions.PickRules.Matches(query, label, pinned));
}
