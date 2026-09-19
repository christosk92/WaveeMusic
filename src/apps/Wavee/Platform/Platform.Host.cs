// ── Platform/Platform.Host.cs ──────────────────────────────────────────────────────────────────────────────────────
// the Win32 seams, the detached-window owner, the zoom/display bridges
//
// Role: SHELL
// Owner: S
// Wave: 6
// Budget: 700 lines
// Spec: DERIVED (the plan's Platform/ bucket)
// Wave 0 first cut (orchestrator): the settings store, the profile paths, store.json, DPAPI, the log writer; owner S completes in Wave 6
// Wave 6 (owner S): the settings isolation (`FileAppSettings`, the `--fake` in-memory store), the crash-writer install,
// the factory reset + restart broker spawn, `Platform.Version`, the NLM cost host, the ambient power poll (G-086, G-094,
// G-198, G-199). Its CORE halves are `Platform.Settings.cs`.
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

    /// <summary>Where the profile lives. "" = `%LOCALAPPDATA%\Wavee` (packaged: the LocalCache, by OS redirection). Set
    /// BEFORE <see cref="Boot"/> and never after: every path (store.json, logs, library.db, cache/) hangs off it. The GUI
    /// and the headless host both honour `--profile <dir>` through `Diagnostics.Probe.ProfileArg` (headless plan §3.6).</summary>
    public static string ProfileRoot { get; set; } = "";

    /// <summary>`%LOCALAPPDATA%\Wavee` — or the package's LocalCache on a packaged run, by OS redirection, never by
    /// a branch here — unless <see cref="ProfileRoot"/> names another folder. Created on demand.</summary>
    public static string LocalFolder
    {
        get
        {
            string dir = ProfileRoot.Length > 0
                ? ProfileRoot
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Publisher);
            try { Directory.CreateDirectory(dir); } catch { }
            return dir;
        }
    }

    /// <summary>Where the rolling app log and the dealer archive live.</summary>
    public static string LogFolder => Path.Combine(LocalFolder, "logs");

    /// <summary>The portable key/value file: the credential blob and the device id.</summary>
    public static string StorePath => Path.Combine(LocalFolder, "store.json");

    /// <summary>The store <see cref="Backing"/> names: an in-memory overlay over defaults for `--fake` (never the
    /// registry), a JSON file inside the profile folder for `--profile`, HKCU otherwise.</summary>
    static partial void HostOpenSettings() => UseSettings(Backing switch
    {
        SettingsBacking.Memory => new Diagnostics.Headless.OverlaySettings(DefaultsOnlySettings.Instance),
        SettingsBacking.ProfileFile => new FileAppSettings(Path.Combine(LocalFolder, ProfileSettingsFileName)),
        _ => RegistryAppSettings.Open(Publisher, Product),
    });

    /// <summary>The raw store <see cref="Boot"/> installed (never the facade). The headless host overlays THIS — an
    /// overlay over the facade would read itself.</summary>
    public static IAppSettings BackingSettings => s_backing ?? DefaultsOnlySettings.Instance;

    /// <summary>The OS UI locale, from Win32 rather than <c>CultureInfo.CurrentUICulture</c>: this app builds with
    /// <c>InvariantGlobalization</c>, under which that property is always the invariant culture and "system" would
    /// silently resolve to English for everyone. 0.2.9 went to <c>GetUserDefaultLocaleName</c> for the same reason.</summary>
    static partial void HostOsCulture(ref string culture)
    {
        if (!OperatingSystem.IsWindows()) return;
        try { culture = FluentGpu.WindowsApi.Globalization.WindowsCulture.GetUserDefaultLocaleName(); }
        catch { }
    }

    static partial void HostOpenLocale(ref AppLocale locale)
    {
        FluentGpu.Localization.Localization.DefaultCulture = AppLocale.English.UiCulture;
        FluentGpu.Localization.Localization.LoadFolder(Path.Combine(AppContext.BaseDirectory, "assets", "loc"));
        // English first, always: an unsupported culture must never leave a stale process-global culture selected.
        FluentGpu.Localization.Localization.SetCulture(AppLocale.English.UiCulture);
        if (!FluentGpu.Localization.Localization.TrySetCulture(locale.UiCulture))
            FluentGpu.Localization.Localization.SetCulture(AppLocale.English.UiCulture);
        string effective = FluentGpu.Localization.Localization.CurrentCulture;
        locale = new AppLocale(effective, AppLocale.LanguageOf(effective));
    }

    static partial void HostOpenCredentials()
    {
        ICredentialProtector protector = OperatingSystem.IsWindows() ? new DpapiProtector() : new NoOpProtector();
        UseCredentialSlot(new FileLocalStore(StorePath), protector);
    }

    static partial void HostOpenLog()
    {
        // The persisted levels are -1 = "use the build default"; LogCapturePolicy is the ONE place that resolves that,
        // so this launch path and the logs panel's runtime toggles can never disagree.
        Log.Configure(Path.Combine(LogFolder, "wavee.log"),
            LogCapturePolicy.Resolve(Settings.Get(Keys.LogMinLevel), LogCapturePolicy.BuildDefaultMinLevel),
            LogCapturePolicy.Resolve(Settings.Get(Keys.LogFileMinLevel), LogCapturePolicy.BuildDefaultFileLevel));

        Diag.Sink = Log.DiagSink;   // fold engine diagnostics into the one always-on stream

        Log.Event(WaveeLogLevel.Info, "app", "startup", "Wavee starting", null, -1, null,
            WaveeLogField.Of("pid", Environment.ProcessId),
            WaveeLogField.Of("log", Log.FilePath ?? "?"),
            // Where that path REALLY lands. MSIX redirects %LOCALAPPDATA% into the package's LocalCache; an
            // unpackaged run writes the literal path. CLAUDE.md's packaged-run rule reads this field.
            WaveeLogField.Of("logResolved", FinalPath.Resolve(LogFolder) ?? "?"),
            WaveeLogField.Of("framework", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription),
            WaveeLogField.Of("os", System.Runtime.InteropServices.RuntimeInformation.OSDescription));
        // The update end-to-end harness waits for this exact line: category app, event startup.
        if (Array.IndexOf(Environment.GetCommandLineArgs(), RelaunchedAfterUpdateFlag) >= 0)
            Log.Event(WaveeLogLevel.Info, "app", "startup", "relaunched by Windows after an update", null, -1, null,
                WaveeLogField.Of("flag", RelaunchedAfterUpdateFlag));
    }

    // ── 1.1 the crash writers (G-094, S half) ───────────────────────────────────────────────────────────────────────

    /// <summary>A crash on ANY thread leaves a report on disk, arms <c>crash.pendingReport</c> for the next launch and
    /// marks the run "crashed". The report body is <c>Diagnostics.CrashReport</c> (beside the files list it shares its
    /// naming and prune rule with); `Shell.Host.cs`'s own crash net only logs. Everything here is best-effort (we are
    /// terminating), but a failure is LOGGED, never swallowed — a silent catch here is how a lost report goes unnoticed.
    /// The Shell's app-loop catch rethrows into this handler, and <c>CrashReport.Write</c> is idempotent per process.</summary>
    static partial void HostInstallCrashWriters()
    {
        AppDomain.CurrentDomain.UnhandledException += static (_, e) =>
        {
            if (e.ExceptionObject is not Exception fatal) return;
            try
            {
                string report = Diagnostics.CrashReport.Write(fatal, Log.FilePath);
                if (report.Length > 0) Settings.Set(Keys.PendingCrashReport, report);
            }
            catch (Exception writeEx)
            {
                try { Log.Warn("crash", "Crash report could not be written or armed", writeEx); } catch { }
            }
            try { RunMarker.MarkCrashed(Settings); } catch { }
            try { Log.Flush(); } catch { }
        };
    }

    // ── 1.2 the factory reset and the restart broker (G-094, S half) ────────────────────────────────────────────────

    /// <summary>The token Wavee appends to the restart command line it registers before an MSIX deployment, so the
    /// process Windows brings back AFTER the update can say so in its log. INERT: one log line, nothing branches on it.</summary>
    public const string RelaunchedAfterUpdateFlag = "--relaunched-after-update";

    /// <summary>The marker lives in %TEMP%, outside every wipe root.</summary>
    public static string FactoryResetMarkerPath => Path.Combine(Path.GetTempPath(), FactoryResetPlan.MarkerFileName);

    /// <summary>What a reset wipes by default: the profile folder and %TEMP%\Wavee.</summary>
    public static IReadOnlyList<string> FactoryResetDefaultRoots() => [LocalFolder, Path.Combine(Path.GetTempPath(), Product)];

    /// <summary>Arm a reset, spawn the restart broker, and END THIS PROCESS (Settings ▸ Storage ▸ Factory reset, after its
    /// confirm). The wipe runs in the next process; if the spawn fails the marker is still on disk, so a manual launch
    /// applies it anyway.</summary>
    public static void RequestFactoryResetAndRelaunch(IEnumerable<string>? extraRoots = null)
    {
        var lines = FactoryResetPlan.MarkerLines(extraRoots, FactoryResetDefaultRoots());
        Directory.CreateDirectory(Path.GetDirectoryName(FactoryResetMarkerPath)!);
        File.WriteAllLines(FactoryResetMarkerPath, lines);
        Log.Info("app", "factory reset armed; relaunching (extra roots " + lines.Count.ToString(CultureInfo.InvariantCulture) + ")");
        Log.Flush();
        RestartAfterExit();
        Environment.Exit(0);
    }

    static partial void HostApplyFactoryReset()
    {
        string marker = FactoryResetMarkerPath;
        if (!File.Exists(marker)) return;
        string[] extras;
        try { extras = File.ReadAllLines(marker); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { extras = []; }
        foreach (string root in FactoryResetPlan.Roots(FactoryResetDefaultRoots(), extras)) DeleteTree(root);
        // The registry half only when this launch's settings ARE the registry: a `--profile` run's reset is its folder.
        if (Backing == SettingsBacking.Registry && OperatingSystem.IsWindows())
        {
            try { AppDataStore.ForUnpackaged(Publisher, Product).Clear(); } catch { }
            try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\" + Publisher, throwOnMissingSubKey: false); } catch { }
        }
        try { File.Delete(marker); } catch { }
    }

    static void DeleteTree(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        string path;
        try { path = Path.GetFullPath(raw); } catch { return; }
        try
        {
            if (File.Exists(path)) { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); return; }
            if (!Directory.Exists(path)) return;
            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                try { File.SetAttributes(file, FileAttributes.Normal); File.Delete(file); } catch { }
            Directory.Delete(path, recursive: true);
        }
        catch { }
    }

    /// <summary>Spawn the broker (this exe as <c>--relaunch-after &lt;pid&gt;</c>) that starts a fresh Wavee once THIS pid
    /// is gone — which is what releases the instance mutex and library.db. The caller MUST then end the process. Never
    /// throws: a failed spawn leaves the user to relaunch by hand, strictly better than breaking the shutdown path.</summary>
    public static void RestartAfterExit()
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("--relaunch-after");
            psi.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex) { Log.Warn("app", "restart broker could not be spawned", ex); }
    }

    // ── 1.3 the build stamp (G-198) ──────────────────────────────────────────────────────────────────────────────────

    static WaveeVersionInfo? s_version;

    /// <summary>This build's stamp, read once from the entry assembly's metadata (the attributes `Wavee.csproj` writes).
    /// About, the crash header, the update checker and the what's-new gate read THIS, never AssemblyName.Version.</summary>
    public static WaveeVersionInfo Version => s_version ??= ReadVersion();

    static WaveeVersionInfo ReadVersion()
    {
        try
        {
            var asm = System.Reflection.Assembly.GetEntryAssembly() ?? typeof(Platform).Assembly;
            var pairs = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var a in System.Reflection.CustomAttributeExtensions.GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(asm))
                if (a.Value is not null) pairs[a.Key] = a.Value;
            string? inf = System.Reflection.CustomAttributeExtensions
                .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(asm)?.InformationalVersion;
            return WaveeVersionInfo.Parse(inf, pairs);
        }
        catch { return WaveeVersionInfo.Parse(null, null); }
    }

    // ── 1.4 the network cost host (ch 29 W9) ─────────────────────────────────────────────────────────────────────────

    public static partial class Network
    {
        /// <summary>The fallback poll for a host whose NLM cost connection point is missing (the push is the fast path).</summary>
        const int RefreshMs = 60_000;

        static int s_installed, s_refreshing;
        static Action<Action>? s_post;
        static Timer? s_poll;
        static IDisposable? s_connectivity, s_costEvents;

        /// <summary>UI THREAD, once, from the GUI composition (`Diagnostics.Install`): seed the caps, read the cost now,
        /// subscribe NLM's connectivity and cost pushes, and arm the 60 s fallback. The headless host does not install it
        /// (an unknown cost is unmetered, so its quality verb is never capped by a policy it cannot see).</summary>
        public static void Install(Action<Action> post)
        {
            if (Interlocked.Exchange(ref s_installed, 1) != 0) return;
            s_post = post;
            try { SeedCaps(Settings); } catch (Exception ex) { Log.Warn("network", "metered caps could not be seeded", ex); }
            Refresh();
            s_poll = new Timer(static _ => Refresh(), null, RefreshMs, RefreshMs);
            try { s_connectivity = FluentGpu.WindowsApi.Network.NetworkStatus.Subscribe(static _ => Refresh()); } catch { s_connectivity = null; }
            try { s_costEvents = FluentGpu.WindowsApi.Network.NetworkStatus.SubscribeCost(static c => Publish(c)); } catch { s_costEvents = null; }
        }

        public static void Shutdown()
        {
            try { s_costEvents?.Dispose(); } catch { }
            try { s_connectivity?.Dispose(); } catch { }
            try { s_poll?.Dispose(); } catch { }
            s_costEvents = s_connectivity = null;
            s_poll = null;
        }

        static void Refresh()
        {
            if (Interlocked.Exchange(ref s_refreshing, 1) != 0) return;
            _ = RefreshAsync();
        }

        static async Task RefreshAsync()
        {
            try
            {
                var cost = FluentGpu.WindowsApi.Network.NetworkCost.Unknown;
                try { cost = await FluentGpu.WindowsApi.Network.NetworkStatus.ReadCostAsync().ConfigureAwait(false); }
                catch { cost = FluentGpu.WindowsApi.Network.NetworkCost.Unknown; }
                Publish(cost);
            }
            finally { Interlocked.Exchange(ref s_refreshing, 0); }
        }

        /// <summary>Any thread → the UI thread → <see cref="Apply"/>.</summary>
        static void Publish(FluentGpu.WindowsApi.Network.NetworkCost cost)
        {
            if (s_post is { } post) post(() => Apply(cost));
        }
    }

    // ── 1.5 the ambient power poll (G-086) ───────────────────────────────────────────────────────────────────────────

    static FluentGpu.Hosting.AppHost? s_powerHost;
    static AmbientPower.Debounce s_power;

    /// <summary>Bind the ambient cadence to the host and apply the launch verdict immediately (no debounce at launch).
    /// Called once per launch from the engine's pre-loop hook (`FluentApp.DiagnosticRun`, installed by
    /// `Diagnostics.Probe.InstallGuiArms`) — the only app-reachable point that holds the host. The recurring poll
    /// itself is NOT a raw <see cref="Timer"/> (that kept waking a minimized/suspended window): <see cref="Shell"/>'s
    /// root component hosts it on a <c>UseInterval</c> tick — engine's per-frame timer queue, which auto-pauses while
    /// parked/minimized and resumes cleanly — calling <see cref="TickAmbientPower"/> every <see cref="AmbientPower.PollMs"/>.</summary>
    public static void AttachAmbientPower(FluentGpu.Hosting.AppHost host)
    {
        s_powerHost = host;
        host.InactiveFrameIntervalMs = Design.Cadence.InactiveFrameIntervalMs;
        s_power = AmbientPower.Debounce.Start(ReadPlugged(), System.Diagnostics.Stopwatch.GetTimestamp());
        ApplyPower();
    }

    /// <summary>The recurring half of the ambient power poll (ch 00 §9.5, G-086) — the root component's
    /// <c>UseInterval</c> tick calls this every <see cref="AmbientPower.PollMs"/>. There is no power-source change
    /// notification to subscribe to, so this stays a poll; hosting it on the frame-clock timer queue rather than a
    /// raw thread-pool <see cref="Timer"/> is what lets it go quiet while the window is minimized or suspended.</summary>
    public static void TickAmbientPower()
    {
        if (s_powerHost is null) return;
        if (AmbientPower.Step(ref s_power, ReadPlugged(), System.Diagnostics.Stopwatch.GetTimestamp(),
                System.Diagnostics.Stopwatch.Frequency))
            ApplyPower();
    }

    /// <summary>Stop the power poll (the exit tail, via `Diagnostics.Shutdown`). The root component's `UseInterval`
    /// tick itself dies with the component at process exit; this just makes a stray tick a no-op in the meantime.</summary>
    public static void DetachAmbientPower()
    {
        s_powerHost = null;
    }

    static bool ReadPlugged()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(8)) return true;
        try
        {
            var status = FluentGpu.WindowsApi.Power.PowerSession.ReadPower();
            // The same read answers the engine's material policy: WinUI drops acrylic to its solid fallback under
            // energy saver, and this 2 s poll is the only edge that sees the user toggle it MID-session (App.cs reads
            // it at startup and on suspend/resume, which cannot). Fails open, exactly like the cadence below.
            FluentGpu.Dsl.Materials.EnergySaver = status.EnergySaverOn;
            return AmbientPower.Plugged(true, status);
        }
        catch
        {
            FluentGpu.Dsl.Materials.EnergySaver = false;
            return AmbientPower.Plugged(false, default);          // a failed read resolves plugged, never dims the app
        }
    }

    static void ApplyPower()
    {
        if (s_powerHost is not { } host) return;
        float hz = AmbientPower.LoopHzFor(s_power.Applied);
        host.Animation.DefaultLoopHz = hz;
        Log.Info("app", "ambient cadence " + (s_power.Applied ? "plugged" : "battery") + " loopHz=" + hz.ToString(CultureInfo.InvariantCulture));
    }
}

/// <summary><see cref="IAppSettings"/> in one JSON object inside a profile folder — a `--profile &lt;dir&gt;` run's store,
/// so an isolated launch never touches HKCU. The key/value contract is the registry store's: the same key names, the
/// same scalar types, values rendered invariant, a missing or unparsable value answering the key's default, and every
/// failure swallowed. Write-through with an fsync'd write-then-rename (a scratch profile's writes are rare).</summary>
public sealed class FileAppSettings : IAppSettings
{
    readonly string _path;
    readonly Lock _gate = new();
    readonly Dictionary<string, string> _data;

    public FileAppSettings(string filePath)
    {
        _path = filePath;
        try
        {
            _data = File.Exists(filePath)
                ? JsonSerializer.Deserialize(File.ReadAllText(filePath), LocalStoreJson.Default.DictionaryStringString) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { _data = new(); }
    }

    public T Get<T>(SettingKey<T> key)
    {
        string? raw;
        lock (_gate) if (!_data.TryGetValue(key.Name, out raw)) return key.Default;
        var inv = CultureInfo.InvariantCulture;
        if (typeof(T) == typeof(string)) return (T)(object)raw;
        if (typeof(T) == typeof(bool) && bool.TryParse(raw, out bool b)) return Unsafe.As<bool, T>(ref b);
        if (typeof(T) == typeof(int) && int.TryParse(raw, NumberStyles.Integer, inv, out int i)) return Unsafe.As<int, T>(ref i);
        if (typeof(T) == typeof(long) && long.TryParse(raw, NumberStyles.Integer, inv, out long l)) return Unsafe.As<long, T>(ref l);
        if (typeof(T) == typeof(float) && float.TryParse(raw, NumberStyles.Float, inv, out float f)) return Unsafe.As<float, T>(ref f);
        if (typeof(T) == typeof(double) && double.TryParse(raw, NumberStyles.Float, inv, out double d)) return Unsafe.As<double, T>(ref d);
        return key.Default;
    }

    public void Set<T>(SettingKey<T> key, T value)
    {
        if (value is null) return;
        var inv = CultureInfo.InvariantCulture;
        string? text =
            typeof(T) == typeof(string) ? (string)(object)value
            : typeof(T) == typeof(bool) ? (Unsafe.As<T, bool>(ref value) ? "true" : "false")
            : typeof(T) == typeof(int) ? Unsafe.As<T, int>(ref value).ToString(inv)
            : typeof(T) == typeof(long) ? Unsafe.As<T, long>(ref value).ToString(inv)
            : typeof(T) == typeof(float) ? Unsafe.As<T, float>(ref value).ToString("R", inv)
            : typeof(T) == typeof(double) ? Unsafe.As<T, double>(ref value).ToString("R", inv)
            : null;
        if (text is null) return;
        lock (_gate)
        {
            if (_data.TryGetValue(key.Name, out string? old) && old == text) return;
            _data[key.Name] = text;
            try
            {
                string? dir = Path.GetDirectoryName(_path);
                if (dir is not null) Directory.CreateDirectory(dir);
                string tmp = _path + ".tmp";
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    fs.Write(JsonSerializer.SerializeToUtf8Bytes(_data, LocalStoreJson.Default.DictionaryStringString));
                    fs.Flush(flushToDisk: true);
                }
                File.Move(tmp, _path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
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
