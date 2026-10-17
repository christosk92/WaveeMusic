// ── Themes/Zune/Structure/Views/Home/ZuneHomeDaylist.cs — Zune's `Home.Daylist` and `Home.DaylistClock` (themes plan 6.3; P5 Z5) ───────────────
//
// Board Z-Home's daylist strip: ONE 176 plate (the card ground), the art 320 wide on the left (the daylist's header art, focused a little above
// centre), the copy 32 in (the eyebrow 13 secondary, the daylist's name 40/48 light on one line, the meta 13 secondary, then the actions 22 apart:
// the accent PLAY disc and the outlined shuffle, save and "…" rings, each 36 with its lowercase word), and the clock column 260 wide at the end:
//
//   next daylist in 02:14:33           the count bound to the host's signal (nothing re-renders on the tick)
//   ▁ ▁ █ ▁ ▁                          five 40-wide bars 4 apart, the current daypart's 20 tall in the accent, the rest 6 at 14 % ink
//   early morning · morning · afternoon · evening · night    the current one in the primary ink
//
// Under the strip's room (`ZuneDaylistForm.Stacked`: Compact and Small) the SAME three nodes stack: the art over the copy, the clock under it (only
// the direction and the sizes are live props, so a crossing moves them). The whole plate opens the daylist; its right click and the "…" ring
// open the playlist's own menu. The skeleton is the same builders over placeholders.

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

        Element[] actions =
        [
            K.Ring("zune-daylist-play", pauses ? Icons.Pause : Icons.Play, Loc.Get(pauses ? Strings.Home.Pause : Strings.Home.Play), showLabel: true,
                   primary: true, () => HomeCardNav.Play(in c)),
            // Shuffle STARTS the daylist shuffled; it never toggles.
            K.Ring("zune-daylist-shuffle", Icons.Shuffle, Loc.Get(Strings.Detail.Shuffle), showLabel: true, primary: false, () => HomeVerbs.ShuffleContext(c.Uri)),
            SaveRing(uri, title),
            MoreRing(),
        ];

        var root = Strip(props.Eyebrow, title, Strings.Home.DaylistMeta(card.TrackCount), actions, props.Clock ?? new BoxEl(),
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
        Element[] actions =
        [
            K.Ring("zune-daylist-play", Icons.Play, Loc.Get(Strings.Home.Play), showLabel: true, primary: true, static () => { }),
            K.Ring("zune-daylist-shuffle", Icons.Shuffle, Loc.Get(Strings.Detail.Shuffle), showLabel: true, primary: false, static () => { }),
            K.Ring("zune-daylist-save", Icons.Heart, Loc.Get(Strings.Home.Save), showLabel: true, primary: false, static () => { }),
            K.Ring("zune-daylist-more", Icons.More, Loc.Get(Strings.Home.More), showLabel: false, primary: false, static () => { }),
        ];
        var current = DaypartRules.OfHour(DateTime.Now.Hour);
        Element clock = Clock(StaticLine(Strings.Home.NextDaylistIn(DaypartRules.Countdown(0))), (int)current, null);
        string words = Loc.Get(Strings.Home.YourDaylist);
        return Strip(words, Loc.Get(Strings.Home.Layout.Daylist), Strings.Home.DaylistMeta(50), actions, clock, null, lay);
    }

    /// <summary>The save ring: the shared heart (it subscribes to the saved set on its own) inside the outlined ring, with its word.</summary>
    static Element SaveRing(string uri, string title)
    {
        var disc = K.RingDisc(Icons.Heart, primary: false) with
        {
            Children = [Embed.Comp(() => new Controls.SaveButton { Uri = uri, Name = title, Box = H.RingSize - 2f * H.RingStroke }) with { Key = "save:" + uri }],
        };
        return new BoxEl
        {
            Key = "zune-daylist-save", Direction = 0, AlignItems = FlexAlign.Center, Gap = H.DaylistActionLabelGap, Shrink = 0f, BlocksDragArm = true,
            Children =
            [
                disc,
                R.Type.TrackTitle(Loc.Get(Strings.Home.Save)) with { Color = Tok.TextPrimary, Case = R.Case.For(LabelKind.Command), MaxLines = 1, Wrap = TextWrap.NoWrap },
            ],
        };
    }

    /// <summary>The "…" ring: a stand-alone icon action that re-enters the context funnel, so the plate's attached menu opens anchored at it.</summary>
    static Element MoreRing()
        => Controls.Named(K.RingDisc(Icons.More, primary: false) with
        {
            Key = "zune-daylist-more", BlocksDragArm = true,
            Children = [Controls.IconAction(Icons.More, null, requestsContext: true, size: H.RingSize - 2f * H.RingStroke)],
        }, Loc.Get(Strings.Home.More));

    /// <summary>The strip in the tier's form: ONE tree (art, copy, clock) whose direction and sizes are live props.</summary>
    static BoxEl Strip(string eyebrow, string title, string meta, Element[] actions, Element clock, string? art, ZuneHomeLayout lay)
    {
        bool stacked = lay.DaylistStacked;
        Element copy = new BoxEl
        {
            Key = "zune-daylist-copy", Direction = 1, Grow = 1f, Shrink = 1f, Basis = stacked ? float.NaN : 0f, MinWidth = 0f,
            Justify = FlexJustify.Center, Gap = H.DaylistCopyGap,
            Padding = new Edges4(H.DaylistCopyPad, stacked ? H.DaylistCopyPad / 2f : 0f, H.DaylistCopyPad, stacked ? H.DaylistCopyPad / 2f : 0f),
            Children =
            [
                R.Type.DenseMeta(eyebrow) with { Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                R.Type.HeroTitle(title) with { MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                R.Type.DenseMeta(meta) with
                {
                    Color = Tok.TextSecondary, Case = R.Case.For(LabelKind.Label), MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                },
                new BoxEl
                {
                    Direction = 0, Wrap = true, Gap = H.DaylistActionsGap, RowGap = H.DaylistCopyGap, AlignItems = FlexAlign.Center, MinWidth = 0f,
                    Margin = new Edges4(0f, H.DaylistActionsTop, 0f, 0f), Children = actions,
                },
            ],
        };
        return new BoxEl
        {
            Key = H.DaylistKey, Direction = (byte)(stacked ? 1 : 0), MinWidth = 0f, AlignItems = FlexAlign.Stretch,
            Height = stacked ? float.NaN : lay.DaylistH, ClipToBounds = true,
            Fill = Prop.Of(static () => Tok.FillCardDefault), Corners = R.Shape.All(ShapeRole.Card),
            Layout = ZuneMotion.ColumnCollapse,
            Children =
            [
                new BoxEl
                {
                    Key = "zune-daylist-art", Shrink = 0f, ClipToBounds = true, MinWidth = 0f,
                    Width = stacked ? float.NaN : lay.DaylistArtW, Height = stacked ? lay.DaylistH : float.NaN,
                    Children = [Controls.CoverFill(art, 0f, H.DaylistArtDecodePx, focusY: H.DaylistArtFocusY)],
                },
                copy,
                new BoxEl
                {
                    Key = "zune-daylist-clock", Direction = 1, Shrink = 0f, MinWidth = 0f, Justify = FlexJustify.Center,
                    Width = stacked ? float.NaN : lay.ClockW,
                    Padding = new Edges4(stacked ? H.DaylistCopyPad : 0f, 0f, H.ClockPadRight, stacked ? H.DaylistCopyPad / 2f : 0f),
                    Children = [clock],
                },
            ],
        };
    }

    // ══ THE CLOCK ════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.HomeDaylistClock"/>.</summary>
    public static Element ClockTemplate(HomeDaylistClockProps p, in ViewContext ctx)
    {
        int current = (int)p.Current;
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
                return Clock(new BoxEl { Direction = 0, MinWidth = 0f, AlignItems = FlexAlign.Center, Children = kids.ToArray() }, current, null);
            }
            case HomeDaylistClockFace.Late:
                return Clock(StaticLine(p.Line), current,
                    K.Link("zune-daylist-check-again", Loc.Get(Strings.Home.Daylist.CheckAgain), p.CheckAgain ?? (static () => { })));
            default:
                return Clock(StaticLine(p.Line), current, null);
        }
    }

    static TextEl LineText(string s)
        => R.Type.MicroMeta(s) with { Color = Tok.TextSecondary, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f };

    static Element StaticLine(string s) => LineText(s) with { Case = R.Case.For(LabelKind.Label) };

    /// <summary>The clock block: the line, the five bars (the current one lit), the dayparts line (the current one in the primary ink), then the
    /// action (the Late face's link) when there is one.</summary>
    static Element Clock(Element line, int current, Element? action)
    {
        int lit = Math.Clamp(current, 0, DaypartTimeline.Segments - 1);
        var bars = new Element[DaypartTimeline.Segments];
        var names = new List<Element>(DaypartTimeline.Segments * 2);
        for (int i = 0; i < bars.Length; i++)
        {
            bool on = i == lit;
            bars[i] = new BoxEl
            {
                Width = H.BarW, Height = on ? H.BarLitH : H.BarH, Shrink = 0f,
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
