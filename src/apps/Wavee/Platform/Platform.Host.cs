// ── Platform/Platform.Host.cs ──────────────────────────────────────────────────────────────────────────────────────
// the Win32 seams, the detached-window owner, the zoom/display bridges
//
// Role: SHELL
// Owner: S
// Wave: 6
// Budget: 700 lines
// Spec: DERIVED (the plan's Platform/ bucket)
// Wave 0 first cut (orchestrator): the settings store, the profile paths, store.json, DPAPI, the log writer; owner S completes in Wave 6
//
// The SHELL half of everything `Platform.cs` declares as a seam: the registry-backed settings store, the profile path
// resolver, the portable `store.json` the credential slot and the device id live in, the Windows at-rest protector,
// and the log's file sink (its own queue + drain, daily and size rolling, retention, the engine `Diag` bridge, the
// flush-on-exit contract). Nothing here is on a hot path; everything here is allowed to touch the disk, and nothing
// here may block the UI thread for longer than one small file write (C9).
//
// EVERY PATH IS 0.2.9's. `%LOCALAPPDATA%\Wavee\store.json`, `%LOCALAPPDATA%\Wavee\logs\wavee.log` (daily-rolled to
// wavee-yyyyMMdd.log) and HKCU\Software\Wavee\Wavee for the settings. On a packaged run Windows redirects
// %LOCALAPPDATA% into the package's LocalCache — which is exactly why the startup line reports `logResolved=`: the
// two paths print identically and differ in every consequence, and a split settings/log store reads to a user as
// "the app forgot everything".

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.WindowsApi.Storage;

namespace Wavee;

// ── 1. the profile, the settings store and the local store ───────────────────────────────────────────────────────────

public static partial class Platform
{
    const string Publisher = "Wavee", Product = "Wavee";

    /// <summary>`%LOCALAPPDATA%\Wavee` — or the package's LocalCache on a packaged run, by OS redirection, never by
    /// a branch here. Created on demand.</summary>
    public static string LocalFolder
    {
        get
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Publisher);
            try { Directory.CreateDirectory(dir); } catch { }
            return dir;
        }
    }

    /// <summary>Where the rolling app log and the dealer archive live.</summary>
    public static string LogFolder => Path.Combine(LocalFolder, "logs");

    /// <summary>The portable key/value file: the credential blob and the device id.</summary>
    public static string StorePath => Path.Combine(LocalFolder, "store.json");

    static partial void HostOpenSettings() => UseSettings(RegistryAppSettings.Open(Publisher, Product));

    /// <summary>The OS UI locale, from Win32 rather than <c>CultureInfo.CurrentUICulture</c>: this app builds with
    /// <c>InvariantGlobalization</c>, under which that property is always the invariant culture and "system" would
    /// silently resolve to English for everyone. 0.2.9 went to <c>GetUserDefaultLocaleName</c> for the same reason.</summary>
    static partial void HostOsCulture(ref string culture)
    {
        if (!OperatingSystem.IsWindows()) return;
        try { culture = FluentGpu.WindowsApi.Globalization.WindowsCulture.GetUserDefaultLocaleName(); }
        catch { }
    }

    static partial void HostOpenCredentials()
    {
        ICredentialProtector protector = OperatingSystem.IsWindows() ? new DpapiProtector() : new NoOpProtector();
        UseCredentialSlot(new FileLocalStore(StorePath), protector);
    }

    static partial void HostOpenLog()
    {
        // The persisted levels are -1 = "use the build default" until owner S ports LogCapturePolicy, which is the
        // ONE place allowed to resolve that; until then a stored level is honoured verbatim and -1 means Info.
        int min = Settings.Get(Keys.LogMinLevel);
        int file = Settings.Get(Keys.LogFileMinLevel);
        Log.Configure(Path.Combine(LogFolder, "wavee.log"),
            min >= 0 ? (WaveeLogLevel)min : WaveeLogLevel.Info,
            file >= 0 ? (WaveeLogLevel)file : WaveeLogLevel.Info);

        Diag.Sink = Log.DiagSink;   // fold engine diagnostics into the one always-on stream

        Log.Event(WaveeLogLevel.Info, "app", "startup", "Wavee starting", null, -1, null,
            WaveeLogField.Of("pid", Environment.ProcessId),
            WaveeLogField.Of("log", Log.FilePath ?? "?"),
            // Where that path REALLY lands. MSIX redirects %LOCALAPPDATA% into the package's LocalCache; an
            // unpackaged run writes the literal path. CLAUDE.md's packaged-run rule reads this field.
            WaveeLogField.Of("logResolved", FinalPath.Resolve(LogFolder) ?? "?"),
            WaveeLogField.Of("framework", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription),
            WaveeLogField.Of("os", System.Runtime.InteropServices.RuntimeInformation.OSDescription));
    }
}

/// <summary><see cref="IAppSettings"/> over the engine's AppDataStore (HKCU, unpackaged; the package's own settings
/// container when packaged). Every access is DEFENSIVE — a storage failure, or no store at all, falls back to the
/// key's default and never throws into the UI. Type dispatch is a closed switch over the store's supported scalars,
/// AOT-clean (no reflection) and BOX-FREE: the `typeof(T) ==` arms fold at compile time for every value-type
/// instantiation, and <c>Unsafe.As</c> reinterprets the ref rather than paying an allocation per preference read.</summary>
sealed class RegistryAppSettings : IAppSettings
{
    readonly AppDataStore? _store;

    RegistryAppSettings(AppDataStore? store) => _store = store;

    public static IAppSettings Open(string publisher, string product)
    {
        try { return new RegistryAppSettings(AppDataStore.ForUnpackaged(publisher, product)); }
        catch { return new RegistryAppSettings(null); }   // storage unavailable → reads return defaults, writes no-op
    }

    public T Get<T>(SettingKey<T> key)
    {
        if (_store is null) return key.Default;
        T def = key.Default;
        try
        {
            if (typeof(T) == typeof(bool))
            {
                bool v = _store.GetBool(key.Name, Unsafe.As<T, bool>(ref def));
                return Unsafe.As<bool, T>(ref v);
            }
            if (typeof(T) == typeof(int))
            {
                int v = _store.GetInt(key.Name, Unsafe.As<T, int>(ref def));
                return Unsafe.As<int, T>(ref v);
            }
            if (typeof(T) == typeof(long))
            {
                long v = _store.GetLong(key.Name, Unsafe.As<T, long>(ref def));
                return Unsafe.As<long, T>(ref v);
            }
            if (typeof(T) == typeof(float))
            {
                float v = (float)_store.GetDouble(key.Name, Unsafe.As<T, float>(ref def));
                return Unsafe.As<float, T>(ref v);
            }
            if (typeof(T) == typeof(double))
            {
                double v = _store.GetDouble(key.Name, Unsafe.As<T, double>(ref def));
                return Unsafe.As<double, T>(ref v);
            }
            if (typeof(T) == typeof(string))
            {
                // A plain cast, not Unsafe.As: T IS string on this arm, so neither direction boxes.
                string fallback = (string)(object)def!;
                string v = _store.GetString(key.Name, fallback) ?? fallback;
                return (T)(object)v;
            }
        }
        catch { }
        return key.Default;   // unsupported T, or a storage failure — the key's own answer, never an exception
    }

    public void Set<T>(SettingKey<T> key, T value)
    {
        if (_store is null || value is null) return;
        try
        {
            if (typeof(T) == typeof(bool)) { _store.SetBool(key.Name, Unsafe.As<T, bool>(ref value)); return; }
            if (typeof(T) == typeof(int)) { _store.SetInt(key.Name, Unsafe.As<T, int>(ref value)); return; }
            if (typeof(T) == typeof(long)) { _store.SetLong(key.Name, Unsafe.As<T, long>(ref value)); return; }
            if (typeof(T) == typeof(float)) { _store.SetDouble(key.Name, Unsafe.As<T, float>(ref value)); return; }
            if (typeof(T) == typeof(double)) { _store.SetDouble(key.Name, Unsafe.As<T, double>(ref value)); return; }
            if (typeof(T) == typeof(string)) { _store.SetString(key.Name, (string)(object)value); return; }
        }
        catch { }
    }
}

/// <summary>The portable key/value file behind <see cref="ILocalStore"/> — one JSON object, the whole thing rewritten
/// on every Set, which is correct at this size (a credential and a device id) and wrong at any larger one. The write
/// is fsync'd and then RENAMED into place, so neither a crash nor a power loss can leave a half-written credential;
/// a corrupt or locked file starts fresh rather than crashing the app over a settings file. On POSIX the temp file is
/// chmod 0600 before the rename, so the credential is never world-readable.</summary>
public sealed class FileLocalStore : ILocalStore
{
    readonly string _path;
    readonly Lock _gate = new();
    readonly Dictionary<string, string> _data;

    public FileLocalStore(string filePath)
    {
        _path = filePath;
        _data = LoadFile(filePath);
    }

    public string? Get(string key)
    {
        lock (_gate) return _data.TryGetValue(key, out var v) ? v : null;
    }

    public void Set(string key, string value)
    {
        lock (_gate) { _data[key] = value; Save(); }
    }

    public void Remove(string key)
    {
        lock (_gate) { if (_data.Remove(key)) Save(); }
    }

    static Dictionary<string, string> LoadFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return new();
            return JsonSerializer.Deserialize(File.ReadAllText(path), LocalStoreJson.Default.DictionaryStringString) ?? new();
        }
        catch { return new(); }
    }

    void Save()
    {
        try
        {
            string? dir = Path.GetDirectoryName(_path);
            if (dir is not null) Directory.CreateDirectory(dir);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(_data, LocalStoreJson.Default.DictionaryStringString);
            var tmp = _path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);   // fsync — survive power loss, not just a process crash
            }
            if (!OperatingSystem.IsWindows())
                try { File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
            File.Move(tmp, _path, overwrite: true);   // write-then-rename
        }
        catch (Exception ex) { Log.Warn("store", "could not persist store.json", ex); }
    }
}

[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class LocalStoreJson : JsonSerializerContext { }

/// <summary>The Windows at-rest protector — one swap behind the portable <see cref="ICredentialProtector"/> seam.
/// DPAPI CurrentUser scope: the blob is decryptable only by this Windows user on this machine. The "dpapi" scheme tag
/// means the blob is cleanly rejected (→ re-auth) if the profile is opened elsewhere, rather than mis-decrypted.
/// (0.2.9 also carried a macOS/Linux Keychain/libsecret swap. It is NOT ported: 0.3 is a Windows NativeAOT app and
/// the seam plus <see cref="NoOpProtector"/> is what keeps the port possible, not a keystore CLI shim nothing runs.)</summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiProtector : ICredentialProtector
{
    public string Scheme => "dpapi";
    public byte[] Protect(byte[] plaintext) => ProtectedData.Protect(plaintext, optionalEntropy: null, DataProtectionScope.CurrentUser);
    public byte[] Unprotect(byte[] ciphertext) => ProtectedData.Unprotect(ciphertext, optionalEntropy: null, DataProtectionScope.CurrentUser);
}

// ── 2. the log's file sink ───────────────────────────────────────────────────────────────────────────────────────────

public static partial class Log
{
    const long DefaultMaxFileBytes = 10L * 1024L * 1024L;
    const int DefaultRetainedFiles = 7;
    const int MaxQueueLines = 8192;

    static readonly Lock s_fileGate = new();
    static readonly Lock s_writeGate = new();
    static readonly Queue<WaveeLogEntry> s_fileQueue = new();
    static bool s_drainScheduled;
    static int s_droppedFileLines;

    // The persistent sink handle — touched only under s_writeGate (the drain and Flush serialize on it).
    static FileStream? s_fs;
    static StreamWriter? s_sw;
    static bool s_fileSinkFailed;

    static string? s_basePath;    // as configured (wavee.log); the active dated file is derived from it
    static string? s_openPath;    // the path the sink currently appends to
    static long s_maxFileBytes = DefaultMaxFileBytes;
    static int s_retainedFiles = DefaultRetainedFiles;

    /// <summary>The file lines are being appended to RIGHT NOW — the dated file (wavee-yyyyMMdd.log). Recomputed on
    /// read, so it stays correct across a midnight roll.</summary>
    public static string? FilePath => ActiveFilePath();

    /// <summary>The CONFIGURED path (wavee.log). Session discovery derives the whole rolling file set from THIS —
    /// deriving it from <see cref="FilePath"/> would glob only one day's files.</summary>
    public static string? BasePath => s_basePath;

    /// <summary>Point the sink at a file and set the two level gates. The main app log is DAILY: one file per local
    /// calendar day, and 0.2.9's single ever-growing wavee.log is migrated into the dated set on first launch so the
    /// session picker keeps seeing its history.</summary>
    public static void Configure(string basePath, WaveeLogLevel minLevel, WaveeLogLevel fileMinLevel,
        long? maxFileBytes = null, int? retainedFiles = null)
    {
        if (!string.Equals(s_basePath, basePath, StringComparison.OrdinalIgnoreCase))
            lock (s_writeGate) CloseStream();          // path change → reopen on the next batch

        s_basePath = basePath;
        MinLevel = minLevel;
        FileMinLevel = fileMinLevel;
        s_maxFileBytes = maxFileBytes.GetValueOrDefault(DefaultMaxFileBytes);
        s_retainedFiles = Math.Max(1, retainedFiles.GetValueOrDefault(DefaultRetainedFiles));

        try { Directory.CreateDirectory(Path.GetDirectoryName(basePath)!); } catch { }
        MigrateLegacyBaseFile(basePath);
        PruneRolledFiles(Path.GetDirectoryName(basePath),
            Path.GetFileNameWithoutExtension(basePath), Path.GetExtension(basePath));
    }

    /// <summary>Best-effort synchronous drain of the queued lines. Safe from a crash path, and the whole of the
    /// flush-on-exit contract: the sink writes on a pool thread, so a process teardown right after the last line —
    /// the failure reason, usually — tears it down before that line ever reaches the file.</summary>
    public static void Flush()
    {
        try { DrainFileQueue(); } catch { }
    }

    /// <summary>Test hook: drain synchronously AND release the handle, so a test can read the file back.</summary>
    public static void FlushAndClose()
    {
        Flush();
        lock (s_writeGate) CloseStream();
    }

    static partial void FileWrite(WaveeLogEntry entry)
    {
        if (s_basePath is null) return;
        lock (s_fileGate)
        {
            if (s_fileQueue.Count >= MaxQueueLines) { s_fileQueue.Dequeue(); s_droppedFileLines++; }
            s_fileQueue.Enqueue(entry);
            if (s_drainScheduled) return;
            s_drainScheduled = true;
        }
        ThreadPool.UnsafeQueueUserWorkItem(static (object? _) => DrainFileQueue(), null);
    }

    static void DrainFileQueue()
    {
        while (true)
        {
            WaveeLogEntry[] batch;
            int dropped;
            lock (s_fileGate)
            {
                if (s_fileQueue.Count == 0) { s_drainScheduled = false; return; }
                dropped = s_droppedFileLines;
                s_droppedFileLines = 0;
                int n = Math.Min(512, s_fileQueue.Count);
                batch = new WaveeLogEntry[n];
                for (int i = 0; i < n; i++) batch[i] = s_fileQueue.Dequeue();
            }
            WriteBatch(batch, dropped);
        }
    }

    static void WriteBatch(WaveeLogEntry[] entries, int dropped)
    {
        var path = ActiveFilePath();
        if (path is null || (entries.Length == 0 && dropped == 0)) return;

        lock (s_writeGate)
        {
            try
            {
                // Midnight rolled the active date → release yesterday's file; the open below starts today's.
                if (s_sw is not null && !string.Equals(s_openPath, path, StringComparison.OrdinalIgnoreCase))
                    CloseStream();
                EnsureStream(path);
                if (dropped > 0)
                    s_sw!.WriteLine("W [log] file sink dropped queued lines count=" + dropped.ToString(CultureInfo.InvariantCulture));
                for (int i = 0; i < entries.Length; i++) s_sw!.WriteLine(FormatFileLine(in entries[i]));
                s_sw!.Flush();
                if (s_fileSinkFailed)
                {
                    s_fileSinkFailed = false;
                    PushInternalRingOnly(WaveeLogLevel.Info, "file sink recovered path=" + path);
                }
                if (s_fs is { } fs && fs.Length >= s_maxFileBytes) CloseStream();   // roll on the next EnsureStream
            }
            catch
            {
                CloseStream();   // reopen next batch → feeds the recovery signal above
                if (!s_fileSinkFailed)
                {
                    s_fileSinkFailed = true;
                    PushInternalRingOnly(WaveeLogLevel.Warning, "file sink write failed - dropping lines path=" + path);
                }
            }
        }
    }

    static string? ActiveFilePath()
    {
        string? basePath = s_basePath;
        return basePath is null ? null : DatedPath(basePath, DateTime.Now);
    }

    static string DatedPath(string basePath, DateTime day)
        => Path.Combine(Path.GetDirectoryName(basePath) ?? "",
            Path.GetFileNameWithoutExtension(basePath) + "-" + day.ToString("yyyyMMdd", CultureInfo.InvariantCulture)
            + Path.GetExtension(basePath));

    /// <summary>One-time migration to daily files: the pre-split single wavee.log is RENAMED into the dated set,
    /// stamped with its own last write, so its history stays visible and the writer never appends to the un-dated
    /// name again. A failure (another instance still holds it open, a name collision) leaves it alone — harmless.</summary>
    static void MigrateLegacyBaseFile(string basePath)
    {
        try
        {
            if (!File.Exists(basePath)) return;
            string dir = Path.GetDirectoryName(basePath) ?? "";
            string root = Path.GetFileNameWithoutExtension(basePath);
            string ext = Path.GetExtension(basePath);
            string stamp = File.GetLastWriteTime(basePath).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            File.Move(basePath, Path.Combine(dir, root + "-" + stamp + ext), overwrite: false);
        }
        catch { }
    }

    static void EnsureStream(string path)
    {
        if (s_sw is not null) return;
        string? dir = Path.GetDirectoryName(path);
        if (dir is not null) Directory.CreateDirectory(dir);
        RollIfNeeded(path, dir);
        // Daily retention: keep the newest N of the WHOLE dated set (dated days AND their intra-day size-rolls), so
        // the folder stays about a week deep instead of accreting one file per day forever.
        if (s_basePath is { } bp)
            PruneRolledFiles(Path.GetDirectoryName(bp), Path.GetFileNameWithoutExtension(bp), Path.GetExtension(bp));
        s_fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        s_sw = new StreamWriter(s_fs, new UTF8Encoding(false));
        s_openPath = path;
    }

    static void CloseStream()
    {
        try { s_sw?.Flush(); } catch { }
        try { s_sw?.Dispose(); } catch { }   // disposes the underlying FileStream
        s_sw = null;
        s_fs = null;
        s_openPath = null;
    }

    static void RollIfNeeded(string path, string? dir)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length < s_maxFileBytes) return;
            string root = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            string rolled = Path.Combine(dir ?? "",
                root + "-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ext);
            File.Move(path, rolled, overwrite: true);
            PruneRolledFiles(dir, root, ext);
        }
        catch { }
    }

    static void PruneRolledFiles(string? dir, string root, string ext)
    {
        if (dir is null) return;
        try
        {
            var files = Directory.GetFiles(dir, root + "-*" + ext);
            Array.Sort(files, static (a, b) => File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a)));
            for (int i = s_retainedFiles; i < files.Length; i++)
                try { File.Delete(files[i]); } catch { }
        }
        catch { }
    }

    /// <summary>The action plugged into the engine's `Diag` sink. GPU-forensics lines (adapter identity, the
    /// device-lost dump, the fence-stall watchdog, present-time DWM glitches, the wake census) route at Warning so
    /// they clear the default Info FILE threshold and land in wavee-yyyyMMdd.log — the one channel a user can send
    /// after an intermittent hang. Everything else stays Debug-gated by MinLevel.</summary>
    public static Action<string> DiagSink => static s =>
    {
        if (GpuForensic(s)) Warn("engine", s); else Debug("engine", s);
    };

    /// <summary>True for the sink-routed engine lines that name a GPU stall, loss, recovery or adapter — the evidence
    /// that must survive the Info file gate. Allocation-free ordinal prefix/substring checks.</summary>
    static bool GpuForensic(string s) =>
        s.StartsWith("[d3d12.adapter]", StringComparison.Ordinal)
        // The compositor-clock latch silently drops production from vblank pacing to a wall-clock timer for the rest
        // of the session; it must reach the Info file.
        || s.StartsWith("[compositor-clock]", StringComparison.Ordinal)
        || s.StartsWith("[device-lost]", StringComparison.Ordinal)
        // The always-on wake census: one line per 30 s naming the frame rate and WHICH wake term held the loop awake.
        // It has to clear the Info gate or it answers nothing after the fact — "pinned at panel rate, cause unknown"
        // is precisely the report this instrument exists to make answerable.
        || s.StartsWith("[wake]", StringComparison.Ordinal)
        || s.StartsWith("[repaint]", StringComparison.Ordinal)
        || s.StartsWith("[repaint-causes]", StringComparison.Ordinal)
        || s.StartsWith("[repaint-raw-sample]", StringComparison.Ordinal)
        // Present-queue depth and the window's actual monitor/refresh are both invisible from the outside: a queue
        // two frames deep still reports a healthy frame rate, and a window on a 50 Hz secondary reports the same fps
        // as one on the 120 Hz panel unless the mode line says otherwise. Both are once-per-edge.
        || s.StartsWith("[d3d12.present]", StringComparison.Ordinal)
        || s.StartsWith("[d3d12.display]", StringComparison.Ordinal)
        || s.StartsWith("[d3d12.stall]", StringComparison.Ordinal)
        || s.StartsWith("[d3d12] ", StringComparison.Ordinal)
        || s.Contains("dwmGlitches", StringComparison.Ordinal);
}

// ── 3. glyphs ────────────────────────────────────────────────────────────────────────────────────────────────────────

public static partial class Glyphs
{
    /// <summary>The icon face is the BUNDLED file, not the system family of the same name — see
    /// <see cref="WaveeFonts.Icons"/>. Set BEFORE the harness runs: the host interns `Theme.IconFont` once for the
    /// overlay scrollbar arrows and every control reads it at render.</summary>
    static partial void RegisterHost() => Theme.IconFont = WaveeFonts.Icons;
}
