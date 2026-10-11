// ── Playback/PlayerVerbs.cs ────────────────────────────────────────────────────────────────────────────────────────
// the player's VERBS as one core service (themes plan C2, X1/X5/X6/X7): everything a transport surface can ASK of the player,
// as methods. The player bar, the stage, the on-media transport, the palette and the queue pills call these; a theme's player
// view takes an `IPlayerVerbs` and wires its buttons to it, and never composes an `Input`, reads a setting or opens a store.
//
// Role: CORE
// Spec: themes technical plan §5.1 row C2, §5.3, §5.7; abstraction map §2.2.1
//
// What stays in a view: the look, the flyouts' placement, hover/press state, the tooltips, the marquee. What lives here: what a
// click DOES. `PlayerVerbs.Default` is the one cached instance (props stability, §5.7: a verb interface is implemented by a
// cached instance, never a fresh lambda per render), so a static lambda `static () => PlayerVerbs.Default.Next()` allocates
// nothing per render.
//
// The decisions that are not just a `Playback` post are PURE (`PlayerVerbRules`, `Shell.ScrubController`, `Shell.QueueVerbs`,
// `Shell.DeviceVerbs`) and unit-tested without a reducer; this class composes them with the model.

using System;

using FluentGpu.Controls;

using RepeatMode = Wavee.Spotify.Decode.RepeatMode;

namespace Wavee;

/// <summary>What a transport surface can ask of the player. Every member is one user intent; none of them owns a look.</summary>
public interface IPlayerVerbs
{
    /// <summary>Play or pause (THE intent: the bar's primary, the Space key, the palette and the deck faces all land here).
    /// <paramref name="cause"/> is the entry that pressed it ("playerbar.button", "space", "palette"…), logged once per action.</summary>
    void TogglePlay(string cause);

    void Previous();

    void Next();

    /// <summary>Seek ±<paramref name="deltaMs"/> from the reported position, ONE seek (the podcast ±10 s buttons).</summary>
    void SeekBy(int deltaMs);

    void ToggleShuffle();

    /// <summary>Off → Context → Track → Off.</summary>
    void CycleRepeat();

    /// <summary>Save or unsave the deck's playable: a track toggles in the library, an episode in the saved-episodes list.</summary>
    void ToggleLike();

    /// <summary>The episode playback rate (clamped by the model).</summary>
    void SetSpeed(float rate);

    /// <summary>Jump to the live edge of a broadcast.</summary>
    void GoLive();

    /// <summary>Flip the elapsed/remaining preference of the duration lane.</summary>
    void ToggleRemaining();

    /// <summary>The panel mode's door. Clicking the already-showing mode CLOSES the panel; otherwise it switches to that mode
    /// and opens.</summary>
    void OpenPanel(Shell.RailMode mode);

    /// <summary>Enter the fullscreen stage. <paramref name="begin"/> is the caller's <c>UseContext(SharedTransition.Begin)</c>
    /// (null when headless): the art's flight is captured before the mount.</summary>
    void OpenStage(Action<string>? begin, string cause);

    /// <summary>The video split's primary: turn the video on or off for the deck's track.</summary>
    void ToggleVideo();

    /// <summary>Open the video at a chosen home (the placement menu's rows). <paramref name="hasVideo"/> is the LIVE
    /// has-video answer the menu was built from: the availability is folded first, because a session restored mid-track
    /// leaves the stamped one at None and an unfolded open would resolve to no surface.</summary>
    void SetVideoPlacement(Video.SurfacePlacement placement, bool hasVideo);

    /// <summary>A device picker is opening: the cluster hears it. The view shows its own flyout.</summary>
    void OpenDevicePicker();

    /// <summary>Go to a route. A <see cref="Shell.Route.None"/> goes nowhere.</summary>
    void Navigate(in Shell.Route route);

    /// <summary>A drop on the transport: front-insert the dropped tracks (PLAY NEXT), never an immediate playback change.</summary>
    void DropPlayNext(DragPayload payload);

    /// <summary>The now-playing track's context menu, or null while nothing is playing or the playable is not a track.</summary>
    ContextMenuModel? NowPlayingMenu();

    /// <summary>The errored primary: re-issue the current playable from where it stopped.</summary>
    void Retry();

    /// <summary>The entity demand behind the heart: ask for the saved-episodes read when it has not been made. A no-op once
    /// it is ready or in flight.</summary>
    void DemandSaved();
}

/// <summary>The pure decisions behind <see cref="PlayerVerbs"/>.</summary>
public static class PlayerVerbRules
{
    /// <summary>Where a like lands.</summary>
    public enum LikeTarget : byte { None, Episode, Track }

    /// <summary>Which store a like writes: an episode saves into the saved-episodes list, and only once that list is READY and
    /// idle (<paramref name="episodeSaveReady"/>: a write against an unread list would clobber it); anything else is a track
    /// like, which the verb then resolves to a uri.</summary>
    public static LikeTarget LikeTargetOf(EntityRef row, bool episodeSaveReady)
        => !row.IsNone && row.Kind == EntityKind.Episode
            ? (episodeSaveReady ? LikeTarget.Episode : LikeTarget.None)
            : LikeTarget.Track;

    /// <summary>The shuffle flag after a toggle.</summary>
    public static bool NextShuffle(bool current) => !current;

    /// <summary>Off → Context → Track → Off.</summary>
    public static RepeatMode NextRepeat(RepeatMode current) => Shell.PlayerBarRules.NextRepeat(current);

    /// <summary>Is there anything to retry? A retry re-issues the deck's row, so an empty deck has none.</summary>
    public static bool CanRetry(EntityRef current) => !current.IsNone;
}

/// <summary>The production <see cref="IPlayerVerbs"/>: each member is one door onto <see cref="Playback"/> or the shell.</summary>
public sealed class PlayerVerbs : IPlayerVerbs
{
    /// <summary>The one cached instance every surface shares.</summary>
    public static readonly PlayerVerbs Default = new();

    /// <summary>THE transport fact set every surface (the bar, the stage, the video overlay) reads for Previous / Next and
    /// the shuffle / repeat satellites: one fold (<see cref="Shell.SkipRule"/>), so no surface arms a button the reducer
    /// refuses. <paramref name="track"/> = false reads with Peek (menu rows built at open time).</summary>
    public static Shell.PlayerBarFacts TransportFacts(bool track = true)
        => track
            ? Shell.SkipRule.Facts(!Playback.CurrentId.Value.IsEmpty, Playback.Error.Value, Playback.PhaseSignal.Value,
                Playback.Buffering.Value, Playback.Recovery.Value, Playback.PrevAllowedByContext.Value, Playback.NextAllowedByContext.Value)
            : Shell.SkipRule.Facts(!Playback.CurrentId.Peek().IsEmpty, Playback.Error.Peek(), Playback.PhaseSignal.Peek(),
                Playback.Buffering.Peek(), Playback.Recovery.Peek(), Playback.PrevAllowedByContext.Peek(), Playback.NextAllowedByContext.Peek());

    public void TogglePlay(string cause) => Playback.TogglePlay(cause);

    public void Previous() => Playback.Previous();

    public void Next() => Playback.Next();

    public void SeekBy(int deltaMs) => Shell.ScrubController.SeekBy(deltaMs);

    public void ToggleShuffle() => Playback.SetShuffle(PlayerVerbRules.NextShuffle(Playback.Shuffle.Peek()));

    public void CycleRepeat() => Playback.SetRepeat(PlayerVerbRules.NextRepeat(Playback.Repeat.Peek()));

    public void ToggleLike()
    {
        var r = Playback.Current.Peek();
        switch (PlayerVerbRules.LikeTargetOf(r, Spotify.Podcasts.SavedReady && !Spotify.Podcasts.SavedBusy))
        {
            case PlayerVerbRules.LikeTarget.Episode:
                Spotify.Podcasts.ToggleSaved(new Episode(r.Slot));
                return;
            case PlayerVerbRules.LikeTarget.Track:
                if (Shell.NowPlayingFace.UriOf(r) is not { } uri || Controls.Library is not { } library) return;
                library.ToggleSaved(uri, Shell.NowPlayingFace.TitleOf(r));
                return;
        }
    }

    public void SetSpeed(float rate) => Playback.SetEpisodeSpeed(rate);

    public void GoLive() => Playback.GoLive();

    public void ToggleRemaining() => Prefs.PlayerBar.ToggleRemaining();

    public void OpenPanel(Shell.RailMode mode) => Shell.Ui.Toggle(mode);

    public void OpenStage(Action<string>? begin, string cause) => Stage.Open(begin, cause);

    public void ToggleVideo() => Video.State.TogglePrimary(Shell.NowPlayingFace.HasVideoPeek());

    public void SetVideoPlacement(Video.SurfacePlacement placement, bool hasVideo)
    {
        Video.State.FoldAvailability(hasVideo);
        Video.State.OpenAt(placement);
    }

    public void OpenDevicePicker() => Shell.DeviceVerbs.PickerOpened();

    public void Navigate(in Shell.Route route)
    {
        if (!route.IsNone) Shell.GoTo(route);
    }

    public void DropPlayNext(DragPayload payload) => Shell.QueueVerbs.DropPlayNext(payload);

    public ContextMenuModel? NowPlayingMenu()
        => Shell.NowPlayingFace.TrackPeek().IsValid ? Stage.NowPlayingMenu?.Invoke() : null;

    /// <summary>0.3 has no retry token (the 0.2.9 bridge's <c>InvokePlaybackErrorAction</c>); a fresh <c>PlayNow</c> of the
    /// same row, context and cursor is the same intent through the reducer's one door.</summary>
    public void Retry()
    {
        var s = Playback.Snap();
        if (!PlayerVerbRules.CanRetry(s.Current)) return;
        Playback.PlayNow(s.Current, s.Context, s.Cursor, s.Kind, s.PosMs);
    }

    public void DemandSaved()
    {
        if (!Spotify.Podcasts.SavedReady && !Spotify.Podcasts.SavedBusy)
            _ = Spotify.Podcasts.ReadSavedAsync(CancellationToken.None);
    }
}
