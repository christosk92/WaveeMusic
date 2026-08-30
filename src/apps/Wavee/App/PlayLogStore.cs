using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Signals;
using Wavee.Core;

namespace Wavee;

// ── The local play-log (§C1.8.1) ──────────────────────────────────────────────────────────────────────────────────────
// "Recently PLAYED" — things you listened to — as distinct from HistoryStore's "recently VISITED" (places you navigated
// to). A JumpBackIn sidebar section with Display.Recents == Played reads this; Visited keeps reading HistoryStore.
//
// Persistence copies the HistoryStore pattern exactly (foundation F.3 mechanics): play-log.json BESIDE history.json, a
// ring capped at 200 entries, snapshot-on-the-UI-thread + write-on-the-pool, write-then-rename so a crash cannot leave a
// half-written file. Saves are DEBOUNCED and COALESCED — a play-log append happens at every track boundary, and writing
// a file per boundary would be a needless disk hit on a shuffled album.
//
// THREADING: UI thread only, like HistoryStore. Append is called from the playback push (see PlaybackBridge.PushState);
// the pool task only ever touches the snapshot array and the path string.
public enum PlayContextKind : byte
{
    None = 0,        // no context at all — a bare track play
    Album = 1,
    Playlist = 2,
    Artist = 3,
    Show = 4,
    Collection = 5,  // spotify:collection:tracks (Liked Songs) and friends
    Other = 6,       // a context uri shape this build doesn't classify — preserved verbatim, never dropped
}

/// <summary>One playback start. <c>ContextUri</c> is what the user pressed play ON (album/playlist/artist/show); it is
/// empty for a bare track play, in which case the sidebar falls back to a Track row.</summary>
public readonly record struct PlayLogEntry(string TrackUri, string ContextUri, PlayContextKind ContextKind, long PlayedAtMs, string? ContextTitle = null)
{
    public DateTime PlayedAtUtc => DateTimeOffset.FromUnixTimeMilliseconds(PlayedAtMs).UtcDateTime;
}

/// <summary>One row of the sidebar's context-first "recently played" projection: the newest play of a distinct context,
/// or (when a play had no context) the track itself.</summary>
public readonly record struct PlayLogContext(string Uri, PlayContextKind Kind, long PlayedAtMs, string TrackUri, string? Title = null)
{
    /// <summary>True when this row is a bare track (no context) and must render as a Track row, not a container.</summary>
    public bool IsTrack => Kind == PlayContextKind.None;
}

public sealed class PlayLogStore
{
    /// <summary>The ring cap (§C1.8.1). 200 plays is far more than any "top 3–8" projection needs and keeps the file a
    /// few KB; the oldest entry is dropped FIFO.</summary>
    public const int MaxEntries = 200;

    /// <summary>Debounce window for the coalesced save. One album side is ~10 boundaries; at 2 s they become one write.</summary>
    public const int SaveDebounceMs = 2000;

    readonly List<PlayLogEntry> _entries = new(MaxEntries);
    readonly Signal<int> _revision = new(0);
    readonly IWaveeLog _log;
    string? _path;
    Timer? _saveTimer;
    int _savePending;
    int _writeFaulted;
    int _loadFaultLogged;

    public PlayLogStore(IWaveeLog? log = null) => _log = log ?? WaveeLog.Instance;

    /// <summary>Bumped on every accepted append. The sidebar's projection keys its <c>DepKey</c> on this.</summary>
    public IReadSignal<int> Version => _revision;

    /// <summary>The same counter as a plain int, for callers that only need the value (SidebarProjectionInput).</summary>
    public int Revision => _revision.Peek();

    /// <summary>Newest LAST (the HistoryStore convention). Live view — do not mutate.</summary>
    public IReadOnlyList<PlayLogEntry> Entries => _entries;

    /// <summary>%LOCALAPPDATA%\Wavee\WaveeMusic\play-log.json — beside history.json and sidebar-layout.json.</summary>
    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Wavee", "WaveeMusic", "play-log.json");

    /// <summary>Call once (before <see cref="LoadFromDisk"/>) with the full file path. Injectable so tests point at a
    /// temp file (the <c>HistoryStore.Init</c> / <c>FileLocalStore</c> precedent).</summary>
    public void Init(string playLogFilePath) => _path = playLogFilePath;

    public void LoadFromDisk()
    {
        if (_path is null || !File.Exists(_path)) return;
        try
        {
            var bytes = File.ReadAllBytes(_path);
            var dtos = JsonSerializer.Deserialize(bytes, PlayLogJsonCtx.Default.PlayLogEntryDtoArray);
            if (dtos is null) return;
            for (int i = 0; i < dtos.Length; i++)
            {
                var d = dtos[i];
                if (string.IsNullOrEmpty(d.Track)) continue;                 // a row with no track is unusable
                _entries.Add(new PlayLogEntry(d.Track!, d.Context ?? "", KindOfByte(d.Kind), d.AtMs, d.Title));
            }
            TrimToCap();
            // No revision bump: no listeners exist yet at startup time (the HistoryStore.LoadFromDisk contract).
        }
        catch (Exception ex)
        {
            _entries.Clear();
            PreserveUnreadableFile(ex);
        }
    }

    /// <summary>Record one playback start. Idempotent at the boundary: a repeat of the SAME (track, context) pair within
    /// one second is treated as the same play (a push storm at a track edge must not fill the ring). Returns whether the
    /// append was accepted.</summary>
    public bool Append(string? trackUri, string? contextUri, long atMs = 0, string? contextTitle = null)
    {
        if (string.IsNullOrEmpty(trackUri)) return false;
        if (atMs <= 0) atMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string context = contextUri ?? "";
        string? title = string.IsNullOrEmpty(contextTitle) ? null : contextTitle;

        if (_entries.Count > 0)
        {
            var last = _entries[^1];
            if (string.Equals(last.TrackUri, trackUri, StringComparison.Ordinal)
                && string.Equals(last.ContextUri, context, StringComparison.Ordinal)
                && Math.Abs(atMs - last.PlayedAtMs) < 1000)
                return false;
        }

        _entries.Add(new PlayLogEntry(trackUri!, context, ClassifyContext(context), atMs, title));
        TrimToCap();
        _revision.Value++;
        ScheduleSave();
        return true;
    }

    /// <summary>The sidebar's read API (§C1.8.1): entries collapse to their CONTEXT (album/playlist/artist/show),
    /// newest-first, deduped — "the last <paramref name="max"/> distinct things you listened to". A play with no context
    /// falls back to a Track row keyed by the track uri, so a bare single still shows up.</summary>
    public IReadOnlyList<PlayLogContext> RecentContexts(int max = 8)
    {
        if (max <= 0 || _entries.Count == 0) return Array.Empty<PlayLogContext>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<PlayLogContext>(Math.Min(max, _entries.Count));
        for (int i = _entries.Count - 1; i >= 0 && rows.Count < max; i--)
        {
            var e = _entries[i];
            bool hasContext = e.ContextUri.Length > 0 && e.ContextKind != PlayContextKind.None;
            string key = hasContext ? e.ContextUri : e.TrackUri;
            if (key.Length == 0 || !seen.Add(key)) continue;
            rows.Add(new PlayLogContext(
                key,
                hasContext ? e.ContextKind : PlayContextKind.None,
                e.PlayedAtMs,
                e.TrackUri,
                e.ContextTitle));
        }
        return rows;
    }

    /// <summary>Drop everything and delete the file (a "clear history" affordance / a sign-out wipe).</summary>
    public void Clear()
    {
        if (_entries.Count == 0) return;
        _entries.Clear();
        _revision.Value++;
        Interlocked.Exchange(ref _savePending, 0);
        if (_path is { } p)
            _ = Task.Run(() => DeleteFile(p));
    }

    /// <summary>Issue any debounced write NOW (a design switch / a deliberate drain point). Does not block on the pool.</summary>
    public void Flush()
    {
        if (Interlocked.Exchange(ref _savePending, 0) == 0) return;
        SaveNow();
    }

    /// <summary>Classify a context uri into the family the sidebar renders. Unknown shapes become
    /// <see cref="PlayContextKind.Other"/> and are still persisted verbatim — a context this build cannot draw is not a
    /// reason to lose the play.</summary>
    public static PlayContextKind ClassifyContext(string? contextUri)
    {
        if (string.IsNullOrEmpty(contextUri)) return PlayContextKind.None;
        // EntityUri already multiplexes the user-namespaced forms (spotify:user:<u>:playlist:<id> → Playlist,
        // spotify:user:<u>:collection → Collection), which the four hand-rolled prefix tests here used to half-cover.
        return EntityUri.KindOf(contextUri) switch
        {
            EntityKind.Album => PlayContextKind.Album,
            EntityKind.Playlist => PlayContextKind.Playlist,
            EntityKind.Artist => PlayContextKind.Artist,
            EntityKind.Show => PlayContextKind.Show,
            EntityKind.Collection => PlayContextKind.Collection,
            _ => PlayContextKind.Other,
        };
    }

    void TrimToCap()
    {
        int overflow = _entries.Count - MaxEntries;
        if (overflow > 0) _entries.RemoveRange(0, overflow);   // FIFO — drop the oldest
    }

    // ── the debounced, coalesced save ─────────────────────────────────────────────────────────────────────────────────
    void ScheduleSave()
    {
        if (_path is null) return;
        Interlocked.Exchange(ref _savePending, 1);
        // One reused timer, re-armed on each append: the write lands SaveDebounceMs after the LAST append of a burst.
        _saveTimer ??= new Timer(static s => ((PlayLogStore)s!).OnSaveTimer(), this, Timeout.Infinite, Timeout.Infinite);
        try { _saveTimer.Change(SaveDebounceMs, Timeout.Infinite); } catch (ObjectDisposedException) { }
    }

    void OnSaveTimer()
    {
        if (Interlocked.Exchange(ref _savePending, 0) == 0) return;
        SaveNow();
    }

    void SaveNow()
    {
        if (_path is null) return;
        // Snapshot on the CALLER's thread (the HistoryStore.SaveToDisk contract): the pool task then only touches the
        // snapshot array and the path string, never the live list.
        var snapshot = Snapshot();
        string path = _path;
        _ = Task.Run(() => WriteFile(path, snapshot));
    }

    /// <summary>Test/shutdown seam: write SYNCHRONOUSLY, so a store reopened on the next line observes the same rows.</summary>
    public void SaveAndWait()
    {
        if (_path is null) return;
        Interlocked.Exchange(ref _savePending, 0);
        WriteFile(_path, Snapshot());
    }

    PlayLogEntryDto[] Snapshot()
    {
        int count = Math.Min(_entries.Count, MaxEntries);
        int start = _entries.Count - count;
        var snapshot = new PlayLogEntryDto[count];
        for (int i = 0; i < count; i++)
        {
            var e = _entries[start + i];
            snapshot[i] = new PlayLogEntryDto(e.TrackUri, e.ContextUri.Length == 0 ? null : e.ContextUri, (byte)e.ContextKind, e.PlayedAtMs, e.ContextTitle);
        }
        return snapshot;
    }

    void WriteFile(string path, PlayLogEntryDto[] snapshot)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, PlayLogJsonCtx.Default.PlayLogEntryDtoArray);
            string tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);   // write-then-rename: a crash can't leave a half-written file
            if (Interlocked.Exchange(ref _writeFaulted, 0) != 0)
                _log.Info("sidebar", "sidebar.play_log.save_recovered",
                    "Recently played persistence recovered.");
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _writeFaulted, 1) == 0)
                _log.Warn("sidebar", "sidebar.play_log.save_failed",
                    "Recently played could not be saved; in-memory history remains available.",
                    WaveeLogField.Of("exception_type", ex.GetType().Name));
            try
            {
                string tmp = path + ".tmp";
                if (File.Exists(tmp)) File.Delete(tmp);
            }
            catch (Exception) { }
        }
    }

    void PreserveUnreadableFile(Exception ex)
    {
        bool preserved = false;
        if (_path is { } path)
        {
            try
            {
                File.Move(path, path + ".corrupt", overwrite: true);
                preserved = true;
            }
            catch (Exception) { /* inaccessible data stays in place; a failed move must not stop startup */ }
        }

        if (Interlocked.Exchange(ref _loadFaultLogged, 1) == 0)
            _log.Warn("sidebar", "sidebar.play_log.load_failed",
                "Recently played could not be loaded; the session starts with an empty in-memory log.",
                WaveeLogField.Of("fault", "corrupt_or_unreadable"),
                WaveeLogField.Of("preserved", preserved),
                WaveeLogField.Of("exception_type", ex.GetType().Name));
    }

    void DeleteFile(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex)
        {
            _log.Warn("sidebar", "sidebar.action.failed",
                "Recently played could not be cleared from disk.",
                WaveeLogField.Of("action", "clear_play_log"),
                WaveeLogField.Of("exception_type", ex.GetType().Name));
        }
    }

    static PlayContextKind KindOfByte(byte b) => b <= (byte)PlayContextKind.Other ? (PlayContextKind)b : PlayContextKind.Other;
}

// AOT-safe source-gen JSON for the persisted play log (the HistoryJsonCtx precedent). Short member names: the file is
// written at every listening session and 200 rows of verbose keys is pure waste.
internal readonly record struct PlayLogEntryDto(string Track, string? Context, byte Kind, long AtMs, string? Title = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PlayLogEntryDto[]))]
internal sealed partial class PlayLogJsonCtx : JsonSerializerContext { }
