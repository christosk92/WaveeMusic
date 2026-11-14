// ── Themes/WinUi/Structure/Views/Stage/WinUiStageAudioFace.cs — WinUI's audio face in the stage's Video mode ──────────────────────────────
//
// While video stays on and the queue reaches a track without a video, the full screen keeps its frame (the top bar with the Video tab disabled,
// Cinema's shelf or Theatre's pane, the command bar) and the picture's place holds the track's cover and its synced lyrics
// (`StageRootView.ShowAudioFace`). The next track with a video brings the picture back by itself.
//
// THE FIELD. The frame stays exactly where the picture is and keeps its shape (Theatre's 16:9 box, Cinema's whole stage) and is filled with the track's
// own colour: WinUI's is MICA-like, a still diagonal wash of the cover's colours veiled toward the theme's floor until the theme arm's lyric inks read
// on it (`WinUiStageAudioFaceGeometry.FieldTones`): pale on the light theme, deep on the dark, so the face, the top bar, the shelf and the lyrics all
// read in the theme's own inks. One opaque fill per track, keyed by its tones (a track change cross-fades it), never animated per frame.
//
//   Cinema   the field edge to edge; the cover (r8, the dialog's shadow) with the title (Title 28/36) and "Artist · Album" under it on the left, the
//            lyrics beside it, down to the shelf's place.
//   Theatre  inside the picture's box (its title block stays under the box): the cover at the box's left and the lyrics beside it at the pane's type,
//            or the cover alone, centred, beside Theatre's own Lyrics pane (one reading column, never two).
//
// THE MOUNT. The field, the cover and the title on the change's frame; the reading column a frame later (`_column`), its rows when its document lands.
// No presenter is up while an audio-only track plays, so no hole erases this layer: it is an ordinary stage layer over the reservation. Every part
// moves on the reservation's own spring between Cinema and Theatre.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;

using B = Wavee.Themes.WinUi.WinUiStageVideoBarGeometry;
using G = Wavee.Themes.WinUi.WinUiStageAudioFaceGeometry;
using Ink = Wavee.Theming.Roles.StageInk;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's audio face: the cover and the lyrics in the picture's place while an audio-only track plays in Video mode.</summary>
internal sealed class WinUiStageAudioFace : Component
{
    public sealed record Props(StageRootView View);

    /// <summary>The parts of the face in stage DIP: the plate (the picture's place), the cover, the title column (Cinema) and the lyrics column.</summary>
    public readonly record struct Rects(RectF Plate, RectF Cover, RectF Title, RectF Lyrics);

    public static Rects Of(StageRootView v, Stage.Layout L, WinUiStageLayout t, bool theatre, bool lyrics)
    {
        var plate = WinUiStageTheatre.PictureRect(L, t, theatre);
        if (theatre)
        {
            float barH = B.BarH(B.Inset(t.BarInset, true));
            float insetX = MathF.Round(plate.W * G.TheatreInsetX), insetY = MathF.Round(plate.H * G.TheatreInsetY);
            float roomH = MathF.Max(0f, plate.H - barH - insetY);
            float gap = MathF.Round(plate.W * G.TheatreLyricsGap), right = MathF.Round(plate.W * G.TheatreLyricsRight);
            float cover = MathF.Round(MathF.Max(0f, MathF.Min(MathF.Min(plate.H * G.TheatreCoverH, roomH), (plate.W - insetX - gap - right) * G.TheatreCoverMaxW)));
            if (!lyrics)
                return new Rects(plate, new RectF(plate.X + MathF.Round((plate.W - cover) * 0.5f), plate.Y + insetY + MathF.Round((roomH - cover) * 0.5f), cover, cover), default, default);
            float lx = plate.X + insetX + cover + gap;
            float ly = plate.Y + MathF.Round(plate.H * G.TheatreLyricsTop);
            return new Rects(plate, new RectF(plate.X + insetX, plate.Y + insetY, cover, cover), default,
                new RectF(lx, ly, MathF.Max(0f, plate.X + plate.W - right - lx), MathF.Max(0f, plate.Y + plate.H - barH - ly)));
        }
        float top = t.BarH + G.TopUnderBar;
        float bottom = WinUiStagePictureChrome.LowerBlockTop(L, t, v.Layers.UpNextShelf is not null) - G.BottomGap;
        float regionH = MathF.Max(0f, bottom - top);
        float titleH = G.TitleGap + G.TitleMaxLines * G.TitleLine + G.MetaGap + G.MetaLine;
        if (L.Aspect == Stage.Aspect.Portrait)
        {
            // a column: the cover and its title over the lyrics, at the stage's margin (beside each other the lyrics would break word by word)
            float px = L.PadX, pw = MathF.Max(0f, L.W - 2f * px);
            float side = Stage.Layout.Q4(MathF.Max(0f, MathF.Min(pw, regionH * G.PortraitCoverShare)));
            float ly = top + side + titleH + G.PortraitLyricsGap;
            return new Rects(plate, new RectF(px, top, side, side), new RectF(px, top + side + G.TitleGap, pw, titleH - G.TitleGap),
                lyrics ? new RectF(px, ly, pw, MathF.Max(0f, bottom - ly)) : default);
        }
        float size = Stage.Layout.Q4(Math.Clamp(MathF.Min(L.HeroArt, regionH - titleH), MathF.Min(G.CoverMin, regionH), Stage.Layout.HeroMax));
        float x = L.PadX;
        float lyricsX = x + size + Stage.Layout.Q4(G.LyricsGapFrac * L.W);
        return new Rects(plate, new RectF(x, top, size, size), new RectF(x, top + size + G.TitleGap, size, titleH - G.TitleGap),
            lyrics ? new RectF(lyricsX, top, MathF.Max(0f, L.W - lyricsX - L.PaneRight), regionH) : default);
    }

    readonly Signal<bool> _column = new(false);
    Action? _armColumn;

    public override Element Render()
    {
        var p = UseProps<Props>();
        var v = p.View;
        var t = WinUiStageTheatre.Tier(UseContext(ThemeFactsContext.Signal));
        var sc = v.Stage;
        var L = sc.Layout.Value;
        bool theatre = v.ShowTheatre();
        bool lyrics = sc.AudioLyrics.Value;
        var r = Of(v, L, t, theatre, lyrics);
        var lr = lyrics ? r.Lyrics : Of(v, L, t, theatre, true).Lyrics;
        var track = sc.RowValue();
        string url = track.IsValid ? Controls.ArtUrl(track.ImageId) ?? "" : "";
        float corners = Radii.Card;
        int decode = r.Cover.W > G.CoverDecodeSmallW ? G.CoverDecodeLarge : G.CoverDecodeSmall;
        var identity = v.Identity;
        var fade = new EnterExit(Opacity: 0f, Active: true);
        var move = WinUiStageTheatre.Move;
        var post = UsePost();
        _armColumn ??= () => _column.Value = true;
        UseEffect(() => post(_armColumn), DepKey.Empty);
        return Stage.Layer with
        {
            Children =
            [
                Embed.Comp(new Props(v), static () => new Field()),
                new BoxEl
                {
                    Key = "stage:audioface:cover", AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, HitTestVisible = false,
                    Margin = new Edges4(r.Cover.X, r.Cover.Y, 0f, 0f), Width = r.Cover.W, Height = r.Cover.H,
                    Corners = CornerRadius4.All(corners), Shadow = Elevation.Dialog, ClipToBounds = true, Layout = move,
                    Children = [Stage.CoverSwap(url, r.Cover.W, corners, decode)],
                },
                Flow.Show(() => !v.ShowTheatre(), Stage.Layer with
                {
                    Key = "stage:audioface:title", HitTestVisible = false, HitTestPassThrough = false,
                    Enter = fade, Exit = fade, Transition = MotionTok.StandardEnter,
                    Children =
                    [
                        new BoxEl
                        {
                            AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Direction = 1, Gap = G.MetaGap, HitTestVisible = false,
                            Margin = new Edges4(r.Title.X, r.Title.Y, 0f, 0f), Width = MathF.Max(r.Title.W, r.Cover.W), Layout = move,
                            Children =
                            [
                                new TextEl("")
                                {
                                    Text = Prop.Of(identity.Title), FontFamily = WinUiStageLayerGeometry.DisplayFace, Size = G.TitleSize, LineHeight = G.TitleLine,
                                    Weight = G.TitleWeight, Color = Ink.Ink, Wrap = TextWrap.Wrap, MaxLines = G.TitleMaxLines, Trim = TextTrim.CharacterEllipsis,
                                    MinWidth = 0f, Shrink = 1f,
                                },
                                new TextEl("")
                                {
                                    Text = Prop.Of(() => identity.Meta(0).Line), Size = G.MetaSize, LineHeight = G.MetaLine, Color = Ink.InkSecondary,
                                    MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
                                },
                            ],
                        },
                    ],
                }),
                // the synced lyrics beside the cover (not beside Theatre's own Lyrics pane), from the mount's second frame, in the theme's inks
                Flow.Show(() => _column.Value && sc.AudioLyrics.Value && v.Layers.PictureLyrics is not null, Stage.Layer with
                {
                    Key = "stage:audioface:lyrics",
                    Enter = fade, Exit = fade, Transition = MotionTok.StandardEnter,
                    Children =
                    [
                        new BoxEl
                        {
                            AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Direction = 1, ClipToBounds = true,
                            Margin = new Edges4(lr.X, lr.Y, 0f, 0f), Width = lr.W, Height = lr.H, Layout = move,
                            Children = [new BoxEl { Direction = 1, Grow = 1f, MinHeight = 0f, Children = [v.Layers.PictureLyrics is { } build ? build() : new BoxEl()] }],
                        },
                    ],
                }),
            ],
        };
    }

    /// <summary>The field in the picture's place: a still box at the picture's rect moving with the reservation; the wash inside it keyed by its tones,
    /// so the old track's field fades out under the new one. Its own component: a track change or a Cinema ⇄ Theatre change re-renders it alone.</summary>
    sealed class Field : Component
    {
        static readonly EnterExit s_fade = new(Opacity: 0f, Active: true);

        public override Element Render()
        {
            var v = UseProps<Props>().View;
            var t = WinUiStageTheatre.Tier(UseContext(ThemeFactsContext.Signal));
            var st = v.Stage;
            var plate = WinUiStageTheatre.PictureRect(st.Layout.Value, t, v.ShowTheatre());
            var (light, mid, deep) = G.FieldTones(st.BasePalette.Value, Tok.Theme);
            var field = new GradientSpec(GradientShape.Linear, G.FieldAngle,
                [new GradientStop(0f, light), new GradientStop(G.FieldMidAt, mid), new GradientStop(1f, deep)]);
            return Stage.Layer with
            {
                HitTestVisible = false, HitTestPassThrough = false,
                Children =
                [
                    new BoxEl
                    {
                        Key = "stage:audioface:plate", AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, HitTestVisible = false, ZStack = true,
                        Margin = new Edges4(plate.X, plate.Y, 0f, 0f), Width = plate.W, Height = plate.H, ClipToBounds = true, Layout = WinUiStageTheatre.Move,
                        Children =
                        [
                            new BoxEl
                            {
                                Key = "stage:audioface:field:" + HashCode.Combine(light, mid, deep), AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch,
                                HitTestVisible = false, Gradient = field, Enter = s_fade, Exit = s_fade, Transition = MotionTok.StandardEnter,
                            },
                        ],
                    },
                ],
            };
        }
    }
}
