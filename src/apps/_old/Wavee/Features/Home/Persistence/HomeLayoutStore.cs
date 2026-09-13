using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core.Home;

namespace Wavee;

/// <summary>How a <see cref="HomeLayoutStore.Load"/> ended. Anything other than <see cref="None"/> means the service
/// must load the built-in default IN MEMORY, leave the file on disk untouched, and suppress every write until the
/// user discards the corrupt file (sidebar locked decision 8 — preserve-don't-destroy).</summary>
public enum HomeLayoutLoadFault : byte { None = 0, Corrupt = 1, TooNew = 2, Unreadable = 3 }

public readonly record struct HomeLayoutLoad(HomeLayoutDocDto? Doc, HomeLayoutLoadFault Fault, string? Detail);

public enum HomeLayoutSaveFault : byte { None = 0, DocumentTooLarge = 1, IoFailure = 2 }

/// <summary>home-layout.json: load/validate/fault-classify + atomic write with one .bak. Same File.Replace
/// cadence as <see cref="SidebarLayoutStore"/>, beside it under %LOCALAPPDATA%\Wavee\WaveeMusic\.</summary>
public sealed class HomeLayoutStore
{
    public const int CurrentVersion = 1;
    public const int MaxDocumentBytes = 256 * 1024;

    readonly string _path;
    readonly object _writeGate = new();
    readonly IWaveeLog _log;
    long _seq;
    volatile bool _writesBlocked;
    volatile HomeLayoutSaveFault _saveFault;
    Task? _pending;

    public HomeLayoutStore(string path, IWaveeLog? log = null)
    {
        _path = path;
        _log = log ?? WaveeLog.Instance;
    }

    public static HomeLayoutStore ForApp() => new(DefaultPath());

    /// <summary>%LOCALAPPDATA%\Wavee\WaveeMusic\home-layout.json — BESIDE sidebar-layout.json.</summary>
    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Wavee", "WaveeMusic", "home-layout.json");

    public string FilePath => _path;
    public string BakPath => _path + ".bak";
    public string TmpPath => _path + ".tmp";
    public string CorruptPath => _path + ".corrupt";
    public bool WritesBlocked => _writesBlocked;
    public HomeLayoutSaveFault SaveFault => _saveFault;

    public HomeLayoutLoad Load()
    {
        if (!File.Exists(_path)) return new HomeLayoutLoad(null, HomeLayoutLoadFault.None, null);

        switch (TryRead(_path, out var doc, out string? primaryDetail))
        {
            case ReadOutcome.Ok:
                return new HomeLayoutLoad(doc, HomeLayoutLoadFault.None, null);

            case ReadOutcome.TooNew:
                _writesBlocked = true;
                LogLoadFailed("too_new");
                return new HomeLayoutLoad(null, HomeLayoutLoadFault.TooNew, primaryDetail);

            case ReadOutcome.Unreadable:
                _writesBlocked = true;
                LogLoadFailed("unreadable");
                return new HomeLayoutLoad(null, HomeLayoutLoadFault.Unreadable, primaryDetail);
        }

        if (File.Exists(BakPath) && TryRead(BakPath, out var bak, out _) == ReadOutcome.Ok)
        {
            _log.Warn("home", "home.layout.recovered",
                "The Home layout was recovered from its backup.",
                WaveeLogField.Of("recovery", "backup"));
            return new HomeLayoutLoad(bak, HomeLayoutLoadFault.None, "recovered from .bak");
        }

        _writesBlocked = true;
        LogLoadFailed("corrupt");
        return new HomeLayoutLoad(null, HomeLayoutLoadFault.Corrupt, primaryDetail);
    }

    enum ReadOutcome : byte { Ok, Malformed, TooNew, Unreadable }

    ReadOutcome TryRead(string path, out HomeLayoutDocDto? doc, out string? detail)
    {
        doc = null; detail = null;
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            detail = "The Home layout could not be read (" + ex.GetType().Name + ").";
            return ReadOutcome.Unreadable;
        }

        HomeLayoutDocDto? parsed;
        try { parsed = JsonSerializer.Deserialize(bytes, HomeLayoutJsonCtx.Default.HomeLayoutDocDto); }
        catch (Exception ex)
        {
            detail = "The Home layout contains invalid data (" + ex.GetType().Name + ").";
            return ReadOutcome.Malformed;
        }

        if (parsed is null) { detail = "The Home layout contains no document."; return ReadOutcome.Malformed; }
        if (parsed.Version > CurrentVersion)
        {
            detail = $"Layout version {parsed.Version} is newer than supported version {CurrentVersion}.";
            return ReadOutcome.TooNew;
        }
        if (parsed.Version <= 0)
        {
            detail = $"The Home layout has an invalid version ({parsed.Version}).";
            return ReadOutcome.Malformed;
        }

        doc = parsed;
        return ReadOutcome.Ok;
    }

    void LogLoadFailed(string fault)
    {
        _log.Warn("home", "home.layout.load_failed",
            "The Home layout could not be loaded; the saved file was preserved.",
            WaveeLogField.Of("fault", fault));
    }

    public void Commit(HomeLayoutDocDto snapshot)
    {
        if (snapshot is null || _writesBlocked) return;

        snapshot.Version = CurrentVersion;
        snapshot.UpdatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        snapshot.AppVersion ??= AppVersion();

        long mine = Interlocked.Increment(ref _seq);
        var task = Task.Run(() => WriteOnPool(snapshot, mine));
        lock (_writeGate) _pending = task;
    }

    void WriteOnPool(HomeLayoutDocDto snapshot, long mine)
    {
        if (Interlocked.Read(ref _seq) != mine) return;
        var watch = Stopwatch.StartNew();
        lock (_writeGate)
        {
            if (Interlocked.Read(ref _seq) != mine) return;
            if (_writesBlocked) return;
            try
            {
                string? dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, HomeLayoutJsonCtx.Default.HomeLayoutDocDto);
                if (bytes.Length > MaxDocumentBytes)
                {
                    _saveFault = HomeLayoutSaveFault.DocumentTooLarge;
                    _log.Warn("home", "home.layout.save_failed",
                        "The Home layout was not saved.",
                        WaveeLogField.Of("fault", "document_too_large"),
                        WaveeLogField.Of("bytes", bytes.Length));
                    return;
                }

                using (var fs = new FileStream(TmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    fs.Write(bytes);
                    fs.Flush(flushToDisk: true);
                }

                if (File.Exists(_path))
                {
                    try { File.Replace(TmpPath, _path, BakPath, ignoreMetadataErrors: true); }
                    catch (Exception)
                    {
                        try { File.Copy(_path, BakPath, overwrite: true); } catch (Exception) { }
                        File.Move(TmpPath, _path, overwrite: true);
                    }
                }
                else
                {
                    File.Move(TmpPath, _path, overwrite: true);
                }

                _saveFault = HomeLayoutSaveFault.None;
            }
            catch (Exception ex)
            {
                _saveFault = HomeLayoutSaveFault.IoFailure;
                _log.Warn("home", "home.layout.save_failed",
                    "The Home layout was not saved.",
                    WaveeLogField.Of("fault", "io_failure"),
                    WaveeLogField.Of("exception_type", ex.GetType().Name),
                    WaveeLogField.Of("elapsed_ms", watch.ElapsedMilliseconds));
                try { if (File.Exists(TmpPath)) File.Delete(TmpPath); } catch (Exception) { }
            }
        }
    }

    public bool WaitForWrites(int timeoutMs = 5000)
    {
        Task? t;
        lock (_writeGate) t = _pending;
        if (t is null) return true;
        try { return t.Wait(timeoutMs); }
        catch (Exception) { return false; }
    }

    public void DiscardCorrupt()
    {
        lock (_writeGate)
        {
            try
            {
                if (File.Exists(_path)) File.Move(_path, CorruptPath, overwrite: true);
                if (File.Exists(BakPath)) File.Delete(BakPath);
                if (File.Exists(TmpPath)) File.Delete(TmpPath);
                _writesBlocked = false;
                _saveFault = HomeLayoutSaveFault.None;
            }
            catch (Exception ex)
            {
                _saveFault = HomeLayoutSaveFault.IoFailure;
                _log.Warn("home", "home.layout.discard_failed",
                    "The unreadable Home layout could not be set aside.",
                    WaveeLogField.Of("exception_type", ex.GetType().Name));
            }
        }
    }

    static string? s_appVersion;

    static string AppVersion()
    {
        if (s_appVersion is not null) return s_appVersion;
        try { s_appVersion = typeof(HomeLayoutStore).Assembly.GetName().Version?.ToString() ?? ""; }
        catch (Exception) { s_appVersion = ""; }
        return s_appVersion;
    }
}
