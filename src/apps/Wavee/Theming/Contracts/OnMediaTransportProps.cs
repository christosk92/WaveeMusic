// ── Theming/Contracts/OnMediaTransportProps.cs — the props of `Player.OnMediaTransport` (themes plan 6.0, T6b) ───────────────
//
// THE ON-MEDIA TRANSPORT. Every video overlay (the docked card, the mini player, the pop-out, the fullscreen layer: engine chrome
// off) carries ONE strip with the stage's transport language: play, skip, the seek rail, the times, mute, the volume popup and a
// "more" menu. It is revealed and concealed by the surface's own idle machine (`visible`, never hover: the pop-out's tree has no
// pointer handler above the stage), and the pointer sitting on the strip counts as chrome activity (`Feed`).
//
// WHAT STAYED CORE. The facts the strip draws (is it playing, can it skip, is it muted: <see cref="OnMediaFacts"/>), every verb a
// button asks for, the seek rail's and the time labels' core hosts (the scrub controller and the labels' binds, shared with the
// player bar and the stage; they mount `Player.SeekRail` / `Player.TimeLabel`), and the "more" menu's rows: where the video plays, then the aspect, quality and speed cascades are decisions built from
// live reads when the menu opens (`OnMedia.MoreMenu`). The chrome fade's imperative seed is the core's `OnMedia.FadeChrome` (the
// video band shares it).
//
// WVTH006: a sealed record; the shared members are one cached set (`RailWiring.OnMedia`) and a surface adds its own signal and feed.

using FluentGpu.Controls;
using FluentGpu.Controls.Media;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace Wavee.Theming.Contracts;

/// <summary>What the strip draws that playback decides.</summary>
/// <param name="Playing">The player is playing (the primary shows PAUSE).</param>
/// <param name="CanPlay">There is a playable (the primary may be pressed).</param>
/// <param name="PrevEnabled">Previous may be pressed.</param>
/// <param name="NextEnabled">Next may be pressed.</param>
/// <param name="Muted">The player is muted.</param>
public readonly record struct OnMediaFacts(bool Playing, bool CanPlay, bool PrevEnabled, bool NextEnabled, bool Muted);

/// <summary>The props of <see cref="ViewKeys.OnMediaTransport"/>.</summary>
public sealed record OnMediaTransportProps
{
    // ── the surface's own ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The surface's idle machine's "chrome visible" signal.</summary>
    public required IReadSignal<bool> Visible { get; init; }

    /// <summary>The SAME window's chrome feed the video element reports activity to.</summary>
    public required PlayerChromeFeed Feed { get; init; }

    // ── playback's ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The facts the strip draws; call it inside a render (it subscribes playback).</summary>
    public required Func<OnMediaFacts> Read { get; init; }

    /// <summary>The player's volume (the popup's slider mirrors it).</summary>
    public required IReadSignal<float> Volume { get; init; }

    /// <summary>The ONE cell the popup's slider binds: the model's volume mirrored for a slider (its writes go to <see cref="SetVolume"/>).</summary>
    public required FloatSignal VolumeCell { get; init; }

    /// <summary>Previous.</summary>
    public required Action Previous { get; init; }

    /// <summary>Next.</summary>
    public required Action Next { get; init; }

    /// <summary>Play or pause.</summary>
    public required Action TogglePlay { get; init; }

    /// <summary>Flip mute.</summary>
    public required Action ToggleMute { get; init; }

    /// <summary>Set the volume (0..1).</summary>
    public required Action<float> SetVolume { get; init; }

    // ── core pieces the strip places ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The "⋯" rows, built from live reads when the menu opens.</summary>
    public required Func<IReadOnlyList<MenuFlyoutItem>> MoreMenu { get; init; }

    /// <summary>The seek rail's core host over the surface's feed (it mounts <see cref="ViewKeys.SeekRail"/>), live only while <c>reveal</c> holds.</summary>
    public required Func<PlayerChromeFeed, IReadSignal<bool>, Element> SeekBar { get; init; }

    /// <summary>A time label's core host (it mounts <see cref="ViewKeys.TimeLabel"/>): (right-hand remaining slot, the ink, <c>reveal</c>).</summary>
    public required Func<bool, ColorF, IReadSignal<bool>, Element> TimeText { get; init; }

    /// <summary>Seed the eased chrome fade on a captured node: (the hook context, the node, show). Skip the mount run: chrome is born
    /// at its terminal.</summary>
    public required Action<RenderContext, NodeHandle, bool> FadeChrome { get; init; }
}
