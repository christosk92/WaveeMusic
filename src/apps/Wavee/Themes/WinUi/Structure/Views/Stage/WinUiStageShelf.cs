// ── Themes/WinUi/Structure/Views/Stage/WinUiStageShelf.cs — WinUI's `Queue.StageShelf` and the stage's 16:9 thumbnail ───────────────────────
//
// THE SHELF (Video mode, Cinema): "Up next" (BodyStrong) over a row of the next tracks as W-Video's cards: no plate; a video's card IS its 16:9 still
// (r8), an audio-only track's IS its square cover at the same height, so the shape tells the two kinds apart; the duration is a Caption badge on the
// art's bottom-right corner (r4, on the media scrim); the title (BodyStrong) and the artist (Caption) sit under the art at its width; the first card,
// what plays next, keeps the accent ring (WinUI's selected state). The shape comes from the queue's video fact (`IQueueLane.VideoOf`), never from an
// image arriving, so a still that lands after its cover moves nothing. A click plays that track, a right-click opens the row's menu; as many cards as
// the stage's width seats, never a scrollbar. The picture chrome places it above the command bar under the idle chrome's Show; its place (and the
// title block's above it) is reserved whether it shows or rests. While the END CARD is up the card names what plays next, so the shelf is "Then":
// the tracks after the card's, none ringed.
//
// THE THUMBNAIL (the end card's): the video's still full-bleed, or the audio-only cover inset on a plate of the same 16:9 size, tagged "Video" / "Audio".

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

using G = Wavee.Themes.WinUi.WinUiStageShelfGeometry;
using Ink = Wavee.Theming.Roles.StageInk;
using T = Wavee.Themes.WinUi.WinUiStageThumbGeometry;

namespace Wavee.Themes.WinUi;

/// <summary>The stage's 16:9 thumbnail in Video mode: the video's still, or the audio-only track's cover inset on a plate, tagged.</summary>
internal static class WinUiStageThumb
{
    /// <summary>A <paramref name="w"/> x <paramref name="h"/> thumbnail with its kind tag and an optional accent ring (<paramref name="ringW"/> 0: none).</summary>
    public static Element Of(in QueueRowText text, in QueueRowVideo video, float w, float h, float ringW = 0f, ColorF ring = default)
    {
        var kids = new List<Element>(4);
        if (video.HasVideo)
            kids.Add(Controls.Artwork(video.Still, w, h, Radii.Card, decodePx: (int)MathF.Round(w * 2f)));
        else
        {
            float cover = MathF.Round(h * T.CoverShare);
            kids.Add(new BoxEl
            {
                AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Center, Width = cover, Height = cover, HitTestVisible = false,
                Children = [Controls.Artwork(text.Art, cover, cover, Radii.Control, decodePx: (int)MathF.Round(cover * 2f))],
            });
        }
        kids.Add(Tag(Loc.Get(video.HasVideo ? Strings.Stage.Mode.Video : Strings.Stage.AudioTag)));
        if (ringW > 0f)
            kids.Add(new BoxEl
            {
                AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
                Corners = Radii.CardAll, BorderWidth = ringW, BorderColor = ring,
            });
        return new BoxEl
        {
            Width = w, Height = h, Shrink = 0f, ZStack = true, ClipToBounds = true, Corners = Radii.CardAll,
            Fill = Ink.Plate, HitTestVisible = false,
            Children = kids.ToArray(),
        };
    }

    /// <summary>The kind tag, top-left on the media scrim (a Start layer fills its slot: the tag sits content-sized in a full-bleed row).</summary>
    static Element Tag(string text) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Start, Padding = new Edges4(T.TagInset, T.TagInset, 0f, 0f), HitTestVisible = false,
        Children =
        [
            new BoxEl
            {
                HitTestVisible = false, Padding = new Edges4(T.TagPadX, T.TagPadY, T.TagPadX, T.TagPadY), Corners = Radii.ControlAll,
                Fill = R.OnMedia.CoverScrim with { A = T.TagPlateA },
                Children = [new TextEl(text) { Size = T.TagSize, LineHeight = T.TagLine, Weight = T.TagWeight, Color = R.OnMedia.Ink, MaxLines = 1, Wrap = TextWrap.NoWrap }],
            },
        ],
    };
}

/// <summary>WinUI's Up next shelf over the music video (<see cref="ViewKeys.QueueStageShelf"/>).</summary>
internal sealed class WinUiStageShelf : Component
{
    /// <summary>The registered template of <see cref="ViewKeys.QueueStageShelf"/>.</summary>
    public static Element Template(QueueStageShelfProps props, in ViewContext ctx)
        => Embed.Comp(new Props(props, WinUiStageTiers.StageOf(ctx.Tier())), static () => new WinUiStageShelf());

    sealed record Props(QueueStageShelfProps Shelf, WinUiStageLayout Tier);

    /// <summary>The cards' slide when the width (a tier crossing) moves them.</summary>
    static readonly LayoutTransition s_move = new(TransitionChannels.Bounds,
        TransitionDynamics.Spring(WinUiStageGeometry.PaneMoveResponse, WinUiStageGeometry.PaneMoveDamping), SizeMode.Reveal);

    static readonly EnterExit s_rowFade = new(Opacity: 0f, Active: true);

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
        float width = WinUiStagePictureChrome.ShelfWidth(L, tier);
        var queue = props.Queue;
        bool then = st.EndCard.Value;
        int skip = then ? 1 : 0;
        var lane = props.Lane;
        var verbs = props.Verbs;
        var overControls = st.OverControls;
        // while a track with a video plays as its video (`VideosPlay`, contract 1.105) a video track's card is its 16:9 still, else every card is the
        // square cover; the film glyph after the title marks a track with a video either way
        bool videosPlay = st.VideosPlay.Value;
        int rows = Math.Max(0, Math.Min(G.MaxCards, queue.Rows.Length - skip));
        Span<float> widths = stackalloc float[G.MaxCards];
        Span<bool> videos = stackalloc bool[G.MaxCards];
        Span<bool> stills = stackalloc bool[G.MaxCards];
        for (int i = 0; i < rows; i++)
        {
            videos[i] = lane.VideoOf(queue.Rows[i + skip].Ref).HasVideo;
            stills[i] = videos[i] && videosPlay;
            widths[i] = G.ArtW(cardW, stills[i]);
        }
        var (n, scale) = G.Seat(width, widths[..rows], _held);
        _held = n;
        float artH = MathF.Round(G.ThumbH(cardW) * scale);
        var cards = new Element[n];
        for (int i = 0; i < n; i++)
            cards[i] = Card(lane, verbs, queue.Rows[i + skip], MathF.Round(widths[i] * scale), artH, videos[i], stills[i], i == 0 && !then, set.Fill, overControls);
        return new BoxEl
        {
            Direction = 1, Gap = G.CaptionGap, Width = width, Height = G.Height(cardW),
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

    /// <summary>One card, plateless: the art with its duration badge and, for what plays next, the accent ring, over the title and the artist.</summary>
    static Element Card(IQueueLane lane, IQueueVerbs verbs, QueueRowProps row, float artW, float artH, bool video, bool still, bool next, ColorF accent, Action<bool> overControls)
    {
        var text = lane.TextOf(row.Ref);
        string? url = still ? lane.VideoOf(row.Ref).Still : text.Art;
        var titleRow = new List<Element>(2)
        {
            new TextEl(text.Title)
            {
                Size = G.TitleSize, LineHeight = G.TitleLine, Weight = G.TitleWeight, Color = Ink.Ink, MaxLines = 1, Wrap = TextWrap.NoWrap,
                Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
            },
        };
        if (video) titleRow.Add(new TextEl(Icons.Movie) { Size = WinUiListRules.FilmGlyph, FontFamily = Tok.Definition.Fonts.Icon, Color = Ink.InkTertiary, Shrink = 0f });
        ulong itemId = row.ItemId;
        int index = row.Index;
        EntityRef r = row.Ref;
        var art = new List<Element>(3) { Controls.Artwork(url, artW, artH, Radii.Card, decodePx: (int)MathF.Round(artW * 2f)) };
        if (text.DurationMs > 0) art.Add(Badge(lane.Clock(text.DurationMs)));
        if (next)
            art.Add(new BoxEl
            {
                AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
                Corners = Radii.CardAll, BorderWidth = G.NextRing, BorderColor = accent,
            });
        var el = new BoxEl
        {
            Key = lane.RowKey("shelf", itemId, index),
            Width = artW, Shrink = 0f, Direction = 1, Gap = G.TextGap,
            Corners = Radii.CardAll, Fill = ColorF.Transparent, HoverFill = ColorF.Transparent, PressedFill = ColorF.Transparent,
            PressScale = R.Motion.ScaleSubtle.Press,
            Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true, AllowFocusOnInteraction = false,
            OnClick = () => verbs.Play(itemId, index, r),
            OnHoverMove = _ => overControls(true), OnPointerExit = () => overControls(false),
            Enter = s_rowFade, Exit = s_rowFade, Transition = MotionTok.ControlNormal, Layout = s_move,
            Children =
            [
                new BoxEl
                {
                    Width = artW, Height = artH, Shrink = 0f, ZStack = true, ClipToBounds = true, Corners = Radii.CardAll, HitTestVisible = false,
                    Children = art.ToArray(),
                },
                new BoxEl
                {
                    Direction = 1, MinWidth = 0f, HitTestVisible = false,
                    Children =
                    [
                        new BoxEl { Height = G.TitleLine, Direction = 0, AlignItems = FlexAlign.Center, Gap = G.FilmGap, MinWidth = 0f, Children = titleRow.ToArray() },
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

    /// <summary>The duration badge on the art's bottom-right corner: Caption on the media scrim (the art is media in both arms), r4.</summary>
    static Element Badge(string duration) => new BoxEl
    {
        AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.End, Margin = new Edges4(0f, 0f, G.BadgeInset, G.BadgeInset), HitTestVisible = false,
        Padding = new Edges4(G.BadgePadX, G.BadgePadY, G.BadgePadX, G.BadgePadY), Corners = Radii.ControlAll,
        Fill = R.OnMedia.CoverScrim with { A = G.BadgeA },
        Children = [new TextEl(duration) { Size = G.BadgeSize, LineHeight = G.BadgeLine, Color = R.OnMedia.Ink, MaxLines = 1, Wrap = TextWrap.NoWrap }],
    };
}
