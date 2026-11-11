// ── Themes/Zune/Structure/Views/Stage/ZuneStageShelf.cs — Zune's `Queue.StageShelf` (Video mode, Cinema) ─────────────────────────────────
//
// The "up next" strip over the music video, above the plate: "up next" in the display face (light, lowercase through the heading case) over a
// row of flat cards, Zune's panorama strip. A card has no frame and no corners: a video's card IS its 16:9 still, an audio-only track's IS its
// square cover at the same height (the shape tells them apart), the title and the artists under it at the art's width, and what plays next carries
// the 4-px accent line along its art's foot. The shape comes from the queue's video fact (`IQueueLane.VideoOf`), never from an image landing. A
// click plays that track, a right-click opens the row's menu. As many cards as the stage's width seats (`ZuneStageVideoGeometry.Seat`), never a
// scrollbar. While the END CARD is up the card names what plays next, so the strip lists the tracks after it ("then"), none marked.

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

/// <summary>Zune's "up next" strip over the music video (<see cref="ViewKeys.QueueStageShelf"/>).</summary>
internal sealed class ZuneStageShelf : Component
{
    /// <summary>The registered template of <see cref="ViewKeys.QueueStageShelf"/>.</summary>
    public static Element Template(QueueStageShelfProps props, in ViewContext ctx)
        => Embed.Comp(new Props(props, ZuneStageTiers.StageOf(ctx.Tier())), static () => new ZuneStageShelf());

    sealed record Props(QueueStageShelfProps Shelf, ZuneStageLayout Tier);

    static readonly LayoutTransition s_move = new(TransitionChannels.Bounds, TransitionDynamics.Spring(G.MoveResponse, G.MoveDamping), SizeMode.Reveal);

    int _held;

    public override Element Render()
    {
        var p = UseProps<Props>();
        var t = p.Tier;
        var z = t.Video;
        var st = UseContext(Stage.StageContext)!;
        var L = st.Layout.Value;
        var set = st.Accent.Value;
        float cardW = z.ShelfCardW;
        float width = MathF.Max(0f, L.W - 2f * t.Gutter);
        var queue = p.Shelf.Queue;
        bool then = st.EndCard.Value;
        int skip = then ? 1 : 0;
        var lane = p.Shelf.Lane;
        var verbs = p.Shelf.Verbs;
        var overControls = st.OverControls;
        float gap = z.Px(G.ShelfCardGap);
        bool play = st.VideosPlay.Value;
        int rows = Math.Max(0, Math.Min(G.ShelfMaxCards, queue.Rows.Length - skip));
        Span<float> widths = stackalloc float[G.ShelfMaxCards];
        Span<bool> videos = stackalloc bool[G.ShelfMaxCards];
        for (int i = 0; i < rows; i++)
        {
            // a video's card is its 16:9 still while videos play (StageCtx.VideosPlay); else every card is its square cover
            videos[i] = play && lane.VideoOf(queue.Rows[i + skip].Ref).HasVideo;
            widths[i] = G.ArtW(cardW, videos[i]);
        }
        var (n, scale) = G.Seat(width, widths[..rows], _held, gap);
        _held = n;
        float artH = MathF.Round(G.ThumbH(cardW) * scale);
        var cards = new Element[n];
        for (int i = 0; i < n; i++)
            cards[i] = Card(lane, verbs, queue.Rows[i + skip], MathF.Round(widths[i] * scale), artH, videos[i], i == 0 && !then, set.Fill, overControls, z);
        return new BoxEl
        {
            Direction = 1, Width = width, Height = G.ShelfH(z),
            Children =
            [
                new BoxEl
                {
                    Height = z.Dx(G.ShelfHeadLine), Shrink = 0f, Margin = new Edges4(0f, 0f, 0f, z.Px(G.ShelfHeadGap)), HitTestVisible = false,
                    Children =
                    [
                        new TextEl(n > 0 ? Loc.Get(then ? Strings.Stage.EndCard.Then : Strings.Stage.UpNext) : "")
                        {
                            Size = z.Dx(G.ShelfHeadSize), LineHeight = z.Dx(G.ShelfHeadLine), Weight = G.TitleWeight, FontFamily = R.Type.DisplayFace,
                            Color = Ink.Ink, Case = R.Case.For(LabelKind.Heading), MaxLines = 1, Wrap = TextWrap.NoWrap,
                        },
                    ],
                },
                new BoxEl { Direction = 0, Gap = gap, Children = cards },
            ],
        };
    }

    /// <summary>One flat card: the art (the 16:9 still, or the square cover) with the accent line under what plays next, over the title and the
    /// artists at the art's width; a click plays the row, a right-click opens its menu.</summary>
    static Element Card(IQueueLane lane, IQueueVerbs verbs, QueueRowProps row, float artW, float artH, bool video, bool next, ColorF accent, Action<bool> overControls, ZuneStageVideo z)
    {
        var text = lane.TextOf(row.Ref);
        string? url = video ? lane.VideoOf(row.Ref).Still : text.Art;
        ulong itemId = row.ItemId;
        int index = row.Index;
        EntityRef r = row.Ref;
        var art = new List<Element>(2) { Controls.Artwork(url, artW, artH, R.Shape.Cover, decodePx: (int)MathF.Round(artW * 2f)) };
        if (next)
            art.Add(new BoxEl { AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Stretch, Height = G.NextLineH, Fill = accent, HitTestVisible = false });
        var el = new BoxEl
        {
            Key = lane.RowKey("shelf", itemId, index),
            Width = artW, Shrink = 0f, Direction = 1, Gap = z.Px(G.ShelfTextGap),
            Fill = ColorF.Transparent, HoverFill = ColorF.Transparent, PressedFill = ColorF.Transparent,
            PressScale = R.Motion.ScaleSubtle.Press,
            Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true, AllowFocusOnInteraction = false,
            OnClick = () => verbs.Play(itemId, index, r),
            OnHoverMove = _ => overControls(true), OnPointerExit = () => overControls(false),
            Enter = new EnterExit(Dy: z.Px(G.RiseDy), Opacity: 0f, Active: true), Exit = new EnterExit(Opacity: 0f, Active: true), Layout = s_move,
            Children =
            [
                new BoxEl
                {
                    Width = artW, Height = artH, Shrink = 0f, ZStack = true, ClipToBounds = true, Corners = CornerRadius4.All(R.Shape.Cover), HitTestVisible = false,
                    Children = art.ToArray(),
                },
                new BoxEl
                {
                    Direction = 1, MinWidth = 0f, HitTestVisible = false,
                    Children =
                    [
                        TitleLine(text.Title, lane.VideoOf(r).HasVideo, z),
                        new TextEl(text.Artists) { Size = z.Px(G.ShelfMetaSize), LineHeight = z.Px(G.ShelfMetaLine), Color = Ink.InkSecondary, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f },
                    ],
                },
            ],
        };
        return lane.Attach(el, itemId, index, r);
    }

    /// <summary>The card's title, and after it the track lists' film glyph for a track with a music video.</summary>
    static Element TitleLine(string title, bool video, ZuneStageVideo z)
    {
        var text = new TextEl(title) { Size = z.Px(G.ShelfTitleSize), LineHeight = z.Px(G.ShelfTitleLine), Color = Ink.Ink, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f };
        if (!video) return text;
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS, MinWidth = 0f,
            Children = [text, new TextEl(Icons.Movie) { Size = z.Px(G.ShelfMetaSize), FontFamily = Tok.Definition.Fonts.Icon, Color = Ink.InkTertiary, Shrink = 0f }],
        };
    }
}
