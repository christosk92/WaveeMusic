using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Core.Sidebar;

namespace Wavee;

// R3.1.2 — ONE INSET SYSTEM AND ONE HEIGHT LADDER FOR THE WHOLE PANE.
//
// WHAT WAS WRONG. The landed Curated pane had FOUR different left insets and no pane padding at all: rows sat at 6 while
// Classic's sat at 8+6=14, the explicit divider carried its own 16, cards/prompts carried 8, the empty caption carried 12,
// and the grid strip's cell math ASSUMED a 16-DIP pane inset that nothing actually applied. On top of that, Curated owned
// a SECOND height ladder (CuratedMetrics 40/48/56 + art 28/32/40) parallel to the shared SidebarRowMetrics one — so the
// same Cozy row was 48 in one mode and 44 in another.
//
// WHAT IT IS NOW. The pane applies Classic's padding ONCE (PanePad) around the virtualized list, and every special band
// sits at the ROW inset (the row's own 6/8 padding) — so a divider, a card, a prompt, an empty hint and an entity row all
// share one left edge, and the grid math DERIVES its available width from the real inset instead of guessing it. Heights
// and art sizes come from SidebarRowMetrics (the ONE ladder): Cozy+subtitle = 44 = Classic's row, which is what makes
// Classic-as-a-document reproduce Classic's geometry rather than approximate it.
static class SidebarPaneMetrics
{
    /// <summary>The pane's padding — Classic's landed <c>(8,8,8,12)</c>, applied ONCE around the virtualized list. Rows
    /// therefore sit at 8, and their content at <see cref="ContentLane"/> = 14, exactly as the landed Classic body did.
    /// THE single inset owner: no row, band, card or strip may add a second horizontal inset.</summary>
    public static readonly Edges4 PanePad = new(SidebarRowGeometry.PaneEdge, 8f, SidebarRowGeometry.PaneEdge, 12f);

    /// <summary>Horizontal DIPs the pane padding consumes (PanePad.Left + PanePad.Right). The grid strip's cell math reads
    /// THIS rather than assuming <c>Spacing.L</c> — that assumption is what made the grid overhang the pane.</summary>
    public const float PaneInsetH = SidebarRowGeometry.PaneEdge * 2f;

    /// <inheritdoc cref="SidebarRowGeometry.ContentLane"/>
    public const float ContentLane = SidebarRowGeometry.ContentLane;

    /// <inheritdoc cref="SidebarRowGeometry.ContentLaneEnd"/>
    public const float ContentLaneEnd = SidebarRowGeometry.ContentLaneEnd;

    /// <summary>A band that must line up with the rows around it uses the ROW's own padding. One constant pair, so
    /// "the row inset" is a fact and not a per-call-site guess. Valid only INSIDE the virtualized list, which already
    /// carries <see cref="PanePad"/>; a band mounted above the list takes <see cref="BandInset"/> instead.</summary>
    public static readonly Edges4 RowInset =
        new(SidebarRowGeometry.RowInsetLeft, 0f, SidebarRowGeometry.RowInsetRight, 0f);

    /// <summary>The horizontal inset for a FIXED CHROME BAND mounted ABOVE the virtualized list (Library V3's header /
    /// toolbar / chip rail / rule / breadcrumb, and any future mode head). Such a band is a sibling of the padded list,
    /// not a child of it, so <see cref="PanePad"/> never reaches it — it must land on <see cref="ContentLane"/> by
    /// itself. Padding to a bare 8 here (which every V3 band used to do) is exactly the 6-DIP ragged left edge.</summary>
    public static readonly Edges4 BandInset = new(ContentLane, 0f, ContentLaneEnd, 0f);

    /// <summary>R3.1.3 — the vertical air above a section header that is not the pane's first row. Section rhythm used to
    /// be ZERO (the planner emits contiguous rows), so five sections read as one undifferentiated column.</summary>
    public const float SectionGap = SidebarRowGeometry.SectionGap;

    /// <summary>R3.1.3 — the gap between a header and its first body row, matching <c>SidebarSectionHeader.Section</c>'s
    /// own internal gap so a virtualized header and a hand-built one are the same shape.</summary>
    public const float HeaderBodyGap = SidebarRowGeometry.HeaderBodyGap;

    /// <summary>PHASE 2 / Decision B — THE ONE EDIT-CARD HEIGHT. Every section's card in the customize canvas is exactly
    /// this tall, whatever its kind, its density or how many rows it holds: the card band is a <c>Reorderable</c> whose
    /// slot pitch is ONE extent (<c>SidebarPaneBand.Extent</c>) and whose displacement hints are computed from it, and
    /// the virtualizing host's <c>VariableList</c> seeds from it too. A per-kind card height would break both, which is
    /// the same "one height per SECTION" rule <see cref="RowHeight"/> exists to keep — stated here as its own constant
    /// because the card is not a row of any section, it IS the section.
    /// <para>44 = <c>SidebarRowMetrics.ClassicHeight</c>: the card sits in the pane's own rhythm rather than inventing a
    /// third scale beside the 44-DIP rows it replaces.</para></summary>
    public const float EditCardHeight = SidebarRowGeometry.ClassicHeight;

    /// <summary>R3.1.6 — the quiet empty hint's band height (was 40 with a 12f caption; a section that resolved to nothing
    /// must not occupy a full row's worth of pane).</summary>
    public const float EmptyHintHeight = SidebarRowGeometry.EmptyHintHeight;

    /// <summary>Artwork grids stay media-card sized at the 460-DIP pane maximum instead of stretching into billboards.</summary>
    public const float GridCellMax = 160f;

    /// <summary>A section's UNIFORM row height, from the ONE shared ladder. It deliberately depends on the section's
    /// SUBTITLE INTENT and never on whether a given row happens to carry one: a Reorderable's slot pitch and the
    /// virtualizing host's extent both assume one height per section, and a mixed 40/44 list silently breaks both.
    /// <para>Cozy+subtitles = 44 = Classic's row · Cozy without = 40 · Compact = 32 · Comfortable = 44/48.</para></summary>
    public static float RowHeight(SidebarSectionSpec section)
        => SidebarRowMetrics.HeightFor(section.Opts.Density, section.Opts.Subtitles);

    /// <summary>The section's art edge, from the same ladder (20 / 32 / 40).</summary>
    public static float ArtSize(SidebarSectionSpec section)
        => SidebarRowMetrics.ArtFor(section.Opts.Density);

    /// <summary>The EntityEmbed hero card's height ladder (§C1.8.2): Compact 56 / Cozy 72 / Comfortable 88.</summary>
    public static float CardHeight(SidebarSectionSpec section) => SidebarRowGeometry.CardHeightFor(section.Opts.Density);

    /// <summary>The card's cover edge — the card height less its 8-DIP padding on both sides.</summary>
    public static float CardCover(SidebarSectionSpec section) => CardHeight(section) - 16f;
}
