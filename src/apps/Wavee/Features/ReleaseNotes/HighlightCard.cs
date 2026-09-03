using System;
using System.Collections.Generic;
using System.IO;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;
using Wavee.Core.ReleaseNotes;
using static FluentGpu.Dsl.Ui;
using M = Wavee.Core.ReleaseNotes.HighlightCardMetrics;

namespace Wavee;

/// <summary>One hero highlight: poster, kind pill, title, a fixed-height body slot with an edge fade, and a
/// "Read more ›" cue that opens the highlight viewer (<c>HighlightViewer.cs</c>).
///
/// <para>THE ONE OPAQUE CARD. Every other card in Wavee runs <c>Interaction.Card</c>'s translucent
/// <c>Tok.FillCardDefault</c> veil (white @ 5%, cross-fading to <c>FillCardSecondary</c> on hover) over whatever
/// sits behind it. This card cannot: the body slot's edge fade (<see cref="HighlightCardView.Fade"/>) is a gradient
/// that must end in a colour that is ACTUALLY THERE, so the dissolving text reads as "there is more" rather than as
/// a rendering fault. No gradient end-stop can equal a translucent veil composited over two different backdrops —
/// the dialog's <c>FillSolidBase</c> plate and the page's Mica-over-wallpaper — and a stop tuned to the rest state
/// would show a visible seam the moment hover cross-fades the veil underneath it. So the frame paints
/// <c>Tok.FillSolidTertiary</c> (an existing token, unused by any other card before this one) and the fade's far
/// stop is that same opaque colour at full alpha — a true pixel match, not an approximation. Flat interaction recipe
/// follows from the same constraint: <c>Interaction.Card</c> would reintroduce the translucent ramp this card
/// specifically opts out of, so the press/hover cues below are authored directly instead (§A.3 of the design doc).
/// </para>
///
/// <para>MOTION: the card renders a POSTER, never a live video. Wavee's only video surface is
/// <c>MediaPlayerElement</c>, which binds an <c>IMediaPlayer</c> (the Spotify playback session) — there is no
/// file-backed player to hand a cached <c>.mp4</c> to, so a video highlight shows its poster plus the play glyph the
/// prototype draws, and the mp4 stays a release asset for the GitHub page (played back in the viewer's on-media
/// watch link, not here). Reduced motion is therefore already honoured by construction; nothing here branches on
/// <c>Motion.ReducedMotion</c>.</para></summary>
static class HighlightCard
{
    /// <summary>The decode target for a poster. The band is FLUID (its width is whatever the card column resolved to),
    /// so the request-time box is unknown and <c>ImageEl.DecodePx</c> is what the decoder sizes against — 1200 px, the
    /// authored width, with the reconciler deriving 675 from <c>AspectRatio</c>. Without it the decode target collapses
    /// to the band's only known extent and the poster arrives as a smear.</summary>
    internal const float PosterDecodePx = 1200f;

    /// <summary>The full card (What's-new page): poster, pill, title, body slot, "Read more ›".</summary>
    public static Element Create(HighlightItem item, Action open)
        => Embed.Comp(() => new HighlightCardView(item, open, compact: false));

    /// <summary>The dialog card: the same 16:9 poster and text block, capped at <c>HighlightCardMetrics.CompactCardMaxW</c>.</summary>
    public static Element Compact(HighlightItem item, Action open)
        => Embed.Comp(() => new HighlightCardView(item, open, compact: true));

    /// <summary>The poster band. A document with no media (or one whose file never made it into the cache) still gets a
    /// band — a tinted plate carrying the kind pill — because a card that suddenly loses its top third reads as broken
    /// rather than as "no screenshot this time".
    ///
    /// <para>The band has NO fixed height: it stretches to the card's width and derives 16:9 from it
    /// (<c>HighlightCardMetrics.PosterAspect</c>), which is the shape release images are authored at. The dialog's own
    /// width cap (<c>HighlightCardMetrics.CompactCardMaxW</c>) is what bounds it there — there is no separate height
    /// cap.</para></summary>
    /// <param name="store">The store announcement: its plate and pill go accent-tinted (the card has no poster to
    /// carry, so the tint is what makes the band read as deliberate rather than as a missing screenshot).</param>
    internal static Element Media(ReleaseHighlight h, string? poster, bool store)
    {
        var layers = new List<Element>(3);
        if (poster is { Length: > 0 })
            // ImageEl carries no Grow/Shrink (it is a leaf with its own intrinsic sizing), so the poster fills the band
            // by STRETCHING across the ZStack — both extents left fluid so it takes the band's whole box.
            //
            // AspectRatio + DecodePx are what make the DECODE right, and they are not decoration: the reconciler sizes
            // the decode from Width/Height, else from DecodePx with the missing extent derived from AspectRatio. A
            // poster that named only its band height decoded at 1×<height> and arrived as a smear. 1200×675 is the
            // authored size, so the decode is 1:1 with the file and the GPU never upsamples.
            //
            // The top corners are rounded on the IMAGE as well as clipped by the card: the card's ClipToBounds
            // constrains the band's box, and rounding the poster itself is what keeps the card's own radius reading as
            // one continuous edge rather than a square photo peeking out of a rounded frame.
            layers.Add(new ImageEl
            {
                Source = poster, Fit = ImageFit.Cover,
                AspectRatio = M.PosterAspect, DecodePx = PosterDecodePx,
                Corners = new CornerRadius4(Radii.Card, Radii.Card, 0f, 0f),
                Placeholder = Tok.FillSubtleSecondary, AlignSelf = FlexAlign.Stretch,
            });

        layers.Add(new BoxEl
        {
            Grow = 1f, Direction = 0, AlignItems = FlexAlign.Start, Padding = Edges4.All(8f),
            HitTestVisible = false,
            Children = [ KindPill(h.Kind, store), Spacer(), PlayGlyph(h) ],
        });

        return new BoxEl
        {
            AspectRatio = M.PosterAspect,
            AlignSelf = FlexAlign.Stretch, MinWidth = 0f, ZStack = true, ClipToBounds = true,
            Fill = store ? Tok.AccentSubtle : Tok.FillSubtleSecondary,
            Children = layers.ToArray(),
        };
    }

    /// <summary>Open the Wavee listing in the Store app. <c>StoreId</c> is stamped for EVERY channel
    /// (<c>Wavee.csproj</c> defaults <c>WaveeStoreId</c>, so feed builds carry it too); the literal only covers an
    /// unstamped build (a headless test host, a hand-built MSIX), where a dead button would read as broken.</summary>
    internal static void OpenStoreListing()
    {
        string id = AppVersion.Info.StoreId is { Length: > 0 } stamped ? stamped : "9NJPVWTQPT9H";
        ShellOpen.OpenUrl(StoreLinks.ProductPage(id));
    }

    /// <summary>Resolve a highlight's poster to a real on-disk path, or null.
    /// <para>Called from the LOADER, never from <c>Render</c>. It touches the file system (<c>MediaPath</c> plus a
    /// <c>File.Exists</c>), and a render pass does that on the UI thread once per card, on every hover, scroll,
    /// re-theme and reconcile — for an answer that cannot change while the page is open. The result rides on
    /// <see cref="HighlightItem.Poster"/> instead.</para></summary>
    public static string? ResolvePoster(ReleaseHighlight? h, ReleaseNotesStore? store, ReleaseNotesDocument? doc)
    {
        if (store is null || doc is null || h?.Media is not { } m) return null;
        // A video highlight names its own still in Poster; an image highlight IS its still.
        string? src = string.Equals(m.Kind, "video", StringComparison.OrdinalIgnoreCase) ? m.Poster : m.Src;
        if (string.IsNullOrEmpty(src)) return null;
        try
        {
            string path = store.MediaPath(doc, src);
            return File.Exists(path) ? path : null;
        }
        catch { return null; }   // a malformed src is a missing poster, never a crash on a notes page
    }

    internal static Element KindPill(string? kind, bool store)
    {
        string label = Loc.Get(store ? Strings.WhatsNew.Kind.Store : kind switch
        {
            "rebuilt" => Strings.WhatsNew.Kind.Rebuilt,
            "improved" => Strings.WhatsNew.Kind.Improved,
            _ => Strings.WhatsNew.Kind.New,
        });
        return new BoxEl
        {
            Shrink = 0f, Padding = new Edges4(8f, 2f, 8f, 2f), Corners = CornerRadius4.All(Radii.Pill),
            Fill = store ? Tok.AccentDefault : Tok.FillSolidBase,
            Children = [ new TextEl(label)
                { Size = 11f, Weight = 600, Color = store ? Tok.TextOnAccentPrimary : Tok.TextPrimary } ],
        };
    }

    /// <summary>Whether a highlight's media is a video (poster + play glyph) rather than a still image.</summary>
    internal static bool IsVideo(ReleaseHighlight h)
        => h.Media is { } m && string.Equals(m.Kind, "video", StringComparison.OrdinalIgnoreCase);

    internal static Element PlayGlyph(ReleaseHighlight h)
    {
        if (!IsVideo(h)) return new BoxEl { Width = 0f, HitTestVisible = false };
        return new BoxEl
        {
            Width = 32f, Height = 32f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(16f), Fill = Tok.MediaScrim,
            Children = [ Icon(Icons.Play, 14f, Tok.OnMediaPrimary) ],
        };
    }
}

/// <summary>The card's stateful half: owns the per-card <c>overflows</c> signal the body slot's edge fade reads.
/// Props (<paramref name="item"/> hint below) freeze at mount — <c>HighlightItem</c> is immutable and <c>open</c>
/// captures its index, so nothing here needs to react to a changed prop.</summary>
sealed class HighlightCardView : Component
{
    readonly HighlightItem _item;
    readonly Action _open;
    readonly bool _compact;

    public HighlightCardView(HighlightItem item, Action open, bool compact)
    {
        _item = item;
        _open = open;
        _compact = compact;
    }

    readonly Signal<bool> _overflows = new(false);

    // Theme-live: a get-only property re-reads the token each render. Both stops share the card's RGB, so the ramp is
    // a pure alpha ramp with no hue drift mid-fade.
    static GradientSpec Fade => GradientDown(
        new GradientStop(0f, Tok.FillSolidTertiary with { A = 0f }),
        new GradientStop(1f, Tok.FillSolidTertiary));

    public override Element Render()
    {
        var h = _item.Highlight;
        bool store = HighlightVisibility.IsStore(h);
        bool overflows = _overflows.Value;   // subscribe: the fade eases in/out when OnBoundsChanged flips the answer

        var frame = new BoxEl
        {
            Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
            MaxWidth = _compact ? M.CompactCardMaxW : M.CardMaxW,
            Corners = CornerRadius4.All(Radii.Card), ClipToBounds = true,
            BorderWidth = 1f,
            BorderColor = store ? Tok.AccentDefault : Tok.StrokeCardDefault,
            HoverBorderColor = store ? Tok.AccentDefault : Tok.StrokeSurfaceDefault,
            BrushTransitionMs = WaveeMotion.Faster,
            Fill = Tok.FillSolidTertiary,          // opaque — see the file comment on HighlightCard
        };

        // Regular card: frame and hit region are ONE node — hover stroke and click share the box.
        if (!store)
            return frame with
            {
                Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = _open,
                WhilePressed = new MotionTarget { Scale = 0.985f },
                Transition = MotionTok.StandardSpring,
                Children = [ HighlightCard.Media(h, _item.Poster, store: false), RegularBody(h, overflows) ],
            };

        // Store card: frame holds two siblings so a card never nests a button inside a button — the hit region
        // (band + title + slot, no label row) opens the viewer, the footer's own Button.Accent opens the Store.
        var hitRegion = new BoxEl
        {
            Direction = 1, MinWidth = 0f,
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = _open,
            WhilePressed = new MotionTarget { Scale = 0.985f },
            Transition = MotionTok.StandardSpring,
            Children = [ HighlightCard.Media(h, _item.Poster, store: true), StoreBody(h, overflows) ],
        };
        var footer = new BoxEl
        {
            Direction = 0, Padding = new Edges4(M.PadL, M.StoreButtonGap, M.PadR, M.PadB),
            Children = [ Button.Accent(Loc.Get(Strings.WhatsNew.StoreCta), HighlightCard.OpenStoreListing) ],
        };
        return frame with { Children = [ hitRegion, footer ] };
    }

    static Element Title(ReleaseHighlight h) => new TextEl(h.Title)
    {
        Size = M.TitleSize, Weight = 600, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap,
        LineHeight = M.TitleLineHeight, MaxLines = M.TitleMaxLines, Trim = TextTrim.CharacterEllipsis,
    };

    Element RegularBody(ReleaseHighlight h, bool overflows) => new BoxEl
    {
        Direction = 1, Gap = M.TitleBodyGap, MinWidth = 0f,
        Padding = new Edges4(M.PadL, M.PadT, M.PadR, M.PadB),
        Children = [ Title(h), BodySlot(h, overflows), ReadMoreRow() ],
    };

    Element StoreBody(ReleaseHighlight h, bool overflows) => new BoxEl
    {
        // The store hit region's own bottom padding stops 4 DIP under the slot; the footer (a sibling of this whole
        // box) carries the rest — no "Read more" row, the accent button already says what a click does.
        Direction = 1, Gap = M.TitleBodyGap, MinWidth = 0f,
        Padding = new Edges4(M.PadL, M.PadT, M.PadR, M.HitRegionStoreBottomPad),
        Children = [ Title(h), BodySlot(h, overflows) ],
    };

    /// <summary>The fixed 68 DIP slot. The paragraph is rendered UNCLAMPED (MaxLines 0) inside a natural-height wrapper
    /// whose bounds decide the fade; the slot clips whatever falls below line 4. Clamping with MaxLines instead would
    /// make the natural height always ≤ 68 and the overflow unmeasurable — the fade could never be conditional.
    ///
    /// <para>The card's own link handler is a no-op (<c>static _ => { }</c>): the card is one button (or, on the store
    /// card, sits inside one), so a live link run inside it would be a second nested click target. Links are live in
    /// the viewer, where the body is not itself a button.</para></summary>
    Element BodySlot(ReleaseHighlight h, bool overflows) => new BoxEl
    {
        Height = M.BodySlotHeight, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
        ZStack = true, ClipToBounds = true, HitTestVisible = false,
        Children =
        [
            // A FLEX COLUMN, not a bare ZStack layer, and that is the whole trick. ArrangeZStack gives an auto-sized
            // layer its desired height ONLY when it is Center/End aligned, and then clamps it to the stack
            // (`ch = min(slotH, desired)`) — so NO child of a 68 DIP ZStack can ever report a height that says
            // "there is more". Inside a column the rule is the ordinary flex one: a Shrink = 0 child keeps its
            // measured height and overflows its parent, which the slot then clips. That overflow is the measurement.
            new BoxEl
            {
                AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch,
                Direction = 1, MinWidth = 0f,
                Children =
                [
                    new BoxEl   // the measured wrapper: full width, NATURAL height, clipped by the slot above it
                    {
                        Shrink = 0f, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
                        OnBoundsChanged = r =>
                        {
                            bool v = M.Overflows(r.H);                        // RectF.H, not .Height
                            if (_overflows.Peek() != v) _overflows.Value = v; // flip only when the ANSWER changes
                        },
                        Children =
                        [
                            RichTextBlock.Paragraph(
                                ReleaseNotesText.ToSpans(MarkdownLite.Tokenize(h.Body), static _ => { }),
                                isTextSelectionEnabled: false)
                            with
                            {
                                Size = M.BodySize, LineHeight = M.BodyLineHeight, MaxLines = 0,
                                Wrap = TextWrap.Wrap, Color = Tok.TextSecondary,
                            },
                        ],
                    },
                ],
            },
            new BoxEl   // the edge fade, bottom-anchored, full width
            {
                Height = M.FadeHeight, AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Stretch,
                HitTestVisible = false, Gradient = Fade,
                Opacity = overflows ? 1f : 0f,
                Transition = MotionTok.ControlFaster,   // 83 ms KeepFade: a resize eases the fade, never pops it
            },
        ],
    };

    /// <summary>Present on every non-store card, whether or not its body overflows — a short card gives no other cue
    /// that a click opens anything, and a keyboard user needs a name on the target.</summary>
    static Element ReadMoreRow() => new BoxEl
    {
        Direction = 0, Gap = 4f, AlignItems = FlexAlign.Center, Height = M.LabelHeight, HitTestVisible = false,
        Children =
        [
            new TextEl(Loc.Get(Strings.WhatsNew.ReadMore))
            {
                Size = 12f, Weight = 600, Color = Tok.TextSecondary, Wrap = TextWrap.NoWrap, MaxLines = 1,
                // Eases with the nearest interactive ancestor's hover/focus progress — that ancestor is the hit
                // region (this card, or the store card's hit-region sibling), so no separate hover state is needed.
                HoverColor = Tok.AccentTextPrimary, FocusedColor = Tok.AccentTextPrimary,
                BrushTransitionMs = WaveeMotion.Faster,
            },
            Icon(Icons.ChevronRight, 10f, Tok.TextSecondary) with
            {
                HoverColor = Tok.AccentTextPrimary, FocusedColor = Tok.AccentTextPrimary,
                BrushTransitionMs = WaveeMotion.Faster,
            },
        ],
    };
}
