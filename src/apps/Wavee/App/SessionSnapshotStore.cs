using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using FluentGpu.Controls;

namespace Wavee;

// ── session.json: reopen where you left off ───────────────────────────────────────────────────────────────────────────
// Versioned document beside history.json / play-log.json. Nav and shell chrome are written by WaveeShell; playback is
// written by PlaybackBridge. Persistence copies PlayLogStore: UI-thread scratch
// copy into reused buffers (zero alloc on the nav call after warmup) → debounce 2 s → snapshot → atomic tmp→replace.
//
// Fail-soft: missing file → null (first run). Corrupt/unreadable → null, file LEFT IN PLACE, writes stay enabled so the
// first successful save replaces it. Too-new Version → null AND writes blocked (a newer build owns the document).
// Load never throws and never overwrites.

/// <summary>v1 session document. Every section is additive and optional so older v1 snapshots remain readable.</summary>
public sealed class SessionSnapshotDto
{
    public int Version { get; set; } = SessionSnapshotStore.CurrentVersion;
    public SessionNavDto? Nav { get; set; }
    public SessionPlaybackDto? Playback { get; set; }
    public SessionShellDto? Shell { get; set; }
}

/// <summary>Restorable shell chrome. Rail width remains a durable preference; this is the session-only presentation
/// that must reopen exactly as it was when the app closed.</summary>
public sealed class SessionShellDto
{
    public bool RailOpen { get; set; }
    public int RailMode { get; set; }
}

/// <summary>Browser-style nav: active route, back/forward stacks (oldest first), and the shell's <c>OpenTab.Id</c>.</summary>
public sealed class SessionNavDto
{
    public SessionRouteDto? Active { get; set; }
    public SessionRouteDto[]? Back { get; set; }
    public SessionRouteDto[]? Forward { get; set; }
    public int ActiveTabId { get; set; } = -1;
}

/// <summary>Opaque Wavee route key + optional display arg — the same pair <c>FluentGpu.Controls.Route</c> carries.
/// Origin fields are the journey parent crumb; old snapshots omit them and fail-soft to the IA trail.</summary>
public readonly record struct SessionRouteDto(
    string Name, string? Arg,
    string? OriginLabel = null, string? OriginName = null, string? OriginArg = null);

/// <summary>Playback resume payload. Schema-stable; write/consume lands in a later agent. All fields optional.</summary>
public sealed class SessionPlaybackDto
{
    public string? ContextUri { get; set; }
    public string? ContextKind { get; set; }
    public string? TrackUri { get; set; }
    public string? TrackUid { get; set; }
    public int TrackIndex { get; set; }
    public long PositionMs { get; set; }
    public bool Paused { get; set; }
    public bool Shuffle { get; set; }
    public string? RepeatMode { get; set; }
    public string[]? UserQueueUris { get; set; }
    public bool AutoplayActive { get; set; }
    /// <summary>The autoplay source uri when a station/autoplay tail was live (additive v1 field; nulls omitted).</summary>
    public string? AutoplayContextUri { get; set; }
    public long CapturedAtUnixMs { get; set; }
}

/// <summary>Local session snapshot. UI thread for <see cref="UpdateNav"/> / <see cref="UpdatePlayback"/> /
/// <see cref="UpdateShell"/> / <see cref="Flush"/>;
/// the pool task only ever touches a frozen DTO and the path string (the PlayLogStore contract).</summary>
public sealed class SessionSnapshotStore
{
    public const int CurrentVersion = 1;
    public const int MaxStack = 50;
    public const int SaveDebounceMs = 2000;

    readonly IWaveeLog _log;
    readonly object _gate = new();
    readonly SessionRouteDto[] _backScratch = new SessionRouteDto[MaxStack];
    readonly SessionRouteDto[] _fwdScratch = new SessionRouteDto[MaxStack];
    string? _path;
    string? _activeName;
    string? _activeArg;
    string? _activeOriginLabel;
    string? _activeOriginName;
    string? _activeOriginArg;
    int _backCount;
    int _fwdCount;
    int _tabId = -1;
    SessionPlaybackDto? _playback;
    SessionShellDto? _shell;
    Timer? _saveTimer;
    int _savePending;
    int _dirty;
    int _writeFaulted;
    int _loadFaultLogged;
    volatile bool _writesBlocked;

    /// <summary>The live store WaveeShell constructed. PowerBridge flushes this on suspend without touching the shell.</summary>
    public static SessionSnapshotStore? Active { get; private set; }

    public SessionSnapshotStore(IWaveeLog? log = null)
    {
        _log = log ?? WaveeLog.Instance;
        Active = this;
    }

    /// <summary>%LOCALAPPDATA%\Wavee\WaveeMusic\session.json — beside history.json and play-log.json.</summary>
    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Wavee", "WaveeMusic", "session.json");

    /// <summary>Call once (before <see cref="Load"/>) with the full file path. Injectable so tests point at a temp file.</summary>
    public void Init(string sessionFilePath)
    {
        _path = sessionFilePath;
        Active = this;
    }

    /// <summary>Best-effort <see cref="Flush"/> of <see cref="Active"/>. Fail-soft — never throws (OS suspend callbacks).</summary>
    public static void FlushActive()
    {
        try { Active?.Flush(); }
        catch { }
    }

    /// <summary>True after a too-new document suppressed every write (a newer build owns the file).</summary>
    public bool WritesBlocked => _writesBlocked;

    /// <summary>The playback section as last loaded/written (in-memory; no disk read). The playback restore consumer
    /// (PlaybackController via PlaybackBridge's seam) reads this on the empty-cluster launch path.</summary>
    public SessionPlaybackDto? PlaybackSection { get { lock (_gate) return _playback; } }

    /// <summary>The shell section as last loaded/written (in-memory; no second disk read).</summary>
    public SessionShellDto? ShellSection { get { lock (_gate) return _shell; } }

    /// <summary>Sync load. Never throws. Missing → null. Corrupt/unreadable → null, file untouched. Too-new → null + writes blocked.</summary>
    public SessionSnapshotDto? Load()
    {
        if (_path is null || !File.Exists(_path)) return null;
        try
        {
            var bytes = File.ReadAllBytes(_path);
            var dto = JsonSerializer.Deserialize(bytes, SessionSnapshotJsonCtx.Default.SessionSnapshotDto);
            if (dto is null)
            {
                LogLoadFault("corrupt_or_unreadable", preserved: true);
                return null;
            }
            if (dto.Version > CurrentVersion)
            {
                _writesBlocked = true;
                LogLoadFault("too_new", preserved: true);
                return null;
            }
            if (dto.Version < 1)
            {
                LogLoadFault("corrupt_or_unreadable", preserved: true);
                return null;
            }

            CapNavInPlace(dto.Nav);
            lock (_gate)
            {
                AdoptNav(dto.Nav);
                _playback = dto.Playback;
                _shell = dto.Shell;
            }
            return dto;
        }
        catch (Exception ex)
        {
            LogLoadFault("corrupt_or_unreadable", preserved: true, ex);
            return null;
        }
    }

    /// <summary>Mark nav dirty and debounce a save. Zero alloc after warmup: copies into reused 50-slot buffers.
    /// Pass the live shell lists — this method copies; the caller must not allocate a snapshot.</summary>
    internal void UpdateNav(Route route, IReadOnlyList<Route> back, IReadOnlyList<Route> forward, int tabId,
        NavOriginStore? origins = null)
    {
        if (_writesBlocked) return;
        lock (_gate)
        {
            _activeName = route.Name;
            _activeArg = route.Arg;
            var activeOrigin = origins?.Peek(route.Name, route.Arg);
            _activeOriginLabel = activeOrigin?.Label;
            _activeOriginName = activeOrigin?.RouteName;
            _activeOriginArg = activeOrigin?.RouteArg;
            _backCount = CopyNewest(back, _backScratch, origins);
            _fwdCount = CopyNewest(forward, _fwdScratch, origins);
            _tabId = tabId;
        }
        Interlocked.Exchange(ref _dirty, 1);
        ScheduleSave();
    }

    /// <summary>Replace the playback section (schema is stable; the writer is a later agent). Debounced like nav.</summary>
    public void UpdatePlayback(SessionPlaybackDto? dto)
    {
        if (_writesBlocked) return;
        lock (_gate) _playback = dto;
        Interlocked.Exchange(ref _dirty, 1);
        ScheduleSave();
    }

    /// <summary>Persist the session-only right-rail presentation. Equality-gated because the shell effect also runs
    /// once at mount after adopting the saved values.</summary>
    public void UpdateShell(bool railOpen, int railMode)
    {
        if (_writesBlocked) return;
        lock (_gate)
        {
            if (_shell is { } current && current.RailOpen == railOpen && current.RailMode == railMode) return;
            _shell = new SessionShellDto { RailOpen = railOpen, RailMode = railMode };
        }
        Interlocked.Exchange(ref _dirty, 1);
        ScheduleSave();
    }

    /// <summary>Synchronous best-effort write for shutdown. Cancels the debounce and fsyncs on the caller thread.
    /// No-op when nothing is dirty or writes are blocked — a corrupt file is never replaced by an empty document.</summary>
    public void Flush()
    {
        if (_path is null || _writesBlocked) return;
        Interlocked.Exchange(ref _savePending, 0);
        if (Interlocked.Exchange(ref _dirty, 0) == 0) return;
        WriteFile(_path, Snapshot());
    }

    /// <summary>Test seam: same as <see cref="Flush"/> (the PlayLogStore.SaveAndWait name).</summary>
    public void SaveAndWait() => Flush();

    /// <summary>Apply a persisted nav section onto live stacks. Caps at <see cref="MaxStack"/>. Returns false when there
    /// is no usable active route (caller keeps the pinned-workspace default).</summary>
    public static bool TryApplyNav(SessionNavDto? nav, List<SessionRouteDto> back, List<SessionRouteDto> forward, out SessionRouteDto active, out int tabId)
    {
        active = new SessionRouteDto("home", null);
        tabId = -1;
        if (nav is null || nav.Active is not { } a || string.IsNullOrWhiteSpace(a.Name)) return false;

        back.Clear();
        forward.Clear();
        AppendCapped(nav.Back, back);
        AppendCapped(nav.Forward, forward);
        active = new SessionRouteDto(a.Name, a.Arg, a.OriginLabel, a.OriginName, a.OriginArg);
        tabId = nav.ActiveTabId;
        return true;
    }

    void AdoptNav(SessionNavDto? nav)
    {
        _activeName = nav?.Active?.Name;
        _activeArg = nav?.Active?.Arg;
        _activeOriginLabel = nav?.Active?.OriginLabel;
        _activeOriginName = nav?.Active?.OriginName;
        _activeOriginArg = nav?.Active?.OriginArg;
        _tabId = nav?.ActiveTabId ?? -1;
        _backCount = CopyDtos(nav?.Back, _backScratch);
        _fwdCount = CopyDtos(nav?.Forward, _fwdScratch);
    }

    static void CapNavInPlace(SessionNavDto? nav)
    {
        if (nav is null) return;
        nav.Back = CapArray(nav.Back);
        nav.Forward = CapArray(nav.Forward);
    }

    static SessionRouteDto[]? CapArray(SessionRouteDto[]? src)
    {
        if (src is null || src.Length <= MaxStack) return src;
        int start = src.Length - MaxStack;
        var trimmed = new SessionRouteDto[MaxStack];
        Array.Copy(src, start, trimmed, 0, MaxStack);
        return trimmed;
    }

    static void AppendCapped(SessionRouteDto[]? src, List<SessionRouteDto> dest)
    {
        if (src is null || src.Length == 0) return;
        int start = src.Length > MaxStack ? src.Length - MaxStack : 0;
        int n = src.Length - start;
        for (int i = 0; i < n; i++)
        {
            var r = src[start + i];
            if (string.IsNullOrEmpty(r.Name)) continue;
            dest.Add(r);
        }
    }

    static int CopyNewest(IReadOnlyList<Route> src, SessionRouteDto[] dest, NavOriginStore? origins)
    {
        int n = Math.Min(src.Count, MaxStack);
        int start = src.Count - n;
        for (int i = 0; i < n; i++)
        {
            var r = src[start + i];
            var o = origins?.Peek(r.Name, r.Arg);
            dest[i] = o is { } x
                ? new SessionRouteDto(r.Name, r.Arg, x.Label, x.RouteName, x.RouteArg)
                : new SessionRouteDto(r.Name, r.Arg);
        }
        return n;
    }

    static int CopyDtos(SessionRouteDto[]? src, SessionRouteDto[] dest)
    {
        if (src is null || src.Length == 0) return 0;
        int n = Math.Min(src.Length, MaxStack);
        int start = src.Length - n;
        for (int i = 0; i < n; i++) dest[i] = src[start + i];
        return n;
    }

    void ScheduleSave()
    {
        if (_path is null || _writesBlocked) return;
        Interlocked.Exchange(ref _savePending, 1);
        _saveTimer ??= new Timer(static s => ((SessionSnapshotStore)s!).OnSaveTimer(), this, Timeout.Infinite, Timeout.Infinite);
        try { _saveTimer.Change(SaveDebounceMs, Timeout.Infinite); } catch (ObjectDisposedException) { }
    }

    void OnSaveTimer()
    {
        if (Interlocked.Exchange(ref _savePending, 0) == 0) return;
        if (Interlocked.Exchange(ref _dirty, 0) == 0) return;
        if (_path is null || _writesBlocked) return;
        var snapshot = Snapshot();
        string path = _path;
        _ = System.Threading.Tasks.Task.Run(() => WriteFile(path, snapshot));
    }

    SessionSnapshotDto Snapshot()
    {
        lock (_gate)
        {
            var back = new SessionRouteDto[_backCount];
            for (int i = 0; i < _backCount; i++) back[i] = _backScratch[i];
            var fwd = new SessionRouteDto[_fwdCount];
            for (int i = 0; i < _fwdCount; i++) fwd[i] = _fwdScratch[i];
            return new SessionSnapshotDto
            {
                Version = CurrentVersion,
                Nav = new SessionNavDto
                {
                    Active = _activeName is { Length: > 0 } name
                        ? new SessionRouteDto(name, _activeArg, _activeOriginLabel, _activeOriginName, _activeOriginArg)
                        : null,
                    Back = back,
                    Forward = fwd,
                    ActiveTabId = _tabId,
                },
                Playback = _playback,
                Shell = _shell,
            };
        }
    }

    void WriteFile(string path, SessionSnapshotDto snapshot)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, SessionSnapshotJsonCtx.Default.SessionSnapshotDto);
            string tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
            if (Interlocked.Exchange(ref _writeFaulted, 0) != 0)
                _log.Info("session", "session.snapshot.save_recovered",
                    "Session snapshot persistence recovered.");
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _dirty, 1);   // keep the in-memory snapshot eligible for a later Flush
            if (Interlocked.Exchange(ref _writeFaulted, 1) == 0)
                _log.Warn("session", "session.snapshot.save_failed",
                    "Session snapshot could not be saved; in-memory nav remains available.",
                    WaveeLogField.Of("exception_type", ex.GetType().Name));
            try
            {
                string tmp = path + ".tmp";
                if (File.Exists(tmp)) File.Delete(tmp);
            }
            catch (Exception) { }
        }
    }

    void LogLoadFault(string fault, bool preserved, Exception? ex = null)
    {
        if (Interlocked.Exchange(ref _loadFaultLogged, 1) != 0) return;
        _log.Warn("session", "session.snapshot.load_failed",
            "Session snapshot could not be loaded; this launch starts from the pinned-workspace default.",
            WaveeLogField.Of("fault", fault),
            WaveeLogField.Of("preserved", preserved),
            WaveeLogField.Of("exception_type", ex?.GetType().Name ?? "none"));
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SessionSnapshotDto))]
[JsonSerializable(typeof(SessionNavDto))]
[JsonSerializable(typeof(SessionPlaybackDto))]
[JsonSerializable(typeof(SessionShellDto))]
[JsonSerializable(typeof(SessionRouteDto))]
[JsonSerializable(typeof(SessionRouteDto[]))]
internal sealed partial class SessionSnapshotJsonCtx : JsonSerializerContext { }
