// ── Entities/Home.Host.cs ──────────────────────────────────────────────────────────────────────────────────────────
// home-layout.json off the UI thread, the preferences that own it, THE install method for Home / Search / Browse /
// Recents, and the feed host (what's new + gander → the notification centre; userTopContent → the podium; the daylist
// rollover note). There is no other SHELL file for Home
//
// Role: SHELL
// Owner: P
// Wave: 5
// Budget: 400 lines (ch 12's store) — UNVERIFIED past it: the install method and the feed host (contract §2.5, §7)
//   landed here too, reported
// Spec: ch 10 §9's settled seven-file Home table (ch 12's 400: the store, its faults, the .bak recovery); ch 10 §7 (the
//   chrome feeds), ch 11 §7 DATA GAPS (top content), ch 19 §7 (the merge); WP-5.P contract §2.5 and §7; gaps G-088,
//   G-089, G-045 (the P halves); 0.2.9 `Features/Home/Persistence/HomeLayoutStore.cs`, `Features/Home/HomePreferences.cs`,
//   `SpotifyLive/SpotifyWhatsNewService.cs`, `SpotifyLive/SpotifyUserTopService.cs`
//
// THE STORE IS 0.2.9's, VERBATIM IN BEHAVIOUR: load / validate / fault-classify, an atomic write through a .tmp and a
// File.Replace that rotates ONE .bak, a sequence number so only the newest snapshot is written, and a load fault that
// BLOCKS every write until the user sets the file aside (preserve-don't-destroy, the sidebar's locked decision 8). Its
// folder is 0.2.9's own — `WaveeMusic\home-layout.json` under `Platform.LocalFolder`, beside history.json and
// sidebar-layout.json (D8) — and `PathUnder` is the pure half, so a test never creates the real profile folder.
//
// THE FEED HOST WATCHES WITHOUT A COMPONENT (the `Spotify.Library` precedent): a private `ReactiveRuntime` whose effects
// read the session phase, the scope generation, the update observation and the Home / playlist tables, and only POST
// their work. Network runs on the api workers (`Spotify.Api.Run`, bounded — a full queue is a failure the host answers
// for); every table write and every signal write lands on the UI thread.
//
// NOTHING HERE DECIDES WHAT A ROW IS: the folds are `Spotify.Decode.WhatsNew / Notifications / UserTop` (CORE, tested),
// the merge is `Notify.Rebuild`, the update row is `Update.FeedRow`, the reducer is `HomeLayoutReducer`.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using FluentGpu.Signals;
using FluentGpu.WindowsApi.Power;

namespace Wavee;

// ══ 1. THE LAYOUT STORE (0.2.9 Persistence/HomeLayoutStore.cs) ═══════════════════════════════════════════════════════

/// <summary>How a <see cref="HomeLayoutStore.Load"/> ended. Anything but <see cref="None"/> means: load the default IN
/// MEMORY, leave the file untouched, and suppress every write until the user discards it.</summary>
public enum HomeLayoutLoadFault : byte { None = 0, Corrupt = 1, TooNew = 2, Unreadable = 3 }

public readonly record struct HomeLayoutLoad(HomeLayoutDocDto? Doc, HomeLayoutLoadFault Fault, string? Detail);

public enum HomeLayoutSaveFault : byte { None = 0, DocumentTooLarge = 1, IoFailure = 2 }

/// <summary>home-layout.json: load / validate / fault-classify + an atomic write with one .bak.</summary>
public sealed class HomeLayoutStore
{
    public const int CurrentVersion = 1;
    public const int MaxDocumentBytes = 256 * 1024;

    readonly string _path;
    readonly object _writeGate = new();
    long _seq;
    volatile bool _writesBlocked;
    volatile HomeLayoutSaveFault _saveFault;
    Task? _pending;

    public HomeLayoutStore(string path) => _path = path;

    public static HomeLayoutStore ForApp() => new(DefaultPath());

    /// <summary><c>…\WaveeMusic\home-layout.json</c> under <see cref="Platform.LocalFolder"/> — BESIDE sidebar-layout.json
    /// (D8). Reading <c>LocalFolder</c> creates the profile folder, which is why a test uses <see cref="PathUnder"/>.</summary>
    public static string DefaultPath() => PathUnder(Platform.LocalFolder);

    /// <summary>The document's path under a profile root — the pure half of <see cref="DefaultPath"/>.</summary>
    public static string PathUnder(string profileRoot) => Path.Combine(profileRoot, "WaveeMusic", "home-layout.json");

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

        // Malformed / null / version <= 0 → the rotated backup, under the SAME validation. A good backup is a full
        // recovery: writes stay enabled and the next commit rewrites the primary.
        if (File.Exists(BakPath) && TryRead(BakPath, out var bak, out _) == ReadOutcome.Ok)
        {
            Log.Warn("home", "home.layout.recovered recovery=backup: the Home layout was recovered from its backup.");
            return new HomeLayoutLoad(bak, HomeLayoutLoadFault.None, "recovered from .bak");
        }

        _writesBlocked = true;
        LogLoadFailed("corrupt");
        return new HomeLayoutLoad(null, HomeLayoutLoadFault.Corrupt, primaryDetail);
    }

    enum ReadOutcome : byte { Ok, Malformed, TooNew, Unreadable }

    static ReadOutcome TryRead(string path, out HomeLayoutDocDto? doc, out string? detail)
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

    static void LogLoadFailed(string fault)
        => Log.Warn("home", "home.layout.load_failed fault=" + fault + ": the Home layout could not be loaded; the saved file was preserved.");

    /// <summary>Stamp the snapshot and write it on the pool. A newer commit supersedes an older one that has not written
    /// yet (the sequence number); nothing is written while a load fault blocks writes.</summary>
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
                    Log.Warn("home", "home.layout.save_failed fault=document_too_large bytes=" + bytes.Length.ToString(CultureInfo.InvariantCulture));
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
                Log.Warn("home", "home.layout.save_failed fault=io_failure exception_type=" + ex.GetType().Name
                    + " elapsed_ms=" + watch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture));
                try { if (File.Exists(TmpPath)) File.Delete(TmpPath); } catch (Exception) { }
            }
        }
    }

    /// <summary>Block until the newest pending write finished, or the timeout. True when nothing is pending.</summary>
    public bool WaitForWrites(int timeoutMs = 5000)
    {
        Task? t;
        lock (_writeGate) t = _pending;
        if (t is null) return true;
        try { return t.Wait(timeoutMs); }
        catch (Exception) { return false; }
    }

    /// <summary>Set the unreadable file aside (.corrupt), drop the backup and the temp, and UNBLOCK writes — only when the
    /// moves succeeded. A failed move keeps writes blocked and reports an IO fault.</summary>
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
                Log.Warn("home", "home.layout.discard_failed exception_type=" + ex.GetType().Name
                    + ": the unreadable Home layout could not be set aside.");
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

// ══ 2. THE PREFERENCES (0.2.9 HomePreferences.cs) ═══════════════════════════════════════════════════════════════════

/// <summary>The single owner of the Home layout document. Load is fail-soft: a corrupt file is never overwritten until
/// the first successful save after <see cref="DiscardCorrupt"/>. UI thread.</summary>
public sealed class HomePreferences
{
    static HomePreferences? s_current;

    /// <summary>THE app instance, over <see cref="HomeLayoutStore.DefaultPath"/>, created on first read. 0.2.9 provided it
    /// through a context slot; 0.3's pages (the landing, the customizer) read this.</summary>
    public static HomePreferences Current => s_current ??= new HomePreferences(HomeLayoutStore.ForApp());

    readonly HomeLayoutStore _store;
    readonly Signal<int> _layoutVersion = new(0);

    HomeLayoutDoc _layout;
    HomeLayoutWireCarry _carry = HomeLayoutWireCarry.Empty;
    bool _loaded;

    public HomePreferences(HomeLayoutStore store)
    {
        _store = store;
        _layout = HomeLayoutDoc.Default;
        LoadDocument();
    }

    public HomeLayoutDoc Layout => _layout;
    /// <summary>Bumps on every accepted change — the landing and the customizer subscribe to it.</summary>
    public IReadSignal<int> LayoutVersion => _layoutVersion;
    public HomeLayoutLoadFault Fault { get; private set; }
    public string? FaultDetail { get; private set; }
    public bool WritesBlocked => _store.WritesBlocked;
    public string FilePath => _store.FilePath;

    /// <summary>THE mutation entry point: reduce; when it changed, publish and autosave. Returns why it did not.</summary>
    public HomeLayoutRejectReason Dispatch(HomeLayoutCommand command)
    {
        var result = HomeLayoutReducer.Apply(_layout, command);
        if (!result.Changed) return result.Reason;

        _layout = result.Layout;
        _layoutVersion.Value = _layoutVersion.Peek() + 1;
        Commit();
        return HomeLayoutRejectReason.None;
    }

    /// <summary>"Start fresh": set the file aside, load the default, publish, save.</summary>
    public void DiscardCorrupt()
    {
        _store.DiscardCorrupt();
        _layout = HomeLayoutDoc.Default;
        _carry = HomeLayoutWireCarry.Empty;
        Fault = HomeLayoutLoadFault.None;
        FaultDetail = null;
        _layoutVersion.Value = _layoutVersion.Peek() + 1;
        _loaded = true;
        Commit();
    }

    public bool WaitForWrites(int timeoutMs = 5000) => _store.WaitForWrites(timeoutMs);

    void LoadDocument()
    {
        var load = _store.Load();
        Fault = load.Fault;
        FaultDetail = load.Detail;
        if (load.Doc is { } dto)
        {
            var read = HomeLayoutWire.Read(dto);
            _layout = read.Layout;
            _carry = read.Carry;
            _carry.CaptureDoc(dto);
        }
        else
        {
            _layout = HomeLayoutDoc.Default;
            _carry = HomeLayoutWireCarry.Empty;
        }
        _loaded = true;
    }

    void Commit()
    {
        if (!_loaded) return;
        var snapshot = HomeLayoutWire.Write(_layout, _carry);
        _carry.ReattachDoc(snapshot);
        _store.Commit(snapshot);
    }
}

// ══ 3. THE INSTALL METHOD AND THE FEED HOST ══════════════════════════════════════════════════════════════════════════

public readonly partial struct Home
{
    /// <summary>THE install method (contract §7): the eight routes owner P renders, the omnibar's suggestion source, and
    /// every notification seam the feed host fills. The composition calls it once, from App.cs, after
    /// <c>Track.InstallActions</c>. UI thread. Assignments: <c>Shell.SetPage</c> × 8, <c>Shell.Omnibar.Source</c>
    /// (live builds only — <c>--fake</c> keeps the navigation-log answer), <c>Notify.RefreshFeeds</c>, and the feed host's
    /// three watches (<see cref="Feeds"/>: the feeds once per online session, the update row merge, the daylist note).</summary>
    public static void InstallPages()
    {
        Shell.SetPage(Shell.RouteKind.Home, LandingPageFor);
        Shell.SetPage(Shell.RouteKind.HomeSection, SectionPageFor);
        Shell.SetPage(Shell.RouteKind.BrowseSection, SectionPageFor);
        Shell.SetPage(Shell.RouteKind.HomeCustomize, CustomizerPageFor);
        Shell.SetPage(Shell.RouteKind.Search, Search.PageFor);
        Shell.SetPage(Shell.RouteKind.Browse, Browse.DirectoryPageFor);
        Shell.SetPage(Shell.RouteKind.BrowseCategory, Browse.CategoryPageFor);
        Shell.SetPage(Shell.RouteKind.Recents, Recents.PageFor);
        if (!Platform.Args.Fake) Shell.Omnibar.Source = Spotify.Api.SuggestAsync;
        Notify.RefreshFeeds = Feeds.Refresh;
        Feeds.Install();
    }

    /// <summary>The feed host (G-088 / G-089 / G-045, the P halves). UI thread unless a member says otherwise.</summary>
    public static class Feeds
    {
        /// <summary>The podium's load verdict: Idle (never asked — offline, <c>--fake</c>), Pending (in flight with nothing
        /// held), Ready, Failed (the last ask failed and nothing is held).</summary>
        public static Signal<HomeLoad> TopContentState { get; } = new(HomeLoad.Idle);

        /// <summary>A real top-content answer holds this long (0.2.9 <c>CacheTtl</c>).</summary>
        public const long TopContentFreshMs = 30 * 60 * 1000;
        /// <summary>A failed one only this long (0.2.9 <c>FailureTtl</c>): one hiccup must not blank the row for 30 min.</summary>
        public const long TopContentRetryMs = 60 * 1000;
        /// <summary>The page size both remote feeds are asked for (0.2.9's what's-new limit).</summary>
        public const int FeedLimit = 50;
        /// <summary>The podium's rank depth (0.2.9 <c>TopArtists</c>).</summary>
        public const int TopLimit = 10;

        // ── the warm-start marks (a KEEP-ALIVE eviction must not force a cold-boot skeleton) ────────────────────────
        //
        // A remounted HomeLandingView loses its page-local Loadable/HomeRevealGate — that is the whole point of keep-
        // alive eviction — but the entity graph does not lose the sections, and a page that already painted this
        // content once THIS SESSION must not re-shimmer it merely because its component instance was thrown away.
        // The mark below is what lets `Home.Page.cs`'s warm-start seed (`InitialHome`/`InitialCharts`) tell that case
        // apart from a genuine cold boot: a disk-warmed row can carry Known sections BEFORE its first live check
        // concludes (`Store.Warm`), and painting THOSE immediately is the "cached grid, then everything jumps"
        // regression `Home.Classify` exists to prevent (ch 10 §7). So this mark is set ONLY from the same place that
        // already cleared Classify's gate once (`HomeLandingView.Publish`, `ChartsTick`) — it remembers a fact, it
        // never substitutes for the check. Scope-keyed (not page-keyed), so it survives eviction and resets clean on
        // a scope change (a new account, a re-auth) — a fresh scope must never inherit a stale mark.

        static Scope? s_revealScope;
        static readonly HashSet<string> s_revealedFacets = new(StringComparer.Ordinal);
        static bool s_chartsRevealed;

        static void EnsureRevealScope()
        {
            var scope = Entities.Current;
            if (ReferenceEquals(scope, s_revealScope)) return;
            s_revealScope = scope;
            s_revealedFacets.Clear();
            s_chartsRevealed = false;
        }

        /// <summary>Has the Home landing painted a REAL verdict for <paramref name="facet"/> at least once in the
        /// current scope's lifetime? ("" = the unfiltered feed, same as <see cref="Home.SelectedFacet"/>.)</summary>
        public static bool HasRevealed(string facet)
        {
            EnsureRevealScope();
            return s_revealedFacets.Contains(facet);
        }

        /// <summary>Record that <paramref name="facet"/> just cleared the reveal gate for real (called once from
        /// <c>HomeLandingView.Publish</c>, never speculatively).</summary>
        public static void MarkRevealed(string facet)
        {
            EnsureRevealScope();
            s_revealedFacets.Add(facet);
        }

        /// <summary>The Charts row's own mark — it has no facet, one flag covers it.</summary>
        public static bool HasChartsRevealed()
        {
            EnsureRevealScope();
            return s_chartsRevealed;
        }

        public static void MarkChartsRevealed()
        {
            EnsureRevealScope();
            s_chartsRevealed = true;
        }

        // ── the watches ─────────────────────────────────────────────────────────────────────────────────────────────

        static ReactiveRuntime? s_runtime;
        static Effect? s_sessionWatch, s_updateWatch, s_daylistWatch;
        static bool s_flushPosted;
        static readonly Action s_flush = static () => { s_flushPosted = false; s_runtime?.Flush(); };
        static readonly Action s_onlineOnce = OnlineOnce;
        static readonly Action s_rebuild = RebuildFromHeld;
        static readonly Action s_noteDaylist = NoteDaylist;

        /// <summary>Start the three watches. Idempotent. No request goes out here: the feeds wait for Online.</summary>
        internal static void Install()
        {
            if (s_runtime is not null) return;
            s_runtime = new ReactiveRuntime { FrameRequested = RequestFlush };
            s_sessionWatch = new Effect(s_runtime, WatchSession);
            s_updateWatch = new Effect(s_runtime, WatchUpdate);
            s_daylistWatch = new Effect(s_runtime, WatchDaylist);
            // Playback.Os holds the PowerSession subscription; a handler attached later is still raised.
            try { PowerSession.Resumed += OnResumed; }
            catch (Exception ex) { Log.Warn("home", "power resume subscribe failed", ex); }
            Log.Info("home", "feed host installed (feeds on Online, update row merge, daylist note + rollover)");
        }

        static void RequestFlush()
        {
            if (s_flushPosted) return;
            s_flushPosted = true;
            Spotify.Post(s_flush);
        }

        static bool IsAccountScope(Scope scope)
            => scope.Key.Provider == "spotify" && scope.MeSlot > Table.None && scope.Key.Account.Length > 0;

        static bool CanAsk(out Scope scope)
        {
            scope = Entities.Current;
            return IsAccountScope(scope) && Spotify.Current.IsOnline;   // a --fake scope is not a spotify account scope
        }

        static uint s_onlineForGeneration = uint.MaxValue, s_onlineForSession = uint.MaxValue;

        /// <summary>Tracked: the phase and the scope generation. Both feeds and the top content, ONCE per (scope
        /// generation, session epoch) that reaches Online. Posts; never works inline.</summary>
        static void WatchSession()
        {
            var phase = Spotify.Status.Value;
            uint generation = Entities.ScopeEpoch.Value;
            if (phase != Spotify.SessionPhase.Online) return;
            uint session = Spotify.Current.Epoch;
            if (generation == s_onlineForGeneration && session == s_onlineForSession) return;
            s_onlineForGeneration = generation;
            s_onlineForSession = session;
            Spotify.Post(s_onlineOnce);
        }

        static void OnlineOnce()
        {
            Refresh();
            EnsureTopContent();
        }

        static AppUpdateState s_seenUpdate = AppUpdateState.None;

        /// <summary>Tracked: the update observation. A state change re-merges the centre with the new pinned row
        /// (G-088: <c>Update.FeedRow</c> had no caller).</summary>
        static void WatchUpdate()
        {
            var snapshot = Notify.Update.Value;
            if (snapshot.State == s_seenUpdate) return;
            s_seenUpdate = snapshot.State;
            Spotify.Post(s_rebuild);
        }

        // ── the remote feeds → the centre (G-088) ────────────────────────────────────────────────────────────────────

        static IReadOnlyList<Notification> s_social = [], s_releases = [];
        static bool s_refreshing, s_refreshAgain;

        /// <summary>Refetch what's new AND gander, then <c>Notify.Rebuild</c> with the update row. Single-flight (a call
        /// while one runs queues exactly one more); assigned to <c>Notify.RefreshFeeds</c>. Offline, <c>--fake</c> or an
        /// accountless scope asks nothing (offline reads Offline on a feed that holds no rows).</summary>
        public static void Refresh()
        {
            if (!CanAsk(out var scope))
            {
                if (IsAccountScope(scope) && !Spotify.Current.IsOnline)
                {
                    if (s_social.Count == 0) Notify.SocialState.Value = Notify.FeedState.Offline;
                    if (s_releases.Count == 0) Notify.ReleasesState.Value = Notify.FeedState.Offline;
                }
                return;
            }
            if (s_refreshing) { s_refreshAgain = true; return; }
            s_refreshing = true;
            if (s_social.Count == 0) Notify.SocialState.Value = Notify.FeedState.Loading;
            if (s_releases.Count == 0) Notify.ReleasesState.Value = Notify.FeedState.Loading;

            uint epoch = scope.Epoch;
            bool queued = Spotify.Api.Run(() =>
            {
                var releases = new List<Notification>(FeedLimit);
                var social = new List<Notification>(FeedLimit);
                bool releasesOk = false, socialOk = false;
                try
                {
                    var answer = Spotify.Api.WhatsNew(0, FeedLimit, onlyUnplayed: false, CancellationToken.None);
                    if (answer.Ok) { Spotify.Decode.WhatsNew(answer.Bytes, releases); releasesOk = true; }
                    else Log.Warn("home", "what's new answered " + answer.Status.ToString(CultureInfo.InvariantCulture));
                }
                catch (Exception ex) { Log.Warn("home", "what's new failed", ex); }
                try
                {
                    var answer = Spotify.Api.Notifications(FeedLimit, CancellationToken.None);
                    if (answer.Ok) { Spotify.Decode.Notifications(answer.Bytes, social); socialOk = true; }
                    else Log.Warn("home", "gander answered " + answer.Status.ToString(CultureInfo.InvariantCulture));
                }
                catch (Exception ex) { Log.Warn("home", "gander failed", ex); }
                Spotify.Post(() => LandFeeds(epoch, releases, releasesOk, social, socialOk));
            });
            if (!queued)
            {
                s_refreshing = false;
                if (s_social.Count == 0) Notify.SocialState.Value = Notify.FeedState.Error;
                if (s_releases.Count == 0) Notify.ReleasesState.Value = Notify.FeedState.Error;
            }
        }

        /// <summary>UI thread: a stale scope's answer is dropped; a failed feed keeps what it held; the text-form subjects
        /// the decoder could not intern are parsed here; then ONE rebuild with the update row.</summary>
        static void LandFeeds(uint epoch, List<Notification> releases, bool releasesOk, List<Notification> social, bool socialOk)
        {
            s_refreshing = false;
            if (Entities.Current.Epoch == epoch)
            {
                if (releasesOk)
                {
                    for (int i = 0; i < releases.Count; i++)
                        if (!releases[i].Subject.IsValid && releases[i].Id.Length > 0)
                            releases[i] = releases[i] with { Subject = EntityUri.Parse(releases[i].Id.AsSpan()) };
                    s_releases = releases;
                    Notify.ReleasesState.Value = releases.Count == 0 ? Notify.FeedState.Empty : Notify.FeedState.Populated;
                }
                else if (s_releases.Count == 0) Notify.ReleasesState.Value = Notify.FeedState.Error;

                if (socialOk)
                {
                    s_social = social;
                    Notify.SocialState.Value = social.Count == 0 ? Notify.FeedState.Empty : Notify.FeedState.Populated;
                }
                else if (s_social.Count == 0) Notify.SocialState.Value = Notify.FeedState.Error;

                RebuildFromHeld();
                Log.Info("home", "feeds landed: releases=" + (releasesOk ? releases.Count.ToString(CultureInfo.InvariantCulture) : "failed")
                    + " social=" + (socialOk ? social.Count.ToString(CultureInfo.InvariantCulture) : "failed"));
            }
            if (s_refreshAgain) { s_refreshAgain = false; Refresh(); }
        }

        /// <summary>The centre from the held remote rows and the CURRENT update row. The local activity journal has no
        /// source yet (ch 19 DATA GAP 3), so its list is empty.</summary>
        static void RebuildFromHeld()
            => Notify.Rebuild(Update.FeedRow(Notify.Update.Peek()), s_social, s_releases, []);

        // ── the account's top content (G-045 P half) ─────────────────────────────────────────────────────────────────

        static Scope? s_topScope;
        static long s_topAskedAtMs = long.MinValue / 2;
        static bool s_topOk, s_topInflight;

        /// <summary><c>userTopContent</c> → <c>Edges.UserTopArtists / UserTopTracks</c> on the account row. A real answer is
        /// fresh for <see cref="TopContentFreshMs"/>, a failure for <see cref="TopContentRetryMs"/>; one request at a time.
        /// Idempotent from a mount effect. Nothing is asked offline / <c>--fake</c> (the seed's relations stand).</summary>
        public static void EnsureTopContent()
        {
            var scope = Entities.Current;
            if (!ReferenceEquals(scope, s_topScope))
            {
                s_topScope = scope;
                s_topAskedAtMs = long.MinValue / 2;
                s_topOk = s_topInflight = false;
                if (TopContentState.Peek() != HomeLoad.Idle) TopContentState.Value = HomeLoad.Idle;
            }
            if (s_topInflight || !CanAsk(out _)) return;
            long now = Playback.FrameNowMs();                        // the app's monotonic clock (D22), never TickCount64
            if (now - s_topAskedAtMs < (s_topOk ? TopContentFreshMs : TopContentRetryMs)) return;

            int me = scope.MeSlot;
            string meUri = scope.Users.Id[me].Text;
            if (meUri.Length == 0) return;
            s_topAskedAtMs = now;
            s_topInflight = true;
            if (TopContentState.Peek() is HomeLoad.Idle or HomeLoad.Failed) TopContentState.Value = HomeLoad.Pending;

            uint epoch = scope.Epoch;
            byte[] meBytes = Encoding.UTF8.GetBytes(meUri);
            bool queued = Spotify.Api.Run(() =>
            {
                var s = Staging.Rent();
                s.Epoch = epoch;
                bool ok = false;
                try
                {
                    var answer = Spotify.Api.UserTop("SHORT_TERM", TopLimit, CancellationToken.None);
                    if (answer.Ok) { Spotify.Decode.UserTop(answer.Bytes, meBytes, s); ok = s.TopRuns.Count > 0; }
                    else Log.Warn("home", "userTopContent answered " + answer.Status.ToString(CultureInfo.InvariantCulture));
                }
                catch (Exception ex) { Log.Warn("home", "userTopContent failed", ex); }
                Spotify.Post(() => LandTopContent(scope, s, ok));
            });
            if (!queued)
            {
                s_topInflight = false;
                s_topOk = false;
                TopContentState.Value = HomeLoad.Failed;
            }
        }

        static void LandTopContent(Scope scope, Staging s, bool ok)
        {
            bool owned = false;
            try
            {
                if (!ReferenceEquals(scope, s_topScope) || !ReferenceEquals(scope, Entities.Current)) return;
                s_topInflight = false;
                s_topOk = ok;
                int me = scope.MeSlot;
                if (ok)
                {
                    Entities.Commit(s);                       // drops the batch whole if the scope moved (C7)
                    Entities.Publish();
                    owned = Store.WriteBehind(s);
                    TopContentState.Value = HomeLoad.Ready;
                    Log.Info("home", "top content landed: artists=" + scope.Edges.UserTopArtists.Count(me).ToString(CultureInfo.InvariantCulture)
                        + " tracks=" + scope.Edges.UserTopTracks.Count(me).ToString(CultureInfo.InvariantCulture));
                }
                else TopContentState.Value = scope.Edges.UserTopArtists.Count(me) > 0 ? HomeLoad.Ready : HomeLoad.Failed;
            }
            finally
            {
                if (!owned) Staging.Return(s);
            }
        }

        // ── the daylist rollover note (G-089 P half) ─────────────────────────────────────────────────────────────────

        /// <summary>Tracked: the Home subjects and the playlist table. Posts the scan.</summary>
        static void WatchDaylist()
        {
            _ = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            _ = scope.Homes.Changed.Value;
            _ = scope.Playlists.Changed.Value;
            Spotify.Post(s_noteDaylist);
        }

        static Scope? s_daylistScope;
        static int s_daylistSlot;
        static int s_daylistExpiresAt;
        static int s_daylistHome = Table.None;       // the feed row whose decode staged the card's daylist extras
        static int s_daylistSection = Table.None;    // the band the card was found in
        static int s_rolloverAttempts;
        static long s_rolloverLastFireMs;
        static long s_rolloverArmedAt;               // the fireAt the timer currently holds; 0 = stopped
        static System.Threading.Timer? s_rolloverWake;
        static readonly Action s_rolloverFire = RolloverFire;
        static readonly Action s_rearm = RearmDaylist;

        /// <summary>The unfiltered feed's daylist card: <c>Notify.Daylist.Note</c> once per (card, window), then the
        /// rollover ladder is (re)armed against the held window. Only a live Spotify scope — a demo launch must not
        /// schedule OS toasts or refetches.</summary>
        static void NoteDaylist()
        {
            var scope = Entities.Current;
            if (scope.Key.Provider != "spotify") { StopRollover(); return; }
            if (!ReferenceEquals(scope, s_daylistScope))
            {
                s_daylistScope = scope;
                s_daylistSlot = Table.None;
                s_daylistExpiresAt = 0;
                s_daylistHome = Table.None;
                s_daylistSection = Table.None;
                s_rolloverAttempts = 0;
            }
            ScanDaylist(scope);
            ArmRollover();
        }

        static void ScanDaylist(Scope scope)
        {
            if (!scope.Homes.TryGetSlot(FeedUri.AsSpan(), out int homeSlot)) return;
            var home = new Home(homeSlot);
            if (!home.IsValid) return;
            var playlists = scope.Playlists;
            foreach (int sectionSlot in home.SectionSlots)
            {
                var section = new Section(sectionSlot);
                if (!section.IsValid) continue;
                var targets = section.CardSlots;
                var kinds = section.CardKinds;
                for (int i = 0; i < targets.Length && i < kinds.Length; i++)
                {
                    if (kinds[i].Kind != EntityKind.Playlist) continue;
                    int slot = targets[i];
                    if (slot <= Table.None || slot >= playlists.Count) continue;
                    if ((PlaylistFormat)playlists.Format[slot] != PlaylistFormat.Daylist) continue;
                    int expires = playlists.DaylistExpiresAt[slot];
                    if (expires <= 0 || (slot == s_daylistSlot && expires == s_daylistExpiresAt)) continue;
                    // A new (card, window): a fresh ladder.
                    s_daylistSlot = slot;
                    s_daylistExpiresAt = expires;
                    s_daylistHome = homeSlot;
                    s_daylistSection = sectionSlot;
                    s_rolloverAttempts = 0;
                    var title = playlists.Title[slot];
                    Notify.Daylist.Note(new EntityUri(playlists.Id[slot]), expires * 1000L,
                        title.IsEmpty ? null : Entities.Strings.Resolve(title));
                    return;
                }
            }
        }

        // ── the rollover ladder (DaylistRollover decides; this owns the clock, the timer and the attempts) ───────────

        static void StopRollover()
        {
            if (s_rolloverArmedAt == 0) return;
            s_rolloverArmedAt = 0;
            s_rolloverWake?.Change(Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>Decide against the wall clock: Fire now, or arm the one-shot timer for the due instant. Called
        /// on every daylist scan, so an unchanged due instant costs one compare and never touches the timer.</summary>
        static void ArmRollover()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            int windowEnd = s_daylistSlot > Table.None ? s_daylistExpiresAt : 0;
            switch (DaylistRollover.Decide(windowEnd, s_rolloverAttempts, now, out long fireAt))
            {
                case DaylistRollover.Verdict.Fire:
                    // A decide far past the window (a resume hours later) must not walk the whole ladder at once:
                    // each rung waits its own step after the previous fire.
                    long earliest = s_rolloverAttempts > 0 ? s_rolloverLastFireMs + DaylistRollover.RetryMs[s_rolloverAttempts - 1] : now;
                    if (earliest > now) ArmAt(earliest, now);
                    else RolloverFire();
                    break;
                case DaylistRollover.Verdict.Arm:
                    ArmAt(fireAt, now);
                    break;
                default:
                    StopRollover();
                    break;
            }
        }

        static void ArmAt(long fireAt, long now)
        {
            if (fireAt == s_rolloverArmedAt) return;
            s_rolloverArmedAt = fireAt;
            s_rolloverWake ??= new System.Threading.Timer(static _ => Spotify.Post(s_rolloverFire), null, Timeout.Infinite, Timeout.Infinite);
            s_rolloverWake.Change(DaylistRollover.DelayMs(fireAt, now), Timeout.Infinite);
        }

        /// <summary>UI thread. One rung: re-ask the daylist row's edition groups, its tracks and the feed row's section
        /// list, then arm the next rung. The rows keep rendering the old edition while the answer is on the wire; a
        /// later window in the answer is a new (card, window) for <see cref="ScanDaylist"/>, which resets the ladder.</summary>
        static void RolloverFire()
        {
            s_rolloverArmedAt = 0;
            var scope = Entities.Current;
            if (!ReferenceEquals(scope, s_daylistScope) || s_daylistSlot <= Table.None) return;
            if (!Spotify.Current.IsOnline) return;   // the next scan or activation re-arms
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            // The timer may run early or a stale post may land late: re-decide before spending a rung.
            if (DaylistRollover.Decide(s_daylistExpiresAt, s_rolloverAttempts, now, out _) != DaylistRollover.Verdict.Fire)
            {
                ArmRollover();
                return;
            }
            s_rolloverAttempts++;
            s_rolloverLastFireMs = now;
            int slot = s_daylistSlot;
            Log.Info("home", "daylist rollover: window " + s_daylistExpiresAt.ToString(CultureInfo.InvariantCulture)
                + " ended; attempt " + s_rolloverAttempts.ToString(CultureInfo.InvariantCulture)
                + " — invalidating row, tracks, band " + s_daylistSection.ToString(CultureInfo.InvariantCulture));
            Entities.Invalidate(scope.Playlists, new ReadOnlySpan<int>(in slot), (uint)DaylistRollover.Groups, FetchPriority.Visible);
            Entities.InvalidateEdge(FetchEdge.PlaylistTracks, slot);
            // Only the feed decode (Decode.HomeFeed → CardOf) stages the masthead, generic title and window; the
            // homeSection page route stages a bare card. So the feed row's Sections group is re-asked, not the band's
            // cards edge.
            int home = s_daylistHome;
            if (home > Table.None && home < scope.Homes.Count)
                Entities.Invalidate(scope.Homes, new ReadOnlySpan<int>(in home), (uint)HomeFields.Sections, FetchPriority.Prefetch);
            ArmRollover();
        }

        /// <summary>A page activation or an OS resume: restart the ladder when the window has ended and the last fire
        /// is old enough (<see cref="DaylistRollover.ResetsOnActivation"/>), then decide again.</summary>
        public static void RearmDaylist()
        {
            if (s_daylistScope is null || s_daylistSlot <= Table.None) return;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (DaylistRollover.ResetsOnActivation(s_daylistExpiresAt, s_rolloverLastFireMs, now)) s_rolloverAttempts = 0;
            ArmRollover();
        }

        static void OnResumed() => Spotify.Post(s_rearm);
    }
}
