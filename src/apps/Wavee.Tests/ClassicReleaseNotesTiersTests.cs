// ── Wavee.Tests/ClassicReleaseNotesTiersTests.cs — Classic's What's-new page and its overlays per tier (themes plan 3.5, 3.6; TC8) ─────────────────
//
// Pure rules, no host: the records the page, the highlight viewer and the after-update plate read (`ClassicReleaseNotesTiers`) carry the numbers of the boards
// `C-ReleaseNotes-Compact` / `-Small` / `-Huge`, and Standard is the tuned page unchanged. Asserted on the records, never on pixels.

using FluentGpu.Controls;
using Wavee.Themes.Classic;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.Tests;

public class ClassicReleaseNotesTiersTests
{
    static ClassicReleaseNotesLayout Page(WidthTier tier, bool shortWindow = false) => ClassicReleaseNotesTiers.For(tier, shortWindow);

    [Fact]
    public void Compact_IsAPickerUnderTheTitle_AndEveryTargetIs40()
    {
        var l = Page(WidthTier.Compact);
        Assert.Equal(ClassicReleaseRailForm.Picker, l.RailForm);
        Assert.False(l.PickerInTitleRow);
        Assert.Equal(40f, l.PickerHeight);                 // C-ReleaseNotes-Compact: the release button is 40 tall
        Assert.Equal(40f, l.Target);
        Assert.Equal(0f, l.RailWidth);
        Assert.False(l.TitleGlyph);
        Assert.Equal(24f, l.HeadlineSize);                 // 24/30 headline
        Assert.Equal(ClassicReleaseHeroActions.GridBelow, l.HeroActions);
        Assert.Equal(ControlSize.Large, l.ActionSize);
        Assert.Equal(ClassicReleaseHighlightForm.Column, l.HighlightForm);
        Assert.Equal(44f, l.RowMinH);                      // section rows min 44
        Assert.Equal(1, l.SectionColumns);
        Assert.True(float.IsNaN(l.BodyMaxW));
        Assert.Equal(12f, l.PageTop);
    }

    [Fact]
    public void Small_IsAPickerInTheTitleRow_WithTwoColumnsOfRowCards()
    {
        var l = Page(WidthTier.Small);
        Assert.Equal(ClassicReleaseRailForm.Picker, l.RailForm);
        Assert.True(l.PickerInTitleRow);
        Assert.Equal(32f, l.PickerHeight);                 // C-ReleaseNotes-Small: the button is 32 tall
        Assert.Equal(0f, l.Target);
        Assert.Equal(28f, l.HeadlineSize);
        Assert.Equal(ClassicReleaseHeroActions.RowBelow, l.HeroActions);
        Assert.Equal(ClassicReleaseHighlightForm.Grid2, l.HighlightForm);
        Assert.Equal(40f, l.RowMinH);
        Assert.Equal(16f, l.PageTop);
    }

    [Fact]
    public void Standard_IsTheTunedPage()
    {
        var l = Page(WidthTier.Standard);
        Assert.Equal(ClassicReleaseRailForm.Rail, l.RailForm);
        Assert.Equal(208f, l.RailWidth);                   // the rail the page always had
        Assert.Equal(14f, l.FrameGap);
        Assert.Equal(ClassicReleaseHighlightForm.Strip, l.HighlightForm);
        Assert.Equal(ClassicReleaseHeroActions.StackBeside, l.HeroActions);
        Assert.Equal(ControlSize.Small, l.ActionSize);
        Assert.Equal(30f, l.HeadlineSize);
        Assert.Equal(16f, l.BodyGap);
        Assert.Equal(10f, l.HighlightGap);
        Assert.Equal(0f, l.RowMinH);
        Assert.Equal(1, l.SectionColumns);
        Assert.True(float.IsNaN(l.BodyMaxW));
        Assert.True(l.TitleGlyph);
    }

    [Fact]
    public void Huge_CapsTheBodyAt1600_LeftAligned_WithThreeSectionColumnsAndA280Rail()
    {
        var l = Page(WidthTier.Huge);
        Assert.Equal(1600f, l.BodyMaxW);                   // C-ReleaseNotes-Huge: max-width 1600
        Assert.Equal(ClassicReleaseRailForm.Rail, l.RailForm);
        Assert.Equal(280f, l.RailWidth);
        Assert.Equal(32f, l.FrameGap);
        Assert.Equal(3, l.SectionColumns);
        Assert.Equal(24f, l.SectionColumnGap);
        Assert.Equal(ClassicReleaseHeroActions.RowBeside, l.HeroActions);
        Assert.True(l.HighlightsHeading);                  // the 20/28 Highlights heading
        Assert.Equal(ClassicReleaseHighlightForm.Strip, l.HighlightForm);   // three cards, each at most 420 (the card's own cap)
        Assert.Equal(24f, l.PageTop);
    }

    [Fact]
    public void Huge4K_ReadsTheHugeRecord()
    {
        Assert.Same(Page(WidthTier.Huge), Page(WidthTier.Huge4K));
        Assert.Same(ClassicReleaseNotesTiers.OverlayFor(WidthTier.Huge, false), ClassicReleaseNotesTiers.OverlayFor(WidthTier.Huge4K, false));
    }

    [Theory]
    [InlineData(WidthTier.Compact)]
    [InlineData(WidthTier.Small)]
    [InlineData(WidthTier.Standard)]
    [InlineData(WidthTier.Huge)]
    public void ShortWindow_DropsTheHeroMeta_AndTightensTheTitleRow_WithoutChangingTheForm(WidthTier tier)
    {
        var regular = Page(tier);
        var shortRecord = Page(tier, shortWindow: true);
        Assert.True(shortRecord.Short);
        Assert.False(shortRecord.HeroMeta);
        Assert.True(regular.HeroMeta);
        Assert.Equal(ClassicReleaseNotesTiers.ShortPageTop, shortRecord.PageTop);
        Assert.Equal(ClassicReleaseNotesTiers.ShortTitleBottom, shortRecord.TitleBottom);
        Assert.Equal(regular.RailForm, shortRecord.RailForm);
        Assert.Equal(regular.HighlightForm, shortRecord.HighlightForm);
        Assert.Equal(regular.Target, shortRecord.Target);
        Assert.Same(shortRecord, Page(tier, shortWindow: true));   // cached: a re-render's props compare equal
    }

    [Fact]
    public void ThePickerExistsExactlyWhereTheRailDoesNot()
    {
        foreach (var tier in new[] { WidthTier.Compact, WidthTier.Small, WidthTier.Standard, WidthTier.Huge })
        {
            var l = Page(tier);
            Assert.Equal(!l.HasRail, l.PickerHeight > 0f);
            Assert.Equal(l.HasRail, l.RailWidth > 0f);
            Assert.Equal(l.HasRail, l.FrameGap > 0f);
        }
    }

    [Fact]
    public void AFactlessRead_IsStandards()
        => Assert.Same(Page(WidthTier.Standard), ClassicReleaseNotesTiers.Of(TierFacts.Unknown));

    // ── the overlays ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CompactOverlays_HaveFortyDipChromeAndAColumnOfRowCards()
    {
        var o = ClassicReleaseNotesTiers.OverlayFor(WidthTier.Compact, short_: false);
        Assert.Equal(40f, o.Target);
        Assert.Equal(40f, o.ChromeCircle);                 // the viewer's circles (36 elsewhere)
        Assert.Equal(40f, o.PagerRowH);                    // each pip carries a 40 hit box
        Assert.True(o.CardsColumn);
        Assert.True(o.FooterStacked);
        Assert.True(o.ScrollBody);
        Assert.Equal(16f, o.PlatePadX);
    }

    [Theory]
    [InlineData(WidthTier.Small)]
    [InlineData(WidthTier.Standard)]
    [InlineData(WidthTier.Huge)]
    public void OtherOverlays_KeepThePlateAsItWas(WidthTier tier)
    {
        var o = ClassicReleaseNotesTiers.OverlayFor(tier, short_: false);
        Assert.Equal(0f, o.Target);
        Assert.Equal(ClassicReleaseNotesRules.Viewer.ChromeCircle, o.ChromeCircle);
        Assert.Equal(ClassicReleaseNotesRules.PagerRowHeight, o.PagerRowH);
        Assert.False(o.CardsColumn);
        Assert.False(o.FooterStacked);
        Assert.False(o.ScrollBody);
        Assert.Equal(ClassicReleaseNotesRules.Card.PlatePadX, o.PlatePadX);
    }

    [Theory]
    [InlineData(WidthTier.Small)]
    [InlineData(WidthTier.Standard)]
    [InlineData(WidthTier.Huge)]
    [InlineData(WidthTier.Compact)]
    public void AShortWindowScrollsThePlateBody(WidthTier tier)
        => Assert.True(ClassicReleaseNotesTiers.OverlayFor(tier, short_: true).ScrollBody);

    [Theory]
    [InlineData(1180f, 720f)]     // the tuned plate
    [InlineData(752f, 720f)]      // 720 + 2 x 16: the last width it fits whole
    [InlineData(751f, 719f)]
    [InlineData(400f, 368f)]      // Compact: the window less 16 a side
    [InlineData(300f, 268f)]
    public void ThePlateNeverRunsPastTheWindow(float window, float expected)
        => Assert.Equal(expected, ClassicReleaseNotesTiers.OverlayFor(WidthTier.Standard, false).PlateWidth(window));

    [Theory]
    [InlineData(900f, 620f)]
    [InlineData(652f, 620f)]
    [InlineData(330f, 298f)]
    public void ThePlateKeepsItsTallestAndNeverRunsPastTheWindowHeight(float window, float expected)
        => Assert.Equal(expected, ClassicReleaseNotesTiers.OverlayFor(WidthTier.Standard, false).PlateMaxHeight(window));
}
