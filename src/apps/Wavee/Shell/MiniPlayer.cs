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
// THE SHAPE IS THE WINDOW. No layout setting: the content classifies its own viewport into Strip / Wide / Square / Tall and reflows
// live; when a move or resize gesture ends the window settles on the nearest ratio (keeping roughly the area dragged and the nearest
// screen corner) and snaps to a work-area corner within <see cref="Rules.SnapDip"/>, animated. Never the main window's tier: the shape
// is this window's own.
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
        /// <summary>The smallest client the window allows while dragging (a strip's height, a tall column's width).</summary>
        public const float MinW = 200f, MinH = 48f;
        /// <summary>Within this distance (DIP) of a work-area corner a released window snaps to it, inset by <see cref="EdgeDip"/>.</summary>
        public const float SnapDip = 56f, EdgeDip = 12f;
        /// <summary>The settle animation (window bounds) and its step.</summary>
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

        public static float Ratio(MiniShape s) => s switch { MiniShape.Strip => 6f, MiniShape.Wide => 2.4f, MiniShape.Square => 1f, _ => 9f / 16f };

        public static (float Min, float Max) WidthRange(MiniShape s) => s switch
        {
            MiniShape.Strip => (300f, 520f),
            MiniShape.Wide => (380f, 560f),   // 340 (the prototype's) left a Zune title, the seek and the transport no room at 142 tall
            MiniShape.Square => (260f, 400f),
            _ => (232f, 330f),
        };

        /// <summary>The nearest shape by ratio (log distance: the boundaries are the geometric means of neighbouring ratios).</summary>
        public static MiniShape Classify(float w, float h)
        {
            if (w <= 0f || h <= 0f) return MiniShape.Wide;
            float r = w / h;
            if (r >= MathF.Sqrt(6f * 2.4f)) return MiniShape.Strip;
            if (r >= MathF.Sqrt(2.4f)) return MiniShape.Wide;
            if (r >= MathF.Sqrt(9f / 16f)) return MiniShape.Square;
            return MiniShape.Tall;
        }

        /// <summary>The settled client size for a dragged one: the nearest shape at roughly the same area, inside the shape's range.</summary>
        public static Size2 Settle(float w, float h) => SizeFor(Classify(w, h), w * h);

        /// <summary>A shape at an area (DIP²), its width clamped to the shape's range.</summary>
        public static Size2 SizeFor(MiniShape shape, float area)
        {
            float ratio = Ratio(shape);
            var (min, max) = WidthRange(shape);
            float width = Math.Clamp(MathF.Sqrt(MathF.Max(1f, area) * ratio), min, max);
            return new Size2(MathF.Round(width), MathF.Round(width / ratio));
        }

        /// <summary>Place a window of <paramref name="size"/> (outer px) where <paramref name="from"/> was, keeping the corner of it that is
        /// nearest the work area's matching corner fixed, then snap to a work-area corner within <paramref name="snapPx"/>
        /// (inset <paramref name="edgePx"/>), and keep it inside the work area.</summary>
        public static RectF Place(RectF from, Size2 size, RectF work, float snapPx, float edgePx)
        {
            if (work.IsInfinite || work.W <= 0f) return new RectF(from.X, from.Y, size.Width, size.Height);
            float cx = from.X + from.W * 0.5f, cy = from.Y + from.H * 0.5f;
            bool right = cx > work.X + work.W * 0.5f, bottom = cy > work.Y + work.H * 0.5f;
            float x = right ? from.X + from.W - size.Width : from.X;
            float y = bottom ? from.Y + from.H - size.Height : from.Y;
            float left = work.X + edgePx, top = work.Y + edgePx;
            float rightX = work.X + work.W - edgePx - size.Width, bottomY = work.Y + work.H - edgePx - size.Height;
            bool nearH = right ? MathF.Abs(x - rightX) <= snapPx : MathF.Abs(x - left) <= snapPx;
            bool nearV = bottom ? MathF.Abs(y - bottomY) <= snapPx : MathF.Abs(y - top) <= snapPx;
            if (nearH && nearV) { x = right ? rightX : left; y = bottom ? bottomY : top; }
            x = Math.Clamp(x, work.X, MathF.Max(work.X, work.X + work.W - size.Width));
            y = Math.Clamp(y, work.Y, MathF.Max(work.Y, work.Y + work.H - size.Height));
            return new RectF(MathF.Round(x), MathF.Round(y), size.Width, size.Height);
        }

        /// <summary>One settle step: eased (decelerate) interpolation of the outer rect.</summary>
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
        Element Cover() => Controls.Artwork(r.ArtUrl, r.Width, r.Height, r.Corner, keepWhileHidden: true);
        if (r.Use == MiniFaceUse.Thumb) return Cover();
        switch (f.Kind)
        {
            case MiniFaceKind.Lyrics:
                // The lines are the template's (MiniPlayerProps.Lyrics); the face is what they read over: the cover in a box, and filling the
                // window the cover blurred and darkened, so the lines read on-dark in light and dark alike (the prototype's fc-blur).
                if (r.Use != MiniFaceUse.Fill) return Cover();
                return new BoxEl
                {
                    Width = r.Width, Height = r.Height, ZStack = true, ClipToBounds = true, HitTestVisible = false,
                    Children =
                    [
                        new ImageEl
                        {
                            Source = r.ArtUrl ?? "", Width = r.Width * LyricsBlurScale, Height = r.Height * LyricsBlurScale, Fit = ImageFit.Cover,
                            AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Center, BakedBlur = new BakedBlurSpec(LyricsBlurSigma, 0.5f),
                            KeepWhileHidden = true, Placeholder = LyricsVeil,
                        },
                        new BoxEl { Grow = 1f, AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, Fill = LyricsVeil, HitTestVisible = false },
                    ],
                };
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


    // ══ 3. STATE AND VERBS ═══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The mini player is open (the main window is hidden while it is).</summary>
    public static readonly Signal<bool> IsOpen = new(false);
    static readonly Func<bool> s_lyricsVisible = static () => IsOpen.Value;
    /// <summary>The veil a filling lyrics face reads over (the prototype's blurred cover at half brightness), the blur and its overscan.</summary>
    static readonly ColorF LyricsVeil = new(0f, 0f, 0f, 0.5f);
    const float LyricsBlurSigma = 22f, LyricsBlurScale = 1.3f;
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
    static Size2 s_clientDip;   // the mini window's last viewport (DIP), for the settle's chrome arithmetic
    static IDetachedVideoWindow? s_live;   // the open mini window (its work area caps an escaping menu)
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

    static void StepFace(int delta) => SetFace(((s_face.Peek() < 0 ? 0 : s_face.Peek()) + delta + s_faces.Length) % s_faces.Length);

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
        TimeLabel = static (remaining, ink) => Shell.TimeText(remaining, ink),
        ArtistText = ArtistText,
        DetailText = DetailText,
        StepFace = StepFace,
        SetFace = SetFace,
        BackToWavee = static () => Close("back"),
        OnTop = s_onTop,
        ToggleOnTop = ToggleOnTop,
        SetShape = static shape => s_shapeRequest.Value = (shape, s_shapeRequest.Peek().Seq + 1),
        WheelVolume = WheelVolume,
        UseMaterial = static (m, dark) => s_material.Value = (m, dark),
        ArtColour = ArtColour,
        SetArtColour = SetArtColour,
        AlbumAccent = AlbumAccent,
        SeekRail = static draw => Shell.SeekBarDrawn(draw),
        Lyrics = static draw => Lyrics.Mini(draw, s_lyricsVisible),
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
                        Margin = new Edges4(0f, 0f, Rules.EdgeDip, Rules.EdgeDip),
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
            return ViewSlots.View(ViewKeys.MiniPlayer, Props);
        }
    }

    /// <summary>The viewport → shape fold (outside the core host: a size compared with the shape boundaries is a rule, not a look).</summary>
    static void Track(Size2 viewport, float scale)
    {
        if (viewport.Width <= 0f || viewport.Height <= 0f) return;
        s_clientDip = viewport;
        s_scale = scale > 0f ? scale : 1f;
        var shape = Rules.Classify(viewport.Width, viewport.Height);
        if (s_shape.Peek() != shape) s_shape.Value = shape;
        bool roomy = Rules.Roomy(viewport.Width, viewport.Height);
        if (s_roomy.Peek() != roomy) s_roomy.Value = roomy;
    }

    // ══ 5. THE WINDOW'S OWNER ════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>THE single owner of the mini window's lifecycle, mounted once in the shell's top layer; renders nothing. It opens the window
    /// on the <see cref="IsOpen"/> edge (posted out of the reactive flush), hides the main window, persists the rect, follows keep-on-top
    /// and the theme's material, settles and snaps the window when a gesture ends, and holds the global hot key.</summary>
    public sealed class Owner : Component
    {
        static readonly Action<FrameStats> s_shellTick = Shell.FrameTick;
        IDetachedVideoWindow? _window;
        bool _openQueued, _unmounted, _ownBounds;
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
            var settling = UseSignal(false);
            UseInterval(() => SettleStep(settling), Rules.SettleStepMs, enabled: settling.Value);

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
                Material: material, MaterialDark: dark, DragAnywhere: true);
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

        void OnBoundsSettled(RectF outer, Signal<bool> settling)
        {
            if (_window is not { IsOpen: true } w) return;
            if (_ownBounds) { _ownBounds = settling.Peek(); Persist(outer); return; }   // our own settle step landed
            float chromeW = MathF.Max(0f, outer.W - s_clientDip.Width * s_scale), chromeH = MathF.Max(0f, outer.H - s_clientDip.Height * s_scale);
            float cw = (outer.W - chromeW) / s_scale, ch = (outer.H - chromeH) / s_scale;
            var size = Rules.Settle(cw, ch);
            var target = Target(outer, size, chromeW, chromeH);
            if (MathF.Abs(target.X - outer.X) < 1f && MathF.Abs(target.Y - outer.Y) < 1f && MathF.Abs(target.W - outer.W) < 1f && MathF.Abs(target.H - outer.H) < 1f)
            {
                Persist(outer);
                return;
            }
            Begin(outer, target, settling);
        }

        void StartSettle(IDetachedVideoWindow w, Size2 sizeDip, Signal<bool> settling)
        {
            var outer = w.BoundsPx;
            float chromeW = MathF.Max(0f, outer.W - s_clientDip.Width * s_scale), chromeH = MathF.Max(0f, outer.H - s_clientDip.Height * s_scale);
            Begin(outer, Target(outer, sizeDip, chromeW, chromeH), settling);
        }

        RectF Target(RectF outer, Size2 sizeDip, float chromeW, float chromeH)
        {
            var px = new Size2(sizeDip.Width * s_scale + chromeW, sizeDip.Height * s_scale + chromeH);
            var work = Win32App.WorkAreaAt(new Point2(outer.X + outer.W * 0.5f, outer.Y + outer.H * 0.5f));
            return Rules.Place(outer, px, work, Rules.SnapDip * s_scale, Rules.EdgeDip * s_scale);
        }

        void Begin(RectF from, RectF to, Signal<bool> settling)
        {
            _settleFrom = from;
            _settleTo = to;
            _settleT = 0f;
            _ownBounds = true;
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
                Persist(w.BoundsPx);
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
