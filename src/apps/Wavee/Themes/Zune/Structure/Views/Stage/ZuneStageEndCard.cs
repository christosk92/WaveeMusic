// ── Themes/Zune/Structure/Views/Stage/ZuneStageEndCard.cs — Zune's `Stage.EndCard` (Video mode, the end of a music video) ────────────────
//
// The picture's last frame stays where it is under the chrome's veil; over it, in Zune's voice: "up next in 5" in the light display face (the
// number ticking through a bound text), then the next track's flat 16:9 still (the video's, or the audio-only track's square cover on the floor at
// the still's size) with the 4-px accent progress line along its foot, and beside it the accent artist line, the TITLE in the light display face
// (two lines, wrapping, never shrunk), "from …" and two words: "play now" on the accent plate and "cancel". CINEMA centres the card between the
// head and the strip's reserved place (the strip lists the tracks after this one: "then"); THEATRE centres it in the picture's box. The countdown is
// the core's (one timer): the progress line is one linear transition from where the countdown stands at the card's mount, so nothing renders per frame.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;

using G = Wavee.Themes.Zune.ZuneStageVideoGeometry;
using Ink = Wavee.Theming.Roles.StageInk;

namespace Wavee.Themes.Zune;

/// <summary>Zune's end-of-video card (<see cref="ViewKeys.StageEndCard"/>).</summary>
internal sealed class ZuneStageEndCard : Component
{
    /// <summary>The registered template of <see cref="ViewKeys.StageEndCard"/>.</summary>
    public static Element Template(StageEndCardProps props, in ViewContext ctx)
        => Embed.Comp(new Props(props, ZuneStageTiers.StageOf(ctx.Tier())), static () => new ZuneStageEndCard());

    sealed record Props(StageEndCardProps Card, ZuneStageLayout Tier);

    int _leftAtMount = -1;

    public override Element Render()
    {
        var p = UseProps<Props>();
        var t = p.Tier;
        var z = t.Video;
        var st = UseContext(Stage.StageContext)!;
        var L = st.Layout.Value;
        bool theatre = st.Theatre.Value;
        var set = st.Accent.Value;
        if (_leftAtMount < 0) _leftAtMount = Math.Max(1, st.EndRemainingMs());
        var fill = new FillProps(_leftAtMount, _leftAtMount / (float)st.EndTotalMs);
        var region = Region(in L, t, theatre);
        float thumbW = G.EndThumbW(z, region.W, theatre);
        float thumbH = G.ThumbH(thumbW);
        float textW = G.EndTextW(z, thumbW, region.W);
        var lane = p.Card.Lane;
        var rows = p.Card.Queue.Rows;
        int at = 0;
        while (at < rows.Length && lane.TextOf(rows[at].Ref).Unavailable) at++;
        Element body = at >= rows.Length ? new BoxEl() : Card(st, lane, rows[at], p.Card.From, t, thumbW, thumbH, textW, set, fill);
        return Stage.Layer with
        {
            Children =
            [
                new BoxEl
                {
                    AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Margin = new Edges4(region.X, region.Y, 0f, 0f),
                    Width = region.W, Height = region.H, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HitTestPassThrough = true,
                    Children = [body],
                },
            ],
        };
    }

    /// <summary>Cinema: between the head and the lower block (the strip's reserved place); Theatre: inside the picture's box, above its plate.</summary>
    static RectF Region(in Stage.Layout L, ZuneStageLayout t, bool theatre)
    {
        var z = t.Video;
        if (theatre)
        {
            var box = G.Theatre(in L, t).Picture;
            float pad = z.Px(G.EndTheatrePad);
            return new RectF(box.X + pad, box.Y + pad, MathF.Max(0f, box.W - 2f * pad), MathF.Max(0f, box.H - G.BarH(t) - 2f * pad));
        }
        float top = t.HeadH;
        float bottom = G.LowerBlockTop(in L, t, shelf: z.ShelfCardW > 0f);
        return new RectF(t.Gutter, top, MathF.Max(0f, L.W - 2f * t.Gutter), MathF.Max(0f, bottom - top));
    }

    static Element Card(Stage.StageCtx st, IQueueLane lane, QueueRowProps row, string from, ZuneStageLayout t, float thumbW, float thumbH, float textW, AccentSet set, FillProps fill)
    {
        var z = t.Video;
        var text = lane.TextOf(row.Ref);
        var video = lane.VideoOf(row.Ref);
        float target = MathF.Max(t.TargetH, z.Px(G.EndWordLine));
        var lines = new List<Element>(5)
        {
            new TextEl(text.Artists)
            {
                Size = z.Dx(G.ArtistSize), LineHeight = z.Dx(G.ArtistLine), Weight = G.ArtistWeight, FontFamily = R.Type.DisplayFace, Color = set.Text,
                MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
            },
            new TextEl(text.Title)
            {
                Size = z.Dx(G.EndTitleSize), LineHeight = z.Dx(G.EndTitleLine), Weight = G.TitleWeight, FontFamily = R.Type.DisplayFace, CharSpacing = G.TitleTracking,
                Color = Ink.Ink, Wrap = TextWrap.Wrap, MaxLines = G.EndTitleLines, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
            },
        };
        if (from.Length > 0)
            lines.Add(new TextEl(Strings.Stage.EndCard.From(from))
            {
                Size = z.Px(G.EndFromSize), LineHeight = z.Px(G.EndFromLine), Color = Ink.InkTertiary, MaxLines = 1, Wrap = TextWrap.NoWrap,
                Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
            });
        lines.Add(new BoxEl
        {
            Direction = 0, Gap = z.Px(G.EndWordGap), Margin = new Edges4(0f, z.Px(G.EndWordsAbove), 0f, 0f), AlignItems = FlexAlign.Center,
            Children =
            [
                new BoxEl
                {
                    Key = "stage:end:play", Height = target, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Padding = new Edges4(z.Px(G.EndPlatePadX), 0f, z.Px(G.EndPlatePadX), 0f),
                    Fill = set.Fill, HoverFill = set.FillSecondary, PressedFill = set.FillTertiary, BrushTransitionMs = Design.Motion.Faster,
                    Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand, OnClick = st.EndPlayNow,
                    Children = [new TextEl(Loc.Get(Strings.Stage.EndCard.PlayNow)) { Size = z.Px(G.EndWordSize), LineHeight = z.Px(G.EndWordLine), Weight = G.ArtistWeight, Color = set.Ink, Case = R.Case.For(LabelKind.Command), MaxLines = 1 }],
                },
                new BoxEl
                {
                    Key = "stage:end:cancel", Height = target, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center,
                    Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand, OnClick = st.EndCancel,
                    Children = [new TextEl(Loc.Get(Strings.Stage.EndCard.Cancel)) { Size = z.Px(G.EndWordSize), LineHeight = z.Px(G.EndWordLine), Color = Ink.InkSecondary, HoverColor = Ink.Ink, Case = R.Case.For(LabelKind.Command), MaxLines = 1 }],
                },
            ],
        });
        // the art is the video's 16:9 still, or the audio-only track's square cover at the still's height (the shape tells them apart, as on the
        // strip); the progress line runs along the art's own foot
        float artW = video.HasVideo ? thumbW : thumbH;
        var art = new List<Element>(2)
        {
            Controls.Artwork(video.HasVideo ? video.Still ?? text.Art : text.Art, artW, thumbH, R.Shape.Cover, decodePx: (int)MathF.Round(artW * 2f)),
            Progress(artW, set.Fill, fill),
        };
        return new BoxEl
        {
            Key = "stage:endcard", Direction = 1, Gap = z.Px(G.EndCountGap), Shrink = 0f, HitTestPassThrough = true,
            Children =
            [
                Count(st, z),
                new BoxEl
                {
                    Direction = 0, Gap = z.Px(G.EndRowGap), AlignItems = FlexAlign.Center, HitTestPassThrough = true,
                    Children =
                    [
                        new BoxEl { Width = artW, Height = thumbH, Shrink = 0f, ZStack = true, ClipToBounds = true, HitTestVisible = false, Children = art.ToArray() },
                        new BoxEl { Direction = 1, Gap = z.Px(G.EndTextGap), Width = textW, MinWidth = 0f, Shrink = 0f, Children = lines.ToArray() },
                    ],
                },
            ],
        };
    }

    /// <summary>"up next in 5": the localized sentence around its number, all in the light display face, the number in the ink, ticking.</summary>
    static Element Count(Stage.StageCtx st, ZuneStageVideo z)
    {
        string template = Loc.Get(Strings.Stage.EndCard.UpNextInKey);
        const string slot = "{seconds}";
        int cut = template.IndexOf(slot, StringComparison.Ordinal);
        string head = cut < 0 ? template : template[..cut], tail = cut < 0 ? "" : template[(cut + slot.Length)..];
        var seconds = st.EndSeconds;
        float size = z.Dx(G.EndCountSize), line = z.Dx(G.EndCountLine);
        var kids = new List<Element>(3);
        if (head.Length > 0) kids.Add(Word(head, Ink.InkSecondary, size, line));
        kids.Add(new TextEl("")
        {
            Text = Prop.Of(() => seconds.Value.ToString(System.Globalization.CultureInfo.CurrentCulture)),
            Size = size, LineHeight = line, Weight = G.TitleWeight, FontFamily = R.Type.DisplayFace, Color = Ink.Ink, MaxLines = 1, Wrap = TextWrap.NoWrap,
        });
        if (tail.Length > 0) kids.Add(Word(tail, Ink.InkSecondary, size, line));
        return new BoxEl { Key = "stage:end:caption", Direction = 0, AlignItems = FlexAlign.End, HitTestVisible = false, Children = kids.ToArray() };

        static TextEl Word(string s, ColorF c, float size, float line) => new(s)
        {
            Size = size, LineHeight = line, Weight = G.TitleWeight, FontFamily = R.Type.DisplayFace, Color = c, Case = R.Case.For(LabelKind.Heading), MaxLines = 1, Wrap = TextWrap.NoWrap,
        };
    }

    /// <summary>The 4-px line along the still's foot: the track, and the accent fill sliding in from the left over what is left.</summary>
    static Element Progress(float w, ColorF accent, FillProps fill) => new BoxEl
    {
        Key = "stage:end:progress", AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Start, Width = w, Height = G.EndProgressH, ClipToBounds = true,
        Fill = Ink.Ink with { A = G.EndProgressTrackA }, HitTestVisible = false,
        Children = [Embed.Comp(fill with { W = w, Accent = accent }, static () => new ProgressFill())],
    };

    sealed record FillProps(int LeftMs, float LeftShare)
    {
        public float W { get; init; }

        public ColorF Accent { get; init; }
    }

    sealed class ProgressFill : Component
    {
        public override Element Render()
        {
            var f = UseProps<FillProps>();
            UseTransition(AnimChannel.TranslateX, -f.LeftShare * f.W, 0f, f.LeftMs, Easing.Linear, DepKey.Empty);
            return new BoxEl { Key = "stage:end:fill", Width = f.W, Height = G.EndProgressH, Fill = f.Accent, HitTestVisible = false };
        }
    }
}
