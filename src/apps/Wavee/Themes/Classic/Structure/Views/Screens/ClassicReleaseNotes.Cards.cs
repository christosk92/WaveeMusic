// ── Themes/Classic/Structure/Views/Screens/ClassicReleaseNotes.Cards.cs — the highlight strip and the highlight card (themes plan 6.1, T11c) ─────
//
// From `Screens\ReleaseNotes.UI.cs` §3: the strip (at most three cards, an eyebrow over the row), the card (poster band, kind pill, title, the fixed 68-DIP body
// slot with its true alpha edge fade, "Read more"; the Store card with its footer button) and the poster band shared with the viewer's kind pill. The card is shared
// by the page and the after-update plate (compact = the plate's lone-card cap).

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;
using static Wavee.ReleaseNotes;
using K = Wavee.Themes.Classic.ClassicReleaseNotesRules;
using M = Wavee.Themes.Classic.ClassicReleaseNotesRules.Card;

namespace Wavee.Themes.Classic;

internal static partial class ClassicReleaseNotes
{
    /// <summary>Three is a cap, not a layout. No highlights ⇒ a 0-height, hit-invisible box: the eyebrow AND the row vanish. The record sets the form: one row of
    /// poster cards (Standard, Huge: under a 20/28 heading), two columns of ROW cards (Small) or one column of them (Compact).</summary>
    static Element Strip(IReadOnlyList<HighlightItem> highlights, Action<int> open, Action openStore, ClassicReleaseNotesLayout layout)
    {
        if (highlights.Count == 0) return new BoxEl { Height = 0f, HitTestVisible = false };
        int n = Math.Min(highlights.Count, HighlightMax);
        var form = layout.HighlightForm;
        var cards = new Element[n];
        for (int i = 0; i < n; i++)
        {
            int idx = i;   // the row's own index, not the closed-over loop variable
            Element card = form == ClassicReleaseHighlightForm.Strip
                ? CardFor(highlights[i], () => open(idx), openStore, compact: false)
                : RowCard(highlights[i], () => open(idx), openStore, layout, inRow: form == ClassicReleaseHighlightForm.Grid2);
            cards[i] = card with { Key = "hl:" + SlideId(highlights[i], i) };
        }
        Element heading = layout.HighlightsHeading
            ? new TextEl(Loc.Get(Strings.WhatsNew.Highlights)) { Size = K.HeadingSize, LineHeight = K.HeadingLine, Weight = 600, Color = Tok.TextPrimary }
            : R.Type.Eyebrow(Loc.Get(Strings.WhatsNew.Highlights)) with { Color = Tok.TextTertiary };
        Element body = form switch
        {
            ClassicReleaseHighlightForm.Grid2 => TwoColumns(cards, layout.HighlightGap),
            ClassicReleaseHighlightForm.Column => new BoxEl { Key = "rn:hl", Direction = 1, Gap = layout.HighlightGap, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, Children = cards },
            _ => new BoxEl { Key = "rn:hl", Direction = 0, Gap = layout.HighlightGap, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Children = cards },
        };
        return new BoxEl
        {
            Direction = 1, Gap = K.StripGap, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
            Children = [heading, body],
        };
    }

    /// <summary>Rows of two equal columns; a lone last card keeps its column's width (an inert spacer takes the other).</summary>
    static Element TwoColumns(Element[] cards, float gap)
    {
        var rows = new List<Element>((cards.Length + 1) / 2);
        for (int i = 0; i < cards.Length; i += 2)
            rows.Add(new BoxEl
            {
                Key = "rn:hlrow:" + (i / 2).ToString(System.Globalization.CultureInfo.InvariantCulture),
                Direction = 0, Gap = gap, AlignItems = FlexAlign.Stretch, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
                Children =
                [
                    cards[i],
                    i + 1 < cards.Length ? cards[i + 1] : new BoxEl { Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, HitTestVisible = false },
                ],
            });
        return new BoxEl { Key = "rn:hl", Direction = 1, Gap = gap, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, Children = rows.ToArray() };
    }

    /// <summary>The ROW card (Compact and Small, boards `C-ReleaseNotes-Compact` / `-Small`; also the after-update plate's cards at Compact): a 120 x 68 poster beside the
    /// kind pill, the title and "Read more". A new path: the poster-over-copy <see cref="CardView"/> and its plate arithmetic are untouched. The whole card is the hit
    /// region (at least the tier's target tall: the poster alone is 68); the Store card keeps its button a SIBLING of the hit region, as the poster card does.</summary>
    static Element RowCard(HighlightItem item, Action open, Action openStore, ClassicReleaseNotesLayout layout, bool inRow)
    {
        var h = item.Highlight;
        bool store = HighlightVisibility.IsStore(h);
        var frame = new BoxEl
        {
            Direction = 1, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, MinHeight = layout.Target,
            Grow = inRow ? 1f : 0f, Shrink = 1f, Basis = inRow ? 0f : float.NaN,
            Corners = CornerRadius4.All(Radii.Card), ClipToBounds = true,
            Fill = Tok.FillCardDefault, BorderWidth = 1f,
            BorderColor = store ? Tok.AccentDefault : Tok.StrokeCardDefault,
            HoverBorderColor = store ? Tok.AccentDefault : Tok.StrokeCardDefault,
            BrushTransitionMs = Design.Motion.Faster,
        };
        var row = new BoxEl
        {
            Direction = 0, Gap = K.RowCardGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Padding = Edges4.All(K.RowCardPad),
            Children = [RowPoster(item.Poster, store), RowCopy(h, store, layout)],
        };
        if (!store)
            return frame.Interactive(Interaction.Card) with
            {
                Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = open,
                Children = [row],
            };
        return frame with
        {
            Children =
            [
                new BoxEl { Direction = 1, MinWidth = 0f, Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = open, Children = [row] },
                new BoxEl
                {
                    Direction = 0, Padding = new Edges4(K.RowCardPad, 0f, K.RowCardPad, K.RowCardPad),
                    Children = [Btn(Loc.Get(Strings.WhatsNew.StoreCta), openStore, ButtonAppearance.Accent, ControlSize.Medium, layout.Target)],
                },
            ],
        };
    }

    /// <summary>The row card's poster: a tinted plate (a card that loses its picture reads as broken) with the image over it.</summary>
    static Element RowPoster(string? poster, bool store) => new BoxEl
    {
        Width = K.RowPosterW, Height = K.RowPosterH, Shrink = 0f, ZStack = true, ClipToBounds = true, HitTestVisible = false,
        Corners = CornerRadius4.All(Radii.Control), Fill = store ? Tok.AccentSubtle : Tok.FillSubtleSecondary,
        Children = poster is { Length: > 0 }
            ? [new ImageEl
              {
                  Source = poster, Fit = ImageFit.Cover, Height = K.RowPosterH, AlignSelf = FlexAlign.Stretch, DecodePx = K.RowPosterDecodePx,
                  Placeholder = Tok.FillSubtleSecondary,
              }]
            : [],
    };

    static Element RowCopy(ReleaseHighlight h, bool store, ClassicReleaseNotesLayout layout)
    {
        var kids = new List<Element>(3)
        {
            new BoxEl { AlignSelf = FlexAlign.Start, Children = [KindPill(h.Kind, store)] },
            new TextEl(h.Title)
            {
                Size = layout.RowTitleSize, LineHeight = layout.RowTitleLine, Weight = 600, Color = Tok.TextPrimary,
                Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis,
            },
        };
        if (!store)   // the Store card's accent button says it
            kids.Add(new TextEl(Loc.Get(Strings.WhatsNew.ReadMore)) { Size = K.MetaSize, Weight = 600, Color = Tok.AccentTextPrimary, Wrap = TextWrap.NoWrap, MaxLines = 1 });
        return new BoxEl
        {
            Direction = 1, Gap = K.RowCopyGap, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, Justify = FlexJustify.Center, HitTestVisible = false,
            Children = kids.ToArray(),
        };
    }

    /// <summary>One highlight card (shared by the page's strip and the after-update plate's row).</summary>
    static Element CardFor(HighlightItem item, Action open, Action openStore, bool compact) => Embed.Comp(() => new CardView(item, open, openStore, compact));

    /// <summary>The poster band: 16:9 from the card's own width, and a band EVEN WITHOUT a poster (a tinted plate) — a card
    /// that loses its top third reads as broken. DecodePx 1200 is the authored width; without it the poster is a smear.</summary>
    static Element Media(ReleaseHighlight h, string? poster, bool store)
    {
        var layers = new List<Element>(2);
        if (poster is { Length: > 0 })
            layers.Add(new ImageEl
            {
                Source = poster, Fit = ImageFit.Cover, AspectRatio = M.PosterAspect, DecodePx = K.PosterDecodePx,
                Corners = new CornerRadius4(Radii.Card, Radii.Card, 0f, 0f),
                Placeholder = Tok.FillSubtleSecondary, AlignSelf = FlexAlign.Stretch,
            });
        layers.Add(new BoxEl
        {
            Grow = 1f, Direction = 0, AlignItems = FlexAlign.Start, Padding = Edges4.All(8f), HitTestVisible = false,
            Children = [KindPill(h.Kind, store), Spacer(), PlayGlyph(h)],
        });
        // The ratio sits on a PLAIN column box and the layers on a ZStack inside it (G-256): a ZStack's measure ignores
        // AspectRatio and reports its tallest layer, so without a poster the band collapsed to the pill row (~36 DIP).
        return new BoxEl
        {
            AspectRatio = M.PosterAspect, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, Direction = 1, ClipToBounds = true,
            Fill = store ? Tok.AccentSubtle : Tok.FillSubtleSecondary,
            Children =
            [
                new BoxEl { ZStack = true, Grow = 1f, Basis = 0f, MinHeight = 0f, ClipToBounds = true, Children = layers.ToArray() },
            ],
        };
    }

    // Issue #89 (L3): FillSolidTertiary is a deliberately LIGHTER opaque rung than the plate so the pill is never read as
    // "the plate behind it" where the poster does not paint, plus a 0/1/4 #00000066 shadow; an explicit Height + Radii.Full
    // (clamped to H/2) because Radii.Pill (16) exceeded half this pill's height.
    static Element KindPill(string? kind, bool store) => new BoxEl
    {
        Shrink = 0f, Height = K.KindPillHeight, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Padding = new Edges4(8f, 0f, 8f, 0f), Corners = CornerRadius4.All(Radii.Full),
        Fill = store ? Tok.AccentDefault : Tok.FillSolidTertiary,
        Shadow = K.KindPillShadow,
        Children = [R.Type.MicroMeta(Loc.Get(KindPillLocKey(kind, store))) with { Weight = 600, Color = store ? Tok.TextOnAccentPrimary : Tok.TextPrimary }],
    };

    static bool IsVideo(ReleaseHighlight h) => h.Media is { } m && string.Equals(m.Kind, "video", StringComparison.OrdinalIgnoreCase);

    static Element PlayGlyph(ReleaseHighlight h) => !IsVideo(h)
        ? new BoxEl { Width = 0f, HitTestVisible = false }
        : new BoxEl
        {
            Width = K.PlayDisc, Height = K.PlayDisc, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(16f), Fill = Tok.MediaScrim,
            Children = [Icon(Icons.Play, 14f, Tok.OnMediaPrimary)],
        };

    /// <summary>One card. A component only for its <c>overflows</c> signal; the ctor args are legitimately frozen
    /// (<see cref="HighlightItem"/> is immutable, <c>open</c> captures its index). The MOTION is a poster, never live video.</summary>
    sealed class CardView(HighlightItem item, Action open, Action openStore, bool compact) : Component
    {
        readonly Signal<bool> _overflows = new(false);

        public override Element Render()
        {
            var h = item.Highlight;
            bool store = HighlightVisibility.IsStore(h);
            bool overflows = _overflows.Value;   // the slot's edge fade engages when OnBoundsChanged flips the ANSWER

            // The standard translucent Mica card (FillCardDefault, flat StrokeCardDefault) — possible only because the
            // "there is more" cue is a true alpha EdgeFade, not a painted gradient that needs an opaque far stop.
            var frame = new BoxEl
            {
                Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
                MaxWidth = compact ? M.CompactCardMaxW : M.CardMaxW,
                Corners = CornerRadius4.All(Radii.Card), ClipToBounds = true,
                Fill = Tok.FillCardDefault, BorderWidth = 1f,
                BorderColor = store ? Tok.AccentDefault : Tok.StrokeCardDefault,
                HoverBorderColor = store ? Tok.AccentDefault : Tok.StrokeCardDefault,
                BrushTransitionMs = Design.Motion.Faster,
            };

            if (!store)   // frame and hit region are ONE node: the whole Interaction.Card (hover ramp, flat stroke, 0.985 press)
                return frame.Interactive(Interaction.Card) with
                {
                    Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = open,
                    Children = [Media(h, item.Poster, store: false), Body(h, overflows, store: false)],
                };

            // Store card: the hit region and the footer are SIBLINGS — a card never nests a button in a button — and the
            // frame takes no hover ramp at all (ch 28 W26); the press spring is authored on the hit region itself.
            return frame with
            {
                Children =
                [
                    new BoxEl
                    {
                        Direction = 1, MinWidth = 0f,
                        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = open,
                        WhilePressed = new MotionTarget { Scale = 0.985f }, Transition = MotionTok.StandardSpring,
                        Children = [Media(h, item.Poster, store: true), Body(h, overflows, store: true)],
                    },
                    new BoxEl
                    {
                        Direction = 0, Padding = new Edges4(M.PadL, M.StoreButtonGap, M.PadR, M.PadB),
                        Children = [Button.Accent(Loc.Get(Strings.WhatsNew.StoreCta), openStore)],
                    },
                ],
            };
        }

        Element Body(ReleaseHighlight h, bool overflows, bool store) => new BoxEl
        {
            Direction = 1, Gap = M.TitleBodyGap, MinWidth = 0f,
            Padding = new Edges4(M.PadL, M.PadT, M.PadR, store ? M.HitRegionStoreBottomPad : M.PadB),
            Children = store
                ? [CardTitle(h), BodySlot(h, overflows)]                   // no "Read more": the accent button says it
                : [CardTitle(h), BodySlot(h, overflows), ReadMoreRow()],
        };

        static Element CardTitle(ReleaseHighlight h) => new TextEl(h.Title)
        {
            Size = M.TitleSize, Weight = 600, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap,
            LineHeight = M.TitleLineHeight, MaxLines = M.TitleMaxLines, Trim = TextTrim.CharacterEllipsis,
        };

        /// <summary>The fixed 68-DIP slot. The paragraph is UNCLAMPED (MaxLines 0) inside a natural-height wrapper whose
        /// bounds decide the fade — and that wrapper is a Shrink=0 child of a FLEX COLUMN, not a bare ZStack layer:
        /// ArrangeZStack clamps an auto-sized layer to the stack, so no child of a 68-DIP ZStack could ever report "more".
        /// Links inside a card are inert (the card is one button); they are live in the viewer.</summary>
        Element BodySlot(ReleaseHighlight h, bool overflows) => new BoxEl
        {
            Height = M.BodySlotHeight, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
            ZStack = true, ClipToBounds = true, HitTestVisible = false,
            EdgeFade = overflows ? new EdgeFadeSpec(EdgeMask.Bottom, M.FadeHeight) : null,
            Children =
            [
                new BoxEl
                {
                    AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, Direction = 1, MinWidth = 0f,
                    Children =
                    [
                        new BoxEl
                        {
                            Shrink = 0f, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
                            OnBoundsChanged = r =>
                            {
                                bool v = M.Overflows(r.H);
                                if (_overflows.Peek() != v) _overflows.Value = v;   // only when the ANSWER changes
                            },
                            Children =
                            [
                                RichTextBlock.Paragraph(ToSpans(MarkdownLite.Tokenize(h.Body), IgnoreLink), isTextSelectionEnabled: false)
                                    with { Size = M.BodySize, LineHeight = M.BodyLineHeight, MaxLines = 0, Wrap = TextWrap.Wrap, Color = Tok.TextSecondary },
                            ],
                        },
                    ],
                },
            ],
        };

        /// <summary>On every non-store card, overflowing or not: a short card gives no other cue that a click opens anything.
        /// Its ink eases with the nearest interactive ancestor's hover AND focus.</summary>
        static Element ReadMoreRow() => new BoxEl
        {
            Direction = 0, Gap = K.ReadMoreGap, AlignItems = FlexAlign.Center, Height = M.LabelHeight, HitTestVisible = false,
            Children =
            [
                new TextEl(Loc.Get(Strings.WhatsNew.ReadMore))
                {
                    Size = K.MetaSize, Weight = 600, Color = Tok.TextSecondary, Wrap = TextWrap.NoWrap, MaxLines = 1,
                    HoverColor = Tok.AccentTextPrimary, FocusedColor = Tok.AccentTextPrimary, BrushTransitionMs = Design.Motion.Faster,
                },
                Icon(Icons.ChevronRight, 10f, Tok.TextSecondary) with
                    { HoverColor = Tok.AccentTextPrimary, FocusedColor = Tok.AccentTextPrimary, BrushTransitionMs = Design.Motion.Faster },
            ],
        };
    }
}
