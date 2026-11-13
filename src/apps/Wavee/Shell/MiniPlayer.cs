// ── Shell/MiniPlayer.cs ────────────────────────────────────────────────────────────────────────────────────────────
// the mini player (#197): an always-on-top window in four shapes over the player bar's core
//
// Role: SHELL
// Spec: the approved prototype (scratchpad miniplayer/index.html, 2026-11-09) and the hosting spike (C:/WAVEE/scratch/miniplayer/spike.md)
//
// HOSTING. A second top-level window in this process: the engine's detached child AppHost (the video pop-out's), sharing the device,
// fonts and image cache, ticked by the main loop and presented by the one render thread. While it is open the main window is HIDDEN
// (FluentApp.SetWindowVisible(false)): its loop parks (no reconcile, layout, record or present) and its memory machine releases
// unpinned images after 2 s and parks the rest after <see cref="Rules.DeepAfterMs"/>, keeping what the mini window shows.
//
// THE SHAPE IS THE WINDOW. No layout setting: the content reads its own viewport as Strip / Wide / Square / Tall (each form by what it
// needs, <see cref="Rules.Live"/>) and reflows live, at every step of an edge drag (the window opts in to the engine's live resize, between
// <see cref="Rules.MinW"/> x MinH and MaxW x MaxH). The window is the user's: a move or a resize leaves it exactly where and how big the
// user left it (only the rect is saved); the one thing that resizes it is a shape picked in the options panel, which glides it to that
// shape's size. Never the main window's tier: the shape is this window's own.
//
// THE FACE is the mini player's own choice (default Cover): Cover, Lyrics, Canvas, Visualizer, Vinyl, CD, Cassette, Reel — the full
// player's style faces (`Deck.Face`), its lyrics view and its Canvas layer, gated on THIS window (`Deck.SurfaceOpen`, Canvas's Mini
// surface), so they run while the main window is hidden and stop with the mini player.
//
// ENTRY POINTS: `Toggle` from the player bar's mini player button, Ctrl+Alt+M (a global hot key, quiet when taken), the palette and the
// taskbar jump list (`wavee://mini`). Leaving never stops the music: the art or "back to Wavee" brings the full window back.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu;
using FluentGpu.Hosting;
using FluentGpu.Localization;
using FluentGpu.Pal;
using FluentGpu.Pal.Windows;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using ViewSlots = FluentGpu.Theming.Slots;

namespace Wavee;

public static partial class MiniPlayer
{
    const string LogCategory = "mini";

    // ══ 1. THE RULES (pure) ══════════════════════════════════════════════════════════════════════════════════════════════

    public static class Rules
    {
        /// <summary>The prototype's default: Wide at 392 × 163 DIP.</summary>
        public const float DefaultW = 392f, DefaultH = 163f;
        /// <summary>The smallest client the window allows: a tall column's width, and a strip's height. The height holds the tallest theme's
        /// strip with air above and below: Zune's 23-DIP display title over its artist line is 44.5 DIP (48.6 with the row's ink), never
        /// shrunk, so 56 leaves about 4 DIP each side, and every theme's strip, ticker and hover plate stays inside the window.</summary>
        public const float MinW = 232f, MinH = 56f;
        /// <summary>The largest client the window allows: past it a mini player is a second main window (the forms are laid out and
        /// swept up to here).</summary>
        public const float MaxW = 720f, MaxH = 900f;
        /// <summary>The form floors. Wide: the card's height (title, artist, seek, transport beside an art box of the height) and the column
        /// beside its art (the save, transport and device row). Square: the filling form's height (its window buttons, title block and hover
        /// controls). Tall: a column tall enough for its fixed rows plus a useful face.</summary>
        public const float WideColumnFloor = 214f, WideHeightFloor = 142f, SquareHeightFloor = 170f, TallHeightFloor = 300f;
        /// <summary>Clearly landscape (Wide) is at least this wide for its height; clearly portrait (Tall) at most this.</summary>
        public const float WideAspect = 4f / 3f, TallAspect = 0.75f;
        /// <summary>A form is entered this far (DIP) past its boundary and kept until its boundary itself (its floors) or this far back
        /// across it (its aspect): a live drag held on a boundary never flips the form back and forth.</summary>
        public const float Hysteresis = 8f;
        /// <summary>The shape pick's glide (window bounds) and its step.</summary>
        public const float SettleMs = 300f, SettleStepMs = 16f;
        /// <summary>The main window's deep memory park while the mini player is the only surface (300 s otherwise).</summary>
        public const long DeepAfterMs = 20_000;
        /// <summary>The global hot key: Ctrl+Alt+M.</summary>
        public const int HotKeyId = 0x4D50;
        public const uint HotKeyMods = 0x0002 | 0x0001, HotKeyVk = 0x4D;

        /// <summary>The mini window's motion ceiling per face (fps; 0 = the display's rate). Measured 2026-11-09 at mini sizes: a spinning
        /// style ran its per-frame clock at the panel's 120 Hz, though what moves visibly is the label or the reels (the grooves and the
        /// rim are rotationally even): a 33⅓ rpm label at a mini side moves about 120 px/s, so 60 steps of 2 px keep it smooth at half
        /// the frames. The lyrics view's line sweep reads the same at 30. Cover, Canvas (the clip's own rate) and the analyser (its band
        /// clock, ~25/s) need no cap.</summary>
        public static int FrameCap(MiniFaceKind kind) => kind switch
        {
            MiniFaceKind.Object => 60,
            MiniFaceKind.Lyrics => 30,
            _ => 0,
        };

        /// <summary>A wheel notch over the player moves the volume this much (the prototype's ±4 %).</summary>
        public const float WheelStep = 0.04f;

        /// <summary>The tall column shows its volume row when there is room: 470 DIP tall or 270 wide (the prototype's rule).</summary>
        public static bool Roomy(float w, float h) => h >= 470f || w >= 270f;

        /// <summary>The shape pick's sizes: each shape's ratio and width range (the window glides to the picked shape at about its area).</summary>
        public static float Ratio(MiniShape s) => s switch { MiniShape.Strip => 6f, MiniShape.Wide => 2.4f, MiniShape.Square => 1f, _ => 9f / 16f };

        public static (float Min, float Max) WidthRange(MiniShape s) => s switch
        {
            MiniShape.Strip => (336f, 520f),   // 336 / 6 = the floor's 56
            MiniShape.Wide => (390f, 560f),   // at 390 x 163 the card clears its floors by the hysteresis
            MiniShape.Square => (260f, 400f),
            _ => (232f, 330f),
        };

        /// <summary>The form the content draws at a live size, given the form it draws now. Each form is defined by what it needs, never
        /// by a nearest ratio: Wide is clearly landscape with room for the card's height and its column; Tall is clearly portrait and tall
        /// enough for its rows and a face; Square (the filling form) is everything else at its height floor; Strip is everything shorter.
        /// The form drawn now is kept on its own floors and with <see cref="Hysteresis"/> of slack on its aspect; any other must clear its
        /// floors and aspect by the hysteresis to take over.</summary>
        public static MiniShape Live(float w, float h, MiniShape current)
        {
            if (w <= 0f || h <= 0f) return current;
            if (Holds(MiniShape.Wide, w, h, current)) return MiniShape.Wide;
            if (Holds(MiniShape.Tall, w, h, current)) return MiniShape.Tall;
            if (Holds(MiniShape.Square, w, h, current)) return MiniShape.Square;
            return MiniShape.Strip;
        }

        static bool Holds(MiniShape form, float w, float h, MiniShape current)
        {
            bool kept = form == current;
            float floor = kept ? 0f : Hysteresis, aspect = kept ? -Hysteresis : Hysteresis;
            return form switch
            {
                MiniShape.Wide => h >= WideHeightFloor + floor && w - h >= WideColumnFloor + floor && w >= h * WideAspect + aspect,
                MiniShape.Tall => h >= TallHeightFloor + floor && w <= h * TallAspect - aspect,
                MiniShape.Square => h >= SquareHeightFloor + floor,
                _ => true,
            };
        }

        /// <summary>A shape at an area (DIP²), its width clamped to the shape's range.</summary>
        public static Size2 SizeFor(MiniShape shape, float area)
        {
            float ratio = Ratio(shape);
            var (min, max) = WidthRange(shape);
            float width = Math.Clamp(MathF.Sqrt(MathF.Max(1f, area) * ratio), min, max);
            return new Size2(MathF.Round(width), MathF.Round(width / ratio));
        }

        /// <summary>Place a window of <paramref name="size"/> (outer px) where <paramref name="from"/> was, keeping the corner of it that is
        /// nearest the work area's matching corner fixed, and keep it inside the work area.</summary>
        public static RectF Place(RectF from, Size2 size, RectF work)
        {
            if (work.IsInfinite || work.W <= 0f) return new RectF(from.X, from.Y, size.Width, size.Height);
            float cx = from.X + from.W * 0.5f, cy = from.Y + from.H * 0.5f;
            bool right = cx > work.X + work.W * 0.5f, bottom = cy > work.Y + work.H * 0.5f;
            float x = right ? from.X + from.W - size.Width : from.X;
            float y = bottom ? from.Y + from.H - size.Height : from.Y;
            x = Math.Clamp(x, work.X, MathF.Max(work.X, work.X + work.W - size.Width));
            y = Math.Clamp(y, work.Y, MathF.Max(work.Y, work.Y + work.H - size.Height));
            return new RectF(MathF.Round(x), MathF.Round(y), size.Width, size.Height);
        }

        /// <summary>One glide step: eased (decelerate) interpolation of the outer rect.</summary>
        public static RectF Lerp(RectF a, RectF b, float t)
        {
            float e = 1f - (1f - t) * (1f - t) * (1f - t);
            return new RectF(a.X + (b.X - a.X) * e, a.Y + (b.Y - a.Y) * e, a.W + (b.W - a.W) * e, a.H + (b.H - a.H) * e);
        }
    }

    // ══ 2. THE FACES ═════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>One face: its kind and the player style it draws (-1 = not a deck style).</summary>
    readonly record struct FaceDef(MiniFaceKind Kind, int StyleId);

    /// <summary>The eight faces, in flip order (decision 2026-11-09: the curated set, the mini player's own choice, default Cover).</summary>
    static readonly FaceDef[] s_faces =
    [
        new(MiniFaceKind.Cover, Rail.PlayerCatalog.Cover),
        new(MiniFaceKind.Lyrics, -1),
        new(MiniFaceKind.Canvas, Rail.PlayerCatalog.Canvas),
        new(MiniFaceKind.Visualizer, Rail.PlayerCatalog.Visualizer),
        new(MiniFaceKind.Object, Rail.PlayerCatalog.Vinyl),
        new(MiniFaceKind.Object, Rail.PlayerCatalog.Cd),
        new(MiniFaceKind.Object, Rail.PlayerCatalog.Cassette),
        new(MiniFaceKind.Object, Rail.PlayerCatalog.Reel),
    ];

    public static int FaceCount => s_faces.Length;
    static int ClampFace(int i) => (uint)i < (uint)s_faces.Length ? i : 0;

    static string FaceName(int i)
    {
        var f = s_faces[ClampFace(i)];
        return f.Kind == MiniFaceKind.Lyrics ? Loc.Get(Strings.Mini.Lyrics) : Loc.Get(Rail.PlayerCatalog.ById(f.StyleId).NameKey);
    }

    /// <summary>The face in a box. The animated faces gate on <see cref="IsOpen"/> (the deck's <see cref="Deck.SurfaceOpen"/>, the lyrics
    /// view's visibility) and on the window's own activity (a parked mini window runs none of them).</summary>
    static Element BuildFace(MiniFaceRequest r)
    {
        var f = s_faces[ClampFace(r.Face)];
        Element Cover() => Embed.Comp(new CoverProps(r.ArtUrl, r.Width, r.Height, r.Corner), static () => new MiniCover());
        if (r.Use == MiniFaceUse.Thumb) return Cover();
        // The options panel's tile: a deck face at the tile's side under a surface gate that never opens, so it draws its resting picture
        // and runs no clock, loop or audio lease; every other face's picture is the panel's own, over the cover.
        if (r.Use == MiniFaceUse.Tile)
            return f.Kind == MiniFaceKind.Object
                ? Ctx.Provide(Deck.SurfaceOpen, s_tileSurface, new BoxEl
                {
                    Width = r.Width, Height = r.Height, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, ClipToBounds = true,
                    Corners = CornerRadius4.All(r.Corner), HitTestVisible = false,
                    Children = [Deck.Face(f.StyleId, MathF.Floor(MathF.Min(r.Width, r.Height)))],
                })
                : Cover();
        switch (f.Kind)
        {
            case MiniFaceKind.Lyrics:
                // The lines are the template's (the app's own lyrics view); the face is what they read over: the cover in a box, and filling the
                // window the album's colour (`LyricsGround`): the cover baked blurred once per track (the previous one kept under the new until it
                // has faded in), its colour lifted, under an even dark veil, so the lines read on-dark in light and dark alike.
                if (r.Use != MiniFaceUse.Fill) return Cover();
                return Embed.Comp(new LyricsGroundProps(r.ArtUrl ?? "", r.Width, r.Height), static () => new LyricsGround());
            case MiniFaceKind.Canvas:
                return new BoxEl
                {
                    Width = r.Width, Height = r.Height, ZStack = true, ClipToBounds = true, Corners = CornerRadius4.All(r.Corner),
                    Children = [Cover(), CanvasVideo.Layer(CanvasVideo.Surface.Mini, CornerRadius4.All(r.Corner))],
                };
            case MiniFaceKind.Visualizer when r.Paint is { } paint:
            {
                // The theme draws the analyser (WinUI's bars, Zune's waveform, Classic's sines) over the full player's own bands: the same
                // deck host, model, clock and spectrum lease, another picture.
                float w = MathF.Floor(r.Width), h = MathF.Floor(r.Height);
                return Ctx.Provide(Deck.SurfaceOpen, IsOpen,
                    Deck.Painted(f.StyleId, w, h, sig => paint(new MiniVisualizerFrame(w, h, sig.Bands, sig.Peaks, sig.LoopsRun))));
            }
            case MiniFaceKind.Visualizer:
            case MiniFaceKind.Object:
            {
                float side = MathF.Floor(MathF.Min(r.Width, r.Height));
                return Ctx.Provide(Deck.SurfaceOpen, IsOpen, new BoxEl
                {
                    Width = r.Width, Height = r.Height, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, ClipToBounds = true,
                    Children = [Deck.Face(f.StyleId, side)],
                });
            }
            default:
                return Cover();
        }
    }

    sealed record CoverProps(string? Url, float Width, float Height, float Corner);

    /// <summary>The cover in a box, decoded for the device pixels it covers. <see cref="Controls.Artwork"/> left to itself decodes at the
    /// box's DIP size (its hint is taken as device pixels), so on a 150 % display a 310-DIP square drew a 312-px decode over 465 px:
    /// soft and blocky. The edge is rounded UP to a <see cref="CoverDecodeStepPx"/> rung, so a window resized live asks for a new decode
    /// only when it crosses a rung (four or five over the whole size range), and the engine holds the old picture until the new one is in.</summary>
    sealed class MiniCover : Component
    {
        public override Element Render()
        {
            var p = UseProps<CoverProps>();
            float scale = UseContext(Viewport.Scale);
            return Controls.Artwork(p.Url, p.Width, p.Height, p.Corner, decodePx: CoverDecodePx(MathF.Max(p.Width, p.Height), scale),
                keepWhileHidden: true);
        }
    }

    /// <summary>The cover's decode edge (device px) for a box edge (DIP): rounded up to the rung, at most the shared decode ceiling.</summary>
    static int CoverDecodePx(float edgeDip, float scale)
    {
        if (!(scale > 0f) || !float.IsFinite(scale)) scale = 1f;
        int px = (int)MathF.Ceiling(MathF.Max(1f, edgeDip) * scale / CoverDecodeStepPx) * CoverDecodeStepPx;
        return Math.Min(px, Design.ImageDecodeScale.Ceiling);
    }

    /// <summary>The cover's decode rung (device px): coarse enough that a live drag crosses few, fine enough that a rung's overshoot
    /// (at most this much) costs little memory.</summary>
    const int CoverDecodeStepPx = 128;


    // ══ 3. STATE AND VERBS ═══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The options panel's deck pictures stand on a surface that is never open: the deck's clock and loops never start.</summary>
    static readonly Signal<bool> s_tileSurface = new(false);

    /// <summary>The playing item's cover url, read live (the options panel's pictures and accent).</summary>
    static string? NowArtUrl()
    {
        var r = Playback.Current.Value;
        if (r.IsNone) return null;
        _ = Entities.ScopeEpoch.Value;   // FIRST (G-179): a scope switch re-points the table read below
        switch (r.Kind)
        {
            case EntityKind.Track:
            {
                _ = Entities.Current.Tracks.Changed.Value;
                var t = new Track(r.Slot);
                return t.IsValid && t.Knows(TrackFields.Image) ? Controls.ArtUrl(t.ImageId) : null;
            }
            case EntityKind.Episode:
            {
                _ = Entities.Current.Episodes.Changed.Value;
                var e = new Episode(r.Slot);
                return e.IsValid && e.Knows(EpisodeFields.Image) ? Controls.ArtUrl(e.ImageId) : null;
            }
            default:
                return null;
        }
    }

    /// <summary>The mini player is open (the main window is hidden while it is).</summary>
    public static readonly Signal<bool> IsOpen = new(false);
    static readonly Func<bool> s_lyricsVisible = static () => IsOpen.Value;
    sealed record LyricsGroundProps(string Url, float Width, float Height);

    /// <summary>The lyrics face's ground: the album's own colour. The cover under a baked blur (once per cover, a cached bitmap: nothing per
    /// frame at rest), its saturation lifted so a red cover gives a deep red ground rather than brown, the previous cover kept under the new one
    /// so a track change fades blur to blur (the stage backdrop's way), and over both an even veil, deepening toward the foot where the title
    /// stands. The veil is light (<see cref="GroundVeilA"/>) over a dark or mid cover and only as deep as a bright one needs for the white
    /// active line to hold 4.5:1: solved once per cover from its measured luminance (<see cref="Palette.Tone"/>), faded in when it lands.
    /// <para>The blur is decoded at a FIXED small square (<see cref="GroundDecodePx"/>) and cover-fitted into the window, whatever its size: a
    /// colour field needs no detail, and a window resized live re-lays the picture out each frame but never asks for a new decode or bake.</para></summary>
    sealed class LyricsGround : Component
    {
        string _shown = "", _previous = "";

        public override Element Render()
        {
            var p = UseProps<LyricsGroundProps>();
            if (p.Url != _shown) { _previous = _shown; _shown = p.Url; }
            var kids = new List<Element>(3);
            if (_previous.Length > 0) kids.Add(Art(_previous, p) with { Key = "lg:" + _previous });
            if (_shown.Length > 0) kids.Add(Art(_shown, p) with { Key = "lg:" + _shown });
            var arm = Wavee.Theming.Roles.StageInk.Arm(ThemeKind.Dark);
            var veil = arm.Veil;
            float veilA = VeilFor(_shown.Length > 0 ? Palette.Tone(_shown) : null);
            kids.Add(new BoxEl
            {
                Grow = 1f, AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
                Fill = veil with { A = veilA }, BrushTransitionMs = MediaScrim.FadeMs,
            });
            kids.Add(new BoxEl
            {
                Grow = 1f, AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
                Gradient = new GradientSpec(GradientShape.Linear, GroundVeilAngle,
                    [new GradientStop(GroundVeilFootFrom, veil with { A = 0f }), new GradientStop(1f, veil with { A = GroundVeilFootA })]),
            });
            return new BoxEl
            {
                Width = p.Width, Height = p.Height, ZStack = true, ClipToBounds = true, HitTestVisible = false,
                Fill = Wavee.Theming.Roles.StageInk.Arm(ThemeKind.Dark).Floor, Children = kids.ToArray(),
            };
        }

        MediaScrim.LumaMap? _solvedMap;
        float _solvedA = GroundVeilA;

        /// <summary>The even veil's alpha for a cover: <see cref="GroundVeilA"/>, or deeper where the blurred cover's bright part
        /// (<see cref="GroundPercentile"/> of its luminance: the blur spreads a highlight, so not the photograph's brightest tenth) would leave the
        /// active line under 4.5:1. The line is white, or (Zune) the accent lifted toward white: the solve holds an ink as dim as
        /// <see cref="GroundInkGrey"/>, so either reads. Solved once per measured map (the window's resizes re-render this without solving again).</summary>
        float VeilFor(MediaScrim.LumaMap? map)
        {
            if (map is null) return GroundVeilA;
            if (ReferenceEquals(map, _solvedMap)) return _solvedA;
            var ink = new ColorF(GroundInkGrey, GroundInkGrey, GroundInkGrey, 1f);
            float grey = MediaScrim.GreyAt(map, (0f, 0f, 1f, 1f), GroundPercentile);
            float need = MediaScrim.NeedBody * MediaScrim.Headroom, lo = GroundVeilA, hi = GroundVeilMax;
            if (MediaScrim.Contrast(ink, grey * (1f - lo)) >= need) hi = lo;
            else
                for (int i = 0; i < 16; i++)
                {
                    float mid = (lo + hi) * 0.5f;
                    if (MediaScrim.Contrast(ink, grey * (1f - mid)) >= need) hi = mid; else lo = mid;
                }
            _solvedMap = map;
            _solvedA = hi;
            return hi;
        }

        /// <summary>The blurred cover, overscanned past the window by <see cref="GroundOverscan"/> each side so the blur's soft rim never shows.</summary>
        static ImageEl Art(string url, LyricsGroundProps p)
        {
            float ox = p.Width * GroundOverscan, oy = p.Height * GroundOverscan;
            return new ImageEl
            {
                Source = url, DecodePx = GroundDecodePx, Fit = ImageFit.Cover, Saturation = GroundSaturation,
                AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, Margin = new Edges4(-ox, -oy, -ox, -oy),
                BakedBlur = new BakedBlurSpec(GroundBlurSigma, GroundBlurResolution),
                KeepWhileHidden = true, Placeholder = Wavee.Theming.Roles.StageInk.ArtStandIn(url), RevealTransition = ImageTransition.Fade(Stage.Tone.CrossFadeMs),
            };
        }
    }

    /// <summary>The ground's decode (device px, square) and its bake: the blur's σ is in the bake's texels (σ × resolution), here about a
    /// twelfth of the picture, a colour field with the cover's large shapes still felt.</summary>
    const int GroundDecodePx = 192;
    const float GroundBlurSigma = 18f, GroundBlurResolution = 0.5f;
    /// <summary>How far the blurred cover reaches past each edge (a fraction of the window).</summary>
    const float GroundOverscan = 0.12f;
    /// <summary>The ground's colour lift (the blur averages chroma away; 1 = the cover's own).</summary>
    const float GroundSaturation = 1.45f;
    /// <summary>The veil: even over the lines (at least <see cref="GroundVeilA"/>, at most <see cref="GroundVeilMax"/> over the brightest cover), and
    /// a second one from <see cref="GroundVeilFootFrom"/> of the height deepening to the foot (the title's ground).</summary>
    const float GroundVeilA = 0.42f, GroundVeilMax = 0.82f, GroundVeilFootA = 0.4f, GroundVeilFootFrom = 0.55f, GroundVeilAngle = 90f;
    /// <summary>The share of the cover's luminance the veil is solved against (see <c>VeilFor</c>).</summary>
    const float GroundPercentile = 0.75f;
    /// <summary>The dimmest active-line ink the veil is solved for (sRGB grey, a relative luminance of about 0.42: an accent lifted 45 % toward
    /// white, Zune's sung line).</summary>
    const float GroundInkGrey = 0.68f;
    /// <summary>The per-theme "Colour from album art" choices (<see cref="Platform.Keys.MiniPlayerArtColour"/>).</summary>
    static readonly Signal<int> s_artColour = new(-1);
    /// <summary>Bumps on every wheel step (the template's volume HUD).</summary>
    static readonly Signal<int> s_volumeHud = new(0);
    /// <summary>The tall column has room for its volume row.</summary>
    static readonly Signal<bool> s_roomy = new(false);
    static readonly Signal<int> s_face = new(-1);
    static readonly Signal<bool> s_onTop = new(true);
    static readonly Signal<MiniShape> s_shape = new(MiniShape.Wide);
    static readonly Signal<(WindowMaterial Material, bool Dark)> s_material = new((WindowMaterial.None, true));
    /// <summary>A shape the Options asked for (the owner resizes to it), with a sequence so the same shape twice still acts.</summary>
    static readonly Signal<(MiniShape Shape, int Seq)> s_shapeRequest = new((MiniShape.Wide, 0));
    static Size2 s_clientDip;   // the mini window's last viewport (DIP), for the shape pick's chrome arithmetic
    static IDetachedVideoWindow? s_live;   // the open mini window (its work area caps an escaping menu)
    /// <summary>The shape pick's glide is running, and its step (the owner's): ticked by the mini window's own <see cref="Body"/>.</summary>
    static readonly Signal<bool> s_settling = new(false);
    static Action? s_settleStep;
    static readonly Action s_settleTick = static () => s_settleStep?.Invoke();
    static float s_scale = 1f;

    static void EnsurePrefs()
    {
        if (s_face.Peek() >= 0) return;
        s_face.Value = ClampFace(Platform.Settings.Get(Platform.Keys.MiniPlayerFace));
        s_onTop.Value = Platform.Settings.Get(Platform.Keys.MiniPlayerOnTop);
        s_artColour.Value = Platform.Settings.Get(Platform.Keys.MiniPlayerArtColour);
    }

    /// <summary>"Colour from album art" for a theme slot: its explicit choice, else the theme's default.</summary>
    static bool ArtColour(int slot, bool fallback)
    {
        int mask = s_artColour.Value;
        if (mask < 0 || (uint)slot > 7u) return fallback;
        return (mask & (1 << slot)) != 0 || ((mask & (1 << (slot + 8))) == 0 && fallback);
    }

    static void SetArtColour(int slot, bool on)
    {
        if ((uint)slot > 7u) return;
        int mask = Math.Max(0, s_artColour.Peek());
        mask &= ~((1 << slot) | (1 << (slot + 8)));
        mask |= on ? 1 << slot : 1 << (slot + 8);
        s_artColour.Value = mask;
        Platform.Settings.Set(Platform.Keys.MiniPlayerArtColour, mask);
    }

    /// <summary>A cover's accent, floored for text against the theme (the detail pages' ladder without its payload and hold): tracked on the
    /// palette's grading of that url, so a late grading repaints.</summary>
    static ColorF? AlbumAccent(string? url)
    {
        if (url is not { Length: > 0 }) return null;
        _ = Palette.Watch(url).Value;
        return Design.ChromeSchemeFor(url) is { } scheme ? Design.Palette.Floor(Design.Palette.ChromeAccent(scheme)) : null;
    }

    /// <summary>A wheel over the player: volume, <see cref="Rules.WheelStep"/> per notch (unmuting), with the HUD. The engine's steps are
    /// signed toward the content's end (a notch DOWN is positive), so a notch up is louder.</summary>
    static void WheelVolume(float steps)
    {
        if (steps == 0f) return;
        if (Playback.Muted.Peek()) Playback.ToggleMute();
        float v = Math.Clamp(Playback.Volume.Peek() - steps * Rules.WheelStep, 0f, 1f);
        Playback.SetVolume(MathF.Round(v * 100f) / 100f);
        s_volumeHud.Value = s_volumeHud.Peek() + 1;
    }

    /// <summary>Open or close the mini player (the button, the hot key, the jump list, the palette).</summary>
    public static void Toggle(string cause)
    {
        Log.Info(LogCategory, "toggle cause=" + cause + " open=" + !IsOpen.Peek());
        EnsurePrefs();
        IsOpen.Value = !IsOpen.Peek();
    }

    /// <summary>Back to the full window: the mini player closes, the music plays on.</summary>
    public static void Close(string cause)
    {
        if (!IsOpen.Peek()) return;
        Log.Info(LogCategory, "close cause=" + cause);
        IsOpen.Value = false;
    }

    static void SetFace(int index)
    {
        index = ClampFace(index);
        if (s_face.Peek() == index) return;
        s_face.Value = index;
        Platform.Settings.Set(Platform.Keys.MiniPlayerFace, index);
    }

    static void ToggleOnTop()
    {
        bool next = !s_onTop.Peek();
        s_onTop.Value = next;
        Platform.Settings.Set(Platform.Keys.MiniPlayerOnTop, next);
    }

    static string ArtistText()
    {
        var c = Shell.PlayerBarHost.Props.Credits();
        if (c.Show is { } show) return show.Text;
        var a = c.Artists;
        if (a.Length == 0) return "";
        if (a.Length == 1) return a[0].Text;
        var sb = new System.Text.StringBuilder(a[0].Text);
        for (int i = 1; i < a.Length; i++) sb.Append(", ").Append(a[i].Text);
        return sb.ToString();
    }

    static string DetailText()
    {
        if (Shell.PlayerBarHost.Props.RemoteDeviceName() is { Length: > 0 } device) return Strings.Player.PlayingOn(device);
        var current = Playback.Current.Value;
        if (current.Kind != EntityKind.Track || current.IsNone || Entities.Current is not { } scope) return "";
        _ = scope.Albums.Changed.Value;
        var track = new Track(current.Slot);
        return track.IsValid && track.Album.IsValid ? track.Album.Title : "";
    }

    /// <summary>The height of the work area the mini window sits on (DIP): what a menu escaping the window can use.</summary>
    static float ScreenRoomDip()
    {
        var at = s_live is { IsOpen: true } w ? new Point2(w.BoundsPx.X + w.BoundsPx.W * 0.5f, w.BoundsPx.Y + w.BoundsPx.H * 0.5f) : default;
        var work = Win32App.WorkAreaAt(at);
        return work.IsInfinite || work.H <= 0f ? 0f : work.H / s_scale;
    }

    /// <summary>The ONE props record (every member stable).</summary>
    static readonly MiniPlayerProps s_props = new()
    {
        Bar = Shell.PlayerBarHost.Props,
        Shape = s_shape,
        Face = s_face,
        FaceCount = s_faces.Length,
        FaceKind = static i => s_faces[ClampFace(i)].Kind,
        FaceName = FaceName,
        FaceView = BuildFace,
        TimeText = Shell.TimeLabelText,
        ToggleRemaining = static () => PlayerVerbs.Default.ToggleRemaining(),
        ArtistText = ArtistText,
        DetailText = DetailText,
        SetFace = SetFace,
        BackToWavee = static () => Close("back"),
        OnTop = s_onTop,
        ToggleOnTop = ToggleOnTop,
        SetShape = static shape => s_shapeRequest.Value = (shape, s_shapeRequest.Peek().Seq + 1),
        WheelVolume = WheelVolume,
        UseMaterial = static (m, dark) => s_material.Value = (m, dark),
        ArtColour = ArtColour,
        SetArtColour = SetArtColour,
        ArtUrl = NowArtUrl,
        AlbumAccent = AlbumAccent,
        SeekRail = static draw => Shell.SeekBarDrawn(draw),
        Lyrics = static draw => Lyrics.Mini(draw, s_lyricsVisible),
        LyricsView = static v => Lyrics.MiniView(v.OnMedia, v.TypeScale, v.Band, v.Gutter, v.Accent, s_lyricsVisible),
        ToggleDevicePicker = static (overlay, anchor, handle) => Shell.EscapedToggleDevicePicker(overlay, anchor, handle, ScreenRoomDip()),
        VolumeHud = s_volumeHud,
        CanSave = static () => Playback.Current.Value is { IsNone: false } c && c.Kind is EntityKind.Track or EntityKind.Episode,
        Roomy = s_roomy,
    };

    /// <summary>The inspector's `mount mini=1 face=N`: the window's content alone (its overlay host; the headless root adds the theme
    /// providers), with the mini player reading as open so its faces run.</summary>
    internal static readonly Func<Element> InspectContent = static () =>
        OverlayHost.Create(Embed.Comp(static () => new Body()), isPrimaryToastHost: false);

    /// <summary>The inspector's `mount mini=1 deskW= deskH=`: the window's content at <paramref name="window"/> in the bottom-right corner of a
    /// stand-in screen, under ONE overlay host spanning the screen, so a menu that escapes the real window draws where it would open.</summary>
    internal static Func<Element> InspectDesk(Size2 window) => () =>
        OverlayHost.Create(Embed.Comp(new DeskProps(window), static () => new Desk()), isPrimaryToastHost: false);

    sealed record DeskProps(Size2 Window);

    sealed class Desk : Component
    {
        static readonly ColorF Screen = new(0.5f, 0.5f, 0.52f, 1f);
        /// <summary>The window's inset from the stand-in screen's corner.</summary>
        const float Inset = 12f;

        public override Element Render()
        {
            var w = UseProps<DeskProps>().Window;
            return new BoxEl
            {
                Grow = 1f, AlignSelf = FlexAlign.Stretch, ZStack = true, Fill = Screen,
                Children =
                [
                    new BoxEl
                    {
                        Width = w.Width, Height = w.Height, AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.End, Direction = 1, ClipToBounds = true,
                        Margin = new Edges4(0f, 0f, Inset, Inset),
                        Children = [Ctx.Provide(Viewport.Size, w, Embed.Comp(static () => new Body()))],
                    },
                ],
            };
        }
    }

    internal static void InspectMount(bool mini, int face)
    {
        EnsurePrefs();
        if (IsOpen.Peek() != mini) IsOpen.Value = mini;
        if (face >= 0) s_face.Value = ClampFace(face);
    }

    /// <summary>The props, for the headless inspector's mini route (the same record the window draws).</summary>
    internal static MiniPlayerProps Props { get { EnsurePrefs(); return s_props; } }

    // ══ 4. THE WINDOW'S CONTENT ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The mini window's root: its own overlay host (flyouts, the device picker) inside the theme providers, which re-render it
    /// on a theme switch. A detached window builds its own AppHost, so nothing of the main window's tree reaches it.</summary>
    [CoreHost]
    internal sealed class Root : Component
    {
        public override Element Render() => Theming.ThemeRoot.Wrap(static () =>
            OverlayHost.Create(Embed.Comp(static () => new Body()), isPrimaryToastHost: false));
    }

    /// <summary>The core host: the shape from THIS window's viewport, then `Player.Mini`.</summary>
    [CoreHost]
    internal sealed class Body : Component
    {
        public override Element Render()
        {
            var vp = UseContextSignal(Viewport.Size);
            float scale = UseContext(Viewport.Scale);
            UseSignalEffect(() => Track(vp.Value, scale));
            // The shape pick's glide clock: THIS window's (the owner's own interval sits in the main window's tree, which is hidden while
            // the mini player shows, and a hidden tree's intervals pause: a shape picked from the panel moved the window one step and stopped).
            UseInterval(s_settleTick, Rules.SettleStepMs, enabled: s_settling.Value);
            return ViewSlots.View(ViewKeys.MiniPlayer, Props);
        }
    }

    /// <summary>The viewport → shape fold (outside the core host: a size compared with the shape boundaries is a rule, not a look).</summary>
    static void Track(Size2 viewport, float scale)
    {
        if (viewport.Width <= 0f || viewport.Height <= 0f) return;
        s_clientDip = viewport;
        s_scale = scale > 0f ? scale : 1f;
        var shape = Rules.Live(viewport.Width, viewport.Height, s_shape.Peek());
        if (s_shape.Peek() != shape) s_shape.Value = shape;
        bool roomy = Rules.Roomy(viewport.Width, viewport.Height);
        if (s_roomy.Peek() != roomy) s_roomy.Value = roomy;
    }

    // ══ 5. THE WINDOW'S OWNER ════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>THE single owner of the mini window's lifecycle, mounted once in the shell's top layer; renders nothing. It opens the window
    /// on the <see cref="IsOpen"/> edge (posted out of the reactive flush), hides the main window, persists the rect where the user leaves
    /// it, follows keep-on-top and the theme's material, glides the window to a picked shape, and holds the global hot key.</summary>
    public sealed class Owner : Component
    {
        static readonly Action<FrameStats> s_shellTick = Shell.FrameTick;
        IDetachedVideoWindow? _window;
        bool _openQueued, _unmounted;
        RectF _settleFrom, _settleTo;
        float _settleT = 1f;
        long _deepWas = -1;
        Action<int>? _onHotKey;
        Action<WindowStateChange>? _onMainState;
        bool _mainHidden;

        public override Element Render()
        {
            var hooks = UseContext(InputHooks.Current);
            var post = UsePost();
            var settling = s_settling;
            UseEffect(() =>
            {
                s_settleStep = () => SettleStep(settling);
                return () => s_settleStep = null;
            }, DepKey.Empty);

            UseEffect(() =>
            {
                // The full window coming back by ANY route (the tray, a relaunch, a wavee:// link, a jump-list task, a toast) closes the mini
                // player: the two never show at once. Our own hide and show pass through here too, harmlessly (closed already, or not shown).
                _onMainState = change =>
                {
                    if (!_mainHidden || !IsOpen.Peek() || !(change.Shown || change.Unparked) || change.Current.Parked) return;
                    Close("main-shown");
                };
                FluentApp.WindowStateChanged += _onMainState;
                return () => FluentApp.WindowStateChanged -= _onMainState;
            }, DepKey.Empty);

            UseEffect(() =>
            {
                _onHotKey = id => { if (id == Rules.HotKeyId) Toggle("hotkey"); };
                Win32App.HotKeyPressed += _onHotKey;
                bool ok = Win32App.TryRegisterHotKey(FluentApp.WindowHandle, Rules.HotKeyId, Rules.HotKeyMods, Rules.HotKeyVk);
                Log.Info(LogCategory, "hotkey ctrl+alt+m registered=" + ok);
                return () =>
                {
                    _unmounted = true;
                    Win32App.HotKeyPressed -= _onHotKey;
                    Win32App.UnregisterHotKey(Rules.HotKeyId);
                    CloseWindow();
                };
            }, DepKey.Empty);

            UseSignalEffect(() =>
            {
                bool open = IsOpen.Value;
                bool alive = _window is { IsOpen: true };
                if (open && !alive) QueueOpen(hooks, post, settling);
                else if (!open && alive) CloseWindow();
            });
            UseSignalEffect(() =>
            {
                bool onTop = s_onTop.Value;
                if (_window is { IsOpen: true } w) w.SetTopmost(onTop);
            });
            UseSignalEffect(() =>
            {
                var (m, dark) = s_material.Value;
                if (_window is { IsOpen: true } w) w.SetMaterial(m, dark);
            });
            UseSignalEffect(() =>
            {
                var (shape, seq) = s_shapeRequest.Value;
                if (seq == 0 || _window is not { IsOpen: true } w) return;
                var size = Rules.SizeFor(shape, s_clientDip.Width * s_clientDip.Height);
                StartSettle(w, size, settling);
            });
            UseSignalEffect(() =>
            {
                int face = s_face.Value;
                if (_window is { IsOpen: true } w) w.PowerCapFps = Rules.FrameCap(s_faces[ClampFace(face)].Kind);
            });
            UseSignalEffect(() =>
            {
                _ = Playback.Current.Value;
                if (_window is { IsOpen: true } w) w.SetTitle(Title());
            });
            return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };
        }

        static string Title() => Shell.PlayerBarHost.Props.TitleText() is { Length: > 0 } t ? t + " · Wavee" : "Wavee";

        void QueueOpen(InputHooks hooks, Action<Action> post, Signal<bool> settling)
        {
            if (_openQueued) return;
            _openQueued = true;
            post(() =>
            {
                _openQueued = false;
                if (_unmounted || !IsOpen.Peek() || _window is { IsOpen: true }) return;
                Open(hooks, settling);
            });
        }

        void Open(InputHooks hooks, Signal<bool> settling)
        {
            EnsurePrefs();
            var restored = default(RectF);
            if (Video.PlacementPersistence.TryLoadRect(Platform.Settings.Get(Platform.Keys.MiniPlayerRect), out float x, out float y, out float w, out float h))
                restored = new RectF(x, y, w, h);
            var (material, dark) = s_material.Peek();
            var request = new DetachedWindowRequest("Wavee", new Size2(Rules.DefaultW, Rules.DefaultH), new Root(), AlwaysOnTop: s_onTop.Peek(),
                InitialBoundsPx: restored, MinClientSizeDip: new Size2(Rules.MinW, Rules.MinH), NoActivate: true,
                Material: material, MaterialDark: dark, DragAnywhere: true,
                // The form IS the size, so the content follows an edge drag step by step (the engine's default for a composited
                // window holds the old layout until mouse-up); the limits are a floor and a ceiling, nothing else.
                LiveResize: true, MaxClientSizeDip: new Size2(Rules.MaxW, Rules.MaxH));
            var window = hooks.OpenDetachedWindow?.Invoke(request);
            if (window is null)
            {
                Log.Warn(LogCategory, "open refused (no detached window support)");
                IsOpen.Value = false;
                return;
            }
            _window = window;
            s_live = window;
            window.PowerCapFps = Rules.FrameCap(s_faces[ClampFace(s_face.Peek())].Kind);
            window.SetTitle(Title());
            window.OnClosed = () =>
            {
                // The OS closed it (Alt+F4, the taskbar): the full window comes back, exactly as "back to Wavee".
                _window = null;
                s_live = null;
                if (IsOpen.Peek()) IsOpen.Value = false;
                ShowMain();
            };
            window.BoundsChanged = rect => OnBoundsSettled(rect, settling);
            window.OnRevealed = timing =>
            {
                Log.Info(LogCategory, "revealed firstPresentMs=" + timing.FirstPresentMs.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
                // Only now hide the main window: the mini player is on screen first, so there is never a moment with no Wavee window.
                if (IsOpen.Peek()) HideMain();
            };
        }

        /// <summary>The window's rect came to rest after a move or a resize: it stays exactly there, and that is what reopens. The shape
        /// pick's own glide steps are not gestures (its landing persists itself).</summary>
        void OnBoundsSettled(RectF outer, Signal<bool> settling)
        {
            if (_window is not { IsOpen: true } || settling.Peek()) return;
            Persist(outer);
        }

        /// <summary>The shape pick: glide the window to <paramref name="sizeDip"/> (client), keeping its screen-nearest corner.</summary>
        void StartSettle(IDetachedVideoWindow w, Size2 sizeDip, Signal<bool> settling)
        {
            var outer = w.BoundsPx;
            float chromeW = MathF.Max(0f, outer.W - s_clientDip.Width * s_scale), chromeH = MathF.Max(0f, outer.H - s_clientDip.Height * s_scale);
            var px = new Size2(sizeDip.Width * s_scale + chromeW, sizeDip.Height * s_scale + chromeH);
            var work = Win32App.WorkAreaAt(new Point2(outer.X + outer.W * 0.5f, outer.Y + outer.H * 0.5f));
            Begin(outer, Rules.Place(outer, px, work), settling);
        }

        void Begin(RectF from, RectF to, Signal<bool> settling)
        {
            _settleFrom = from;
            _settleTo = to;
            _settleT = 0f;
            if (Motion.ReducedMotion) _settleT = 1f;
            SettleStepCore();
            settling.Value = _settleT < 1f;
        }

        void SettleStep(Signal<bool> settling)
        {
            SettleStepCore();
            if (_settleT >= 1f && settling.Peek()) settling.Value = false;
        }

        void SettleStepCore()
        {
            if (_window is not { IsOpen: true } w) { _settleT = 1f; return; }
            _settleT = MathF.Min(1f, _settleT + Rules.SettleStepMs / Rules.SettleMs);
            w.SetBounds(_settleT >= 1f ? _settleTo : Rules.Lerp(_settleFrom, _settleTo, _settleT));
            if (_settleT >= 1f) Persist(_settleTo);
        }

        static void Persist(RectF r) => Platform.Settings.Set(Platform.Keys.MiniPlayerRect, Video.PlacementPersistence.SaveRect(r.X, r.Y, r.W, r.H));

        void CloseWindow()
        {
            var w = _window;
            _window = null;
            s_live = null;
            if (w is not null)
            {
                // A close mid-glide saves where the glide was going.
                Persist(_settleT < 1f ? _settleTo : w.BoundsPx);
                w.FrameCompleted = null;
                w.OnClosed = null;
                w.BoundsChanged = null;
                w.Close();
            }
            ShowMain();
        }

        void HideMain()
        {
            if (_deepWas < 0) { _deepWas = HiddenMemoryBudget.DeepDelayMs; HiddenMemoryBudget.DeepDelayMs = Rules.DeepAfterMs; }
            FluentApp.SetWindowVisible(false);
            _mainHidden = true;
            // The shell's frame tick (the fetch drain, the palette's grading debounce, the publish cadence) rides the MAIN window's
            // rendered frames, and a hidden main window renders none: a cover first played while the mini player is the only surface
            // was never graded, so "Colour from album art" fell back to the system accent. While the main window is hidden, this
            // window's frames are the clock.
            if (_window is { IsOpen: true } w) w.FrameCompleted = s_shellTick;
            Log.Info(LogCategory, "main window hidden");
        }

        void ShowMain()
        {
            if (_deepWas >= 0) { HiddenMemoryBudget.DeepDelayMs = _deepWas; _deepWas = -1; }
            if (_window is { } w) w.FrameCompleted = null;
            _mainHidden = false;
            Tray.Host.ShowWindow();   // un-hide, restore and bring to the front through the engine seam (the tray's door)
            Log.Info(LogCategory, "main window shown");
        }
    }
}
