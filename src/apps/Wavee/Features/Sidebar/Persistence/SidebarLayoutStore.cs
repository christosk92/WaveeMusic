using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core.Sidebar;

namespace Wavee;

/// <summary>How a <see cref="SidebarLayoutStore.Load"/> ended. Anything other than <see cref="None"/> means the service
/// must load the built-in Wavee Curated layout IN MEMORY, leave the file on disk untouched, and suppress every write for
/// the rest of the process (locked decision 8). Owned here — the persistence layer classifies the fault;
/// <c>SidebarPreferences</c> only surfaces it.</summary>
public enum SidebarLoadFault : byte { None = 0, Corrupt = 1, TooNew = 2, Unreadable = 3 }

/// <summary>The outcome of a load. <c>Doc</c> is null on any fault (and on a first run, which is NOT a fault).</summary>
public readonly record struct SidebarLayoutLoad(SidebarLayoutDocDto? Doc, SidebarLoadFault Fault, string? Detail);

/// <summary>Why the LAST write attempt refused to touch the disk (a budget cap or an I/O failure). The document is never
/// truncated and never partially written: an over-cap snapshot is dropped whole and classified here, so the customizer can
/// tell the user which section to shrink. Recoverable by construction — the next in-budget commit clears it.
/// Append only; <see cref="SidebarLoadFault"/> stays the LOAD-side health enum.</summary>
public enum SidebarSaveFault : byte
{
    None = 0,
    ConfigTooLarge = 1,     // one section's extension config exceeds SidebarExtensionRef.MaxConfigBytes (64 KiB)
    DocumentTooLarge = 2,   // the serialized document exceeds SidebarLayoutStore.MaxDocumentBytes (2 MiB)
    IoFailure = 3,          // serialization / directory / fsync / atomic-replace failure
}

/// <summary>The unified, redaction-safe persistence fault vocabulary consumed by diagnostics and the customizer.
/// Append only: values are observable in tests and may be carried into future diagnostic exports.</summary>
public enum SidebarPersistenceFault : byte
{
    None = 0,
    Corrupt = 1,
    TooNew = 2,
    Unreadable = 3,
    IoFailure = 4,
    ConfigTooLarge = 5,
    DocumentTooLarge = 6,
}

/// <summary>One completed write verdict. <c>SafeDetail</c> is suitable for normal UI: it contains no local path,
/// user title, entity URI, search text, extension configuration, or exception message.</summary>
public readonly record struct SidebarWriteResult(
    bool Success,
    SidebarPersistenceFault Fault,
    int Bytes,
    long ElapsedMs,
    string? SafeDetail)
{
    public static SidebarWriteResult Healthy =>
        new(true, SidebarPersistenceFault.None, 0, 0, null);
}

// ── sidebar-layout.json: load/validate/fault-classify + atomic write with one .bak (F.3.2.3) ──────────────────────────
// The FileLocalStore.Save precedent (write tmp → fsync → rename), extended with File.Replace so installing the new file
// and rotating the previous good one into .bak is ONE atomic call.
//
// Corruption policy is preserve-don't-destroy: an unreadable document is NEVER rewritten, NEVER deleted and NEVER
// silently replaced. The fault is surfaced only in the customizer (an InfoBar with "Start fresh" → DiscardCorrupt), never
// as a startup toast — the user must not be interrupted at launch by a preferences problem.
//
// LAYOUT V2 adds two SIZE BUDGETS with the same preserve-don't-destroy stance: 64 KiB per section extension config and
// 2 MiB per document. An over-budget snapshot is refused WHOLE — no temp file, no partial write, the document on disk and
// its .bak untouched — and classified on SaveFault for the customizer to surface. It never latches: the next in-budget
// commit clears it, because shrinking the offending section is the recovery.
//
// THREADING: Load/Commit/DiscardCorrupt are called on the UI thread. Commit snapshots on the caller's thread and writes
// on the pool (the HistoryStore.SaveToDisk precedent); the write itself is serialized by _writeGate and last-wins by
// _seq, so a burst of editor commands produces ONE file write.
public sealed class SidebarLayoutStore
{
    /// <summary>2 = LAYOUT V2 (extension refs, action bindings, query uri sets). v1 upgrades by IDENTITY, so an existing
    /// document loads unchanged and re-stamps itself on the next ordinary commit — see <see cref="SidebarLayoutMigrations"/>.</summary>
    public const int CurrentVersion = 2;

    /// <summary>The whole-document budget (the platform doc's 2 MiB). Checked against the SERIALIZED bytes, before any
    /// file is created: an over-budget snapshot is dropped whole and classified as
    /// <see cref="SidebarSaveFault.DocumentTooLarge"/>. Never truncated, never partially written.</summary>
    public const int MaxDocumentBytes = 2 * 1024 * 1024;

    /// <summary>The per-section extension-config budget. Owned by <see cref="SidebarExtensionRef.MaxConfigBytes"/> — the
    /// reducer rejects an over-cap edit up front; this is the second line of defence against a HAND-EDITED document.</summary>
    public const int MaxSectionConfigBytes = SidebarExtensionRef.MaxConfigBytes;

    readonly string _path;
    readonly object _writeGate = new();
    readonly object _resultGate = new();
    readonly IWaveeLog _log;
    long _seq;                       // monotonic commit sequence — last write wins, earlier pool tasks bail
    volatile bool _writesBlocked;    // set by a fault; DiscardCorrupt clears it
    volatile SidebarSaveFault _saveFault;
    volatile string? _saveFaultDetail;
    Task? _pending;                  // the newest queued write (WaitForWrites)
    SidebarWriteResult _lastWriteResult = SidebarWriteResult.Healthy;
    Action<SidebarWriteResult>? _writeCompleted;
    SidebarPersistenceFault _lastReportedWriteFault;

    public SidebarLayoutStore(string path, IWaveeLog? log = null)
    {
        _path = path;
        _log = log ?? WaveeLog.Instance;
    }

    public static SidebarLayoutStore ForApp() => new(DefaultPath());

    /// <summary>%LOCALAPPDATA%\Wavee\WaveeMusic\sidebar-layout.json — BESIDE history.json (locked decision 8). Mirrors
    /// <c>WaveeShell.HistoryFilePath()</c>. No directory is created here; the first write creates it.</summary>
    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Wavee", "WaveeMusic", "sidebar-layout.json");

    public string FilePath => _path;
    public string BakPath => _path + ".bak";
    public string TmpPath => _path + ".tmp";
    public string CorruptPath => _path + ".corrupt";

    /// <summary>True while a fault suppresses every write. Cleared by <see cref="DiscardCorrupt"/>.</summary>
    public bool WritesBlocked => _writesBlocked;

    /// <summary>Why the last write attempt refused the disk (a budget cap or I/O failure), or <c>None</c>. Unlike
    /// <see cref="WritesBlocked"/> this does NOT latch: the next in-budget commit clears it, because the user fixing the
    /// oversized section is exactly the recovery path. The config check runs SYNCHRONOUSLY inside <see cref="Commit"/>
    /// (so it is observable the moment Commit returns); the document-size check needs the serialized bytes and therefore
    /// lands with the pool write (observable after <see cref="WaitForWrites"/>).</summary>
    public SidebarSaveFault SaveFault => _saveFault;

    /// <summary>Which section / how many bytes, for the customizer's warning. Null when <see cref="SaveFault"/> is None.</summary>
    public string? SaveFaultDetail => _saveFaultDetail;

    public bool SaveFaulted => _saveFault != SidebarSaveFault.None;

    /// <summary>The most recent completed write attempt. Readable from any thread.</summary>
    public SidebarWriteResult LastWriteResult
    {
        get { lock (_resultGate) return _lastWriteResult; }
    }

    /// <summary>Completion edge for the UI-thread owner. The store invokes this on the writing thread; consumers MUST
    /// marshal before touching a signal. <c>SidebarPreferences.Activate(post)</c> owns that marshal.</summary>
    public Action<SidebarWriteResult>? WriteCompleted
    {
        get { lock (_resultGate) return _writeCompleted; }
        set { lock (_resultGate) _writeCompleted = value; }
    }

    /// <summary>Read + validate. NEVER throws. The fault rides on the RESULT; the caller decides what to load.</summary>
    public SidebarLayoutLoad Load()
    {
        // 1 — a first run is not a fault: the service loads the built-in Curated default and the first commit creates
        //     the file. (An orphaned .bak with no primary is deliberately NOT recovered: without the primary we cannot
        //     tell a rotated backup from a leftover, and a first-run default is the safe answer.)
        if (!File.Exists(_path)) return new SidebarLayoutLoad(null, SidebarLoadFault.None, null);

        switch (TryRead(_path, out var doc, out string? primaryDetail))
        {
            case ReadOutcome.Ok:
                return new SidebarLayoutLoad(SidebarLayoutMigrations.Upgrade(doc!), SidebarLoadFault.None, null);

            case ReadOutcome.TooNew:
                // 3 — do NOT read further and do NOT touch the file: a newer build owns it.
                _writesBlocked = true;
                LogLoadFailed(SidebarPersistenceFault.TooNew);
                return new SidebarLayoutLoad(null, SidebarLoadFault.TooNew, primaryDetail);

            case ReadOutcome.Unreadable:
                _writesBlocked = true;
                LogLoadFailed(SidebarPersistenceFault.Unreadable);
                return new SidebarLayoutLoad(null, SidebarLoadFault.Unreadable, primaryDetail);
        }

        // 4 — malformed / null / version <= 0 → try the rotated backup with the SAME validation.
        if (File.Exists(BakPath) && TryRead(BakPath, out var bak, out _) == ReadOutcome.Ok)
        {
            // A good backup is a full recovery: writes stay ENABLED and the next commit rewrites the primary.
            _log.Warn("sidebar", "sidebar.layout.recovered",
                "The sidebar layout was recovered from its backup.",
                WaveeLogField.Of("recovery", "backup"));
            return new SidebarLayoutLoad(SidebarLayoutMigrations.Upgrade(bak!), SidebarLoadFault.None, "recovered from .bak");
        }

        _writesBlocked = true;
        LogLoadFailed(SidebarPersistenceFault.Corrupt);
        return new SidebarLayoutLoad(null, SidebarLoadFault.Corrupt, primaryDetail);
    }

    enum ReadOutcome : byte { Ok, Malformed, TooNew, Unreadable }

    ReadOutcome TryRead(string path, out SidebarLayoutDocDto? doc, out string? detail)
    {
        doc = null; detail = null;
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            detail = "The sidebar layout could not be read (" + ex.GetType().Name + ").";
            return ReadOutcome.Unreadable;
        }

        SidebarLayoutDocDto? parsed;
        try { parsed = JsonSerializer.Deserialize(bytes, SidebarLayoutJsonCtx.Default.SidebarLayoutDocDto); }
        catch (Exception ex)   // JsonException, and anything a hostile file can provoke out of the reader
        {
            detail = "The sidebar layout contains invalid data (" + ex.GetType().Name + ").";
            return ReadOutcome.Malformed;
        }

        if (parsed is null) { detail = "The sidebar layout contains no document."; return ReadOutcome.Malformed; }
        if (parsed.Version > CurrentVersion)
        {
            detail = $"Layout version {parsed.Version} is newer than supported version {CurrentVersion}.";
            return ReadOutcome.TooNew;
        }
        // A missing/zero/negative version is NOT silently accepted as v1: v1 is the first schema that ever shipped, so
        // no real file lacks it, and accepting `{}` as an empty layout would let the very next commit overwrite a user's
        // document with nothing. Treated as malformed → .bak → Curated default, file preserved.
        if (parsed.Version <= 0) { detail = $"The sidebar layout has an invalid version ({parsed.Version})."; return ReadOutcome.Malformed; }

        doc = parsed;
        return ReadOutcome.Ok;
    }

    void LogLoadFailed(SidebarPersistenceFault fault)
    {
        _log.Warn("sidebar", "sidebar.layout.load_failed",
            "The sidebar layout could not be loaded; the saved file was preserved.",
            WaveeLogField.Of("fault", FaultName(fault)));
    }

    void PublishWriteResult(in SidebarWriteResult result, string? exceptionType = null)
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
        {
            _log.Event(WaveeLogLevel.Warning, "sidebar", "sidebar.layout.save_failed",
                "The sidebar layout was not saved.",
                elapsedMs: result.ElapsedMs,
                fields:
                [
                    WaveeLogField.Of("fault", FaultName(result.Fault)),
                    WaveeLogField.Of("bytes", result.Bytes),
                    WaveeLogField.Of("exception_type", exceptionType),
                ]);
        }
        else if (previous != SidebarPersistenceFault.None)
        {
            _log.Event(WaveeLogLevel.Info, "sidebar", "sidebar.layout.save_recovered",
                "Sidebar layout persistence recovered.",
                elapsedMs: result.ElapsedMs,
                fields:
                [
                    WaveeLogField.Of("previous_fault", FaultName(previous)),
                    WaveeLogField.Of("bytes", result.Bytes),
                ]);
        }

        try { completed?.Invoke(result); }
        catch (Exception ex)
        {
            _log.Warn("sidebar", "sidebar.layout.completion_failed",
                "A sidebar persistence completion observer failed.",
                WaveeLogField.Of("exception_type", ex.GetType().Name));
        }
    }

    static string FaultName(SidebarPersistenceFault fault) => fault switch
    {
        SidebarPersistenceFault.Corrupt => "corrupt",
        SidebarPersistenceFault.TooNew => "too_new",
        SidebarPersistenceFault.Unreadable => "unreadable",
        SidebarPersistenceFault.IoFailure => "io_failure",
        SidebarPersistenceFault.ConfigTooLarge => "config_too_large",
        SidebarPersistenceFault.DocumentTooLarge => "document_too_large",
        _ => "none",
    };

    /// <summary>Snapshot on the UI thread, write on the pool. Coalescing: each call bumps <c>_seq</c> and captures it; the
    /// pool task aborts if <c>_seq</c> moved on (a newer snapshot is already queued), so a burst of editor commands
    /// produces ONE file write. No-op while writes are blocked by a fault.</summary>
    public void Commit(SidebarLayoutDocDto snapshot)
    {
        if (snapshot is null || _writesBlocked) return;

        // LAYOUT V2 cap #1, synchronously: a single section whose extension config is over budget. Cheap (it measures
        // only the config elements) and it must not reach the disk at all, so it is refused before anything is queued.
        if (OversizedConfig(snapshot) is { } tooBig)
        {
            Fault(SidebarSaveFault.ConfigTooLarge, tooBig, elapsedMs: 0);
            return;
        }

        snapshot.Version = CurrentVersion;
        snapshot.UpdatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        snapshot.AppVersion ??= AppVersion();

        long mine = Interlocked.Increment(ref _seq);
        var task = Task.Run(() => WriteOnPool(snapshot, mine));
        lock (_writeGate) _pending = task;
    }

    void WriteOnPool(SidebarLayoutDocDto snapshot, long mine)
    {
        if (Interlocked.Read(ref _seq) != mine) return;   // superseded before we even started
        var watch = Stopwatch.StartNew();
        SidebarWriteResult completion = default;
        string? exceptionType = null;
        bool hasCompletion = false;
        lock (_writeGate)
        {
            if (Interlocked.Read(ref _seq) != mine) return;   // superseded while we waited for the gate — last wins
            if (_writesBlocked) return;
            try
            {
                string? dir = System.IO.Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, SidebarLayoutJsonCtx.Default.SidebarLayoutDocDto);

                // LAYOUT V2 cap #2: the whole-document budget, measured on the real serialized payload and checked BEFORE
                // the temp file exists. Bailing here leaves the previous good document and its .bak exactly as they were.
                if (bytes.Length > MaxDocumentBytes)
                {
                    string detail = $"Document is {bytes.Length} B, over the {MaxDocumentBytes} B budget.";
                    Fault(SidebarSaveFault.DocumentTooLarge, detail, bytes.Length, watch.ElapsedMilliseconds, publish: false);
                    completion = new SidebarWriteResult(
                        false, SidebarPersistenceFault.DocumentTooLarge, bytes.Length, watch.ElapsedMilliseconds, detail);
                    hasCompletion = true;
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

                // A write that landed clears a previous budget fault: shrinking the offending section IS the recovery.
                _saveFault = SidebarSaveFault.None;
                _saveFaultDetail = null;
                completion = new SidebarWriteResult(
                    true, SidebarPersistenceFault.None, bytes.Length, watch.ElapsedMilliseconds, null);
                hasCompletion = true;
            }
            catch (Exception ex)
            {
                const string safe = "The sidebar layout could not be saved.";
                _saveFault = SidebarSaveFault.IoFailure;
                _saveFaultDetail = safe;
                completion = new SidebarWriteResult(
                    false, SidebarPersistenceFault.IoFailure, 0, watch.ElapsedMilliseconds, safe);
                exceptionType = ex.GetType().Name;
                hasCompletion = true;
                try { if (File.Exists(TmpPath)) File.Delete(TmpPath); } catch (Exception) { }
            }
        }
Complete:
        if (hasCompletion) PublishWriteResult(completion, exceptionType);
    }

    // ── LAYOUT V2 budget caps ─────────────────────────────────────────────────────────────────────────────────────────

    void Fault(SidebarSaveFault fault, string detail, int bytes = 0, long elapsedMs = 0, bool publish = true)
    {
        _saveFault = fault;
        _saveFaultDetail = detail;
        var persistenceFault = fault switch
        {
            SidebarSaveFault.ConfigTooLarge => SidebarPersistenceFault.ConfigTooLarge,
            SidebarSaveFault.DocumentTooLarge => SidebarPersistenceFault.DocumentTooLarge,
            SidebarSaveFault.IoFailure => SidebarPersistenceFault.IoFailure,
            _ => SidebarPersistenceFault.None,
        };
        if (publish)
            PublishWriteResult(new SidebarWriteResult(false, persistenceFault, bytes, elapsedMs, detail));
    }

    /// <summary>The first section (top level or child) whose extension config is over
    /// <see cref="MaxSectionConfigBytes"/>, as a human-readable detail string — or null when every section fits.
    /// Measures the raw config element only, so it costs nothing on a document with no contributed sections.</summary>
    string? OversizedConfig(SidebarLayoutDocDto snapshot)
    {
        var sections = snapshot.Curated?.Sections;
        if (sections is null) return null;
        for (int i = 0; i < sections.Length; i++)
        {
            if (Check(sections[i]) is { } hit) return hit;
            var kids = sections[i]?.Children;
            if (kids is null) continue;
            for (int j = 0; j < kids.Length; j++)
                if (Check(kids[j]) is { } childHit) return childHit;
        }
        return null;

        string? Check(SidebarSectionDto? s)
        {
            if (s?.Extension?.Config is not { } config) return null;
            int bytes = SidebarJson.ByteCount(config);
            return bytes > MaxSectionConfigBytes
                ? $"Section {s.Id ?? "?"} config is {bytes} B, over the {MaxSectionConfigBytes} B per-section budget."
                : null;
        }
    }

    /// <summary>Block until the newest queued write has finished. NOT for the UI thread's steady state — it exists for
    /// tests and for a deliberate drain point; the coalesced commit path is fire-and-forget by design.</summary>
    public bool WaitForWrites(int timeoutMs = 5000)
    {
        Task? t;
        lock (_writeGate) t = _pending;
        if (t is null) return true;
        try { return t.Wait(timeoutMs); }
        catch (Exception) { return false; }
    }

    /// <summary>Fault recovery (the customizer's "Start fresh"): move the unreadable file to <c>*.corrupt</c> (replacing
    /// any previous one), delete the stale <c>.bak</c>, and unblock writes. The user's bytes are preserved, not deleted,
    /// so the document can still be inspected or hand-repaired.</summary>
    public void DiscardCorrupt()
    {
        SidebarWriteResult completion = default;
        string? exceptionType = null;
        bool hasCompletion = false;
        lock (_writeGate)
        {
            try
            {
                if (File.Exists(_path)) File.Move(_path, CorruptPath, overwrite: true);
                if (File.Exists(BakPath)) File.Delete(BakPath);
                if (File.Exists(TmpPath)) File.Delete(TmpPath);
                _writesBlocked = false;
                _saveFault = SidebarSaveFault.None;
                _saveFaultDetail = null;
            }
            catch (Exception ex)
            {
                const string safe = "The unreadable sidebar layout could not be set aside.";
                _saveFault = SidebarSaveFault.IoFailure;
                _saveFaultDetail = safe;
                completion = new SidebarWriteResult(
                    false, SidebarPersistenceFault.IoFailure, 0, 0, safe);
                exceptionType = ex.GetType().Name;
                hasCompletion = true;
            }
        }
        if (hasCompletion) PublishWriteResult(completion, exceptionType);
    }

    static string? s_appVersion;

    static string AppVersion()
    {
        if (s_appVersion is not null) return s_appVersion;
        try { s_appVersion = typeof(SidebarLayoutStore).Assembly.GetName().Version?.ToString() ?? ""; }
        catch (Exception) { s_appVersion = ""; }
        return s_appVersion;
    }
}
