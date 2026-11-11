// ── Themes/Classic/Structure/Views/Stage/ClassicStageShelf.cs — Classic's `Queue.StageShelf` and the stage's 16:9 thumbnail ─────────────
//
// THE THUMBNAIL (Video mode, the boards' rules note item 6): while the stage shows the music video, every queued track is told apart at a glance.
// A track with a video shows the video's still full-bleed; an audio-only track shows its square cover inset, centred, on a dark plate of the same
// 16:9 size; each carries a small tag ("Video" / "Audio"). The Theatre list's rows (`ClassicQueueStagePane`) and the shelf's cards share it.
//
// THE SHELF (the board "Classic · Cinema"): "Up next" over a row of the next tracks' cards, each the thumbnail (its duration in the corner) over the
// title and the artist line; the first card, what plays next, is ringed in the accent. A click plays that track (the queue's own verb), a right-click
// opens the row's menu. As many cards as the stage's width holds at the tier's card width (`ClassicStageLayout.ShelfCardW`), never a scrollbar. The
// picture chrome (`ClassicStagePictureChrome`) places it above the transport card under the idle chrome's Show; its place (and the title block's above
// it) is reserved whether it shows or rests. While the END CARD is up (`StageCtx.EndCard`) the card names what plays next, so the shelf is the board's
// "Then" row: the tracks after the card's, none ringed.

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

using G = Wavee.Themes.Classic.ClassicStageShelfGeometry;
using Ink = Wavee.Theming.Roles.StageInk;
using T = Wavee.Themes.Classic.ClassicStageThumbGeometry;

namespace Wavee.Themes.Classic;

/// <summary>The stage's 16:9 thumbnail in Video mode: the video's still, or the audio-only track's cover inset on a plate, tagged.</summary>
internal static class ClassicStageThumb
{
    /// <summary>A <paramref name="w"/> x <paramref name="h"/> thumbnail for one row, with its kind tag, an optional duration badge and an optional
    /// ring (<paramref name="ringW"/> 0: none).</summary>
    public static Element Of(in QueueRowText text, in QueueRowVideo video, float w, float h, float corners, string? duration = null,
        float ringW = 0f, ColorF ring = default)
    {
        bool small = w < T.SmallBelowW;
        var kids = new List<Element>(4);
        if (video.HasVideo)
            kids.Add(Controls.Artwork(video.Still, w, h, corners, decodePx: (int)MathF.Round(w * 2f)));
        else
        {
            float cover = MathF.Round(h * T.CoverShare);
            kids.Add(new BoxEl
            {
                AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Center, Width = cover, Height = cover, HitTestVisible = false,
                Children = [Controls.Artwork(text.Art, cover, cover, Radii.Control, decodePx: (int)MathF.Round(cover * 2f))],
            });
        }
        kids.Add(Tag(Loc.Get(video.HasVideo ? Strings.Stage.Mode.Video : Strings.Stage.AudioTag), small, top: true));
        if (duration is { Length: > 0 }) kids.Add(Tag(duration, small, top: false));
        if (ringW > 0f)
            kids.Add(new BoxEl
            {
                AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
                Corners = CornerRadius4.All(corners), BorderWidth = ringW, BorderColor = ring,
            });
        return new BoxEl
        {
            Width = w, Height = h, Shrink = 0f, ZStack = true, ClipToBounds = true, Corners = CornerRadius4.All(corners),
            Fill = Ink.Floor, HitTestVisible = false,
            Children = kids.ToArray(),
        };
    }

    /// <summary>The kind tag (top-left) or the duration badge (bottom-right) on the veil's plate.</summary>
    static Element Tag(string text, bool small, bool top)
    {
        float inset = small ? T.SmallTagInset : T.TagInset, padX = small ? T.SmallTagPadX : T.TagPadX;
        return new BoxEl
        {
            AlignSelf = top ? FlexAlign.Start : FlexAlign.End, JustifySelf = top ? FlexAlign.Start : FlexAlign.End, HitTestVisible = false,
            Margin = top ? new Edges4(inset, inset, 0f, 0f) : new Edges4(0f, 0f, inset, inset),
            Padding = new Edges4(padX, T.TagPadY, padX, T.TagPadY), Corners = CornerRadius4.All(T.TagCorners),
            Fill = Ink.Veil with { A = T.TagPlateA },
            Children =
            [
                new TextEl(text)
                {
                    Size = small ? T.SmallTagSize : top ? T.TagSize : T.BadgeSize, LineHeight = small ? T.SmallTagLine : top ? T.TagLine : T.BadgeLine,
                    Weight = top ? T.TagWeight : (ushort)400, Color = top ? Ink.InkSecondary : Ink.Ink, MaxLines = 1, Wrap = TextWrap.NoWrap,
                },
            ],
        };
    }
}

/// <summary>Wavee Classic's Up next shelf over the music video (<see cref="ViewKeys.QueueStageShelf"/>).</summary>
internal sealed class ClassicStageShelf : Component
{
    /// <summary>The registered template of <see cref="ViewKeys.QueueStageShelf"/>.</summary>
    public static Element Template(QueueStageShelfProps props, in ViewContext ctx)
        => Embed.Comp(new Props(props, ClassicStageTiers.StageOf(ctx.Tier())), static () => new ClassicStageShelf());

    /// <summary>The core's props and the window tier's stage record (its card width).</summary>
    sealed record Props(QueueStageShelfProps Shelf, ClassicStageLayout Tier);

    /// <summary>The cards' slide when the width (a tier crossing) moves them.</summary>
    static readonly LayoutTransition s_move = new(TransitionChannels.Bounds,
        TransitionDynamics.Spring(ClassicStageGeometry.PaneMoveResponse, ClassicStageGeometry.PaneMoveDamping), SizeMode.Reveal);

    int _held;

    public override Element Render()
    {
        var p = UseProps<Props>();
        var props = p.Shelf;
        var tier = p.Tier;
        var st = UseContext(Stage.StageContext)!;
        var L = st.Layout.Value;
        var set = st.Accent.Value;
        float cardW = tier.ShelfCardW;
        float width = ClassicStagePictureChrome.ShelfWidth(L, tier);
        var queue = props.Queue;
        // the end card shows the first row: the shelf starts after it ("Then")
        bool then = st.EndCard.Value;
        int skip = then ? 1 : 0;
        // the count holds while the cards can narrow to keep it (a crossing's narrowing frames never drop a card to bring it back)
        _held = G.CardsHeld(width, cardW, _held);
        int n = Math.Max(0, Math.Min(_held, queue.Rows.Length - skip));
        float cardFit = G.CardWFor(width, cardW, _held);
        var lane = props.Lane;
        var verbs = props.Verbs;
        var overControls = st.OverControls;
        var cards = new Element[n];
        for (int i = 0; i < n; i++) cards[i] = Card(lane, verbs, queue.Rows[i + skip], cardFit, i == 0 && !then, set.Fill);
        return new BoxEl
        {
            Direction = 1, Gap = G.CaptionGap, Width = width, Height = G.Height(cardW),
            OnHoverMove = _ => overControls(true), OnPointerExit = () => overControls(false),
            Children =
            [
                new TextEl(n > 0 ? Loc.Get(then ? Strings.Stage.EndCard.Then : Strings.Stage.UpNext) : "")
                {
                    Size = G.CaptionSize, LineHeight = G.CaptionLine, Weight = G.CaptionWeight, Color = Ink.Ink, MaxLines = 1, Wrap = TextWrap.NoWrap,
                },
                new BoxEl { Direction = 0, Gap = G.CardGap, Children = cards },
            ],
        };
    }

    /// <summary>One card: the thumbnail over the title and the artist line; a click plays the row, a right-click opens its menu.</summary>
    static Element Card(IQueueLane lane, IQueueVerbs verbs, QueueRowProps row, float cardW, bool next, ColorF accent)
    {
        var text = lane.TextOf(row.Ref);
        var video = lane.VideoOf(row.Ref);
        float thumbH = G.ThumbH(cardW);
        ulong itemId = row.ItemId;
        int index = row.Index;
        EntityRef r = row.Ref;
        var el = new BoxEl
        {
            Key = lane.RowKey("shelf", itemId, index),
            Width = cardW, Shrink = 0f, Direction = 1, Gap = G.TextGap,
            Corners = CornerRadius4.All(G.ThumbCorners), Fill = ColorF.Transparent, HoverFill = ColorF.Transparent, PressedFill = ColorF.Transparent,
            PressScale = R.Motion.ScaleSubtle.Press,
            Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true, AllowFocusOnInteraction = false,
            OnClick = () => verbs.Play(itemId, index, r),
            Enter = ClassicQueue.RowEnter, Exit = ClassicQueue.RowExit, Layout = s_move,
            Children =
            [
                ClassicStageThumb.Of(text, video, cardW, thumbH, G.ThumbCorners, text.DurationMs > 0 ? lane.Clock(text.DurationMs) : null,
                    next ? G.NextRing : G.Ring, next ? accent : Ink.Stroke),
                new BoxEl
                {
                    Direction = 1, Gap = G.LineGap, MinWidth = 0f, HitTestVisible = false,
                    Children =
                    [
                        new TextEl(text.Title)
                        {
                            Size = G.TitleSize, LineHeight = G.TitleLine, Color = Ink.Ink, MaxLines = 1, Wrap = TextWrap.NoWrap,
                            Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
                        },
                        new TextEl(text.Artists)
                        {
                            Size = G.MetaSize, LineHeight = G.MetaLine, Color = Ink.InkSecondary, MaxLines = 1, Wrap = TextWrap.NoWrap,
                            Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
                        },
                    ],
                },
            ],
        };
        return lane.Attach(el, itemId, index, r);
    }
}
