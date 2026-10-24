// ── Themes/Zune/Structure/Views/Home/ZuneHomeDaylist.cs — Zune's `Home.Daylist` and `Home.DaylistClock` (themes plan 6.3; P5 Z5, ZT4) ────────
//
// Board Z-Home's daylist strip: ONE 176 plate (the card ground), the art 320 wide on the left (the daylist's header art, focused a little above
// centre), the copy 32 in (the eyebrow 13 secondary, the daylist's name 40/48 light on one line, the meta 13 secondary, then the actions 22 apart:
// the accent PLAY disc and the outlined shuffle, save and "…" rings, each 36 with its lowercase word), and the clock column 260 wide at the end:
//
//   next daylist in 02:14:33           the count bound to the host's signal (nothing re-renders on the tick)
//   ▁ ▁ █ ▁ ▁                          five 40-wide bars 4 apart, the current daypart's 20 tall in the accent, the rest 6 at 14 % ink
//   early morning · morning · afternoon · evening · night    the current one in the primary ink
//
// THE TIERS (`ZuneHomeLayout`, ZT4; ONE tree in every form, only sizes and a few presences are live props):
//   Huge          a 220 strip: 440 art, the title the 64/72 hero, play · shuffle · save, a 320 clock of 52 bars (Z-Frame-Huge)
//   Standard      the 176 strip above
//   Small         a 112 ROW (Z-Frame-Small): 140 art, the eyebrow and the 26/32 title, play and shuffle with their words; no clock, no meta line
//   Compact       BLED (the owner's i8 option C over Z-Frame-Compact's 112 row): the art is the strip's ground under a scrim of the theme's ground;
//                 over it, full width, the eyebrow with the play ring at its row's end, the 40/48 title on as many lines as the name needs (never
//                 cut: the strip grows), then the countdown with the shuffle, save and "…" rings ending its row (36 discs in 40 targets)
//   one row       a window too short for any of them (Z-Shell-Short, the height rung): 56 high, 56 art, the eyebrow and the title on one line, the
//                 36 play ring at the end
// The whole plate opens the daylist; its right click and the "…" ring open the playlist's own menu. The skeleton is the same builders over placeholders.
// ONE PARENT FOR THE RINGS (round G): the rings row (`zune-daylist-actions`) is a child of the copy box (`zune-daylist-copy`) in EVERY form, play
// first. Under the strip's forms the copy is a column (the text block over the rings row); in the one-row form it is a row (the text block, then the
// rings row, which holds the play ring alone, at the strip's end). The play ring never changes parent, so a height crossing keeps its node and its move
// is a slide, not a remount.
// THE BLED FORM IS THE SAME TREE: the strip, the copy and the rings row turn into z-stacks (live props), the art fills the strip instead of taking a
// width, each ring stands in its slot (`zune-daylist-slot-<i>`, in every form) whose alignment and margin park it (play at the top end, the others
// at the foot's end), and the eyebrow and the countdown stand in their own row boxes (in every form) whose height and end inset leave the rings
// their room. Nothing changes parent, so the 820 <-> 400 crossing keeps the art's and the rings' nodes.
// HIT TARGETS (Compact, 40 x 40): the node that takes the click is the ring's target box (`zune-daylist-more`) or the heart button itself at the target's
// size (`save:<uri>`); the disc is only the picture inside, never a smaller target of its own.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.HomeUi;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using H = Wavee.Themes.Zune.ZuneHomeRules;
using K = Wavee.Themes.Zune.ZuneDiscoveryKit;
using FluentGpu.Signals;

namespace Wavee.Themes.Zune;

/// <summary>Zune's daylist strip and its clock.</summary>
internal static class ZuneHomeDaylist
{
    /// <summary>The registered template of <see cref="ViewKeys.HomeDaylist"/>.</summary>
    public static Element Template(HomeDaylistProps props, in ViewContext ctx)
    {
        var lay = ZuneHomeTiers.Of(ctx.Tier());
        // The strip's own height (a wrapped title grows it): the art's width follows it, so the cover keeps its shape. Only a form with an art COLUMN
        // writes it: the bled forms' height (the art is their ground) would hand the strip, on the crossing back, an art sized for a taller strip.
        var stripH = ctx.Hooks.UseSignal(lay.DaylistH);
        var zone = props.Zone;
        if (zone.Items.Count == 0) return new BoxEl();
        var card = zone.Items[0];
        string uri = card.Uri;
        if (uri.Length == 0) return new BoxEl();

        var c = card;
        string title = card.Title;
        bool pauses = props.Pauses;
        var host = props.Overlay;
        var menu = HomeCardNav.MenuOf(in card);

        Element[] rings =
        [
            K.Ring("zune-daylist-play", pauses ? Icons.Pause : Icons.Play, Loc.Get(pauses ? Strings.Home.Pause : Strings.Home.Play), lay.RingLabels,
                   primary: true, () => HomeCardNav.Play(in c), lay.RingSize, lay.RingTarget, stableName: true),
            // Shuffle STARTS the daylist shuffled; it never toggles.
            K.Ring("zune-daylist-shuffle", Icons.Shuffle, Loc.Get(Strings.Detail.Shuffle), lay.RingLabels, primary: false,
                   () => HomeVerbs.ShuffleContext(c.Uri), lay.RingSize, lay.RingTarget, stableName: true),
            SaveRing(uri, title, lay),
            MoreRing(lay),
        ];

        var root = Strip(props.Eyebrow, title, Strings.Home.DaylistMeta(card.TrackCount), rings, props.Clock ?? new BoxEl(),
                         DaylistArt.Of(card.HeaderImageUrl, card.ImageUrl), lay,
                         ArtWidth(lay, stripH), r => { if (!lay.DaylistBleed && MathF.Abs(stripH.Peek() - r.H) > 0.5f) stripH.Value = r.H; })
            .Interactive(Interaction.Tile) with
            {
                OnClick = () => HomeCardNav.Open(in c), Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true,
            };
        return Swap(menu is not null && host is not null ? root.WithContextMenu(host, menu) : root, lay);
    }

    /// <summary>The strip inside its host: a z-stack whose height eases (<see cref="ZuneHomeRules.DaylistResize"/>), the strip under a layer keyed by its form's
    /// family (the strip, art | copy | clock, or the bled art). A crossing within a family keeps every node (the strip's own height glides, the rings slide);
    /// a crossing between them (the strip's content rung, Small &lt;-&gt; Standard at a docked panel) fades the old layer out, then the new one in
    /// (`SwapThrough`: the two forms' titles never show at once) instead of snapping the art's width, the copy's inset and the clock column, and the
    /// host's height carries the page below.</summary>
    static Element Swap(Element strip, ZuneHomeLayout lay) => new BoxEl
    {
        Key = H.DaylistHostKey, Direction = 1, ZStack = true, MinWidth = 0f, AlignItems = FlexAlign.Stretch, ClipToBounds = true,
        Layout = Design.Reduced ? null : H.DaylistResize,
        Children =
        [
            new BoxEl
            {
                Key = H.DaylistFormPrefix + (lay.DaylistBleed ? "bleed" : "strip"), Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
                Enter = ZuneMotion.SwapThrough, Exit = ZuneMotion.SwapThrough, Transition = ZuneMotion.SwapThroughMotion, Children = [strip],
            },
        ],
    };

    /// <summary>The cold-load shape: the same builders over placeholder words and stand-in rings, the art's placeholder slot, a static clock.</summary>
    internal static Element Skeleton(ZuneHomeLayout lay)
    {
        bool words = lay.RingLabels;
        Element[] rings =
        [
            K.Ring("zune-daylist-play", Icons.Play, Loc.Get(Strings.Home.Play), words, primary: true, static () => { }, lay.RingSize, lay.RingTarget, stableName: true),
            K.Ring("zune-daylist-shuffle", Icons.Shuffle, Loc.Get(Strings.Detail.Shuffle), words, primary: false, static () => { }, lay.RingSize, lay.RingTarget, stableName: true),
            K.Ring("zune-daylist-save", Icons.Heart, Loc.Get(Strings.Home.Save), words, primary: false, static () => { }, lay.RingSize, lay.RingTarget),
            K.Ring("zune-daylist-more", Icons.More, Loc.Get(Strings.Home.More), showLabel: false, primary: false, static () => { }, lay.RingSize, lay.RingTarget),
        ];
        var current = DaypartRules.OfHour(DateTime.Now.Hour);
        Element clock = Clock(StaticLine(Strings.Home.NextDaylistIn(DaypartRules.Countdown(0)), lay.DaylistBleed), (int)current, null, lay.ClockBarW,
                              lay.ClockInline);
        string eyebrow = Loc.Get(Strings.Home.YourDaylist);
        return Swap(Strip(eyebrow, Loc.Get(Strings.Home.Layout.Daylist), Strings.Home.DaylistMeta(50), rings, clock, null, lay,
                     lay.DaylistBleed ? float.NaN : lay.DaylistArtW, null), lay);
    }

    /// <summary>The art's width: the strip's height times the tier's aspect, between the board's width and the tier's cap (a fixed width where the
    /// tier has no aspect). A bound prop: a strip that grows with its title re-lays the art, nothing re-renders.</summary>
    static Prop<float> ArtWidth(ZuneHomeLayout lay, FluentGpu.Signals.Signal<float> stripH)
    {
        // ALWAYS bound: a box whose width goes from a binding to a plain value keeps the binding's last answer, so the crossing into the bled form
        // (where the art is the strip's ground and fills the z-stack: NaN) would keep the row's art width.
        if (lay.DaylistBleed) return Prop.Of(static () => float.NaN);
        if (lay.DaylistArtAspect <= 0f) return Prop.Of(() => lay.DaylistArtW);
        return Prop.Of(() => lay.DaylistArtWidthAt(stripH.Value));
    }

    /// <summary>The save ring: the shared heart (it subscribes to the saved set on its own) inside the outlined ring, with its word where the tier
    /// has words.</summary>
    static Element SaveRing(string uri, string title, ZuneHomeLayout lay)
    {
        // The picture (the outlined disc, no glyph of its own) under the heart button, which is the target: its box is `SaveHitBox` (40) at every
        // tier (the button freezes its props at mount, so the size cannot follow the tier; where the stack is the disc's 36 it fits the stack).
        float stack = MathF.Max(lay.RingSize, lay.RingTarget);
        var disc = K.RingDisc(Icons.Heart, primary: false, lay.RingSize) with { Children = [], HitTestVisible = false };
        Element ring = new BoxEl
        {
            Direction = 0, ZStack = true, Width = stack, Height = stack, Shrink = 0f,
            Children =
            [
                new BoxEl { Grow = 1f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HitTestVisible = false, Children = [disc] },
                new BoxEl
                {
                    Grow = 1f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HitTestPassThrough = true,
                    Children = [Embed.Comp(() => new Controls.SaveButton { Uri = uri, Name = title, Box = H.SaveHitBox }) with { Key = "save:" + uri }],
                },
            ],
        };
        var kids = new List<Element>(2) { ring };
        if (lay.RingLabels)
            kids.Add(R.Type.TrackTitle(Loc.Get(Strings.Home.Save)) with
            {
                Color = Tok.TextPrimary, Case = R.Case.For(LabelKind.Command), MaxLines = 1, Wrap = TextWrap.NoWrap,
            });
        return new BoxEl
        {
            Key = "zune-daylist-save", Direction = 0, AlignItems = FlexAlign.Center, Gap = H.DaylistActionLabelGap, Shrink = 0f, BlocksDragArm = true,
            Justify = lay.RingTarget > 0f ? FlexJustify.Center : FlexJustify.Start, MinWidth = lay.RingTarget, MinHeight = lay.RingTarget,
            Children = kids.ToArray(),
        };
    }

    /// <summary>The "…" ring: a stand-alone icon action that re-enters the context funnel, so the plate's attached menu opens anchored at it.</summary>
    static Element MoreRing(ZuneHomeLayout lay)
    {
        // ONE node at every tier: the target box (the disc's size where the tier has no larger target) takes the click and re-enters the context funnel;
        // the disc inside is the picture. The key stays on the target (the node the tests and the pointer find).
        Element ring = new BoxEl
        {
            Key = "zune-daylist-more", Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Shrink = 0f, BlocksDragArm = true,
            MinWidth = lay.RingTarget, MinHeight = lay.RingTarget,
            Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand, ClickRequestsContext = true,
            Children = [K.RingDisc(Icons.More, primary: false, lay.RingSize)],
        };
        return Controls.Named(ring, Loc.Get(Strings.Home.More));
    }

    /// <summary>The title in the tier's role (the quickplay head, the heading, the daylist title or the hero), on its lines. Its box scales from the
    /// left edge when the role changes across a crossing (the panorama's recipe).</summary>
    static Element TitleBox(string title, ZuneHomeLayout lay)
    {
        TextEl text = lay.DaylistTitle switch
        {
            ZuneDaylistTitle.Group => R.Type.QuickplayHead(title),
            ZuneDaylistTitle.Heading => R.Type.Heading(title),
            ZuneDaylistTitle.Hero => R.Type.Hero(title),
            _ => R.Type.HeroTitle(title),
        };
        bool one = lay.DaylistTitleLines == 1;   // 0 = no cap (the bled form)
        return new BoxEl
        {
            Key = "zune-daylist-title", Direction = 1, MinWidth = 0f, Animate = Design.Reduced ? null : ZuneMotion.TitleScale,
            Children =
            [
                text with
                {
                    MaxLines = lay.DaylistTitleLines, Wrap = one ? TextWrap.NoWrap : TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                },
            ],
        };
    }

    /// <summary>The strip in the tier's form: ONE tree (art, copy, clock, and the play ring at the end where the tier puts it) whose direction
    /// and sizes are live props.</summary>
    static BoxEl Strip(string eyebrow, string title, string meta, Element[] rings, Element clock, string? art, ZuneHomeLayout lay,
                       Prop<float> artWidth, Action<RectF>? onBounds)
    {
        bool bleed = lay.DaylistBleed;
        bool parked = bleed && lay.DaylistRingsParked;   // Compact's bled form: the rings stand over the copy, at its rows' ends
        float target = MathF.Max(lay.RingSize, lay.RingTarget);
        int shown = Math.Min(rings.Length, lay.DaylistRings);
        var inCopy = new List<Element>(shown);
        for (int i = 0; i < shown; i++) inCopy.Add(RingSlot(rings[i], i, shown, lay, target));

        var textKids = new List<Element>(3)
        {
            // The eyebrow's row: where the rings are parked it is the play ring's height and stops short of it (the ring ends the row).
            new BoxEl
            {
                Key = "zune-daylist-eyebrow-row", Direction = 1, Justify = FlexJustify.Center, MinWidth = 0f, MinHeight = parked ? target : float.NaN,
                Padding = parked ? new Edges4(0f, 0f, target + H.DaylistBleedRowGap, 0f) : default,
                Children =
                [
                    R.Type.DenseMeta(eyebrow) with
                    {
                        Key = "zune-daylist-eyebrow", Color = bleed ? Tok.TextPrimary : Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                        MinWidth = 0f,
                    },
                ],
            },
            TitleBox(title, lay),
        };
        if (lay.DaylistMeta)
            textKids.Add(R.Type.DenseMeta(meta) with
            {
                Key = "zune-daylist-meta", Color = Tok.TextSecondary, Case = R.Case.For(LabelKind.Label), MaxLines = 1,
                Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            });
        // Where the tier has no clock column the countdown stands here, one line under the title (the clock is a feature: it moves, it is not dropped).
        // Its row: where the rings are parked it is the rings' height and stops short of them (the shuffle, save and "…" rings end the row).
        if (lay.ClockInline)
            textKids.Add(new BoxEl
            {
                Key = "zune-daylist-clock-row", Direction = 1, Justify = FlexJustify.Center, MinWidth = 0f, MinHeight = parked ? target : float.NaN,
                Padding = parked ? new Edges4(0f, 0f, lay.DaylistTailRingsW + H.DaylistBleedRowGap, 0f) : default,
                Children = [clock],
            });

        // The copy: ONE box, the rings row its last child in every form. A column under the strip's forms (the text block, then the rings row 8 below
        // it, the wide bled form's too); a row in the one-row form (the text block grows, the rings row, the play ring alone, ends it); a z-stack where the
        // rings are parked (Compact's bled form: the text block with the rings row over it, each ring parked by its slot). The rings row slides to its place.
        bool end = lay.PlayAtEnd;
        var copyKids = new Element[]
        {
            new BoxEl
            {
                Key = "zune-daylist-text", Direction = 1, Gap = H.DaylistCopyGap, MinWidth = 0f,
                Justify = parked ? FlexJustify.SpaceBetween : FlexJustify.Center,
                Grow = end ? 1f : 0f, Shrink = 1f, Basis = end ? 0f : float.NaN, Children = textKids.ToArray(),
            },
            new BoxEl
            {
                Key = "zune-daylist-actions", Direction = 0, Wrap = !end && !parked, ZStack = parked, HitTestPassThrough = parked, Gap = lay.RingsGap,
                AlignItems = FlexAlign.Center, MinWidth = 0f,
                Shrink = end ? 0f : 1f, Margin = end || parked ? default : new Edges4(0f, H.DaylistActionsTop, 0f, 0f),
                Animate = Design.Reduced ? null : ZuneMotion.PivotSlide, Children = inCopy.ToArray(),
            },
        };

        var kids = new List<Element>(4)
        {
            // The art: a z-stack of the cover and its scrim in every form; the scrim paints only where the art is the strip's ground.
            new BoxEl
            {
                Key = "zune-daylist-art", Shrink = 0f, ClipToBounds = true, MinWidth = 0f, Width = artWidth, ZStack = true,
                Children =
                [
                    Controls.CoverFill(art, 0f, H.DaylistArtDecodePx, focusY: H.DaylistArtFocusY),
                    new BoxEl { Key = "zune-daylist-scrim", HitTestVisible = false, MinWidth = 0f, Gradient = parked ? H.DaylistScrim(Tok.FillSolidBase) : bleed ? H.DaylistWideScrim(Tok.FillSolidBase) : null },
                ],
            },
            new BoxEl
            {
                Key = "zune-daylist-copy", Direction = (byte)(end ? 0 : 1), ZStack = parked, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
                Justify = end || parked ? FlexJustify.Start : FlexJustify.Center, AlignItems = end ? FlexAlign.Center : FlexAlign.Stretch,
                Gap = end ? lay.DaylistPad : H.DaylistCopyGap,
                Padding = bleed ? new Edges4(lay.DaylistPad, lay.DaylistPadY, lay.DaylistPad, lay.DaylistPadY) : new Edges4(lay.DaylistPad, 0f, lay.DaylistPad, 0f),
                Children = copyKids,
            },
            new BoxEl
            {
                Key = "zune-daylist-clock", Direction = 1, Shrink = 0f, MinWidth = 0f, Justify = FlexJustify.Center, ClipToBounds = true,
                Width = lay.ClockW, MaxHeight = lay.ClockW > 0f ? float.NaN : 0f,   // a tier with no clock must not let its hidden face stretch the strip
                Padding = new Edges4(0f, 0f, lay.ClockW > 0f ? H.ClockPadRight : 0f, 0f), Children = lay.ClockW > 0f ? [clock] : [],
            },
        };
        return new BoxEl
        {
            Key = H.DaylistKey, Direction = 0, ZStack = bleed, MinWidth = 0f, AlignItems = FlexAlign.Stretch, ClipToBounds = true, OnBoundsChanged = onBounds,
            Height = lay.DaylistGrows ? float.NaN : lay.DaylistH, MinHeight = lay.DaylistGrows ? lay.DaylistH : 0f,
            // The strip's own height (176 / 160 / 112 / 56 across the tiers and the height rung) lands at once: its HOST (`Swap`) glides it on a SIZE channel,
            // so it holds a track on the crossing frame whatever the chapter around it does (a position-only slide would snap beside a live reflow), and one
            // track carries both a crossing inside a form and the cross-fade between forms (a second track here left the host short of the new height).
            Fill = Prop.Of(static () => Tok.FillCardDefault), Corners = R.Shape.All(ShapeRole.Card),
            Children = kids.ToArray(),
        };
    }

    /// <summary>A ring's slot (in every form, so a crossing never re-parents the ring): a plain cell of the rings row, or, where the rings are parked, a layer of
    /// the rings' z-stack parked at its place: the play ring (the first) at the top end, the eyebrow's row; the others at the foot's end, the countdown's
    /// row, the last one at the very end and each earlier one a target and a gap further in.</summary>
    static Element RingSlot(Element ring, int i, int shown, ZuneHomeLayout lay, float target)
    {
        bool bleed = lay.DaylistBleed && lay.DaylistRingsParked;
        float inset = bleed && i > 0 ? (shown - 1 - i) * (target + lay.RingsGap) : 0f;
        return new BoxEl
        {
            Key = "zune-daylist-slot-" + i.ToString(CultureInfo.InvariantCulture), Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f, MinWidth = 0f,
            Height = bleed ? target : float.NaN,
            AlignSelf = !bleed ? FlexAlign.Auto : i == 0 ? FlexAlign.Start : FlexAlign.End,
            JustifySelf = bleed ? FlexAlign.End : FlexAlign.Auto,
            Margin = new Edges4(0f, 0f, inset, 0f),
            Children = [ring],
        };
    }

    // ══ THE CLOCK ════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.HomeDaylistClock"/>.</summary>
    public static Element ClockTemplate(HomeDaylistClockProps p, in ViewContext ctx)
    {
        int current = (int)p.Current;
        var lay = ZuneHomeTiers.Of(ctx.Tier());
        float barW = lay.ClockBarW;
        bool inline = lay.ClockInline;
        bool bleed = lay.DaylistBleed;   // over the scrimmed art the line takes the primary ink (the 4.5 : 1 the scrim's floor is set for)
        switch (p.Face)
        {
            case HomeDaylistClockFace.Counting when p.Countdown is { } countdown:
            {
                // The sentence stays the translator's; only the count is cut out of it, bound to the host's signal.
                var (before, after) = DaylistCountdownLine.Split(Strings.Home.NextDaylistIn(DaylistCountdownLine.Slot));
                var kids = new List<Element>(3);
                if (before.Length > 0) kids.Add(LineText(before, bleed) with { Case = R.Case.For(LabelKind.Label) });
                kids.Add(LineText("") with { Text = Prop.Of(() => countdown.Value), Color = Tok.TextPrimary, Shrink = 0f });
                if (after.Length > 0) kids.Add(LineText(after, bleed) with { Case = R.Case.For(LabelKind.Label) });
                return Clock(new BoxEl { Direction = 0, MinWidth = 0f, AlignItems = FlexAlign.Center, Children = kids.ToArray() }, current, null, barW, inline);
            }
            case HomeDaylistClockFace.Late:
                return Clock(StaticLine(p.Line, bleed), current,
                    K.Link("zune-daylist-check-again", Loc.Get(Strings.Home.Daylist.CheckAgain), p.CheckAgain ?? (static () => { })), barW, inline);
            default:
                return Clock(StaticLine(p.Line, bleed), current, null, barW, inline);
        }
    }

    // `strong`: the bled form's line, over the scrimmed art: the primary ink, and it wraps beside the rings that end its row instead of being cut.
    static TextEl LineText(string s, bool strong = false)
        => R.Type.MicroMeta(s) with
        {
            Color = strong ? Tok.TextPrimary : Tok.TextSecondary, MaxLines = strong ? 0 : 1, Wrap = strong ? TextWrap.Wrap : TextWrap.NoWrap,
            Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };

    static Element StaticLine(string s, bool strong = false) => LineText(s, strong) with { Case = R.Case.For(LabelKind.Label) };

    /// <summary>The clock block: the line, the five bars (the current one lit), the dayparts line (the current one in the primary ink), then the
    /// action (the Late face's link) when there is one.</summary>
    static Element Clock(Element line, int current, Element? action, float barW, bool inline = false)
    {
        // Inline (a tier with no clock column): just the count line, its action beside it, one row under the title.
        if (inline)
            return new BoxEl
            {
                Key = "zune-daylist-clock-block", Direction = 0, Wrap = true, Gap = H.ClockGap, AlignItems = FlexAlign.Center, MinWidth = 0f,
                Children = action is null ? [line] : [line, action],
            };
        int lit = Math.Clamp(current, 0, DaypartTimeline.Segments - 1);
        var bars = new Element[DaypartTimeline.Segments];
        var names = new List<Element>(DaypartTimeline.Segments * 2);
        for (int i = 0; i < bars.Length; i++)
        {
            bool on = i == lit;
            bars[i] = new BoxEl
            {
                Width = barW, Height = on ? H.BarLitH : H.BarH, Shrink = 0f,
                Fill = on ? Prop.Of(static () => Tok.AccentDefault) : Prop.Of(static () => Tok.TextPrimary with { A = H.BarIdleAlpha }),
            };
            // Each word carries the dot after it ("evening ·"), so the line wraps between words and a dot never starts a line (the board's second
            // line is "night"); the current one is in the primary ink.
            names.Add(LineText(Loc.Get(DaypartTimeline.LabelKey(i)) + (i < bars.Length - 1 ? " ·" : "")) with
            {
                Case = R.Case.For(LabelKind.Label), Color = on ? Tok.TextPrimary : Tok.TextSecondary, Shrink = 0f,
            });
        }
        var kids = new List<Element>(4)
        {
            line,
            new BoxEl { Direction = 0, Gap = H.BarGap, AlignItems = FlexAlign.End, Height = H.BarLitH, MinWidth = 0f, Children = bars },
            new BoxEl { Direction = 0, Wrap = true, Gap = H.DaypartWordGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = names.ToArray() },
        };
        if (action is not null) kids.Add(action);
        return new BoxEl { Key = "zune-daylist-clock-block", Direction = 1, Gap = H.ClockGap, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Children = kids.ToArray() };
    }
}
