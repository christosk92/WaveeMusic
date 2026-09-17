// ── Platform/Modules.Host.cs ───────────────────────────────────────────────────────────────────────────────────────
// the Sdk host, module items → tables, module playables → Playback
//
// Role: SHELL
// Owner: T
// Wave: 6
// Budget: 2500 lines
// Spec: ch 09 §9.3, docs/guide/playback-modules.md, gap register G-021 · G-110 · G-128 · G-148 · G-212
//
// A playback module is an out-of-process exe speaking JSON-RPC over stdio (`Wavee.Sdk`, unchanged). This file is the
// app side of that wire, ported from 0.2.9's `Backend/Modules/**`:
//
//   discovery      ModuleCatalog     the two roots, the manifest gate with its reasons, one winner per id
//   routing        ModuleRouter      the urlPattern prefilter in front of `playback/match`
//   the process    ModuleProcess     Stopped → Starting → Ready → (idle) → Stopped; Crashed/Faulted with backoff
//   the facade     ModuleHost        match · resolve (cached, deduped) · page (cached, deduped) · warm · host services
//   the bytes      ModuleByteStream  `stream/open|read|close` binary frames; LiveHttpStream (ICY, reconnecting)
//   the codec      AacAudioDecoder   ADTS → MF's AAC MFT, primed past the implicit-SBR stream change (G-128)
//
// …and the 0.3 composition that replaces 0.2.9's provider registry, because 0.3 has none: a module playable is a
// TRACK ROW (`RowFor`, an `Authority.Full` staged commit), its audio is `Playback.Audio.ModuleOpen`, its video is a
// tier in front of `Playback.VideoResolver` (`VideoSource.Clear`, G-148), a row the reducer met with no identity is a
// `FetchProvider` answer, and a pasted link is `Shell.MatchLink`. Under `--fake` the one module is an in-process demo
// (`DemoModule`) driven through the SAME wire over in-memory pipes, so the whole host path runs offline.
//
// Threads: module traffic runs on the pool (the JSON-RPC pumps); every table write hops through `Playback.ToUi` (C1).
// The resolve/page caches are concurrent dictionaries; nothing here holds a table span across a post.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using FluentGpu.Media;
using FluentGpu.Media.Windows;
using Wavee.Sdk;
using Wavee.Sdk.Protocol;

namespace Wavee;

public static partial class Modules
{
    // ══ 0. COMPOSITION ══════════════════════════════════════════════════════════════════════════════════════════════

    static ModuleHost? s_host;

    /// <summary>The app's module host, or null before <see cref="Boot"/> (and in a unit test that attached none).</summary>
    public static ModuleHost? Current => s_host;

    /// <summary>Discover the modules and compose the host. PRE-LOGIN by design: pasting a YouTube link needs no session.
    /// Nothing is launched until a match, resolve or page needs a module. Under <c>--fake</c> the catalogue is the one
    /// in-process demo module (ch 31 row 33) — the disk modules would reach the network.</summary>
    public static void Boot()
    {
        if (s_host is not null) return;
        ModuleCatalog catalog;
        ModuleSpawn? spawn = null;
        string? dataRoot = null;
        if (Platform.Args.Fake)
        {
            catalog = ModuleCatalog.From([DemoModule.Installed]);
            spawn = DemoModule.SpawnAsync;
        }
        else
        {
            catalog = ModuleCatalog.Discover(ModuleCatalog.DefaultBundledRoot, Path.Combine(Platform.LocalFolder, "modules"));
            dataRoot = Path.Combine(Platform.LocalFolder, "modules-data");
            try { ChildProcessChannel.Job ??= global::FluentGpu.WindowsApi.Shell.ChildProcessJob.CreateKillOnClose(); }
            catch (Exception ex) { Log.Warn("module", "job object unavailable; modules will not die with a killed app", ex); }
        }

        var host = new ModuleHost(catalog, CurrentPrefs, new ModuleHostServices(), spawn, Platform.Version.SemVer,
            Platform.Locale.UiCulture, startIdleTimer: true, dataRoot: dataRoot);
        Attach(host);
        host.MetadataChanged += OnModuleMetadata;
        Fetch.Register(new ModuleFetchProvider());
        Playback.Audio.ModuleOpen = OpenAudio;             // G-110 / G-021: the module arm of the audio routing table
        InstallVideoTier();                                // G-148: a module `form: video` answer → `VideoSource.Clear`
        for (int i = 0; i < catalog.Rejections.Count; i++)
            Log.Info("module", "rejected " + catalog.Rejections[i].Dir + ": " + catalog.Rejections[i].Reason);
        Log.Info("module", "boot installed=" + catalog.Modules.Count.ToString(CultureInfo.InvariantCulture)
                           + " rejected=" + catalog.Rejections.Count.ToString(CultureInfo.InvariantCulture)
                           + (Platform.Args.Fake ? " demo=1" : ""));
    }

    /// <summary>Stop every module process (the exit tail). Idempotent.</summary>
    public static void Shutdown()
    {
        if (s_host is not { } host) return;
        Attach(null);
        host.MetadataChanged -= OnModuleMetadata;
        host.Dispose();
    }

    /// <summary>Publish <paramref name="host"/> as <see cref="Current"/> and attach its two caches to the process-wide
    /// answers. Null detaches all three — the honest answer for a build with no host.</summary>
    public static void Attach(ModuleHost? host)
    {
        s_host = host;
        Playables.Attach(host?.Playables);
        Pages.Attach(host?.Pages);
    }

    /// <summary>The app's source-neutral playback preferences, read at every resolve.</summary>
    static ResolvePreferences CurrentPrefs()
        => PrefsFrom(Platform.Network.EffectiveQuality(), Platform.Network.IsMetered,
            Platform.Settings.Get(Platform.Keys.CrossfadeEnabled), Platform.Settings.Get(Platform.Keys.CrossfadeMs));

    /// <summary>PURE: the source-neutral preferences a module sees — the effective rung (the user's, capped on a metered
    /// link), whether the link IS metered, and the crossfade (0 when off, clamped to 12 s). A module never learns which
    /// Spotify rung the ladder picked (0.2.9 <c>Services.ResolvePrefs</c>).</summary>
    public static ResolvePreferences PrefsFrom(int effectiveQuality, bool metered, bool crossfadeEnabled, int crossfadeMs)
    {
        string quality = effectiveQuality switch
        {
            (int)Spotify.Audio.Quality.Normal96 => "normal",
            (int)Spotify.Audio.Quality.High160 => "high",
            (int)Spotify.Audio.Quality.Lossless => "lossless",
            _ => "veryHigh",
        };
        return new ResolvePreferences(quality, metered, crossfadeEnabled ? Math.Clamp(crossfadeMs, 0, 12_000) : 0);
    }

    // ══ 1. DISCOVERY (0.2.9 ModuleCatalog) ══════════════════════════════════════════════════════════════════════════

    /// <summary>One module the host will run: a validated manifest plus its directory.</summary>
    public sealed record InstalledModule(string Id, string Version, string Dir, ModuleManifest Manifest, bool Bundled);

    /// <summary>A directory that looked like a module and was refused — a diagnostics row, never a silent skip.</summary>
    public sealed record ModuleRejection(string Dir, string Reason);

    /// <summary>The injectable file-system seam: discovery is pure over these four probes.</summary>
    public sealed record ModuleFileSystem(
        Func<string, bool> DirectoryExists, Func<string, string[]> EnumerateDirectories,
        Func<string, bool> FileExists, Func<string, string> ReadAllText)
    {
        public static ModuleFileSystem Real { get; } = new(
            Directory.Exists,
            static dir => { try { return Directory.GetDirectories(dir); } catch { return []; } },
            File.Exists,
            File.ReadAllText);
    }

    /// <summary>Discovers, validates and ranks the installed modules across the bundled root and the user store.</summary>
    public sealed class ModuleCatalog
    {
        public const string ManifestFileName = "wavee-module.json";
        public const int MinProtocol = ModuleProtocol.MinSupported;
        public const int MaxProtocol = ModuleProtocol.Version;

        ModuleCatalog(IReadOnlyList<InstalledModule> modules, IReadOnlyList<ModuleRejection> rejections)
        {
            Modules = modules;
            Rejections = rejections;
        }

        public IReadOnlyList<InstalledModule> Modules { get; }
        public IReadOnlyList<ModuleRejection> Rejections { get; }

        /// <summary><c>&lt;app dir&gt;\modules</c>.</summary>
        public static string DefaultBundledRoot => Path.Combine(AppContext.BaseDirectory, "modules");

        public static ModuleCatalog Empty { get; } = new([], []);

        /// <summary>A catalogue of already-validated modules (the `--fake` demo, a test).</summary>
        public static ModuleCatalog From(IReadOnlyList<InstalledModule> modules) => new(modules, []);

        public static ModuleCatalog Discover(string bundledRoot, string userRoot, ModuleFileSystem? fs = null)
        {
            fs ??= ModuleFileSystem.Real;
            var candidates = new List<InstalledModule>();
            var rejections = new List<ModuleRejection>();
            if (fs.DirectoryExists(bundledRoot))
                foreach (string dir in fs.EnumerateDirectories(bundledRoot))
                    Probe(fs, dir, bundled: true, candidates, rejections);
            if (fs.DirectoryExists(userRoot))
                foreach (string idDir in fs.EnumerateDirectories(userRoot))
                    foreach (string versionDir in fs.EnumerateDirectories(idDir))
                        Probe(fs, versionDir, bundled: false, candidates, rejections);
            return new ModuleCatalog(Rank(candidates), rejections);
        }

        static void Probe(ModuleFileSystem fs, string dir, bool bundled, List<InstalledModule> candidates, List<ModuleRejection> rejections)
        {
            string manifestPath = Path.Combine(dir, ManifestFileName);
            if (!fs.FileExists(manifestPath)) { rejections.Add(new ModuleRejection(dir, "no " + ManifestFileName)); return; }
            ModuleManifest? manifest;
            try { manifest = JsonSerializer.Deserialize(fs.ReadAllText(manifestPath), SdkJsonContext.Default.ModuleManifest); }
            catch (Exception ex)
            {
                rejections.Add(new ModuleRejection(dir, "unreadable manifest: " + ex.GetType().Name + ": " + ex.Message));
                return;
            }
            if (manifest is null) { rejections.Add(new ModuleRejection(dir, "empty manifest")); return; }
            if (Validate(manifest, dir, bundled) is { } reason) { rejections.Add(new ModuleRejection(dir, reason)); return; }
            candidates.Add(new InstalledModule(manifest.Id, manifest.Version, dir, manifest, bundled));
        }

        /// <summary>The whole manifest gate as one pure function: null = accepted, otherwise the reason.</summary>
        public static string? Validate(ModuleManifest m, string dir, bool bundled)
        {
            var inv = CultureInfo.InvariantCulture;
            if (m.SchemaVersion < 1) return "unsupported schemaVersion " + m.SchemaVersion.ToString(inv);
            if (!Actions.Key.IsValid(m.Id)) return "invalid module id '" + (m.Id ?? "") + "' (publisher.name, ASCII, <= 128 chars)";
            if (string.IsNullOrWhiteSpace(m.Version)) return "missing version";
            if (string.IsNullOrWhiteSpace(m.Entry)) return "missing entry";
            if (m.ProtocolVersion < MinProtocol || m.ProtocolVersion > MaxProtocol)
                return "protocolVersion " + m.ProtocolVersion.ToString(inv) + " outside the host range "
                       + MinProtocol.ToString(inv) + ".." + MaxProtocol.ToString(inv);
            // The entry must resolve INSIDE the module directory — a manifest is untrusted input.
            if (!ResolvesInside(dir, m.Entry)) return "entry '" + m.Entry + "' escapes the module directory";
            string expected = bundled ? LeafName(dir) : LeafName(ParentName(dir));
            if (expected.Length > 0 && !string.Equals(expected, m.Id, StringComparison.OrdinalIgnoreCase))
                return "directory '" + expected + "' does not match manifest id '" + m.Id + "'";
            return null;
        }

        static bool ResolvesInside(string dir, string entry)
        {
            if (Path.IsPathRooted(entry) || entry.Contains('\0')) return false;
            try
            {
                string root = Path.GetFullPath(dir);
                string full = Path.GetFullPath(Path.Combine(root, entry));
                string prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
                return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        static string LeafName(string dir)
        {
            string t = dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            int i = t.LastIndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
            return i >= 0 ? t[(i + 1)..] : t;
        }

        static string ParentName(string dir)
        {
            string t = dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            int i = t.LastIndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
            return i > 0 ? t[..i] : "";
        }

        /// <summary>One winner per id: highest compatible protocol, then version, then the user store on a dead tie.</summary>
        static IReadOnlyList<InstalledModule> Rank(List<InstalledModule> candidates)
        {
            var best = new Dictionary<string, InstalledModule>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in candidates)
                if (!best.TryGetValue(c.Id, out var cur) || Beats(c, cur)) best[c.Id] = c;
            var list = new List<InstalledModule>(best.Values);
            list.Sort(static (a, b) => string.CompareOrdinal(a.Id, b.Id));
            return list;
        }

        static bool Beats(InstalledModule a, InstalledModule b)
        {
            if (a.Manifest.ProtocolVersion != b.Manifest.ProtocolVersion) return a.Manifest.ProtocolVersion > b.Manifest.ProtocolVersion;
            int v = CompareVersions(a.Version, b.Version);
            return v != 0 ? v > 0 : !a.Bundled && b.Bundled;
        }

        /// <summary>Dotted numeric version compare; a non-numeric tail compares ordinally.</summary>
        public static int CompareVersions(string? a, string? b)
        {
            string x = a ?? "", y = b ?? "";
            int i = 0, j = 0;
            while (true)
            {
                bool xNum = TryTakeNumber(x, ref i, out int xn);
                bool yNum = TryTakeNumber(y, ref j, out int yn);
                if (!xNum && !yNum) return string.CompareOrdinal(x[Math.Min(i, x.Length)..], y[Math.Min(j, y.Length)..]);
                if (!xNum) return -1;
                if (!yNum) return 1;
                if (xn != yn) return xn < yn ? -1 : 1;
            }
        }

        static bool TryTakeNumber(string s, ref int i, out int value)
        {
            value = 0;
            if (i >= s.Length) return false;
            int start = i;
            while (i < s.Length && s[i] is >= '0' and <= '9') { value = value > 100_000_000 ? value : value * 10 + (s[i] - '0'); i++; }
            if (i == start) return false;
            if (i < s.Length && s[i] == '.') i++;
            return true;
        }
    }

    // ══ 2. ROUTING (0.2.9 ModuleRouter + ModuleCapabilities) ═══════════════════════════════════════════════════════

    /// <summary>Capability tokens a manifest may declare, spelled once.</summary>
    public static class ModuleCapabilities
    {
        public const string Playback = "playback", Match = "match", Metadata = "metadata", Fallback = "fallback";
        /// <summary>Host-service permissions ride the capability list under this prefix (one list, one gate).</summary>
        public const string PermissionPrefix = "permission:";

        public static bool Declares(ModuleManifest manifest, string capability)
        {
            string[] caps = manifest.Capabilities ?? [];
            for (int i = 0; i < caps.Length; i++)
                if (string.Equals(caps[i], capability, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static bool HasPermission(ModuleManifest manifest, string permission) => Declares(manifest, PermissionPrefix + permission);
    }

    /// <summary>"Who can play this link?" — the cheap prefilter in front of the match RPC. Pure.</summary>
    public static class ModuleRouter
    {
        /// <summary>The ask order: a pinned module alone; else pattern hits in catalog order; else every match module,
        /// fallback modules last (a stable partition — catalog order inside each half is the routing table).</summary>
        public static IReadOnlyList<InstalledModule> Prefilter(IReadOnlyList<InstalledModule>? modules, string? input, string? pinnedModuleId)
        {
            if (modules is null || modules.Count == 0 || string.IsNullOrWhiteSpace(input)) return [];
            string text = input.Trim();
            if (pinnedModuleId is { Length: > 0 })
            {
                for (int i = 0; i < modules.Count; i++)
                    if (string.Equals(modules[i].Id, pinnedModuleId, StringComparison.OrdinalIgnoreCase)
                        && ModuleCapabilities.Declares(modules[i].Manifest, ModuleCapabilities.Match))
                        return [modules[i]];
                return [];
            }
            string host = HostOf(text);
            var hits = new List<InstalledModule>();
            for (int i = 0; i < modules.Count; i++)
                if (ModuleCapabilities.Declares(modules[i].Manifest, ModuleCapabilities.Match)
                    && MatchesPattern(modules[i].Manifest.UrlPatterns, host, text)) hits.Add(modules[i]);
            if (hits.Count == 0)
                for (int i = 0; i < modules.Count; i++)
                    if (ModuleCapabilities.Declares(modules[i].Manifest, ModuleCapabilities.Match)) hits.Add(modules[i]);
            var head = new List<InstalledModule>(hits.Count);
            var tail = new List<InstalledModule>(2);
            foreach (var m in hits) (ModuleCapabilities.Declares(m.Manifest, ModuleCapabilities.Fallback) ? tail : head).Add(m);
            head.AddRange(tail);
            return head;
        }

        static bool MatchesPattern(string[]? patterns, string host, string text)
        {
            if (patterns is null) return false;
            foreach (string p in patterns)
            {
                if (string.IsNullOrEmpty(p)) continue;
                if (host.Length > 0 ? host.Contains(p, StringComparison.OrdinalIgnoreCase) : text.Contains(p, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>The lower-cased host of an http(s) url; "" for anything else.</summary>
        public static string HostOf(string? text)
        {
            if (text is not { Length: > 0 } || !Uri.TryCreate(text, UriKind.Absolute, out Uri? uri)) return "";
            return uri.Scheme is "http" or "https" ? uri.Host.ToLowerInvariant() : "";
        }

        /// <summary>Is this a bare http(s) url?</summary>
        public static bool IsHttpUrl(string? text) => HostOf(text).Length > 0;
    }

    // ══ 3. THE PROCESS (0.2.9 ModuleProcess) ════════════════════════════════════════════════════════════════════════

    public enum ModuleProcessState { Stopped, Starting, Ready, Crashed, Faulted }

    /// <summary>The transport to one running module: a JSON-RPC peer plus the process behind it.</summary>
    public interface IModuleChannel : IAsyncDisposable
    {
        JsonRpcConnection Connection { get; }
        int ProcessId { get; }
        bool HasExited { get; }
        void Kill();
        Task<bool> WaitForExitAsync(TimeSpan grace);
    }

    /// <summary>How a channel is produced for one module. Injected so a test (and `--fake`) never spawns a process.</summary>
    public delegate Task<IModuleChannel> ModuleSpawn(InstalledModule module, Action<string> stderr, CancellationToken ct);

    /// <summary>Every host-side timeout, in one place.</summary>
    public static class ModuleTimeouts
    {
        public static readonly TimeSpan Initialize = TimeSpan.FromSeconds(5), Match = TimeSpan.FromSeconds(5),
            Resolve = TimeSpan.FromSeconds(20), StreamOpen = TimeSpan.FromSeconds(10), StreamRead = TimeSpan.FromSeconds(10),
            Diagnostics = TimeSpan.FromSeconds(10), Page = TimeSpan.FromSeconds(15), ShutdownGrace = TimeSpan.FromSeconds(2),
            CancelGrace = TimeSpan.FromSeconds(2), Idle = TimeSpan.FromMinutes(10), FaultCooldown = TimeSpan.FromMinutes(10);
        public static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(16)];
        public const int MaxConsecutiveFailedStarts = 3;
    }

    /// <summary>What a module's out-of-band traffic reaches. <see cref="ModuleHost"/> implements it.</summary>
    public interface IModuleHostSink
    {
        void OnMetadata(string moduleId, MetadataUpdate update);
        void OnExpired(string moduleId, string playableId);
        void OnStatus(string moduleId, ModuleStatus status);
        void OnProgress(string moduleId, ProgressNotification progress);
        void OnLog(string moduleId, LogNotification line);
        void RegisterHostServices(InstalledModule module, JsonRpcConnection connection);
    }

    /// <summary>One module's child process, its JSON-RPC peer and its lifecycle state machine.</summary>
    public sealed class ModuleProcess : IAsyncDisposable
    {
        readonly InstalledModule _module;
        readonly IModuleHostSink _sink;
        readonly ModuleSpawn _spawn;
        readonly string _hostVersion, _locale, _category;
        readonly string? _dataRoot;
        readonly Func<ResolvePreferences> _prefs;
        readonly Func<DateTimeOffset> _now;
        readonly SemaphoreSlim _startGate = new(1, 1);
        readonly Lock _stateGate = new();

        IModuleChannel? _channel;
        CancellationTokenSource? _pumpCts;
        ModuleProcessState _state = ModuleProcessState.Stopped;
        string[] _capabilities = [];
        int _negotiatedProtocol, _consecutiveFailedStarts, _openStreamLeases, _inFlight, _disposed;
        string? _lastError;
        DateTimeOffset _retryNotBefore = DateTimeOffset.MinValue, _lastUsedUtc;
        long _generation;

        /// <param name="dataRoot">The parent of every module's private data directory, or null for none (a test).</param>
        public ModuleProcess(InstalledModule module, IModuleHostSink sink, ModuleSpawn? spawn = null, string? hostVersion = null,
            string? locale = null, Func<ResolvePreferences>? prefs = null, Func<DateTimeOffset>? now = null, string? dataRoot = null)
        {
            ArgumentNullException.ThrowIfNull(module);
            ArgumentNullException.ThrowIfNull(sink);
            _module = module;
            _sink = sink;
            _spawn = spawn ?? ChildProcessChannel.SpawnAsync;
            _hostVersion = hostVersion ?? "0.0.0";
            _locale = locale ?? "en-US";
            _prefs = prefs ?? (static () => ResolvePreferences.Default);
            _now = now ?? (static () => DateTimeOffset.UtcNow);
            _dataRoot = dataRoot;
            _category = "module." + module.Id;
            _lastUsedUtc = _now();
        }

        public InstalledModule Module => _module;
        public ModuleStats Stats { get; } = new();
        public ModuleProcessState State { get { lock (_stateGate) return _state; } }
        public int? ProcessId { get { lock (_stateGate) return _channel is { HasExited: false } c ? c.ProcessId : null; } }
        public string? LastError { get { lock (_stateGate) return _lastError; } }
        public IReadOnlyList<string> Capabilities { get { lock (_stateGate) return _capabilities; } }
        public int NegotiatedProtocol { get { lock (_stateGate) return _negotiatedProtocol; } }

        /// <summary>The module's last <c>module/status</c> card, or null.</summary>
        public ModuleStatus? Status { get; internal set; }

        /// <summary>The private, writable directory handed to the module, or "" when none was configured.</summary>
        public string DataDir => _dataRoot is null ? "" : Path.Combine(_dataRoot, _module.Id);

        /// <summary>Clear the Faulted latch — the diagnostics page's Retry.</summary>
        public void Retry()
        {
            lock (_stateGate)
            {
                if (_state == ModuleProcessState.Faulted) _state = ModuleProcessState.Stopped;
                _consecutiveFailedStarts = 0;
                _retryNotBefore = DateTimeOffset.MinValue;
            }
        }

        /// <summary>Does the module declare this capability (the handshake's effective list, the manifest's before)?</summary>
        public bool Declares(string capability)
        {
            IReadOnlyList<string> effective = Capabilities;
            IReadOnlyList<string> source = effective.Count > 0 ? effective : _module.Manifest.Capabilities ?? [];
            for (int i = 0; i < source.Count; i++)
                if (string.Equals(source[i], capability, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public async Task<TResult> RequestAsync<TParams, TResult>(string method, TParams p, JsonTypeInfo<TParams> paramsInfo,
            JsonTypeInfo<TResult> resultInfo, TimeSpan timeout, CancellationToken ct)
        {
            IModuleChannel channel = await EnsureReadyAsync(ct).ConfigureAwait(false);
            long started = Environment.TickCount64;
            Interlocked.Increment(ref _inFlight);
            try
            {
                TResult r = await channel.Connection.RequestAsync(method, p, paramsInfo, resultInfo, timeout, ct).ConfigureAwait(false);
                Stats.NoteRequest(Environment.TickCount64 - started);
                Touch();
                return r;
            }
            catch (Exception ex) { throw Fail(channel, method, ex); }
            finally { Interlocked.Decrement(ref _inFlight); }
        }

        public async Task<BinaryPayload> RequestBinaryAsync<TParams>(string method, TParams p, JsonTypeInfo<TParams> paramsInfo,
            TimeSpan timeout, CancellationToken ct)
        {
            IModuleChannel channel = await EnsureReadyAsync(ct).ConfigureAwait(false);
            long started = Environment.TickCount64;
            Interlocked.Increment(ref _inFlight);
            try
            {
                BinaryPayload r = await channel.Connection.RequestBinaryAsync(method, p, paramsInfo, timeout, ct).ConfigureAwait(false);
                Stats.NoteRequest(Environment.TickCount64 - started);
                Touch();
                return r;
            }
            catch (Exception ex) { throw Fail(channel, method, ex); }
            finally { Interlocked.Decrement(ref _inFlight); }
        }

        /// <summary>Re-install the host services on a live connection (a go-live install reaching a module already up).</summary>
        public void NotifyServicesChanged(ModuleHostServices services)
        {
            IModuleChannel? c;
            lock (_stateGate) c = _state == ModuleProcessState.Ready ? _channel : null;
            if (c is not null) services.Install(_module, c.Connection);
        }

        /// <summary>A lease that keeps this process out of the idle stop while a byte stream is open.</summary>
        public IDisposable AcquireStreamLease()
        {
            Interlocked.Increment(ref _openStreamLeases);
            Touch();
            return new StreamLease(this);
        }

        public bool HasOpenStreams => Volatile.Read(ref _openStreamLeases) > 0;

        async Task<IModuleChannel> EnsureReadyAsync(CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            lock (_stateGate)
                if (_state == ModuleProcessState.Ready && _channel is { HasExited: false } live) { _lastUsedUtc = _now(); return live; }
            await _startGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                lock (_stateGate)
                    if (_state == ModuleProcessState.Ready && _channel is { HasExited: false } live) { _lastUsedUtc = _now(); return live; }
                await WaitOutBackoffAsync(ct).ConfigureAwait(false);
                return await StartAsync(ct).ConfigureAwait(false);
            }
            finally { _startGate.Release(); }
        }

        async Task WaitOutBackoffAsync(CancellationToken ct)
        {
            DateTimeOffset notBefore;
            lock (_stateGate)
            {
                if (_state == ModuleProcessState.Faulted)
                {
                    if (_now() < _retryNotBefore)
                        throw new ModuleException(ModuleErrorCode.Transient, _lastError ?? (_module.Id + " failed to start three times in a row"));
                    _state = ModuleProcessState.Stopped;
                    _consecutiveFailedStarts = 0;
                    _retryNotBefore = DateTimeOffset.MinValue;
                }
                notBefore = _retryNotBefore;
            }
            TimeSpan wait = notBefore - _now();
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
        }

        async Task<IModuleChannel> StartAsync(CancellationToken ct)
        {
            await TearDownAsync(kill: true).ConfigureAwait(false);
            long generation;
            lock (_stateGate)
            {
                _state = ModuleProcessState.Starting;
                generation = ++_generation;
                if (generation > 1) Stats.NoteRestart();
            }

            IModuleChannel? channel = null;
            CancellationTokenSource? cts = null;
            try
            {
                channel = await _spawn(_module, OnStderrLine, ct).ConfigureAwait(false);
                JsonRpcConnection conn = channel.Connection;
                string id = _module.Id;
                conn.OnNotification(ModuleMethods.Metadata, SdkJsonContext.Default.MetadataUpdate, u => _sink.OnMetadata(id, u));
                conn.OnNotification(ModuleMethods.Expired, SdkJsonContext.Default.ExpiredNotification, e => _sink.OnExpired(id, e.PlayableId));
                conn.OnNotification(ModuleMethods.Status, SdkJsonContext.Default.ModuleStatus, s => { Status = s; _sink.OnStatus(id, s); });
                conn.OnNotification(ModuleMethods.Progress, SdkJsonContext.Default.ProgressNotification, pr => _sink.OnProgress(id, pr));
                conn.OnNotification(ModuleMethods.Log, SdkJsonContext.Default.LogNotification, l => _sink.OnLog(id, l));
                _sink.RegisterHostServices(_module, conn);

                cts = new CancellationTokenSource();
                IModuleChannel started = channel;
                CancellationToken pumpToken = cts.Token;
                Task pump = Task.Run(() => conn.RunAsync(pumpToken), CancellationToken.None);
                _ = pump.ContinueWith(_ => OnChannelDown(generation), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

                var init = new InitializeParams(_hostVersion, ModuleCatalog.MinProtocol, ModuleCatalog.MaxProtocol,
                    EnsureDataDir(), _locale, 0, _prefs());
                InitializeResult result = await conn.RequestAsync(ModuleMethods.Initialize, init, SdkJsonContext.Default.InitializeParams,
                    SdkJsonContext.Default.InitializeResult, ModuleTimeouts.Initialize, ct).ConfigureAwait(false);
                if (result.ProtocolVersion < ModuleCatalog.MinProtocol || result.ProtocolVersion > ModuleCatalog.MaxProtocol)
                    throw new ModuleException(ModuleErrorCode.Unsupported, _module.Id + " answered protocol "
                        + result.ProtocolVersion.ToString(CultureInfo.InvariantCulture) + ", outside the host range");

                lock (_stateGate)
                {
                    _channel = started;
                    _pumpCts = cts;
                    _state = ModuleProcessState.Ready;
                    _capabilities = result.Capabilities ?? [];
                    _negotiatedProtocol = result.ProtocolVersion;
                    _consecutiveFailedStarts = 0;
                    _retryNotBefore = DateTimeOffset.MinValue;
                    _lastError = null;
                    _lastUsedUtc = _now();
                }
                Log.Info(_category, "ready pid=" + started.ProcessId.ToString(CultureInfo.InvariantCulture)
                                    + " protocol=" + result.ProtocolVersion.ToString(CultureInfo.InvariantCulture));
                return started;
            }
            catch (Exception ex)
            {
                if (cts is not null) { try { await cts.CancelAsync().ConfigureAwait(false); } catch { } }
                if (channel is not null)
                {
                    try { channel.Kill(); } catch { }
                    try { await channel.DisposeAsync().ConfigureAwait(false); } catch { }
                }
                string message = Describe(ex);
                lock (_stateGate)
                {
                    _channel = null;
                    _pumpCts = null;
                    _lastError = message;
                    _consecutiveFailedStarts++;
                    if (_consecutiveFailedStarts >= ModuleTimeouts.MaxConsecutiveFailedStarts)
                    {
                        _state = ModuleProcessState.Faulted;
                        _retryNotBefore = _now() + ModuleTimeouts.FaultCooldown;
                    }
                    else
                    {
                        _state = ModuleProcessState.Crashed;
                        _retryNotBefore = _now() + ModuleTimeouts.Backoff[Math.Min(_consecutiveFailedStarts - 1, ModuleTimeouts.Backoff.Length - 1)];
                    }
                }
                Stats.NoteFailure(ModuleMethods.Initialize, message);
                Log.Info(_category, "failed to start: " + message);
                if (ex is ModuleException or OperationCanceledException) throw;
                throw new ModuleException(ModuleErrorCode.Transient, message);
            }
        }

        void OnChannelDown(long generation)
        {
            bool wasReady;
            lock (_stateGate)
            {
                if (_generation != generation) return;
                wasReady = _state is ModuleProcessState.Ready;
                if (_state is ModuleProcessState.Ready or ModuleProcessState.Starting)
                {
                    _state = ModuleProcessState.Crashed;
                    _retryNotBefore = _now() + ModuleTimeouts.Backoff[0];
                }
            }
            if (wasReady) Log.Info(_category, "exited or its pipe broke");
        }

        /// <summary>Shut a module down after 10 minutes of silence; never one with an open stream or a request in flight.</summary>
        public async Task IdleSweepAsync(CancellationToken ct = default)
        {
            bool idle;
            lock (_stateGate)
                idle = _state == ModuleProcessState.Ready && Volatile.Read(ref _openStreamLeases) == 0
                       && Volatile.Read(ref _inFlight) == 0 && _now() - _lastUsedUtc >= ModuleTimeouts.Idle;
            if (idle) await StopAsync("idle", ct).ConfigureAwait(false);
        }

        /// <summary>Ask the module to wind down (2 s grace), then kill it.</summary>
        public async Task StopAsync(string reason, CancellationToken ct = default)
        {
            IModuleChannel? channel;
            lock (_stateGate)
            {
                channel = _channel;
                _state = ModuleProcessState.Stopped;
                if (channel is null) return;
                _generation++;
            }
            Log.Info(_category, "stopping (" + reason + ")");
            try
            {
                await channel.Connection.RequestAsync(ModuleMethods.Shutdown, RpcUnit.Value, SdkJsonContext.Default.RpcUnit,
                    SdkJsonContext.Default.RpcUnit, ModuleTimeouts.ShutdownGrace, ct).ConfigureAwait(false);
            }
            catch { /* a module that will not answer its own shutdown is killed below */ }
            await TearDownAsync(kill: true).ConfigureAwait(false);
        }

        async Task TearDownAsync(bool kill)
        {
            IModuleChannel? channel;
            CancellationTokenSource? cts;
            lock (_stateGate)
            {
                channel = _channel;
                cts = _pumpCts;
                _channel = null;
                _pumpCts = null;
                _capabilities = [];
            }
            if (cts is not null)
            {
                try { await cts.CancelAsync().ConfigureAwait(false); } catch { }
                cts.Dispose();
            }
            if (channel is null) return;
            if (kill && !channel.HasExited && !await channel.WaitForExitAsync(ModuleTimeouts.ShutdownGrace).ConfigureAwait(false))
            {
                try { channel.Kill(); } catch { }
            }
            try { await channel.DisposeAsync().ConfigureAwait(false); } catch { }
        }

        void Touch() { lock (_stateGate) _lastUsedUtc = _now(); }

        Exception Fail(IModuleChannel channel, string method, Exception ex)
        {
            string message = Describe(ex);
            Stats.NoteFailure(method, message);
            lock (_stateGate) _lastError = message;
            if (ex is TimeoutException) _ = KillAfterCancelGraceAsync(channel);
            if (ex is IOException or ObjectDisposedException) OnChannelDown(Interlocked.Read(ref _generation));
            if (ex is ModuleException or OperationCanceledException) return ex;
            if (ex is JsonRpcException rpc)
            {
                if (rpc.Code == JsonRpcErrorCodes.MethodNotFound)
                    return new ModuleException(ModuleErrorCode.Unsupported, method + " is not implemented by " + _module.Id);
                return new ModuleException(rpc.ErrorData?.Kind ?? ModuleErrorCode.Transient, rpc.Message)
                {
                    RetryAfterMs = rpc.ErrorData?.RetryAfterMs,
                    Detail = rpc.ErrorData?.Detail,
                };
            }
            return new ModuleException(ModuleErrorCode.Transient, message);
        }

        async Task KillAfterCancelGraceAsync(IModuleChannel channel)
        {
            await Task.Delay(ModuleTimeouts.CancelGrace).ConfigureAwait(false);
            bool stillCurrent;
            lock (_stateGate) stillCurrent = ReferenceEquals(_channel, channel);
            if (!stillCurrent || channel.HasExited) return;
            Log.Info(_category, "did not answer $/cancelRequest — killing it");
            try { channel.Kill(); } catch { }
        }

        void OnStderrLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            if (line.StartsWith("ERROR ", StringComparison.Ordinal)) Log.Error(_category, line[6..]);
            else if (line.StartsWith("WARN ", StringComparison.Ordinal)) Log.Warn(_category, line[5..]);
            else Log.Info(_category, line);
        }

        string EnsureDataDir()
        {
            string dir = DataDir;
            if (dir.Length > 0) { try { Directory.CreateDirectory(dir); } catch { } }
            return dir;
        }

        static string Describe(Exception ex) => ex switch
        {
            TimeoutException => "the module did not answer in time",
            ModuleException m => m.Message,
            JsonRpcException r => r.Message,
            _ => ex.GetType().Name + ": " + ex.Message,
        };

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { await StopAsync("host shutdown").ConfigureAwait(false); } catch { }
            _startGate.Dispose();
        }

        sealed class StreamLease(ModuleProcess owner) : IDisposable
        {
            int _released;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _released, 1) != 0) return;
                Interlocked.Decrement(ref owner._openStreamLeases);
                owner.Touch();
            }
        }
    }

    /// <summary>The production channel: a redirected child process whose stdio carries the protocol and whose stderr
    /// streams into the log. Every process joins the job object, so a module dies with the app.</summary>
    public sealed class ChildProcessChannel : IModuleChannel
    {
        readonly System.Diagnostics.Process _process;

        ChildProcessChannel(System.Diagnostics.Process process, JsonRpcConnection connection)
        {
            _process = process;
            Connection = connection;
        }

        public JsonRpcConnection Connection { get; }
        public int ProcessId { get { try { return _process.Id; } catch { return 0; } } }
        public bool HasExited { get { try { return _process.HasExited; } catch { return true; } } }

        /// <summary>The job every module process is assigned to; null when the OS refused one.</summary>
        public static global::FluentGpu.WindowsApi.Shell.ChildProcessJob? Job { get; set; }

        /// <summary><c>dotnet &lt;entry&gt;</c> for a <c>.dll</c> entry (dev), the executable itself otherwise (publish).</summary>
        public static Task<IModuleChannel> SpawnAsync(InstalledModule module, Action<string> stderr, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(module);
            ct.ThrowIfCancellationRequested();
            string entry = Path.Combine(module.Dir, module.Manifest.Entry);
            bool managed = module.Manifest.Entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
            var utf8 = new UTF8Encoding(false);
            var psi = new ProcessStartInfo
            {
                FileName = managed ? "dotnet" : entry,
                WorkingDirectory = module.Dir,
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = utf8, StandardErrorEncoding = utf8, StandardInputEncoding = utf8,
            };
            if (managed) psi.ArgumentList.Add(entry);
            psi.ArgumentList.Add(ModuleRunner.ModuleSwitch);
            psi.ArgumentList.Add("--protocol");
            psi.ArgumentList.Add(ModuleCatalog.MaxProtocol.ToString(CultureInfo.InvariantCulture));
            // The module protocol's own environment (docs/guide/playback-modules.md §2) — not an app switch.
            psi.Environment["WAVEE_MODULE_ID"] = module.Id;
            psi.Environment["WAVEE_HOST_PID"] = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
            psi.Environment["WAVEE_HOST_VERSION"] = Platform.Version.SemVer;

            System.Diagnostics.Process process = System.Diagnostics.Process.Start(psi)
                ?? throw new ModuleException(ModuleErrorCode.Transient, "could not start " + module.Id);
            try { Job?.Assign(process.Handle); } catch { }
            process.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 } line) stderr(line); };
            process.BeginErrorReadLine();
            var connection = new JsonRpcConnection(process.StandardOutput.BaseStream, process.StandardInput.BaseStream);
            return Task.FromResult<IModuleChannel>(new ChildProcessChannel(process, connection));
        }

        public void Kill()
        {
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
        }

        public async Task<bool> WaitForExitAsync(TimeSpan grace)
        {
            try
            {
                using var cts = new CancellationTokenSource(grace);
                await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                return true;
            }
            catch { return _process.HasExited; }
        }

        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync().ConfigureAwait(false);
            try { _process.Dispose(); } catch { }
        }
    }

    /// <summary>A read of a module's counters — what the diagnostics page renders.</summary>
    public readonly record struct ModuleStatsSnapshot(long Requests, long Failures, long Restarts, int P50Ms, int P95Ms,
                                                      string? LastError, string? LastErrorMethod);

    /// <summary>Per-module counters: interlocked totals plus a fixed 128-sample latency ring.</summary>
    public sealed class ModuleStats
    {
        const int SampleCapacity = 128;
        readonly int[] _samples = new int[SampleCapacity];
        readonly Lock _gate = new();
        int _count, _next;
        long _requests, _failures, _restarts;
        string? _lastError, _lastErrorMethod;

        public void NoteRequest(long elapsedMs)
        {
            Interlocked.Increment(ref _requests);
            lock (_gate)
            {
                _samples[_next] = (int)Math.Clamp(elapsedMs, 0, int.MaxValue);
                _next = (_next + 1) % SampleCapacity;
                if (_count < SampleCapacity) _count++;
            }
        }

        public void NoteFailure(string method, string message)
        {
            Interlocked.Increment(ref _failures);
            Volatile.Write(ref _lastError, message);
            Volatile.Write(ref _lastErrorMethod, method);
        }

        public void NoteRestart() => Interlocked.Increment(ref _restarts);

        public ModuleStatsSnapshot Snapshot()
        {
            int p50 = 0, p95 = 0;
            lock (_gate)
            {
                if (_count > 0)
                {
                    var copy = new int[_count];
                    Array.Copy(_samples, copy, _count);
                    Array.Sort(copy);
                    p50 = copy[(int)((copy.Length - 1) * 0.50)];
                    p95 = copy[(int)((copy.Length - 1) * 0.95)];
                }
            }
            return new ModuleStatsSnapshot(Interlocked.Read(ref _requests), Interlocked.Read(ref _failures),
                Interlocked.Read(ref _restarts), p50, p95, Volatile.Read(ref _lastError), Volatile.Read(ref _lastErrorMethod));
        }
    }

    // ══ 4. HOST SERVICES (0.2.9 ModuleHostServices) ═════════════════════════════════════════════════════════════════

    /// <summary>Per-module secret storage, namespaced so one module never reads another's.</summary>
    public interface IModuleSecretStore
    {
        byte[]? Get(string moduleId, string key);
        void Set(string moduleId, string key, byte[] value);
    }

    /// <summary>The registry of module→host services and the permission gate in front of them: a module that did not
    /// declare a permission gets a typed refusal, never the data.</summary>
    public sealed class ModuleHostServices
    {
        public const string StoragePrivatePermission = "storage.private";
        readonly Lock _gate = new();
        readonly Dictionary<string, Registration> _services = new(StringComparer.Ordinal);

        /// <param name="secrets">Backs <c>host/secrets/*</c>; null leaves them unregistered (a module sees -32601).</param>
        public ModuleHostServices(IModuleSecretStore? secrets = null)
        {
            if (secrets is null) return;
            Register(ModuleMethods.SecretsGet, StoragePrivatePermission, SdkJsonContext.Default.SecretGetParams,
                SdkJsonContext.Default.SecretGetResult, (moduleId, p, _) => ValueTask.FromResult(new SecretGetResult(secrets.Get(moduleId, p.Key))));
            Register(ModuleMethods.SecretsSet, StoragePrivatePermission, SdkJsonContext.Default.SecretSetParams,
                SdkJsonContext.Default.RpcUnit, (moduleId, p, _) => { secrets.Set(moduleId, p.Key, p.Value); return ValueTask.FromResult(RpcUnit.Value); });
        }

        public event Action? Changed;

        public void Register<TParams, TResult>(string method, string permission, JsonTypeInfo<TParams> paramsInfo,
            JsonTypeInfo<TResult> resultInfo, Func<string, TParams, CancellationToken, ValueTask<TResult>> handler)
        {
            ArgumentException.ThrowIfNullOrEmpty(method);
            ArgumentNullException.ThrowIfNull(handler);
            lock (_gate)
            {
                _services[method] = new Registration(permission ?? "", (module, conn, perm) =>
                    conn.OnRequest(method, paramsInfo, resultInfo, (p, ct) =>
                    {
                        if (perm.Length > 0 && !ModuleCapabilities.HasPermission(module.Manifest, perm))
                            throw new ModuleException(ModuleErrorCode.Unsupported, module.Id + " did not declare the '" + perm + "' permission");
                        return handler(module.Id, p, ct);
                    }));
            }
            Changed?.Invoke();
        }

        public void Unregister(string method)
        {
            bool removed;
            lock (_gate) removed = _services.Remove(method);
            if (removed) Changed?.Invoke();
        }

        public void Install(InstalledModule module, JsonRpcConnection connection)
        {
            Registration[] snapshot;
            lock (_gate) { snapshot = new Registration[_services.Count]; _services.Values.CopyTo(snapshot, 0); }
            for (int i = 0; i < snapshot.Length; i++) snapshot[i].Install(module, connection, snapshot[i].Permission);
        }

        public IReadOnlyList<string> Methods
        {
            get
            {
                lock (_gate)
                {
                    var names = new string[_services.Count];
                    _services.Keys.CopyTo(names, 0);
                    Array.Sort(names, StringComparer.Ordinal);
                    return names;
                }
            }
        }

        readonly record struct Registration(string Permission, Action<InstalledModule, JsonRpcConnection, string> Install);
    }

    // ══ 5. THE TWO SYNC CACHES (0.2.9 ModulePlayables + ModulePages) ════════════════════════════════════════════════

    /// <summary>The per-uri cache of resolve answers. An expired entry reads as ABSENT, never deleted eagerly.</summary>
    public sealed class ModulePlayableCache(Func<long>? nowUnixMs = null)
    {
        readonly ConcurrentDictionary<string, ResolvedPlayable> _entries = new(StringComparer.Ordinal);
        readonly Func<long> _now = nowUnixMs ?? (static () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        public void Put(string playableUri, ResolvedPlayable resolved)
        {
            if (string.IsNullOrEmpty(playableUri)) return;
            ArgumentNullException.ThrowIfNull(resolved);
            _entries[playableUri] = resolved;
        }

        public ResolvedPlayable? Get(string? playableUri)
            => playableUri is { Length: > 0 } && _entries.TryGetValue(playableUri, out var e) && !Expired(e) ? e : null;

        /// <summary>The answer EVEN IF expired — a signed url dying does not make a broadcast stop being one.</summary>
        public ResolvedPlayable? GetIncludingExpired(string? playableUri)
            => playableUri is { Length: > 0 } && _entries.TryGetValue(playableUri, out var e) ? e : null;

        public bool IsExpired(string? playableUri)
            => playableUri is { Length: > 0 } && _entries.TryGetValue(playableUri, out var e) && Expired(e);

        public void Invalidate(string? playableUri) { if (playableUri is { Length: > 0 }) _entries.TryRemove(playableUri, out _); }

        public void InvalidateModule(string moduleId)
        {
            string prefix = ModuleUri.Prefix(moduleId);
            foreach (var e in _entries) if (e.Key.StartsWith(prefix, StringComparison.Ordinal)) _entries.TryRemove(e.Key, out _);
        }

        public bool HasVideo(string? playableUri) => Get(playableUri) is { Form: MediaForm.Video };
        public bool IsLive(string? playableUri) => Get(playableUri) is { IsLive: true };
        public int Count => _entries.Count;

        bool Expired(ResolvedPlayable p) => p.ExpiresAtUnixMs is { } exp && exp > 0 && _now() >= exp;
    }

    /// <summary>The per-uri cache of page documents: the module's own expiry, else ten minutes.</summary>
    public sealed class ModulePageCache(Func<long>? nowUnixMs = null)
    {
        public const long DefaultTtlMs = 10 * 60 * 1000;
        readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
        readonly Func<long> _now = nowUnixMs ?? (static () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        readonly record struct Entry(ModulePageDoc Doc, long ExpiresAtUnixMs);

        public void Put(string pageUri, ModulePageDoc doc)
        {
            if (string.IsNullOrEmpty(pageUri)) return;
            ArgumentNullException.ThrowIfNull(doc);
            long expires = doc.ExpiresAtUnixMs is { } exp && exp > 0 ? exp : _now() + DefaultTtlMs;
            _entries[pageUri] = new Entry(doc, expires);
        }

        public ModulePageDoc? Get(string? pageUri)
            => pageUri is { Length: > 0 } && _entries.TryGetValue(pageUri, out Entry e) && _now() < e.ExpiresAtUnixMs ? e.Doc : null;

        public bool IsExpired(string? pageUri)
            => pageUri is { Length: > 0 } && _entries.TryGetValue(pageUri, out Entry e) && _now() >= e.ExpiresAtUnixMs;

        public void Invalidate(string? pageUri) { if (pageUri is { Length: > 0 }) _entries.TryRemove(pageUri, out _); }

        public void InvalidateModule(string moduleId)
        {
            string prefix = ModuleUri.Prefix(moduleId);
            foreach (var e in _entries) if (e.Key.StartsWith(prefix, StringComparison.Ordinal)) _entries.TryRemove(e.Key, out _);
        }

        public int Count => _entries.Count;
    }

    /// <summary>The process-wide resolve answers every sync surface reads (the stage, the links, the video tier).</summary>
    public static class Playables
    {
        static ModulePlayableCache? s_cache;

        public static void Attach(ModulePlayableCache? cache) => s_cache = cache;
        public static ModulePlayableCache? Cache => s_cache;
        public static ResolvedPlayable? Get(string? uri) => s_cache?.Get(uri);
        public static bool HasVideo(string? uri) => s_cache is { } c && c.HasVideo(uri);
        public static bool IsLive(string? uri) => s_cache is { } c && c.IsLive(uri);
    }

    public static partial class Pages
    {
        static ModulePageCache? s_cache;

        public static void Attach(ModulePageCache? cache) => s_cache = cache;
        public static ModulePageCache? Cache => s_cache;

        /// <summary>The cached document for one page uri — the SEED a revisit paints its first frame from.</summary>
        public static ModulePageDoc? Get(string? pageUri) => s_cache?.Get(pageUri);
    }

    // ══ 6. THE HOST (0.2.9 ModuleHost) ═════════════════════════════════════════════════════════════════════════════

    /// <summary>What a pasted link resolved to.</summary>
    public sealed record ModuleMatch(InstalledModule Module, MatchResult Match, ResolvedPlayable Resolved);

    /// <summary>The app-side host for every installed module. Reference-stable for the app lifetime.</summary>
    public sealed class ModuleHost : IDisposable, IModuleHostSink
    {
        readonly ModuleCatalog _catalog;
        readonly Dictionary<string, ModuleProcess> _processes = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, InstalledModule> _byId = new(StringComparer.OrdinalIgnoreCase);
        readonly ConcurrentDictionary<string, Task<ResolvedPlayable>> _inFlightResolves = new(StringComparer.Ordinal);
        readonly ConcurrentDictionary<string, Task<ModulePageDoc>> _inFlightPages = new(StringComparer.Ordinal);
        readonly ModuleSpawn? _spawn;
        readonly Func<ResolvePreferences> _prefs;
        readonly Timer? _idleTimer;
        readonly string _hostVersion, _locale;
        readonly string? _dataRoot;
        int _disposed;

        public ModuleHost(ModuleCatalog catalog, Func<ResolvePreferences>? prefs = null, ModuleHostServices? services = null,
            ModuleSpawn? spawn = null, string? hostVersion = null, string? locale = null, bool startIdleTimer = true,
            Func<long>? nowUnixMs = null, string? dataRoot = null)
        {
            ArgumentNullException.ThrowIfNull(catalog);
            _catalog = catalog;
            _spawn = spawn;
            _prefs = prefs ?? (static () => ResolvePreferences.Default);
            _hostVersion = hostVersion ?? "0.0.0";
            _locale = locale ?? "en-US";
            _dataRoot = dataRoot;
            Services = services ?? new ModuleHostServices();
            Playables = new ModulePlayableCache(nowUnixMs);
            Pages = new ModulePageCache(nowUnixMs);
            foreach (InstalledModule m in catalog.Modules) _byId[m.Id] = m;
            Services.Changed += ReinstallServices;
            // The ONE named timer (P10): the idle sweep, once a minute, only while any module is installed.
            if (startIdleTimer && catalog.Modules.Count > 0)
                _idleTimer = new Timer(static s => _ = ((ModuleHost)s!).SweepIdleAsync(), this, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        }

        public IReadOnlyList<InstalledModule> Installed => _catalog.Modules;
        public ModuleCatalog Catalog => _catalog;
        public ModulePlayableCache Playables { get; }
        public ModulePageCache Pages { get; }
        public ModuleHostServices Services { get; }

        /// <summary>A module pushed a live "now playing" correction (playable uri, update). Module thread.</summary>
        public event Action<string, MetadataUpdate>? MetadataChanged;
        /// <summary>A module's locator expired (playable uri). Module thread.</summary>
        public event Action<string>? PlayableExpired;
        /// <summary>A module's status card changed (module id, status). Module thread.</summary>
        public event Action<string, ModuleStatus>? StatusChanged;

        /// <summary>Trim, prefilter, ask each candidate's match in order, take the FIRST answer and resolve it, so the
        /// caller knows form, liveness and the real title before playing anything. A resolve failure THROWS with the
        /// module's own words.</summary>
        public async Task<ModuleMatch?> MatchAsync(string input, string? pinnedModuleId, CancellationToken ct)
        {
            string text = (input ?? "").Trim();
            if (text.Length == 0) return null;
            foreach (InstalledModule module in ModuleRouter.Prefilter(Installed, text, pinnedModuleId))
            {
                MatchResult? match;
                try
                {
                    match = await Process(module).RequestAsync(ModuleMethods.Match, new MatchParams(text), SdkJsonContext.Default.MatchParams,
                        SdkJsonContext.Default.MatchResult, ModuleTimeouts.Match, ct).ConfigureAwait(false);
                }
                catch (ModuleException ex) when (ex.Code is ModuleErrorCode.NotOwned or ModuleErrorCode.Unsupported) { continue; }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Log.Info("module." + module.Id, "match failed: " + ex.Message); continue; }
                if (match is not { PlayableId.Length: > 0 }) continue;
                ResolvedPlayable resolved = await ResolveAsync(ModuleUri.Encode(module.Id, match.PlayableId), force: false, ct).ConfigureAwait(false);
                return new ModuleMatch(module, match, resolved);
            }
            return null;
        }

        /// <summary>Resolve one module playable: cached until the module's own expiry; concurrent resolves of the SAME uri
        /// share one in-flight task, which is dropped by a continuation (a failure is never latched).</summary>
        public Task<ResolvedPlayable> ResolveAsync(string playableUri, bool force, CancellationToken ct)
        {
            if (!ModuleUri.TryDecode(playableUri, out string moduleId, out string playableId))
                throw new ModuleException(ModuleErrorCode.NotOwned, "not a module playable uri: " + playableUri);
            if (force) Playables.Invalidate(playableUri);
            else if (Playables.Get(playableUri) is { } cached) return Task.FromResult(cached);
            Task<ResolvedPlayable> task = _inFlightResolves.GetOrAdd(playableUri,
                static (uri, st) => st.Host.ResolveCoreAsync(uri, st.ModuleId, st.PlayableId, st.Ct),
                (Host: this, ModuleId: moduleId, PlayableId: playableId, Ct: ct));
            string key = playableUri;
            _ = task.ContinueWith(done => _inFlightResolves.TryRemove(key, out Task<ResolvedPlayable>? _),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;
        }

        async Task<ResolvedPlayable> ResolveCoreAsync(string playableUri, string moduleId, string playableId, CancellationToken ct)
        {
            if (!_byId.TryGetValue(moduleId, out InstalledModule? module))
                throw new ModuleException(ModuleErrorCode.NotOwned, "no module named '" + moduleId + "' is installed");
            ResolvedPlayable resolved = await Process(module).RequestAsync(ModuleMethods.Resolve, new ResolveParams(playableId, _prefs()),
                SdkJsonContext.Default.ResolveParams, SdkJsonContext.Default.ResolvedPlayable, ModuleTimeouts.Resolve, ct).ConfigureAwait(false);
            Playables.Put(playableUri, resolved);
            return resolved;
        }

        /// <summary>Fetch the declarative page document for one module entity; cached and deduped like a resolve.
        /// A module that did not declare <c>pages</c> is never spawned to be told -32601.</summary>
        public Task<ModulePageDoc> PageAsync(string moduleUri, CancellationToken ct)
        {
            if (!ModuleUri.TryDecode(moduleUri, out string moduleId, out string entityId))
                throw new ModuleException(ModuleErrorCode.NotOwned, "not a module uri: " + moduleUri);
            if (Pages.Get(moduleUri) is { } cached) return Task.FromResult(cached);
            Task<ModulePageDoc> task = _inFlightPages.GetOrAdd(moduleUri,
                static (uri, st) => st.Host.PageCoreAsync(uri, st.ModuleId, st.EntityId, st.Ct),
                (Host: this, ModuleId: moduleId, EntityId: entityId, Ct: ct));
            string key = moduleUri;
            _ = task.ContinueWith(done => _inFlightPages.TryRemove(key, out Task<ModulePageDoc>? _),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;
        }

        async Task<ModulePageDoc> PageCoreAsync(string moduleUri, string moduleId, string entityId, CancellationToken ct)
        {
            if (!_byId.TryGetValue(moduleId, out InstalledModule? module))
                throw new ModuleException(ModuleErrorCode.NotOwned, "no module named '" + moduleId + "' is installed");
            if (!ModuleCapabilities.Declares(module.Manifest, Modules.Pages.PagesCapability))
                throw new ModuleException(ModuleErrorCode.Unsupported, "module '" + moduleId + "' does not provide pages");
            ModulePageDoc? doc = await Process(module).RequestAsync(ModuleMethods.Page, new PageParams(entityId), SdkJsonContext.Default.PageParams,
                SdkJsonContext.Default.ModulePageDoc, ModuleTimeouts.Page, ct).ConfigureAwait(false);
            if (doc is null) throw new ModuleException(ModuleErrorCode.Unavailable, "module '" + moduleId + "' has no page for this");
            // Re-checked here: "the SDK validated it" is only true of a module that used the SDK.
            ModulePageBudget.Validate(doc);
            Pages.Put(moduleUri, doc);
            return doc;
        }

        /// <summary>Best-effort pre-warm. Never throws, never starts a faulted module, never blocks.</summary>
        public void Warm(string playableUri, string reason = "")
        {
            if (!ModuleUri.TryDecode(playableUri, out string moduleId, out string playableId) || !_byId.TryGetValue(moduleId, out var module)) return;
            ModuleProcess process = Process(module);
            if (process.State is ModuleProcessState.Faulted) return;
            _ = WarmCoreAsync(process, playableId, reason);
        }

        static async Task WarmCoreAsync(ModuleProcess process, string playableId, string reason)
        {
            try
            {
                await process.RequestAsync(ModuleMethods.Warm, new WarmParams(playableId), SdkJsonContext.Default.WarmParams,
                    SdkJsonContext.Default.RpcUnit, ModuleTimeouts.Match, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) { Log.Info("module." + process.Module.Id, "warm (" + reason + ") failed: " + ex.Message); }
        }

        public ModuleProcess Process(InstalledModule module)
        {
            ArgumentNullException.ThrowIfNull(module);
            lock (_processes)
            {
                if (_processes.TryGetValue(module.Id, out ModuleProcess? existing)) return existing;
                var created = new ModuleProcess(module, this, _spawn, _hostVersion, _locale, _prefs, null, _dataRoot);
                _processes[module.Id] = created;
                return created;
            }
        }

        public ModuleProcess? ProcessFor(string? moduleId)
            => moduleId is { Length: > 0 } && _byId.TryGetValue(moduleId, out InstalledModule? m) ? Process(m) : null;

        public IReadOnlyList<ModuleProcess> ActiveProcesses
        {
            get { lock (_processes) { var list = new ModuleProcess[_processes.Count]; _processes.Values.CopyTo(list, 0); return list; } }
        }

        public void Retry(string moduleId) => ProcessFor(moduleId)?.Retry();

        public async Task SweepIdleAsync()
        {
            foreach (ModuleProcess p in ActiveProcesses)
            {
                try { await p.IdleSweepAsync().ConfigureAwait(false); }
                catch (Exception ex) { Log.Info("module." + p.Module.Id, "idle sweep failed: " + ex.Message); }
            }
        }

        void ReinstallServices()
        {
            foreach (ModuleProcess p in ActiveProcesses)
            {
                try { p.NotifyServicesChanged(Services); }
                catch (Exception ex) { Log.Info("module." + p.Module.Id, "re-installing host services failed: " + ex.Message); }
            }
        }

        public void OnMetadata(string moduleId, MetadataUpdate update)
        {
            if (update is not { PlayableId.Length: > 0 }) return;
            string uri = ModuleUri.Encode(moduleId, update.PlayableId);
            if (Playables.Get(uri) is { } cached)
                Playables.Put(uri, cached with
                {
                    Title = update.Title ?? cached.Title,
                    Artists = update.Artists ?? cached.Artists,
                    ArtworkUrl = update.ArtworkUrl ?? cached.ArtworkUrl,
                });
            MetadataChanged?.Invoke(uri, update);
        }

        public void OnExpired(string moduleId, string playableId)
        {
            if (string.IsNullOrEmpty(playableId)) return;
            string uri = ModuleUri.Encode(moduleId, playableId);
            Playables.Invalidate(uri);
            PlayableExpired?.Invoke(uri);
        }

        public void OnStatus(string moduleId, ModuleStatus status) => StatusChanged?.Invoke(moduleId, status);

        public void OnProgress(string moduleId, ProgressNotification progress)
            => Log.Info("module." + moduleId, progress.Stage + " " + progress.Percent.ToString("0", CultureInfo.InvariantCulture) + "%");

        public void OnLog(string moduleId, LogNotification line)
        {
            string category = "module." + moduleId;
            switch (line.Level)
            {
                case ModuleLogLevel.Error: Log.Error(category, line.Message); break;
                case ModuleLogLevel.Warn: Log.Warn(category, line.Message); break;
                default: Log.Info(category, line.Message); break;
            }
        }

        public void RegisterHostServices(InstalledModule module, JsonRpcConnection connection) => Services.Install(module, connection);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Services.Changed -= ReinstallServices;
            _idleTimer?.Dispose();
            if (ReferenceEquals(s_host, this)) Attach(null);
            foreach (ModuleProcess p in ActiveProcesses)
            {
                // The exit tail: bounded by each process's own 2 s shutdown grace.
                try { p.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5)); } catch { }
            }
            lock (_processes) _processes.Clear();
        }
    }

    /// <summary>The fire-and-forget <c>module/action</c> behind a page's moduleAction button: never awaited on the UI
    /// thread, and a refusal is a log line.</summary>
    public static void InvokeAction(string moduleId, string actionId)
    {
        if (s_host?.ProcessFor(moduleId) is not { } process) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await process.RequestAsync(ModuleMethods.Action, new ModuleActionParams(actionId), SdkJsonContext.Default.ModuleActionParams,
                    SdkJsonContext.Default.ModuleActionResult, ModuleTimeouts.Diagnostics, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) { Log.Warn("module." + moduleId, "page action '" + actionId + "' failed: " + ex.Message); }
        });
    }

    // ══ 7. MODULE-SERVED BYTES (0.2.9 ModuleByteStream) ════════════════════════════════════════════════════════════

    /// <summary>A <see cref="Stream"/> over one <c>stream/open|read|close</c> handle: a 256 KiB read-ahead filled in
    /// 64 KiB binary frames, seek = a new offset (when the module says it can). A short read is normal on this wire.</summary>
    public sealed class ModuleByteStream : Stream
    {
        public const int ReadAheadBytes = 256 * 1024;
        public const int ChunkBytes = 64 * 1024;

        readonly ModuleProcess _process;
        readonly string _handle;
        readonly IDisposable _lease;
        readonly byte[] _buffer = new byte[ReadAheadBytes];
        readonly Lock _gate = new();
        long _position, _bufferStart = -1, _knownSize;
        int _bufferLength, _disposed;
        bool _eofSeen, _primed;

        ModuleByteStream(ModuleProcess process, StreamOpenResult open)
        {
            _process = process;
            _handle = open.Handle;
            _knownSize = open.Length is { } len && len > 0 ? len : 0;
            Seekable = open.Seekable;
            ContentType = open.ContentType;
            _lease = process.AcquireStreamLease();
        }

        public bool Seekable { get; }
        public string? ContentType { get; }
        public long KnownSize { get { lock (_gate) return _knownSize; } }

        public static async Task<ModuleByteStream> OpenAsync(ModuleProcess process, string streamId, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(process);
            ArgumentException.ThrowIfNullOrEmpty(streamId);
            StreamOpenResult open = await process.RequestAsync(ModuleMethods.StreamOpen, new StreamOpenParams(streamId),
                SdkJsonContext.Default.StreamOpenParams, SdkJsonContext.Default.StreamOpenResult, ModuleTimeouts.StreamOpen, ct).ConfigureAwait(false);
            if (open.Handle is not { Length: > 0 })
                throw new ModuleException(ModuleErrorCode.Unavailable, process.Module.Id + " opened stream '" + streamId + "' with no handle");
            return new ModuleByteStream(process, open);
        }

        public override bool CanRead => Volatile.Read(ref _disposed) == 0;
        public override bool CanSeek => Seekable && Volatile.Read(ref _disposed) == 0;
        public override bool CanWrite => false;
        public override long Length { get { long s = KnownSize; return s > 0 ? s : throw new NotSupportedException("the module did not report a length"); } }
        public override long Position { get { lock (_gate) return _position; } set => Seek(value, SeekOrigin.Begin); }
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> destination)
        {
            if (destination.Length == 0) return 0;
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            lock (_gate)
            {
                if (!TryServe(destination, out int served))
                {
                    if (!FillAsync(_position).GetAwaiter().GetResult() || !TryServe(destination, out served)) return 0;
                }
                _position += served;
                return served;
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            lock (_gate)
            {
                long target = origin switch
                {
                    SeekOrigin.Begin => offset,
                    SeekOrigin.Current => _position + offset,
                    SeekOrigin.End => (_knownSize > 0 ? _knownSize : throw new NotSupportedException("seek-from-end needs a length")) + offset,
                    _ => throw new ArgumentOutOfRangeException(nameof(origin)),
                };
                if (target < 0) throw new IOException("cannot seek before the start of the stream");
                if (target == _position) return _position;
                if (!Seekable) throw new NotSupportedException(_process.Module.Id + " serves this stream forward-only");
                _position = target;
                _eofSeen = false;
                return _position;
            }
        }

        bool TryServe(Span<byte> destination, out int served)
        {
            served = 0;
            if (_bufferStart < 0 || _position < _bufferStart || _position >= _bufferStart + _bufferLength) return false;
            int start = (int)(_position - _bufferStart);
            served = Math.Min(_bufferLength - start, destination.Length);
            _buffer.AsSpan(start, served).CopyTo(destination);
            return served > 0;
        }

        /// <summary>Refill the window at <paramref name="offset"/>: keep asking until full, EOF, or an empty non-EOF frame;
        /// the FIRST fill stops at the first non-empty chunk (instant start beats read-ahead exactly once).</summary>
        async Task<bool> FillAsync(long offset)
        {
            if (_eofSeen && _knownSize > 0 && offset >= _knownSize) return false;
            _bufferStart = offset;
            _bufferLength = 0;
            bool first = !_primed;
            while (_bufferLength < ReadAheadBytes)
            {
                int want = Math.Min(ChunkBytes, ReadAheadBytes - _bufferLength);
                BinaryPayload payload = await _process.RequestBinaryAsync(ModuleMethods.StreamRead, new StreamReadParams(_handle, offset + _bufferLength, want),
                    SdkJsonContext.Default.StreamReadParams, ModuleTimeouts.StreamRead, CancellationToken.None).ConfigureAwait(false);
                ReadOnlySpan<byte> bytes = payload.Bytes.Span;
                if (bytes.Length > 0)
                {
                    int copy = Math.Min(bytes.Length, ReadAheadBytes - _bufferLength);
                    bytes[..copy].CopyTo(_buffer.AsSpan(_bufferLength));
                    _bufferLength += copy;
                    if (offset + _bufferLength > _knownSize) _knownSize = offset + _bufferLength;
                }
                if (payload.Eof)
                {
                    _eofSeen = true;
                    if (_knownSize <= 0) _knownSize = offset + _bufferLength;
                    break;
                }
                if (bytes.Length == 0 || (first && _bufferLength > 0)) break;
            }
            _primed = true;
            return _bufferLength > 0;
        }

        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0 && disposing)
            {
                _ = CloseAsync(_process, _handle);
                _lease.Dispose();
            }
            base.Dispose(disposing);
        }

        static async Task CloseAsync(ModuleProcess process, string handle)
        {
            if (process.State != ModuleProcessState.Ready) return;
            try
            {
                await process.RequestAsync(ModuleMethods.StreamClose, new StreamCloseParams(handle), SdkJsonContext.Default.StreamCloseParams,
                    SdkJsonContext.Default.RpcUnit, ModuleTimeouts.StreamOpen, CancellationToken.None).ConfigureAwait(false);
            }
            catch { /* the handle dies with the process anyway */ }
        }
    }

    // ══ 8. THE AUDIO OPEN SEAM (Playback.Audio.ModuleOpen — G-021, G-110, G-128) ═══════════════════════════════════════

    /// <summary>The body shapes a resolve answer maps onto (0.2.9 <c>ModuleMediaProvider.HandleFor</c>'s table).</summary>
    public enum AudioShape : byte
    {
        /// <summary>No playable body: no locator, a url that is not http(s), a stream with no id, or an HLS audio
        /// playlist (this pipeline has no HLS audio reader — refused honestly rather than fed to a codec).</summary>
        None,
        /// <summary>A video playable: the audio path refuses it; the video tier plays it (G-148).</summary>
        Video,
        /// <summary><c>stream</c>: the module serves the bytes over <c>stream/open|read|close</c>.</summary>
        ModuleStream,
        /// <summary><c>url</c> + <c>progressive</c>: a finite, rangeable body the app fetches.</summary>
        Progressive,
        /// <summary><c>url</c> + <c>icy</c>, or any live answer: forward-only, reconnecting (<see cref="LiveHttpStream"/>).</summary>
        Live,
    }

    /// <summary>PURE: which body a resolve answer names.</summary>
    public static AudioShape AudioShapeOf(ResolvedPlayable? resolved)
    {
        if (resolved?.Media is not { } media) return AudioShape.None;
        if (resolved.Form == MediaForm.Video) return AudioShape.Video;
        if (string.Equals(media.Kind, MediaLocator.KindStream, StringComparison.Ordinal))
            return media.StreamId is { Length: > 0 } ? AudioShape.ModuleStream : AudioShape.None;
        if (!ModuleRouter.IsHttpUrl(media.Url)) return AudioShape.None;
        if (string.Equals(media.Container, MediaLocator.ContainerHls, StringComparison.OrdinalIgnoreCase)) return AudioShape.None;
        return resolved.IsLive || string.Equals(media.Container, MediaLocator.ContainerIcy, StringComparison.OrdinalIgnoreCase)
            ? AudioShape.Live : AudioShape.Progressive;
    }

    /// <summary>PURE: what the pump knows about a module body before a byte is read. A live body declares NO duration —
    /// that 0 is what keeps every ending-soon / gapless / prepared-next arm off (0.2.9 <c>LiveSessionRules</c>). The
    /// format is the content type's fold; <see cref="Spotify.Audio.Format.Unknown"/> hands the choice to the pump's own
    /// head sniff.</summary>
    public static Playback.Audio.Opened OpenedFor(ResolvedPlayable resolved, string? streamContentType, int bitrateKbps = 0)
    {
        bool live = AudioShapeOf(resolved) == AudioShape.Live || resolved.IsLive;
        Spotify.Audio.Format format = Playback.Audio.SniffContentType(streamContentType ?? resolved.Media?.ContentType)
                                      ?? Spotify.Audio.Format.Unknown;
        long duration = live ? 0 : Math.Max(0, resolved.DurationMs);
        return new Playback.Audio.Opened(format, duration, resolved.GainDb, Playback.Audio.LabelFor(format, 0, 0), bitrateKbps, live);
    }

    /// <summary>PURE: is this body AAC — a content type that names it (but not an MP4 container), or an ADTS frame that
    /// validates against its successor within the first bytes?</summary>
    public static bool LooksAac(string? contentType, ReadOnlySpan<byte> head)
    {
        if (contentType is { Length: > 0 } type && type.Contains("aac", StringComparison.OrdinalIgnoreCase)
            && !type.Contains("mp4", StringComparison.OrdinalIgnoreCase)) return true;
        int sync = Adts.FindSync(head);
        return sync >= 0 && sync < 16;
    }

    /// <summary>THE <c>Playback.Audio.ModuleOpen</c> seam. Blocks — the audio pump, never the UI thread: resolve (cached),
    /// then open the body the answer names. A failure answers a null stream (the pump's <c>Fault.Unavailable</c>) and a
    /// log line with the module's own words; a cancellation propagates.</summary>
    public static (Stream? Stream, Playback.Audio.Opened Opened) OpenAudio(EntityId id, CancellationToken ct)
    {
        if (s_host is not { } host || id.Provider != EntityProvider.Module) return (null, default);
        string uri = id.Text;
        if (!ModuleUri.TryDecode(uri, out string moduleId, out _)) return (null, default);
        string category = "module." + moduleId;
        try
        {
            ResolvedPlayable resolved = host.ResolveAsync(uri, force: false, ct).GetAwaiter().GetResult();
            switch (AudioShapeOf(resolved))
            {
                case AudioShape.ModuleStream:
                {
                    if (host.ProcessFor(moduleId) is not { } process) return (null, default);
                    ModuleByteStream stream = ModuleByteStream.OpenAsync(process, resolved.Media.StreamId!, ct).GetAwaiter().GetResult();
                    Log.Info(category, "audio.open stream len=" + stream.KnownSize.ToString(CultureInfo.InvariantCulture)
                                       + " type=" + (stream.ContentType ?? resolved.Media.ContentType ?? "?"));
                    if (LooksAac(stream.ContentType ?? resolved.Media.ContentType, default) && !AacAudioDecoder.IsAvailable())
                    {
                        stream.Dispose();
                        Log.Warn(category, "audio.open refused: this Windows edition has no AAC decoder (the Media Feature Pack)");
                        return (null, default);
                    }
                    return (stream, OpenedFor(resolved, stream.ContentType));
                }
                case AudioShape.Progressive:
                {
                    Spotify.Audio.FileChoice choice = Spotify.Audio.ExternalChoice(resolved.Media.Url!, resolved.DurationMs);
                    Spotify.Audio.Opened o = Spotify.Audio.Open(in choice, ct);
                    if (!o.Ok || o.Stream is null)
                    {
                        Log.Info(category, "audio.open progressive refused fault=" + o.Fault);
                        try { o.Stream?.Dispose(); } catch { }
                        return (null, default);
                    }
                    return (o.Stream, OpenedFor(resolved, null));
                }
                case AudioShape.Live:
                {
                    var live = LiveHttpStream.Open(resolved.Media.Url!, LiveHttpOptions.Default, ct);
                    string playable = uri;
                    live.TitleChanged += title => OnLiveTitle(playable, title);
                    Log.Info(category, "audio.open live type=" + (live.ContentType ?? "?") + " metaint="
                                       + live.MetaInt.ToString(CultureInfo.InvariantCulture));
                    Playback.Audio.Opened opened = OpenedFor(resolved, live.ContentType, live.BitrateKbps);
                    // A forward-only body cannot be sniffed by the pump (it would have to rewind), so the head is PEEKED
                    // here: the first buffered bytes decide a station that named no content type.
                    Span<byte> head = stackalloc byte[64];
                    int n = live.PeekHead(head);
                    if (opened.Format == Spotify.Audio.Format.Unknown && Playback.Audio.SniffFormat(head[..n]) is { } sniffed)
                        opened = opened with { Format = sniffed, Label = Playback.Audio.LabelFor(sniffed, 0, 0) };
                    if (LooksAac(live.ContentType, head[..n]) && !AacAudioDecoder.IsAvailable())
                    {
                        live.Dispose();
                        Log.Warn(category, "audio.open refused: this Windows edition has no AAC decoder (the Media Feature Pack)");
                        return (null, default);
                    }
                    return (live, opened);
                }
                case AudioShape.Video:
                    Log.Info(category, "audio.open refused: a video playable plays through the video host");
                    return (null, default);
                default:
                    Log.Info(category, "audio.open refused: the answer names no body this pipeline opens");
                    return (null, default);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn(category, "audio.open failed: " + ex.GetType().Name + ": " + ex.Message);
            return (null, default);
        }
    }

    // ══ 9. THE LIVE TRANSPORT (0.2.9 LiveHttpAudioStream + LiveRingBuffer + IcyHttpConnector + IcyDemuxer — G-128) ════

    /// <summary>Tuning for the live transport; every value is a policy. ~3 MiB is ≈ 3 minutes at 128 kbit/s — a stall
    /// absorber, never latency (an overrun drops the OLDEST bytes); the first read waits for the prefill so the codec
    /// sees a real header window; a socket quiet for 15 s is dead; reconnects share a 60 s budget.</summary>
    public sealed record LiveHttpOptions(
        int CapacityBytes = 3 * 1024 * 1024, int PrefillBytes = 24 * 1024, int ConnectTimeoutMs = 10_000,
        int ReadIdleTimeoutMs = 15_000, int BudgetMs = 60_000, int BaseBackoffMs = 500, int MaxBackoffMs = 8_000)
    {
        public static LiveHttpOptions Default { get; } = new();
    }

    /// <summary>One live connection: the status, the headers (lower-cased), the body PREFIXED with whatever the head
    /// read over-consumed, and the url after redirects (reconnects target it).</summary>
    public sealed record LiveResponse(int Status, IReadOnlyDictionary<string, string> Headers, Stream Body, string FinalUrl) : IDisposable
    {
        public string? Header(string name) => Headers.TryGetValue(name, out string? v) ? v : null;
        public void Dispose() { try { Body.Dispose(); } catch { } }
    }

    /// <summary>The connect seam: production is <see cref="IcyConnect"/>; a test scripts heads, bodies and drops.</summary>
    public delegate Task<LiveResponse> LiveConnect(string url, CancellationToken ct);

    /// <summary>A bounded single-producer / single-consumer byte ring for an ENDLESS body. <see cref="Write"/> never
    /// blocks (an overrun drops the oldest bytes and counts them); <see cref="Read"/> does, because the decode edge
    /// reads a zero as end of stream. Completion is three-way: clean (drain, then 0), faulted (drain, then THROW), and
    /// disposed (throw <see cref="ObjectDisposedException"/> at once — the wake a torn-down reader needs).</summary>
    public sealed class LiveRing : IDisposable
    {
        const int WaitSliceMs = 100;
        readonly byte[] _buf;
        readonly object _gate = new();   // Monitor.Wait/PulseAll below: a System.Threading.Lock cannot be waited on
        int _head, _count;
        long _written, _dropped;
        bool _completed, _disposed;
        Exception? _error;

        public LiveRing(int capacityBytes)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(capacityBytes, 1024);
            _buf = new byte[capacityBytes];
        }

        public int Capacity => _buf.Length;
        public int Available { get { lock (_gate) return _count; } }
        public long TotalDropped { get { lock (_gate) return _dropped; } }
        public long TotalWritten { get { lock (_gate) return _written; } }

        public void Write(ReadOnlySpan<byte> data)
        {
            if (data.IsEmpty) return;
            lock (_gate)
            {
                if (_disposed || _completed) return;
                _written += data.Length;
                if (data.Length >= _buf.Length)
                {
                    _dropped += _count + (data.Length - _buf.Length);
                    data[^_buf.Length..].CopyTo(_buf);
                    _head = 0;
                    _count = _buf.Length;
                    Monitor.PulseAll(_gate);
                    return;
                }
                int overflow = _count + data.Length - _buf.Length;
                if (overflow > 0)
                {
                    _head = (_head + overflow) % _buf.Length;
                    _count -= overflow;
                    _dropped += overflow;
                }
                int tail = (_head + _count) % _buf.Length;
                int first = Math.Min(data.Length, _buf.Length - tail);
                data[..first].CopyTo(_buf.AsSpan(tail));
                if (first < data.Length) data[first..].CopyTo(_buf.AsSpan(0));
                _count += data.Length;
                Monitor.PulseAll(_gate);
            }
        }

        /// <summary>Blocking read of at least <paramref name="minAvailable"/> bytes (clamped). 0 ONLY after a clean
        /// completion has drained.</summary>
        public int Read(Span<byte> dst, int minAvailable = 1)
        {
            if (dst.IsEmpty) return 0;
            int want = Math.Clamp(minAvailable, 1, Math.Min(_buf.Length, dst.Length));
            lock (_gate)
            {
                while (true)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    if (_count >= want || (_completed && _count > 0)) break;
                    if (_completed)
                    {
                        if (_error is not null) throw _error;
                        return 0;
                    }
                    Monitor.Wait(_gate, WaitSliceMs);
                }
                int n = Math.Min(dst.Length, _count);
                int first = Math.Min(n, _buf.Length - _head);
                _buf.AsSpan(_head, first).CopyTo(dst);
                if (first < n) _buf.AsSpan(0, n - first).CopyTo(dst[first..]);
                _head = (_head + n) % _buf.Length;
                _count -= n;
                return n;
            }
        }

        /// <summary>Copy buffered bytes without consuming them, waiting at most <paramref name="timeoutMs"/> for
        /// <paramref name="minAvailable"/> of them — the head sniff must see real bytes but must never hang the pump.</summary>
        public int Peek(Span<byte> dst, int minAvailable, int timeoutMs)
        {
            if (dst.IsEmpty) return 0;
            int want = Math.Clamp(minAvailable, 1, _buf.Length);
            long deadline = Environment.TickCount64 + Math.Max(0, timeoutMs);
            lock (_gate)
            {
                while (!_disposed && !_completed && _count < want)
                {
                    long remaining = deadline - Environment.TickCount64;
                    if (remaining <= 0) break;
                    Monitor.Wait(_gate, (int)Math.Min(WaitSliceMs, remaining));
                }
                if (_disposed) return 0;
                int n = Math.Min(dst.Length, _count);
                int first = Math.Min(n, _buf.Length - _head);
                _buf.AsSpan(_head, first).CopyTo(dst);
                if (first < n) _buf.AsSpan(0, n - first).CopyTo(dst[first..]);
                return n;
            }
        }

        public void Complete(Exception? error = null)
        {
            lock (_gate)
            {
                if (_completed) return;
                _completed = true;
                _error = error;
                Monitor.PulseAll(_gate);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _count = 0;
                Monitor.PulseAll(_gate);
            }
        }
    }

    /// <summary>The ICY metaint state machine over producer bytes: audio runs go to the ring, each metadata block's
    /// <c>StreamTitle</c> goes to <see cref="TitleChanged"/> (deduped). A BYTE machine, because a block, its length byte
    /// and the audio run before it routinely straddle three reads.</summary>
    public sealed class IcyDemux(int metaInt)
    {
        const int MaxMetaBytes = 255 * 16;
        readonly int _metaInt = Math.Max(0, metaInt);
        readonly byte[] _meta = new byte[MaxMetaBytes];
        int _untilMeta = Math.Max(0, metaInt), _metaRemaining, _metaFilled;
        bool _needLength;
        string _last = "";

        public event Action<string>? TitleChanged;

        public void Push(ReadOnlySpan<byte> data, LiveRing sink)
        {
            if (_metaInt == 0) { sink.Write(data); return; }
            while (!data.IsEmpty)
            {
                if (_needLength)
                {
                    _metaRemaining = data[0] * 16;
                    _metaFilled = 0;
                    _needLength = false;
                    data = data[1..];
                    if (_metaRemaining == 0) _untilMeta = _metaInt;
                    continue;
                }
                if (_metaRemaining > 0)
                {
                    int take = Math.Min(_metaRemaining, data.Length);
                    data[..take].CopyTo(_meta.AsSpan(_metaFilled));
                    _metaFilled += take;
                    _metaRemaining -= take;
                    data = data[take..];
                    if (_metaRemaining == 0)
                    {
                        _untilMeta = _metaInt;
                        Deliver();
                    }
                    continue;
                }
                int run = Math.Min(_untilMeta, data.Length);
                sink.Write(data[..run]);
                _untilMeta -= run;
                data = data[run..];
                if (_untilMeta == 0) _needLength = true;
            }
        }

        void Deliver()
        {
            string block = Encoding.UTF8.GetString(_meta, 0, _metaFilled);
            if (Playback.Audio.IcyStreamTitle(block) is not { } title) return;
            title = title.Trim();
            if (title.Length == 0 || string.Equals(title, _last, StringComparison.Ordinal)) return;
            _last = title;
            TitleChanged?.Invoke(title);
        }
    }

    /// <summary>The ENDLESS body the pump reads: one ICY/HTTP connection demuxed of its metadata into a bounded ring,
    /// re-established across socket drops without ever ending the track. No length, no seek (the pump's
    /// <c>Prefetching</c> shim sees a forward-only stream); a drop past the budget completes the ring with the error, so
    /// the last buffered second still plays before the read throws.</summary>
    public sealed class LiveHttpStream : Stream
    {
        const int PumpBufferBytes = 32 * 1024;
        const int HeadSniffWaitMs = 3_000;

        readonly LiveRing _ring;
        readonly LiveHttpOptions _options;
        readonly LiveConnect _connect;
        readonly CancellationTokenSource _cts = new();
        readonly Lock _connGate = new();
        readonly string _sourceId;
        LiveResponse _response;
        IcyDemux _demux;
        long _position;
        bool _firstRead = true;
        int _disposed;

        LiveHttpStream(LiveResponse response, LiveHttpOptions options, LiveConnect connect)
        {
            _options = options;
            _connect = connect;
            _response = response;
            _ring = new LiveRing(options.CapacityBytes);
            _sourceId = Uri.TryCreate(response.FinalUrl, UriKind.Absolute, out Uri? final) ? final.Host : "live";
            _demux = Adopt(response);
        }

        public string FinalUrl { get; private set; } = "";
        public string? ContentType { get; private set; }
        public string? StationName { get; private set; }
        public int BitrateKbps { get; private set; }
        public int MetaInt { get; private set; }
        public long DroppedBytes => _ring.TotalDropped;

        /// <summary>A station title change (the producer thread).</summary>
        public event Action<string>? TitleChanged;

        /// <summary>Open over a real socket, blocking. ONE connect attempt: a first-connect failure is an immediate,
        /// typed failure, never a silent budget the user watches spin.</summary>
        public static LiveHttpStream Open(string url, LiveHttpOptions options, CancellationToken ct)
            => OpenAsync(url, (u, c) => IcyConnect(u, options.ConnectTimeoutMs, c), options, ct).GetAwaiter().GetResult();

        /// <summary>The seam overload the tests drive.</summary>
        public static async Task<LiveHttpStream> OpenAsync(string url, LiveConnect connect, LiveHttpOptions options, CancellationToken ct)
        {
            LiveResponse response = await connect(url, ct).ConfigureAwait(false);
            if (response.Status is < 200 or >= 300)
            {
                int status = response.Status;
                response.Dispose();
                throw new IOException("live stream refused: HTTP " + status.ToString(CultureInfo.InvariantCulture));
            }
            var stream = new LiveHttpStream(response, options, connect);
            CancellationToken token = stream._cts.Token;
            var pump = new Thread(() => stream.Pump(token)) { IsBackground = true, Name = "wavee-live-" + stream._sourceId };
            pump.Start();
            return stream;
        }

        IcyDemux Adopt(LiveResponse response)
        {
            ContentType = response.Header("content-type") ?? ContentType;
            StationName = response.Header("icy-name") ?? StationName;
            if (int.TryParse(response.Header("icy-br"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int br) && br > 0) BitrateKbps = br;
            MetaInt = int.TryParse(response.Header("icy-metaint"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int mi) && mi > 0 ? mi : 0;
            FinalUrl = response.FinalUrl;
            var demux = new IcyDemux(MetaInt);
            demux.TitleChanged += OnTitle;
            return demux;
        }

        void OnTitle(string title) => TitleChanged?.Invoke(title);

        /// <summary>The first buffered bytes WITHOUT consuming them — the codec sniff.</summary>
        public int PeekHead(Span<byte> dst) => _ring.Peek(dst, dst.Length, HeadSniffWaitMs);

        // The producer: one named thread per live body (P10), reading the socket and demuxing into the ring.
        void Pump(CancellationToken ct)
        {
            var buffer = new byte[PumpBufferBytes];
            while (!ct.IsCancellationRequested)
            {
                int n;
                try
                {
                    n = ReadBody(buffer, ct);
                    if (n <= 0) throw new IOException("live stream body closed");
                }
                catch (Exception) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    if (!Recover(ex, ct)) return;
                    continue;
                }
                IcyDemux demux;
                lock (_connGate) demux = _demux;
                demux.Push(buffer.AsSpan(0, n), _ring);
            }
        }

        int ReadBody(byte[] buffer, CancellationToken ct)
        {
            Stream body;
            lock (_connGate) body = _response.Body;
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idle.CancelAfter(_options.ReadIdleTimeoutMs);
            try { return body.ReadAsync(buffer.AsMemory(), idle.Token).AsTask().GetAwaiter().GetResult(); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new IOException("live stream idle for " + _options.ReadIdleTimeoutMs.ToString(CultureInfo.InvariantCulture) + " ms");
            }
        }

        /// <summary>Reconnect inside the budget. The ring is NOT cleared: the buffered tail keeps playing while the
        /// socket comes back and the decoder resyncs over the splice. False = the stream is finished.</summary>
        bool Recover(Exception cause, CancellationToken ct)
        {
            long started = Environment.TickCount64;
            int attempt = 0;
            Exception error = cause;
            Log.Info("module", "live.drop host=" + _sourceId + " " + cause.GetType().Name + ": " + cause.Message);
            while (!ct.IsCancellationRequested)
            {
                if (Environment.TickCount64 - started >= _options.BudgetMs)
                {
                    _ring.Complete(new IOException("live stream lost after " + attempt.ToString(CultureInfo.InvariantCulture) + " reconnects", error));
                    return false;
                }
                if (ct.WaitHandle.WaitOne(BackoffMs(attempt, _options))) return false;
                attempt++;
                LiveResponse? next = null;
                try
                {
                    next = _connect(FinalUrl, ct).GetAwaiter().GetResult();
                    if (next.Status is < 200 or >= 300)
                        throw new IOException("live reconnect refused: HTTP " + next.Status.ToString(CultureInfo.InvariantCulture));
                    LiveResponse old;
                    lock (_connGate)
                    {
                        old = _response;
                        _demux.TitleChanged -= OnTitle;
                        _response = next;
                        _demux = Adopt(next);
                    }
                    next = null;
                    old.Dispose();
                    Log.Info("module", "live.recovered host=" + _sourceId + " attempt=" + attempt.ToString(CultureInfo.InvariantCulture));
                    return true;
                }
                catch (Exception) when (ct.IsCancellationRequested) { next?.Dispose(); return false; }
                catch (Exception ex) { next?.Dispose(); error = ex; }
            }
            return false;
        }

        /// <summary>PURE: the reconnect delay — base × 4^attempt, capped (no jitter: one listener, one station).</summary>
        public static int BackoffMs(int attempt, LiveHttpOptions options)
            => (int)Math.Clamp(options.BaseBackoffMs * Math.Pow(4, Math.Clamp(attempt, 0, 8)), 1, options.MaxBackoffMs);

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty) return 0;
            int min = _firstRead ? Math.Max(1, Math.Min(_options.PrefillBytes, buffer.Length)) : 1;
            int n = _ring.Read(buffer, min);
            _firstRead = false;
            if (n > 0) Interlocked.Add(ref _position, n);
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException("a live stream has no length");
        public override long Position { get => Interlocked.Read(ref _position); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin)
            => offset == 0 && origin is SeekOrigin.Begin or SeekOrigin.Current ? Interlocked.Read(ref _position)
               : throw new NotSupportedException("a live stream cannot seek");
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                // Cancel the producer, wake the (possibly blocked) reader, then close the socket.
                try { _cts.Cancel(); } catch { }
                _ring.Dispose();
                lock (_connGate) _response.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>A hand-rolled HTTP/1.0 GET for Icecast/SHOUTcast, because the BCL parser rejects SHOUTcast v1's
    /// <c>ICY 200 OK</c>. 1.0 has no chunked encoding (the body is raw to EOF), <c>Icy-MetaData: 1</c> asks for the
    /// interleaved titles, and the head parse is <c>Playback.Audio.TryParseIcyHead</c> — one parser for the app.</summary>
    public static async Task<LiveResponse> IcyConnect(string url, int connectTimeoutMs, CancellationToken ct)
    {
        const int MaxHeadBytes = 32 * 1024, MaxRedirects = 5;
        string current = url;
        for (int hop = 0; ; hop++)
        {
            if (!Uri.TryCreate(current, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https"))
                throw new IOException("live stream url is not http(s)");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Math.Max(1000, connectTimeoutMs));
            var client = new TcpClient { NoDelay = true };
            Stream? transport = null;
            try
            {
                await client.ConnectAsync(uri.Host, uri.Port, timeout.Token).ConfigureAwait(false);
                transport = client.GetStream();
                if (uri.Scheme == "https")
                {
                    var ssl = new SslStream(transport, leaveInnerStreamOpen: false);
                    await ssl.AuthenticateAsClientAsync(uri.IdnHost).ConfigureAwait(false);
                    transport = ssl;
                }
                string path = uri.PathAndQuery.Length == 0 ? "/" : uri.PathAndQuery;
                string host = uri.IsDefaultPort ? uri.IdnHost : uri.IdnHost + ":" + uri.Port.ToString(CultureInfo.InvariantCulture);
                byte[] request = Encoding.ASCII.GetBytes("GET " + path + " HTTP/1.0\r\nHost: " + host + "\r\nUser-Agent: Wavee/"
                    + Platform.Version.Core + "\r\nAccept: */*\r\nIcy-MetaData: 1\r\nConnection: close\r\n\r\n");
                await transport.WriteAsync(request, timeout.Token).ConfigureAwait(false);
                await transport.FlushAsync(timeout.Token).ConfigureAwait(false);

                var buf = new byte[MaxHeadBytes];
                int filled = 0, consumed;
                Playback.Audio.IcyHead head;
                while (!Playback.Audio.TryParseIcyHead(buf.AsSpan(0, filled), out head, out consumed))
                {
                    if (filled == buf.Length) throw new IOException("live stream response head exceeded 32 KiB");
                    int n = await transport.ReadAsync(buf.AsMemory(filled), timeout.Token).ConfigureAwait(false);
                    if (n <= 0) throw new IOException("live stream closed before its response head completed");
                    filled += n;
                }
                if (head.Status is >= 300 and < 400 && head.Location is { Length: > 0 } location)
                {
                    if (hop >= MaxRedirects) throw new IOException("live stream redirect limit exceeded");
                    current = Uri.TryCreate(uri, location, out Uri? abs) ? abs.ToString() : location;
                    transport.Dispose();
                    client.Dispose();
                    continue;
                }
                var headers = new Dictionary<string, string>(StringComparer.Ordinal);
                if (head.ContentType is { } type) headers["content-type"] = type;
                if (head.Name is { } name) headers["icy-name"] = name;
                if (head.BitrateKbps > 0) headers["icy-br"] = head.BitrateKbps.ToString(CultureInfo.InvariantCulture);
                if (head.MetaInt > 0) headers["icy-metaint"] = head.MetaInt.ToString(CultureInfo.InvariantCulture);
                var body = new PrefixedStream(transport, client, buf.AsSpan(consumed, filled - consumed).ToArray());
                return new LiveResponse(head.Status, headers, body, uri.ToString());
            }
            catch
            {
                transport?.Dispose();
                client.Dispose();
                throw;
            }
        }
    }

    /// <summary>A body that first hands back the bytes the head read pulled in ahead of itself, then the socket. Owns
    /// the transport and the client.</summary>
    sealed class PrefixedStream(Stream inner, IDisposable client, byte[] prefix) : Stream
    {
        int _prefixPos, _disposed;

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty) return 0;
            if (_prefixPos < prefix.Length)
            {
                int n = Math.Min(buffer.Length, prefix.Length - _prefixPos);
                prefix.AsSpan(_prefixPos, n).CopyTo(buffer);
                _prefixPos += n;
                return n;
            }
            return inner.Read(buffer);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (buffer.IsEmpty) return ValueTask.FromResult(0);
            if (_prefixPos < prefix.Length)
            {
                int n = Math.Min(buffer.Length, prefix.Length - _prefixPos);
                prefix.AsSpan(_prefixPos, n).CopyTo(buffer.Span);
                _prefixPos += n;
                return ValueTask.FromResult(n);
            }
            return inner.ReadAsync(buffer, ct);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                try { inner.Dispose(); } catch { }
                try { client.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }

    // ══ 10. AAC (0.2.9 AdtsFrameParser + AdtsFrameReader + AacSampleSource — G-128) ════════════════════════════════════

    /// <summary>One parsed ADTS frame header (ISO/IEC 13818-7 §6.2). <see cref="SampleRate"/> is the CORE rate — HE-AAC
    /// output is double it.</summary>
    public readonly record struct AdtsHeader(bool Mpeg2, int Profile, int SamplingFrequencyIndex, int SampleRate,
        int ChannelConfiguration, bool ProtectionAbsent, int FrameLength, int HeaderLength, int RawDataBlocks);

    /// <summary>ADTS header parsing and AudioSpecificConfig synthesis. PURE. The MFT still has to be CONFIGURED before
    /// its first frame, and an ICY body has no container to ask, so the first frame's header IS the configuration.</summary>
    public static class Adts
    {
        static ReadOnlySpan<int> SampleRates => [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350, 0, 0, 0];

        public const int MinHeaderBytes = 7;

        public static bool TryParseHeader(ReadOnlySpan<byte> data, out AdtsHeader header)
        {
            header = default;
            if (data.Length < MinHeaderBytes || data[0] != 0xFF || (data[1] & 0xF0) != 0xF0) return false;
            if (((data[1] >> 1) & 0x03) != 0) return false;   // layer 00: an MPEG audio frame also starts FFFx
            int sfi = (data[2] >> 2) & 0x0F;
            int rate = SampleRates[sfi];
            if (rate == 0) return false;
            bool protectionAbsent = (data[1] & 0x01) != 0;
            int frameLength = ((data[3] & 0x03) << 11) | (data[4] << 3) | ((data[5] >> 5) & 0x07);
            int headerLength = protectionAbsent ? 7 : 9;
            if (frameLength < headerLength) return false;
            header = new AdtsHeader((data[1] & 0x08) != 0, (data[2] >> 6) & 0x03, sfi, rate,
                ((data[2] & 0x01) << 2) | ((data[3] >> 6) & 0x03), protectionAbsent, frameLength, headerLength, (data[6] & 0x03) + 1);
            return true;
        }

        /// <summary>The first offset that starts a plausible frame, or −1. A bare syncword false-positives constantly
        /// inside compressed audio, so a candidate stands only when the frame it declares lands on ANOTHER header —
        /// unless the buffer is too short to check (the caller reads more and re-validates).</summary>
        public static int FindSync(ReadOnlySpan<byte> data)
        {
            for (int i = 0; i + MinHeaderBytes <= data.Length; i++)
            {
                if (!TryParseHeader(data[i..], out AdtsHeader h)) continue;
                int next = i + h.FrameLength;
                if (next + MinHeaderBytes > data.Length || TryParseHeader(data[next..], out _)) return i;
            }
            return -1;
        }

        /// <summary>The 2-byte MPEG-4 AudioSpecificConfig the stream implies (object type, rate index, channels).</summary>
        public static int WriteAudioSpecificConfig(in AdtsHeader header, Span<byte> dst)
        {
            if (dst.Length < 2) throw new ArgumentException("AudioSpecificConfig needs 2 bytes", nameof(dst));
            int objectType = header.Profile + 1;
            int sfi = header.SamplingFrequencyIndex & 0x0F;
            int channels = header.ChannelConfiguration <= 0 ? 2 : header.ChannelConfiguration;
            dst[0] = (byte)(((objectType & 0x1F) << 3) | ((sfi >> 1) & 0x07));
            dst[1] = (byte)(((sfi & 0x01) << 7) | ((channels & 0x0F) << 3));
            return 2;
        }
    }

    /// <summary>Whole ADTS frames off a forward-only stream, resynchronising over junk and over a reconnect's splice.
    /// One growable buffer; the frame span is valid until the next call.</summary>
    public sealed class AdtsFrameReader(Stream stream)
    {
        const int MaxFrameBytes = 8 * 1024;
        byte[] _buf = new byte[16 * 1024];
        int _start, _end;
        bool _eof;

        /// <summary>Bytes skipped resynchronising — non-zero after a splice.</summary>
        public long ResyncSkipped { get; private set; }

        public bool TryReadFrame(out ReadOnlySpan<byte> frame, out AdtsHeader header)
        {
            while (true)
            {
                int available = _end - _start;
                if (available >= Adts.MinHeaderBytes)
                {
                    int sync = Adts.FindSync(_buf.AsSpan(_start, available));
                    if (sync > 0) { ResyncSkipped += sync; _start += sync; available -= sync; }
                    if (sync >= 0 && Adts.TryParseHeader(_buf.AsSpan(_start, available), out AdtsHeader h))
                    {
                        if (h.FrameLength <= available)
                        {
                            frame = _buf.AsSpan(_start, h.FrameLength);
                            header = h;
                            _start += h.FrameLength;
                            return true;
                        }
                    }
                    else if (sync < 0)
                    {
                        ResyncSkipped += Math.Max(0, available - (Adts.MinHeaderBytes - 1));
                        _start = Math.Max(_start, _end - (Adts.MinHeaderBytes - 1));
                    }
                }
                if (_eof || !Fill()) { frame = default; header = default; return false; }
            }
        }

        bool Fill()
        {
            if (_start > 0)
            {
                int len = _end - _start;
                if (len > 0) Array.Copy(_buf, _start, _buf, 0, len);
                _start = 0;
                _end = len;
            }
            if (_end == _buf.Length)
            {
                if (_buf.Length >= MaxFrameBytes * 2) { ResyncSkipped += _buf.Length; _start = _end = 0; }
                else Array.Resize(ref _buf, _buf.Length * 2);
            }
            int n = stream.Read(_buf, _end, _buf.Length - _end);
            if (n <= 0) { _eof = true; return false; }
            _end += n;
            return true;
        }
    }

    /// <summary>AAC (ADTS) behind the pump's decoder seam: frames from <see cref="AdtsFrameReader"/>, decode by Media
    /// Foundation's in-box AAC MFT (<see cref="MfAacDecoder"/>). <b>Priming is not optional</b>: HE-AAC reveals its real
    /// (doubled) output rate only on the first output, as a stream change, so the open decodes up to
    /// <see cref="MaxPrimeFrames"/> frames and discards anything produced before the change — publishing the core rate
    /// would bind the resampler at half the true rate. A change AFTER priming is a genuine mid-stream encoding switch:
    /// the read answers an error and the retry re-opens (and re-primes). Not seekable: a live body has no timeline.</summary>
    public sealed class AacAudioDecoder(float gainDb, float peak = 0f) : IAudioDecoder, IDisposable
    {
        /// <summary>Enough to get past the implicit-SBR change (the first or second frame) without burning real audio.</summary>
        public const int MaxPrimeFrames = 8;

        readonly float _gain = Playback.Audio.GainLinear(gainDb, peak);
        readonly float[] _scratch = new float[16384];
        AdtsFrameReader? _reader;
        MfAacDecoder? _decoder;
        LinearResampler? _resampler;
        MixFormat _target;
        int _srcRate, _srcChannels;
        float[] _held = [];        // interleaved SOURCE-rate samples, conformed to the target channel count
        int _heldStart, _heldCount;
        float[] _out = [];
        bool _eof, _faulted;

        public GaplessInfo Gapless => GaplessInfo.None;

        /// <summary>Whether this Windows edition carries the AAC MFT ("N" editions without the media feature pack do
        /// not) — the host maps false to a refusal rather than a noise decode.</summary>
        public static bool IsAvailable() => MfAacDecoder.IsAvailable();

        public bool TryOpen(IMediaByteSource src, MixFormat target, out DecodedInfo info)
        {
            info = default;
            _target = target;
            try
            {
                _reader = new AdtsFrameReader(new Playback.Audio.ByteSourceStream(src));
                if (!_reader.TryReadFrame(out ReadOnlySpan<byte> first, out AdtsHeader header))
                {
                    Log.Warn("audio", "aac open: the stream carried no ADTS frame");
                    return false;
                }
                Span<byte> asc = stackalloc byte[2];
                Adts.WriteAudioSpecificConfig(in header, asc);
                _decoder = new MfAacDecoder(header.SampleRate, header.ChannelConfiguration, asc);
                Prime(first);
                _srcRate = _decoder.OutputSampleRate > 0 ? _decoder.OutputSampleRate : header.SampleRate;
                _srcChannels = _decoder.OutputChannels > 0 ? _decoder.OutputChannels : Math.Max(1, header.ChannelConfiguration);
                _resampler = _srcRate != target.SampleRate ? new LinearResampler(_srcRate, target.SampleRate, target.Channels) : null;
                info = new DecodedInfo(new MediaContentType(Container.Adts, CodecId.None, CodecId.Aac),
                    new MixFormat(_srcRate, _srcChannels), TimeSpan.Zero, default);
                Log.Info("audio", "aac.open core=" + header.SampleRate.ToString(CultureInfo.InvariantCulture) + " out="
                                  + _srcRate.ToString(CultureInfo.InvariantCulture) + "/" + _srcChannels.ToString(CultureInfo.InvariantCulture));
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("audio", "aac open failed", ex);
                Dispose();
                return false;
            }
        }

        void Prime(ReadOnlySpan<byte> firstFrame)
        {
            PushAndDrain(firstFrame, primingPhase: true);
            for (int i = 1; i < MaxPrimeFrames && _heldCount == 0 && !_eof; i++)
            {
                if (!_reader!.TryReadFrame(out ReadOnlySpan<byte> frame, out _)) { _eof = true; break; }
                PushAndDrain(frame, primingPhase: true);
            }
            if (_heldCount == 0 && !_eof) throw new InvalidOperationException("AAC: the decoder produced no samples while priming");
        }

        void PushAndDrain(ReadOnlySpan<byte> frame, bool primingPhase)
        {
            if (!_decoder!.TryPushFrame(frame))
            {
                DrainRaw();
                _decoder.TryPushFrame(frame);
            }
            DrainRaw();
            if (!_decoder.FormatChanged) return;
            if (!primingPhase) { _faulted = true; return; }
            // Everything decoded before the change was at the pre-SBR format — it must never reach the mixer.
            _decoder.ClearFormatChanged();
            _heldStart = _heldCount = 0;
        }

        /// <summary>Drain the MFT's output as-is (source channels) into the hold.</summary>
        void DrainRaw()
        {
            while (true)
            {
                int n = _decoder!.ReadSamples(_scratch);
                if (n <= 0) return;
                Append(_scratch.AsSpan(0, n));
            }
        }

        void Append(ReadOnlySpan<float> samples)
        {
            if (_heldCount == 0) _heldStart = 0;
            int needed = _heldStart + _heldCount + samples.Length;
            if (_held.Length < needed)
            {
                if (_heldStart > 0 && _heldCount > 0) Array.Copy(_held, _heldStart, _held, 0, _heldCount);
                _heldStart = 0;
                needed = _heldCount + samples.Length;
                if (_held.Length < needed) Array.Resize(ref _held, Math.Max(needed, 32768));
            }
            samples.CopyTo(_held.AsSpan(_heldStart + _heldCount));
            _heldCount += samples.Length;
        }

        public int Read(Span<float> dst)
        {
            if (_decoder is null || _faulted) return -1;
            int ch = _target.Channels;
            int wantFrames = dst.Length / Math.Max(1, ch);
            if (wantFrames <= 0) return 0;
            int srcCh = Math.Max(1, _srcChannels);
            // Fill the hold with at least one frame's worth of SOURCE samples.
            while (_heldCount < srcCh)
            {
                if (_eof) return 0;
                if (!_reader!.TryReadFrame(out ReadOnlySpan<byte> frame, out _)) { _eof = true; return 0; }
                PushAndDrain(frame, primingPhase: false);
                if (_faulted)
                {
                    Log.Warn("audio", "aac: the stream changed encoding mid-play");
                    return -1;
                }
            }
            int srcFrames = _heldCount / srcCh;
            if (_resampler is { IsActive: true } rs) srcFrames = Math.Min(srcFrames, Math.Max(1, rs.SrcFramesForOutput(wantFrames)));
            else srcFrames = Math.Min(srcFrames, wantFrames);
            int conformedLength = srcFrames * ch;
            if (_out.Length < conformedLength) _out = new float[Math.Max(conformedLength, 8192)];
            ConformInto(_held.AsSpan(_heldStart, srcFrames * srcCh), srcCh, _out.AsSpan(0, conformedLength), ch, _gain);

            int produced, consumed;
            if (_resampler is { IsActive: true } active)
            {
                ResampleResult rr = active.Process(_out.AsSpan(0, conformedLength), srcFrames, dst);
                produced = rr.Produced;
                consumed = rr.Consumed;
            }
            else
            {
                _out.AsSpan(0, conformedLength).CopyTo(dst);
                produced = consumed = srcFrames;
            }
            _heldStart += consumed * srcCh;
            _heldCount -= consumed * srcCh;
            if (_heldCount <= 0) { _heldStart = 0; _heldCount = 0; }
            return produced;
        }

        /// <summary>PURE: channel conform + gain from an interleaved source span into a destination span of
        /// <paramref name="dstChannels"/>. Mono duplicates; more than the target downmixes pairwise.</summary>
        public static void ConformInto(ReadOnlySpan<float> src, int srcChannels, Span<float> dst, int dstChannels, float gain)
        {
            int frames = src.Length / Math.Max(1, srcChannels);
            if (srcChannels == dstChannels)
            {
                for (int i = 0; i < frames * dstChannels; i++) dst[i] = src[i] * gain;
                return;
            }
            for (int f = 0; f < frames; f++)
            {
                if (srcChannels == 1)
                {
                    float v = src[f] * gain;
                    for (int c = 0; c < dstChannels; c++) dst[f * dstChannels + c] = v;
                    continue;
                }
                float l = 0f, r = 0f;
                int lc = 0, rc = 0;
                for (int c = 0; c < srcChannels; c++)
                {
                    float v = src[f * srcChannels + c];
                    if ((c & 1) == 0) { l += v; lc++; } else { r += v; rc++; }
                }
                l = l / Math.Max(1, lc) * gain;
                r = rc > 0 ? r / rc * gain : l;
                if (dstChannels == 1) dst[f] = (l + r) * 0.5f;
                else { dst[f * dstChannels] = l; dst[f * dstChannels + 1] = r; }
            }
        }

        public long Seek(long frame) => frame == 0 && _decoder is not null ? 0 : -1;

        public void Dispose()
        {
            try { _decoder?.Dispose(); } catch { }
            _decoder = null;
        }
    }

    // ══ 11. MODULE PLAYABLES AS TRACK ROWS ═════════════════════════════════════════════════════════════════════════

    /// <summary>The groups a module row speaks for: its identity, a playable verdict and its video bit.</summary>
    public const TrackFields RowFields = TrackFields.Identity | TrackFields.Availability | TrackFields.Video;

    /// <summary>Stage one module playable's row from its resolve answer. Any thread (a decoder's shape: text into the
    /// arena, nothing interned). <paramref name="title"/>/<paramref name="artistLine"/> override the answer's — the live
    /// ICY correction.</summary>
    public static void StageRow(Staging s, string playableUri, ResolvedPlayable resolved, string? title = null, string? artistLine = null)
    {
        ref StagedTrack row = ref s.Tracks.RowFor(new StagedId(s.AddText(Encoding.UTF8.GetBytes(playableUri))), Authority.Full, (uint)RowFields);
        row.Title = TextOf(s, Trimmed(title) ?? Trimmed(resolved.Title) ?? playableUri);
        row.ArtistLine = TextOf(s, Trimmed(artistLine) ?? JoinArtists(resolved.Artists));
        row.Image = TextOf(s, Trimmed(resolved.ArtworkUrl));
        row.DurationMs = resolved.IsLive ? 0 : (int)Math.Clamp(resolved.DurationMs, 0, int.MaxValue);
        row.Flags = resolved.Form == MediaForm.Video ? (uint)TrackFlags.HasVideo : 0u;
        s.Tracks.Settle();
    }

    static TextRef TextOf(Staging s, string? value) => value is { Length: > 0 } ? s.AddText(Encoding.UTF8.GetBytes(value)) : default;

    /// <summary>PURE: the credit line a module's artist NAMES make (no uris behind them).</summary>
    public static string? JoinArtists(string[]? artists)
    {
        if (artists is not { Length: > 0 }) return null;
        var sb = new StringBuilder();
        for (int i = 0; i < artists.Length; i++)
        {
            if (Trimmed(artists[i]) is not { } name) continue;
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(name);
        }
        return sb.Length == 0 ? null : sb.ToString();
    }

    /// <summary>Commit one module playable's row and publish. UI THREAD (C1). Returns the handle.</summary>
    public static Track CommitRow(string playableUri, ResolvedPlayable resolved, string? title = null, string? artistLine = null)
    {
        Staging s = Staging.Rent();
        try
        {
            StageRow(s, playableUri, resolved, title, artistLine);
            Entities.Commit(s);
        }
        finally { Staging.Return(s); }
        Entities.Publish();
        return new Track(Entities.Current.Tracks.Slot(playableUri.AsSpan()));
    }

    /// <summary>A module pushed a now-playing correction (module thread): the host already merged it into the resolve
    /// cache, so the row is re-staged from the merged answer on the UI thread — only when the row exists.</summary>
    static void OnModuleMetadata(string playableUri, MetadataUpdate update)
        => Playback.ToUi(() =>
        {
            if (Playables.Cache?.GetIncludingExpired(playableUri) is not { } merged) return;
            if (Entities.Current is null || !Entities.Current.Tracks.TryGetSlot(playableUri.AsSpan(), out _)) return;
            CommitRow(playableUri, merged);
        });

    /// <summary>An ICY <c>StreamTitle</c> from the app's own live transport (producer thread): "Artist - Title" splits
    /// on the first " - ", and with no separator the station stays the credit.</summary>
    static void OnLiveTitle(string playableUri, string raw)
        => Playback.ToUi(() =>
        {
            if (Playables.Cache?.GetIncludingExpired(playableUri) is not { } resolved) return;
            if (Entities.Current is null || !Entities.Current.Tracks.TryGetSlot(playableUri.AsSpan(), out _)) return;
            (string title, string? artist) = Playback.Audio.SplitIcyTitle(raw);
            CommitRow(playableUri, resolved, title, artist ?? Trimmed(resolved.Title));
        });

    /// <summary>The reducer met a module row with no identity (a restored queue, a Connect snapshot): resolve each uri
    /// and answer the batch with staged rows. <c>Start</c> is on the UI thread and must not block, so the work runs on
    /// the pool and the answer posts back.</summary>
    sealed class ModuleFetchProvider : FetchProvider
    {
        public override EntityProvider Provider => EntityProvider.Module;

        public override void Start(FetchBatch batch)
        {
            uint ticket = batch.Ticket, epoch = batch.Epoch;
            if (s_host is not { } host || batch.Subject == FetchSubject.Edge || batch.Kind != EntityKind.Track || batch.Count == 0)
            {
                Playback.ToUi(() => Fetch.Answer(ticket, null));   // nobody will ever answer these: seal them
                return;
            }
            var uris = new string[batch.Count];
            for (int i = 0; i < uris.Length; i++) uris[i] = batch.Uri(i);
            _ = Task.Run(async () =>
            {
                Staging? staging = null;
                foreach (string uri in uris)
                {
                    try
                    {
                        ResolvedPlayable resolved = await host.ResolveAsync(uri, force: false, CancellationToken.None).ConfigureAwait(false);
                        staging ??= Staging.Rent();
                        StageRow(staging, uri, resolved);
                    }
                    catch (Exception ex) { Log.Info("module", "row resolve failed: " + ex.Message); }
                }
                if (staging is not null) staging.Epoch = epoch;
                Playback.ToUi(() => Fetch.Answer(ticket, staging));
            });
        }
    }

    // ══ 12. THE VIDEO TIER (G-148) ═════════════════════════════════════════════════════════════════════════════════

    static int s_videoTierInstalled;

    /// <summary>PURE: a module VIDEO answer's source — its http(s) url as a clear source, live-flagged. Anything else
    /// (audio form, a stream locator the video host cannot open, no url) answers null, which the reducer demotes to
    /// audio.</summary>
    public static Playback.Video.VideoSource? VideoSourceOf(ResolvedPlayable? resolved)
        => resolved is { Form: MediaForm.Video, Media: { } media }
           && string.Equals(media.Kind, MediaLocator.KindUrl, StringComparison.Ordinal) && ModuleRouter.IsHttpUrl(media.Url)
            ? Playback.Video.VideoSource.Clear(media.Url!, resolved.IsLive)
            : null;

    /// <summary>Blocks (an api thread): resolve a module row and answer its video source.</summary>
    public static Playback.Video.VideoSource? VideoSourceFor(EntityId id, CancellationToken ct)
    {
        if (s_host is not { } host || id.Provider != EntityProvider.Module) return null;
        try { return VideoSourceOf(host.ResolveAsync(id.Text, force: false, ct).GetAwaiter().GetResult()); }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) { Log.Warn("module", "video resolve failed: " + ex.Message); return null; }
    }

    /// <summary>Put the module tier IN FRONT of whatever resolver is installed (the manifest tier by default; B7's
    /// tiers when they install first). Idempotent. A resolver installed AFTER this that does not chain the previous one
    /// drops the module tier — B7 must capture <c>Playback.VideoResolver</c> before replacing it.</summary>
    public static void InstallVideoTier()
    {
        if (Interlocked.Exchange(ref s_videoTierInstalled, 1) != 0) return;
        Func<EntityId, string, CancellationToken, Playback.Video.VideoSource?> prior = Playback.VideoResolver;
        Playback.VideoResolver = (id, gid, ct) => id.Provider == EntityProvider.Module ? VideoSourceFor(id, ct) : prior(id, gid, ct);
    }

    // ══ 13. THE LINK ROUTER (Shell.MatchLink) ══════════════════════════════════════════════════════════════════════

    /// <summary>THE <c>Shell.MatchLink</c> seam: match → resolve through the host; the one play verb commits the row and
    /// plays it (lighting the video surface first when the answer is video). A module's own failure THROWS with its own
    /// words, which the card shows.</summary>
    public static async Task<Shell.LinkMatch?> MatchLinkAsync(string text, string? pinnedModuleId, CancellationToken ct)
    {
        if (s_host is not { } host) return null;
        ModuleMatch? match = await host.MatchAsync(text, pinnedModuleId, ct).ConfigureAwait(false);
        if (match is null) return null;
        string uri = ModuleUri.Encode(match.Module.Id, match.Match.PlayableId);
        ResolvedPlayable resolved = match.Resolved;
        string name = Trimmed(match.Module.Manifest.DisplayName) ?? match.Module.Id;
        return new Shell.LinkMatch(name, Trimmed(resolved.Title) ?? Trimmed(match.Match.Title), resolved.IsLive, () => Play(uri, resolved));
    }

    /// <summary>Play one module playable now. UI THREAD: commit its row, light the video surface for a video answer,
    /// and play it as its own one-row context.</summary>
    public static void Play(string playableUri, ResolvedPlayable resolved)
    {
        if (Entities.Current is null) return;
        Track track = CommitRow(playableUri, resolved);
        if (!track.IsValid) return;
        if (resolved.Form == MediaForm.Video) LightVideo();
        ReadOnlySpan<EntityRef> one = [new EntityRef(EntityKind.Track, track.Slot)];
        Playback.PlayRows(one, 0, track.Id);
    }

    /// <summary>Turn the one video surface on before a video play, so the reducer loads the video host (G-141's
    /// placement edge is B7's). Availability first: a stale None would resolve the request to nothing.</summary>
    static void LightVideo()
    {
        Video.State.FoldAvailability(hasVideo: true);
        if (!Video.State.IsActive) Video.State.OpenAt(Video.SurfacePlacement.Docked);
    }

    // ══ 14. THE --fake MODULE (ch 31: the module route renders offline) ════════════════════════════════════════════

    /// <summary>An in-process module over in-memory pipes — the SAME <see cref="ModuleRunner"/> wire a real module
    /// speaks, so under <c>--fake</c> the catalog, the process state machine, match, resolve, page and the
    /// <c>stream/*</c> bytes all run with no child process and no network. Its playables are generated SILENT MP3, so
    /// the pump decodes real frames into the silent endpoint.</summary>
    public sealed class DemoModule : WaveeModule
    {
        public const string Id = "wavee.demo";
        public const string LinkHost = "wavee-demo.test";
        public const string WatchEntity = "watch:demo", StationEntity = "entity:demo", CustomEntity = "custom:demo",
            MinimalEntity = "minimal:demo", BrokenEntity = "broken:demo", ChannelEntity = "channel:demo";
        const string ChannelName = "Wavee Demo Channel", RepoUrl = "https://github.com/christosk92/WaveeMusic";

        public static readonly ModuleManifest Manifest = new(1, Id, "1.0.0", "Wavee Demo", "wavee", ModuleProtocol.Version,
            "in-process", [ModuleCapabilities.Playback, ModuleCapabilities.Match, Modules.Pages.PagesCapability], [LinkHost],
            new ModuleMenu("Wavee Demo…", "Paste a " + LinkHost + " link"));

        public static InstalledModule Installed => new(Id, Manifest.Version, AppContext.BaseDirectory, Manifest, Bundled: true);

        /// <summary>The route key of one demo page — what a seed row, a test or a deep link navigates to.</summary>
        public static string RouteOf(string entityId) => Modules.Pages.RouteForEntity(Id, entityId)!;

        public static Task<IModuleChannel> SpawnAsync(InstalledModule module, Action<string> stderr, CancellationToken ct)
            => Task.FromResult<IModuleChannel>(new InProcessChannel(new DemoModule()));

        static readonly (string Id, string Title, int Seconds)[] s_playables =
            [("demo-watch", "The Demo Broadcast", 45), ("demo-1", "Quiet Hours", 30), ("demo-2", "Low Tide", 40), ("demo-3", "Night Shift", 35)];

        public override ValueTask<MatchResult?> MatchAsync(string input, CancellationToken ct)
            => new(input.Contains(LinkHost, StringComparison.OrdinalIgnoreCase)
                ? new MatchResult("demo-1", "Quiet Hours", MediaForm.Audio, false, 1.0) : null);

        public override ValueTask<ResolvedPlayable> ResolveAsync(string playableId, CancellationToken ct)
        {
            foreach (var p in s_playables)
                if (p.Id == playableId)
                    return new(new ResolvedPlayable(p.Id, p.Title, [ChannelName], null, SilentMp3.DurationMs(p.Seconds), false,
                        MediaForm.Audio, MediaLocator.FromStream("silence:" + p.Seconds.ToString(CultureInfo.InvariantCulture), "audio/mpeg"),
                        null, [], PageEntityId: p.Id == "demo-watch" ? WatchEntity : StationEntity, SubtitleEntityId: ChannelEntity));
            throw new ModuleException(ModuleErrorCode.Unavailable, "The demo module has no playable '" + playableId + "'.");
        }

        public override ValueTask<IModuleStream?> OpenStreamAsync(string streamId, CancellationToken ct)
            => new(streamId.StartsWith("silence:", StringComparison.Ordinal)
                   && int.TryParse(streamId.AsSpan(8), NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds)
                ? new SilentMp3(seconds) : null);

        public override ValueTask<ModulePageDoc?> GetPageAsync(string entityId, CancellationToken ct) => entityId switch
        {
            WatchEntity => new(new ModulePageDoc(ModulePageDoc.CurrentVersion, ModulePageDoc.TemplateWatch,
                new PageHero("The Demo Broadcast", null, ChannelName, null, "Demo · 1,204 watching", false, SubtitleEntityId: ChannelEntity),
                [PageAction.Play("demo-watch", "Play"), PageAction.OpenUrl(RepoUrl, "Open on GitHub")],
                [
                    PageSection.FromFacts([["Length", "0:45"], ["Published", "2026"]]),
                    PageSection.FromText("An offline broadcast served by the demo module, so the watch layout renders under --fake: the stage, the caption, the chips, this description and the shelf below."),
                    PageSection.FromPlayables([Item("demo-1"), Item("demo-2"), Item("demo-3")], "More from " + ChannelName),
                ], null)),
            StationEntity => new(new ModulePageDoc(ModulePageDoc.CurrentVersion, ModulePageDoc.TemplateEntity,
                new PageHero("Wavee Demo Station", "Station", ChannelName, null, "Ambient · Offline", false, SubtitleEntityId: ChannelEntity),
                [PageAction.Play("demo-1", "Play"), PageAction.OpenUrl(RepoUrl, "Open in browser")],
                [
                    PageSection.FromText("A station page carrying every section kind the entity template draws."),
                    PageSection.FromFacts([["Genre", "Ambient"], ["Bitrate", "128 kbps"], ["Country", "Nowhere"]], "About"),
                    PageSection.FromPlayables([Item("demo-1"), Item("demo-2"), Item("demo-3")], "Recently played"),
                    PageSection.FromCards([Card(ChannelEntity, ChannelName), Card(WatchEntity, "The Demo Broadcast")], "Explore"),
                    PageSection.FromLinks([new PageItem("Wavee on GitHub", "github.com", null, null, null, RepoUrl, null, false, null)], "Links"),
                ], null)),
            ChannelEntity => new(new ModulePageDoc(ModulePageDoc.CurrentVersion, ModulePageDoc.TemplateEntity,
                new PageHero(ChannelName, "Channel", null, null, "4 uploads", false),
                [PageAction.Play("demo-watch", "Play latest")],
                [PageSection.FromCards([Card(WatchEntity, "The Demo Broadcast"), Card(StationEntity, "Wavee Demo Station")], "Uploads")], null)),
            CustomEntity => new(new ModulePageDoc(ModulePageDoc.CurrentVersion, ModulePageDoc.TemplateCustom, null, [],
                [PageSection.FromText("A custom page: sections only, no hero."), PageSection.FromPlayables([Item("demo-2")])], null)),
            MinimalEntity => new(new ModulePageDoc(ModulePageDoc.CurrentVersion, ModulePageDoc.TemplateEntity, null, [], [], null)),
            BrokenEntity => throw new ModuleException(ModuleErrorCode.Unavailable, "The demo page is broken on purpose."),
            _ => new((ModulePageDoc?)null),
        };

        static PageItem Item(string playableId)
        {
            foreach (var p in s_playables)
                if (p.Id == playableId)
                    return new PageItem(p.Title, ChannelName, null, p.Id, null, null, MediaForm.Audio, false,
                        "0:" + p.Seconds.ToString("00", CultureInfo.InvariantCulture));
            return new PageItem(playableId, null, null, playableId, null, null, null, false, null);
        }

        static PageItem Card(string entityId, string title) => new(title, null, null, null, entityId, null, null, false, null);
    }

    /// <summary>A generated silent MPEG-1 Layer III body: 128 kbit/s, 44.1 kHz, mono, 417-byte frames, each a header
    /// (<c>FF FB 90 C0</c>) over zeroed side info — no Huffman data, so every granule decodes to silence. Computed by
    /// offset: nothing is allocated per read.</summary>
    public sealed class SilentMp3(int seconds) : IModuleStream
    {
        public const int FrameBytes = 417, SamplesPerFrame = 1152, SampleRate = 44100;

        readonly long _frames = FramesFor(seconds);

        static long FramesFor(int seconds) => (long)Math.Ceiling(Math.Max(1, seconds) * (double)SampleRate / SamplesPerFrame);

        public static long DurationMs(int seconds) => FramesFor(seconds) * SamplesPerFrame * 1000L / SampleRate;

        public long? Length => _frames * FrameBytes;
        public bool Seekable => true;
        public string? ContentType => "audio/mpeg";

        public ValueTask<int> ReadAsync(long offset, Memory<byte> dst, CancellationToken ct)
        {
            long length = _frames * FrameBytes;
            if (offset < 0 || offset >= length || dst.IsEmpty) return new(0);
            int n = (int)Math.Min(dst.Length, length - offset);
            Fill(offset, dst.Span[..n]);
            return new(n);
        }

        /// <summary>PURE: the bytes at <paramref name="offset"/>.</summary>
        public static void Fill(long offset, Span<byte> span)
        {
            span.Clear();
            for (int i = 0; i < span.Length; i++)
            {
                int r = (int)((offset + i) % FrameBytes);
                if (r > 3) { i += FrameBytes - r - 1; continue; }     // jump to the next frame's header
                span[i] = r switch { 0 => 0xFF, 1 => 0xFB, 2 => 0x90, _ => 0xC0 };
            }
        }

        public void Dispose() { }
    }

    /// <summary>An <see cref="IModuleChannel"/> whose "process" is a <see cref="ModuleRunner"/> loop on the pool,
    /// connected by two in-memory pipes. The loop ending closes the module→host pipe, so the host sees a crash exactly
    /// as it would a dead child's broken stdout.</summary>
    public sealed class InProcessChannel : IModuleChannel
    {
        readonly MemoryPipe _hostToModule = new(), _moduleToHost = new();
        readonly CancellationTokenSource _cts = new();
        readonly Task<int> _loop;

        public InProcessChannel(WaveeModule module)
        {
            Connection = new JsonRpcConnection(_moduleToHost, _hostToModule);
            CancellationToken token = _cts.Token;
            MemoryPipe toHost = _moduleToHost;
            _loop = Task.Run(() => ModuleRunner.RunAsync(module, _hostToModule, _moduleToHost, token), CancellationToken.None);
            _ = _loop.ContinueWith(_ => toHost.Complete(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        public JsonRpcConnection Connection { get; }
        public int ProcessId => Environment.ProcessId;
        public bool HasExited => _loop.IsCompleted;

        public void Kill()
        {
            try { _cts.Cancel(); } catch { }
            _hostToModule.Complete();
            _moduleToHost.Complete();
        }

        public async Task<bool> WaitForExitAsync(TimeSpan grace)
        {
            try { await _loop.WaitAsync(grace).ConfigureAwait(false); return true; }
            catch { return _loop.IsCompleted; }
        }

        public async ValueTask DisposeAsync()
        {
            Kill();
            await Connection.DisposeAsync().ConfigureAwait(false);
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        }
    }

    /// <summary>A one-way in-memory byte pipe over an unbounded channel of chunks: the writer never blocks, the reader
    /// waits, and <see cref="Complete"/> is a clean end of stream.</summary>
    public sealed class MemoryPipe : Stream
    {
        readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
        byte[]? _current;
        int _position;

        public void Complete() => _chunks.Writer.TryComplete();

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer) { if (!buffer.IsEmpty) _chunks.Writer.TryWrite(buffer.ToArray()); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            while (true)
            {
                if (_current is not null && _position < _current.Length)
                {
                    int n = Math.Min(buffer.Length, _current.Length - _position);
                    _current.AsMemory(_position, n).CopyTo(buffer);
                    _position += n;
                    return n;
                }
                _current = null;
                _position = 0;
                if (!await _chunks.Reader.WaitToReadAsync(ct).ConfigureAwait(false)) return 0;
                if (_chunks.Reader.TryRead(out byte[]? next)) _current = next;
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
