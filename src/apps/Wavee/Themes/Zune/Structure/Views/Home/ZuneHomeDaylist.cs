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
//   Compact       a 112 row (Z-Frame-Compact): 112 art, the eyebrow and the 22/28 title over two lines, ONE 40 play ring at the strip's end
//   one row       a window too short for any of them (Z-Shell-Short, the height rung): 56 high, 56 art, the eyebrow and the title on one line, the
//                 36 play ring at the end
// The whole plate opens the daylist; its right click and the "…" ring open the playlist's own menu. The skeleton is the same builders over placeholders.

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
                   primary: true, () => HomeCardNav.Play(in c), lay.RingSize, lay.RingTarget),
            // Shuffle STARTS the daylist shuffled; it never toggles.
            K.Ring("zune-daylist-shuffle", Icons.Shuffle, Loc.Get(Strings.Detail.Shuffle), lay.RingLabels, primary: false,
                   () => HomeVerbs.ShuffleContext(c.Uri), lay.RingSize, lay.RingTarget),
            SaveRing(uri, title, lay),
            MoreRing(lay),
        ];

        var root = Strip(props.Eyebrow, title, Strings.Home.DaylistMeta(card.TrackCount), rings, props.Clock ?? new BoxEl(),
                         DaylistArt.Of(card.HeaderImageUrl, card.ImageUrl), lay)
            .Interactive(Interaction.Tile) with
            {
                OnClick = () => HomeCardNav.Open(in c), Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true,
            };
        return menu is not null && host is not null ? root.WithContextMenu(host, menu) : root;
    }

    /// <summary>The cold-load shape: the same builders over placeholder words and stand-in rings, the art's placeholder slot, a static clock.</summary>
    internal static Element Skeleton(ZuneHomeLayout lay)
    {
        bool words = lay.RingLabels;
        Element[] rings =
        [
            K.Ring("zune-daylist-play", Icons.Play, Loc.Get(Strings.Home.Play), words, primary: true, static () => { }, lay.RingSize, lay.RingTarget),
            K.Ring("zune-daylist-shuffle", Icons.Shuffle, Loc.Get(Strings.Detail.Shuffle), words, primary: false, static () => { }, lay.RingSize, lay.RingTarget),
            K.Ring("zune-daylist-save", Icons.Heart, Loc.Get(Strings.Home.Save), words, primary: false, static () => { }, lay.RingSize, lay.RingTarget),
            K.Ring("zune-daylist-more", Icons.More, Loc.Get(Strings.Home.More), showLabel: false, primary: false, static () => { }, lay.RingSize, lay.RingTarget),
        ];
        var current = DaypartRules.OfHour(DateTime.Now.Hour);
        Element clock = Clock(StaticLine(Strings.Home.NextDaylistIn(DaypartRules.Countdown(0))), (int)current, null, lay.ClockBarW);
        string eyebrow = Loc.Get(Strings.Home.YourDaylist);
        return Strip(eyebrow, Loc.Get(Strings.Home.Layout.Daylist), Strings.Home.DaylistMeta(50), rings, clock, null, lay);
    }

    /// <summary>The save ring: the shared heart (it subscribes to the saved set on its own) inside the outlined ring, with its word where the tier
    /// has words.</summary>
    static Element SaveRing(string uri, string title, ZuneHomeLayout lay)
    {
        var disc = K.RingDisc(Icons.Heart, primary: false, lay.RingSize) with
        {
            Children = [Embed.Comp(() => new Controls.SaveButton { Uri = uri, Name = title, Box = lay.RingSize - 2f * H.RingStroke }) with { Key = "save:" + uri }],
        };
        var kids = new List<Element>(2) { disc };
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
        var disc = K.RingDisc(Icons.More, primary: false, lay.RingSize) with
        {
            BlocksDragArm = true,
            Children = [Controls.IconAction(Icons.More, null, requestsContext: true, size: lay.RingSize - 2f * H.RingStroke)],
        };
        // Where the tier draws the disc inside a larger target the key stays on the target (the node the tests and the pointer find).
        Element ring = lay.RingTarget > lay.RingSize
            ? new BoxEl
            {
                Key = "zune-daylist-more", Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Shrink = 0f, BlocksDragArm = true,
                MinWidth = lay.RingTarget, MinHeight = lay.RingTarget, Children = [disc],
            }
            : disc with { Key = "zune-daylist-more" };
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
        bool one = lay.DaylistTitleLines <= 1;
        if (lay.DaylistTitleSize > 0f) text = text with { Size = lay.DaylistTitleSize, LineHeight = lay.DaylistTitleLine };
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
    static BoxEl Strip(string eyebrow, string title, string meta, Element[] rings, Element clock, string? art, ZuneHomeLayout lay)
    {
        int shown = Math.Min(rings.Length, lay.DaylistRings);
        var inCopy = new List<Element>(shown);
        for (int i = lay.PlayAtEnd ? 1 : 0; i < shown; i++) inCopy.Add(rings[i]);

        var copyKids = new List<Element>(4)
        {
            R.Type.DenseMeta(eyebrow) with
            {
                Key = "zune-daylist-eyebrow", Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            },
            TitleBox(title, lay),
        };
        if (lay.DaylistMeta)
            copyKids.Add(R.Type.DenseMeta(meta) with
            {
                Key = "zune-daylist-meta", Color = Tok.TextSecondary, Case = R.Case.For(LabelKind.Label), MaxLines = 1,
                Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            });
        if (inCopy.Count > 0)
            copyKids.Add(new BoxEl
            {
                Key = "zune-daylist-actions", Direction = 0, Wrap = true, Gap = lay.RingsGap, AlignItems = FlexAlign.Center, MinWidth = 0f,
                Margin = new Edges4(0f, H.DaylistActionsTop, 0f, 0f), Children = inCopy.ToArray(),
            });

        var kids = new List<Element>(4)
        {
            new BoxEl
            {
                Key = "zune-daylist-art", Shrink = 0f, ClipToBounds = true, MinWidth = 0f, Width = lay.DaylistArtW,
                Children = [Controls.CoverFill(art, 0f, H.DaylistArtDecodePx, focusY: H.DaylistArtFocusY)],
            },
            new BoxEl
            {
                Key = "zune-daylist-copy", Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
                Justify = FlexJustify.Center, Gap = H.DaylistCopyGap, Padding = new Edges4(lay.DaylistPad, 0f, lay.DaylistPad, 0f),
                Children = copyKids.ToArray(),
            },
            new BoxEl
            {
                Key = "zune-daylist-clock", Direction = 1, Shrink = 0f, MinWidth = 0f, Justify = FlexJustify.Center, ClipToBounds = true,
                Width = lay.ClockW, Padding = new Edges4(0f, 0f, lay.ClockW > 0f ? H.ClockPadRight : 0f, 0f), Children = [clock],
            },
        };
        if (lay.PlayAtEnd)
            kids.Add(new BoxEl
            {
                Key = "zune-daylist-end", Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center, Margin = new Edges4(0f, 0f, lay.DaylistPad, 0f),
                Children = [rings[0]],
            });

        return new BoxEl
        {
            Key = H.DaylistKey, Direction = 0, MinWidth = 0f, AlignItems = FlexAlign.Stretch, ClipToBounds = true,
            Height = lay.DaylistGrows ? float.NaN : lay.DaylistH, MinHeight = lay.DaylistGrows ? lay.DaylistH : 0f,
            // The strip's own height (176 / 112 / 56 across the tiers and the height rung) glides on a SIZE channel, so it holds a track on the crossing frame
            // whatever the chapter around it does (a position-only slide would snap beside a live reflow).
            Layout = H.ChapterResize,
            Fill = Prop.Of(static () => Tok.FillCardDefault), Corners = R.Shape.All(ShapeRole.Card),
            Children = kids.ToArray(),
        };
    }

    // ══ THE CLOCK ════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.HomeDaylistClock"/>.</summary>
    public static Element ClockTemplate(HomeDaylistClockProps p, in ViewContext ctx)
    {
        int current = (int)p.Current;
        float barW = ZuneHomeTiers.Of(ctx.Tier()).ClockBarW;
        switch (p.Face)
        {
            case HomeDaylistClockFace.Counting when p.Countdown is { } countdown:
            {
                // The sentence stays the translator's; only the count is cut out of it, bound to the host's signal.
                var (before, after) = DaylistCountdownLine.Split(Strings.Home.NextDaylistIn(DaylistCountdownLine.Slot));
                var kids = new List<Element>(3);
                if (before.Length > 0) kids.Add(LineText(before));
                kids.Add(LineText("") with { Text = Prop.Of(() => countdown.Value), Color = Tok.TextPrimary, Shrink = 0f });
                if (after.Length > 0) kids.Add(LineText(after));
                return Clock(new BoxEl { Direction = 0, MinWidth = 0f, AlignItems = FlexAlign.Center, Children = kids.ToArray() }, current, null, barW);
            }
            case HomeDaylistClockFace.Late:
                return Clock(StaticLine(p.Line), current,
                    K.Link("zune-daylist-check-again", Loc.Get(Strings.Home.Daylist.CheckAgain), p.CheckAgain ?? (static () => { })), barW);
            default:
                return Clock(StaticLine(p.Line), current, null, barW);
        }
    }

    static TextEl LineText(string s)
        => R.Type.MicroMeta(s) with { Color = Tok.TextSecondary, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f };

    static Element StaticLine(string s) => LineText(s) with { Case = R.Case.For(LabelKind.Label) };

    /// <summary>The clock block: the line, the five bars (the current one lit), the dayparts line (the current one in the primary ink), then the
    /// action (the Late face's link) when there is one.</summary>
    static Element Clock(Element line, int current, Element? action, float barW)
    {
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
            if (i > 0) names.Add(LineText(" · ") with { Shrink = 0f });
            names.Add(LineText(Loc.Get(DaypartTimeline.LabelKey(i))) with
            {
                Case = R.Case.For(LabelKind.Label), Color = on ? Tok.TextPrimary : Tok.TextSecondary, Shrink = on ? 0f : 1f,
            });
        }
        var kids = new List<Element>(4)
        {
            line,
            new BoxEl { Direction = 0, Gap = H.BarGap, AlignItems = FlexAlign.End, Height = H.BarLitH, MinWidth = 0f, Children = bars },
            new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f, ClipToBounds = true, Children = names.ToArray() },
        };
        if (action is not null) kids.Add(action);
        return new BoxEl { Key = "zune-daylist-clock-block", Direction = 1, Gap = H.ClockGap, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Children = kids.ToArray() };
    }
}
