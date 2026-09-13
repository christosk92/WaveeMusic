using System;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using FluentGpu.WindowsApi.Packaging;
using Wavee.Core;

namespace Wavee;

/// <summary>
/// The real <see cref="IAppUpdateService"/>. It reads the SAME per-arch <c>.appinstaller</c> feed Windows itself polls
/// for packaged auto-update, so the in-app prompt and the OS never disagree about what "available" means, and it
/// applies an update through <see cref="IPackageUpdater"/> — <c>PackageManager.AddPackageByAppInstallerFileAsync</c>,
/// in-process, with real progress.
/// <para>
/// <b>Why not <c>ms-appinstaller:</c>.</b> That hand-off is disabled by default on consumer Windows: the protocol
/// launch silently does nothing and the app was left claiming the update had been handed over. There is no legacy
/// path here — packaged means the deployment API, unpackaged means the release page, and nothing pretends.
/// </para>
/// <para>
/// <b>Which feed.</b> BOTH halves of the feed URL are BUILD-TIME metadata stamped by the pack script — the download
/// root (<see cref="WaveeVersionInfo.UpdateBaseUrl"/>, GitHub's release-asset prefix by default) and the rolling
/// release that carries the feed on it (<see cref="WaveeVersionInfo.FeedRelease"/>) — so a test package can poll a
/// scratch release, or a loopback HTTP server, with no environment switch and no runtime toggle. The channel picks the
/// asset prefix on top of that.
/// </para>
/// <para>
/// <b>State.</b> Every observation is published whole as an <see cref="AppUpdateSnapshot"/> — the UI can never read a
/// torn (state, version, progress) triple. Failures are terminal for the attempt and never for the process: they land
/// in <see cref="AppUpdateState.Failed"/> with a classified <see cref="AppUpdateFailure"/>, are logged, and are
/// swallowed.
/// </para>
/// </summary>
sealed class AppInstallerUpdateService : IAppUpdateService
{
    const string LogCategory = "update";

    readonly SimpleEvent<int> _changed = new();
    readonly IAppSettings _settings;
    readonly HttpClient _http;
    readonly IPackageUpdater _updater;
    readonly IWaveeLog _log;
    readonly WaveeVersionInfo _me;
    readonly string _arch;
    readonly Func<bool> _isMetered;
    // The notes store is INJECTED, never resolved through ReleaseNotesStore.Instance: that static is whichever store was
    // constructed last in the process, which under a parallel test run is another test's store (and its recorded
    // requests). Null = no notes (the unpackaged/test shape); Services wires the real one.
    readonly ReleaseNotesStore? _notes;
    readonly Action<string> _openUrl;
    readonly object _gate = new();
    int _rev;

    /// <summary>The process-wide updater instance, published by the composition root's construction of it.
    /// <see cref="IAppUpdateService"/> is app-scoped by contract (one updater per process, a plain field on
    /// <c>Services</c> with no switchable wrapper), so surfaces that cannot reach the service bag —
    /// <c>PlaybackBridge.Activate</c>, which starts the poll — resolve it here. Null until <c>Services</c> is built.</summary>
    public static AppInstallerUpdateService? Instance { get; private set; }

    public AppUpdateSnapshot Current { get; private set; } = AppUpdateSnapshot.Idle;
    public IObservable<int> Changed => _changed;

    /// <summary>The per-arch update feed this build watches — also what <see cref="ApplyAsync"/> hands the deployment API.
    /// <para>Built entirely from BUILD-TIME metadata: <c>UpdateBaseUrl</c> + <c>FeedRelease</c> + the channel's asset
    /// prefix + the running architecture. The base URL is normally GitHub's release-download root, but a package packed
    /// with <c>-UpdateBaseUrl http://127.0.0.1:8099/</c> polls a loopback feed instead — the local end-to-end update
    /// test exercises this exact code path with no runtime switch. Human-facing links keep using
    /// <c>ReleaseNotesText.RepoUrl</c>; only the machine-read feed follows the stamped base.</para></summary>
    public string FeedUrl { get; }

    /// <param name="settings">The persisted settings store (last-run version, snooze, metered opt-in).</param>
    /// <param name="github">The GitHub pool (product user-agent + <c>application/vnd.github+json</c> already set).</param>
    /// <param name="me">This build's parsed identity — the feed name, the channel and the quad all come from it.</param>
    /// <param name="arch">"arm64" or "x64" — selects the per-arch feed asset.</param>
    /// <param name="updater">The packaged-deployment seam; a <c>NullPackageUpdater</c> when unpackaged.</param>
    /// <param name="log">The app log.</param>
    /// <param name="updatedFrom">The version this launch updated FROM, or "" when this launch was not an update —
    /// the return of <see cref="AppLaunchVersion.Arm"/>, which the composition root (<c>Services.cs</c>) calls ONCE,
    /// for BOTH install shapes, before either updater is constructed. This ctor no longer decides that itself: doing
    /// so here (as it used to) meant a Store-installed build — whose composition root never constructs this class —
    /// silently skipped the arming entirely. Consuming the already-decided answer, rather than re-deciding it, also
    /// avoids reading <c>WaveeSettings.LastRunVersion</c> a second time after <see cref="AppLaunchVersion.Arm"/> has
    /// already advanced it to <c>me.LastRunKey</c> (which would always answer "no update").</param>
    /// <param name="isMetered">The live metered-connection probe. The composition root passes
    /// <c>NetworkPolicy.IsMetered</c>; the default is unmetered-conservative, matching that policy's own fail-soft
    /// (a probe we do not have must never silently block an update the user asked for).</param>
    /// <param name="openUrl">How a link reaches the browser. Defaults to the shell (<see cref="ShellOpen.OpenUrl"/>).</param>
    public AppInstallerUpdateService(IAppSettings settings, HttpClient github, WaveeVersionInfo me, string arch,
        IPackageUpdater updater, IWaveeLog log, string updatedFrom, Func<bool>? isMetered = null,
        Action<string>? openUrl = null, ReleaseNotesStore? notes = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(github);
        ArgumentNullException.ThrowIfNull(me);
        ArgumentNullException.ThrowIfNull(updater);
        ArgumentNullException.ThrowIfNull(log);
        _settings = settings;
        _http = github;
        _me = me;
        _arch = string.IsNullOrWhiteSpace(arch) ? "x64" : arch;
        _updater = updater;
        _log = log;
        _isMetered = isMetered ?? (static () => false);
        _notes = notes;
        _openUrl = openUrl ?? (static u => ShellOpen.OpenUrl(u));

        string assetPrefix = me.Channel == "beta" ? "Wavee.Beta." : "Wavee.";
        FeedUrl = me.UpdateBaseUrl + me.FeedRelease + "/" + assetPrefix + _arch + ".appinstaller";

        // "You were updated" was decided ONCE, by AppLaunchVersion.Arm (see the <param> above) — the only moment
        // where the previous run's LastRunVersion was still on disk. All this ctor does with the answer is publish
        // the resulting Completed snapshot; it never re-reads settings to re-derive it.
        if (updatedFrom is { Length: > 0 })
        {
            Current = AppUpdateSnapshot.Idle with
            {
                State = AppUpdateState.Completed,
                TargetQuad = me.Quad,
                TargetSemVer = me.Core,
                TargetCodename = me.Codename,
            };
        }
        Instance = this;
    }

    // ── check ────────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task CheckAsync(UpdateCheckOrigin origin, CancellationToken ct)
    {
        // A background poll that cannot reach the feed is recorded but not ANNOUNCED (Quiet): the first launch of a
        // fresh install polls 30 s in while the user is still in the setup wizard, and a link that is down for a
        // minute is not an error the user asked to hear about. Settings › About still shows it, with Retry.
        bool quiet = origin == UpdateCheckOrigin.Scheduled;
        // What we knew BEFORE the spinner. Publishing Checking overwrites Current, so a "was this launch an update?"
        // notice raised by the ctor has to be remembered here or the up-to-date branch below silently eats it.
        var entry = Current;
        Publish(Current with { State = AppUpdateState.Checking, Failure = null });
        try
        {
            string? remote = await ReadFeedVersionAsync(ct).ConfigureAwait(false);
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _settings.Set(WaveeSettings.UpdateLastCheckedMs, now);
            bool associated = false;
            try { associated = _updater.IsSupported && _updater.GetAppInstallerInfo() is not null; }
            catch (Exception ex) { _log.Warn(LogCategory, "app-installer association probe failed", ex); }

            if (string.IsNullOrEmpty(remote))
            {
                _log.Warn(LogCategory, "update feed had no Version attribute: " + FeedUrl);
                Fail(AppUpdateFailureKind.Network, 0, "The update feed did not carry a version.", associated, now, quiet);
                return;
            }

            if (!_me.IsDev && AppUpdateVersion.IsNewer(remote, _me.Quad))
            {
                bool snoozed = string.Equals(_settings.Get(WaveeSettings.UpdateSnoozedVersion), remote, StringComparison.Ordinal);
                // Naming the release costs NO network: PeekIndexAsync only consults what is already in memory or on
                // disk. The prefetch below is what (best-effort) fills that in for the next check.
                var index = _notes is { } store
                    ? await store.PeekIndexAsync(ct).ConfigureAwait(false)
                    : null;
                var indexEntry = index?.Find(remote);
                _log.Info(LogCategory, "update available: " + remote + " (running " + _me.Quad + ")");
                Publish(new AppUpdateSnapshot(
                    snoozed ? AppUpdateState.Snoozed : AppUpdateState.Available,
                    remote, indexEntry?.Version, indexEntry?.Name, 0, null, associated, now));
                if (!_isMetered() && _notes is { } prefetch)
                    _ = prefetch.PrefetchAsync(remote, ct);
                return;
            }

            _log.Info(LogCategory, "up to date: feed " + remote + ", running " + _me.Quad);
            // "Up to date" must not silently eat the "you were updated" notice the ctor raised — the poll runs 30 s
            // after launch, long before the user has necessarily looked at the notification centre. Only
            // Acknowledge() clears a Completed.
            Publish(entry.State == AppUpdateState.Completed
                ? entry with { AutoUpdateAssociated = associated, LastCheckedMs = now }
                : AppUpdateSnapshot.Idle with { AutoUpdateAssociated = associated, LastCheckedMs = now });
        }
        catch (OperationCanceledException)
        {
            // A cancelled check is not a failure — restore exactly what we knew before the spinner.
            Publish(entry);
        }
        catch (Exception ex)
        {
            _log.Warn(LogCategory, "update check failed", ex);
            Fail(AppUpdateFailureKind.Network, ex.HResult, ExceptionCode(ex),
                Current.AutoUpdateAssociated, Current.LastCheckedMs, quiet);
        }
    }

    // ── apply ────────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task ApplyAsync(CancellationToken ct)
    {
        // Unpackaged (a loose NativeAOT publish): there is nothing to deploy INTO, so the honest action is the release
        // page. Never a fake "installing" state.
        if (!_updater.IsSupported)
        {
            _openUrl(ReleaseNotesText.ReleasePageUrl(Current));
            _log.Info(LogCategory, "unpackaged: opened the release page");
            return;
        }
        // ONE read of Current for the whole attempt. Every progress tick used to re-read the property and rebuild its
        // snapshot from whatever the last publish left there, so a concurrent Snooze/Acknowledge between two ticks
        // would be folded into the next progress publish and silently resurrected. The attempt's identity (target
        // quad, semver, codename) is fixed the moment it starts.
        var entry = Current;
        if (_isMetered() && !_settings.Get(WaveeSettings.UpdateOnMetered))
        {
            Fail(AppUpdateFailureKind.Metered, 0, "", entry.AutoUpdateAssociated, entry.LastCheckedMs);
            return;
        }
        if (entry.State is not (AppUpdateState.Available or AppUpdateState.Snoozed or AppUpdateState.Failed)) return;

        Publish(entry with { State = AppUpdateState.Downloading, ProgressPercent = 0, Failure = null });
        try
        {
            // Every child process that shares our package identity must be gone before ForceTargetAppShutdown, or the
            // deployment fails with 0x80073D02 (packages in use) after the whole download.
            try { Wavee.Backend.Modules.ModuleHost.Current?.Dispose(); }
            catch (Exception ex) { _log.Warn(LogCategory, "module host teardown before update failed", ex); }

            var result = await _updater.ApplyFromAppInstallerAsync(
                new Uri(FeedUrl),
                pct => Publish(entry with { State = AppUpdateState.Downloading, ProgressPercent = pct, Failure = null }),
                ct).ConfigureAwait(false);

            // REGISTERED is the only success. An HRESULT of 0 with nothing registered is what a deployment call that
            // never actually ran looks like (an async operation that completed Closed/Canceled, a seam that lost its
            // result), and treating it as "staged" published Installing over an install that had not happened — the
            // user then waited for a restart that never came. It is a failure, and it says so.
            if (result.IsRegistered)
            {
                _settings.Set(WaveeSettings.UpdateSnoozedVersion, "");
                // Windows terminates us next (ForceTargetAppShutdown) and relaunches via the restart registration the
                // updater made. Installing is therefore the LAST state this process ever publishes.
                Publish(entry with { State = AppUpdateState.Installing, ProgressPercent = 100, Failure = null });
                _log.Info(LogCategory, "staged " + (entry.TargetQuad ?? "") + "; restarting");
            }
            else if (result.HResult == 0)
            {
                _log.Warn(LogCategory, "deployment reported success but registered nothing: " + result.ErrorText);
                Fail(AppUpdateFailureKind.Unknown, 0,
                    "The installer reported success but registered nothing.",
                    entry.AutoUpdateAssociated, entry.LastCheckedMs);
            }
            else
            {
                var kind = MapFailure(PackageUpdateErrors.Classify(result.HResult));
                _log.Warn(LogCategory, "deployment failed 0x"
                    + result.HResult.ToString("X8", CultureInfo.InvariantCulture) + ": " + result.ErrorText);
                Fail(kind, result.HResult, result.ErrorText, entry.AutoUpdateAssociated, entry.LastCheckedMs);
            }
        }
        catch (OperationCanceledException)
        {
            Publish(entry with { State = AppUpdateState.Available, ProgressPercent = 0, Failure = null });
        }
        catch (Exception ex)
        {
            _log.Warn(LogCategory, "update apply failed", ex);
            Fail(AppUpdateFailureKind.Unknown, ex.HResult, ExceptionCode(ex),
                entry.AutoUpdateAssociated, entry.LastCheckedMs);
        }
    }

    // ── user gestures ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>"Later": this exact target stops shouting. A NEWER feed version is a different quad and is offered
    /// again — snooze is per-version, never a mute switch.</summary>
    public void Snooze()
    {
        if (Current.TargetQuad is not { Length: > 0 } quad) return;
        _settings.Set(WaveeSettings.UpdateSnoozedVersion, quad);
        Publish(Current with { State = AppUpdateState.Snoozed });
    }

    public void Acknowledge()
        => Publish(AppUpdateSnapshot.Idle with
        {
            AutoUpdateAssociated = Current.AutoUpdateAssociated,
            LastCheckedMs = Current.LastCheckedMs,
        });

    // ── plumbing ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One GET of the feed and one attribute read of the root <c>&lt;AppInstaller Version="…"&gt;</c>. Parsed
    /// with <see cref="XmlReader"/> rather than an object mapper: no reflection, AOT-clean, and it stops at the root
    /// element instead of materializing a document. A feed is untrusted input — DTDs and external entities are off.</summary>
    async Task<string?> ReadFeedVersionAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, FeedUrl);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            Async = true,
        };
        using var reader = XmlReader.Create(stream, settings);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            if (reader.NodeType != XmlNodeType.Element) continue;
            if (!string.Equals(reader.LocalName, "AppInstaller", StringComparison.Ordinal)) return null;
            return reader.GetAttribute("Version");
        }
        return null;
    }

    /// <summary>The deployment classifier and the app-facing failure kinds are the same taxonomy under two names (one
    /// lives in the OS-services library, one in the app's domain). An EXPLICIT switch rather than a name round-trip
    /// through <c>Enum.TryParse</c>: the round-trip allocated two strings per failure, silently re-bound itself if
    /// either enum were ever renamed, and answered <c>Unknown</c> for a mismatch instead of failing to compile. This
    /// switch is total in both directions and the compiler keeps it that way.</summary>
    internal static AppUpdateFailureKind MapFailure(PackageUpdateFailureKind kind) => kind switch
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

    /// <summary>What a BCL exception may contribute to a user-facing snapshot: its type and HRESULT, never its
    /// <c>Message</c>.
    /// <para>The publish leg runs with <c>UseSystemResourceKeys=true</c> (the NativeAOT size trim), which collapses
    /// every framework exception message to a bare resource key like <c>"Arg_InvalidOperationException"</c>. Putting
    /// that in a snapshot ships a string that is worse than useless: it looks like a diagnosis and is not one. The
    /// classified <see cref="AppUpdateFailureKind"/> already carries the meaning; this carries the two facts that
    /// survive the trim and that a bug report can act on. The FULL exception still goes to the log.</para></summary>
    internal static string ExceptionCode(Exception ex)
        => ex is null ? "" : ex.GetType().Name + " 0x" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture);

    void Fail(AppUpdateFailureKind kind, int hresult, string message, bool associated, long lastChecked, bool quiet = false)
        => Publish(new AppUpdateSnapshot(AppUpdateState.Failed, Current.TargetQuad, Current.TargetSemVer,
            Current.TargetCodename, 0, new AppUpdateFailure(kind, hresult, message ?? ""), associated, lastChecked, quiet));

    void Publish(AppUpdateSnapshot snapshot)
    {
        lock (_gate) { Current = snapshot; }
        _changed.OnNext(Interlocked.Increment(ref _rev));
    }
}
