// ── Themes/Classic/Structure/Views/Stage/ClassicStageAudioFace.cs — Classic's audio face in the stage's Video mode ──────────────────────────
//
// The board "Classic · An audio-only track plays", and the stage's OVERVIEW tab (contract 1.105): in Video mode without a picture (a track without
// a video, or video off) the full screen keeps its frame (the top bar with the first tab reading Overview, Cinema's Up next shelf or Theatre's
// panel, the control bar) and the picture's place holds the track's cover and its synced lyrics (`StageRootView.ShowAudioFace`). The next track
// with a video that plays brings the picture back by itself, and the tab reads Video again in place.
//
// THE COLOUR FIELD (the board "Audio player as a colour field", concept 1; the owner's "not a fan that the player thing is just a black slab"). The
// frame stays exactly where the picture is and keeps the picture's shape (Theatre's 16:9 box, square-cornered as the picture; Cinema's whole stage):
// an audio-only track fills it with its OWN colour instead of the letterbox black, a still field from the stage's palette (`StageCtx.BasePalette`;
// `ClassicStageAudioFaceGeometry.FieldTones`, clamped so the lyrics' inks read on it in both arms). One opaque radial fill per track, keyed by the
// palette so a track change cross-fades it, never animated per frame (`Field`, its own component).
//
//   Cinema   the field edge to edge; the cover (the audio stage's hero size, held above the shelf's place) with the title (Display 28/34, two lines)
//            and the "Artist · Album" line under it on the left, the lyrics beside it at the stage's type, down to the shelf's place.
//   Theatre  inside the picture's 16:9 box (its title block stays under the box, the chrome's): the cover at the box's left (the board's 63 % of its
//            height, the stage covers' shadow and control corners) and the lyrics in the column beside it at the Theatre panel's type, or the
//            cover alone, centred, beside Theatre's own Lyrics panel (one reading column, never two).
//
// THE MOUNT (a video → audio track change used to cost one 22 ms frame: the face, its lyrics host, the lyrics view and its rows in one go). The face
// mounts in stages: the field, the cover and the title on the change's frame; the reading column a frame later (`_column`, armed by a posted
// action), its rows when its document lands. The column fades in as it always did, so the stage is invisible.
//
// THE VIDEO HOLE. No presenter is up while an audio-only track plays (the placement resolves to nothing), so no hole erases this layer: it is an
// ordinary stage layer over the reservation (the root mounts it right after the picture's). When the picture comes back the presenter covers the box
// and this face fades out under it. Every part moves on the reservation's own spring (`ClassicStageTheatre.Move`) between Cinema and Theatre.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;

using G = Wavee.Themes.Classic.ClassicStageAudioFaceGeometry;
using Ink = Wavee.Theming.Roles.StageInk;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's audio face: the cover and the lyrics in the picture's place while an audio-only track plays in Video mode.</summary>
internal sealed class ClassicStageAudioFace : Component
{
    public sealed record Props(StageRootView View);

    /// <summary>The parts of the face, in stage DIP: the plate (the picture's place), the cover, the title column (Cinema only: empty in Theatre) and
    /// the lyrics column (empty where the face shows none).</summary>
    public readonly record struct Rects(RectF Plate, RectF Cover, RectF Title, RectF Lyrics);

    public static Rects Of(StageRootView v, Stage.Layout L, ClassicStageLayout tier, bool theatre, bool lyrics)
    {
        var plate = ClassicStageTheatre.PictureRect(L, theatre);
        if (theatre)
        {
            // the board's proportions inside the box, clear of the control bar at its foot (at rest over the face, ClassicStageVideoBar)
            float insetX = MathF.Round(plate.W * G.TheatreInsetX), insetY = MathF.Round(plate.H * G.TheatreInsetY);
            float roomH = MathF.Max(0f, plate.H - ClassicStageVideoBarGeometry.BarH - insetY);
            float gap = MathF.Round(plate.W * G.TheatreLyricsGap), right = MathF.Round(plate.W * G.TheatreLyricsRight);
            float cover = MathF.Round(MathF.Max(0f, MathF.Min(MathF.Min(plate.H * G.TheatreCoverH, roomH), (plate.W - insetX - gap - right) * G.TheatreCoverMaxW)));
            if (!lyrics)
                return new Rects(plate, new RectF(plate.X + MathF.Round((plate.W - cover) * 0.5f), plate.Y + insetY + MathF.Round((roomH - cover) * 0.5f), cover, cover), default, default);
            float lx = plate.X + insetX + cover + gap;
            float ly = plate.Y + MathF.Round(plate.H * G.TheatreLyricsTop);
            return new Rects(plate, new RectF(plate.X + insetX, plate.Y + insetY, cover, cover), default,
                new RectF(lx, ly, MathF.Max(0f, plate.X + plate.W - right - lx), MathF.Max(0f, plate.Y + plate.H - ClassicStageVideoBarGeometry.BarH - ly)));
        }
        // Cinema: under the top bar, down to the shelf's reserved place (or the control bar), at the audio stage's left gutter
        float top = Stage.Layout.TopBarH + G.TopUnderBar;
        float bottom = ClassicStagePictureChrome.LowerBlockTop(v, L, tier) - G.BottomGap;
        float regionH = MathF.Max(0f, bottom - top);
        float titleH = G.TitleGap + G.TitleMaxLines * G.TitleLine + G.MetaGap + G.MetaLine;
        float size = Stage.Layout.Q4(Math.Clamp(MathF.Min(L.HeroArt, regionH - titleH), MathF.Min(G.CoverMin, regionH), Stage.Layout.HeroMax));
        float x = L.PadX;
        float lyricsX = x + size + Stage.Layout.Q4(G.LyricsGapFrac * L.W);
        return new Rects(plate, new RectF(x, top, size, size), new RectF(x, top + size + G.TitleGap, size, titleH - G.TitleGap),
            lyrics ? new RectF(lyricsX, top, MathF.Max(0f, L.W - lyricsX - L.PaneRight), regionH) : default);
    }

    /// <summary>The reading column's stage of the mount (see the file header): false on the face's first frame, true from the next.</summary>
    readonly Signal<bool> _column = new(false);
    Action? _armColumn;

    public override Element Render()
    {
        var p = UseProps<Props>();
        var v = p.View;
        // the window tier's stage record (the shelf's place under the face): a crossing re-renders this face alone
        var facts = UseContext(ThemeFactsContext.Signal);
        var tier = ClassicStageTiers.StageOf(facts is null ? TierFacts.Unknown : facts.Value.Tiers);
        var sc = v.Stage;
        var L = sc.Layout.Value;
        bool theatre = v.ShowTheatre();
        bool lyrics = sc.AudioLyrics.Value;
        var r = Of(v, L, tier, theatre, lyrics);
        // the lyrics column's rect as if shown, so a column leaving (Theatre's own Lyrics panel opening) fades out where it stood
        var lr = lyrics ? r.Lyrics : Of(v, L, tier, theatre, true).Lyrics;
        var track = sc.RowValue();
        string url = track.IsValid ? Controls.ArtUrl(track.ImageId) ?? "" : "";
        float corners = Radii.Control;   // the stage covers' control corners (the identity card's)
        int decode = r.Cover.W > G.CoverDecodeSmallW ? G.CoverDecodeLarge : G.CoverDecodeSmall;
        var identity = v.Identity;
        // the field decides the arm (G.FieldArm): the title, the meta line and the reading column read on the arm the field resolved to
        var fieldArm = G.FieldArm(sc.BasePalette.Value);
        var arm = Ink.Arm(fieldArm);
        var fade = new EnterExit(Opacity: 0f, Active: true);
        var move = ClassicStageTheatre.Move;
        // the reading column mounts a frame after the face (the staged mount): posted once, from the mount's effect
        var post = UsePost();
        _armColumn ??= () => _column.Value = true;
        UseEffect(() => post(_armColumn), DepKey.Empty);
        return Stage.Layer with
        {
            Children =
            [
                // the picture's place: the track's colour field over the reservation's letterbox (the frame's own shape and place)
                Embed.Comp(new Props(v), static () => new Field()),
                new BoxEl
                {
                    Key = "stage:audioface:cover", AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, HitTestVisible = false,
                    Margin = new Edges4(r.Cover.X, r.Cover.Y, 0f, 0f), Width = r.Cover.W, Height = r.Cover.H,
                    Corners = CornerRadius4.All(corners), Shadow = Elevation.Dialog, ClipToBounds = true, Layout = move,
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
                            AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Direction = 1, Gap = G.MetaGap, HitTestVisible = false,
                            Margin = new Edges4(r.Title.X, r.Title.Y, 0f, 0f), Width = MathF.Max(r.Title.W, r.Cover.W), Layout = move,
                            Children =
                            [
                                new TextEl("")
                                {
                                    Text = Prop.Of(identity.Title), FontFamily = ClassicStageLayerGeometry.DisplayFace, Size = G.TitleSize, LineHeight = G.TitleLine,
                                    Weight = G.TitleWeight, Color = arm.Ink, Wrap = TextWrap.Wrap, MaxLines = G.TitleMaxLines, Trim = TextTrim.CharacterEllipsis,
                                    MinWidth = 0f, Shrink = 1f,
                                },
                                new TextEl("")
                                {
                                    Text = Prop.Of(() => identity.Meta(0).Line), Size = G.MetaSize, LineHeight = G.MetaLine, Color = arm.InkSecondary,
                                    MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
                                },
                            ],
                        },
                    ],
                }),
                // the synced lyrics beside the cover (not beside Theatre's own Lyrics panel), from the mount's second frame
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
                            // keyed by the arm: the column's ink is mount config, so a field that changes arm remounts it
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

    /// <summary>The colour field in the picture's place (see the file header): its own component, so a palette (a track change) or a Cinema ⇄
    /// Theatre change re-renders this box alone. The frame is a still box at the picture's rect moving with the reservation; the field inside it is
    /// keyed by its tones, so the old track's field fades out under the new one.</summary>
    sealed class Field : Component
    {
        static readonly EnterExit s_fade = new(Opacity: 0f, Active: true);

        public override Element Render()
        {
            var v = UseProps<Props>().View;
            var st = v.Stage;
            var plate = ClassicStageTheatre.PictureRect(st.Layout.Value, v.ShowTheatre());
            var palette = st.BasePalette.Value;
            var (pool, mid, deep) = G.FieldTones(palette, G.FieldArm(palette));
            var field = new GradientSpec(GradientShape.Radial, 0f,
                [new GradientStop(0f, pool), new GradientStop(G.FieldMidAt, mid), new GradientStop(G.FieldDeepAt, deep)])
            {
                RadialCenter = new Point2(G.FieldCentreX, G.FieldCentreY),
                RadialRadius = new Point2(G.FieldReachX, G.FieldReachY),
            };
            return Stage.Layer with
            {
                HitTestVisible = false, HitTestPassThrough = false,
                Children =
                [
                    new BoxEl
                    {
                        Key = "stage:audioface:plate", AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, HitTestVisible = false, ZStack = true,
                        Margin = new Edges4(plate.X, plate.Y, 0f, 0f), Width = plate.W, Height = plate.H, ClipToBounds = true, Layout = ClassicStageTheatre.Move,
                        Children =
                        [
                            new BoxEl
                            {
                                Key = "stage:audioface:field:" + HashCode.Combine(pool, mid, deep), AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch,
                                HitTestVisible = false, Gradient = field, Enter = s_fade, Exit = s_fade, Transition = MotionTok.StandardEnter,
                            },
                        ],
                    },
                ],
            };
        }
    }
}
