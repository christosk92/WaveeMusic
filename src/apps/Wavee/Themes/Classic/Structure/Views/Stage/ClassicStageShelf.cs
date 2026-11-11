// ── Themes/Classic/Structure/Views/Stage/ClassicStageShelf.cs — Classic's `Queue.StageShelf` and the stage's 16:9 thumbnail ─────────────
//
// THE THUMBNAIL (Video mode, the boards' rules note item 6): while the stage shows the music video, every queued track is told apart at a glance.
// A track with a video shows the video's still full-bleed; an audio-only track shows its square cover inset, centred, on a dark plate of the same
// 16:9 size; each carries a small tag ("Video" / "Audio"). The Theatre list's rows (`ClassicQueueStagePane`) use it.
//
// THE SHELF (the board "Classic · Cinema", its cards the board "E · Shelf", strip "1 · No frame"): "Up next" over a row of the next tracks' cards. A
// card has NO frame: a video's card IS its 16:9 still, an audio-only track's IS its square cover at the same height, so the two kinds have two widths
// and the shape tells them apart (no "Video" / "Audio" tag); the duration is a small badge on the art's bottom-right corner, the title and the artist
// line sit under the art at the card's own width, and the first card, what plays next, keeps the accent ring. The shape comes from the queue's video
// fact (`IQueueLane.VideoOf`), never from an image arriving, so a still that lands after its cover moves nothing. A click plays that track (the
// queue's own verb), a right-click opens the row's menu. As many cards as the stage's width seats (`ClassicStageShelfGeometry.Seat`), never a
// scrollbar. The picture chrome (`ClassicStagePictureChrome`) places it above the control bar under the idle chrome's Show; its place (and the title
// block's above it) is reserved whether it shows or rests. While the END CARD is up (`StageCtx.EndCard`) the card names what plays next, so the shelf
// is the board's "Then" row: the tracks after the card's, none ringed.

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
    /// <summary>A <paramref name="w"/> x <paramref name="h"/> thumbnail for one row, with its kind tag and an optional ring (<paramref name="ringW"/>
    /// 0: none).</summary>
    public static Element Of(in QueueRowText text, in QueueRowVideo video, float w, float h, float corners, float ringW = 0f, ColorF ring = default)
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
        kids.Add(Tag(Loc.Get(video.HasVideo ? Strings.Stage.Mode.Video : Strings.Stage.AudioTag), small));
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

    /// <summary>The kind tag, top-left on the veil's plate. A ZStack layer placed at Start FILLS its slot (the engine's legacy top-left origin; only
    /// Center/End take the desired size), so the tag's plate would veil the whole thumbnail: the tag sits content-sized in a full-bleed row that
    /// starts it at the inset.</summary>
    static Element Tag(string text, bool small)
    {
        float inset = small ? T.SmallTagInset : T.TagInset;
        float padX = small ? T.SmallTagPadX : T.TagPadX;
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Start, Padding = new Edges4(inset, inset, 0f, 0f), HitTestVisible = false,
            Children =
            [
                new BoxEl
                {
                    HitTestVisible = false,
                    Padding = new Edges4(padX, T.TagPadY, padX, T.TagPadY), Corners = CornerRadius4.All(T.TagCorners),
                    Fill = Ink.Veil with { A = T.TagPlateA },
                    Children =
                    [
                        new TextEl(text)
                        {
                            Size = small ? T.SmallTagSize : T.TagSize, LineHeight = small ? T.SmallTagLine : T.TagLine,
                            Weight = T.TagWeight, Color = Ink.InkSecondary, MaxLines = 1, Wrap = TextWrap.NoWrap,
                        },
                    ],
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
        float width = ClassicStagePictureChrome.ShelfWidth(L);
        var queue = props.Queue;
        // the end card shows the first row: the shelf starts after it ("Then")
        bool then = st.EndCard.Value;
        int skip = then ? 1 : 0;
        var lane = props.Lane;
        var verbs = props.Verbs;
        var overControls = st.OverControls;
        // the inks' arm: over the AUDIO FACE the colour field decides it (the face's rule: a deep field on the light theme reads with the dark arm's
        // light ink), over a picture the theme kind does
        var arm = Ink.Arm(st.AudioFace.Value ? ClassicStageAudioFaceGeometry.FieldArm(st.BasePalette.Value) : Tok.Theme);
        // each card's art width from the row's video fact (a video's 16:9 still, an audio-only track's square cover), then the cards the width
        // seats; the count holds while the cards can narrow to keep it (a crossing's narrowing frames never drop a card to bring it back)
        int rows = Math.Max(0, Math.Min(G.MaxCards, queue.Rows.Length - skip));
        Span<float> widths = stackalloc float[G.MaxCards];
        Span<bool> videos = stackalloc bool[G.MaxCards];
        for (int i = 0; i < rows; i++)
        {
            videos[i] = lane.VideoOf(queue.Rows[i + skip].Ref).HasVideo;
            widths[i] = G.ArtW(cardW, videos[i]);
        }
        var (n, scale) = G.Seat(width, widths[..rows], _held);
        _held = n;
        float artH = MathF.Round(G.ThumbH(cardW) * scale);
        var cards = new Element[n];
        for (int i = 0; i < n; i++)
            cards[i] = Card(lane, verbs, queue.Rows[i + skip], MathF.Round(widths[i] * scale), artH, videos[i], i == 0 && !then, set.Fill, overControls, arm.Ink, arm.InkSecondary);
        return new BoxEl
        {
            Direction = 1, Gap = G.CaptionGap, Width = width, Height = G.Height(cardW),
            Children =
            [
                new TextEl(n > 0 ? Loc.Get(then ? Strings.Stage.EndCard.Then : Strings.Stage.UpNext) : "")
                {
                    Size = G.CaptionSize, LineHeight = G.CaptionLine, Weight = G.CaptionWeight, Color = arm.Ink, MaxLines = 1, Wrap = TextWrap.NoWrap,
                },
                new BoxEl { Direction = 0, Gap = G.CardGap, Children = cards },
            ],
        };
    }

    /// <summary>One card, frameless: the art (the video's 16:9 still, or the square cover) with its duration badge and, for what plays next, the accent
    /// ring, over the title and the artist line at the art's width; a click plays the row, a right-click opens its menu.</summary>
    static Element Card(IQueueLane lane, IQueueVerbs verbs, QueueRowProps row, float artW, float artH, bool video, bool next, ColorF accent, Action<bool> overControls,
        ColorF ink, ColorF inkSecondary)
    {
        var text = lane.TextOf(row.Ref);
        string? url = video ? lane.VideoOf(row.Ref).Still : text.Art;
        ulong itemId = row.ItemId;
        int index = row.Index;
        EntityRef r = row.Ref;
        var art = new List<Element>(3) { Controls.Artwork(url, artW, artH, Radii.Control, decodePx: (int)MathF.Round(artW * 2f)) };
        if (text.DurationMs > 0) art.Add(Badge(lane.Clock(text.DurationMs)));
        if (next)
            art.Add(new BoxEl
            {
                AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
                Corners = Radii.ControlAll, BorderWidth = G.NextRing, BorderColor = accent,
            });
        var el = new BoxEl
        {
            Key = lane.RowKey("shelf", itemId, index),
            Width = artW, Shrink = 0f, Direction = 1, Gap = G.TextGap,
            Corners = Radii.ControlAll, Fill = ColorF.Transparent, HoverFill = ColorF.Transparent, PressedFill = ColorF.Transparent,
            PressScale = R.Motion.ScaleSubtle.Press,
            Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true, AllowFocusOnInteraction = false,
            OnClick = () => verbs.Play(itemId, index, r),
            // the card holds the idle chrome while the pointer is on it (the card, never the gaps between cards or the shelf's box)
            OnHoverMove = _ => overControls(true), OnPointerExit = () => overControls(false),
            Enter = ClassicQueue.RowEnter, Exit = ClassicQueue.RowExit, Layout = s_move,
            Children =
            [
                new BoxEl
                {
                    Width = artW, Height = artH, Shrink = 0f, ZStack = true, ClipToBounds = true, Corners = Radii.ControlAll, HitTestVisible = false,
                    Children = art.ToArray(),
                },
                new BoxEl
                {
                    Direction = 1, Gap = G.LineGap, MinWidth = 0f, HitTestVisible = false,
                    Children =
                    [
                        new TextEl(text.Title)
                        {
                            Size = G.TitleSize, LineHeight = G.TitleLine, Color = ink, MaxLines = 1, Wrap = TextWrap.NoWrap,
                            Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
                        },
                        new TextEl(text.Artists)
                        {
                            Size = G.MetaSize, LineHeight = G.MetaLine, Color = inkSecondary, MaxLines = 1, Wrap = TextWrap.NoWrap,
                            Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
                        },
                    ],
                },
            ],
        };
        return lane.Attach(el, itemId, index, r);
    }

    /// <summary>The duration badge on the art's bottom-right corner: caption type on the on-media scrim (the art is media in both arms).</summary>
    static Element Badge(string duration) => new BoxEl
    {
        AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.End, Margin = new Edges4(0f, 0f, T.TagInset, T.TagInset), HitTestVisible = false,
        Padding = new Edges4(T.TagPadX, T.TagPadY, T.TagPadX, T.TagPadY), Corners = CornerRadius4.All(T.TagCorners),
        Fill = R.OnMedia.CoverScrim with { A = T.TagPlateA },
        Children = [new TextEl(duration) { Size = T.BadgeSize, LineHeight = T.BadgeLine, Color = R.OnMedia.Ink, MaxLines = 1, Wrap = TextWrap.NoWrap }],
    };
}
