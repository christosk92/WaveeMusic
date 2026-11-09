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
/// Built by the template, answered by the core (<see cref="MiniPlayerProps.FaceView"/>).</summary>
public readonly record struct MiniFaceRequest(int Face, float Width, float Height, string? ArtUrl, float Corner, MiniFaceUse Use);

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
    /// <summary>A wheel over the player: volume, in detents (positive = louder).</summary>
    public required Action<float> WheelVolume { get; init; }
    /// <summary>The template's choice of window material for the active theme and mode, applied by the window owner.</summary>
    public required Action<WindowMaterial, bool> UseMaterial { get; init; }
}

/// <summary>The theme's look for the mini player (<c>StyleKeys.MiniPlayer</c>): the numbers come from the theme's Rules, the text
/// from its roles. Every theme registers one; the picture is the Common template's.</summary>
public sealed record MiniPlayerStyle
{
    /// <summary>The window's own backdrop in light / dark (WinUI: acrylic; Zune and Classic: none, an opaque ground).</summary>
    public required WindowMaterial MaterialLight { get; init; }
    public required WindowMaterial MaterialDark { get; init; }
    /// <summary>The window's ground (translucent over a backdrop, opaque otherwise).</summary>
    public required Func<ColorF> Ground { get; init; }
    /// <summary>The art box's corner radius.</summary>
    public required float ArtCorner { get; init; }
    /// <summary>A transport / save button's box (40 DIP or more) and its glyph.</summary>
    public required float ButtonBox { get; init; }
    public required float ButtonGlyph { get; init; }
    /// <summary>The play button's box and glyph.</summary>
    public required float PlayBox { get; init; }
    public required float PlayGlyph { get; init; }
    /// <summary>A hover-only window button (options, back, face flip): 32 DIP, like caption buttons.</summary>
    public required float ChromeBox { get; init; }
    public required float ChromeGlyph { get; init; }
    /// <summary>A button's plate corner (Zune square, Classic and WinUI round).</summary>
    public required float ButtonCorner { get; init; }
    /// <summary>The play button is an accent disc (all three themes) with this corner.</summary>
    public required float PlayCorner { get; init; }
    /// <summary>Paddings and gaps per shape.</summary>
    public required float Pad { get; init; }
    public required float Gap { get; init; }
    public required float StripPadStart { get; init; }
    public required float StripThumb { get; init; }
    public required float StripGap { get; init; }
    /// <summary>The resting progress hairline and its hover thickness.</summary>
    public required float Hairline { get; init; }
    public required float HairlineHot { get; init; }
    /// <summary>The play-state badge's side.</summary>
    public required float Badge { get; init; }
    /// <summary>The title per shape: the theme's type (Zune's is large, light and lowercase, and bleeds rather than shrinks).</summary>
    public required Func<string, MiniShape, TextEl> Title { get; init; }
    /// <summary>The artist line (accent in Zune and Classic).</summary>
    public required Func<string, TextEl> Artist { get; init; }
    /// <summary>The detail line and the times.</summary>
    public required Func<string, TextEl> Detail { get; init; }
    /// <summary>The face name in the picker.</summary>
    public required Func<string, TextEl> FaceLabel { get; init; }
    /// <summary>The hover overlay's veil over a filling face (Square, Tall immersive).</summary>
    public required Func<ColorF> Veil { get; init; }
    /// <summary>The fade under the text over a filling face.</summary>
    public required Func<ColorF> Shade { get; init; }
    /// <summary>The chrome plate (the hover window buttons' pill).</summary>
    public required Func<ColorF> ChromePlate { get; init; }
    /// <summary>The hover in / out timing (120 ms in, 600 ms out in the prototype).</summary>
    public required float HoverInMs { get; init; }
    public required float HoverOutDelayMs { get; init; }
}
