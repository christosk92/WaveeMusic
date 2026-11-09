// ── Theming/Contracts/MiniPlayerProps.cs — the mini player's contract (#197) ─────────────────────────────────────────────
//
// The always-on-top mini player window draws `Player.Mini` (ViewKeys.Player.cs) over the player bar's own core (`Bar`: its facts,
// verbs, seek rail, time labels, device picker and volume cell) plus what only the mini player has: the shape the window is in,
// the face that fills its art and the window verbs. The window decides the shape from its own size (never the main window's
// tier), the core builds the face (cover, lyrics, Canvas, a player style), the theme draws the rest (`MiniPlayerStyle`).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Pal;
using FluentGpu.Signals;
using FluentGpu.Theming;

namespace Wavee.Theming.Contracts;

/// <summary>The window's four shapes. The window settles on the nearest of them when a resize ends; the content follows the live
/// size meanwhile. Values are not persisted (the rect is).</summary>
public enum MiniShape : byte
{
    /// <summary>6:1, a line over the taskbar: thumb, title or lyric, controls on hover.</summary>
    Strip,
    /// <summary>12:5, the default card: every core control at rest.</summary>
    Wide,
    /// <summary>1:1: the face fills the window, controls on hover.</summary>
    Square,
    /// <summary>9:16: the face above (or behind) the full transport.</summary>
    Tall,
}

/// <summary>What kind of picture a face is: decides whether it sits in the art box or fills the window behind the controls.</summary>
public enum MiniFaceKind : byte
{
    /// <summary>The album art.</summary>
    Cover,
    /// <summary>Synced lyrics: a line pair in a strip, a scrolling column elsewhere.</summary>
    Lyrics,
    /// <summary>The track's looping 9:16 video (falls back to the cover).</summary>
    Canvas,
    /// <summary>The analyser face.</summary>
    Visualizer,
    /// <summary>A physical player style (vinyl, CD, cassette, reel): an object on a ground.</summary>
    Object,
}

/// <summary>Where a face is drawn: the strip's thumbnail, the art box, the whole window behind the controls, or (lyrics only) the
/// strip's line pair in place of the title.</summary>
public enum MiniFaceUse : byte { Thumb, Box, Fill, Line }

/// <summary>One face request: which face, the box it fills (DIP), the playing cover, the box's corner radius and where it sits.
/// Built by the template, answered by the core (<see cref="MiniPlayerProps.FaceView"/>). <paramref name="Paint"/> draws the analyser face in
/// the theme's own way (the core feeds it the full player's bands); null keeps the full player's picture.</summary>
public readonly record struct MiniFaceRequest(int Face, float Width, float Height, string? ArtUrl, float Corner, MiniFaceUse Use,
    Func<MiniVisualizerFrame, Element>? Paint = null);

/// <summary>What a theme's visualizer draws from: its box and the full player's analyser bands (0..1, the deck clock writes them at a
/// perceptual quantum; silent audio leaves them at rest). <paramref name="Running"/> is true while the face may loop (playing, shown).</summary>
public sealed record MiniVisualizerFrame(float Width, float Height, FloatSignal[] Bands, FloatSignal[] Peaks, IReadSignal<bool> Running);

/// <summary>What the lyrics face can show.</summary>
public enum MiniLyricsState : byte
{
    /// <summary>Nothing playing, or the document is not here yet.</summary>
    None,
    /// <summary>The song has no words (an instrumental, or a document with no lines).</summary>
    Instrumental,
    /// <summary>Words without timing: the face cannot follow them.</summary>
    Unsynced,
    /// <summary>Synced lines: <see cref="MiniLyricsFrame.Active"/> follows the playhead.</summary>
    Lines,
}

/// <summary>One render of the lyrics face's core host: the lines and the active one (a signal the drawer reads where it re-renders; it moves
/// once per line, never per frame).</summary>
public sealed record MiniLyricsFrame(MiniLyricsState State, IReadOnlyList<string> Lines, IReadSignal<int> Active);

/// <summary>The mini player's props. The core host builds ONE instance per window (verbs and signals are stable), so the template
/// re-runs on the signals it reads, never on a fresh record.</summary>
public sealed record MiniPlayerProps
{
    /// <summary>The player bar's core: facts (<c>Bar.Host.Use</c>), verbs, seek rail, time labels, device picker, volume.</summary>
    public required PlayerBarProps Bar { get; init; }
    /// <summary>The shape the window is in now (from its own size).</summary>
    public required IReadSignal<MiniShape> Shape { get; init; }
    /// <summary>The face index (into <see cref="FaceCount"/>).</summary>
    public required IReadSignal<int> Face { get; init; }
    /// <summary>How many faces there are; the order is the core's.</summary>
    public required int FaceCount { get; init; }
    /// <summary>A face's kind (decides where it sits).</summary>
    public required Func<int, MiniFaceKind> FaceKind { get; init; }
    /// <summary>A face's display name (localised).</summary>
    public required Func<int, string> FaceName { get; init; }
    /// <summary>Builds a face for a box (the core's: deck faces, Canvas, lyrics, the cover).</summary>
    public required Func<MiniFaceRequest, Element> FaceView { get; init; }
    /// <summary>A transport time label (the right one: remaining or duration), in an ink (null: the theme's; on-media white over a face).</summary>
    public required Func<bool, ColorF?, Element> TimeLabel { get; init; }
    /// <summary>The artist line of the playing item.</summary>
    public required Func<string> ArtistText { get; init; }
    /// <summary>The third line (album · year, or "Playing on …" when another device plays).</summary>
    public required Func<string> DetailText { get; init; }
    /// <summary>Step the face by ±1 (the ‹ › flip).</summary>
    public required Action<int> StepFace { get; init; }
    /// <summary>Pick a face.</summary>
    public required Action<int> SetFace { get; init; }
    /// <summary>Back to the full window (the mini player closes; the music never stops).</summary>
    public required Action BackToWavee { get; init; }
    /// <summary>Keep on top (the switch in Options).</summary>
    public required IReadSignal<bool> OnTop { get; init; }
    /// <summary>Toggle keep on top.</summary>
    public required Action ToggleOnTop { get; init; }
    /// <summary>Resize the window to a shape (Options), keeping its nearest corner.</summary>
    public required Action<MiniShape> SetShape { get; init; }
    /// <summary>A wheel over the player: volume, in the engine's detents (positive toward the content's end: a notch down is quieter).</summary>
    public required Action<float> WheelVolume { get; init; }
    /// <summary>The template's choice of window material for the active theme and mode, applied by the window owner.</summary>
    public required Action<WindowMaterial, bool> UseMaterial { get; init; }

    /// <summary>"Colour from album art" for a theme (the style's <see cref="MiniPlayerStyle.ArtColourSlot"/>), given that theme's default.
    /// A tracked read.</summary>
    public required Func<int, bool, bool> ArtColour { get; init; }
    /// <summary>Set "Colour from album art" for a theme slot.</summary>
    public required Action<int, bool> SetArtColour { get; init; }
    /// <summary>A cover's accent, floored for text (null until its grading lands, or for no cover). A tracked read: a late grading re-runs it.</summary>
    public required Func<string?, ColorF?> AlbumAccent { get; init; }
    /// <summary>The seek rail's core host (the scrub, the pixel dwell, the commit) drawn by the template instead of the theme's player rail.</summary>
    public required Func<Func<SeekRailProps, Element>, Element> SeekRail { get; init; }
    /// <summary>The strip's lyric ticker's core host: it reads the one lyrics document, ticks the active line while the mini player shows, and
    /// draws through the template's callback.</summary>
    public required Func<Func<MiniLyricsFrame, Element>, Element> Lyrics { get; init; }
    /// <summary>The lyrics face's window (wide, square, tall): the app's own lyrics view (the side pane's rows, ink, wipe and follow, in the
    /// theme's style), ticking while the mini player shows.</summary>
    public required Func<MiniLyricsView, Element> LyricsView { get; init; }
    /// <summary>Open the player bar's own device picker over an anchor, or close it when it is open: the bar's toggle, escaping this small
    /// window into a popup over the screen.</summary>
    public required Action<IOverlayService, Ref<NodeHandle>, Ref<OverlayHandle?>> ToggleDevicePicker { get; init; }
    /// <summary>The save heart can act: something is playing that can be saved (the bar's own gate also folds in its tier's slot, which is the
    /// main window's, not this one's).</summary>
    public required Func<bool> CanSave { get; init; }
    /// <summary>Bumps on every wheel step over the player (the volume HUD shows for a moment).</summary>
    public required IReadSignal<int> VolumeHud { get; init; }
    /// <summary>The tall column has room for the volume row (a size rule of the window, decided by the core).</summary>
    public required IReadSignal<bool> Roomy { get; init; }
}

/// <summary>How a theme draws the save heart.</summary>
public enum MiniHeart : byte
{
    /// <summary>A plain glyph button; saved, the glyph fills in the accent.</summary>
    Plain,
    /// <summary>Zune: a ring around the glyph in the button's ink.</summary>
    Ring,
    /// <summary>Classic: an album-tinted plate under the accent glyph.</summary>
    Plate,
}

/// <summary>One seek rail for a theme to draw: the core host's binds (<see cref="SeekRailProps"/>), the accent, the ink (on-dark over
/// imagery), whether the music moves (Classic's line ripples only then), whether it is the window's edge line (a hairline at rest that
/// thickens into a seek bar while the controls show) and whether the controls show.</summary>
public readonly record struct MiniSeekRequest(SeekRailProps Rail, ColorF Accent, bool OnDark, bool Playing, bool Edge, bool Hot);

/// <summary>Which of the strip ticker's two lines is drawn: the current line or the next.</summary>
public enum MiniLyricUse : byte { TickerNow, TickerNext }

/// <summary>One mount of the app's lyrics view in the mini window: over imagery (the stage's ink) or on the window's ground, its type as a
/// share of the side pane's, where the active line is held (its centre at <paramref name="Band"/> of the box, or, with
/// <paramref name="TopRoom"/> of 0 or more, its top that far below the box's top, so no earlier line shows above it) and the gutter. Over
/// imagery a style that sings in the accent (Zune) sings in <paramref name="Accent"/> (the mini's own, lifted for the dark ground).</summary>
public readonly record struct MiniLyricsView(bool OnMedia, float TypeScale, float Band, float Gutter, float TopRoom = -1f, ColorF Accent = default);

/// <summary>One lyric line for a theme to draw.</summary>
public readonly record struct MiniLyricLine(string Text, bool Active, MiniLyricUse Use, MiniShape Shape, ColorF Accent, bool OnDark);

/// <summary>The theme's look for the mini player (<c>StyleKeys.MiniPlayer</c>): the numbers come from the theme's Rules, the text
/// from its roles, the distinctive parts (seek line, visualizer, lyric line) from the theme's own builders. Every theme registers one;
/// the layout of the four shapes is the Common template's.</summary>
public sealed record MiniPlayerStyle
{
    /// <summary>The window's own backdrop in light / dark (WinUI: acrylic; Zune and Classic: none, an opaque ground).</summary>
    public required WindowMaterial MaterialLight { get; init; }
    public required WindowMaterial MaterialDark { get; init; }
    /// <summary>The theme's slot for the per-theme "Colour from album art" switch, and its default (Classic on, as in the app).</summary>
    public required int ArtColourSlot { get; init; }
    public required bool ArtColourDefault { get; init; }
    /// <summary>The window's ground for an accent (translucent over a backdrop, opaque otherwise; Classic: the album wash).</summary>
    public required Func<ColorF, ColorF> Ground { get; init; }
    /// <summary>A glow of the accent from the window's top-right corner over the ground (Zune's dark card), or transparent for none.</summary>
    public required Func<ColorF, ColorF> Glow { get; init; }
    /// <summary>The same ground, opaque: under the hover overlays and fades, and the flyouts' plate.</summary>
    public required Func<ColorF, ColorF> Solid { get; init; }
    /// <summary>A soft accent plate (Classic: the heart's plate) and a stronger one (the active lyric line, the faces' glow).</summary>
    public required Func<ColorF, ColorF> Tint { get; init; }
    public required Func<ColorF, ColorF> Tint2 { get; init; }
    /// <summary>The flyouts' corner (WinUI 8, Zune 0, Classic 14).</summary>
    public required float Radius { get; init; }
    /// <summary>The art box's corner radius, and the strip thumb's.</summary>
    public required float ArtCorner { get; init; }
    public required float ThumbCorner { get; init; }
    /// <summary>A transport / save button's box (40 DIP or more) and its glyph.</summary>
    public required float ButtonBox { get; init; }
    public required float ButtonGlyph { get; init; }
    /// <summary>The play button's box and glyph, and the bigger one of the square and the tall column.</summary>
    public required float PlayBox { get; init; }
    public required float PlayBigBox { get; init; }
    public required float PlayGlyph { get; init; }
    /// <summary>A hover-only window button (options, back, face flip): 32 DIP, like caption buttons.</summary>
    public required float ChromeBox { get; init; }
    public required float ChromeGlyph { get; init; }
    /// <summary>A button's plate corner, the play disc's, the chrome pill's and the badge's.</summary>
    public required float ButtonCorner { get; init; }
    public required float PlayCorner { get; init; }
    public required float ChromeCorner { get; init; }
    public required float BadgeCorner { get; init; }
    /// <summary>The save heart's look.</summary>
    public required MiniHeart Heart { get; init; }
    /// <summary>Paddings and gaps per shape.</summary>
    public required float Pad { get; init; }
    public required float Gap { get; init; }
    public required float StripPadStart { get; init; }
    public required float StripThumb { get; init; }
    public required float StripGap { get; init; }
    /// <summary>The play-state badge's side.</summary>
    public required float Badge { get; init; }
    /// <summary>The title per shape: the theme's type. A bleeding title (Zune) is never shrunk or ellipsized: it runs past its column under a
    /// fade (<see cref="TitleFade"/> DIP) instead.</summary>
    public required Func<string, MiniShape, TextEl> Title { get; init; }
    public required bool TitleBleeds { get; init; }
    public required float TitleFade { get; init; }
    /// <summary>The artist line for an accent, on the theme's ground or on-dark over imagery (Zune and Classic: the accent; WinUI: the
    /// secondary ink).</summary>
    public required Func<string, ColorF, bool, TextEl> Artist { get; init; }
    /// <summary>The detail line and the times.</summary>
    public required Func<string, TextEl> Detail { get; init; }
    /// <summary>The artist written big across the square's cover at a window width (Zune), or null.</summary>
    public required Func<string, float, TextEl>? CoverBleed { get; init; }
    /// <summary>Classic's ground band: the square's cover stands on a band of the window's ground (a straight join, never a wave), and a
    /// tinted band closing on a slow swell stands behind the tall column's object faces.</summary>
    public required bool GroundBand { get; init; }
    /// <summary>A label (flyout rows, the face name, section heads).</summary>
    public required Func<string, TextEl> Label { get; init; }
    /// <summary>A small caption in the theme's voice (the flyouts' section heads and tile names: lowercase in Zune).</summary>
    public required Func<string, TextEl> Small { get; init; }
    /// <summary>The flyout's heading ("Mini player", "Play on").</summary>
    public required Func<string, TextEl> Heading { get; init; }
    /// <summary>The theme's seek rail and edge line.</summary>
    public required Func<MiniSeekRequest, Element> SeekRail { get; init; }
    /// <summary>The theme's visualizer over the full player's bands, in an accent.</summary>
    public required Func<MiniVisualizerFrame, ColorF, Element> Visualizer { get; init; }
    /// <summary>The strip ticker's start bar (WinUI 2, Classic 3, Zune none) and its two lines in the theme's type.</summary>
    public required float TickerBar { get; init; }
    public required Func<MiniLyricLine, Element> LyricLine { get; init; }
    /// <summary>The lyric windows' type per shape, as a share of the app's own lyrics view's (the view scales its rows by it).</summary>
    public required Func<MiniShape, float> LyricScale { get; init; }
    /// <summary>The fade under the text over a filling face, and the hover veil over it.</summary>
    public required Func<ColorF> Veil { get; init; }
    public required Func<ColorF> Shade { get; init; }
    /// <summary>The hover in / out timing (120 ms in, 600 ms out in the prototype).</summary>
    public required float HoverInMs { get; init; }
    /// <summary>The wide card's art-box height from which its column has room for the third line (album, or "Playing on") above the seek and
    /// transport rows: the theme's type decides (Zune's large title takes more).</summary>
    public required float WideDetailFrom { get; init; }
    public required float HoverOutDelayMs { get; init; }
}
