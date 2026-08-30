using System;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// F.1.4 — the shared 28-DIP sidebar section header + its always-mounted reveal wrapper.
//
// Every visual here is the landed WaveeSidebar `Section(...)` chrome, moved verbatim:
//   • 28-DIP header, HoverFill = Tok.FillSubtleSecondary, Corners 4, gap 4, and SidebarPaneMetrics.RowInset — the
//     landed chrome padded to a literal (8,0,8,0), which sat 2 DIP right of the rows it labels (see Header);
//   • stock NavigationViewItemHeader typography — Caption-scale 12 at BodyStrong weight 600 in the SECONDARY text
//     colour (11/Tertiary reads as disabled micro-copy rather than as a group label);
//   • chevron at 10 DIP in Tok.TextTertiary, AFTER the optional trailing action slot. R3.1.7a replaced the
//     ChevronUp/ChevronDown GLYPH SWAP with ONE glyph whose Rotation animates (SidebarChevron) — pass the animated one
//     through `chevron:` and the header renders it instead of the static swap. The swap remains the fallback for a caller
//     that has no live open-state delegate to give (a bare, non-reactive header);
//   • `rule` draws the stock NavigationViewItemSeparator above the header — every group EXCEPT the first is preceded by a
//     1px divider + an 8-DIP lead-in, so the pane reads as grouped bands instead of one undifferentiated column;
//   • an ALWAYS-MOUNTED reveal wrapper (the Expander PartClip idiom): the body's layout height eases 0↔auto so sections
//     below reflow. The body stays MOUNTED while collapsed (clip height 0) — that is what keeps a selected row's node
//     measurable for the selection pill (it just goes hidden when its section closes).
//
// Expanded pane only: the 56-DIP rail never calls this (SidebarRailItem.Divider stands in for the rule).

static class SidebarSectionHeader
{
    /// <summary>The header band height.</summary>
    public const float Height = SidebarRowGeometry.HeaderHeight;

    /// <summary>Section open/close reveal. Revision 2's motion table assigns section expansion
    /// <c>MotionTok.ContentResize</c>, so the token — not a hand-typed duration — is the single source of the dynamics
    /// (spring, response 0.40 / damping 0.90: smooth, no overshoot). <c>SizeMode.Reflow</c> + <c>SizeAnchor.Trailing</c>
    /// keep the Expander slide-out-from-under-the-header shape the landed sidebar has.</summary>
    public static readonly LayoutTransition Reveal = new(
        TransitionChannels.Size, MotionTok.ContentResize.ToDynamics(),
        Size: SizeMode.Reflow, Anchor: SizeAnchor.Trailing);

    /// <summary>The 1px group separator + its 8-DIP lead-in (the `rule: true` chrome).</summary>
    public static Element Rule() => Divider() with { Margin = new Edges4(0f, 8f, 0f, 0f) };

    /// <summary>
    /// An EXPLICIT document Divider section. Unlike <see cref="Rule"/>, this is never injected as ordinary group chrome:
    /// the layout document must contain a Divider section.
    ///
    /// <para>R3.1.2 — ONE INSET SYSTEM. It used to carry its own <c>Spacing.L</c> (16) horizontal padding, which — added
    /// to whatever the pane contributed — made the rule the FOURTH distinct left inset in the pane. It now spans exactly
    /// the ROW content box (the row's own 6/8 padding), so a divider lines up with the rows it separates and the pane has
    /// a single inset owner (<c>SidebarPaneMetrics.PanePad</c>). The 16-DIP band height centres the hairline 8 DIP below
    /// the previous row — the same lead-in <see cref="Rule"/> draws.</para>
    /// </summary>
    public static Element ExplicitDivider() => new BoxEl
    {
        Direction = 0,
        Height = SidebarRowGeometry.DividerHeight,
        Shrink = 0f,
        AlignItems = FlexAlign.Center,
        Padding = SidebarPaneMetrics.RowInset,
        Children =
        [
            new BoxEl { Grow = 1f, Height = 1f, Fill = Tok.StrokeDividerDefault },
        ],
    };

    /// <summary>
    /// One collapsible group: optional rule · header · always-mounted reveal wrapper around <paramref name="body"/>.
    /// </summary>
    /// <param name="open">The live open state (the caller has already subscribed to its signal).</param>
    /// <param name="onToggle">Invoked with the NEW state when the header is activated. Null ⇒ a non-collapsible group
    /// (no chevron, no click) — use <see cref="Label"/> for a bare heading instead.</param>
    /// <param name="action">A trailing affordance placed BEFORE the chevron (the Playlists "+" button, the layout menu
    /// button). Click dispatch targets the nearest clickable self-or-ancestor, so a clickable action consumes its own
    /// clicks without toggling the section.</param>
    /// <param name="onHover">Header hover transitions — the carrier for a hover-revealed <paramref name="action"/>
    /// (§3.1.5). Called true on pointer-move-within, false on pointer-exit.</param>
    public static Element Section(string title, bool open, Action<bool>? onToggle, Element body,
                                  Element? action = null, bool rule = false, Action<bool>? onHover = null,
                                  Element? chevron = null)
    {
        Element header = Header(title, open, onToggle, action, onHover, chevron);
        Element reveal = RevealWrapper(open, body);
        Element[] kids = rule ? new[] { Rule(), header, reveal } : new[] { header, reveal };
        return new BoxEl { Direction = 1, Gap = 2f, Children = kids };
    }

    /// <summary>Just the header band (for a surface that owns its own body/reveal, e.g. a virtualized plan row).</summary>
    /// <param name="chevron">The animated disclosure mark (<see cref="SidebarChevron.Section"/>). Null ⇒ the legacy
    /// static glyph swap, kept only for a caller with no live open-state delegate.</param>
    public static Element Header(string title, bool open, Action<bool>? onToggle,
                                 Element? action = null, Action<bool>? onHover = null, Element? chevron = null)
    {
        bool collapsible = onToggle is not null;
        int count = 2 + (action is null ? 0 : 1) + (collapsible ? 1 : 0);
        var kids = new Element[count];
        int k = 0;
        kids[k++] = new TextEl(title) { Size = 12f, Weight = 600, Color = Tok.TextSecondary };
        kids[k++] = new BoxEl { Grow = 1f };
        if (action is { } a) kids[k++] = a;
        if (collapsible) kids[k++] = chevron ?? Icon(open ? Icons.ChevronUp : Icons.ChevronDown, 10f, Tok.TextTertiary);

        // Explicit locals rather than inline conditionals: `_ => …` has no natural type, so a ternary against null would
        // rely on target typing inside an object initializer.
        Action<Point2>? onMove = null;
        Action? onExit = null;
        if (onHover is { } hover)
        {
            onMove = _ => hover(true);
            onExit = () => hover(false);
        }

        return new BoxEl
        {
            Direction = 0, Height = SidebarSectionHeader.Height, AlignItems = FlexAlign.Center, Gap = 4f,
            // THE ROW INSET, not the landed literal 8. Inside the plan list this header is a sibling of the rows, so a
            // bare 8 put its title at PanePad+8 = 16 while every row's leading content sat at PanePad+6 = 14 — a 2-DIP
            // drift inherited verbatim from the pre-unification WaveeSidebar body. One inset owner, one lane.
            Padding = SidebarPaneMetrics.RowInset, Corners = CornerRadius4.All(4f),
            HoverFill = collapsible ? Tok.FillSubtleSecondary : ColorF.Transparent,
            Role = collapsible ? AutomationRole.Button : AutomationRole.None,
            OnClick = collapsible ? () => onToggle!(!open) : null,
            OnPointerMoveWithin = onMove,
            OnPointerExit = onExit,
            Children = kids,
        };
    }

    /// <summary>A non-collapsible heading row (<c>SidebarSectionKind.Header</c>): the same typography, no chevron, no
    /// hover plate, not a click target.</summary>
    public static Element Label(string title) => Header(title, open: true, onToggle: null);

    /// <summary>The always-mounted clip wrapper whose height eases 0↔auto. Keep the body MOUNTED while collapsed.</summary>
    public static Element RevealWrapper(bool open, Element body) => new BoxEl
    {
        Direction = 1, ClipToBounds = true,
        Height = open ? float.NaN : 0f,
        Animate = Reveal,
        Children = [body],
    };
}
