using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using FluentGpu.Localization;
using FluentGpu.WindowsApi.Notifications;
using FluentGpu.WindowsApi.Shell;
using Wavee.Backend.Metadata;
using Wavee.Backend.Persistence;
using Wavee.Core;
using EntityKind = Wavee.Backend.Metadata.EntityKind;   // disambiguate: Wavee.Core.EntityKind is the ROUTING vocabulary; the PERSISTED column is the transport one

namespace Wavee;

/// <summary>
/// Taskbar Jump List mirror: two always-on Tasks (Pause-or-Resume / Search) plus a "Jump back in" category of recent
/// albums/playlists/artists/shows. Sibling of <see cref="SystemMediaControlsBridge"/> — owned by
/// <see cref="PlaybackBridge"/>, STA/UI-thread, fail-soft. Rebuilds on a track-boundary
/// <see cref="OnStateChanged"/> (capped at ~1/min so a skip-storm does not hammer <c>ICustomDestinationList</c>) and
/// immediately on a play/pause edge so the Pause/Resume task cannot lag the actual transport.
/// <para>
/// Destinations are <c>wavee://</c> verbs only (never <c>spotify:</c>). Category rows come from
/// <see cref="PlayLogStore.RecentContexts"/> (context collapse, bare-track/None skipped) plus
/// <see cref="RecentSurfaceRoute"/>-classifiable <see cref="HistoryStore"/> visits when one is attached — no new store.
/// Titles prefer the play-log's stored context name, then the warm <see cref="LibraryStore"/>, then the now-playing
/// track — never a bare kind word or a raw URI.
/// </para>
/// <para>
/// Icons are the app .ico (or the playback task's play/pause glyph). Jump List destinations are <c>IShellLinkW</c>
/// entries; <c>SetIconLocation</c> only accepts an .ico / .exe / .dll resource — not a JPEG/PNG cover — so recents
/// cannot carry album art without a separate on-disk icon cache, which this bridge does not own.
/// </para>
/// </summary>
[SupportedOSPlatform("windows6.1")]
public sealed class JumpListBridge
{
    const int CategoryCap = 6;
    const long RebuildMinIntervalMs = 60_000;

    readonly PlaybackBridge _bridge;
    readonly Action<Action> _post;
    readonly Action _rebuild;

    bool _active, _dirty;
    long _lastRebuildTick;
    string? _lastTrackUri = "\0"; // first OnStateChanged is always a boundary
    bool _lastPlaying;
    bool _havePlayState;
    HistoryStore? _history;
    LibraryStore? _library;

    public JumpListBridge(PlaybackBridge bridge, IPlaybackPlayer player, Action<Action> post)
    {
        _ = player;
        _bridge = bridge;
        _post = post;
        _rebuild = Rebuild;
    }

    /// <summary>Optional navigation-recents source (the shell's <see cref="HistoryStore"/>). Safe before or after
    /// <see cref="Activate"/>; null means the category is play-log only. Does not invent a store.</summary>
    public void AttachHistory(HistoryStore? history)
    {
        if (ReferenceEquals(_history, history)) return;
        _history = history;
        RequestRebuild();
    }

    /// <summary>Optional library cache for resolving playlist/album/artist/show names that the play log did not
    /// persist (pre-title rows, editorial playlists once they land in the warm cells). Safe before or after
    /// <see cref="Activate"/>.</summary>
    public void AttachLibrary(LibraryStore? library)
    {
        if (ReferenceEquals(_library, library)) return;
        _library = library;
        RequestRebuild();
    }

    void RequestRebuild()
    {
        _dirty = true;
        if (_active) _post(_rebuild);
    }

    /// <summary>Publish the standing Tasks (and an empty-or-seeded category). UI/STA thread. Idempotent. Fail-soft.</summary>
    public void Activate()
    {
        if (_active) return;
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) return;
        _active = true;
        _dirty = true;
        Rebuild();
    }

    /// <summary>Called from <see cref="PlaybackBridge"/> on every unified-state push (UI thread). A track-URI change
    /// marks the list dirty and rebuilds at most once a minute (a later pause/heartbeat after the window elapses still
    /// flushes a pending dirty). A play/pause edge rebuilds immediately — the Pause/Resume task is the one verb that
    /// must match the transport, not last minute's skip.</summary>
    public void OnStateChanged()
    {
        if (!_active) return;
        string uri = _bridge.CurrentTrack.Peek()?.Uri ?? "";
        bool playing = uri.Length > 0 && _bridge.IsPlaying.Peek();
        bool trackChanged = !string.Equals(uri, _lastTrackUri, StringComparison.Ordinal);
        bool playChanged = !_havePlayState || playing != _lastPlaying;
        if (trackChanged) _lastTrackUri = uri;
        if (playChanged)
        {
            _lastPlaying = playing;
            _havePlayState = true;
        }
        if (trackChanged || playChanged) _dirty = true;
        if (!_dirty) return;
        long now = Environment.TickCount64;
        if (!playChanged && _lastRebuildTick != 0 && now - _lastRebuildTick < RebuildMinIntervalMs) return;
        // Post so a burst of track-boundary PushState calls in one drain collapse to one COM transaction.
        _post(_rebuild);
    }

    void Rebuild()
    {
        if (!_active || !_dirty) return;
        _dirty = false;
        _lastRebuildTick = Environment.TickCount64;
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;
        string icon = WaveeAppIcon.Path() ?? exe;

        try
        {
            bool playing = _bridge.CurrentTrack.Peek() is not null && _bridge.IsPlaying.Peek();
            string playbackIcon = TaskbarGlyph(playing ? "pause.ico" : "play.ico") ?? icon;
            var tasks = new JumpTask[]
            {
                new(playing ? "Pause" : "Resume", exe,
                    playing ? "wavee://pause" : "wavee://resume",
                    playbackIcon, playing ? "Pause playback" : "Resume playback"),
                new("Search", exe, "wavee://open?route=search", icon, "Search"),
            };
            JumpListItem[] items = BuildCategory(exe, icon);
            // Pass the AUMID the toast layer actually registered rather than relying on the process default association:
            // the shell keys a custom destination list by AUMID, so if these two ever disagree the list is written for an
            // identity the taskbar button does not have and silently never appears. Empty (Register not called yet) keeps
            // the old default-association behaviour.
            string? aumid = ToastNotifier.Default.Aumid is { Length: > 0 } id ? id : null;
            JumpList.SetCategory("Jump back in", items, tasks, aumid);
        }
        catch (Exception)
        {
            // BeginList/Commit failure, missing STA, no shell — the app keeps working with no Jump List.
        }
    }

    JumpListItem[] BuildCategory(string exe, string icon)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var items = new List<JumpListItem>(CategoryCap);

        PlayLogStore? log = _bridge.PlayLog;
        if (log is not null)
        {
            var rows = log.RecentContexts(16);
            for (int i = 0; i < rows.Count && items.Count < CategoryCap; i++)
            {
                var row = rows[i];
                if (row.Kind is PlayContextKind.None or PlayContextKind.Other) continue;
                if (!TryRoute(row.Kind, row.Uri, out string route)) continue;
                if (!seen.Add(route)) continue;
                items.Add(new JumpListItem(TitleOf(row), exe, "wavee://open?route=" + route, icon, row.Uri));
            }
        }

        HistoryStore? history = _history;
        if (history is not null)
        {
            var entries = history.Entries;
            for (int i = entries.Count - 1; i >= 0 && items.Count < CategoryCap; i--)
            {
                string name = entries[i].Route.Name;
                if (!RecentSurfaceRoute.TryClassify(name, out string uri, out var kind)) continue;
                if (!seen.Add(name)) continue;
                string title = LookupLibrary(uri, ToPlayKind(kind)) ?? KindLabel(ToPlayKind(kind));
                items.Add(new JumpListItem(title, exe, "wavee://open?route=" + name, icon, name));
            }
        }

        return items.Count == 0 ? [] : items.ToArray();
    }

    string TitleOf(PlayLogContext row)
    {
        if (row.Title is { Length: > 0 } stored) return stored;
        if (LookupLibrary(row.Uri, row.Kind) is { Length: > 0 } known) return known;

        var track = _bridge.CurrentTrack.Peek();
        if (track is not null)
        {
            if (FromTrack(track, row.Uri, row.Kind) is { Length: > 0 } live) return live;
        }
        return KindLabel(row.Kind);
    }

    /// <summary>Best-effort context display name from the track that just started. Album / artist / liked / the
    /// bare-track title are free; playlists and shows wait for the library (or a later stored title).</summary>
    internal static string? FromTrack(Track t, string? contextUri, PlayContextKind? kind = null)
    {
        var k = kind ?? PlayLogStore.ClassifyContext(contextUri);
        switch (k)
        {
            case PlayContextKind.Album:
                if (t.Album.Name.Length > 0
                    && (string.IsNullOrEmpty(contextUri)
                        || string.Equals(t.Album.Uri, contextUri, StringComparison.Ordinal)))
                    return t.Album.Name;
                break;
            case PlayContextKind.Artist:
                if (!string.IsNullOrEmpty(contextUri))
                {
                    for (int i = 0; i < t.Artists.Count; i++)
                    {
                        if (string.Equals(t.Artists[i].Uri, contextUri, StringComparison.Ordinal)
                            && t.Artists[i].Name.Length > 0)
                            return t.Artists[i].Name;
                    }
                }
                if (t.Artists is { Count: > 0 } && t.Artists[0].Name.Length > 0)
                    return t.Artists[0].Name;
                break;
            case PlayContextKind.Collection:
                return Loc.Get(Strings.Detail.LikedSongs);
            case PlayContextKind.None:
                return t.Title.Length > 0 ? t.Title : null;
        }
        return null;
    }

    string? LookupLibrary(string uri, PlayContextKind kind)
    {
        if (uri.Length == 0) return null;
        if (kind == PlayContextKind.Collection) return Loc.Get(Strings.Detail.LikedSongs);
        var lib = _library;
        if (lib is null) return null;
        switch (kind)
        {
            case PlayContextKind.Playlist:
                return FindName(lib.Playlists.Value.Peek(), uri, static p => p.Uri, static p => p.Name);
            case PlayContextKind.Album:
                return FindName(lib.Albums.Value.Peek(), uri, static a => a.Uri, static a => a.Name);
            case PlayContextKind.Artist:
                return FindName(lib.Artists.Value.Peek(), uri, static a => a.Uri, static a => a.Name);
            case PlayContextKind.Show:
                return FindName(lib.Shows.Value.Peek(), uri, static s => s.Uri, static s => s.Name);
        }
        return null;
    }

    static string? FindName<T>(IReadOnlyList<T>? list, string uri, Func<T, string> uriOf, Func<T, string> nameOf)
    {
        if (list is null) return null;
        for (int i = 0; i < list.Count; i++)
        {
            if (!string.Equals(uriOf(list[i]), uri, StringComparison.Ordinal)) continue;
            string name = nameOf(list[i]);
            return name.Length > 0 ? name : null;
        }
        return null;
    }

    static string KindLabel(PlayContextKind kind) => kind switch
    {
        PlayContextKind.Album => "Album",
        PlayContextKind.Playlist => "Playlist",
        PlayContextKind.Artist => "Artist",
        PlayContextKind.Show => "Show",
        PlayContextKind.Collection => Loc.Get(Strings.Detail.LikedSongs),
        _ => "Wavee",
    };

    static PlayContextKind ToPlayKind(EntityKind kind) => kind switch
    {
        EntityKind.Album => PlayContextKind.Album,
        EntityKind.Playlist => PlayContextKind.Playlist,
        EntityKind.Artist => PlayContextKind.Artist,
        EntityKind.Show => PlayContextKind.Show,
        _ => PlayContextKind.Other,
    };

    static bool TryRoute(PlayContextKind kind, string uri, out string route)
    {
        route = kind switch
        {
            PlayContextKind.Album => "album:" + uri,
            PlayContextKind.Playlist => "pl:" + uri,
            PlayContextKind.Artist => "artist:" + uri,
            PlayContextKind.Show => "show:" + uri,
            PlayContextKind.Collection => "liked",
            _ => "",
        };
        return route.Length > 0 && (kind == PlayContextKind.Collection || uri.Length > 0);
    }

    static string? TaskbarGlyph(string fileName)
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "assets", "taskbar", fileName);
            return File.Exists(path) ? path : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
