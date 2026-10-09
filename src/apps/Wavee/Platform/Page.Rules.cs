// ── Platform/Page.Rules.cs ─────────────────────────────────────────────────────────────────────────────────────────
// The PURE geometry of a page inside the content card: the responsive gutter, the page head's vertical rhythm, the
// hoisted strip, the views bar's measured box, the bottom reserve and the pane inset. One place for the numbers every
// page used to restate privately, so two pages cannot sit 4 DIP apart and a head's height is a function of its ROUTE KIND
// alone, never of the data that arrives later.
//
// Role: CORE
// Engine-free apart from Spacing and Design.Dock (constants only): a test reads every rule without a scene.
//
// WHY A HEAD'S HEIGHT IS STATIC. A page head that grows when its meta line, its views row or its action cluster arrives
// pushes the body down a frame after first paint. Every height here is reserved from the first frame; later work fades or
// swaps in place inside a box that was already that size.

using FluentGpu.Dsl;

namespace Wavee;

/// <summary>The frame numbers of a page. Pure; every member is a constant or a function of its arguments.
/// <para>The gutter's hysteresis is <see cref="ArtistHeroLayout.TierFor"/>'s own rule (<c>Entities/Artist.UI.cs</c>):
/// the page gutter and the artist hero tier step at the same widths in the same direction, so the artist page and a
/// page head cannot disagree about the gutter in steady state.</para></summary>
public static class PageGeometry
{
    // ══ 1. THE GUTTER ═════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The desktop page gutter (<c>Spacing.PageWide</c>, 36).</summary>
    public const float GutterWide = Spacing.PageWide;

    /// <summary>The medium gutter (<c>Spacing.XXXL</c>, 32).</summary>
    public const float GutterMedium = Spacing.XXXL;

    /// <summary>The narrow gutter (<c>Spacing.PageNarrow</c>, 16).</summary>
    public const float GutterNarrow = Spacing.PageNarrow;

    /// <summary>The card width from which the wide gutter applies (the artist hero's Wide tier, 880).</summary>
    public const float GutterWideMinWidth = ArtistHeroLayout.WideWidth;

    /// <summary>The card width from which the medium gutter applies (the artist hero's Medium tier, 600).</summary>
    public const float GutterMediumMinWidth = ArtistHeroLayout.MediumWidth;

    /// <summary>The recovery band: a step UP needs the threshold plus this; a step DOWN happens at the bare threshold
    /// (the artist hero's <see cref="ArtistHeroLayout.TierHysteresis"/>, 24).</summary>
    public const float GutterHysteresis = ArtistHeroLayout.TierHysteresis;

    /// <summary>The bare, memoryless gutter for a card width: 36 at &gt;= 880, 32 at &gt;= 600, else 16. Seeds the first
    /// frame; a resize uses <see cref="GutterFor(float, float)"/>.</summary>
    public static float GutterFor(float width)
        => width >= GutterWideMinWidth ? GutterWide
        : width >= GutterMediumMinWidth ? GutterMedium
        : GutterNarrow;

    /// <summary>The hysteretic gutter, mirroring <see cref="ArtistHeroLayout.TierFor"/> (Artist.UI.cs) tier for tier:
    /// <list type="bullet">
    /// <item>a previous 36 (Wide) holds while width &gt;= 856;</item>
    /// <item>a previous 32 (Medium) holds while 576 &lt;= width &lt; 904;</item>
    /// <item>a previous 16 (Compact and Narrow, which both map to 16; the Compact hold band 336..624 is merged into it)
    /// holds while width &lt; 624;</item>
    /// <item>otherwise a step UP from a lower gutter needs the threshold + 24 (904, 624) and a step DOWN happens at the
    /// bare threshold (the tier function's own fall-through).</item>
    /// </list>
    /// A non-finite width keeps <paramref name="previous"/>. Resizing near 600 or 880 therefore cannot flicker.</summary>
    public static float GutterFor(float width, float previous)
    {
        if (!float.IsFinite(width)) return previous;
        if (previous >= GutterWide && width >= GutterWideMinWidth - GutterHysteresis) return GutterWide;
        if (previous == GutterMedium && width >= GutterMediumMinWidth - GutterHysteresis
            && width < GutterWideMinWidth + GutterHysteresis) return GutterMedium;
        if (previous <= GutterNarrow && width < GutterMediumMinWidth + GutterHysteresis) return GutterNarrow;

        // The fall-through: an up-step from a LOWER gutter needs the +24 recovery band, a down-step is the bare edge.
        if (width >= GutterWideMinWidth + (previous < GutterWide ? GutterHysteresis : 0f)) return GutterWide;
        if (width >= GutterMediumMinWidth + (previous < GutterMedium ? GutterHysteresis : 0f)) return GutterMedium;
        return GutterNarrow;
    }

    // ══ 2. THE PAGE HEAD'S VERTICAL RHYTHM ════════════════════════════════════════════════════════════════════════════

    /// <summary>Space above the head's first line (<c>Spacing.XXL</c>, 24).</summary>
    public const float HeadTop = Spacing.XXL;

    /// <summary>The breadcrumb row's reserved line. A BreadcrumbBar item measures 26 (a 20-DIP line + the item's 1,3
    /// padding, fluent-gpu <c>BreadcrumbBar.cs</c> :91/:112); the row reserves 32 so the 32-DIP action buttons that sit
    /// on it never grow it.</summary>
    public const float AboveLine = 32f;

    /// <summary>Gap between the breadcrumb row and the title (<c>Spacing.XS</c>, 4).</summary>
    public const float AboveToTitle = Spacing.XS;

    /// <summary>The title's one line: <see cref="Design.Type.PageTitle"/>'s 52 line height.</summary>
    public const float TitleLine = 52f;

    /// <summary>Gap between the title and the meta line (<c>Spacing.XS</c>, 4).</summary>
    public const float TitleToMeta = Spacing.XS;

    /// <summary>The meta line: <see cref="Design.Type.PageMeta"/>'s 16 line height. ALWAYS reserved in a non-hoisted
    /// head, so meta arriving (or being absent) never moves the body.</summary>
    public const float MetaLine = 16f;

    /// <summary>Head to the views bar's plate (<c>Spacing.M</c>, 12). Under the bar's own padding it is
    /// <see cref="HeadToViewsGap"/>.</summary>
    public const float HeadToViews = Spacing.M;

    /// <summary>Views bar's plate to the body (<c>Spacing.L</c>, 16). Under the bar's own padding it is
    /// <see cref="ViewsToBodyGap"/>.</summary>
    public const float ViewsToBody = Spacing.L;

    /// <summary>Head (no views) to the body (<c>Spacing.XXL</c>, 24).</summary>
    public const float HeadToBody = Spacing.XXL;

    // ══ 3. THE HOISTED STRIP ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Space above a hoisted head's one row (<c>Spacing.M</c>, 12). A hoisted head is
    /// <c>HoistedTop + ViewsBarH + HoistedTop</c> = 72 on every pivot page.</summary>
    public const float HoistedTop = Spacing.M;

    // ══ 4. THE VIEWS BAR ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The SelectorBar container's unconditional vertical padding (0,4,0,4), restated from fluent-gpu
    /// <c>SelectorBar.cs</c> :275 (<c>SelectorBarPadding</c>). The engine owns it; the app absorbs it into the gaps.</summary>
    public const float ViewsBarPadY = 4f;

    /// <summary>The bar's item box (<c>ItemHeight</c>), measured: 40.</summary>
    public const float ViewsItemH = 40f;

    /// <summary>The bar's whole height: the item box + the container padding (48).</summary>
    public const float ViewsBarH = ViewsItemH + 2f * ViewsBarPadY;

    /// <summary><see cref="HeadToViews"/> less the bar's own top padding, so the PLATE sits 12 below the head (8).</summary>
    public const float HeadToViewsGap = HeadToViews - ViewsBarPadY;

    /// <summary><see cref="ViewsToBody"/> less the bar's own bottom padding, so the body starts 16 below the plate (12).</summary>
    public const float ViewsToBodyGap = ViewsToBody - ViewsBarPadY;

    /// <summary>Pulls the first item left by its 12-DIP plate padding so its WORD sits on the gutter.</summary>
    public const float ViewsLeadingInset = -12f;

    // ══ 5. THE BOTTOM RESERVE, THE PANE INSET ═════════════════════════════════════════════════════════════════════════

    /// <summary>The trailing space under a page's last row: clears the player bar (<see cref="Design.Dock.Reserve"/>) and
    /// leaves <c>Spacing.XXXL</c> of air. The default for any <c>ScrollView</c> content.
    /// <para><b>THE ONE EXCEPTION.</b> A page whose VIRTUALIZED LIST OWNS ITS OWN SCROLLER (Recents body, Profile.Lists
    /// body, Logs list, Library master nav, track tables) keeps a <c>Spacing.L</c> trailing inset instead: the player bar is
    /// a SIBLING in the shell column (Shell.UI.cs, the content/player-bar column), so a list that fills the card ends
    /// above the bar already and only needs the small air. Every exception site cites this doc.</para></summary>
    public const float BottomReserve = Design.Dock.Reserve + Spacing.XXXL;

    /// <summary>The inset of a pane's own content from its edge (<c>Spacing.L</c>, 16).</summary>
    public const float PaneInset = Spacing.L;
}

/// <summary>The five heads a page can have. A page's kind is decided by three ROUTE-STATIC facts (is it hoisted into the
/// Zune band, does it have a breadcrumb row above the title, does it have a views row) and by nothing that arrives later.</summary>
public enum PageHeadKind : byte
{
    /// <summary>Title and meta line.</summary>
    Title,
    /// <summary>Title, meta line and the views row.</summary>
    TitleViews,
    /// <summary>Breadcrumb row, title and meta line.</summary>
    CrumbTitle,
    /// <summary>Breadcrumb row, title, meta line and the views row.</summary>
    CrumbTitleViews,
    /// <summary>The one 72-DIP strip a pivot destination keeps under the Zune band.</summary>
    Hoisted,
}

/// <summary>The page head's height: a pure function of its <see cref="PageHeadKind"/>.
/// <para>Meta, actions, the views' labels and the trailing control are NOT inputs. The meta line is always reserved, the
/// views row is reserved whenever the route has views (even while its labels are still empty), and actions and trailing
/// controls live inside rows that already have the height. So a head is the same height on the first frame and on the
/// last, and the body's top edge lands at the same y on every page of the same kind.</para></summary>
public static class PageHeadRules
{
    /// <summary>Whether a route's head hoists into the Zune band: the PRESENTED nav style is Zune and the route is a pivot
    /// destination. <paramref name="presented"/> is <c>Shell.Ui.PresentedNavStyle</c>, which lags a switch by the card's
    /// tween (see <c>Shell.FrameRules.HoistSettleMs</c>), never the live <c>Sidebar.NavStyle</c>.</summary>
    public static bool Hoisted(ShellNavStyle presented, string routeName)
        => presented == ShellNavStyle.Zune && ZuneNavRules.IsPivotDestination(routeName);

    /// <summary>The head kind for three route-static facts. A hoisted head ignores the other two.</summary>
    public static PageHeadKind KindOf(bool hoisted, bool hasAbove, bool hasViews)
        => hoisted ? PageHeadKind.Hoisted
         : hasAbove ? (hasViews ? PageHeadKind.CrumbTitleViews : PageHeadKind.CrumbTitle)
         : hasViews ? PageHeadKind.TitleViews
         : PageHeadKind.Title;

    /// <summary>The head's height above the views row (or above the body, with none): the top air, the optional breadcrumb
    /// row, the title line and the always-reserved meta line. A hoisted head has only <see cref="PageGeometry.HoistedTop"/>.</summary>
    public static float Lead(PageHeadKind k) => k switch
    {
        PageHeadKind.Hoisted => PageGeometry.HoistedTop,
        PageHeadKind.CrumbTitle or PageHeadKind.CrumbTitleViews
            => PageGeometry.HeadTop + PageGeometry.AboveLine + PageGeometry.AboveToTitle + PageGeometry.TitleLine
               + PageGeometry.TitleToMeta + PageGeometry.MetaLine,
        _ => PageGeometry.HeadTop + PageGeometry.TitleLine + PageGeometry.TitleToMeta + PageGeometry.MetaLine,
    };

    /// <summary>The head's whole height. Title 120, TitleViews 164, CrumbTitle 156, CrumbTitleViews 200, Hoisted 72.</summary>
    public static float Extent(PageHeadKind k) => k switch
    {
        PageHeadKind.Hoisted => PageGeometry.HoistedTop + PageGeometry.ViewsBarH + PageGeometry.ViewsToBodyGap,
        PageHeadKind.TitleViews or PageHeadKind.CrumbTitleViews
            => Lead(k) + PageGeometry.HeadToViewsGap + PageGeometry.ViewsBarH + PageGeometry.ViewsToBodyGap,
        _ => Lead(k) + PageGeometry.HeadToBody,
    };
}
