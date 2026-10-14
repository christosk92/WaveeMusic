// ── Theming/Contracts/StageProps.cs — the props of the stage's frame views (themes plan 6.0, T7a1) ────────────────────────────
//
// THE STAGE IS A CORE SURFACE AND A THEME PICTURE. `Stage.SurfaceCore` (Shell\Stage.UI.cs) keeps every hook, effect and state
// machine of the fullscreen stage: the window and the focus scope (`ImmersiveService`), the idle machine and the cursor, the
// ONE layout signal and its aspect classes (compact / portrait / ultrawide with their hysteresis, `StagePolicy`), the preferences
// mirrored into `Stage.StageCtx`, the accent and palette, the key map (Escape / F11 / [ / ] / G), the options popover's open and
// close, the TeachingTip. What it does NOT own is the picture of the frame: it hands the structure a `StageRootView` and the
// structure's `Stage.Root` template lays the layers out.
//
//   `Stage.Root`       StageRootProps          the layer stack, the chrome's placement and its Enter/Exit, the hairline
//   `Stage.TopBar`     StageTopBarProps        brand, the mode selector, the options and exit buttons
//   `Stage.Transport`  StageTransportProps     the seek row and the transport card's clusters
//   `Stage.Gallery`    StageGalleryProps       the visualizer gallery pane
//   `Stage.Options`    StageOptionsProps       the options popover (it mounts in the overlay layer, outside the stage subtree)
//   `Stage.Caption`    StageCaptionProps       the lyric caption (its own file)
//   `Stage.Backdrop` ... `Stage.ArtistPane`   the layout layers (T7a2: `StageLayerProps.cs`)
//
// WHY THE ROOT IS MOUNTED THROUGH `Mount`. The surface is a component with hooks that must run in a fixed order whatever theme
// is active, and its tree is the structure's: so the frame mounts the key, the template calls `props.Mount(Draw)` and the core
// component hands `Draw` the view it decided on each render. `Draw` is a static method: the delegate is cached, so a re-run of
// the template with the same registry passes an equal argument and re-renders nothing; another structure's `Draw` re-renders the
// surface in place (the window and the focus scope belong to `ImmersiveService`, not to the component, so a remount never leaves
// fullscreen).
//
// The stage's state is the ambient signal bag `Stage.StageCtx`: the views read its signals (mode, layout, accent, ...) and call
// its verbs (`SetMode`, `OpenOptions`, `Exit`, `OverControls`, `MenuOpen`, ...). It is a core object that names no `Shell` type,
// so a theme may carry it; the preference writes and the playback reads that Wavee's themes may not name travel as verbs and facts.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee.Theming.Contracts;

/// <summary>The props of <see cref="ViewKeys.StageRoot"/>: how to mount the core surface with the structure's picture.</summary>
public sealed record StageRootProps
{
    /// <summary>Mount the core surface; it calls <c>draw</c> with each render's <see cref="StageRootView"/> and the structure answers the tree.</summary>
    public required Func<Func<StageRootView, Element>, Element> Mount { get; init; }
}

/// <summary>What the core surface decided for one render of the stage: the size, the behaviour it wired onto the root, the predicates of
/// the layers that come and go, the props of the chrome's views, and the builders of the layers other T-PRs own.</summary>
public sealed record StageRootView
{
    /// <summary>The ambient signal bag (the stage's life: one instance).</summary>
    public required Stage.StageCtx Stage { get; init; }

    /// <summary>The stage's size in DIP (the allocator's <c>W</c> × <c>H</c>).</summary>
    public required float Width { get; init; }

    public required float Height { get; init; }

    // ── behaviour the surface wires onto the root box: the template only PLACES it ───────────────────────────────────

    public required Action<NodeHandle> OnRealized { get; init; }

    public required Action<Point2> OnHoverMove { get; init; }

    public required Action OnPointerExit { get; init; }

    public required Action<WheelEventArgs> OnPointerWheel { get; init; }

    /// <summary>Escape / F11 close, [ / ] step the face, G the gallery, the rest the player bar's key map; any key is idle activity.</summary>
    public required Action<KeyEventArgs> OnKeyDown { get; init; }

    /// <summary>Never leave focus null while the stage is up.</summary>
    public required Action<bool> OnFocusChanged { get; init; }

    // ── predicates: which layers are mounted (decisions of the layout axis) ──────────────────────────────────────────

    /// <summary>The Artist layout's photo and scrims are up.</summary>
    public required Func<bool> ShowHeroLayer { get; init; }

    /// <summary>A visualizer face is drawn (Visualizer mode, a layout that has a face).</summary>
    public required Func<bool> ShowFace { get; init; }

    /// <summary>The bottom smoke under the face (Visualizer mode in the Card layout).</summary>
    public required Func<bool> ShowSmoke { get; init; }

    /// <summary>The Artist layout's big name layer (no lyrics over the photo).</summary>
    public required Func<bool> ShowArtistName { get; init; }

    /// <summary>The lyric caption is up.</summary>
    public required Func<bool> ShowCaption { get; init; }

    /// <summary>The idle machine's output: the controls are mounted.</summary>
    public required Func<bool> ChromeVisible { get; init; }

    /// <summary>The gallery pane is mounted (Visualizer mode, open, with room).</summary>
    public required Func<bool> ShowGallery { get; init; }

    /// <summary>The artist-header lookups run (the Artist layout is the stored choice in Visualizer mode).</summary>
    public required Func<bool> ShowHeroFacts { get; init; }

    /// <summary>The playhead as a 0..1 fraction (the hairline shown while the chrome rests); read inside a bound channel.</summary>
    public required Func<float> HairlineFraction { get; init; }

    // ── the chrome's views ───────────────────────────────────────────────────────────────────────────────────────────

    public required StageTopBarProps TopBar { get; init; }

    public required StageTransportProps Transport { get; init; }

    public required StageGalleryProps Gallery { get; init; }

    // ── the layout layers' views (T7a2) ──────────────────────────────────────────────────────────────────────────────

    public required StageBackdropProps Backdrop { get; init; }

    /// <summary>The Artist layout's photo and scrims (mounted under <see cref="ShowHeroLayer"/>).</summary>
    public required StageHeroProps Hero { get; init; }

    public required StageLayerProps Face { get; init; }

    public required StageFaceScrimProps FaceScrim { get; init; }

    public required StageLayerProps Spectrum { get; init; }

    public required StageNowPlayingCardProps NowPlayingCard { get; init; }

    /// <summary>The morphing cover and its title column.</summary>
    public required StageIdentityProps Identity { get; init; }

    /// <summary>The Artist layout's big name (mounted under <see cref="ShowArtistName"/>).</summary>
    public required StageArtistNameProps ArtistName { get; init; }

    /// <summary>The core layers, as builders: the pane host (it mounts the lyrics pane, `Queue.StagePane` and <see cref="ViewKeys.StageArtistPane"/>), the caption host and the zero-size machinery.</summary>
    public required StageLayers Layers { get; init; }
}

/// <summary>Element builders of the stage's layers that are not the layout layers' (those are views since T7a2: backdrop, hero, face, scrim,
/// spectrum, card, identity, artist name). Each is a cached static over the core component; the root places them (and keys them) at the
/// position the stack has always had.</summary>
public sealed record StageLayers
{
    /// <summary>The pane region (lyrics, up next; the Artist pane inside it is <see cref="ViewKeys.StageArtistPane"/>).</summary>
    public required Func<Element> Pane { get; init; }

    /// <summary>The caption's core host (it mounts <see cref="ViewKeys.StageCaption"/>).</summary>
    public required Func<Element> Caption { get; init; }

    /// <summary>The zero-size machinery: it writes <c>HasTimedLyrics</c> and ensures the playing track's lyrics.</summary>
    public required Func<Element> LyricFacts { get; init; }

    /// <summary>The zero-size machinery: the artist-header lookups.</summary>
    public required Func<Element> HeroFacts { get; init; }

    /// <summary>The visualizer clock (zero-size).</summary>
    public required Func<Element> Clock { get; init; }
}

/// <summary>The props of <see cref="ViewKeys.StagePane"/>: the pane region (Lyrics, Up next, Artist) the core pane host mounts. The template
/// draws the region (its rect from <c>Stage.Layout.PaneRect</c>, its move, its clip and when it shows) and the frame each pane's body stands in;
/// WHICH pane is shown and the KeepAlive that keeps each one's state across mode switches are the core's (<see cref="Panes"/>). Built once per
/// stage context, so an equal record re-renders nothing: the template re-renders on the context's signals it reads.</summary>
public sealed record StagePaneProps
{
    /// <summary>The ambient signal bag (the layout, the look, the shown pane).</summary>
    public required Stage.StageCtx Stage { get; init; }

    /// <summary>The core's KeepAlive over the shown pane, keyed <c>pane:&lt;kind&gt;</c>: it wraps each pane's body in <c>frame</c> (the theme's
    /// frame: its entrance and exit) and mounts <c>empty</c> for no pane. Call it from the template's render and place what comes back.</summary>
    public required Func<Func<Element, Element>, Func<Element>, Element> Panes { get; init; }
}

/// <summary>The props of <see cref="ViewKeys.StageTopBar"/>.</summary>
public sealed record StageTopBarProps
{
    public required Stage.StageCtx Stage { get; init; }
}

/// <summary>The props of <see cref="ViewKeys.StageTransport"/>. The facts are read inside the template's render (a signal read in
/// <see cref="Facts"/> subscribes the card), the rest are verbs and the core pieces the player bar shares (the seek rail, the time labels).</summary>
public sealed record StageTransportProps
{
    public required Stage.StageCtx Stage { get; init; }

    /// <summary>Everything the card draws that is playback state; the call reads the signals, so the card re-renders on them.</summary>
    public required Func<StageTransportFacts> Facts { get; init; }

    /// <summary>TogglePlay, Previous, Next, ToggleShuffle, CycleRepeat.</summary>
    public required IPlayerVerbs Verbs { get; init; }

    /// <summary>The bar's own seek rail (the core host that mounts <see cref="ViewKeys.SeekRail"/>); the argument is the scrub edge that holds the chrome.</summary>
    public required Func<Action<bool>, Element> SeekBar { get; init; }

    /// <summary>The bar's elapsed / remaining time label (the core host that mounts <see cref="ViewKeys.TimeLabel"/>) in the given ink.</summary>
    public required Func<bool, ColorF, Element> TimeText { get; init; }

    /// <summary>Is playback muted (read inside the mute button's own render).</summary>
    public required Func<bool> Muted { get; init; }

    public required Action ToggleMute { get; init; }

    /// <summary>The playback volume 0..1 (read inside the volume slider's own effect).</summary>
    public required Func<float> Volume { get; init; }

    public required Action<float> SetVolume { get; init; }

    /// <summary>The output device button's glyph and name (read inside the button's own render).</summary>
    public required Func<StageDeviceFacts> Device { get; init; }

    /// <summary>The device picker's rows, built when it opens.</summary>
    public required Func<List<MenuFlyoutItem>> DeviceItems { get; init; }

    /// <summary>The device picker was opened (a demand for the device list).</summary>
    public required Action OpenDevicePicker { get; init; }
}

/// <summary>What the transport card draws that is playback state.</summary>
/// <param name="Playing">Playing (the disc shows pause).</param>
/// <param name="PrimaryEnabled">There is a track and it is not loading.</param>
/// <param name="CanTransport">Shuffle and repeat are armable.</param>
/// <param name="PrevEnabled">Previous is armed.</param>
/// <param name="NextEnabled">Next is armed.</param>
/// <param name="Shuffle">Shuffle is on.</param>
/// <param name="RepeatOn">Repeat is on (context or track).</param>
/// <param name="RepeatOne">Repeat is the single track.</param>
/// <param name="Saved">The playing track is saved.</param>
/// <param name="Like">The heart's verb (null: nothing to save).</param>
/// <param name="PlayingFrom">The "Playing from X" label, empty with no source.</param>
/// <param name="Source">The source's name, empty with no source.</param>
public readonly record struct StageTransportFacts(bool Playing, bool PrimaryEnabled, bool CanTransport, bool PrevEnabled, bool NextEnabled,
    bool Shuffle, bool RepeatOn, bool RepeatOne, bool Saved, Action? Like, string PlayingFrom, string Source);

/// <summary>The output device button's face.</summary>
public readonly record struct StageDeviceFacts(string Glyph, string Name);

/// <summary>The props of <see cref="ViewKeys.StageGallery"/>. The pane's size is the layout's (<c>Stage.Layout.GalleryW/GalleryH</c>).</summary>
public sealed record StageGalleryProps
{
    public required Stage.StageCtx Stage { get; init; }

    public required IStageSettingsVerbs Verbs { get; init; }
}

/// <summary>The props of <see cref="ViewKeys.StageOptions"/>.</summary>
public sealed record StageOptionsProps
{
    public required Stage.StageCtx Stage { get; init; }

    public required IStageSettingsVerbs Verbs { get; init; }

    /// <summary>Close the popover.</summary>
    public required Action Close { get; init; }
}

/// <summary>What the stage's option surfaces (the options popover and the gallery) write: each is one <c>Prefs.Stage</c> setter, the
/// face pick (with its diagnostics line) or the audio seam. A theme calls these; it never names <c>Prefs</c>.</summary>
public interface IStageSettingsVerbs
{
    /// <summary>The stored layout (a <c>Stage.VizLayout</c> value).</summary>
    void SetLayout(int layout);

    /// <summary>The spectrum choice under the cover (a <c>Stage.SpectrumStyle</c> value).</summary>
    void SetSpectrum(int style);

    void SetHeroLyrics(bool on);

    void SetHeroMotion(bool on);

    /// <summary>The Artist hero's dimming, 0..100.</summary>
    void SetHeroDim(int percent);

    void SetSensitivity(float value);

    void SetLyricsOverlay(bool on);

    void SetCalm(bool on);

    void SetMoments(bool on);

    /// <summary>The sync offset in ms (the stored value and the audio graph's spectrum offset).</summary>
    void SetSyncOffsetMs(int ms);

    void SetGalleryOpen(bool open);

    /// <summary>Pick a visualizer face (the stored face and the always-on pick line).</summary>
    void PickFace(Visualizer.Kind face);
}
