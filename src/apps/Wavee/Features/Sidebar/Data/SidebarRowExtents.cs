using System;
using System.Collections.Generic;
using Wavee.Core.Sidebar;

namespace Wavee;

// THE PANE'S ANALYTIC ROW EXTENT — the height a planned row will occupy, computed from the PLAN alone.
//
// WHY IT EXISTS. Every sidebar row's height is already a pure function of (row kind × section display options ×
// previous row kind): the 32/40/44/48 density ladder, the 28-DIP header band inside its 8/2 rhythm, the 16-DIP
// divider, the 24-DIP tree gutter, the 56/72/88 hero card, the 32-DIP empty hint. Nothing about it needs measuring.
// The pane nevertheless fed the virtualizing host ONE estimate (44) for all thirteen kinds, so every unrealized row
// claimed 44 DIP and the published content extent was fiction until the row scrolled into view and measured itself.
// Expanding a folder is what made that visible: the inserted band's rows all claimed 44, the extent jumped by
// (real − 44) × n as they measured, the scroll anchor re-pinned against the stale offset, and rows above AND below the
// folder shuffled for a frame or two. Seeding the extent table from THIS function instead means the geometry is right
// before anything is realized.
//
// IT IS A SEED, NOT A SUBSTITUTE FOR MEASUREMENT. Two kinds cannot be predicted exactly — a GridStrip (its cells wrap
// artwork + one or two text lines at font metrics the layer below owns) and a header carrying the wrapping inline chip
// strip. Those report their best analytic guess and the measured seam corrects them on realize, exactly as every row
// does today. Everything else is an identity with what `SidebarPaneSlot` builds.
//
// ENGINE-FREE (System + Wavee.Core.Sidebar + SidebarRowGeometry) so `Wavee.Tests` compiles it and can pin the ladder
// against the renderer's own constants — the same reason `SidebarRowGeometry` lives here.
static class SidebarRowExtents
{
    /// <summary>R3.1.3 SECTION RHYTHM — the air a header band carries ABOVE it: 8 DIP, suppressed for the pane's first
    /// row (nothing to separate from) and directly after a <c>Divider</c> or a bare <c>HeaderLabel</c>, both of which
    /// already supply the gap. Mirrors <c>SidebarPaneSlot.Banded</c> exactly; it is the one term of the ladder that
    /// depends on the PREVIOUS row.</summary>
    public static float BandTop(IReadOnlyList<SidebarRow> rows, int index)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (index <= 0 || index >= rows.Count) return 0f;
        var prev = rows[index - 1].Kind;
        return prev is SidebarRowKind.Divider or SidebarRowKind.HeaderLabel ? 0f : SidebarRowGeometry.SectionGap;
    }

    /// <summary>The analytic extent of plan row <paramref name="index"/>. <paramref name="section"/> is the row's
    /// section (null ⇒ the slot renders nothing, so the row is 0 tall); <paramref name="editable"/> is the pane's
    /// <c>!Config.ReadOnly</c>, which decides whether an <c>EntityList</c> header carries the inline chip strip.
    /// Returns <see cref="float.NaN"/> for the one kind whose height is genuinely not analytic (a <c>GridStrip</c>),
    /// which the layout reads as "use the estimate and correct on measure".</summary>
    public static float HeightOf(IReadOnlyList<SidebarRow> rows, int index, SidebarSectionSpec? section, bool editable)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if ((uint)index >= (uint)rows.Count) return 0f;
        var row = rows[index];
        if (section is null) return 0f;                     // SidebarPaneSlot renders Blank (height 0)

        switch (row.Kind)
        {
            case SidebarRowKind.SectionHeader:
                return BandTop(rows, index) + SidebarRowGeometry.HeaderHeight
                     + (CarriesChipStrip(section, editable)
                            ? SidebarRowGeometry.ChipStripGap + SidebarRowGeometry.ChipStripHeight : 0f)
                     + SidebarRowGeometry.HeaderBodyGap;

            case SidebarRowKind.HeaderLabel:
                return BandTop(rows, index) + SidebarRowGeometry.HeaderHeight + SidebarRowGeometry.HeaderBodyGap;

            case SidebarRowKind.Divider:
                return SidebarRowGeometry.DividerHeight;

            // Every item-shaped kind is the section's ONE uniform row height (iron rule 4: one height per SECTION).
            case SidebarRowKind.IconRow:
            case SidebarRowKind.EntityRow:
            case SidebarRowKind.Placeholder:
            case SidebarRowKind.FolderHeader:
            case SidebarRowKind.Skeleton:
                return SidebarRowGeometry.HeightFor(section.Opts);

            case SidebarRowKind.Empty:
                return EmptyHeight(section);

            case SidebarRowKind.TreeEnd:
                return SidebarRowGeometry.TreeEndHeight;

            case SidebarRowKind.EntityCard:
                return SidebarRowGeometry.CardHeightFor(section.Opts.Density);

            case SidebarRowKind.PromptRow:
                // A Concerts prompt never carries a reason line; a contribution prompt does whenever the host could
                // name one. The reason string is a RENDER-time resolution, so the ladder takes the taller shape only
                // for the kind that can have one — the shorter guess would leave that band 8 DIP short every time.
                return SidebarRowGeometry.PromptHeight(section.Kind != SidebarSectionKind.Concerts);

            case SidebarRowKind.SectionCard:
                return SidebarRowGeometry.ClassicHeight;    // the ONE edit-card height (SidebarPaneMetrics.EditCardHeight)

            case SidebarRowKind.GridStrip:
            default:
                return float.NaN;                           // not analytic — estimate, then correct on measure
        }
    }

    /// <summary>An <c>EntityList</c> header carries the inline filter chips only when the pane is editable, the section
    /// asked for them, and the section is open — <c>SidebarPaneSlot.Header</c>'s own four-term guard.</summary>
    static bool CarriesChipStrip(SidebarSectionSpec section, bool editable)
        => editable && section.Kind == SidebarSectionKind.EntityList
           && section.Opts.InlineControls && !section.Collapsed;

    /// <summary>A section that resolved to zero rows: Pinned's empty state IS its (unconditional) drop zone, a
    /// <c>HideBody</c> section draws nothing at all, an <c>ActionCard</c> borrows the section's row height, and the
    /// default is the quiet 32-DIP hint.</summary>
    static float EmptyHeight(SidebarSectionSpec section)
    {
        if (section.Kind == SidebarSectionKind.Pinned) return SidebarRowGeometry.PinDropZoneRestHeight;
        var behavior = SidebarSectionKinds.EmptyBehaviorFor(section.Kind, section.Opts.EmptyBehavior);
        return behavior switch
        {
            SidebarEmptyBehavior.HideBody => 0f,
            SidebarEmptyBehavior.ActionCard => SidebarRowGeometry.HeightFor(section.Opts),
            _ => SidebarRowGeometry.EmptyHintHeight,
        };
    }
}
