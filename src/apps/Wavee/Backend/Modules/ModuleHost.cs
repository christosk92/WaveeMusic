using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.MediaSources;
using Wavee.Core;
using Wavee.Sdk;
using Wavee.Sdk.Protocol;

namespace Wavee.Backend.Modules;

// ── THE MODULE HOST — one per app, built PRE-LOGIN, disposed with Services ───────────────────────────────────────────
// It owns the catalog, one lazy child process per installed module, the resolve cache and the permission-gated host
// services. Nothing in it needs a session, a network or credentials, which is exactly why it is composed in
// `Services` and not in the live bootstrap: pasting a YouTube link must work before anyone has signed in.
//
// Reference-stable for the app lifetime (props-freeze safe) and the ONE place that knows a module exists — the UI reads
// `Installed`, the routing table reads `Providers`, and nothing between play-intent and a host names a module type.

/// <summary>What a pasted link resolved to: the module, its match, its resolve answer, and the queueable Track.</summary>
/// <param name="Module">The module that claimed the input.</param>
/// <param name="Match">Its <c>playback/match</c> answer.</param>
/// <param name="Resolved">Its <c>playback/resolve</c> answer (so form / isLive / title are known before play).</param>
/// <param name="Track">The synthetic Track the queue carries.</param>
public sealed record ModuleMatch(InstalledModule Module, MatchResult Match, ResolvedPlayable Resolved, Track Track);

/// <summary>The app-side host for every installed playback module.</summary>
public sealed class ModuleHost : IDisposable, IModuleHostSink
{
    readonly ModuleCatalog _catalog;
    readonly WaveeLogger _log;
    readonly Dictionary<string, ModuleProcess> _processes = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, InstalledModule> _byId = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, Task<ResolvedPlayable>> _inFlightResolves = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, Task<ModulePageDoc>> _inFlightPages = new(StringComparer.Ordinal);
    readonly ModuleSpawn? _spawn;
    readonly Func<ResolvePreferences> _prefs;
    readonly IPlayableMediaProvider[] _providers;
    readonly Timer? _idleTimer;
    readonly string _hostVersion;
    readonly string _locale;
    int _disposed;

    /// <summary>Compose the host. Nothing is launched until a match/resolve needs a module.</summary>
    /// <param name="catalog">The discovered modules.</param>
    /// <param name="log">The app log; each module's own lines use the category <c>module.&lt;id&gt;</c>.</param>
    public ModuleHost(ModuleCatalog catalog, WaveeLogger log)
        : this(catalog, log, null, null, null, null, null, startIdleTimer: true) { }

    /// <summary>Compose the host with the seams the tests and the composition root need.</summary>
    /// <param name="catalog">The discovered modules.</param>
    /// <param name="log">The app log.</param>
    /// <param name="prefs">The app's current playback preferences (quality / metered / crossfade).</param>
    /// <param name="services">The permission-gated host services; null builds an empty registry.</param>
    /// <param name="spawn">The transport factory; null spawns real child processes.</param>
    /// <param name="hostVersion">The app's informational version.</param>
    /// <param name="locale">The app's BCP-47 locale.</param>
    /// <param name="startIdleTimer">False in tests, where the idle sweep is driven by hand.</param>
    /// <param name="nowUnixMs">Clock seam for cache expiry (tests).</param>
    public ModuleHost(ModuleCatalog catalog, WaveeLogger log, Func<ResolvePreferences>? prefs,
        ModuleHostServices? services, ModuleSpawn? spawn, string? hostVersion, string? locale,
        bool startIdleTimer = true, Func<long>? nowUnixMs = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
        _log = log;
        _spawn = spawn;
        _prefs = prefs ?? (() => ResolvePreferences.Default);
        _hostVersion = hostVersion ?? "0.0.0";
        _locale = locale ?? "en-US";
        Services = services ?? new ModuleHostServices();
        Playables = new ModulePlayableCache(nowUnixMs);
        Pages = new ModulePageCache(nowUnixMs);

        var providers = new List<IPlayableMediaProvider>(catalog.Modules.Count);
        foreach (InstalledModule m in catalog.Modules)
        {
            _byId[m.Id] = m;
            providers.Add(new ModuleMediaProvider(this, m));
        }

        _providers = providers.ToArray();
        Services.Changed += ReinstallServices;

        if (startIdleTimer && _providers.Length > 0)
            _idleTimer = new Timer(_ => _ = SweepIdleAsync(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>The process-wide host, for the few seams that cannot be handed one (the audio host's module-stream
    /// branch resolves a module id out of the playable uri and needs the process behind it). Set by the composition
    /// root through <see cref="Attach"/>; null in a build with no modules.</summary>
    public static ModuleHost? Current { get; private set; }

    /// <summary>Publish this host as <see cref="Current"/> and attach its cache to <see cref="ModulePlayables"/>.
    /// Idempotent; passing null detaches both.</summary>
    /// <param name="host">The host to publish, or null.</param>
    public static void Attach(ModuleHost? host)
    {
        Current = host;
        ModulePlayables.Attach(host?.Playables);
        ModulePages.Attach(host?.Pages);
    }

    /// <summary>The modules that were discovered and accepted.</summary>
    public IReadOnlyList<InstalledModule> Installed => _catalog.Modules;

    /// <summary>One <see cref="ModuleMediaProvider"/> per installed module, in catalog order.</summary>
    public IReadOnlyList<IPlayableMediaProvider> Providers => _providers;

    /// <summary>The catalog this host was built from (roots + rejection reasons for the diagnostics page).</summary>
    public ModuleCatalog Catalog => _catalog;

    /// <summary>The resolve cache — the sync, allocation-free has-video / is-live / locator answers.</summary>
    public ModulePlayableCache Playables { get; }

    /// <summary>The page-document cache — the sync answers behind <see cref="ModulePages"/>.</summary>
    public ModulePageCache Pages { get; }

    /// <summary>The permission-gated module→host services.</summary>
    public ModuleHostServices Services { get; }

    /// <summary>A module pushed a live "now playing" correction. Argument: the playable URI and the update.</summary>
    public event Action<string, MetadataUpdate>? MetadataChanged;

    /// <summary>A module's locator expired. Argument: the playable URI whose cache entry was just dropped.</summary>
    public event Action<string>? PlayableExpired;

    /// <summary>A module's status card changed (drives the generic setup card). Argument: module id and status.</summary>
    public event Action<string, ModuleStatus>? StatusChanged;

    // ── match ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// "Who can play this link?" — trim, prefilter by <c>urlPatterns</c>, ask each candidate's <c>playback/match</c>
    /// in order, and take the FIRST answer. The winner is then resolved immediately, so the caller knows the form
    /// (audio vs video), the live-ness and the real title before it starts playing anything.
    /// </summary>
    /// <param name="input">The pasted text.</param>
    /// <param name="pinnedModuleId">The module the user picked in the Play ▸ menu, or null for "whoever claims it".</param>
    /// <param name="ct">Cancels the walk.</param>
    /// <returns>The winning module + its answers, or null when nothing claimed the input.</returns>
    public async Task<ModuleMatch?> MatchAsync(string input, string? pinnedModuleId, CancellationToken ct)
    {
        string text = (input ?? "").Trim();
        if (text.Length == 0) return null;

        IReadOnlyList<InstalledModule> candidates = ModuleRouter.Prefilter(Installed, text, pinnedModuleId);
        for (int i = 0; i < candidates.Count; i++)
        {
            InstalledModule module = candidates[i];
            MatchResult? match;
            try
            {
                match = await Process(module).RequestAsync(ModuleMethods.Match, new MatchParams(text),
                    SdkJsonContext.Default.MatchParams, SdkJsonContext.Default.MatchResult,
                    ModuleTimeouts.Match, ct).ConfigureAwait(false);
            }
            catch (ModuleException ex) when (ex.Code is ModuleErrorCode.NotOwned or ModuleErrorCode.Unsupported)
            {
                continue;   // "not mine" / "no match capability" — keep walking, never a failure
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.Info("module " + module.Id + " match failed: " + ex.Message);
                continue;
            }

            if (match is not { PlayableId.Length: > 0 }) continue;

            string uri = ModuleUri.Encode(module.Id, match.PlayableId);
            ResolvedPlayable resolved = await ResolveAsync(uri, force: false, ct).ConfigureAwait(false);
            Track track = LocalPlayables.ForModule(module.Id, match.PlayableId,
                string.IsNullOrWhiteSpace(resolved.Title) ? match.Title : resolved.Title,
                resolved.Form, resolved.Artists, resolved.ArtworkUrl);
            return new ModuleMatch(module, match, resolved, track);
        }

        return null;
    }

    // ── resolve ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolve one module playable. Cached until the module's own <c>expiresAtUnixMs</c>; concurrent resolves of the
    /// SAME uri share one in-flight task (a track row clicked twice must not spawn two upstream lookups).
    /// </summary>
    /// <param name="playableUri">The <c>wavee:module:</c> uri.</param>
    /// <param name="force">True to ignore the cache (an expired locator, a NETWORK failure on the video path).</param>
    /// <param name="ct">Cancels the resolve.</param>
    public Task<ResolvedPlayable> ResolveAsync(string playableUri, bool force, CancellationToken ct)
    {
        if (!ModuleUri.TryDecode(playableUri, out string moduleId, out string playableId))
            throw new ModuleException(ModuleErrorCode.NotOwned, "not a module playable uri: " + playableUri);

        if (force) Playables.Invalidate(playableUri);
        else if (Playables.Get(playableUri) is { } cached) return Task.FromResult(cached);

        // One in-flight resolve per uri: the loser of a race awaits the winner's task instead of starting a second
        // upstream lookup. The entry is dropped by a continuation attached AFTER GetOrAdd — never from inside the
        // resolve itself, which for a synchronously-faulting resolve would run before the entry existed and leave the
        // failed task cached forever. The winner's cancellation token is the one that governs; a second caller that
        // cancels is only abandoning its own await.
        Task<ResolvedPlayable> task = _inFlightResolves.GetOrAdd(playableUri,
            static (uri, state) => state.Host.ResolveCoreAsync(uri, state.ModuleId, state.PlayableId, state.Ct),
            (Host: this, ModuleId: moduleId, PlayableId: playableId, Ct: ct));
        string key = playableUri;
        _ = task.ContinueWith(_ => { _inFlightResolves.TryRemove(key, out Task<ResolvedPlayable>? _); },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    async Task<ResolvedPlayable> ResolveCoreAsync(string playableUri, string moduleId, string playableId, CancellationToken ct)
    {
        if (!_byId.TryGetValue(moduleId, out InstalledModule? module))
            throw new ModuleException(ModuleErrorCode.NotOwned, "no module named '" + moduleId + "' is installed");

        ResolvedPlayable resolved = await Process(module).RequestAsync(ModuleMethods.Resolve,
            new ResolveParams(playableId, _prefs()),
            SdkJsonContext.Default.ResolveParams, SdkJsonContext.Default.ResolvedPlayable,
            ModuleTimeouts.Resolve, ct).ConfigureAwait(false);
        Playables.Put(playableUri, resolved);
        return resolved;
    }

    // ── pages ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>How long a module gets to answer <c>module/page</c>. Longer than a match (a page is a second upstream
    /// fetch) and shorter than a resolve (nothing is playing yet — a page that takes this long is a failure the user
    /// should see as one).</summary>
    public static readonly TimeSpan PageTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Fetch the DECLARATIVE page document a module publishes for one of its entities. Cached until the document's own
    /// <c>ExpiresAtUnixMs</c> (10 minutes when it states none); concurrent fetches of the SAME uri share one in-flight
    /// task, exactly like <see cref="ResolveAsync"/> — a page opened from the art tile and from the subtitle in the
    /// same breath must not become two upstream lookups.
    /// </summary>
    /// <param name="moduleUri">The <c>wavee:module:&lt;id&gt;:&lt;b64(entityId)&gt;</c> page uri.</param>
    /// <param name="ct">Cancels the fetch.</param>
    /// <exception cref="ModuleException">The uri is not a module uri, the module is not installed, or the module
    /// refused — including <c>-32601</c>, which the process layer maps to "capability absent"
    /// (<see cref="ModuleErrorCode.Unsupported"/>) rather than to a failure.</exception>
    public Task<ModulePageDoc> PageAsync(string moduleUri, CancellationToken ct)
    {
        if (!ModuleUri.TryDecode(moduleUri, out string moduleId, out string entityId))
            throw new ModuleException(ModuleErrorCode.NotOwned, "not a module uri: " + moduleUri);

        if (Pages.Get(moduleUri) is { } cached) return Task.FromResult(cached);

        Task<ModulePageDoc> task = _inFlightPages.GetOrAdd(moduleUri,
            static (uri, state) => state.Host.PageCoreAsync(uri, state.ModuleId, state.EntityId, state.Ct),
            (Host: this, ModuleId: moduleId, EntityId: entityId, Ct: ct));
        string key = moduleUri;
        _ = task.ContinueWith(_ => { _inFlightPages.TryRemove(key, out Task<ModulePageDoc>? _); },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    async Task<ModulePageDoc> PageCoreAsync(string moduleUri, string moduleId, string entityId, CancellationToken ct)
    {
        if (!_byId.TryGetValue(moduleId, out InstalledModule? module))
            throw new ModuleException(ModuleErrorCode.NotOwned, "no module named '" + moduleId + "' is installed");

        // Capabilities are DECLARED, never probed: a module that did not claim `pages` is not spawned to be told
        // -32601. (A module that claims it and then answers -32601 still surfaces as Unsupported from the process
        // layer, so both halves of "no pages here" are the same typed refusal.)
        if (!ModuleCapabilities.Declares(module.Manifest, ModulePages.PagesCapability))
            throw new ModuleException(ModuleErrorCode.Unsupported,
                "module '" + moduleId + "' does not provide pages");

        ModulePageDoc doc = await Process(module).RequestAsync(ModuleMethods.Page, new PageParams(entityId),
            SdkJsonContext.Default.PageParams, SdkJsonContext.Default.ModulePageDoc,
            PageTimeout, ct).ConfigureAwait(false);
        // The budget is the module's contract, but the app re-checks it: a page arrives over a pipe from a full-trust
        // child process, and "the SDK validated it" is only true for a module that used the SDK.
        ModulePageBudget.Validate(doc);
        Pages.Put(moduleUri, doc);
        return doc;
    }

    /// <summary>Best-effort pre-warm of an upcoming playable (<c>playback/warm</c>). Never throws, never starts a
    /// module that is faulted, and never blocks the caller.</summary>
    /// <param name="playableUri">The <c>wavee:module:</c> uri.</param>
    /// <param name="reason">Why, for the log.</param>
    public void Warm(string playableUri, string reason = "")
    {
        if (!ModuleUri.TryDecode(playableUri, out string moduleId, out string playableId)) return;
        if (!_byId.TryGetValue(moduleId, out InstalledModule? module)) return;
        ModuleProcess process = Process(module);
        if (process.State is ModuleProcessState.Faulted) return;
        _ = WarmCoreAsync(process, playableId, reason);
    }

    async Task WarmCoreAsync(ModuleProcess process, string playableId, string reason)
    {
        try
        {
            await process.RequestAsync(ModuleMethods.Warm, new WarmParams(playableId),
                SdkJsonContext.Default.WarmParams, SdkJsonContext.Default.RpcUnit,
                ModuleTimeouts.Match, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Info("module warm (" + reason + ") failed: " + ex.Message);
        }
    }

    // ── processes ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The (lazily created) process wrapper for one module.</summary>
    /// <param name="module">The module.</param>
    public ModuleProcess Process(InstalledModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        lock (_processes)
        {
            if (_processes.TryGetValue(module.Id, out ModuleProcess? existing)) return existing;
            var created = new ModuleProcess(module, this, _log.With("module." + module.Id),
                _spawn, _hostVersion, _locale, _prefs);
            _processes[module.Id] = created;
            return created;
        }
    }

    /// <summary>The process wrapper for a module id, or null when it is not installed.</summary>
    /// <param name="moduleId">The module id.</param>
    public ModuleProcess? ProcessFor(string? moduleId)
        => moduleId is { Length: > 0 } && _byId.TryGetValue(moduleId, out InstalledModule? m) ? Process(m) : null;

    /// <summary>Every process wrapper that has been created so far (the diagnostics page's rows).</summary>
    public IReadOnlyList<ModuleProcess> ActiveProcesses
    {
        get
        {
            lock (_processes)
            {
                var list = new ModuleProcess[_processes.Count];
                _processes.Values.CopyTo(list, 0);
                return list;
            }
        }
    }

    /// <summary>Clear a module's <see cref="ModuleProcessState.Faulted"/> latch — the "Retry" button.</summary>
    /// <param name="moduleId">The module id.</param>
    public void Retry(string moduleId) => ProcessFor(moduleId)?.Retry();

    /// <summary>Run the idle sweep once (the timer's body; called directly by the tests).</summary>
    public async Task SweepIdleAsync()
    {
        foreach (ModuleProcess p in ActiveProcesses)
        {
            try { await p.IdleSweepAsync().ConfigureAwait(false); }
            catch (Exception ex) { _log.Info("idle sweep for " + p.Module.Id + " failed: " + ex.Message); }
        }
    }

    void ReinstallServices()
    {
        // A go-live install adds host services after modules may already be running; re-registering on the live
        // connection replaces the handler in place, so a module that is already up gains the new service immediately.
        foreach (ModuleProcess p in ActiveProcesses)
        {
            if (p.State != ModuleProcessState.Ready) continue;
            try { p.NotifyServicesChanged(Services); }
            catch (Exception ex) { _log.Info("re-installing host services on " + p.Module.Id + " failed: " + ex.Message); }
        }
    }

    // ── IModuleHostSink — the module→host traffic ───────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void OnMetadata(string moduleId, MetadataUpdate update)
    {
        if (update is not { PlayableId.Length: > 0 }) return;
        string uri = ModuleUri.Encode(moduleId, update.PlayableId);
        // Fold the correction into the cache so a later has-video / title read sees it too.
        if (Playables.Get(uri) is { } cached)
        {
            Playables.Put(uri, cached with
            {
                Title = update.Title ?? cached.Title,
                Artists = update.Artists ?? cached.Artists,
                ArtworkUrl = update.ArtworkUrl ?? cached.ArtworkUrl,
            });
        }

        MetadataChanged?.Invoke(uri, update);
    }

    /// <inheritdoc/>
    public void OnExpired(string moduleId, string playableId)
    {
        if (string.IsNullOrEmpty(playableId)) return;
        string uri = ModuleUri.Encode(moduleId, playableId);
        Playables.Invalidate(uri);
        PlayableExpired?.Invoke(uri);
    }

    /// <inheritdoc/>
    public void OnStatus(string moduleId, ModuleStatus status) => StatusChanged?.Invoke(moduleId, status);

    /// <inheritdoc/>
    public void OnProgress(string moduleId, ProgressNotification progress)
        => _log.With("module." + moduleId).Info(progress.Stage + " "
            + progress.Percent.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%");

    /// <inheritdoc/>
    public void OnLog(string moduleId, LogNotification line)
    {
        var log = _log.With("module." + moduleId);
        switch (line.Level)
        {
            case ModuleLogLevel.Error: log.Error(line.Message); break;
            case ModuleLogLevel.Warn: log.Warn(line.Message); break;
            case ModuleLogLevel.Debug: log.Debug(line.Message); break;
            default: log.Info(line.Message); break;
        }
    }

    /// <inheritdoc/>
    public void RegisterHostServices(InstalledModule module, JsonRpcConnection connection)
        => Services.Install(module, connection);

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Services.Changed -= ReinstallServices;
        _idleTimer?.Dispose();
        if (ReferenceEquals(Current, this)) Attach(null);
        foreach (ModuleProcess p in ActiveProcesses)
        {
            try { p.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            catch { /* teardown is best-effort */ }
        }

        lock (_processes) _processes.Clear();
    }
}
