// ── Entities/Playlist.VideoFirst.cs — "this playlist plays its music videos" (the official client's video-first list) ─────────────────────
//
// Role: CORE
//
// ONE predicate, two ways in:
//   · Spotify's own session chip. A personalised list (an artist mix, a daylist) offers the signal
//     `session_control_display$<id>$video_first_with_icon` among its tune choices, labelled "Video" by the format attribute
//     `session_control_display.displayName.video_first_with_icon` (itsover.saz 158). Picking it is the existing Tune verb
//     (`POST /playlist/v2/playlist/{id}/signals`); the list then states it in `session_control.selected_signals` — which the official
//     xpui reads as "video first" (`xpui-routes-playlist.js`: `e.includes("video_first_with_icon")`) and which Wavee keeps as
//     `Playlist.TuningSelected`.
//   · Wavee's own per-playlist toggle, for a list with no chip (e.g. "Today's Top Videos": no signals, `is_video_first:"false"`
//     everywhere — playlist4, context-resolve, pathfinder). The official client remembers its playlist video toggle per list
//     (`enable_playlist_video_toggle_persistence`); Wavee keeps the lists it is on for in ONE capped local settings blob
//     (the ConcertInterest shape), because the account-side store's setting kinds are not decoded and a write there would be a guess.
//
// What video-first DOES (everything reads `IsVideoFirst`): a local play of the list starts in video mode (the reducer's sticky wish
// carries it row to row), a controller's play that says so plays video, and the track table shows each row's VIDEO length.

using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace Wavee;

/// <summary>Is a playlist video-first, and the local toggle's store. UI thread (C1) for the live half.</summary>
public static class PlaylistVideo
{
    /// <summary>The session-control signal suffix that means "video first".</summary>
    public const string VideoFirstSignal = "video_first_with_icon";

    /// <summary>The most playlists the toggle remembers; switching one more on drops the oldest.</summary>
    public const int Cap = 500;

    static readonly Signal<int> s_version = new(0);

    /// <summary>Bumped on every toggle; a surface that reads <see cref="IsVideoFirst(Playlist)"/> subscribes to it (the chip half rides
    /// <c>Playlists.Changed</c>, which the tune answer publishes).</summary>
    public static IReadSignal<int> Version => s_version;

    // ── the pure half ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Do these selected session-control signals (one string, as the list or a play command states them) pick video first?</summary>
    public static bool NamesVideoFirst(ReadOnlySpan<char> selectedSignals)
        => selectedSignals.IndexOf(VideoFirstSignal.AsSpan(), StringComparison.Ordinal) >= 0;

    /// <inheritdoc cref="NamesVideoFirst(ReadOnlySpan{char})"/>
    public static bool NamesVideoFirst(ReadOnlySpan<byte> selectedSignalsUtf8) => selectedSignalsUtf8.IndexOf("video_first_with_icon"u8) >= 0;

    /// <summary>The blob's playlist uris, most recent first (blank and over-cap entries dropped).</summary>
    public static string[] Parse(string? blob)
    {
        if (string.IsNullOrWhiteSpace(blob)) return [];
        var parts = blob.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length <= Cap ? parts : parts[..Cap];
    }

    /// <summary>The blob after switching <paramref name="uri"/> on or off.</summary>
    public static string With(string? blob, string uri, bool on)
    {
        var list = new List<string>(Parse(blob));
        list.Remove(uri);
        if (on && uri.Length > 0)
        {
            list.Insert(0, uri);
            if (list.Count > Cap) list.RemoveRange(Cap, list.Count - Cap);
        }
        return string.Join(' ', list);
    }

    // ── the live half ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Is Wavee's own toggle on for this playlist? A plain read (subscribe to <see cref="Version"/> to repaint).</summary>
    public static bool ToggledOn(string uri)
        => uri.Length > 0 && Array.IndexOf(Parse(Platform.Settings.Get(Platform.Keys.PlaylistVideoFirst)), uri) >= 0;

    /// <summary>Switch the toggle and announce it.</summary>
    public static void SetToggle(string uri, bool on)
    {
        if (uri.Length == 0 || ToggledOn(uri) == on) return;
        Platform.Settings.Set(Platform.Keys.PlaylistVideoFirst, With(Platform.Settings.Get(Platform.Keys.PlaylistVideoFirst), uri, on));
        s_version.Value++;
    }

    /// <summary>THE predicate: Spotify's "Video" session chip is the list's selected signal, or Wavee's toggle is on for it.
    /// Subscribing: reads <see cref="Version"/>.</summary>
    public static bool IsVideoFirst(Playlist p)
    {
        int version = s_version.Value;
        if (!p.IsValid) return false;
        var selected = p.TuningSelectedId;
        // Memoised on (scope, slot, toggle version, selected signals): every bound row of the list asks, per bind, and the answer is one
        // list's — the blob parse and the uri text happen once per change, never per row.
        var scope = Entities.Current;
        if (ReferenceEquals(scope, s_memoScope) && p.Slot == s_memoSlot && version == s_memoVersion && selected == s_memoSelected) return s_memoValue;
        bool value = (!selected.IsEmpty && NamesVideoFirst(Entities.Strings.Resolve(selected))) || ToggledOn(p.Uri.Text);
        (s_memoScope, s_memoSlot, s_memoVersion, s_memoSelected, s_memoValue) = (scope, p.Slot, version, selected, value);
        return value;
    }

    static Scope? s_memoScope;
    static int s_memoSlot = -1, s_memoVersion;
    static StringId s_memoSelected;
    static bool s_memoValue;

    /// <summary><see cref="IsVideoFirst(Playlist)"/> for a play's context: false for anything that is not a known playlist. A PEEK
    /// (a play reads it once).</summary>
    public static bool IsVideoFirst(EntityId context)
    {
        if (context.Kind != EntityKind.Playlist || Entities.Current is not { } scope || !scope.Playlists.TryGetSlot(context, out int slot)) return false;
        return Reactive.Untrack(() => IsVideoFirst(new Playlist(slot)));
    }

    /// <summary>Does the list hold a row with a music video (so the toggle means something)? A plain scan of the rows' flags.</summary>
    public static bool HasVideoRows(Playlist p)
    {
        if (!p.IsValid) return false;
        var tracks = Entities.Current.Tracks;
        var slots = p.TrackSlots;
        var members = Entities.Current.Edges.PlaylistTracks.Payload(p.Slot);
        for (int i = 0; i < slots.Length && i < members.Length; i++)
            if (members[i].Kind == PlaylistItemKind.Track && (uint)slots[i] < (uint)tracks.Count
                && (tracks.Flags[slots[i]] & (uint)TrackFlags.HasVideo) != 0) return true;
        return false;
    }

    /// <summary>Does the list offer Spotify's own "Video" session chip (a tune choice whose signal is <see cref="VideoFirstSignal"/>)? Then
    /// the chip is the switch and Wavee's toggle is not offered.</summary>
    public static bool OffersChip(Playlist p)
    {
        if (!p.IsValid) return false;
        foreach (var option in p.TuningOptions)
            if (option.Kind == (byte)TuningOptionKind.Choice && !option.Identifier.IsEmpty
                && NamesVideoFirst(Entities.Strings.Resolve(option.Identifier))) return true;
        return false;
    }

    /// <summary>Is Wavee's toggle offered for this list: it holds rows with music videos and has no "Video" chip of its own.</summary>
    public static bool OffersToggle(Playlist p) => HasVideoRows(p) && !OffersChip(p);

    /// <summary>The length a video-first list shows for <paramref name="t"/>: its music video's (the kind-99 counterpart's), 0 while
    /// that is not known (the row keeps its own).</summary>
    public static int VideoDurationMs(Track t)
        => t.IsValid && t.VideoCounterpart is { IsValid: true } video && video.DurationMs > 0 ? video.DurationMs : 0;
}
