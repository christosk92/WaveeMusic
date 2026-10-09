// ── Wavee.Tests/DrawerExtentRuleTests.cs — the drawer toggle's measured-extent writes (Entities/Track.Rules.cs) ─────────
//
// Pure: the decision TableHost.CorrectDrawerExtents applies on every toggle (fluent-gpu smooth-reveal plan §13.2).

using System;
using Xunit;

namespace Wavee.Tests;

public sealed class DrawerExtentRuleTests
{
    static int Display(string key) => key switch { "a" => 3, "b" => 7, _ => -1 };

    [Fact]
    public void OpeningWithNoPreviousDrawerWritesNothingUntilTheDrawerIsMeasured()
    {
        Span<DrawerExtentWrite> into = stackalloc DrawerExtentWrite[2];
        Assert.Equal(0, DrawerExtentRule.For("", "a", Display, 40f, 0f, into));
    }

    [Fact]
    public void ClosingWritesTheRowHeightBack()
    {
        Span<DrawerExtentWrite> into = stackalloc DrawerExtentWrite[2];
        Assert.Equal(1, DrawerExtentRule.For("a", "", Display, 40f, 0f, into));
        Assert.Equal(new DrawerExtentWrite(3, 40f), into[0]);
    }

    [Fact]
    public void ASwitchClosesTheOldRowFirstThenOpensTheNewOne()
    {
        Span<DrawerExtentWrite> into = stackalloc DrawerExtentWrite[2];
        Assert.Equal(2, DrawerExtentRule.For("a", "b", Display, 40f, 120f, into));
        Assert.Equal(new DrawerExtentWrite(3, 40f), into[0]);
        Assert.Equal(new DrawerExtentWrite(7, 160f), into[1]);
    }

    [Fact]
    public void AnUnchangedKeyAFilteredOutRowOrAnUnknownRowHeightWritesNothing()
    {
        Span<DrawerExtentWrite> into = stackalloc DrawerExtentWrite[2];
        Assert.Equal(0, DrawerExtentRule.For("a", "a", Display, 40f, 120f, into));
        Assert.Equal(0, DrawerExtentRule.For("gone", "", Display, 40f, 0f, into));
        Assert.Equal(0, DrawerExtentRule.For("a", "", Display, 0f, 0f, into));
    }
}
