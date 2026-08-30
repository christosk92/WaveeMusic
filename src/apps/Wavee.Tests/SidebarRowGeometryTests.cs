using System;
using Wavee.Core.Sidebar;
using Xunit;

namespace Wavee.Tests;

// SidebarRowGeometry is the engine-free half of the sidebar's row ladder, split out of SidebarRowMetrics precisely so it
// could be pinned here. Two things are under test:
//
//   1. HEIGHT PARITY between the two documents that render the SAME "Your Library" section — Classic's locked built-in
//      document and the Wavee Curated seed template. Both must reach 44 through the ONE ladder. This is the regression
//      the user's screenshots showed (Classic's rows visibly roomier than Curated's), and the reason it is worth a test
//      is that the two section lists are authored in different assemblies by different code paths, so nothing else stops
//      them drifting. NOTE what this canNOT catch: a document already PERSISTED to sidebar-layout.json carries its own
//      density and is never retro-fitted by a template edit — templates seed documents, they do not update them.
//
//   2. The pure plan geometry the selection cue needs: cumulative content-space Y, route→index lookup, and the travel
//      direction (whose 0 case — "unknowable" — is a real answer the indicator depends on, not an error).
public sealed class SidebarRowGeometryTests
{
    // ── 0. THE TREE-CONTENT ORIGIN (the caret's x) ────────────────────────────────────────────────
    //
    // A tree row is NOT laid out on `IndentFor(depth)`. `SidebarEntityRow.TreeLeading` pads the row once at
    // IndentFor(0) and then spends real cells — the 3-DIP selection gutter, one 12-DIP connector cell per level, and a
    // fixed 16-DIP disclosure cell — before the art. The insertion caret used to be translated by IndentFor(depth) and
    // `PickDepth` read the same ladder backwards, so the line painted ~19 DIP left of what it meant and the depth-0
    // band needed x < 12 (F2/F3). One origin now, and these are the numbers.

    [Theory]
    [InlineData(0, 25f)]     // 6 padding + 3 gutter + 0 guides + 16 chevron cell
    [InlineData(1, 37f)]
    [InlineData(2, 49f)]
    [InlineData(4, 73f)]
    [InlineData(9, 73f)]     // past MaxIndentDepth the ladder stops marching right, exactly like IndentFor
    [InlineData(-3, 25f)]
    public void TreeContentX_IsTheSumOfTheRowsOwnLeadingCells(int depth, float expected)
    {
        Assert.Equal(expected, SidebarRowGeometry.TreeContentX(depth), 3);
        // …and it IS a sum of the named constants, not a literal that happens to match.
        int clamped = Math.Clamp(depth, 0, SidebarRowGeometry.MaxIndentDepth);
        Assert.Equal(SidebarRowGeometry.IndentFor(0) + SidebarRowGeometry.SelGutterWidth
                     + clamped * SidebarRowGeometry.TreeGuideStep + SidebarRowGeometry.TreeChevronCell,
                     SidebarRowGeometry.TreeContentX(depth), 3);
    }

    [Fact]
    public void TreeContentX_MarchesOneWholeConnectorCellPerLevel()
    {
        // The step the depth pick reads backwards. If these two ever differ, an outdent lands on the wrong level.
        for (int d = 0; d < SidebarRowGeometry.MaxIndentDepth; d++)
            Assert.Equal(SidebarRowGeometry.TreeGuideStep,
                         SidebarRowGeometry.TreeContentX(d + 1) - SidebarRowGeometry.TreeContentX(d), 3);
        Assert.Equal(SidebarRowGeometry.IndentStep, SidebarRowGeometry.TreeGuideStep);
    }

    // ── 1. the height ladder ─────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(SidebarDensity.Compact, false, 32f)]
    [InlineData(SidebarDensity.Compact, true, 32f)]     // Compact suppresses subtitles outright — no second line, no growth
    [InlineData(SidebarDensity.Cozy, false, 40f)]
    [InlineData(SidebarDensity.Cozy, true, 44f)]        // = Classic's entity row
    [InlineData(SidebarDensity.Comfortable, false, 44f)]// = Classic's glyph/shortcut row
    [InlineData(SidebarDensity.Comfortable, true, 48f)]
    public void HeightFor_IsTheThreeCanonicalHeightsPlusComfortable(SidebarDensity d, bool sub, float expected)
        => Assert.Equal(expected, SidebarRowGeometry.HeightFor(d, sub));

    [Fact]
    public void ClassicHeight_IsTheCozyWithSubtitleHeight()
        => Assert.Equal(SidebarRowGeometry.HeightFor(SidebarDensity.Cozy, true), SidebarRowGeometry.ClassicHeight);

    // (SidebarRowMetrics — the engine-bound facade that now forwards to this ladder — lives in Shared/, which the tests
    // deliberately do not source-include, so its delegation cannot be asserted here. It is one-line forwarding by
    // construction; the file says so, and there is no second copy of the arithmetic left to drift.)

    [Theory]
    [InlineData(-1, 6f)]
    [InlineData(0, 6f)]
    [InlineData(1, 18f)]
    [InlineData(4, 54f)]
    [InlineData(9, 54f)]   // clamped at four levels
    public void IndentFor_IsSixPlusTwelvePerLevelClampedAtFour(int depth, float expected)
        => Assert.Equal(expected, SidebarRowGeometry.IndentFor(depth));

    // ── 2. Classic ⇄ Curated shortcut-row parity (the reported defect) ────────────────────────────────────────────────

    static SidebarSectionSpec Shortcuts(SidebarCustomLayout layout)
    {
        foreach (var s in layout.Sections)
            if (s.Kind == SidebarSectionKind.CollectionShortcuts) return s;
        throw new InvalidOperationException("no CollectionShortcuts section");
    }

    [Fact]
    public void ClassicAndCuratedTemplate_ShortcutRowsAreTheSameHeight()
    {
        var classic = Shortcuts(SidebarBuiltInDocuments.Classic(true, true, true));
        var curated = Shortcuts(SidebarTemplates.Build(SidebarTemplates.Curated));

        Assert.Equal(SidebarRowGeometry.HeightFor(classic.Opts), SidebarRowGeometry.HeightFor(curated.Opts));
        // …and the number itself, so a future "let's make Curated cozier" edit fails HERE instead of in a screenshot.
        Assert.Equal(44f, SidebarRowGeometry.HeightFor(curated.Opts));
    }

    [Fact]
    public void ClassicAndCuratedTemplate_ShortcutRowsShareTheWholeGeometryInput()
    {
        var classic = Shortcuts(SidebarBuiltInDocuments.Classic(true, true, true)).Opts;
        var curated = Shortcuts(SidebarTemplates.Build(SidebarTemplates.Curated)).Opts;

        // Height is Density × Subtitles; the art/glyph shape is Artwork. All three must match or the rows differ in a way
        // the height assertion alone would miss (a 44-DIP row with 40-DIP artwork is not a 44-DIP glyph row).
        Assert.Equal(classic.Density, curated.Density);
        Assert.Equal(classic.Subtitles, curated.Subtitles);
        Assert.Equal(classic.Artwork, curated.Artwork);
    }

    [Fact]
    public void ClassicInspiredTemplate_AlsoMatchesClassicsShortcutHeight()
    {
        // The "Classic-inspired" template exists to reproduce Classic inside an EDITABLE document; if it drifts, a user
        // who picks it gets rows that are not the Classic rows it is named after.
        var classic = Shortcuts(SidebarBuiltInDocuments.Classic(true, true, true));
        var inspired = Shortcuts(SidebarTemplates.Build(SidebarTemplates.ClassicInspired));
        Assert.Equal(SidebarRowGeometry.HeightFor(classic.Opts), SidebarRowGeometry.HeightFor(inspired.Opts));
    }

    // ── 3. pure plan geometry ────────────────────────────────────────────────────────────────────────────────────────

    static readonly float[] MixedExtents = [30f, 44f, 44f, 16f, 30f, 48f, 48f];   // header · 2 rows · divider · header · 2 rows

    static Func<int, float> Extents(float[] e) => i => (uint)i < (uint)e.Length ? e[i] : 0f;

    [Fact]
    public void ContentYOf_IsThePrefixSumOfEveryEarlierRow()
    {
        var extentOf = Extents(MixedExtents);
        int n = MixedExtents.Length;
        Assert.Equal(0f, SidebarRowGeometry.ContentYOf(0, n, extentOf));
        Assert.Equal(30f, SidebarRowGeometry.ContentYOf(1, n, extentOf));
        Assert.Equal(74f, SidebarRowGeometry.ContentYOf(2, n, extentOf));
        Assert.Equal(118f, SidebarRowGeometry.ContentYOf(3, n, extentOf));
        Assert.Equal(134f, SidebarRowGeometry.ContentYOf(4, n, extentOf));
        Assert.Equal(164f, SidebarRowGeometry.ContentYOf(5, n, extentOf));
        Assert.Equal(212f, SidebarRowGeometry.ContentYOf(6, n, extentOf));
    }

    [Fact]
    public void ContentYOf_ClampsBothEnds()
    {
        var extentOf = Extents(MixedExtents);
        int n = MixedExtents.Length;
        float total = 260f;   // the whole MixedExtents sum
        Assert.Equal(0f, SidebarRowGeometry.ContentYOf(-5, n, extentOf));
        Assert.Equal(total, SidebarRowGeometry.ContentYOf(n, n, extentOf));
        Assert.Equal(total, SidebarRowGeometry.ContentYOf(n + 99, n, extentOf));
    }

    [Fact]
    public void ContentYOf_SkipsDegenerateExtents()
    {
        // A zero-height row (the pane's Blank) and a NaN (an unmeasured slot) must contribute nothing rather than
        // poisoning every later offset — one NaN would otherwise make the whole column NaN.
        float[] e = [44f, 0f, float.NaN, 44f];
        Assert.Equal(88f, SidebarRowGeometry.ContentYOf(4, e.Length, Extents(e)));
    }

    [Fact]
    public void IndexOfRoute_FindsTheFirstMatchAndIgnoresNonTargets()
    {
        string?[] routes = [null, "albums", "", "liked", "albums"];
        Assert.Equal(1, SidebarRowGeometry.IndexOfRoute(routes.Length, i => routes[i], "albums"));
        Assert.Equal(3, SidebarRowGeometry.IndexOfRoute(routes.Length, i => routes[i], "liked"));
        Assert.Equal(-1, SidebarRowGeometry.IndexOfRoute(routes.Length, i => routes[i], "podcasts"));
        Assert.Equal(-1, SidebarRowGeometry.IndexOfRoute(routes.Length, i => routes[i], ""));
        Assert.Equal(-1, SidebarRowGeometry.IndexOfRoute(routes.Length, i => routes[i], null));
        // Ordinal, never culture- or case-insensitive: route keys are identifiers.
        Assert.Equal(-1, SidebarRowGeometry.IndexOfRoute(routes.Length, i => routes[i], "Albums"));
    }

    [Theory]
    [InlineData(1, 5, 1)]     // moved down the plan
    [InlineData(5, 1, -1)]    // moved up
    [InlineData(3, 3, 0)]     // same row — nothing travelled
    [InlineData(-1, 4, 0)]    // arriving from off-plan (deep link / collapsed section): direction is unknowable
    [InlineData(4, -1, 0)]    // leaving to off-plan
    [InlineData(-1, -1, 0)]
    public void DirectionOf_IsSignedOnlyWhenBothRowsAreOnThePlan(int from, int to, int expected)
        => Assert.Equal(expected, SidebarRowGeometry.DirectionOf(from, to));
}
