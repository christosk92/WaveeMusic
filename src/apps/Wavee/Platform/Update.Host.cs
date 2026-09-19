// ── Platform/Update.Host.cs ────────────────────────────────────────────────────────────────────────────────────────
// the app updater: the pure update rules (launch-version arming, the feed URL, the check verdict, the verb routing, the
// scheduler cadence, the simulated walk, the feed row) and the SHELL host (the .appinstaller check, the in-process
// deployment, the one scheduler timer, the developer simulation, install-on-quit)
//
// Role: CORE+SHELL
// Owner: R
// Wave: 6
// Budget: 650 lines (no plan row; D25)
// Spec: ch 27 §8 (`Notify.ShutdownUpdatePolicy`, already ported in Notify.cs and USED here), ch 27 W19/W20 (the About
//       states read `Notify.Update` + the facts below), ch 19 / ch 14 (`Notify.AppUpdateToasts` is the toast decision),
//       D25 (this file is the updater's home)
//
// PORTED FROM 0.2.9 `App/AppInstallerUpdateService.cs`, `App/AppUpdateScheduler.cs`, `App/AppUpdateSurface.cs`,
// `App/FakeAppUpdateService.cs`, `App/AppLaunchVersion.cs`, `Wavee.Core/Notifications/StoreUpdateService.cs` and
// `Program.InstallPendingUpdateOnQuit`. EVERY LOG SENTENCE IS 0.2.9's, VERBATIM, category "update": the local update
// E2E (`ops/release/tests/local-update-e2e.ps1`) greps "up to date: feed <q>, running <q>", "update available: <q>
// (running <q>)", "install-on-quit: staging <q>", "staged <q>; restarting", "install-on-quit finished: Installing",
// "install-on-quit: Windows asked us to exit … staged <q>", "install-on-quit gave up…", "deployment failed 0x…",
// "updated: <a> -> <b>" and "identity: " — a reworded sentence is a failed release gate.
//
// THREADS. Checks and applies run on the pool (HttpClient + the engine's MTA deployment worker); every snapshot reaches
// `Notify.Update` THROUGH the shell's `post`; every settings write made off the UI thread is posted too (a settings write
// bumps `Platform.SettingsChanged`, a signal). ONE scheduler timer, one-shot and re-armed, so nothing polls and a
// Store/dev build arms none; the developer simulation has its own one-shot timer that exists only while it walks.

using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Xml;
using FluentGpu.WindowsApi.Packaging;

namespace Wavee;

public static partial class Update
{
    // ══ CORE — engine-free decisions (no FluentGpu types above the SHELL line) ══════════════════════════════════════

    public const string LogCategory = "update";

    public const string RepoUrl = "https://github.com/" + ReleaseNotes.ChangelogParser.WaveeRepo;

    /// <summary>The background cadence: one check 30 s after launch (clear of the cold-start frame budget and the
    /// session bootstrap), then one every 24 h. A restart within an hour of the last successful check skips the launch
    /// check — restarting Wavee five times must not mean five feed hits.</summary>
    public static class Schedule
    {
        public static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(30);
        public static readonly TimeSpan Interval = TimeSpan.FromHours(24);
        public const long LaunchCheckCooldownMs = 60 * 60 * 1000;

        public static bool ShouldCheckAtLaunch(long lastCheckedMs, long nowMs)
        {
            long since = nowMs - lastCheckedMs;
            return lastCheckedMs <= 0 || since < 0 || since >= LaunchCheckCooldownMs;
        }
    }

    /// <summary>"Was this launch an update, and from what?" — decided ONCE per launch for EVERY install shape (a Store
    /// build that skipped it could never arm its after-update plate). Logs the identity line BEFORE deciding (the three
    /// facts the answer depends on, recorded at the moment they were read), arms the plate on an update — pendingFrom is
    /// the one-shot the plate consumes, previousVersion the durable fact nobody clears — and writes LastRunVersion
    /// UNCONDITIONALLY. Returns the version this launch updated FROM, or "".</summary>
    public static class LaunchVersion
    {
        public static string Arm(IAppSettings settings, bool isDev, string lastRunKey, string packageIdentity, string appDataRoot)
        {
            ArgumentNullException.ThrowIfNull(settings);
            string lastRun = settings.Get(Platform.Keys.LastRunVersion);
            Log.Info(LogCategory, "identity: " + (packageIdentity is { Length: > 0 } pfn ? pfn : "unpackaged")
                + "; lastRun='" + lastRun + "'; appData=" + appDataRoot);

            string from = "";
            if (!isDev && Notify.AppUpdateVersion.IsFirstRunAfterUpdate(lastRun, lastRunKey))
            {
                from = lastRun;
                settings.Set(Platform.Keys.ReleaseNotesPendingFrom, lastRun);
                settings.Set(Platform.Keys.ReleaseNotesPreviousVersion, lastRun);
                Log.Info(LogCategory, "updated: " + lastRun + " -> " + lastRunKey);
            }
            settings.Set(Platform.Keys.LastRunVersion, lastRunKey ?? "");
            return from;
        }
    }

    /// <summary>The per-arch feed this build watches — the SAME document Windows polls for packaged auto-update. Built
    /// entirely from BUILD-TIME metadata (stamped base URL + feed release + channel prefix + arch), so a package packed
    /// with <c>-UpdateBaseUrl http://127.0.0.1:8099/</c> polls a loopback feed down the code path that ships.</summary>
    public static string FeedUrlFor(string updateBaseUrl, string feedRelease, string channel, string arch)
        => updateBaseUrl + feedRelease + "/" + (channel == "beta" ? "Wavee.Beta." : "Wavee.")
           + (string.IsNullOrWhiteSpace(arch) ? "x64" : arch) + ".appinstaller";

    /// <summary>The release page a snapshot is talking about: its tag when the version can be named, else the listing.
    /// The ONE owner of this rule (0.2.9 grew three drifting copies).</summary>
    public static string ReleasePageUrl(AppUpdateSnapshot? snapshot)
    {
        if (snapshot is null) return RepoUrl + "/releases";
        string semver = snapshot.TargetSemVer is { Length: > 0 } s ? s : Notify.AppUpdateVersion.ReleaseTagVersion(snapshot.TargetQuad ?? "");
        return semver.Length > 0 ? RepoUrl + "/releases/tag/" + ReleaseNotes.ReleaseNotesValidation.TagPrefix + semver : RepoUrl + "/releases";
    }

    /// <summary>A Store build's "Update now": the Store app's product page, where its pending update lives.</summary>
    public static string StorePageUrl(string storeId) => "ms-windows-store://pdp/?productid=" + storeId;

    /// <summary>What a BCL exception may contribute to a user-facing snapshot: its type and HRESULT, never its Message —
    /// the NativeAOT publish collapses messages to bare resource keys that look like a diagnosis and are not one.</summary>
    public static string ExceptionCode(Exception ex)
        => ex is null ? "" : ex.GetType().Name + " 0x" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture);

    public enum CheckVerdict : byte { NoVersion, UpToDate, Available, Snoozed }

    /// <summary>A feed answer → what the check publishes. A dev build is never prompted; snooze is PER VERSION, so a
    /// newer release than the snoozed one shouts again.</summary>
    public static CheckVerdict Judge(string? remote, string runningQuad, bool isDev, string? snoozedVersion)
    {
        if (string.IsNullOrEmpty(remote)) return CheckVerdict.NoVersion;
        if (isDev || !Notify.AppUpdateVersion.IsNewer(remote, runningQuad)) return CheckVerdict.UpToDate;
        return string.Equals(snoozedVersion, remote, StringComparison.Ordinal) ? CheckVerdict.Snoozed : CheckVerdict.Available;
    }

    /// <summary>Only a pending or failed target can be applied; Downloading/Installing are already in flight.</summary>
    public static bool CanApply(AppUpdateState state) => state is AppUpdateState.Available or AppUpdateState.Snoozed or AppUpdateState.Failed;

    public static bool MeteredBlocks(bool metered, bool allowOnMetered) => metered && !allowOnMetered;

    public enum Verb : byte { None, Apply, Check, Snooze, Acknowledge, OpenReleasePage, OpenStorePage }

    /// <summary>One door for the toast, the panel row and Settings › About (<c>Notify.UpdateCommand</c>). Retry re-does
    /// what FAILED: a check failure has no target (check again), an apply failure has one (apply again). What's new
    /// navigates in the shell and never reaches the updater.</summary>
    public static Verb Route(UpdateRowAction action, AppUpdateSnapshot snapshot, bool isStore) => action switch
    {
        UpdateRowAction.UpdateNow => isStore ? Verb.OpenStorePage : Verb.Apply,
        UpdateRowAction.Retry => snapshot.TargetQuad is null ? Verb.Check : Verb.Apply,
        UpdateRowAction.Later => Verb.Snooze,
        UpdateRowAction.Dismiss => Verb.Acknowledge,
        UpdateRowAction.OpenReleasePage => Verb.OpenReleasePage,
        _ => Verb.None,
    };

    /// <summary>The notification centre's update row for a snapshot: nothing while None, otherwise the pinned, unread
    /// row — an update is STATE, its row lives as long as the state does. The feed host passes this to
    /// <c>Notify.Rebuild</c>.</summary>
    public static Notification? FeedRow(AppUpdateSnapshot snapshot)
        => snapshot.State == AppUpdateState.None ? null : NotifyRows.ForUpdate(snapshot, isUnread: true);

    /// <summary>The developer-mode walk (Settings › General › Developer › "Simulate an update"): Checking → Available →
    /// Downloading 0…100 by 5 → Installing → Completed, so every toast, row and About state can be SEEN without a signed
    /// release on a live feed. The build it names is deliberately implausible so a screenshot is never mistaken for a
    /// real release.</summary>
    public static class Simulation
    {
        public const string Quad = "99.9.9.999", SemVer = "99.9.9", Codename = "Simulated";
        public const int CheckingMs = 600, AvailableMs = 1500, ProgressStepMs = 150, InstallingMs = 900, ProgressIncrement = 5;
        public const int AvailableStep = 1, ApplyStep = 2;
        public const int InstallingStep = ApplyStep + 100 / ProgressIncrement + 1, CompletedStep = InstallingStep + 1;

        public static AppUpdateSnapshot At(int step, long nowMs) => step switch
        {
            <= 0 => AppUpdateSnapshot.Idle with { State = AppUpdateState.Checking },
            AvailableStep => Available(nowMs),
            < InstallingStep => Available(nowMs) with { State = AppUpdateState.Downloading, ProgressPercent = (step - ApplyStep) * ProgressIncrement },
            InstallingStep => Available(nowMs) with { State = AppUpdateState.Installing, ProgressPercent = 100 },
            _ => Available(nowMs) with { State = AppUpdateState.Completed },
        };

        /// <summary>How long a step stays on screen; -1 once the walk has ended (Completed).</summary>
        public static int DelayAfterMs(int step) => step switch
        {
            <= 0 => CheckingMs,
            AvailableStep => AvailableMs,
            < InstallingStep => ProgressStepMs,
            InstallingStep => InstallingMs,
            _ => -1,
        };

        static AppUpdateSnapshot Available(long nowMs)
            => new(AppUpdateState.Available, Quad, SemVer, Codename, 0, null, AutoUpdateAssociated: true, LastCheckedMs: nowMs);
    }

    // ══ SHELL — the service, the host ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>The build facts the service decides with. Built from <c>Platform.Version</c> by <see cref="Host.Start"/>;
    /// from literals by a test.</summary>
    public readonly record struct Identity(string Quad, string Core, string Codename, bool IsDev, string FeedUrl);

    /// <summary>The deployment classifier and the app's failure kinds are one taxonomy under two names: an explicit,
    /// total switch the compiler keeps honest (never a name round-trip).</summary>
    public static AppUpdateFailureKind MapFailure(PackageUpdateFailureKind kind) => kind switch
    {
        PackageUpdateFailureKind.Network => AppUpdateFailureKind.Network,
        PackageUpdateFailureKind.Metered => AppUpdateFailureKind.Metered,
        PackageUpdateFailureKind.PackagesInUse => AppUpdateFailureKind.PackagesInUse,
        PackageUpdateFailureKind.VersionConflict => AppUpdateFailureKind.VersionConflict,
        PackageUpdateFailureKind.SideloadPolicy => AppUpdateFailureKind.SideloadPolicy,
        PackageUpdateFailureKind.AppInstallerOutdated => AppUpdateFailureKind.AppInstallerOutdated,
        PackageUpdateFailureKind.NotAssociated => AppUpdateFailureKind.NotAssociated,
        _ => AppUpdateFailureKind.Unknown,
    };

    /// <summary>The real updater. It reads the SAME .appinstaller Windows polls, so the in-app prompt and the OS never
    /// disagree about "available", and applies through <see cref="IPackageUpdater"/> (in-process
    /// <c>AddPackageByAppInstallerFileAsync</c>, real progress). Unpackaged means the release page — never a fake
    /// "installing". Every observation is published WHOLE; failures are terminal for the attempt, never for the process.</summary>
    public sealed class Service
    {
        readonly Lock _gate = new();
        readonly Identity _me;
        readonly IAppSettings _settings;
        readonly HttpClient _http;
        readonly IPackageUpdater _updater;
        readonly Func<bool> _isMetered;
        readonly Action<string> _openUrl;
        readonly Action<AppUpdateSnapshot>? _published;
        readonly Func<string, ReleaseNotes.ReleaseNotesIndexEntry?>? _peekIndex;
        readonly Action<string>? _prefetchNotes;
        readonly Action? _beforeApply;
        AppUpdateSnapshot _current = AppUpdateSnapshot.Idle;

        /// <param name="updatedFrom"><see cref="LaunchVersion.Arm"/>'s answer, decided before this is built — a non-empty
        /// value publishes the "you were updated" Completed notice.</param>
        /// <param name="peekIndex">Names a feed quad from the release-notes index already in memory or on disk — NEVER
        /// the network.</param>
        /// <param name="prefetchNotes">Fire-and-forget warm of the offered release's notes (skipped on a metered link).</param>
        /// <param name="beforeApply">Tears down every child process sharing our package identity: one still alive at
        /// <c>ForceTargetAppShutdown</c> fails the deployment with 0x80073D02 after the whole download.</param>
        public Service(Identity me, IAppSettings settings, HttpClient http, IPackageUpdater updater, string updatedFrom,
            Func<bool>? isMetered = null, Action<string>? openUrl = null, Action<AppUpdateSnapshot>? published = null,
            Func<string, ReleaseNotes.ReleaseNotesIndexEntry?>? peekIndex = null, Action<string>? prefetchNotes = null,
            Action? beforeApply = null)
        {
            ArgumentNullException.ThrowIfNull(settings);
            ArgumentNullException.ThrowIfNull(http);
            ArgumentNullException.ThrowIfNull(updater);
            _me = me;
            _settings = settings;
            _http = http;
            _updater = updater;
            _isMetered = isMetered ?? (static () => false);
            _openUrl = openUrl ?? (static _ => { });
            _published = published;
            _peekIndex = peekIndex;
            _prefetchNotes = prefetchNotes;
            _beforeApply = beforeApply;
            if (updatedFrom is { Length: > 0 })
                _current = AppUpdateSnapshot.Idle with
                {
                    State = AppUpdateState.Completed, TargetQuad = me.Quad, TargetSemVer = me.Core, TargetCodename = me.Codename,
                };
        }

        public AppUpdateSnapshot Current { get { lock (_gate) return _current; } }
        public string FeedUrl => _me.FeedUrl;
        /// <summary>This process can drive a package update (packaged + a new-enough OS).</summary>
        public bool CanDeploy => _updater.IsSupported;

        public async Task CheckAsync(UpdateCheckOrigin origin, CancellationToken ct)
        {
            // A SCHEDULED poll that cannot reach the feed is recorded but not announced (Quiet); About still shows it.
            bool quiet = origin == UpdateCheckOrigin.Scheduled;
            // Publishing Checking overwrites Current, so the "you were updated" notice is remembered here.
            var entry = Current;
            Publish(entry with { State = AppUpdateState.Checking, Failure = null });
            try
            {
                string? remote = await ReadFeedVersionAsync(ct).ConfigureAwait(false);
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _settings.Set(Platform.Keys.UpdateLastCheckedMs, now);
                bool associated = false;
                try { associated = _updater.IsSupported && _updater.GetAppInstallerInfo() is not null; }
                catch (Exception ex) { Log.Warn(LogCategory, "app-installer association probe failed", ex); }

                var verdict = Judge(remote, _me.Quad, _me.IsDev, _settings.Get(Platform.Keys.UpdateSnoozedVersion));
                if (verdict == CheckVerdict.NoVersion)
                {
                    Log.Warn(LogCategory, "update feed had no Version attribute: " + FeedUrl);
                    Fail(AppUpdateFailureKind.Network, 0, "The update feed did not carry a version.", associated, now, quiet);
                    return;
                }
                if (verdict is CheckVerdict.Available or CheckVerdict.Snoozed)
                {
                    ReleaseNotes.ReleaseNotesIndexEntry? named = null;
                    try { named = _peekIndex?.Invoke(remote!); } catch (Exception ex) { Log.Warn(LogCategory, "release index peek failed", ex); }
                    Log.Info(LogCategory, "update available: " + remote + " (running " + _me.Quad + ")");
                    Publish(new AppUpdateSnapshot(verdict == CheckVerdict.Snoozed ? AppUpdateState.Snoozed : AppUpdateState.Available,
                        remote, named?.Version, named?.Name, 0, null, associated, now));
                    if (!_isMetered() && _prefetchNotes is { } prefetch)
                        try { prefetch(remote!); } catch (Exception ex) { Log.Warn(LogCategory, "release notes prefetch failed", ex); }
                    return;
                }

                Log.Info(LogCategory, "up to date: feed " + remote + ", running " + _me.Quad);
                // "Up to date" must not eat the Completed notice; only Acknowledge clears it.
                Publish(entry.State == AppUpdateState.Completed
                    ? entry with { AutoUpdateAssociated = associated, LastCheckedMs = now }
                    : AppUpdateSnapshot.Idle with { AutoUpdateAssociated = associated, LastCheckedMs = now });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Publish(entry);   // a cancelled check is not a failure: restore what we knew before the spinner
            }
            catch (Exception ex)
            {
                Log.Warn(LogCategory, "update check failed", ex);
                var cur = Current;
                Fail(AppUpdateFailureKind.Network, ex.HResult, ExceptionCode(ex), cur.AutoUpdateAssociated, cur.LastCheckedMs, quiet);
            }
        }

        public async Task ApplyAsync(CancellationToken ct)
        {
            if (!_updater.IsSupported)
            {
                _openUrl(ReleasePageUrl(Current));
                Log.Info(LogCategory, "unpackaged: opened the release page");
                return;
            }
            // ONE read for the whole attempt: every progress publish carries the identity the attempt started with, so a
            // Snooze/Acknowledge between two ticks can never be folded into a later tick and resurrected.
            var entry = Current;
            if (MeteredBlocks(_isMetered(), _settings.Get(Platform.Keys.UpdateOnMetered)))
            {
                Fail(AppUpdateFailureKind.Metered, 0, "", entry.AutoUpdateAssociated, entry.LastCheckedMs);
                return;
            }
            if (!CanApply(entry.State)) return;

            Publish(entry with { State = AppUpdateState.Downloading, ProgressPercent = 0, Failure = null });
            try
            {
                try { _beforeApply?.Invoke(); }
                catch (Exception ex) { Log.Warn(LogCategory, "module host teardown before update failed", ex); }

                var result = await _updater.ApplyFromAppInstallerAsync(new Uri(FeedUrl),
                    pct => Publish(entry with { State = AppUpdateState.Downloading, ProgressPercent = pct, Failure = null }),
                    ct).ConfigureAwait(false);

                // REGISTERED is the only success: HRESULT 0 with nothing registered is a deployment that never ran.
                if (result.IsRegistered)
                {
                    _settings.Set(Platform.Keys.UpdateSnoozedVersion, "");
                    // Windows terminates us next and relaunches through the restart registration: the last state we publish.
                    Publish(entry with { State = AppUpdateState.Installing, ProgressPercent = 100, Failure = null });
                    Log.Info(LogCategory, "staged " + (entry.TargetQuad ?? "") + "; restarting");
                }
                else if (result.HResult == 0)
                {
                    Log.Warn(LogCategory, "deployment reported success but registered nothing: " + result.ErrorText);
                    Fail(AppUpdateFailureKind.Unknown, 0, "The installer reported success but registered nothing.",
                        entry.AutoUpdateAssociated, entry.LastCheckedMs);
                }
                else
                {
                    Log.Warn(LogCategory, "deployment failed 0x"
                        + result.HResult.ToString("X8", CultureInfo.InvariantCulture) + ": " + result.ErrorText);
                    Fail(MapFailure(PackageUpdateErrors.Classify(result.HResult)), result.HResult, result.ErrorText,
                        entry.AutoUpdateAssociated, entry.LastCheckedMs);
                }
            }
            catch (OperationCanceledException)
            {
                Publish(entry with { State = AppUpdateState.Available, ProgressPercent = 0, Failure = null });
            }
            catch (Exception ex)
            {
                Log.Warn(LogCategory, "update apply failed", ex);
                Fail(AppUpdateFailureKind.Unknown, ex.HResult, ExceptionCode(ex), entry.AutoUpdateAssociated, entry.LastCheckedMs);
            }
        }

        /// <summary>"Later": this exact target stops shouting; a NEWER quad is offered again.</summary>
        public void Snooze()
        {
            var cur = Current;
            if (cur.TargetQuad is not { Length: > 0 } quad) return;
            _settings.Set(Platform.Keys.UpdateSnoozedVersion, quad);
            Publish(cur with { State = AppUpdateState.Snoozed });
        }

        /// <summary>Clears a Completed/Failed observation, keeping what the OS knows and when we last looked.</summary>
        public void Acknowledge()
        {
            var cur = Current;
            Publish(AppUpdateSnapshot.Idle with { AutoUpdateAssociated = cur.AutoUpdateAssociated, LastCheckedMs = cur.LastCheckedMs });
        }

        /// <summary>One GET and one attribute read of the root <c>&lt;AppInstaller Version="…"&gt;</c> with
        /// <see cref="XmlReader"/>: no reflection, stops at the root, DTDs and external entities off (untrusted input).
        /// Anything whose root is not AppInstaller (a 404 page, an S3 error document) answers null.</summary>
        async Task<string?> ReadFeedVersionAsync(CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, FeedUrl);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var xml = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true, IgnoreWhitespace = true, Async = true,
            };
            using var reader = XmlReader.Create(stream, xml);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                if (reader.NodeType != XmlNodeType.Element) continue;
                if (!string.Equals(reader.LocalName, "AppInstaller", StringComparison.Ordinal)) return null;
                return reader.GetAttribute("Version");
            }
            return null;
        }

        void Fail(AppUpdateFailureKind kind, int hresult, string? message, bool associated, long lastChecked, bool quiet = false)
        {
            var cur = Current;
            Publish(new AppUpdateSnapshot(AppUpdateState.Failed, cur.TargetQuad, cur.TargetSemVer, cur.TargetCodename, 0,
                new AppUpdateFailure(kind, hresult, message ?? ""), associated, lastChecked, quiet));
        }

        void Publish(AppUpdateSnapshot snapshot)
        {
            lock (_gate) _current = snapshot;
            _published?.Invoke(snapshot);
        }
    }

    /// <summary>The composition of the updater: one per process, started from the GUI path only (never headless).</summary>
    public static class Host
    {
        // ── seams (null = the documented fallback, never a throw) ───────────────────────────────────────────────────

        /// <summary>S assigns <c>NetworkPolicy.IsMetered</c>. Null ⇒ unmetered (logged once): a probe we do not have must
        /// never silently block an update the user asked for.</summary>
        public static Func<bool>? IsMetered;
        /// <summary>The ReleaseNotes stream assigns: the index entry for a quad, from memory/disk ONLY.</summary>
        public static Func<string, ReleaseNotes.ReleaseNotesIndexEntry?>? PeekIndexEntry;
        /// <summary>The ReleaseNotes stream assigns: warm the offered release's notes, fire-and-forget.</summary>
        public static Action<string>? PrefetchNotes;
        /// <summary>T assigns: dispose the playback-module host (every package-identity child) before a deployment.</summary>
        public static Action? BeforeApply;
        /// <summary>How a link reaches the browser / the Store app. Swappable; the default shell-executes it.</summary>
        public static Action<string> OpenUrl = static url =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
            catch (Exception ex) { Log.Warn(LogCategory, "could not open the update link", ex); }
        };

        // ── read-only facts (About, diagnostics, the crash prompt) ──────────────────────────────────────────────────

        /// <summary>The .appinstaller this build polls ("" before Start); the Store product page on a Store build.</summary>
        public static string FeedUrl { get; private set; } = "";
        public static bool IsStore { get; private set; }
        public static bool IsDev { get; private set; }
        /// <summary>True while a simulated walk runs: About must NOT grey its dev-build button during one.</summary>
        public static bool IsSimulating => Volatile.Read(ref s_simStep) >= 0;
        /// <summary>This process can self-update in place (packaged); false = "Update now" opens the release page.</summary>
        public static bool CanDeploy => s_service?.CanDeploy ?? false;
        /// <summary>The live updater's last observation (Idle on a Store/dev build) — thread-safe, for diagnostics.</summary>
        public static AppUpdateSnapshot Current => s_service?.Current ?? AppUpdateSnapshot.Idle;
        public static AppUpdateFailure? LastFailure => Current.Failure;
        /// <summary>The version this launch updated FROM ("" = not an update launch).</summary>
        public static string UpdatedFrom { get; private set; } = "";
        /// <summary><c>app.lastRunVersion</c> as it was BEFORE this launch overwrote it (the crash prompt's versionChanged).</summary>
        public static string PreviousRunVersion { get; private set; } = "";
        public static bool RelaunchedAfterUpdate { get; private set; }

        static int s_started, s_checking, s_applying, s_meteredNoted, s_launchTicked;
        static volatile bool s_exiting;
        static Action<Action>? s_post;
        static Service? s_service;
        static System.Threading.Timer? s_scheduler;
        static readonly CancellationTokenSource s_cts = new();
        static readonly Lock s_simGate = new();
        static int s_simStep = -1;
        static bool s_simPaused;
        static System.Threading.Timer? s_simTimer;

        static string Arch => RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        /// <summary>Idempotent. Arms the launch version (every install shape), assigns <c>Notify.UpdateCommand</c>, and on
        /// a non-Store, non-dev build creates the service (packaged ⇒ the real deployment, unpackaged ⇒ the release page)
        /// and the scheduler. Every snapshot reaches <c>Notify.Update</c> through <paramref name="post"/>.</summary>
        public static void Start(Action<Action> post)
        {
            ArgumentNullException.ThrowIfNull(post);
            if (Interlocked.Exchange(ref s_started, 1) != 0) return;
            s_post = post;
            var me = Platform.Version;
            IsStore = me.IsStore;
            IsDev = me.IsDev;

            // The "relaunched by Windows after an update" boot line is Platform.Host.cs's (S); this only remembers the fact.
            RelaunchedAfterUpdate = Array.IndexOf(Environment.GetCommandLineArgs(), Platform.RelaunchedAfterUpdateFlag) >= 0;
            PreviousRunVersion = Platform.Settings.Get(Platform.Keys.LastRunVersion);
            UpdatedFrom = LaunchVersion.Arm(Platform.Settings, me.IsDev, me.LastRunKey,
                PackageIdentity.PackageFullName is { Length: > 0 } pfn ? pfn : "unpackaged", Platform.LocalFolder);

            Notify.UpdateCommand = Run;
            if (me.IsStore) { FeedUrl = StorePageUrl(me.StoreId); return; }   // the Store owns updates: stay Idle
            FeedUrl = FeedUrlFor(me.UpdateBaseUrl, me.FeedRelease, me.Channel, Arch);
            if (me.IsDev) return;   // nothing for a feed to be newer than: no service, no timer (the simulation still walks)

            IPackageUpdater updater = PackageIdentity.IsPackaged
                ? new PackageUpdater { RestartArgument = Platform.RelaunchedAfterUpdateFlag }
                : new NullPackageUpdater();
            var http = new HttpClient(Wire.Handler("update", new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(2), PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
                MaxConnectionsPerServer = 4, AutomaticDecompression = DecompressionMethods.All,
            })) { Timeout = TimeSpan.FromSeconds(15) };
            // The product token the E2E proves the in-app checker by ("Wavee/…"); GitHub refuses a request without one.
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", me.UserAgent(RuntimeInformation.OSDescription, Arch));
            http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/vnd.github+json");

            var svc = new Service(new Identity(me.Quad, me.Core, me.Codename, me.IsDev, FeedUrl), new PostedSettings(), http, updater,
                UpdatedFrom, isMetered: static () => Metered(), openUrl: static url => OpenUrl(url), published: static s => OnPublished(s),
                peekIndex: static quad => PeekIndexEntry?.Invoke(quad), prefetchNotes: static quad => PrefetchNotes?.Invoke(quad),
                beforeApply: static () => BeforeApply?.Invoke());
            s_service = svc;
            if (svc.Current.State != AppUpdateState.None) PublishUi(svc.Current);
            s_scheduler = new System.Threading.Timer(static state => { _ = OnSchedulerTickAsync(); }, null, Schedule.FirstDelay, Timeout.InfiniteTimeSpan);
        }

        /// <summary>About's "Check for updates" (a USER check: a failure is loud). A no-op on a Store/dev build.</summary>
        public static void CheckNow()
        {
            if (IsSimulating) { RunSimulated(Verb.Check); return; }
            if (s_service is { } svc && !s_exiting) _ = Task.Run(() => CheckGuardedAsync(svc, UpdateCheckOrigin.User));
        }

        /// <summary>Developer mode: walk the whole lifecycle locally, down the same <c>Notify.Update</c> path a real
        /// observation takes; while it walks, every update verb drives the SIMULATION (a simulated card whose buttons
        /// talked to the Idle live updater would be inert).</summary>
        public static void SimulateUpdate()
        {
            if (s_post is null || s_exiting) return;
            lock (s_simGate)
            {
                s_simPaused = false;
                s_simTimer ??= new System.Threading.Timer(static _ => SimTick(), null, Timeout.Infinite, Timeout.Infinite);
                SimShow(0);
            }
        }

        /// <summary>"Install a waiting update when I quit Wavee" — the composition root's exit tail, after the loop and
        /// before <c>Platform.Shutdown</c>. The decision is <c>Notify.ShutdownUpdatePolicy</c>. The apply runs on the pool
        /// while THIS thread pumps messages (a blocked GUI thread is killed as hung), bounded at ten minutes, and it stops
        /// waiting the moment Windows asks us to leave: the deployment's Restart Manager waits for this process to exit,
        /// the package is already staged, and the restart registration brings Wavee back.</summary>
        public static void ApplyOnExit()
        {
            s_exiting = true;
            StopTimers();
            try
            {
                if (s_service is not { } svc) return;
                if (!Notify.ShutdownUpdatePolicy.ShouldApply(Platform.Settings.Get(Platform.Keys.UpdateInstallOnQuit), svc.Current.State))
                    return;
                // Read ONCE: both lines below must name the same quad, and Current is republished by every progress tick.
                string targetQuad = svc.Current.TargetQuad ?? "?";
                Log.Info(LogCategory, "install-on-quit: staging " + targetQuad);
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                Task apply = Task.Run(() => svc.ApplyAsync(cts.Token));
                // A slightly longer ceiling than the token's, so the cancellation lands and the "gave up" line names its state.
                var outcome = FluentGpu.MessagePump.RunUntil(apply, TimeSpan.FromMinutes(10.5));
                if (outcome == FluentGpu.PumpOutcome.ShutdownRequested)
                {
                    Log.Info(LogCategory, "install-on-quit: Windows asked us to exit "
                        + "(the deployment is taking over); staged " + targetQuad);
                    Log.Flush();
                    return;
                }
                if (outcome == FluentGpu.PumpOutcome.TimedOut)
                {
                    Log.Warn(LogCategory, "install-on-quit gave up in state " + svc.Current.State
                        + " (the update is still offered and applies on the next launch)");
                    return;
                }
                apply.GetAwaiter().GetResult();   // observe a fault so the catch logs it
                var state = svc.Current.State;
                if (Notify.ShutdownUpdatePolicy.IsSettled(state)) Log.Info(LogCategory, "install-on-quit finished: " + state);
                else Log.Warn(LogCategory, "install-on-quit gave up in state " + state
                    + " (the update is still offered and applies on the next launch)");
            }
            catch (Exception ex)
            {
                Log.Warn(LogCategory, "install-on-quit failed", ex);   // never turn a quit into a crash
            }
            finally
            {
                Log.Flush();
            }
        }

        /// <summary>Stop both timers and cancel an in-flight check. Idempotent; the exit tail calls it after
        /// <see cref="ApplyOnExit"/>.</summary>
        public static void Shutdown()
        {
            s_exiting = true;
            StopTimers();
            try { s_cts.Cancel(); } catch (ObjectDisposedException) { }
        }

        // ── the scheduler ───────────────────────────────────────────────────────────────────────────────────────────

        static async Task OnSchedulerTickAsync()
        {
            bool launch = Interlocked.Exchange(ref s_launchTicked, 1) == 0;
            try
            {
                if (s_service is not { } svc || s_exiting) return;
                if (launch && !Schedule.ShouldCheckAtLaunch(Platform.Settings.Get(Platform.Keys.UpdateLastCheckedMs), NowMs())) return;
                try { await CheckGuardedAsync(svc, UpdateCheckOrigin.Scheduled).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { Log.Warn(LogCategory, launch ? "initial update check failed" : "periodic update check failed", ex); }
            }
            finally
            {
                // One-shot, re-armed: a slow check can never overlap the next tick, and a stopped scheduler stays stopped.
                if (!s_exiting)
                    try { s_scheduler?.Change(Schedule.Interval, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
            }
        }

        static async Task CheckGuardedAsync(Service svc, UpdateCheckOrigin origin)
        {
            if (Interlocked.Exchange(ref s_checking, 1) != 0) return;   // one check at a time
            try { await svc.CheckAsync(origin, s_cts.Token).ConfigureAwait(false); }
            finally { Volatile.Write(ref s_checking, 0); }
        }

        static void StopTimers()
        {
            try { s_scheduler?.Dispose(); } catch (ObjectDisposedException) { }
            lock (s_simGate)
            {
                try { s_simTimer?.Dispose(); } catch (ObjectDisposedException) { }
                s_simTimer = null;
                Volatile.Write(ref s_simStep, -1);
            }
        }

        static bool Metered()
        {
            if (IsMetered is { } probe)
            {
                try { return probe(); } catch (Exception ex) { Log.Warn(LogCategory, "metered probe failed", ex); return false; }
            }
            if (Interlocked.Exchange(ref s_meteredNoted, 1) == 0)
                Log.Info(LogCategory, "no metered-connection probe attached; treating the connection as unmetered");
            return false;
        }

        // ── the verbs ───────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary><c>Notify.UpdateCommand</c> (UI thread).</summary>
        static void Run(UpdateRowAction action, AppUpdateSnapshot snapshot)
        {
            var verb = Route(action, snapshot, IsStore);
            if (IsSimulating) { RunSimulated(verb, snapshot); return; }
            switch (verb)
            {
                case Verb.Apply:
                    if (s_service is { } svc && !s_exiting && Interlocked.Exchange(ref s_applying, 1) == 0)
                        _ = Task.Run(async () =>
                        {
                            // Never the host's token: a user's deployment is not cancelled by our own shutdown.
                            try { await svc.ApplyAsync(CancellationToken.None).ConfigureAwait(false); }
                            catch (Exception ex) { Log.Warn(LogCategory, "update apply failed", ex); }
                            finally { Volatile.Write(ref s_applying, 0); }
                        });
                    break;
                case Verb.Check: CheckNow(); break;
                case Verb.Snooze: s_service?.Snooze(); break;
                case Verb.Acknowledge:
                    if (s_service is { } live) live.Acknowledge();
                    else PublishUi(AppUpdateSnapshot.Idle);   // a dev build dismissing a finished simulation
                    break;
                case Verb.OpenReleasePage: OpenUrl(ReleasePageUrl(snapshot)); break;
                case Verb.OpenStorePage: OpenUrl(FeedUrl); break;
            }
        }

        // ── publishing ──────────────────────────────────────────────────────────────────────────────────────────────

        static void OnPublished(AppUpdateSnapshot snapshot)
        {
            if (!IsSimulating) PublishUi(snapshot);   // a walk on screen is not overwritten by a background poll
        }

        static void PublishUi(AppUpdateSnapshot snapshot)
        {
            // After the loop has ended there is no UI to post to (install-on-quit): the state lives on in the service.
            if (s_exiting || s_post is not { } post) return;
            post(() => Notify.Update.Value = snapshot);
        }

        /// <summary>Settings writes the service makes from pool threads, marshalled to the UI thread (a write bumps a
        /// signal). Once exiting there is no loop left to marshal onto, so the write goes straight to the store.</summary>
        sealed class PostedSettings : IAppSettings
        {
            public T Get<T>(SettingKey<T> key) => Platform.Settings.Get(key);

            public void Set<T>(SettingKey<T> key, T value)
            {
                if (s_exiting || s_post is not { } post) { Platform.Settings.Set(key, value); return; }
                post(() => Platform.Settings.Set(key, value));
            }
        }

        // ── the simulation ──────────────────────────────────────────────────────────────────────────────────────────

        static void SimTick()
        {
            lock (s_simGate)
            {
                int step = s_simStep;
                if (step < 0 || s_simPaused || s_exiting) return;
                SimShow(step + 1);
            }
        }

        /// <summary>Under <see cref="s_simGate"/>. The walk ENDS on Completed: from then on the verbs drive the live updater.</summary>
        static void SimShow(int step)
        {
            int delay = Simulation.DelayAfterMs(step);
            Volatile.Write(ref s_simStep, delay < 0 ? -1 : step);
            PublishUi(Simulation.At(step, NowMs()));
            if (delay >= 0) s_simTimer?.Change(delay, Timeout.Infinite);
        }

        static void RunSimulated(Verb verb, AppUpdateSnapshot? snapshot = null)
        {
            if (verb == Verb.OpenReleasePage) { OpenUrl(ReleasePageUrl(snapshot)); return; }   // never shell out under the gate
            lock (s_simGate)
            {
                int step = s_simStep;
                if (step < 0) return;
                switch (verb)
                {
                    case Verb.Apply when step < Simulation.ApplyStep:
                        s_simPaused = false;
                        SimShow(Simulation.ApplyStep);
                        break;
                    case Verb.Check or Verb.Snooze:
                        // "Later" means no auto-apply: the walk parks on the offer until Update now resumes it.
                        s_simPaused = true;
                        s_simTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                        Volatile.Write(ref s_simStep, Simulation.AvailableStep);
                        var offer = Simulation.At(Simulation.AvailableStep, NowMs());
                        PublishUi(verb == Verb.Snooze ? offer with { State = AppUpdateState.Snoozed } : offer);
                        break;
                    case Verb.Acknowledge:
                        s_simTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                        s_simPaused = false;
                        Volatile.Write(ref s_simStep, -1);
                        PublishUi(s_service?.Current ?? AppUpdateSnapshot.Idle);   // hand the surfaces back to the live state
                        break;
                }
            }
        }
    }
}
