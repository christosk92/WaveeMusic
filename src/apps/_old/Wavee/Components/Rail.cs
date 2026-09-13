using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// A reusable horizontal rail: a titled strip of cards you page through with end CHEVRONS, the strip eased into place
// (UseAnimatedValue) and the overflowing edges dissolved with the engine's EdgeFade. Used by Home (and any shelf).
sealed class Rail : Component
{
    readonly string _title;
    readonly Element[] _cards;
    readonly float _cardW, _gap, _height;

    public Rail(string title, Element[] cards, float cardWidth, float gap, float height)
    {
        _title = title; _cards = cards; _cardW = cardWidth; _gap = gap; _height = height;
    }

    public override Element Render()
    {
        var (page, setPage) = UseState(0);

        float stride = _cardW + _gap;
        float contentW = _cards.Length * stride;
        float pageStride = 3 * stride;                       // page by ~3 cards
        int maxPage = Math.Max(0, (int)Math.Ceiling(_cards.Length / 3.0) - 1);
        if (page > maxPage) page = maxPage;

        float target = Math.Min(page * pageStride, Math.Max(0f, contentW - pageStride));
        float x = UseAnimatedValue(target, 320f, Easing.SmoothOut);   // smooth glide between pages

        bool canPrev = page > 0, canNext = page < maxPage;
        EdgeMask mask = (canPrev, canNext) switch
        {
            (true, true) => EdgeMask.Horizontal,
            (true, false) => EdgeMask.Left,
            (false, true) => EdgeMask.Right,
            _ => EdgeMask.None,
        };

        var strip = new BoxEl { Direction = 0, Gap = _gap, OffsetX = -x, Children = _cards };

        var viewport = new BoxEl
        {
            Grow = 1, Height = _height, ClipToBounds = true,
            EdgeFade = mask == EdgeMask.None ? null : new EdgeFadeSpec(mask, 36f),
            Children = [strip],
        };

        var header = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S,
            // THE HOVER SCOPE for the pager reveal, and it is deliberately the HEADER ROW, not the whole shelf.
            //
            // The engine reveals a descendant on its container's hover (AnimScheduler.SetHoverDescendants), but only
            // for REVEAL affordances — HoverOpacity / Hover-PressScale — and it recurses THROUGH non-interactive
            // wrappers. Putting the scope on the rail root (or on the viewport, or on the card strip) would therefore
            // hand every MediaCard in the strip its container's hover, and every one of them carries a
            // WaveeMotion HoverScale: hovering ONE card would pop ALL of them. There is no wrapper that fixes it
            // either — a hover boundary is by definition an interactive node, so it acquires HoverWithin itself and
            // cascades from there. The header row contains exactly the title, a spacer and these two buttons, and none
            // of the first two is a reveal, so scoping here reveals the chevrons and nothing else. (This is the same
            // "scope the hover, don't hoist it" shape as TabStrip's hovered-index signal.)
            //
            // The no-op handlers are what make this node interactive so the dispatcher publishes HoverWithin on it.
            OnHoverMove = static _ => { },
            OnPointerExit = static () => { },
            Children =
            [
                WaveeType.RailHeader(_title),
                new BoxEl { Grow = 1 },
                Chevron(Icons.ChevronLeft, canPrev, () => setPage(page - 1)),
                Chevron(Icons.ChevronRight, canNext, () => setPage(page + 1)),
            ],
        };

        return new BoxEl { Direction = 1, Gap = Spacing.M, Children = [header, viewport] };
    }

    // A QUIET pager button: borderless, unpainted at rest, revealed on the header's hover, with the subtle fill on its
    // own hover — WaveeCta's icon-button table row 1 (32 × 32, Radii.Control, 16 glyph).
    //
    // It was a filled 32-DIP CIRCLE (Tok.FillControlDefault) painted permanently at each end of every shelf header, a
    // pair of grey pucks competing with the shelf's own title and cards for attention on a page that stacks many
    // shelves. Circles are reserved for FABs ON MEDIA (the geometry table's row 3); a pager is chrome on a flat page,
    // so it takes the square rung, and chrome that is only useful while the pointer is on the shelf shows up then.
    // A DISABLED end goes to 0 (nothing to page to) rather than dimming a visible puck to 35%.
    //
    // The rest opacity is a quiet 0.7, NOT 0. A fully hidden control that is still Focusable is a keyboard trap you
    // cannot see, and the engine has no focus-driven reveal channel to pair with the hover one — so the affordance
    // stays present and merely recessive, and the HEADER hover is what brings it to full strength.
    static Element Chevron(string glyph, bool enabled, Action onClick) => new BoxEl
    {
        Width = WaveeCta.IconButtonSize, Height = WaveeCta.IconButtonSize,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = CornerRadius4.All(Radii.Control),
        Fill = Tok.FillSubtleTransparent, HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
        Opacity = enabled ? RestOpacity : 0f, HoverOpacity = enabled ? 1f : 0f,
        HoverDurationMs = WaveeMotion.Fast, HoverEasing = Easing.FluentDecelerate,
        HoverScale = WaveeMotion.ScaleStandard.HoverIf(enabled), PressScale = WaveeMotion.ScaleStandard.PressIf(enabled),
        Role = AutomationRole.Button, Focusable = enabled, AllowFocusOnInteraction = false,
        Cursor = enabled ? CursorId.Hand : (CursorId?)null,
        OnClick = enabled ? onClick : null,
        Children = [Icon(glyph, RailChevronGlyph, Tok.TextSecondary)],
    };

    const float RailChevronGlyph = 16f;
    const float RestOpacity = 0.7f;
}
