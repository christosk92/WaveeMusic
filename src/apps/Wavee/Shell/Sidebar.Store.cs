// ── Shell/Sidebar.Store.cs ─────────────────────────────────────────────────────────────────────────────────────────────
// the sidebar's file I/O (a schema-free byte store with a named debounce, an atomic write and one rotated .bak) and the
// v3 device and v1 account wire: the JSON DTOs, their source-generated context and the mapping to the layout and the
// pin store
//
// Role: SHELL
// Plan: docs/plans/wavee/sidebar-rework-implementation.md §P3.7
//
// The store moves bytes and nothing else: the caller serializes on the UI thread and parses what it reads. A corrupt
// file is set aside as .corrupt and the defaults are written over it; the v3 files never block writes.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wavee;

/// <summary>Why the last commit did not land (a write fault never latches: the next good commit clears it).</summary>
public enum SidebarSaveFault : byte
{
    None = 0,
    DocumentTooLarge = 2,   // the serialized file exceeds SidebarFileStore.MaxDocumentBytes (256 KiB)
    IoFailure = 3,          // directory / fsync / atomic-replace failure
}

/// <summary>How reading one sidebar file ended. The caller parses; the store only moves bytes.</summary>
public enum SidebarFileReadOutcome : byte { Missing = 0, Ok = 1, Unreadable = 2 }

public readonly record struct SidebarFileRead(SidebarFileReadOutcome Outcome, byte[]? Bytes)
{
    public static readonly SidebarFileRead Missing = new(SidebarFileReadOutcome.Missing, null);
}

/// <summary>A coalesced, atomic writer for one sidebar file with one rotated <c>.bak</c>. Corruption policy is
/// preserve-don't-destroy: a file the caller cannot parse is set aside (never deleted) and the next commit writes a
/// fresh one.
///
/// THREADING (C9 — the UI thread never blocks on a file): <see cref="Commit"/> only stamps the bytes and (re)arms a
/// <see cref="CommitDebounceMs"/> <see cref="Timer"/> under a state-only lock that is never held during I/O; the timer
/// callback runs on the pool and does the actual write under a SEPARATE gate, so a burst of editor commands produces ONE
/// file write <see cref="CommitDebounceMs"/> after the LAST one lands.</summary>
public sealed class SidebarFileStore
{
    /// <summary>The whole-document budget. Checked against the SERIALIZED bytes, before any file is touched: an
    /// over-budget snapshot is dropped whole and classified <see cref="SidebarSaveFault.DocumentTooLarge"/>.</summary>
    public const int MaxDocumentBytes = 256 * 1024;

    /// <summary>The coalescing window (named per house style — see <c>Spotify.Connect.cs:199</c>'s
    /// <c>PublishDebounceMs</c>). 300 ms is short enough that a save never feels laggy and long enough to fold a whole
    /// drag or a burst of pin/folder edits into one write.</summary>
    public const int CommitDebounceMs = 300;

    readonly string _path;

    // State-only gate: guards the pending bytes, its completion source and the timer handle. Never held during I/O,
    // so Commit() can never block the UI thread on a file (C9).
    readonly Lock _stateGate = new();
    // I/O gate: guards the actual write so at most one write touches the disk at a time.
    readonly Lock _writeGate = new();
    readonly Lock _resultGate = new();

    Timer? _commitTimer;
    byte[]? _pendingSnapshot;
    TaskCompletionSource<bool>? _pendingWrite;   // completes when the CURRENTLY-armed cycle's write lands

    volatile SidebarSaveFault _saveFault;
    volatile string? _saveFaultDetail;

    SidebarWriteResult _lastWriteResult = SidebarWriteResult.Healthy;
    Action<SidebarWriteResult>? _writeCompleted;
    SidebarPersistenceFault _lastReportedWriteFault;

    public SidebarFileStore(string path) => _path = path;

    public string FilePath => _path;
    public string BakPath => _path + ".bak";
    public string TmpPath => _path + ".tmp";
    public string CorruptPath => _path + ".corrupt";

    /// <summary>Why the last write attempt refused the disk, or <c>None</c>. This does NOT latch: the next in-budget
    /// commit clears it. The size cap needs the serialized bytes and lands with the debounced write (observable after
    /// <see cref="WaitForWrites"/>).</summary>
    public SidebarSaveFault SaveFault => _saveFault;

    /// <summary>How many bytes, or the I/O failure, for Settings › Sidebar. Null when <see cref="SaveFault"/> is None.</summary>
    public string? SaveFaultDetail => _saveFaultDetail;

    public bool SaveFaulted => _saveFault != SidebarSaveFault.None;

    /// <summary>The most recent completed write attempt. Readable from any thread.</summary>
    public SidebarWriteResult LastWriteResult { get { lock (_resultGate) return _lastWriteResult; } }

    /// <summary>Completion edge for the UI-thread owner. Invoked on the writing (pool) thread — the caller must marshal
    /// before touching a signal.</summary>
    public Action<SidebarWriteResult>? WriteCompleted
    {
        get { lock (_resultGate) return _writeCompleted; }
        set { lock (_resultGate) _writeCompleted = value; }
    }

    /// <summary>The file's bytes (never throws). The caller parses and calls <see cref="MarkCorrupt"/> when it cannot.</summary>
    public SidebarFileRead Read() => ReadAt(_path);

    /// <summary>The rotated backup's bytes — tried when the primary does not parse.</summary>
    public SidebarFileRead ReadBak() => ReadAt(BakPath);

    static SidebarFileRead ReadAt(string path)
    {
        if (!File.Exists(path)) return SidebarFileRead.Missing;
        try { return new SidebarFileRead(SidebarFileReadOutcome.Ok, File.ReadAllBytes(path)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("sidebar", "sidebar.file.unreadable " + ex.GetType().Name);
            return new SidebarFileRead(SidebarFileReadOutcome.Unreadable, null);
        }
    }

    /// <summary>Set an unparseable file aside as <c>.corrupt</c> (the bytes are kept for inspection, never deleted) so the
    /// next commit writes a fresh one. The <c>.bak</c> is left alone.</summary>
    public void MarkCorrupt()
    {
        lock (_writeGate)
        {
            try { if (File.Exists(_path)) File.Move(_path, CorruptPath, overwrite: true); }
            catch (Exception ex) { Log.Warn("sidebar", "sidebar.file.set_aside_failed", ex); }
        }
    }

    /// <summary>Snapshot NOW (arms the debounce), write <see cref="CommitDebounceMs"/> later on the pool. A burst of calls
    /// inside the window replaces the pending bytes each time, so only the LAST one is ever written. Never blocks — only
    /// the fast state gate is taken here, never the write gate (C9).</summary>
    public void Commit(byte[] bytes)
    {
        if (bytes is null) return;
        lock (_stateGate)
        {
            _pendingSnapshot = bytes;
            if (_pendingWrite is null || _pendingWrite.Task.IsCompleted)
                _pendingWrite = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _commitTimer ??= new Timer(static state => ((SidebarFileStore)state!).FlushDebounced(), this, Timeout.Infinite, Timeout.Infinite);
            _commitTimer.Change(CommitDebounceMs, Timeout.Infinite);
        }
    }

    /// <summary>Fire the pending debounced write NOW, skipping the rest of <see cref="CommitDebounceMs"/>. For app
    /// shutdown: a closing process must not lose the last edit to an armed-but-not-yet-fired debounce.</summary>
    public void FlushNow()
    {
        lock (_stateGate) _commitTimer?.Change(0, Timeout.Infinite);
    }

    // The debounce timer's callback — runs on the pool.
    void FlushDebounced()
    {
        byte[]? snapshot;
        lock (_stateGate) { snapshot = _pendingSnapshot; _pendingSnapshot = null; }

        if (snapshot is not null) WriteNow(snapshot);

        lock (_stateGate) _pendingWrite?.TrySetResult(true);
    }

    void WriteNow(byte[] bytes)
    {
        long start = Environment.TickCount64;
        SidebarWriteResult completion;
        lock (_writeGate)
        {
            try
            {
                string? dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                // The whole-document budget, measured on the real payload and checked BEFORE the temp file exists.
                // Bailing here leaves the previous good file and its .bak untouched.
                if (bytes.Length > MaxDocumentBytes)
                {
                    string detail = $"Document is {bytes.Length} B, over the {MaxDocumentBytes} B budget.";
                    _saveFault = SidebarSaveFault.DocumentTooLarge;
                    _saveFaultDetail = detail;
                    completion = new SidebarWriteResult(
                        false, SidebarPersistenceFault.DocumentTooLarge, bytes.Length, Environment.TickCount64 - start, detail);
                    goto Complete;
                }

                using (var fs = new FileStream(TmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    fs.Write(bytes);
                    fs.Flush(flushToDisk: true);   // fsync — survive power loss, not just a process crash
                }

                if (File.Exists(_path))
                {
                    // ONE atomic call that installs the new file AND rotates the previous good one into .bak.
                    try { File.Replace(TmpPath, _path, BakPath, ignoreMetadataErrors: true); }
                    catch (Exception)
                    {
                        // Some filesystems (and some network shares) refuse Replace — fall back to copy-then-move.
                        try { File.Copy(_path, BakPath, overwrite: true); } catch (Exception) { }
                        File.Move(TmpPath, _path, overwrite: true);
                    }
                }
                else
                {
                    File.Move(TmpPath, _path, overwrite: true);   // first write — no .bak is created
                }

                // A write that landed clears a previous budget fault.
                _saveFault = SidebarSaveFault.None;
                _saveFaultDetail = null;
                completion = new SidebarWriteResult(true, SidebarPersistenceFault.None, bytes.Length, Environment.TickCount64 - start, null);
            }
            catch (Exception ex)
            {
                const string safe = "The sidebar layout could not be saved.";
                _saveFault = SidebarSaveFault.IoFailure;
                _saveFaultDetail = safe;
                completion = new SidebarWriteResult(false, SidebarPersistenceFault.IoFailure, 0, Environment.TickCount64 - start, safe);
                Log.Warn("sidebar", "sidebar.layout.write_exception", ex);
                try { if (File.Exists(TmpPath)) File.Delete(TmpPath); } catch (Exception) { }
            }
        }
    Complete:
        PublishWriteResult(completion);
    }

    /// <summary>Block until the newest ARMED write cycle has finished (or the timeout elapses). NOT for the UI thread's
    /// steady state — for tests and a deliberate drain point; the coalesced commit path is fire-and-forget by design.</summary>
    public bool WaitForWrites(int timeoutMs = 5000)
    {
        Task? t;
        lock (_stateGate) t = _pendingWrite?.Task;
        if (t is null || t.IsCompleted) return true;
        try { return t.Wait(timeoutMs); }
        catch (Exception) { return false; }
    }

    void PublishWriteResult(in SidebarWriteResult result)
    {
        Action<SidebarWriteResult>? completed;
        SidebarPersistenceFault previous;
        lock (_resultGate)
        {
            previous = _lastReportedWriteFault;
            _lastWriteResult = result;
            _lastReportedWriteFault = result.Success ? SidebarPersistenceFault.None : result.Fault;
            completed = _writeCompleted;
        }

        if (!result.Success)
            Log.Warn("sidebar", $"sidebar.layout.save_failed fault={FaultName(result.Fault)} bytes={result.Bytes} elapsedMs={result.ElapsedMs}");
        else if (previous != SidebarPersistenceFault.None)
            Log.Info("sidebar", $"sidebar.layout.save_recovered previousFault={FaultName(previous)} bytes={result.Bytes} elapsedMs={result.ElapsedMs}");

        try { completed?.Invoke(result); }
        catch (Exception ex) { Log.Warn("sidebar", "sidebar.layout.completion_failed", ex); }
    }

    static string FaultName(SidebarPersistenceFault fault) => fault switch
    {
        SidebarPersistenceFault.Corrupt => "corrupt",
        SidebarPersistenceFault.TooNew => "too_new",
        SidebarPersistenceFault.Unreadable => "unreadable",
        SidebarPersistenceFault.IoFailure => "io_failure",
        SidebarPersistenceFault.DocumentTooLarge => "document_too_large",
        _ => "none",
    };
}

// ── sidebar.json v3 (device) and sidebar.acct-<hash>.json v1 (per account) — design A.4 ────────────────────────────────
// Source-generated JSON only (NativeAOT). Null members are omitted, so a default section is just its id.

public sealed class SidebarDeviceFileDto
{
    [JsonPropertyName("v")] public int Version { get; set; }
    public SidebarLayoutsDto? Layouts { get; set; }
}

public sealed class SidebarLayoutsDto
{
    public SidebarOverlayDto? Classic { get; set; }
    public SidebarOverlayDto? Library { get; set; }
}

public sealed class SidebarOverlayDto
{
    public SidebarSectionStateDto[]? Sections { get; set; }
}

public sealed class SidebarSectionStateDto
{
    public string? Id { get; set; }
    public bool? Hidden { get; set; }
    public bool? Collapsed { get; set; }
    public int? Limit { get; set; }
    public string[]? HiddenItems { get; set; }
    public string[]? ItemOrder { get; set; }
    public string? Sort { get; set; }          // "recents" | "recentlyAdded" | "alphabetical" | "creator" | "customOrder"
    public bool? Descending { get; set; }
    public string? View { get; set; }          // "list" | "grid"
    public bool? ShowLiked { get; set; }
}

public sealed class SidebarAccountFileDto
{
    [JsonPropertyName("v")] public int Version { get; set; }
    /// <summary>The account key in clear, for diagnostics and the hash-collision check (the file NAME is the hash).</summary>
    public string? Account { get; set; }
    public bool MigratedToServer { get; set; }
    /// <summary>Every pin id in the user's order. Syncable ids are order hints (membership is the converged server set).</summary>
    public string[]? Pins { get; set; }
    /// <summary>The records the server never has: route, module and wavee: playlist pins.</summary>
    public SidebarLocalPinDto[]? Local { get; set; }
    /// <summary>Last-known names of synced pins (design Q9: a pending pin paints its last name, never blank).</summary>
    public Dictionary<string, string>? Names { get; set; }
    public string[]? ExpandedFolders { get; set; }
    public long NewReleasesSeenMs { get; set; }
    public SidebarFirstSeenDto[]? FirstSeen { get; set; }
}

public sealed class SidebarLocalPinDto
{
    public string? Id { get; set; }
    public string? Kind { get; set; }          // SidebarPinWire.KindName
    public string? Uri { get; set; }
    public string? Name { get; set; }
    public long AddedAtMs { get; set; }
}

/// <summary>A playlist's first-observation stamp (the "recently added" proxy, <see cref="SidebarFirstSeen"/>).</summary>
public readonly record struct SidebarFirstSeenDto(string Id, long Ms);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(SidebarDeviceFileDto))]
[JsonSerializable(typeof(SidebarAccountFileDto))]
public sealed partial class SidebarStoreJsonCtx : JsonSerializerContext { }

/// <summary>The pin kind's wire string (shared by the account file and the v2 reader).</summary>
public static class SidebarPinWire
{
    public static string KindName(SidebarEntryKind k) => k switch
    {
        SidebarEntryKind.Playlist => "playlist",
        SidebarEntryKind.Album => "album",
        SidebarEntryKind.Artist => "artist",
        SidebarEntryKind.Show => "show",
        SidebarEntryKind.Folder => "playlistFolder",
        SidebarEntryKind.Track => "track",
        _ => "appRoute",
    };

    public static bool TryParseKind(string? s, out SidebarEntryKind kind)
    {
        switch (s)
        {
            case "playlist": kind = SidebarEntryKind.Playlist; return true;
            case "album": kind = SidebarEntryKind.Album; return true;
            case "artist": kind = SidebarEntryKind.Artist; return true;
            case "show": kind = SidebarEntryKind.Show; return true;
            case "playlistFolder": kind = SidebarEntryKind.Folder; return true;
            case "appRoute": kind = SidebarEntryKind.AppRoute; return true;
            case "track": kind = SidebarEntryKind.Track; return true;
            default: kind = default; return false;
        }
    }
}

/// <summary><c>sidebar.json</c> v3 ⇄ <see cref="SidebarLayoutState"/>. Unknown section ids are dropped (and returned for
/// the caller's log); missing sections are merged from the catalogue; a section at its defaults writes only its id.</summary>
public static class SidebarStoreV3
{
    public const int Version = 3;
    public const string FileName = "sidebar.json";

    public static string PathUnder(string profileDir) => Path.Combine(profileDir, FileName);

    public static byte[] Serialize(SidebarLayoutState state)
        => JsonSerializer.SerializeToUtf8Bytes(ToDto(state), SidebarStoreJsonCtx.Default.SidebarDeviceFileDto);

    /// <summary>False for anything that is not a readable v3 document (the caller then tries the .bak, then defaults).</summary>
    public static bool TryParse(byte[] bytes, out SidebarLayoutState state, List<string>? dropped = null)
    {
        state = SidebarLayoutState.Default;
        SidebarDeviceFileDto? dto;
        try { dto = JsonSerializer.Deserialize(bytes, SidebarStoreJsonCtx.Default.SidebarDeviceFileDto); }
        catch (Exception) { return false; }
        if (dto is null || dto.Version != Version) return false;
        state = new SidebarLayoutState(
            SidebarLayoutRules.MergeWithCatalogue(FromDto(SidebarLayoutId.Classic, dto.Layouts?.Classic), dropped),
            SidebarLayoutRules.MergeWithCatalogue(FromDto(SidebarLayoutId.Library, dto.Layouts?.Library), dropped));
        return true;
    }

    public static SidebarDeviceFileDto ToDto(SidebarLayoutState state) => new()
    {
        Version = Version,
        Layouts = new SidebarLayoutsDto { Classic = ToDto(state.Classic), Library = ToDto(state.Library) },
    };

    static SidebarOverlayDto ToDto(LayoutOverlay overlay)
    {
        var sections = new SidebarSectionStateDto[overlay.Sections.Count];
        for (int i = 0; i < sections.Length; i++)
        {
            var s = overlay.Sections[i];
            sections[i] = new SidebarSectionStateDto
            {
                Id = s.Id,
                Hidden = s.Hidden ? true : null,
                Collapsed = s.Collapsed ? true : null,
                Limit = s.Limit,
                HiddenItems = s.HiddenItems is { Count: > 0 } h ? [.. h] : null,
                ItemOrder = s.ItemOrder is { Count: > 0 } o ? [.. o] : null,
                Sort = s.Sort is { } sort ? SortName(sort) : null,
                Descending = s.Descending,
                View = s.View is { } view ? (view == SidebarLibraryView.Grid ? "grid" : "list") : null,
                ShowLiked = s.ShowLiked,
            };
        }
        return new SidebarOverlayDto { Sections = sections };
    }

    static LayoutOverlay FromDto(SidebarLayoutId layout, SidebarOverlayDto? dto)
    {
        if (dto?.Sections is not { } list) return SidebarCatalogue.DefaultOverlay(layout);
        var sections = new List<SectionState>(list.Length);
        for (int i = 0; i < list.Length; i++)
        {
            var d = list[i];
            if (d?.Id is not { Length: > 0 } id) continue;
            sections.Add(new SectionState(id,
                Hidden: d.Hidden ?? false,
                Collapsed: d.Collapsed ?? false,
                Limit: d.Limit,
                HiddenItems: d.HiddenItems,
                ItemOrder: d.ItemOrder,
                Sort: TryParseSort(d.Sort, out var sort) ? sort : null,
                Descending: d.Descending,
                View: d.View switch { "grid" => SidebarLibraryView.Grid, "list" => SidebarLibraryView.List, _ => null },
                ShowLiked: d.ShowLiked));
        }
        return new LayoutOverlay(layout, sections.ToArray());
    }

    public static string SortName(SidebarLibrarySort s) => s switch
    {
        SidebarLibrarySort.RecentlyAdded => "recentlyAdded",
        SidebarLibrarySort.Alphabetical => "alphabetical",
        SidebarLibrarySort.Creator => "creator",
        SidebarLibrarySort.CustomOrder => "customOrder",
        _ => "recents",
    };

    public static bool TryParseSort(string? s, out SidebarLibrarySort sort)
    {
        switch (s)
        {
            case "recents": sort = SidebarLibrarySort.Recents; return true;
            case "recentlyAdded": sort = SidebarLibrarySort.RecentlyAdded; return true;
            case "alphabetical": sort = SidebarLibrarySort.Alphabetical; return true;
            case "creator": sort = SidebarLibrarySort.Creator; return true;
            case "customOrder": sort = SidebarLibrarySort.CustomOrder; return true;
            default: sort = SidebarLibrarySort.Recents; return false;
        }
    }
}

/// <summary>One account's sidebar data (design C.6): its pins in order (synced membership + local records), the latch,
/// the expanded folders, the New-releases watermark and the first-seen stamps.</summary>
public sealed record SidebarAccountData(
    string Key,
    bool MigratedToServer,
    IReadOnlyList<SidebarPin> Pins,
    IReadOnlyList<string> ExpandedFolders,
    long NewReleasesSeenMs,
    IReadOnlyList<SidebarFirstSeenDto> FirstSeen);

public static class SidebarAccountStore
{
    public const int Version = 1;
    public const int MaxPins = 2000;
    public const string PendingFileName = "sidebar.acct-pending.json";

    public static SidebarAccountData Empty(string key) => new(key, false, [], [], 0L, []);

    /// <summary>The account's file name; the signed-out key is the "pending" file a migration parks old pins in.</summary>
    public static string FileNameOf(string accountKey)
        => SidebarAccountKey.IsSignedOut(accountKey) ? PendingFileName : "sidebar.acct-" + SidebarAccountKey.Hash(accountKey) + ".json";

    public static byte[] Serialize(SidebarAccountData data)
    {
        var order = new string[data.Pins.Count];
        var local = new List<SidebarLocalPinDto>();
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < order.Length; i++)
        {
            var p = data.Pins[i];
            order[i] = p.Id;
            if (PinSyncRules.IsSyncable(p.Id, ""))
            {
                if (p.Name.Length > 0) names[p.Id] = p.Name;
            }
            else local.Add(new SidebarLocalPinDto
            {
                Id = p.Id, Kind = SidebarPinWire.KindName(p.Kind), Uri = p.Uri.Length > 0 ? p.Uri : null,
                Name = p.Name.Length > 0 ? p.Name : null, AddedAtMs = p.AddedAtMs,
            });
        }
        var dto = new SidebarAccountFileDto
        {
            Version = Version,
            Account = data.Key.Length > 0 ? data.Key : null,
            MigratedToServer = data.MigratedToServer,
            Pins = order.Length > 0 ? order : null,
            Local = local.Count > 0 ? local.ToArray() : null,
            Names = names.Count > 0 ? names : null,
            ExpandedFolders = data.ExpandedFolders.Count > 0 ? [.. data.ExpandedFolders] : null,
            NewReleasesSeenMs = data.NewReleasesSeenMs,
            FirstSeen = data.FirstSeen.Count > 0 ? [.. data.FirstSeen] : null,
        };
        return JsonSerializer.SerializeToUtf8Bytes(dto, SidebarStoreJsonCtx.Default.SidebarAccountFileDto);
    }

    /// <summary>False for anything that is not a readable v1 account file. Fixed routes (Home, Liked) are never pins
    /// (Q1a); an id that names neither a syncable entity nor a local record is dropped; the list is capped.</summary>
    public static bool TryParse(byte[] bytes, string key, out SidebarAccountData data)
    {
        data = Empty(key);
        SidebarAccountFileDto? dto;
        try { dto = JsonSerializer.Deserialize(bytes, SidebarStoreJsonCtx.Default.SidebarAccountFileDto); }
        catch (Exception) { return false; }
        if (dto is null || dto.Version != Version) return false;
        var local = new Dictionary<string, SidebarLocalPinDto>(StringComparer.Ordinal);
        if (dto.Local is { } records)
            for (int i = 0; i < records.Length; i++)
                if (records[i]?.Id is { Length: > 0 } id) local[id] = records[i];
        var pins = new List<SidebarPin>(dto.Pins?.Length ?? 0);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (dto.Pins is { } ids)
            for (int i = 0; i < ids.Length && pins.Count < MaxPins; i++)
            {
                string? id = ids[i];
                if (string.IsNullOrEmpty(id) || SidebarPinRules.IsFixedRoute(id) || !seen.Add(id)) continue;
                if (local.TryGetValue(id, out var rec))
                {
                    if (!SidebarPinWire.TryParseKind(rec.Kind, out var kind)) continue;
                    pins.Add(new SidebarPin(id, kind, rec.Uri ?? "", rec.Name ?? "", rec.AddedAtMs));
                    continue;
                }
                if (!PinSyncRules.IsSyncable(id, "")) continue;
                string name = dto.Names is { } n && n.TryGetValue(id, out var cached) ? cached : "";
                pins.Add(new SidebarPin(id, SidebarPinId.KindOf(id), SidebarPinId.UriOf(id), name, 0L));
            }
        data = new SidebarAccountData(key, dto.MigratedToServer, pins, dto.ExpandedFolders ?? [], dto.NewReleasesSeenMs,
            dto.FirstSeen ?? []);
        return true;
    }
}

/// <summary>Whose sidebar account file is live (design C.6): <c>provider:account</c> of the catalog scope; an empty
/// account is signed out. Market, locale, tier and explicit-filter switches change the scope but NOT this key.</summary>
public static class SidebarAccountKey
{
    public static string Of(in CatalogScope scope) => scope.Account.Length == 0 ? "" : scope.Provider + ":" + scope.Account;

    public static bool IsSignedOut(string key) => key.Length == 0;

    /// <summary>FNV-1a 64 over the key's UTF-16 code units, 16 lowercase hex digits — a stable, path-safe file name.</summary>
    public static string Hash(string key)
    {
        ulong h = 14695981039346656037UL;
        for (int i = 0; i < key.Length; i++) { h ^= key[i]; h *= 1099511628211UL; }
        return h.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
    }
}

/// <summary>The per-account <c>migratedToServer</c> latch and THE ACCOUNT GUARD <c>LibraryPinSync</c> consults before
/// converging the server set onto the store and before writing a local change to the server (Q1b).</summary>
public sealed class SidebarPinLatch
{
    readonly Func<string> _loadedKey;
    readonly Func<string> _liveKey;
    readonly Func<bool> _get;
    readonly Action<bool> _set;

    public SidebarPinLatch(Func<string> loadedAccountKey, Func<string> liveAccountKey, Func<bool> get, Action<bool> set)
    {
        _loadedKey = loadedAccountKey;
        _liveKey = liveAccountKey;
        _get = get;
        _set = set;
    }

    public bool Get() => _get();
    public void Set(bool migrated) => _set(migrated);

    /// <summary>The pin store holds the LIVE account's pins (and someone is signed in). False during the window between a
    /// scope switch and the store's reload: nothing may cross between two accounts then.</summary>
    public bool Matches
    {
        get
        {
            string loaded = _loadedKey();
            return loaded.Length > 0 && string.Equals(loaded, _liveKey(), StringComparison.Ordinal);
        }
    }
}
