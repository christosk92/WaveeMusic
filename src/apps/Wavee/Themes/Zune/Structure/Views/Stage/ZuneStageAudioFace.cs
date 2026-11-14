// ── Themes/Zune/Structure/Views/Stage/ZuneStageAudioFace.cs — Zune's audio face in the stage's Video mode ──────────────────────────────────
//
// While video stays on and the queue reaches a track without a video, the full screen keeps its frame (the head with "video" disabled, the
// strip or Theatre's list, the plate) and the picture's place holds the track itself (`StageRootView.ShowAudioFace`), in Zune's flat colour:
//
//   THE FIELD   ONE flat fill of the cover's colour in the picture's exact place and shape (Cinema: the whole stage; Theatre: the 16:9 box),
//               veiled toward the THEME arm's floor until the lyrics' other lines read on it (`ZuneStageVideoGeometry.Field`): a light field on
//               the light theme, a deep one on the dark, so the head, the strip, the plate and the face share one ink. Keyed by the colour, so a
//               track change cross-fades it.
//   CINEMA      the cover (the audio stage's hero size) at the gutter under the head, the accent artist line and the light display title (two
//               lines) under it, the synced lyrics beside it down to the strip's place.
//   THEATRE     inside the box: the cover on its left and the lyrics beside it, or the cover alone, centred, beside Theatre's own lyrics list.
//
// No presenter is up while an audio-only track plays, so no hole erases this layer: an ordinary stage layer over the reservation. The reading
// column mounts a frame after the face (a staged mount: the field, the cover and the title first). Every part moves on the reservation's spring.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;

using G = Wavee.Themes.Zune.ZuneStageVideoGeometry;
using Ink = Wavee.Theming.Roles.StageInk;

namespace Wavee.Themes.Zune;

/// <summary>Zune's audio face: the flat field, the cover and the lyrics in the picture's place while an audio-only track plays in Video mode.</summary>
internal sealed class ZuneStageAudioFace : Component
{
    public sealed record Props(StageRootView View);

    /// <summary>The face's parts in stage DIP: the plate (the picture's place), the cover, the title block (Cinema only) and the lyrics column.</summary>
    public readonly record struct Rects(RectF Plate, RectF Cover, RectF Title, RectF Lyrics);

    public static Rects Of(StageRootView v, in Stage.Layout L, ZuneStageLayout t, bool theatre, bool lyrics)
    {
        var plate = G.Picture(in L, t, theatre);
        var z = t.Video;
        if (theatre)
        {
            float insetX = MathF.Round(plate.W * G.FaceTheatreInsetX), insetY = MathF.Round(plate.H * G.FaceTheatreInsetY);
            // clear of the plate at the box's foot and the dense half of the fade above it (at rest over the face)
            float foot = G.BarH(t) + G.PlateFadeH * G.FaceFadeShare;
            float roomH = MathF.Max(0f, plate.H - foot - insetY);
            float gap = MathF.Round(plate.W * G.FaceTheatreGap), right = MathF.Round(plate.W * G.FaceTheatreRight);
            float cover = MathF.Round(MathF.Max(0f, MathF.Min(MathF.Min(plate.H * G.FaceTheatreCoverH, roomH), (plate.W - insetX - gap - right) * G.FaceTheatreCoverMaxW)));
            if (!lyrics)
                return new Rects(plate, new RectF(plate.X + MathF.Round((plate.W - cover) * 0.5f), plate.Y + insetY + MathF.Round((roomH - cover) * 0.5f), cover, cover), default, default);
            float lx = plate.X + insetX + cover + gap;
            float ly = plate.Y + MathF.Round(plate.H * G.FaceTheatreLyricsTop);
            return new Rects(plate, new RectF(plate.X + insetX, plate.Y + insetY, cover, cover), default,
                new RectF(lx, ly, MathF.Max(0f, plate.X + plate.W - right - lx), MathF.Max(0f, plate.Y + plate.H - foot - ly)));
        }
        float top = t.HeadH + z.Px(G.FaceTop);
        float bottom = G.LowerBlockTop(in L, t, shelf: z.ShelfCardW > 0f && v.Layers.UpNextShelf is not null) - z.Px(G.FaceBottomGap);
        float regionH = MathF.Max(0f, bottom - top);
        float titleH = z.Px(G.FaceTitleGap) + z.Dx(G.ArtistLine) + G.FaceTitleLines * z.Dx(G.FaceTitleLine);
        float size = Stage.Layout.Q4(Math.Clamp(MathF.Min(L.HeroArt, regionH - titleH), MathF.Min(G.FaceCoverMin, regionH), Stage.Layout.HeroMax));
        float x = t.Gutter;
        float lyricsX = x + size + Stage.Layout.Q4(G.FaceLyricsGapFrac * L.W);
        return new Rects(plate, new RectF(x, top, size, size), new RectF(x, top + size + z.Px(G.FaceTitleGap), size, titleH - z.Px(G.FaceTitleGap)),
            lyrics ? new RectF(lyricsX, top, MathF.Max(0f, L.W - lyricsX - t.Gutter), regionH) : default);
    }

    readonly Signal<bool> _column = new(false);
    Action? _armColumn;

    public override Element Render()
    {
        var p = UseProps<Props>();
        var v = p.View;
        var t = ZuneStageTiers.StageOf(TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal)).Value.Tiers);
        var z = t.Video;
        var sc = v.Stage;
        var L = sc.Layout.Value;
        bool theatre = v.ShowTheatre();
        bool lyrics = sc.AudioLyrics.Value;
        var r = Of(v, in L, t, theatre, lyrics);
        var lr = lyrics ? r.Lyrics : Of(v, in L, t, theatre, true).Lyrics;
        var track = sc.RowValue();
        string url = track.IsValid ? Controls.ArtUrl(track.ImageId) ?? "" : "";
        float corners = R.Shape.Cover;
        int decode = r.Cover.W > G.FaceDecodeSmallW ? ZuneStageGeometry.CoverDecodeLarge : ZuneStageGeometry.CoverDecodeSmall;
        var identity = v.Identity;
        // the field stands on the THEME's arm (Zune's light theme is a light, flat field, its dark one a deep one), so the head, the strip, the plate and
        // the face read in one ink; the field is veiled toward that arm's floor until the lyrics read on it
        var fieldArm = Tok.Theme;
        var arm = Ink.Arm(fieldArm);
        var accent = sc.Accent.Value.Text;
        var fade = new EnterExit(Opacity: 0f, Active: true);
        var move = ZuneStageTheatre.Move;
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
                    Corners = CornerRadius4.All(corners), ClipToBounds = true, Layout = move,
                    Children = [Stage.CoverSwap(url, r.Cover.W, corners, decode)],
                },
                // Cinema's title under the cover (Theatre's is the chrome's, under the box)
                Flow.Show(() => !v.ShowTheatre(), Stage.Layer with
                {
                    Key = "stage:audioface:title", HitTestVisible = false, HitTestPassThrough = false,
                    Enter = fade, Exit = fade, Transition = MotionTok.StandardEnter,
                    Children =
                    [
                        new BoxEl
                        {
                            AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Direction = 1, HitTestVisible = false,
                            Margin = new Edges4(r.Title.X, r.Title.Y, 0f, 0f), Width = MathF.Max(r.Title.W, r.Cover.W), Layout = move,
                            Children =
                            [
                                ZuneStageTheatre.ArtistLine(identity, z, accent),
                                new TextEl("")
                                {
                                    Text = Prop.Of(identity.Title), Size = z.Dx(G.FaceTitleSize), LineHeight = z.Dx(G.FaceTitleLine), Weight = G.TitleWeight, FontFamily = R.Type.DisplayFace,
                                    CharSpacing = G.TitleTracking, Color = arm.Ink, Wrap = TextWrap.Wrap, MaxLines = G.FaceTitleLines, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
                                },
                            ],
                        },
                    ],
                }),
                // the synced lyrics beside the cover (not beside Theatre's own lyrics list), from the mount's second frame
                Flow.Show(() => _column.Value && sc.AudioLyrics.Value && v.Layers.PictureLyricsOn is not null, Stage.Layer with
                {
                    Key = "stage:audioface:lyrics",
                    Enter = fade, Exit = fade, Transition = MotionTok.StandardEnter,
                    Children =
                    [
                        new BoxEl
                        {
                            AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Direction = 1, ClipToBounds = true,
                            Margin = new Edges4(lr.X, lr.Y, 0f, 0f), Width = lr.W, Height = lr.H, Layout = move,
                            Children =
                            [
                                new BoxEl
                                {
                                    Key = "stage:audioface:lyrics:" + (int)fieldArm, Direction = 1, Grow = 1f, MinHeight = 0f,
                                    Children = [v.Layers.PictureLyricsOn is { } build ? build(fieldArm) : new BoxEl()],
                                },
                            ],
                        },
                    ],
                }),
            ],
        };
    }

    /// <summary>The flat field in the picture's place: its own component (a palette or a Cinema ⇄ Theatre change re-renders this box alone).</summary>
    sealed class Field : Component
    {
        static readonly EnterExit s_fade = new(Opacity: 0f, Active: true);

        public override Element Render()
        {
            var v = UseProps<Props>().View;
            var t = ZuneStageTiers.StageOf(TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal)).Value.Tiers);
            var st = v.Stage;
            var plate = G.Picture(st.Layout.Value, t, v.ShowTheatre());
            var palette = st.BasePalette.Value;
            var colour = G.Field(palette, Tok.Theme);
            return Stage.Layer with
            {
                HitTestVisible = false, HitTestPassThrough = false,
                Children =
                [
                    new BoxEl
                    {
                        Key = "stage:audioface:plate", AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, HitTestVisible = false, ZStack = true,
                        Margin = new Edges4(plate.X, plate.Y, 0f, 0f), Width = plate.W, Height = plate.H, ClipToBounds = true, Layout = ZuneStageTheatre.Move,
                        Children =
                        [
                            new BoxEl
                            {
                                Key = "stage:audioface:field:" + colour.GetHashCode(), AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch,
                                HitTestVisible = false, Fill = colour, Enter = s_fade, Exit = s_fade, Transition = MotionTok.StandardEnter,
                            },
                        ],
                    },
                ],
            };
        }
    }
}
