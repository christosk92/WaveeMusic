// ── Theming/Contracts/PlayerBarProps.cs — the props of `Player.Bar` (themes plan 6.0, T6a) ─────────────────────────────────
//
// THE BAR IS A CORE HOST AND A THEME VIEW, on ONE hook context. The player bar is the surface with the most state in the shell
// (the five-way fold, the face hold, the tier machine, the saved-episodes demand, the grace timers), and its picture is one
// render over all of it. Splitting that render across a host component and a child would move the moment the face hold reads
// its model relative to the bookkeeping that follows it, so the contract does not split the component: the TEMPLATE calls
// `props.Host.Use(ctx.Hooks)` first, which runs the core's hooks (the viewport -> level machine, the catalog stamp, the volume
// mirror, the hold's layout effects and its two deadline timers) on the template's own hook context, in the order the old
// component ran them, and answers the decided `PlayerBarView`. The template then draws that view with its own hooks (the hover
// signal, the captured nodes, the like-pop and face-swap effects). A template that skipped `Host.Use` would draw a bar with no
// bookkeeping, which is why the call is the first line of every `Player.Bar` template.
//
// WHAT THE RECORD CARRIES. Nothing that changes: the host (one cached instance), the verbs (`IPlayerVerbs`, the production
// `PlayerVerbs.Default`), and live READS and ACTIONS the core built once (delegates over `Playback`, `Entities`, the rail): a
// template calls a read inside a bound `Prop.Of` thunk or inside its own render, so the subscription lands where it reads.
// The pieces the bar shares with the stage and the on-media strip (the seek rail with its chapter decoration, the time labels)
// arrive as builders: each is a core host (the scrub controller, the label's bind) that mounts its own key (`Player.SeekRail`,
// `Player.TimeLabel`, `PlayerTransportProps.cs`). The credits line under the title is the bar's own picture over the facts
// `Credits` states (the device picker's menu is `Player.DevicePicker`).
//
// WVTH006: a sealed record with init-only members and no writable signal.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace Wavee.Theming.Contracts;

/// <summary>The core half of the player bar: its hooks and the facts they decide.</summary>
public interface IPlayerBarHost
{
    /// <summary>Run the core's hooks on <paramref name="hooks"/> (the template's <c>ctx.Hooks</c>) and answer the decided view.
    /// Call it once per render, first, unconditionally: hooks are positional.</summary>
    PlayerBarView Use(RenderContext hooks);
}

/// <summary>The numbers one render of the bar is laid out with: the resolved level's (<c>PlayerBarPolicy</c>), as the podcast bar
/// adjusts them, plus the bar's height and the widths core rules share with the cluster's arithmetic.</summary>
public sealed record PlayerBarMetrics
{
    /// <summary>A secondary button's box (never below the 32-DIP hit-target floor).</summary>
    public float ButtonBox { get; init; }
    /// <summary>A secondary button's glyph.</summary>
    public float ButtonGlyph { get; init; }
    /// <summary>The play button's box.</summary>
    public float PrimaryBox { get; init; }
    /// <summary>The play button's glyph.</summary>
    public float PrimaryGlyph { get; init; }
    /// <summary>The identity block's width (cover + title + heart).</summary>
    public float LeftW { get; init; }
    /// <summary>The cover.</summary>
    public float ArtSize { get; init; }
    /// <summary>The gap between the left block, the centre and the right cluster.</summary>
    public float RowGap { get; init; }
    /// <summary>The row's horizontal padding.</summary>
    public float RowPad { get; init; }
    /// <summary>The gap between transport buttons.</summary>
    public float ClusterGap { get; init; }
    /// <summary>The gap between the cover and the text.</summary>
    public float LeftGap { get; init; }
    /// <summary>The gap between the seek bar and its time labels.</summary>
    public float SeekGap { get; init; }
    /// <summary>The gap between the right cluster's buttons.</summary>
    public float RightGap { get; init; }
    /// <summary>The indeterminate sweep's nominal track width (the bar's measured width, floored).</summary>
    public float TopEdgeWidth { get; init; }
    /// <summary>The bar's height: the policy's.</summary>
    public float Height { get; init; }
    /// <summary>The inline volume rail's length.</summary>
    public float VolumeSliderW { get; init; }
    /// <summary>The video split button's disclosure chevron width.</summary>
    public float SplitChevronW { get; init; }
    /// <summary>The episode speed button's width (it is part of the podcast transport's width budget).</summary>
    public float EpisodeSpeedButtonW { get; init; }
    /// <summary>The smallest button box: the episode speed button's height.</summary>
    public float MinButtonBox { get; init; }
}

/// <summary>What one render of the bar draws: every fact decided, in words a theme can read.</summary>
public sealed record PlayerBarView
{
    /// <summary>The level's numbers (the podcast bar's, for an episode).</summary>
    public required PlayerBarMetrics Metrics { get; init; }

    /// <summary>The deck holds an episode: the podcast transport (±10 s, speed) and its pressure map.</summary>
    public bool Episode { get; init; }

    /// <summary>The marquee preference: titles scroll when they do not fit.</summary>
    public bool Marquee { get; init; }

    // ── the fold ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The bar is in its error state (the primary glyph stays PLAY and the title states the fault).</summary>
    public bool Errored { get; init; }
    /// <summary>Shuffle, repeat and the satellites may be pressed.</summary>
    public bool CanTransport { get; init; }
    /// <summary>Previous may be pressed.</summary>
    public bool PrevEnabled { get; init; }
    /// <summary>Next may be pressed.</summary>
    public bool NextEnabled { get; init; }
    /// <summary>What the primary does.</summary>
    public PrimaryVerb Primary { get; init; }
    /// <summary>The primary wears the PAUSE glyph (the user's intent through a load, not the pipeline's phase).</summary>
    public bool PauseGlyph { get; init; }
    /// <summary>Shuffle is on.</summary>
    public bool Shuffle { get; init; }
    /// <summary>Repeat is on (context or track).</summary>
    public bool RepeatOn { get; init; }
    /// <summary>The repeat button's glyph (repeat-one for a track repeat).</summary>
    public string RepeatGlyph { get; init; } = "";
    /// <summary>The bar owns the window's one transport (everything but full-bleed fullscreen video).</summary>
    public bool OwnsTransport { get; init; }
    /// <summary>The ±10 s step buttons may be pressed (episodes only; false otherwise).</summary>
    public bool SeekStepEnabled { get; init; }
    /// <summary>The indeterminate sweep runs along the bar's top edge.</summary>
    public bool TopEdgeSweeps { get; init; }

    // ── what the level carries ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The title and artist text sits beside the cover (the podcast bar's narrowest level shows the cover alone).</summary>
    public bool ShowTitleBlock { get; init; } = true;
    /// <summary>The heart's slot is in the identity block.</summary>
    public bool ShowLikeSlot { get; init; }
    /// <summary>The artist line is under the title.</summary>
    public bool ShowArtistLine { get; init; }
    /// <summary>Previous and next flank the primary.</summary>
    public bool ShowPrevNext { get; init; }
    /// <summary>The elapsed label is beside the seek rail.</summary>
    public bool ShowTimesElapsed { get; init; }
    /// <summary>The remaining label is beside the seek rail.</summary>
    public bool ShowTimesRemaining { get; init; }
    /// <summary>The volume button opens a popup rail (there is no inline rail at this level).</summary>
    public bool VolumePopup { get; init; }

    // ── the identity the face states ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>The playable the face states (the deck's, or the held outgoing one while a skip resolves).</summary>
    public EntityRef Face { get; init; }
    /// <summary>The cover's url, or null while none is known.</summary>
    public string? ArtUrl { get; init; }
    /// <summary>The cover is a link (it opens the playback context).</summary>
    public bool ArtNav { get; init; }
    /// <summary>The title is a link (it opens the album or the show).</summary>
    public bool TitleNav { get; init; }
    /// <summary>The deck holds a track: the identity block is a drag source.</summary>
    public bool Draggable { get; init; }
    /// <summary>The face is an episode (its heart saves; a track's likes).</summary>
    public bool FaceIsEpisode { get; init; }
    /// <summary>The face's playable is saved.</summary>
    public bool Liked { get; init; }
    /// <summary>The heart's face is lit.</summary>
    public bool LikeLit { get; init; }
    /// <summary>The heart can be pressed (lit, ready, and not a held face).</summary>
    public bool LikeEnabled { get; init; }
    /// <summary>The "Playing on ..." line is in the text block.</summary>
    public bool RemoteLine { get; init; }

    // ── the right cluster ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The slots the cluster has, in row order.</summary>
    public RightSlot Slots { get; init; }
    /// <summary>The slots whose face is lit (a present slot may be dark: lyrics and video need a playable).</summary>
    public RightSlot Lit { get; init; }
    /// <summary>The cluster's width.</summary>
    public float RightWidth { get; init; }
    /// <summary>One slot's fixed width.</summary>
    public Func<RightSlot, float> SlotWidth { get; init; } = static _ => 0f;
    /// <summary>The "⋯" rows, in menu order.</summary>
    public OverflowCommand[] Overflow { get; init; } = [];
    /// <summary>The current track has a video.</summary>
    public bool HasVideo { get; init; }
    /// <summary>The video is on (it has a surface).</summary>
    public bool VideoLive { get; init; }
    /// <summary>The panel is open on lyrics.</summary>
    public bool LyricsOpen { get; init; }
    /// <summary>The panel is open on the queue.</summary>
    public bool QueueOpen { get; init; }
}

/// <summary>The props of <see cref="ViewKeys.PlayerBar"/>.</summary>
public sealed record PlayerBarProps
{
    // ── the core half ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The core's hooks and decisions (see <see cref="IPlayerBarHost"/>).</summary>
    public required IPlayerBarHost Host { get; init; }

    /// <summary>What a press asks of the player (the production instance).</summary>
    public required IPlayerVerbs Verbs { get; init; }

    // ── live reads (call them inside a render or a bound thunk) ───────────────────────────────────────────────────────

    /// <summary>The title line's text for the face on screen, read live.</summary>
    public required Func<string> TitleText { get; init; }
    /// <summary>The title line's ink rung, read live.</summary>
    public required Func<NowPlayingInk> TitleInk { get; init; }
    /// <summary>Does the face on screen have a title link right now?</summary>
    public required Func<bool> TitleHasRoute { get; init; }
    /// <summary>The identity block's drag payload, peeked at promotion time (null when nothing draggable is playing).</summary>
    public required Func<object?> DragPayload { get; init; }
    /// <summary>The device route is another device (the Devices button latches).</summary>
    public required Func<bool> DeviceIsRemote { get; init; }
    /// <summary>The name of the device playback is on, or null when it is this one.</summary>
    public required Func<string?> RemoteDeviceName { get; init; }
    /// <summary>Bumped when something (a toast's "Choose device") asks for the picker.</summary>
    public required IReadSignal<int> DevicePickerRequest { get; init; }
    /// <summary>The player is muted.</summary>
    public required IReadSignal<bool> Muted { get; init; }
    /// <summary>The episode playback rate.</summary>
    public required IReadSignal<float> EpisodeSpeed { get; init; }
    /// <summary>The ONE float both volume rails bind: the model's volume mirrored for a slider (its own writes go to
    /// <see cref="SetVolume"/>).</summary>
    public required FloatSignal Volume { get; init; }

    // ── actions ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Title link: go to the album or the show.</summary>
    public required Action NavigateTitle { get; init; }
    /// <summary>Cover link: go to the playback context.</summary>
    public required Action NavigateArt { get; init; }
    /// <summary>The panel's lyrics door.</summary>
    public required Action OpenLyrics { get; init; }
    /// <summary>The panel's queue door.</summary>
    public required Action OpenQueue { get; init; }
    /// <summary>Flip mute.</summary>
    public required Action ToggleMute { get; init; }
    /// <summary>Set the volume (0..1).</summary>
    public required Action<float> SetVolume { get; init; }
    /// <summary>The bar's key map: (key event, is the bar the focused container).</summary>
    public required Action<KeyEventArgs, bool> OnKey { get; init; }
    /// <summary>Open the device picker over an anchor, or close it when it is open. The overlay is the core's (placement, dismissal,
    /// the picker-opened side effect); what goes in it is <see cref="ViewKeys.DevicePicker"/> (T6b).</summary>
    public required Action<IOverlayService, Ref<NodeHandle>, Ref<OverlayHandle?>> ToggleDevicePicker { get; init; }
    /// <summary>The heart's pop edge rule: the same playable's unsaved to saved edge only.</summary>
    public required Func<int, bool, int, bool, bool> LikePops { get; init; }

    // ── values and text ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The cover's shared-transition tag (the fullscreen stage's flight starts from it).</summary>
    public required string ArtMorphKey { get; init; }
    /// <summary>A volume as the thumb bubble's text.</summary>
    public required Func<float, string> VolumeText { get; init; }
    /// <summary>A playback rate as text ("1.5x").</summary>
    public required Func<float, string> RateText { get; init; }

    // ── the pieces shared with the stage and the on-media strip (core hosts that mount their own keys) ───────────────────

    /// <summary>The seek rail's core host (it mounts <see cref="ViewKeys.SeekRail"/>, with its chapter decoration).</summary>
    public required Func<Element> SeekBar { get; init; }
    /// <summary>A transport time label's core host (it mounts <see cref="ViewKeys.TimeLabel"/>): the argument picks the right-hand
    /// (remaining / live) slot.</summary>
    public required Func<bool, Element> TimeText { get; init; }

    // ── the credits line (the bar's own picture) ─────────────────────────────────────────────────────────────────────

    /// <summary>The credits line's stamp: the face's row version folded with its credited artists' (or its show's). The line reads it
    /// through a memo, so it re-renders only when THESE rows change.</summary>
    public required Func<long> CreditsStamp { get; init; }
    /// <summary>What the credits line states, read inside the line's render (the face read subscribes it).</summary>
    public required Func<PlayerCredits> Credits { get; init; }
    /// <summary>One "⋯" row, built from live reads when the menu opens. The second argument is the shared-transition begin.</summary>
    public required Func<OverflowCommand, Action<string>?, MenuFlyoutItem> OverflowRow { get; init; }
    /// <summary>The video placement ladder the split button's chevron opens (the rows are the core's; the flyout that presents them is <see cref="ViewKeys.VideoPlacementMenu"/>).</summary>
    public required Func<IReadOnlyList<MenuFlyoutItem>> VideoPlacementMenu { get; init; }

    /// <summary>The progress hairline of the mini player bar (contract 1.13, TC2): the seek rail's core host drawn as a line (<see cref="SeekRailProps.Hairline"/>).
    /// Null where the core offers none.</summary>
    public Func<Element>? ProgressLine { get; init; }

    /// <summary>The mini player bar's identity door (contract 1.13, TC2): open the now-playing panel (a sheet at Compact). Null: no door.</summary>
    public Action? OpenNowPlaying { get; init; }
}
