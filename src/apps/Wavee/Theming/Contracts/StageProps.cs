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
//   `Stage.PictureChrome` StagePictureChromeProps  Video mode's chrome over the picture (mounted by the core in the video layer)
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

    /// <summary>A pointer move: idle activity. Wire it as <c>OnPointerMoveWithin</c> (a move ANYWHERE within the box, a child's hit included: a
    /// lyric line, the picture's ground) rather than <c>OnHoverMove</c> (the hovered leaf alone, so a move over a child is lost). The surface
    /// reads the window position itself, so the box's local point only stands in when the platform has none.</summary>
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

    // ── Video mode (Cinema): the picture is the one video presenter, following a reservation the template draws ──────────

    /// <summary>The stage shows the music video (<c>Stage.Mode.Video</c>). Only a structure whose stage policy states
    /// <see cref="Theming.StagePolicy.VideoStage"/> is ever in it. While it holds, the template draws a hollow reservation for the picture
    /// (its box carries <see cref="PictureRealized"/>; the presenter follows its painted rect), none of the audio layers (the picture covers
    /// them), and none of its chrome: the chrome over the picture is <see cref="ViewKeys.StagePictureChrome"/>, which the core mounts above
    /// the video hole. A predicate. Appended (contract 1.95).</summary>
    public Func<bool> ShowPicture { get; init; } = static () => false;

    /// <summary>The picture's reservation was realized: the stay-mounted presenter follows this node (the engine's <c>FollowRect</c>). Put it
    /// on the reservation's box. Appended (contract 1.95).</summary>
    public Action<NodeHandle> PictureRealized { get; init; } = static _ => { };

    /// <summary>Video mode shows the picture in THEATRE (<c>StageCtx.Theatre</c>): the reservation sits in the left region with the title block
    /// under it, the Theatre panel (<see cref="StageLayers.TheatrePane"/>) beside it on the right; otherwise Video mode is CINEMA (the reservation
    /// fills the stage). The template moves the reservation between the two rects with a layout transition the presenter follows. In Theatre only
    /// the PICTURE counts for the idle chrome (contract 1.99): the core takes the reservation's rect, so a move reveals the chrome over the picture
    /// alone, a pointer that leaves the picture (onto the panel, the floor, the top bar) has left, only controls over the picture hold it, and the
    /// cursor hides only over the picture. A predicate. Appended (contract 1.96).</summary>
    public Func<bool> ShowTheatre { get; init; } = static () => false;

    /// <summary>Video mode holds while an AUDIO-ONLY track plays (the video is on, this track has none: <c>StageCtx.AudioFace</c>): the frame stays
    /// (the top bar with the Video tab disabled, the shelf or the Theatre panel, the transport) and the picture's place shows the track's cover
    /// and lyrics instead of the picture. No presenter (and so no video hole) is up meanwhile: the template draws the face as an ordinary stage
    /// layer over the reservation, and the next track with a video brings the picture back by itself. A predicate. Appended (contract 1.97).</summary>
    public Func<bool> ShowAudioFace { get; init; } = static () => false;

    /// <summary>The END CARD is up (<c>StageCtx.EndCard</c>): a music video ended in Video mode and the next track waits for the countdown. The
    /// template places <see cref="StageLayers.EndCard"/> over the picture's dimmed last frame (Cinema: the stage; Theatre: the picture's box), keeps
    /// the chrome as it is (a paused deck pins the idle chrome) and, in Cinema, the shelf lists the tracks after the card's. A predicate.
    /// Appended (contract 1.98).</summary>
    public Func<bool> ShowEndCard { get; init; } = static () => false;

    /// <summary>The ABOUT block under Theatre's title (YouTube's channel row and description): the playing track's primary artist and the track's
    /// facts, read inside the block's render (it subscribes the track key, the artist and track tables and the billed artists' edge). Null: nothing
    /// is playing. Built from what the stage already asks (the Artist pane's overview): no fetch of its own. Appended (contract 1.100).</summary>
    public Func<StageAboutFacts?> About { get; init; } = static () => null;

    /// <summary>Ask for what <see cref="About"/> shows (the Artist pane's own demand: the artist's overview, the track's credits), once per (artist, track).
    /// Call it from an effect. Appended (contract 1.100).</summary>
    public Action<int, int> DemandAbout { get; init; } = static (_, _) => { };

    /// <summary>Open the stage's own Artist tab (the one home of the artist's whole story); it stays in full screen. Appended (contract 1.100).</summary>
    public Action OpenArtist { get; init; } = static () => { };
}

/// <summary>The about block's facts (<see cref="StageRootView.About"/>). Every string is empty when its fact is missing: a block never shows a placeholder.</summary>
/// <param name="Artist">The primary artist's slot (0: none).</param>
/// <param name="Track">The playing track's slot.</param>
/// <param name="Name">The artist's name.</param>
/// <param name="Uri">The artist's uri (the Follow toggle's), "" when it has none.</param>
/// <param name="ImageUrl">The artist's round portrait, null when it has none (the artwork's placeholder).</param>
/// <param name="Listeners">"12.3M monthly listeners", "" when unknown.</param>
/// <param name="Facts">The track's facts joined by " · ": plays, release year, album, tempo and key; "" when none is known.</param>
/// <param name="Bio">The artist's biography as plain text, "" when there is none.</param>
/// <param name="Pending">The artist's own facts have not landed yet (the block shows its skeleton at its full size).</param>
public sealed record StageAboutFacts(int Artist, int Track, string Name, string Uri, string? ImageUrl, string Listeners, string Facts, string Bio, bool Pending);

/// <summary>The props of <see cref="ViewKeys.StagePictureChrome"/>: the stage's chrome over the music video in Video mode (the top bar, the
/// title block, the transport, the hairline, their scrims), drawn by the structure with the stage's own view (its wiring, predicates and
/// chrome props). The core mounts it in the video layer right after the presenter, so it paints over the picture; the template is a
/// full-bleed layer whose ground passes the pointer to the stage's idle machine (<see cref="StageRootView.OnHoverMove"/>) and leaves full
/// screen on a double-click. Appended (contract 1.95).</summary>
public sealed record StagePictureChromeProps
{
    /// <summary>The mounted stage's view (one per stage: built once by the core surface).</summary>
    public required StageRootView Root { get; init; }
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

    /// <summary>The Up next column a Huge 4K stage docks beside the lyrics (C-Stage-Huge4K; <c>Stage.Layout.UpNextShown</c> / <c>UpNextRect</c>):
    /// a second stage-queue host that mounts <see cref="ViewKeys.QueueStagePane"/> with <see cref="QueueStagePaneProps.Aside"/>. Null where the core
    /// offers none. Appended (contract 1.22).</summary>
    public Func<Element>? UpNext { get; init; }

    /// <summary>Video mode's Theatre panel beside the picture (Lyrics or Up next, the stored Theatre panel): a second pane host that mounts
    /// <see cref="ViewKeys.StagePane"/> with <see cref="StagePaneProps.Theatre"/>, the lyrics at the panel's type and the Up next list in its
    /// column form. Mount it under <see cref="StageRootView.ShowTheatre"/>. Null where the core offers none. Appended (contract 1.96).</summary>
    public Func<Element>? TheatrePane { get; init; }

    /// <summary>Video mode's Up next shelf over the picture (Cinema): the core host that mounts <see cref="ViewKeys.QueueStageShelf"/>. Null where
    /// the core offers none. Appended (contract 1.97).</summary>
    public Func<Element>? UpNextShelf { get; init; }

    /// <summary>The audio face's lyrics (<see cref="StageRootView.ShowAudioFace"/>): the stage's reading column at the stage's type in Cinema and at
    /// the Theatre panel's in Theatre, ticking only while the face shows lyrics (<c>StageCtx.AudioLyrics</c>). Null where the core offers none.
    /// Appended (contract 1.97).</summary>
    public Func<Element>? PictureLyrics { get; init; }

    /// <summary>The end card over the picture (<see cref="StageRootView.ShowEndCard"/>): the core host that mounts <see cref="ViewKeys.StageEndCard"/>
    /// on this render's queue. Null where the core offers none. Appended (contract 1.98).</summary>
    public Func<Element>? EndCard { get; init; }
}

/// <summary>The props of <see cref="ViewKeys.StageEndCard"/>: the end-of-video card ("Up next in 5"). The track it names is the queue's first row
/// (<see cref="QueueProps.Rows"/>[0]: what the advance plays), its facts through <see cref="Lane"/> (<see cref="IQueueLane.TextOf"/>,
/// <see cref="IQueueLane.VideoOf"/>: the shelf's 16:9 thumbnail). The countdown and the two verbs are the stage's (<c>StageCtx.EndSeconds</c>,
/// <c>EndRemainingMs</c>, <c>EndPlayNow</c>, <c>EndCancel</c>), read from the stage's context. Appended (contract 1.98).</summary>
public sealed record StageEndCardProps
{
    /// <summary>What the queue is (the first row is the card's track).</summary>
    public required QueueProps Queue { get; init; }

    /// <summary>The row facts.</summary>
    public required IQueueLane Lane { get; init; }

    /// <summary>The name of what the deck plays from (the card's "From ..." line); empty when there is none.</summary>
    public string From { get; init; } = "";
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

    /// <summary>Video mode's Theatre panel beside the picture (<see cref="StageLayers.TheatrePane"/>): the template places the region at its
    /// Theatre rect and shows it for as long as it is mounted (the root mounts it only while <see cref="StageRootView.ShowTheatre"/> holds and
    /// plays its entrance and exit); <see cref="Panes"/> then keys on the stored Theatre panel (Lyrics or Up next) and never mounts
    /// <c>empty</c>. Appended (contract 1.96).</summary>
    public bool Theatre { get; init; }
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

    /// <summary>The track after this one (the boards' "next · Get Lucky · Daft Punk"): read in the render that draws it, so a queue change
    /// re-renders that part. Empty when nothing is up next. Null: a core with nothing to hand. Appended (contract 1.57, audit B3).</summary>
    public Func<StageNextFacts>? Next { get; init; }

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

    /// <summary>The music video's settings menu, built when it opens: the docked video's own "…" rows (where it plays, then Aspect ratio, Quality
    /// and Playback speed with their current values and cascades). While the stage shows the picture the placement rows leave full screen for
    /// their place and "Video in fullscreen" reads as the current one; turning the video off is the stage's "Switch to audio". Null: a core with
    /// nothing to hand. Appended (contract 1.99).</summary>
    public Func<IReadOnlyList<MenuFlyoutItem>>? VideoMenu { get; init; }
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
public readonly record struct StageDeviceFacts(string Glyph, string Name)
{
    /// <summary>The name is the core's word ("System default"), not a device's own name: a theme may re-case it. Appended (contract 1.68).</summary>
    public bool Word { get; init; }
}

/// <summary>The track up next on the stage (contract 1.57).</summary>
/// <param name="Title">Its title; empty: nothing is up next.</param>
/// <param name="Artists">Its billed artists, ", " joined.</param>
/// <param name="ArtUrl">Its cover, or null.</param>
public readonly record struct StageNextFacts(string? Title, string? Artists, string? ArtUrl)
{
    /// <summary>Nothing is up next (draw no "next" part).</summary>
    public bool IsEmpty => Title is not { Length: > 0 };
}

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
