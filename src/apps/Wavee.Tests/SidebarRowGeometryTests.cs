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
    // IndentFor(0) and then spends real cells — the 3-DIP selection gutter and one 12-DIP connector cell per level —
    // before the art, with no reserved disclosure cell any more (W7 moved the folder's chevron to the row's TRAILING
    // cluster, so a tree row's content starts exactly where a depth-0 StandardLeading row's does). The insertion caret
    // used to be translated by IndentFor(depth) and `PickDepth` read the same ladder backwards, so the line painted
    // ~19 DIP left of what it meant and the depth-0 band needed x < 12 (F2/F3). One origin now, and these are the
    // numbers — DELIBERATELY 6 DIP left of the pre-W7 ladder (25/37/49/…) now that the chevron cell is gone.

    [Theory]
    [InlineData(0, 13f)]     // 4 padding + 3 gutter + 6 gap — identical to StandardLeading at depth 0
    [InlineData(1, 25f)]
    [InlineData(2, 37f)]
    [InlineData(4, 61f)]
    [InlineData(9, 61f)]     // past MaxIndentDepth the ladder stops marching right, exactly like IndentFor
    [InlineData(-3, 13f)]
    public void TreeContentX_IsTheSumOfTheRowsOwnLeadingCells(int depth, float expected)
    {
        Assert.Equal(expected, SidebarRowGeometry.TreeContentX(depth), 3);
        // …and it IS a sum of the named constants, not a literal that happens to match.
        int clamped = Math.Clamp(depth, 0, SidebarRowGeometry.MaxIndentDepth);
        Assert.Equal(SidebarRowGeometry.IndentFor(0) + SidebarRowGeometry.LeadingLaneWidth
                     + clamped * SidebarRowGeometry.TreeGuideStep,
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

    // ── 0b. THE ONE LEADING LANE (the art/glyph column's x, and the label that follows it) ──────────────────────────────
    //
    // W7's whole point: one art column, one label x, for every row SHAPE (art / bare glyph / tree) at a given density,
    // and for the fixed chrome bands mounted above the list too (SidebarPaneMetrics.LeadInset, not source-included
    // here, is asserted equal to this same sum by SidebarPaneInvariantTests).

    [Fact]
    public void ArtX_AtDepthZero_Is21_TheContentLanePlusTheLeadingLane()
    {
        // SidebarPaneMetrics.LeadInset is engine-bound (not source-included by Wavee.Tests) — this is the identity it
        // is pinned to. ContentLane (14) is PaneEdge + IndentFor(0); LeadingLaneWidth (13) is SelGutter + LeadingGap.
        Assert.Equal(21f, SidebarRowGeometry.ArtX(0));
        Assert.Equal(SidebarRowGeometry.ContentLane + SidebarRowGeometry.LeadingLaneWidth, SidebarRowGeometry.ArtX(0));
    }

    [Theory]
    [InlineData(SidebarDensity.Compact, 20f)]
    [InlineData(SidebarDensity.Cozy, 32f)]
    [InlineData(SidebarDensity.Comfortable, 40f)]
    public void ArtFor_IsTheThreeCanonicalArtSizes(SidebarDensity density, float expected)
        => Assert.Equal(expected, SidebarRowGeometry.ArtFor(density));

    [Theory]
    [InlineData(SidebarDensity.Compact, 47f)]     // 21 + 20 + 6
    [InlineData(SidebarDensity.Cozy, 59f)]        // 21 + 32 + 6
    [InlineData(SidebarDensity.Comfortable, 67f)] // 21 + 40 + 6
    public void GlyphRowAndArtRow_AtOneDensity_ShareOneLabelX(SidebarDensity density, float expectedLabelX)
    {
        // SidebarEntityRow.Create builds an ArtFor(density)-wide leading column for BOTH the bare-glyph arm (the icon
        // now centres inside it) and the art arm, with the SAME LeadingGap before the text either way — the
        // bareGlyph ⇒ gap 12 / no-art-column special case this pins the absence of. One formula, one label x, whether
        // the row shows a glyph (Home, Liked) or cover art (a playlist) at the same density.
        float labelX = SidebarRowGeometry.ArtX(0) + SidebarRowGeometry.ArtFor(density) + SidebarRowGeometry.LeadingGap;
        Assert.Equal(expectedLabelX, labelX);
    }

    // ── 1. the height ladder ─────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(SidebarDensity.Compact, false, 32f)]
    [InlineData(SidebarDensity.Compact, true, 32f)]     // Compact suppresses subtitles outright — no second line, no growth
    [InlineData(SidebarDensity.Cozy, false, 40f)]
    [InlineData(SidebarDensity.Cozy, true, 44f)]        // = Classic's entity row, AND (since W7) its glyph/shortcut row
    [InlineData(SidebarDensity.Comfortable, false, 44f)]// also 44 — but a 40-DIP art column, no longer used for Shortcuts/Links
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
    [InlineData(-1, 4f)]
    [InlineData(0, 4f)]
    [InlineData(1, 16f)]
    [InlineData(4, 52f)]
    [InlineData(9, 52f)]   // clamped at four levels
    public void IndentFor_IsFourPlusTwelvePerLevelClampedAtFour(int depth, float expected)
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
