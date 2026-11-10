// ── Themes/Classic/Structure/Views/Stage/ClassicStageAudioFace.cs — Classic's audio face in the stage's Video mode ──────────────────────────
//
// The board "Classic · An audio-only track plays": while video stays on and the queue reaches a track without a video, the full screen keeps its
// frame (the top bar with the Video tab disabled, Cinema's Up next shelf or Theatre's panel, the transport) and the picture's place holds the
// track's cover and its synced lyrics (`StageRootView.ShowAudioFace`). The next track with a video brings the picture back by itself.
//
//   Cinema   the stage's floor under the top bar; the cover (the audio stage's hero size, held above the shelf's place) with the title (Display 28/34,
//            two lines) and the "Artist · Album" line under it on the left, the lyrics beside it at the stage's type, down to the shelf's place.
//   Theatre  inside the picture's 16:9 box (its title block stays under the box, the chrome's): the cover on the left and the lyrics beside it at the
//            Theatre panel's type, or the cover alone, centred, beside Theatre's own Lyrics panel (one reading column, never two).
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
            float pad = G.BoxPad;
            float innerH = MathF.Max(0f, plate.H - 2f * pad);
            if (!lyrics)
            {
                float c = innerH;
                return new Rects(plate, new RectF(plate.X + MathF.Round((plate.W - c) * 0.5f), plate.Y + pad, c, c), default, default);
            }
            float cover = MathF.Round(MathF.Min(innerH, (plate.W - 3f * pad) * 0.4f));
            float cy = plate.Y + MathF.Round((plate.H - cover) * 0.5f);
            float lx = plate.X + pad + cover + pad;
            return new Rects(plate, new RectF(plate.X + pad, cy, cover, cover), default,
                new RectF(lx, plate.Y + pad, MathF.Max(0f, plate.X + plate.W - pad - lx), innerH));
        }
        // Cinema: under the top bar, down to the shelf's reserved place (or the transport card), at the audio stage's left gutter
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
        float corners = Radii.Card;
        int decode = r.Cover.W > 400f ? 1024 : 512;
        var identity = v.Identity;
        var fade = new EnterExit(Opacity: 0f, Active: true);
        var move = ClassicStageTheatre.Move;
        return Stage.Layer with
        {
            Children =
            [
                // the picture's place: the stage's floor over the reservation's letterbox
                new BoxEl
                {
                    Key = "stage:audioface:plate", AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, HitTestVisible = false,
                    Margin = new Edges4(r.Plate.X, r.Plate.Y, 0f, 0f), Width = r.Plate.W, Height = r.Plate.H, Fill = Ink.Floor, Layout = move,
                },
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
                // the synced lyrics beside the cover (not beside Theatre's own Lyrics panel)
                Flow.Show(() => sc.AudioLyrics.Value && v.Layers.PictureLyrics is not null, Stage.Layer with
                {
                    Key = "stage:audioface:lyrics",
                    Enter = fade, Exit = fade, Transition = MotionTok.StandardEnter,
                    Children =
                    [
                        new BoxEl
                        {
                            AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Direction = 1, ClipToBounds = true,
                            Margin = new Edges4(lr.X, lr.Y, 0f, 0f), Width = lr.W, Height = lr.H, Layout = move,
                            Children = [v.Layers.PictureLyrics is { } build ? build() : new BoxEl()],
                        },
                    ],
                }),
            ],
        };
    }
}
