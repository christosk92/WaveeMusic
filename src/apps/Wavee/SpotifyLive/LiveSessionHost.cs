using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Controls;
using FluentGpu.Localization;
using Wavee.Backend;
using Wavee.Backend.Audio;
using Wavee.Backend.Playlists;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Wavee.SpotifyLive.Audio;
using Wavee.SpotifyLive.Hydration;

namespace Wavee.SpotifyLive;

// ── Live session bootstrap — bring up Connect + playback and swap it into the running app ─────────────────────────────
// Logs in, opens the dealer + the persistent AP channel, builds the full LiveConnect stack, and calls svc.GoLive so the
// UI's PlaybackBridge (bound to the switchable facades) starts reflecting + controlling live playback — with NO UI rebuild.
// Returns null if login/dealer aren't available (the app keeps the in-memory fake backend).
//
// EVERY live install goes through `wiring` (Backend/Wiring/LiveWiring.cs) so it carries its own undo, and the whole
// go-live composition runs inside a rollback: see StartAsync's remarks + GoLiveAttempt at the bottom of this file. A
// bootstrap that throws part-way replays its ledger, disposes what it built, and drops only the handles it owns — a
// racing sibling that already won keeps its own.
public sealed class LiveSessionHost : IAsyncDisposable
{
    readonly LiveDealerTransport _transport;
    readonly LiveConnect _connect;
    readonly CancellationTokenSource _cts;
    readonly Wavee.Backend.Wiring.LiveWiring _wiring;
    Wavee.Backend.Realtime.DealerRouter? _router;
    Wavee.Backend.Sync.LibrarySync? _sync;
    IDisposable? _connSub;
    SpotifyFriendActivityService? _friends;
    SpotifyNotificationsService? _notifications;
    SpotifyWhatsNewService? _whatsNew;
    IDisposable? _homeCache;
    IDisposable? _rootlistWatch;
    Wavee.Backend.Hydration.HydrationPump? _hydrationPump;

    LiveSessionHost(LiveDealerTransport transport, LiveConnect connect, CancellationTokenSource cts,
                    Wavee.Backend.Wiring.LiveWiring wiring)
    { _transport = transport; _connect = connect; _cts = cts; _wiring = wiring; }

    /// <summary>THE go-live install ledger (design §2.6) — every live seam this bootstrap installed, each paired with the
    /// inverse that puts its OFFLINE value back. <c>Services.GoOffline</c> and <see cref="DisposeAsync"/> both replay it;
    /// it is idempotent, so the order of a logout (dispose-then-GoOffline) does not matter.</summary>
    public Wavee.Backend.Wiring.LiveWiring Wiring => _wiring;

    /// <summary>Register the sync-loop teardown handles (router → sync → connectivity subscription), disposed on logout
    /// BEFORE the transport so the loop stops recording against a torn-down socket.</summary>
    internal void AttachSync(Wavee.Backend.Realtime.DealerRouter router, Wavee.Backend.Sync.LibrarySync sync, IDisposable? connSub)
    { _router = router; _sync = sync; _connSub = connSub; }

    /// <summary>Register the session-scoped friend-activity (presence) feed, disposed on logout so its dealer/HTTP
    /// subscriptions + watchdog stop with the transport.</summary>
    internal void AttachFriends(SpotifyFriendActivityService friends) => _friends = friends;

    /// <summary>Register the session-scoped notification feeds (gander social + what's-new), disposed on logout so their
    /// in-flight fetches stop with the transport.</summary>
    internal void AttachNotifications(SpotifyNotificationsService notifications) => _notifications = notifications;
    internal void AttachWhatsNew(SpotifyWhatsNewService whatsNew) => _whatsNew = whatsNew;

    /// <summary>Register the session-scoped live Home cache, disposed on logout so its store-change watch (the Home
    /// feed epoch's second publisher) does not outlive the session that created it and accumulate one subscription per
    /// login.</summary>
    internal void AttachHomeCache(IDisposable homeCache) => _homeCache = homeCache;

    /// <summary>Register the session-scoped rootlist hydration watch, disposed on logout so its store-change
    /// subscription does not outlive the session / accumulate one per login.</summary>
    internal void AttachRootlistWatch(IDisposable watch) => _rootlistWatch = watch;

    /// <summary>Register the hydration background lane. Its token is already linked to this session's, so disposing it
    /// on logout is belt-and-braces — but it is what makes "no install without a teardown" true for the façade too.</summary>
    internal void AttachHydration(Wavee.Backend.Hydration.HydrationPump pump) => _hydrationPump = pump;

    public LiveConnect Connect => _connect;

    /// <summary>The MemoryGovernor arena name for the live session's audio body-disk cache — one const so Register and
    /// the wiring's Unregister cannot drift.</summary>
    const string AudioBodyDiskArena = "audio-body-disk";

    /// <summary>Cancelled on dispose (logout) — gates the background hydration / fetch tasks so they stop instead of
    /// running against the store after the user signed out.</summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>Bring a live session up, or answer null when there is nothing to bring up (no credential, a cancelled /
    /// superseded attempt, a Free account, no dealer host).
    ///
    /// <para><b>The failure path is a ROLLBACK, not a leak.</b> Everything from <c>new LiveWiring</c> onwards installs
    /// live seams into a process-wide <c>Services</c>, and the go-live block is long and full of things that can throw
    /// after the first install has landed — <c>transport.Start()</c>, the profile fetch, <c>store.Rootlist()</c>, a
    /// service constructor, and <c>wiring.AssertCovers</c> itself, which exists precisely to throw. Without this wrapper
    /// a throw at any of those points left every seam installed so far pointing at a half-built, never-started session:
    /// the app looked online, `svc.Wiring` held a ledger nobody would ever replay, and the user's obvious next move — hit
    /// "Log in" again — ran <c>AttachWiring</c> over that ledger and ORPHANED it, so those seams could not be undone even
    /// by a logout. <see cref="GoLiveAttempt"/> collects what this attempt built as it builds it, and the catch below
    /// replays the ledger, disposes the transports, and drops only the handles THIS attempt owns before rethrowing.</para>
    ///
    /// <para>"Only the handles this attempt owns" is the racing-sibling rule (WaveeApp: the device-code flow and the
    /// browser flow run at once on one shared ct). A loser that fails after the winner has already published its own
    /// host/ledger must not null out the winner's — hence the reference-equality guards in
    /// <c>Services.DetachWiring</c>/<c>DetachLive</c> rather than blind clears.</para></summary>
    public static async Task<LiveSessionHost?> StartAsync(Services svc, WaveeLogger log, CancellationToken ct,
        ILoginProgress? progress = null, bool interactive = true, bool useBrowser = false, bool quietPhases = false,
        Action<Action>? uiPost = null)
    {
        var attempt = new GoLiveAttempt(svc, new WaveeLogger(svc.Log, "wiring"));
        try
        {
            return await StartCoreAsync(svc, log, ct, attempt, progress, interactive, useBrowser, quietPhases, uiPost)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await attempt.RollbackAsync(ex).ConfigureAwait(false);
            throw;
        }
    }

    static async Task<LiveSessionHost?> StartCoreAsync(Services svc, WaveeLogger log, CancellationToken ct,
        GoLiveAttempt attempt,
        ILoginProgress? progress = null, bool interactive = true, bool useBrowser = false, bool quietPhases = false,
        Action<Action>? uiPost = null)
    {
        var report = progress ?? NullLoginProgress.Instance;
        // No dispatcher (the CLI demo / tests) ⇒ run the posted action inline, the same fallback the bridges use.
        Action<Action> postUi = uiPost ?? (static a => a());
        var adapter = new AuthStateAdapter(report, interactive, useBrowser, quietPhases);
        string op = "live-" + Guid.NewGuid().ToString("N")[..8];
        var connectLog = log.Sink is null ? new WaveeLogger(svc.Log, "connect") : log;
        var spclientLog = new WaveeLogger(svc.Log, "spclient");
        var metadataLog = new WaveeLogger(svc.Log, "metadata");
        var socialLog = new WaveeLogger(svc.Log, "social");
        var notificationsLog = new WaveeLogger(svc.Log, "notifications");
        var syncLog = new WaveeLogger(svc.Log, "sync");
        var dealerLog = new WaveeLogger(svc.Log, "dealer");
        svc.Log.Event(WaveeLogLevel.Info, "connect", "session.start", "Live session bootstrap starting",
            operationId: op,
            fields:
            [
                WaveeLogField.Of("interactive", interactive),
                WaveeLogField.Of("browser", useBrowser),
                WaveeLogField.Of("quiet", quietPhases),
            ]);

        // Silent resume with NO stored credential → Welcome, never the Error card (a null login is ambiguous between "no
        // credential" and "handshake failed"; this pre-check disambiguates the common first-run path).
        if (!interactive && !SpotifyLiveLogin.HasStoredCredential())
        {
            svc.Log.Event(WaveeLogLevel.Info, "auth", "silent.no_credential", "Silent resume skipped; no stored credential",
                operationId: op);
            report.Report(new LoginSnapshot(LoginPhase.LoggedOut));
            return null;
        }
        // quietPhases: a racing sibling (the browser button alongside the device code) stays silent on the intermediate
        // states so it can't replace the two-pane — it surfaces only Finalizing/Authenticated/PremiumRequired on success.
        if (!quietPhases) report.Report(new LoginSnapshot(!interactive ? LoginPhase.SilentResume : useBrowser ? LoginPhase.AwaitingBrowser : LoginPhase.RequestingCode));

        var live = await SpotifyLiveSpclient.ConnectAsync(connectLog, ct, retainApChannel: true,
            allowDeviceCode: interactive && !useBrowser, authObserver: adapter,
            onCredentialAcquired: () => report.Report(new LoginSnapshot(LoginPhase.Finalizing, Step: LoginStep.Connecting)),
            allowBrowser: interactive && useBrowser, language: svc.Locale.SpotifyLanguage).ConfigureAwait(false);
        if (live is null)
        {
            if (ct.IsCancellationRequested || quietPhases) return null;   // superseded / cancelled / a quiet racing sibling → stay silent
            // Welcome on a silent miss (no / rejected-and-cleared credential); Failed/Expired on a genuine error or lapsed code.
            svc.Log.Event(WaveeLogLevel.Warning, "connect", "session.login_failed", "Live login did not produce a session",
                operationId: op,
                fields: [WaveeLogField.Of("storedCredential", SpotifyLiveLogin.HasStoredCredential())]);
            report.Report(adapter.Terminal(credExisted: SpotifyLiveLogin.HasStoredCredential()));
            return null;
        }

        // Premium gate IN-APP (replaces the pre-window MessageBox): refuse a Free account here, and wipe the reusable blob
        // LoginAsync already persisted so the next launch can't silent-resume straight back into the wall.
        if (live.Session.Tier != Tier.Premium)
        {
            svc.Log.Event(WaveeLogLevel.Warning, "auth", "premium.required", "Signed-in account is not Premium",
                operationId: op,
                fields: [WaveeLogField.Of("tier", live.Session.Tier.ToString())]);
            live.CredStore?.Clear();
            report.Report(new LoginSnapshot(LoginPhase.PremiumRequired));
            live.ApChannel?.Dispose();
            return null;
        }

        var dealerJson = await SharedHttp.Client.GetStringAsync("https://apresolve.spotify.com/?type=dealer", ct).ConfigureAwait(false);
        var dealerHosts = ApResolver.ParseHosts(dealerJson, "dealer");
        if (dealerHosts.Count > 0)
            svc.Log.Event(WaveeLogLevel.Info, "dealer", "hosts.resolved", "Dealer access points resolved",
                operationId: op,
                fields: [WaveeLogField.Of("count", dealerHosts.Count), WaveeLogField.Of("first", dealerHosts[0])]);
        if (dealerHosts.Count == 0) { connectLog.Warn("no dealer host — live session not started"); if (!ct.IsCancellationRequested) report.Report(adapter.Terminal(credExisted: true)); live.ApChannel?.Dispose(); return null; }

        // The transport's token provider RE-MINTS on reconnect/expiry (not a captured constant). The WHOLE dealer host
        // list is passed (failover across hosts), and a Connectivity signal is driven by the socket lifecycle so a drop
        // shows in the UI as "Reconnecting…" (not silent stale playback) — surfaced via svc.Connectivity on go-live.
        // §G go-live marks: everything from here to `stack.state` is ONE synchronous block on the apresolve continuation —
        // no awaits — so any cost in it lands directly on the login splash. It was 5–27 s in the field and invisible,
        // because the only two log lines bracket the whole region. These per-step marks are permanent regression
        // detectors, not scaffolding: they are what names the next offender without a repro.
        long goLiveStart = Environment.TickCount64;
        report.Report(new LoginSnapshot(LoginPhase.Finalizing, Step: LoginStep.Metadata));

        var connectivity = new Connectivity();
        var transport = new LiveDealerTransport(dealerHosts, live.TokenProvider, live.Pipeline, () => live.BaseUrl, dealerLog, connectivity,
            forceRefreshToken: live.ForceTokenProvider);   // G6 — force-mint after a failed wss handshake
        long transportMs = Environment.TickCount64 - goLiveStart;
        attempt.Transport(transport);   // rollback handle: a throw before the host exists still has to stop this socket
        // Start the dealer socket NOW rather than after the WHOLE go-live stack (extended metadata, audio, every wiring
        // install) has built, as it used to. Start() only SCHEDULES the connect/receive loop
        // (LiveDealerTransport.Start: `_loop ??= Task.Run(...)`) and returns immediately, so this move costs nothing and
        // lets the socket be mid-handshake for the rest of this method instead of only beginning once everything else
        // already exists — the cache-first-shell fix's "stop putting things between the user and a usable session for
        // no reason" applied to the one piece here that was genuinely free to move.
        transport.Start();

        // Context resolution (inbound Connect play + UI play) needs the metadata stack to hydrate the resolved order, so
        // build it up front — over the SAME store the catalog reads — and hand the controller a unified context resolver.
        // (extendedMetadata + the etag cache are reused below for the façade's catalogue arm → one cache per session.)
        Wavee.Backend.Metadata.ExtendedMetadataSource? extendedMetadata = null;
        Wavee.Backend.Metadata.ExtensionEtagCache? extensionCache = null;
        IContextResolver? contexts = null;
        long extMetaMs = -1, extCacheMs = -1, contextsMs = -1;
        // THE hydration façade seam (design §3). It already exists — Services.CreateReal built it around the offline
        // hydrator — so everything constructed here can hold it NOW and get the live provider the moment SetInner
        // lands further down. That is the whole point of the switchable: no construction-order dance, no null seam.
        // REQUIRED, not coalesced: a null here means CreateReal skipped a seam it owns, and quietly substituting the
        // not-owned hydrator would ship a session where every open silently answers "Unsupported" (wiring-discipline).
        SwitchableEntityHydrator spotifyHydration = svc.SpotifyHydration
            ?? throw new InvalidOperationException("Services.CreateReal must build SpotifyHydration before go-live.");
        // ONE DOOR (P4): everything this session hands a hydrator to gets the ROUTER, never the Spotify switchable
        // directly. The router still lands a spotify: uri in exactly this switchable (StoreLibrarySource.Hydrator IS
        // it), so nothing about the Spotify path changes — but a MIXED batch (a queue holding a local import, a
        // playlist with a wavee:playlist: row, an episode) now reaches the source that actually owns each uri instead
        // of being reported Unsupported by the Spotify ladder. Capturing the switchable here was the last bypass.
        IEntityHydrator hydration = svc.Hydrator;
        // …and the store behind it, on the same terms: LiveSessionHost only ever runs on the REAL backend, so a null here
        // is CreateReal skipping a seam it owns, not a supported degraded mode.
        IStore liveStore = svc.RealStore
            ?? throw new InvalidOperationException("Services.CreateReal must build RealStore before go-live.");
        // …and the rest of the CreateReal-owned write lane, on exactly the same terms. These used to be re-probed at
        // each install site as `svc.X is { } x` / `svc.X?.SetInner(...)`, which reads like a supported degraded mode and
        // is not one: a null means CreateReal skipped a seam it owns, and the `?.` variants installed NOTHING while the
        // ledger still recorded the seam — so `AssertCovers` passed for a session whose mutation transport was never
        // pointed at the dealer and whose spclient base url stayed empty. Failing loud HERE, before the first install,
        // is what makes AssertCovers mean what it says (wiring-discipline).
        var mutTransport = svc.MutTransport
            ?? throw new InvalidOperationException("Services.CreateReal must build MutTransport before go-live.");
        var sessionHost = svc.RealSessionHost
            ?? throw new InvalidOperationException("Services.CreateReal must build RealSessionHost before go-live.");
        var mutationSource = svc.RealMutationSource
            ?? throw new InvalidOperationException("Services.CreateReal must build RealMutationSource before go-live.");
        var spclientBaseUrl = svc.RealSpclientBaseUrl
            ?? throw new InvalidOperationException("Services.CreateReal must build RealSpclientBaseUrl before go-live.");
        var playlistMutations = svc.RealPlaylistMutations
            ?? throw new InvalidOperationException("Services.CreateReal must build RealPlaylistMutations before go-live.");
        if (svc.RealStore is { } mdStore)
        {
            long t = Environment.TickCount64;
            extendedMetadata = new Wavee.Backend.Metadata.ExtendedMetadataSource(live.Pipeline, () => live.BaseUrl, () => live.Session);
            extMetaMs = Environment.TickCount64 - t; t = Environment.TickCount64;
            // O(1) since the bulk seed was deleted — the cold tier is now point-read per miss (HydrateFromCold).
            extensionCache = new Wavee.Backend.Metadata.ExtensionEtagCache(extendedMetadata, () => live.Session, connectLog,
                persistent: svc.RealCold);
            extCacheMs = Environment.TickCount64 - t; t = Environment.TickCount64;
            contexts = new LiveContextResolver(transport, hydration, mdStore, () => live.Session, connectLog);
            contextsMs = Environment.TickCount64 - t;
        }

        // Local audio (Stage H): wire the in-process decode/output stack when extended metadata can resolve file IDs.
        // PlayPlay is optional and supplied by the ignored Wavee.PlayPlay project when present.
        // Dedicated "audio" log category — persisted Info+ to wavee.log (WaveeLog special-cases it) so the whole
        // fetch→key→derive→decrypt pipeline is tailable/diagnosable in a windowed/AOT build with no console.
        var audioLog = new WaveeLogger(svc.Log, "audio");
        report.Report(new LoginSnapshot(LoginPhase.Finalizing, Step: LoginStep.Audio));
        long audioStart = Environment.TickCount64;
        AudioPlaybackStack? audio = extendedMetadata is not null
            ? new AudioPlaybackStack(transport, live.Pipeline, () => live.ApChannel, () => live.Session, extendedMetadata, svc.Settings, audioLog)
            : null;
        long audioMs = Environment.TickCount64 - audioStart;
        audioLog.Info(audio is not null
            ? "local-audio stack active in-process (file IDs via extended-metadata TRACK_V4/AUDIO_FILES)"
            : "local-audio stack OFF - no metadata store; playback stays remote-only");
        svc.Log.Event(WaveeLogLevel.Info, "audio", "stack.state", audio is not null ? "Local audio stack active" : "Local audio stack off",
            operationId: op,
            fields: [WaveeLogField.Of("active", audio is not null), WaveeLogField.Of("metadata", extendedMetadata is not null)]);
        // The go-live block's own budget. `elapsed` here IS the hosts.resolved → stack.state gap that used to have to be
        // reconstructed from two timestamps; the per-step fields say WHICH construction owns it.
        svc.Log.Event(WaveeLogLevel.Info, "connect", "golive.stack", "Go-live stack built",
            operationId: op, elapsedMs: Environment.TickCount64 - goLiveStart, fields:
        [
            WaveeLogField.Of("golive.transport_ms", transportMs),
            WaveeLogField.Of("golive.extmeta_ms", extMetaMs),
            WaveeLogField.Of("golive.extcache_ms", extCacheMs),
            WaveeLogField.Of("golive.contexts_ms", contextsMs),
            WaveeLogField.Of("golive.audio_ms", audioMs),
        ]);
        // Remember-volume: seed the device's announced/local volume from the persisted setting (0.7 default when off).
        double initialVolume = svc.Settings.Get(WaveeSettings.RememberVolume)
            ? Math.Clamp(svc.Settings.Get(WaveeSettings.SavedVolume), 0f, 1f) : 0.7;
        var connect = new LiveConnect(transport, live.DeviceId, live.ApChannel, hydration, liveStore,
            contexts, log: connectLog, audio: audio,
            initialVolume01: initialVolume, refreshTokens: live.TokenProvider, settings: svc.Settings);
        attempt.Connect(connect);   // …and the Connect stack, for the same window
        connect.Controller.AutoplayEnabled = () => svc.Settings.Get(WaveeSettings.AutoplayEnabled);
        // M0 — "one media, one host, one player": hand the controller the app-level video hooks (the per-track video predicate,
        // the async PopOutVideoSource handoff onto the player-owning FluentVideoMediaHost, the PlayerChanged → surface relay,
        // and the mid-track kind re-evaluation). All of it lives in LiveConnect.WireVideoMedia, wired unconditionally.
        // svc.Playback.ResolveVideoSource is wired later in GoLive — the hooks read it late-bound, at invoke time.
        // The user's local video-override curation rides the same hooks (open-failure recovery + the mp4-authoritative
        // duration); null on a backend built without a store, which leaves every override path unreachable.
        // ── THE go-live install ledger (design §2.6) ─────────────────────────────────────────────────────────────────
        // From here down NOTHING is written into `svc` or a process-wide plane except through `wiring`: every install hands
        // over its inverse in the SAME call, GoOffline/DisposeAsync replay those inverses in reverse order, and the
        // AssertCovers at the end of this method fails the login if a seam on Services.LiveSeams never registered one.
        // This is what replaced the hand-maintained GoOffline list that had silently drifted from this block
        // (metadata-entry-points-inventory.md §8.2 #18). It is handed to `svc` BEFORE the first install — earlier than
        // AttachLive — because the video-media hooks below land before the host object exists, and a bootstrap that fails
        // in that window must still be undoable.
        var wiring = attempt.BeginWiring();   // records the ledger for the rollback BEFORE the first install lands
        wiring.Set(Wavee.Backend.Wiring.LiveSeams.PlaybackVideoMedia,
            () => connect.WireVideoMedia(svc.Playback, svc.VideoOverrides),
            // The CONNECT half detaches itself in LiveConnect.Dispose (the PlayerChanged / DurationKnown relays); what
            // outlives it is the hook this installed ON THE BRIDGE, which would otherwise keep calling into a disposed
            // LiveConnect after logout.
            () => svc.Playback.RequestMediaKindRefresh = null);
        // ── Part 7.3 — the module→host services the Spotify playback module needs ───────────────────────────────────
        // host/auth/token, host/auth/context and spotify/audioKey answer from THIS session (the live spclient handle
        // plus the AP socket LiveConnect owns), so they are installed here and taken back off at logout. Registered
        // unconditionally — the conditional is inside the install — so the roster stays covered on a build with no
        // module host at all.
        var moduleHostServices = Wavee.Backend.Modules.ModuleHost.Current?.Services;
        var spotifyHostSession = new SpotifyLiveHostSession(
            (force, c) => force && live.ForceTokenProvider is { } forceMint ? forceMint(c) : live.TokenProvider(c),
            () => SpotifyLiveHostSession.ContextOf(live.DeviceId, live.ClientToken, live.BaseUrl, live.Session),
            live.ApChannel is { } ap ? ap.RequestAudioKeyAsync : SpotifyLiveHostSession.NoApChannel);
        wiring.Set(Wavee.Backend.Wiring.LiveSeams.ModuleHostServices,
            () => { if (moduleHostServices is not null) LiveConnect.RegisterModuleHostServices(moduleHostServices, spotifyHostSession); },
            () => { if (moduleHostServices is not null) LiveConnect.UnregisterModuleHostServices(moduleHostServices); });
        // transport.Start() now runs right after the transport is constructed (see above) — it used to sit here, after
        // the ENTIRE go-live stack was already built, which bought it nothing (Start() only schedules a background Task
        // and returns) but read as though the dealer socket waited on everything above it.
        //
        // Profile (name + avatar) is fetched in the BACKGROUND now — never awaited on the path to go-live. It used to
        // be a REST round trip sitting directly between "we have a session" and "the shell can show it", even though
        // nothing downstream needs it to proceed: LiveSpotifySession below falls back to the bare username, and the
        // profile chip reads PlaybackBridge.User — a signal this can update the moment the fetch actually lands, with
        // no rebuild. It still runs through the SAME port every other owner goes through (SpotifyUserProfileFetch), not
        // a private copy of the parser, and the answer still lands in the store as an ordinary Owner row.
        report.Report(new LoginSnapshot(LoginPhase.Finalizing, Step: LoginStep.Profile));
        string profileAccount = live.Username;
        var liveSession = new LiveSpotifySession(live.Username, live.Session.Tier == Tier.Premium);

        // Owned CTS — INDEPENDENT of the bootstrap `ct` (a racing-sibling cancel must not kill hydration); cancelled on
        // logout. Declared here (rather than immediately before `host`, as before) because the deferred profile fetch
        // below needs this SAME independence: `ct` is the interactive dual-race's shared token, and the WINNING side
        // cancels it the instant it returns (WaveeApp's RestartCode/StartBrowser) — a fetch still in flight at that
        // moment would be aborted before it ever reaches PlaybackBridge.User. `cts` has no such cancel-on-success edge.
        var cts = new CancellationTokenSource();
        if (UserProfileIds.Normalize(profileAccount) is { } me)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var meFetch = new Wavee.SpotifyLive.Hydration.SpotifyUserProfileFetch(
                        reader: null, live.Pipeline, () => live.BaseUrl, socialLog);
                    var resolved = await meFetch.ResolveAsync([me], cts.Token).ConfigureAwait(false);
                    if (!resolved.TryGetValue(me, out var owner) || owner is null) return;
                    svc.RealStore?.UpsertOwner(owner);
                    postUi(() =>
                    {
                        string name = owner.Name is { Length: > 0 } n ? n : profileAccount;
                        // Fold onto the SESSION first. This fetch is deliberately off the go-live path, which means it
                        // routinely resolves BEFORE `svc.GoLive` swaps `liveSession` in — and GoLive's swap re-publishes
                        // CurrentUser onto the chip. Writing only the signal therefore lost the name twice over: the
                        // pre-swap guard below dropped it, and had it passed, the swap would have overwritten it with
                        // the bare account id `liveSession` was constructed with. Enriching the session makes the swap
                        // publish the resolved profile instead, whichever side of it we land on.
                        liveSession.UpdateProfile(name, owner.Avatar?.Url);
                        // Publish onto the LIVE profile chip only if this is still the active account — a fast
                        // logout/re-login (or a losing racing sibling) must not stomp a different session's name/avatar
                        // with a stale fetch that resolves late. Before the swap there is nothing to publish TO: the
                        // enrichment above is what carries the name across.
                        if (!string.Equals(svc.Session.CurrentUser?.Id, profileAccount, StringComparison.Ordinal)) return;
                        svc.Playback.User.Value = liveSession.CurrentUser;
                    });
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { socialLog.Info("login profile: " + ex.Message); }
            }, cts.Token);
        }
        var host = new LiveSessionHost(transport, connect, cts, wiring);
        attempt.Built(host);   // from here a rollback disposes the HOST (which tears the transports down in order)
        audio?.StartProvisioning(cts.Token);   // background PlayPlay pack provision — off the play path, owned CTS

        // The local-audio stack's APP-LEVEL handles. Registered unconditionally (the conditional is inside the install)
        // so the roster is covered even on a build with no audio stack, and so a logout drops the session's disk caches
        // instead of leaving the settings/diagnostics surfaces holding a dead provisioner.
        wiring.Set(Wavee.Backend.Wiring.LiveSeams.PlayPlayProvisioner,
            () => svc.PlayPlayProvisioner = audio?.Provisioner, () => svc.PlayPlayProvisioner = null);
        wiring.Set(Wavee.Backend.Wiring.LiveSeams.AudioBodyCache,
            () => svc.AudioBodyCache = audio?.BodyDiskCache, () => svc.AudioBodyCache = null);
        wiring.Set(Wavee.Backend.Wiring.LiveSeams.AudioLicenseCache,
            () => svc.AudioLicenseCache = audio?.LicenseDiskCache, () => svc.AudioLicenseCache = null);
        // …and its sheddable arena. The governor's list is process-lifetime, so a login/logout cycle that only ever
        // Registers leaves one closure over a dead cache behind per login — hence Unregister as the inverse.
        wiring.Set(Wavee.Backend.Wiring.LiveSeams.AudioBodyDiskArena,
            () => { if (audio?.BodyDiskCache is { } bodyCache) svc.Residency.Register(3, AudioBodyDiskArena, () => bodyCache.Trim()); },
            () => svc.Residency.Unregister(AudioBodyDiskArena));
        // The local-audio runtime/provisioning status feed. Registered unconditionally (the conditional is inside the
        // install) so the roster is covered on a build with no audio stack too. It USED to be a bare `+=` with no seam
        // name and no inverse: the handler died with the stack, but the STATUS it had pushed stayed on the bridge, so a
        // logout left the setup banner and its "Set up" action offering to provision a runtime for a session that no
        // longer existed. The inverse detaches the handler AND puts the signal back to NotApplicable — the same
        // named-offline-value rule every other seam follows (wiring-discipline).
        Action? runtimeStatusHandler = null;
        wiring.Set(Wavee.Backend.Wiring.LiveSeams.PlaybackRuntimeStatus,
            () =>
            {
                if (audio is null) return;
                var stack = audio;
                runtimeStatusHandler = () => svc.Playback.UpdateRuntimeStatus(stack.Provisioner.GetSnapshot(), uiPost);
                stack.Status.Changed += runtimeStatusHandler;
                runtimeStatusHandler();   // seed the current snapshot before the first render
            },
            () =>
            {
                if (audio is not null && runtimeStatusHandler is not null) audio.Status.Changed -= runtimeStatusHandler;
                runtimeStatusHandler = null;
                svc.Playback.UpdateRuntimeStatus(PlaybackRuntimeStatus.NotApplicable, uiPost);
            });

        // Supersede check: a newer login cancels THIS bootstrap's ct. Bail (disposing what we built) so a stale flow can't
        // AttachLive/GoLive over the winner. No await between here and GoLive → effectively atomic. AttachLive runs BEFORE
        // GoLive so a logout fired in the go-live window still tears the host down (not a no-op).
        // Through the SAME rollback the failure path uses, not a bare DisposeAsync: disposing the host replays the
        // ledger but leaves `svc.Wiring` pointing at it, and the winner's AttachWiring would then orphan a spent ledger.
        if (ct.IsCancellationRequested)
        {
            await attempt.RollbackAsync(new OperationCanceledException(ct)).ConfigureAwait(false);
            return null;
        }
        wiring.Set(Wavee.Backend.Wiring.LiveSeams.LiveHost,
            () => svc.AttachLive(host, live.CredStore!), () => svc.DetachLive(host));
        wiring.Set(Wavee.Backend.Wiring.LiveSeams.LiveHttp,
            () => svc.LiveHttp = live.Pipeline, () => svc.LiveHttp = null);   // the pipeline carries this session's auth
        // Point the switchable mutation transport at the live dealer BEFORE go-live so a write in the go-live window networks;
        // on logout it goes back to the inert stub, so writes queue in the durable outbox and replay on next login (§2.1).
        wiring.Set(Wavee.Backend.Wiring.LiveSeams.MutTransport,
            () => mutTransport.SetInner(transport),
            () => mutTransport.SetInner(new StubTransport()));
        // Set the real username into the ambient session so write bodies carry a valid account (§3) — and clear it on
        // logout, so an outbox drained while signed out cannot address the previous account.
        wiring.Set(Wavee.Backend.Wiring.LiveSeams.SessionAccount,
            () => sessionHost.Set(sessionHost.Current with { Account = live.Username }),
            () => sessionHost.Set(sessionHost.Current with { Account = "" }));
        // Lyrics search needs the FULL row (artist + ISRC) even when the cluster's is thin — the same Open rung the
        // player bar asks for, through the same façade, then read back from the store.
        var lyrics = BuildLiveLyrics(() => live.BaseUrl, connect.Controller, live.TokenProvider,
            async (uri, c) =>
            {
                await hydration.EnsureAsync(uri, HydrationLevel.Open,
                    new HydrationOptions(Surface: TraitSurface.NowPlaying, Priority: 1), c).ConfigureAwait(false);
                return svc.RealStore?.GetTrack(uri);
            });
        // Local (silent) playback is unsupported: any play that routes to THIS device shows the standard "choose a remote
        // device" toast instead of pretending to play. The hook can fire from a dealer thread — NotifyLocalPlaybackUnsupported
        // posts to the UI thread. (The --connect-live CLI demo never Activates the bridge, so the notify no-ops there.)
        // Reject local playback ONLY when there's no local-audio stack (remote-only). With the stack wired, a play routed
        // to THIS device actually decodes/outputs in process instead of showing the "choose a remote device" toast.
        if (audio is null)
            connect.Controller.OnLocalPlaybackRejected = () => svc.Playback.NotifyLocalPlaybackUnsupported();
        // A failing transfer / play to the active remote device surfaces as a toast (was silent) — so "switching doesn't work"
        // shows a reason instead of nothing. The controller also logs the HTTP status (grep "outbound transfer"/"outbound play").
        connect.Controller.OnRemoteCommandFailed = () => svc.Playback.NotifyRemoteCommandFailed();
        // A LOCAL play that fails (key/CDN/decode/provisioning) surfaces a typed toast + player-bar error with a Retry that
        // re-provisions the pack (if needed) and replays the current track — instead of a silently-dropped fire-and-forget.
        connect.Controller.OnPlaybackError = e =>
        {
            svc.Log.Event(WaveeLogLevel.Error, "audio", "playback.failed", "Local playback failed",
                operationId: op,
                fields:
                [
                    WaveeLogField.Of("reason", e.Reason.ToString()),
                    WaveeLogField.Of("detail", e.Detail ?? ""),
                ]);   // the structured Event above reaches ring + file (no plain-text duplicate)
            // When the failure is "no local runtime" (nothing to retry into), route the toast action to the one-click
            // SETUP flow instead of a Retry that would just replay and fail again. Also surface the persistent banner by
            // pushing the RuntimeUnavailable status (so the offer isn't a one-shot toast the user can miss).
            bool needsSetup = e.Reason is AudioKeyFailureReason.NeverProvisioned
                or AudioKeyFailureReason.ProvisioningUnavailable
                or AudioKeyFailureReason.ArchUnsupported;
            if (needsSetup && audio is not null)
            {
                var snap = audio.Provisioner.GetSnapshot();
                if (snap.Outcome is ProvisioningOutcome.Ready or ProvisioningOutcome.NeverAttempted)
                    snap = new PlaybackRuntimeStatus(ProvisioningOutcome.RuntimeUnavailable);
                svc.Playback.UpdateRuntimeStatus(snap, uiPost);
                // The wizard's own Local playback page IS this ask (same predicate as the go-live toast above) — a
                // toast AND a player-bar status line repeating "needs a one-time setup" while the user is looking at
                // exactly that page reads as the same ask made twice. Suppressed, this play attempt neither re-arms
                // the dismissed banner flag (there is no banner to re-offer under the wizard) nor publishes the
                // toast/status-line text; UpdateRuntimeStatus above still runs so the wizard's own page reflects it.
                if (!SetupGating.SuppressesRuntimePrompts(SetupGating.IsPending(svc.Settings), SetupSession.Current is not null))
                {
                    svc.Settings.Set(WaveeSettings.PlaybackRuntimeSetupDismissed, false);   // re-offer after an explicit play attempt
                    svc.Playback.NotifyPlaybackError(e.UserMessage, Loc.Get(Strings.Playback.Runtime.SetUp),
                        () => svc.Playback.OpenPlaybackRuntimeSetup.Value++);
                }
            }
            else
            {
                svc.Playback.NotifyPlaybackError(e.UserMessage, Loc.Get(Strings.Common.Retry),
                    () =>
                    {
                        if (e.Reason != AudioKeyFailureReason.Network) audio?.StartProvisioning(cts.Token);
                        _ = connect.Controller.RetryCurrentAsync();
                    });
            }
        };
        // Output-device control + local-output picker (Phase A/B/C). Only when the audio stack is wired (local playback is
        // real): seed the persisted output BEFORE first play, surface device notices as toasts, reflect Windows session
        // volume/mute, and stand up the main-process picker service (its own device monitor, separate from the child's).
        // Through LiveConnect.OutputDeviceControl, never audio.Host directly: that composite is what also carries mute to the
        // video host, so muting the app keeps a music video muted too (and a video that starts later opens muted).
        LocalAudioDeviceService? localOutputs = null;
        wiring.Set(Wavee.Backend.Wiring.LiveSeams.PlaybackLocalOutputs, () =>
        {
            if (audio is not null && connect.OutputDeviceControl is { } odc)
            {
                var persistedId = svc.Settings.Get(WaveeSettings.OutputDeviceId);
                string? seedId = string.IsNullOrEmpty(persistedId) ? null : persistedId;
                odc.SetOutputDevice(seedId);   // seed the selected endpoint before the first play (Hello carries it OOP)
                odc.OutputDeviceNotice += n => svc.Playback.NotifyOutputDeviceNotice(n);
                odc.ExternalVolumeChanged += (v, muted) =>
                {
                    connect.Controller.OnExternalVolumeChanged(v);
                    svc.Playback.NotifyOutputMuted(muted);
                };
                localOutputs = new LocalAudioDeviceService(
                    new Wavee.SpotifyLive.Audio.WasapiAudioDeviceMonitor(audioLog),
                    odc,
                    (id, ct) => connect.Controller.TransferToAsync(id, ct),
                    live.DeviceId,
                    () => connect.Controller.State.ActiveDeviceId,
                    (id, name) =>
                    {
                        svc.Settings.Set(WaveeSettings.OutputDeviceId, id ?? "");
                        svc.Settings.Set(WaveeSettings.OutputDeviceName, name ?? "");
                    },
                    seedId);
                svc.Playback.AttachLocalOutputs(localOutputs);
                // …and let the CONNECT wire name the endpoint we render to (desktop publishes it on every PutState).
                connect.AudioOutputDeviceName = localOutputs.CurrentOutputDeviceName;
                localOutputs.Activate(postUi);
            }
        },
        // The picker service owns a WASAPI device monitor and holds the session's controller — both die with the session,
        // so the bridge drops it and the service is disposed rather than left monitoring endpoints for a dead login.
        () =>
        {
            svc.Playback.AttachLocalOutputs(null);
            localOutputs?.Dispose();
            localOutputs = null;
        });
        // The picker's local rows are truthful/enabled iff local playback is actually supported (an audio stack exists) —
        // fixes the stale unconditional "Unavailable" (OnLocalPlaybackRejected is only wired when audio is null).
        wiring.Set(Wavee.Backend.Wiring.LiveSeams.PlaybackLocalPlaybackSupported,
            () => postUi(() => svc.Playback.LocalPlaybackSupported.Value = audio is not null),
            () => postUi(() => svc.Playback.LocalPlaybackSupported.Value = false));
        // The last step lands BEFORE GoLive: GoLive flips AuthStatus.Authenticated, which unmounts the splash, so a report
        // after it would never be seen. This one gets the checkmarks on screen for the frame before the shell takes over.
        report.Report(new LoginSnapshot(LoginPhase.Finalizing, Step: LoginStep.Done));
        svc.GoLive(connect.Controller, connect.Devices, liveSession, connectivity, lyrics, wiring);
        // Diagnostic one-shot: WAVEE_PLAYPLAY_PROBE=1 (or a file-id hex) fetches that file's PlayPlay obf+aes on the LIVE
        // session and compares to the reference ogg-vorbis-160 golden vector — isolates "is our live obf the vector's value".
        if (audio is not null && Environment.GetEnvironmentVariable("WAVEE_PLAYPLAY_PROBE") is { Length: > 0 } probe)
            _ = ProbePlayPlayAsync(audio, probe, audioLog, cts.Token);
        // Diagnostic one-shot: WAVEE_AUDIO_FORMAT_PROBE=1 plus WAVEE_AUDIO_FORMAT_PROBE_TRACK=<track-uri-or-base62>
        // resolves exactly one track and lets AudioFormatProbe log every exposed audio candidate, CDN prefix, preview MP3,
        // and music-video DRM manifest without requiring a UI play action.
        if (audio is not null && Environment.GetEnvironmentVariable("WAVEE_AUDIO_FORMAT_PROBE_TRACK") is { Length: > 0 } formatProbe)
            _ = ProbeAudioFormatsAsync(audio, formatProbe, audioLog, cts.Token);
        report.Report(new LoginSnapshot(LoginPhase.Authenticated, User: liveSession.CurrentUser));
        // The setup wizard's own Local playback page IS the runtime prompt — while the wizard is armed/open, a toast
        // repeating the same ask would just be noise (SetupSession.Current is not null while an instance is live;
        // IsPending covers the moment BEFORE it opens too, e.g. the app is still on its first painted frame).
        if (audio is not null && !svc.Settings.Get(WaveeSettings.PlaybackRuntimeSetupDismissed)
            && !SetupGating.SuppressesRuntimePrompts(SetupGating.IsPending(svc.Settings), SetupSession.Current is not null))
        {
            var snap = audio.Provisioner.GetSnapshot();
            if (snap.Outcome == ProvisioningOutcome.RuntimeUnavailable)
            {
                void ShowSetupToast() => Toast.Show(
                    Loc.Get(Strings.Playback.Runtime.Missing),
                    new ToastOptions
                    {
                        Severity = InfoBarSeverity.Warning,
                        ActionLabel = Loc.Get(Strings.Playback.Runtime.SetUp),
                        OnAction = () => svc.Playback.OpenPlaybackRuntimeSetup.Value++,
                    });
                if (uiPost is { } post) post(ShowSetupToast);
            }
        }
        log.Info("Live Connect session active — Wavee is a controllable device, mirrors now-playing, and shows the live account.");

        // Live data wiring into the SAME store the catalog reads (InMemoryStore is lock-guarded → safe off-thread):
        if (svc.RealStore is { } store && extendedMetadata is { } em && extensionCache is { } xmCache)
        {
            // ── THE session's extended-metadata read tier (design §2.4/§2.5) ─────────────────────────────────────────
            // ONE negative memo and ONE reader for the whole session, built FIRST because everything below either
            // projects through the trait pipeline or reads through the reader, and both have to share the "no" — a
            // credits drawer that learns a track has no kind-186 must stop the row pass re-asking, and vice versa. This
            // is what replaced the six per-service memos and the seven etag-or-raw copies.
            var negatives = new Wavee.Backend.Hydration.NegativeMemo();
            var xmReader = new Wavee.Backend.Hydration.ExtensionReader(xmCache, negatives, metadataLog.With("hydration.reader"));

            // (a) fetch playlist/album TRACKS the first time a detail page opens (the sync stored headers only). The
            //     hydration façade replaces the no-op that left lists empty. em + the etag cache were built above for
            //     the context resolver — reuse them so the whole session shares one cache.
            // The fetchers' hydrate delegate is Wavee.Backend.Hydration.MembershipHydration — the façade at IDENTITY
            // for rows a membership diff/snapshot just adopted, PLUS a companion trait ask for those SAME rows,
            // attributed to THIS caller's real surface (never TraitSurface.None — see that type's remarks for why a
            // shared None surface here silently starved PLAYS/BPM·KEY for every playlist and saved set).
            var hydratePlaylistMembers = Wavee.Backend.Hydration.MembershipHydration.For(hydration, TraitSurface.PlaylistOpen);
            var hydrateCollectionMembers = Wavee.Backend.Hydration.MembershipHydration.For(hydration, TraitSurface.LikedSongs);
            // onRevisionChanged: the save-count cache's only invalidation hook — a genuinely new revision landing on
            // the OPEN playlist plane is the "this playlist's identity may have moved" signal it has none of its own.
            var fetcher = new PlaylistFetcher(live.Pipeline, () => live.BaseUrl, store, hydratePlaylistMembers, () => live.Username,
                onRevisionChanged: svc.PlaylistPopcount.Invalidate);
            // The recents page's list read (/playlist/v2/list/recents/page[/diff]) — the same pipeline + baseUrl seam as
            // the playlist fetcher, installed into the switchable identity the page binds to for the whole session. It is
            // STATELESS (no revision, no rows), so nothing here has to be torn down beyond the GoOffline Reset().
            wiring.Set(Wavee.Backend.Wiring.LiveSeams.Recents,
                () => svc.Recents.SetInner(new RecentsFetcher(live.Pipeline, () => live.BaseUrl)),
                () => svc.Recents.Reset());

            // The single library-sync writer loop (RC1): the collection fetcher (WIRE-set-keyed sync tokens get/set → the
            // SQLite cold tier; snapshots verified by the ledger, mark-and-sweep shielded by the mutation outbox + the recency
            // window, drift reconciled off a shadow walk), the loop itself, and the dealer router that decode-and-enqueues
            // into it. The DealerRouter no longer writes the store — the in-place apply / mark-dirty / refetch policy is the loop's.
            var cold = svc.RealCold!;
            var collections = new Wavee.Backend.Collections.CollectionFetcher(live.Pipeline, () => live.BaseUrl, () => live.Username, store,
                s => cold.GetCollectionRevision(s),
                (s, r) => cold.SetCollectionRevision(s, r, DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
                hydrateCollectionMembers,   // the ladder's own ref-closure post-step closes blank AlbumRefs / thin tracks (design §2.3)
                (s, u) => svc.RealMutations!.HasPending(s, u),
                syncLog);                   // collection.snapshot.* / collection.reconcile.* / collection.token.reset land in the sync log
            var signalClient = new Wavee.Backend.Playlists.PlaylistSignalsClient(
                live.Pipeline, () => live.BaseUrl, () => live.Session.Locale);
            var sync = new Wavee.Backend.Sync.LibrarySync(store, fetcher, collections, svc.RealMutations!, svc.RealResyncQueue!, mutTransport,
                () => sessionHost.Current, () => live.Username, syncLog, cts.Token, svc.EchoRing, signalClient);
            var router = new Wavee.Backend.Realtime.DealerRouter(transport, sync);
            wiring.Set(Wavee.Backend.Wiring.LiveSeams.RealSync, () => svc.RealSync = sync, () => svc.RealSync = null);
            // Through postUi like every other off-thread bridge write: PlaylistTuning is a UI-thread Signal, the INSTALL
            // runs on whatever continuation the bootstrap landed on, and the INVERSE runs on whatever thread called
            // GoOffline (LogoutAsync awaits Session.LogoutAsync with ConfigureAwait(false), so that is a pool thread).
            // A signal written off the UI thread races the reconciler's subscriber list.
            wiring.Set(Wavee.Backend.Wiring.LiveSeams.PlaylistTuning,
                () => postUi(() => svc.PlaylistTuning.Value = sync),
                () => postUi(() => svc.PlaylistTuning.Value = null));
            sync.Enqueue(new Wavee.Backend.Sync.SyncCommand(Wavee.Backend.Sync.SyncKind.DrainWrites));      // replay writes queued while logged out
            var hydrated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // Addendum A7 — InitialHydrate waits for the cold tier's WARM pass. It rewrites the saved sets and the
            // rootlist wholesale; running it against a still-empty hot tier makes every fold a cache MISS and refetches a
            // library that is already on disk. DrainWrites deliberately does NOT wait: local intent must send promptly,
            // and it touches the outbox, not the entity cache. WarmComplete is guaranteed to complete even when the warm
            // pass FAILS (Wave B's try/finally), so no timeout is needed to avoid a wedge.
            var hydrate = new Wavee.Backend.Sync.SyncCommand(Wavee.Backend.Sync.SyncKind.InitialHydrate, Done: hydrated);
            if (store is Wavee.Backend.Persistence.CachedStore warmStore && !warmStore.WarmComplete.IsCompleted)
                _ = Task.Run(async () =>
                {
                    try { await warmStore.WarmComplete.ConfigureAwait(false); } catch (Exception) { }
                    sync.Enqueue(hydrate);
                });
            else
                sync.Enqueue(hydrate);
            // The warm-up wave AFTER the saved sets land: Liked's members, then the saved artists (whose Open rung IS
            // their assembled discography) and the saved albums. All three are ONE kind of request now — a background,
            // lowest-priority ask on the façade — replacing PagedHydrateAsync's own loop and DiscographyPrefetcher's
            // three-wave scheduler with the pump, which a logout cancels wholesale.
            _ = Task.Run(async () =>
            {
                try
                {
                    await hydrated.Task.ConfigureAwait(false);
                    _ = hydration.EnsureAsync("spotify:collection:tracks", HydrationLevel.Open, HydrationOptions.Prefetch, cts.Token);
                    _ = hydration.EnsureManyAsync(store.SavedUris("artists"), HydrationLevel.Open, HydrationOptions.Prefetch, cts.Token);
                    _ = hydration.EnsureManyAsync(store.SavedUris("albums"), HydrationLevel.Open, HydrationOptions.Prefetch, cts.Token);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { syncLog.Info("library prefetch failed: " + ex.Message); }
            });
            // Reconnect resync (§6.2): on a transition back to Online from a drop, run the ordered convergence pass —
            // drain the outbox FIRST (a like made during the gap sends), then rootlist + token-gated deltas + /diff for
            // the open/dirty resident playlists. Pushes during the gap died with the socket; this pass is the recovery.
            var prevStatus = connectivity.Status;
            IDisposable connSub = connectivity.StatusChanged.Subscribe(Observers.From<Wavee.Core.ConnectionStatus>(s =>
            {
                var prev = prevStatus; prevStatus = s;
                if (s == Wavee.Core.ConnectionStatus.Online && (prev == Wavee.Core.ConnectionStatus.Reconnecting || prev == Wavee.Core.ConnectionStatus.Offline))
                    sync.Enqueue(new Wavee.Backend.Sync.SyncCommand(Wavee.Backend.Sync.SyncKind.ReconnectResync));
            }));
            host.AttachSync(router, sync, connSub);
            // Post-write drains route through the loop (§6 hardening): replay/reconcile serializes with inbound diffs
            // instead of racing them from the caller's thread. GoOffline resets this to inline-drain.
            wiring.Set(Wavee.Backend.Wiring.LiveSeams.MutationScheduleDrain,
                () => mutationSource.ScheduleDrain = () => sync.Enqueue(new Wavee.Backend.Sync.SyncCommand(Wavee.Backend.Sync.SyncKind.DrainWrites)),
                () => mutationSource.ScheduleDrain = null);   // back to inline drains - the loop dies with the host
            wiring.Set(Wavee.Backend.Wiring.LiveSeams.SpclientBaseUrl,
                () => spclientBaseUrl.Value = live.BaseUrl,
                () => spclientBaseUrl.Value = "");            // no spclient until the next go-live
            wiring.Set(Wavee.Backend.Wiring.LiveSeams.PlaylistMutationsHttp,
                () => playlistMutations.SetHttp(live.Pipeline),
                // Drop the live (session-bound auth) pipeline with the host; the bare exchange is the offline
                // stand-in CreateReal constructed this source with.
                () => playlistMutations.SetHttp(new HttpClientExchange()));
            wiring.Set(Wavee.Backend.Wiring.LiveSeams.PlaylistMutationsScheduleDrain,
                () => playlistMutations.ScheduleDrain = ct => sync.DrainWritesAsync(ct),
                () => playlistMutations.ScheduleDrain = null);

            // Pathfinder (GraphQL) for rich catalog reads with no protobuf equivalent — the artist overview, on open.
            var pathfinderExchange = new HttpPipeline(
                new HttpClientExchange(HttpPools.Get(HttpPool.ControlPlane)),
                new AuthMiddleware((force, c) => force && live.ForceTokenProvider is { } refresh
                    ? refresh(c)
                    : live.TokenProvider(c)),
                new RateLimitMiddleware(),
                new PathfinderHeadersMiddleware(_ => Task.FromResult(live.ClientToken), live.Session.Locale));
            var pathfinder = new PathfinderClient(pathfinderExchange, spclientLog);
            var pathfinderResource = new PathfinderResource(pathfinder, () => live.Session, spclientLog);
            // Concert discovery (artist schedules, hub feed, location controls) — the live Pathfinder adapter over the same
            // resource, installed into the switchable the concert pages hold. Reset to the Null service on GoOffline.
            wiring.Swap<IConcertService>(Wavee.Backend.Wiring.LiveSeams.Concerts, svc.Concerts.SetInner,
                new SpotifyConcertService(pathfinderResource), static () => new NullConcertService());
            // Browse: the category directory + category pages, cached by the shared Pathfinder resource TTLs.
            wiring.Set(Wavee.Backend.Wiring.LiveSeams.Browse,
                () => svc.Browse.SetInner(new SpotifyBrowseService(pathfinderResource, spclientLog)),
                () => svc.Browse.Reset());
            // Home's "Show all" axis — the REAL homeSection operation, over the same resource (and the same persisted
            // document as `home` itself). A separate seam from Browse: a spotify:section: URI is a HOME resource.
            wiring.Set(Wavee.Backend.Wiring.LiveSeams.HomeSections,
                () => svc.HomeSections.SetInner(new SpotifyHomeSectionService(pathfinderResource, spclientLog)),
                () => svc.HomeSections.Reset());
            // Expanded-row drawer data (kinds 98/99 associations + kind 5 audio formats), fetched on expand only.
            wiring.Set(Wavee.Backend.Wiring.LiveSeams.TrackExpansion,
                () => svc.TrackExpansion.SetInner(new SpotifyTrackExpansionService(xmReader, store, metadataLog)),
                () => svc.TrackExpansion.Reset());
            // The cover-colour plane's universal feed. Everything that shows art — grids, shelves, heroes, editorial
            // cards, track rows — resolves its colour from the plane, and a miss enqueues the IMAGE here, so no surface
            // has to remember to prefetch its own tints. Kind 179 fills the same plane for free from the row bundle.
            // The offline value is NULL by the plane's OWN contract, not a no-op delegate: CoverColorPlane skips
            // enqueueing entirely when the Filler is null (CoverColorPlane.cs:330), so a None filler would make
            // every miss queue and drain a batch that can only answer nulls. Null IS the plane's named
            // no-filler-installed state.
            wiring.Set(Wavee.Backend.Wiring.LiveSeams.CoverColorFiller,
                () => CoverColorPlane.Current.Filler = CoverColorFiller.Create(pathfinderResource, spclientLog),
                () => CoverColorPlane.Current.Filler = null);
            // Featured-card hover peek: the batched feedBaselineLookup preview-track cache (display-only, no Store).
            wiring.Set(Wavee.Backend.Wiring.LiveSeams.HomeBaselinePreviews,
                () => HomeBaselinePreviews.Install(pathfinderResource),
                () => HomeBaselinePreviews.Uninstall());   // the resource carries this session's auth; the previews are this account's
            // "What's New" feed (queryWhatsNewFeed) — display-only, rides the PathfinderResource TTL. Seeded now so the
            // notification bell badge is correct before the first open; installed into the switchable the panel binds to.
            var whatsNew = new SpotifyWhatsNewService(pathfinderResource, notificationsLog);
            wiring.Swap<IWhatsNewService>(Wavee.Backend.Wiring.LiveSeams.WhatsNew, svc.WhatsNew.SetInner, whatsNew, static () => new NullWhatsNewService());
            host.AttachWhatsNew(whatsNew);
            whatsNew.EnsureFresh();
            // Below-the-fold album enrichment (about-artist / merch / similar via Pathfinder; recommended playlists via the
            // SAME extended-metadata source, kinds 151→205) — installed into the switchable service the album pages hold.
            // inventory 8.2 #18 - this had NO teardown at all: the album pages kept the live Pathfinder-backed
            // service after logout. The offline stand-in is the same catalog-only enrichment the Services ctor installs.
            wiring.Swap<IAlbumEnrichmentService>(Wavee.Backend.Wiring.LiveSeams.AlbumEnrichment, svc.AlbumEnrichment.SetInner,
                new SpotifyAlbumEnrichmentService(pathfinderResource, em, store, hydration, metadataLog, extensionCache),
                () => new CatalogAlbumEnrichmentService(svc.Library));
            var artistLog = spclientLog.With("artist.popular");
            // The artist header stats (queryArtistOverview) and the extended chart (artist-top-tracks-extensions) are
            // NOT services any more: they are the artist ladder's Rich and Full rungs (ArtistHydration), reached through
            // the façade by whoever asks GetArtistAsync(uri, Rich|Full). Two services, two caches and two freshness
            // rules deleted; the ONE queryArtistOverview caller now lives behind IEnvelopeFetch.
            // Kind-185 play counts and kind-183 ©/℗ are no longer services either: they are two projectors on the ONE
            // trait POST (TraitProjectors.Default below), so the album open that used to cost a plays request, a
            // publishing request, an adornment request and a video request now costs one.
            // The account's OWN 4-week affinity ranking (userTopContent) — Home's top-artist row. A ME query, so it is
            // session-scoped and cleared in GoOffline; the row's expander pane asks the artist ladder for Rich rather
            // than a second endpoint.
            wiring.Swap<IUserTopService>(Wavee.Backend.Wiring.LiveSeams.UserTop, svc.UserTop.SetInner,
                new SpotifyUserTopService(pathfinderResource, spclientLog.With("home.usertop")), static () => new NullUserTopService());
            wiring.Swap<IPlaylistPopcountService>(Wavee.Backend.Wiring.LiveSeams.PlaylistPopcount, svc.PlaylistPopcount.SetInner,
                new SpotifyPlaylistPopcountService(live.Pipeline, () => live.BaseUrl, artistLog), static () => NullPlaylistPopcountService.Instance);
            wiring.Swap<IContentFilterService>(Wavee.Backend.Wiring.LiveSeams.ContentFilters, svc.ContentFilters.SetInner,
                new SpotifyContentFilterService(live.Pipeline, () => live.BaseUrl, artistLog), static () => NullContentFilterService.Instance);
            // Upcoming-release identity (kind 138) — through the ONE reader, so its answers (and its 404s) are shared
            // with every other extension read. Resolves prerelease↔album for the artist masthead, prerelease: routing,
            // and the pre-save write.
            wiring.Swap<IPreReleaseService>(Wavee.Backend.Wiring.LiveSeams.PreRelease, svc.PreRelease.SetInner,
                new SpotifyPreReleaseService(xmReader, metadataLog), static () => NullPreReleaseService.Instance);
            // The full credits drawer (kind 186) — same reader. Track-only; the NPV contributor list stays as the
            // fallback for tracks the wire has no drawer for.
            wiring.Swap<ITrackCreditsService>(Wavee.Backend.Wiring.LiveSeams.TrackCredits, svc.TrackCredits.SetInner,
                new SpotifyTrackCreditsService(xmReader, metadataLog), static () => NullTrackCreditsService.Instance);
            // Wire the pop-out/inline video resolver: track uri → Spotify manifest → a playable PopOutVideoSource
            // (PlayReady via the native CDM, or null when the account isn't served a PlayReady mp4). Over the live transport.
            // Through the composite so the tiered walk has ONE home: tier 1 is the user's attached local file (it always
            // wins, for ANY playable), tier 2 is this Spotify source tier, and a null answer falls through to the
            // controller's audio fallback. This REPLACES the overrides-only composite the pre-login bootstrap installed.
            // This is ALL that is left of SpotifyVideoService: the has-video plane is the trait pipeline's VideoProjector.
            var videoManifests = new SpotifyVideoManifestResolver(em, store, metadataLog);
            // inventory 8.2 #18 - the second hook with no teardown: after logout the bridge kept resolving through a
            // dead transport. The inverse is the OVERRIDES-ONLY composite Services.CreateReal installs pre-login, so
            // attaching and playing a local mp4 keeps working signed out - exactly the tier-1-always-wins contract.
            wiring.Set(Wavee.Backend.Wiring.LiveSeams.PlaybackResolveVideoSource,
                () => svc.Playback.ResolveVideoSource =
                    new CompositeVideoResolver((uri, ct) => videoManifests.ResolvePlayableAsync(uri, transport, ct), svc.VideoOverrides).ResolveAsync,
                () =>
                {
                    svc.Playback.ResolveVideoSource = CompositeVideoResolver.OverridesOnly(svc.VideoOverrides).ResolveAsync;
                    // The bridge's resolve memo may hold answers (CDN urls, license-relay closures) captured against
                    // THIS session's transport — a re-login (or a different account) must never be handed one of
                    // those back. Drop the whole cache; the overrides-only composite above still resolves fresh for
                    // any attached local file.
                    svc.Playback.ClearVideoResolveMemo();
                });
            // Owner identities are a LADDER now (UserHydration, registered below), not a switchable service: the kind-15
            // batch + the REST remainder live behind IUserProfileFetch and the resolved Owners land in the store. There
            // is nothing to install and nothing to tear down — going offline just stops the ladder from being asked.
            var userProfileFetch = new Wavee.SpotifyLive.Hydration.SpotifyUserProfileFetch(
                xmReader, live.Pipeline, () => live.BaseUrl, socialLog);
            // Let the player bar reflect the now-playing track's (async-detected) video via the store change stream.
            // Registered for the ROSTER, not for an undo: `store` is svc.RealStore, which CreateReal owns for the whole
            // process - it is not session state, so the bridge keeps reflecting it (and the persisted video map) while
            // logged out. The no-op inverse is the honest answer, and keeping the seam on the roster is what stops the
            // next reader assuming it was simply forgotten.
            wiring.Set(Wavee.Backend.Wiring.LiveSeams.PlaybackStore, () => svc.Playback.AttachStore(store), static () => { });
            // …and let the CONNECT wire reflect it too: the gid the state builder stamps as `associated_video_id` +
            // `switch-to-video`, and the one extra PutState a mid-track association land needs (no playback event fires
            // for a badge-only land, so nothing else would re-publish it).
            connect.AssociatedVideoGid = uri => store.GetVideoAssociation(uri)?.VideoGidHex;
            // The inverse is a NAMED no-op, not null: a badge-only video association can still land while the logout is
            // in flight, and the bridge calls this unconditionally. After logout there is no Connect wire to republish
            // to, so the intent is spelled out rather than left as a nullable hook nobody can read an intent from.
            wiring.Set(Wavee.Backend.Wiring.LiveSeams.PlaybackRepublishConnectState,
                () => svc.Playback.RepublishConnectState = () => connect.RepublishPlayerState(),
                () => svc.Playback.RepublishConnectState = static () => { });   // no live Connect wire while signed out
            // ── THE hydration façade (design §2.3) ────────────────────────────────────────────────────────────────
            // Everything above this line is a TRANSPORT. Everything below asks through ONE door. What this replaces:
            // 9 mutable hooks on the catalog source, 4 per-surface DetectHook closures, 2 container-detect fan-outs and
            // a freshness rule per service — all of it is now either a ladder (per kind) or a policy table
            // (OpenPolicy / TraitPolicy / HydrationPolicy).
            var pump = new Wavee.Backend.Hydration.HydrationPump(cts.Token, metadataLog.With("hydration"));
            // A pure surface → trait-set table with no setting to read: play counts hydrate for every list surface, and
            // the Plays column setting only shows or hides the lane that renders them.
            var traitPolicy = new Wavee.Backend.Hydration.TraitPolicy();
            // THE trait door (design §2.4): one plan → one ExtensionEtagCache POST per ≤300 uris carrying every wanted
            // kind → one lazy bulk write per page. The four services it replaced each owned a cap, a memo, an etag
            // decision and a client-feature-id; the projector registry owns the projection and nothing else.
            var traits = new Wavee.Backend.Hydration.TraitPipeline(store, xmCache, negatives,
                Wavee.Backend.Hydration.TraitProjectors.Default(xmReader, () => CoverColorPlane.Current),
                metadataLog.With("hydration.traits"));
            var catalog = new Wavee.Backend.Metadata.XmCatalogFetch(xmCache, store, metadataLog.With("hydration.catalog"));
            var envelopes = new PathfinderEnvelopeFetch(pathfinderResource);
            var chart = new SpclientArtistChartFetch(live.Pipeline, () => live.BaseUrl, artistLog);
            var opener = new LibrarySyncPlaylistOpener(sync, fetcher);
            Wavee.Backend.Hydration.IKindHydration[] ladders =
            [
                new Wavee.Backend.Hydration.PlayableHydration(EntityKind.Track, store, envelopes, metadataLog.With("hydration.track")),
                new Wavee.Backend.Hydration.PlayableHydration(EntityKind.Episode, store, envelopes, metadataLog.With("hydration.episode")),
                new Wavee.Backend.Hydration.AlbumHydration(store, envelopes, metadataLog.With("hydration.album")),
                new Wavee.Backend.Hydration.ArtistHydration(store, envelopes, chart, artistLog),
                new Wavee.Backend.Hydration.PlaylistHydration(store, opener, traitPolicy, syncLog.With("hydration.playlist")),
                new Wavee.Backend.Hydration.ShowHydration(store, traitPolicy),
                new Wavee.Backend.Hydration.CollectionHydration(store, traitPolicy),
                new Wavee.Backend.Hydration.UserHydration(store, userProfileFetch),
            ];
            var hydrator = new Wavee.Backend.Hydration.SpotifyProviderHydrator(store, () => sessionHost.Current,
                catalog, traits, traitPolicy, Wavee.Backend.Hydration.HydrationPolicy.Default, ladders, pump,
                metadataLog.With("hydration"));
            // The offline inner is a REAL implementation (store-only, promotes cold rows), never a null seam: opens keep
            // painting everything the cache holds and no caller has to ask whether we are logged in (design 1.3).
            wiring.Swap<IEntityHydrator>(Wavee.Backend.Wiring.LiveSeams.SpotifyHydration, spotifyHydration.SetInner, hydrator,
                () => new Wavee.Backend.Hydration.OfflineEntityHydrator(store));
            host.AttachHydration(pump);                 // the pump dies with the session

            // THE online-read seam (design §2.7): full-catalog search, as-you-type suggestions and the editorial Home
            // feed, in ONE switchable instead of the four Live* hooks the catalog source used to expose. It also owns
            // the live Home transport cache — the second publisher of the Home feed epoch, whose store watch must die
            // with the session (AttachHomeCache), and the reactivation head-probe behind svc.HomeFeedRevalidate. The
            // epoch is a UI-thread Signal, so the cache publishes through the same postUi every other off-thread bridge
            // write uses; Home subscribes to it in an EFFECT (effects keep running while a page is parked) and compares
            // it on reactivation — see HomePage's refresh loop.
            var onlineCatalog = new Wavee.SpotifyLive.Hydration.SpotifyOnlineCatalog(
                pathfinder, pathfinderResource, store, hydration,
                () => HomeModuleCopy.Titles,
                fetcher.FetchPlaylistHeaderAsync,
                fetcher.FetchPlaylistRevisionAsync,
                () => postUi(() => svc.HomeFeedEpoch.Value++),
                cts.Token);
            wiring.Set(Wavee.Backend.Wiring.LiveSeams.OnlineCatalog,
                () => svc.OnlineCatalog.SetInner(onlineCatalog),
                () => svc.OnlineCatalog.Reset());               // search/suggest/home stop networking: the store index answers
            host.AttachHomeCache(onlineCatalog);                // the Home cache store watch dies with the session (DisposeAsync)
            wiring.Set(Wavee.Backend.Wiring.LiveSeams.HomeFeedRevalidate,
                () => svc.HomeFeedRevalidate = onlineCatalog.RevalidateHomeAsync,
                () => svc.HomeFeedRevalidate = null);          // the head probe is session-bound (spclient + this session store)
            // The selected home facet is an OPAQUE SERVER TOKEN from this account's homeChips, so it cannot outlive the
            // session that produced it. Nothing installs it (the chip row writes it while the user browses) - the seam
            // exists purely so the teardown is registered and AssertCovers can see it.
            wiring.Set(Wavee.Backend.Wiring.LiveSeams.HomeFacet, static () => { },
                () => postUi(() => svc.HomeFacet.Value = null));   // UI-thread Signal, torn down from a pool thread

            // (b) complete every THIN root-list playlist in the background. Identity-only used to skip any row with a
            //     saved header, even when it had no membership baseline; that left Classic showing "0 songs" and no
            //     mosaic until the user opened the playlist. Open asks LibrarySync for the authoritative membership and
            //     its member metadata. The plan treats a known empty baseline as complete, so real empty lists stay cheap.
            //
            //     This is a store WATCH, not a one-shot plan — the plan used to be computed here, synchronously, before
            //     InitialHydrate (enqueued above, and deferred behind CachedStore.WarmComplete) had landed the cold
            //     rootlist, so on the first launch after the setup wizard it planned against an empty rootlist and never
            //     ran again: the sidebar showed raw `spotify:playlist:` uris until the user opened each list by hand.
            //     The watch re-plans on every rootlist/Bulk store change, which also covers dealer rootlist pushes and
            //     ReconnectResync convergence.
            var rootlistWatch = new Wavee.Backend.Hydration.RootlistHydrationWatch(store, hydrator, cts.Token, syncLog.With("hydration.rootlist"));
            host.AttachRootlistWatch(rootlistWatch);
        }

        // Friend-activity (presence) feed — session-scoped, display-only (never touches the Store). Seeds on the dealer
        // connection id + applies hm://presence2/user/ deltas; installed into the switchable service the friends panel
        // binds to (go-live → live provider; logout → back to the Null service via GoOffline).
        var friends = new SpotifyFriendActivityService(transport, live.Pipeline, () => live.BaseUrl,
            connect.ConnectionId, () => connect.CurrentConnectionId, socialLog);
        wiring.Swap<IFriendActivityService>(Wavee.Backend.Wiring.LiveSeams.Friends, svc.Friends.SetInner, friends,
            static () => new NullFriendActivityService());
        host.AttachFriends(friends);

        // Social notifications (gander) — session-scoped, display-only. One authed GET; seeds itself at construction so the
        // bell badge is right before the first open. Installed into the switchable the notification panel binds to.
        var notifications = new SpotifyNotificationsService(live.Pipeline, () => live.BaseUrl, notificationsLog,
            language: live.Session.Locale);
        wiring.Swap<ISpotifyNotificationsService>(Wavee.Backend.Wiring.LiveSeams.SpotifyNotifications, svc.SpotifyNotifications.SetInner, notifications,
            static () => new NullSpotifyNotificationsService());
        host.AttachNotifications(notifications);

        // THE gate (design 2.6): every seam Services.LiveSeams names must have registered an inverse above. This throws
        // naming whatever did not - i.e. whatever a future edit installs one-way, which is precisely the drift that left
        // AlbumEnrichment, the video hooks and the cover-colour filler live after logout (inventory 8.2 #18).
        wiring.AssertCovers(Services.LiveSeams);

        attempt.Succeeded();   // past the gate: this attempt owns a complete session, so a later throw is not ours to undo
        return host;
    }

    // ── the go-live rollback ledger (finding: "go-live failure leaves every installed seam live") ────────────────────
    // The bootstrap is a long sequence that mutates a PROCESS-WIDE Services from its middle onwards. LiveWiring already
    // guarantees every install has an inverse; what was missing is anyone to RUN those inverses when the bootstrap
    // itself throws. This collects the three things an aborted attempt owns — its ledger, its transports, and (once it
    // exists) its host — and replays them in the right order. It is deliberately tiny and allocation-cheap: one
    // instance per login attempt, and on the success path it does nothing at all.
    sealed class GoLiveAttempt
    {
        readonly Services _svc;
        readonly WaveeLogger _log;
        Wavee.Backend.Wiring.LiveWiring? _wiring;
        LiveDealerTransport? _transport;
        LiveConnect? _connect;
        LiveSessionHost? _host;
        bool _succeeded;

        public GoLiveAttempt(Services svc, WaveeLogger log) { _svc = svc; _log = log; }

        /// <summary>Create THE ledger for this attempt and hand it to <c>Services</c> — in one call, so an install can
        /// never land before the rollback knows where its inverse was recorded.</summary>
        public Wavee.Backend.Wiring.LiveWiring BeginWiring()
        {
            var wiring = new Wavee.Backend.Wiring.LiveWiring(_log);
            _wiring = wiring;
            _svc.AttachWiring(wiring);
            return wiring;
        }

        public void Transport(LiveDealerTransport transport) => _transport = transport;
        public void Connect(LiveConnect connect) => _connect = connect;
        /// <summary>The host now owns the transports; a rollback disposes IT instead of them (its DisposeAsync stops the
        /// sync loop and the router before the socket, which disposing the pieces by hand would get wrong).</summary>
        public void Built(LiveSessionHost host) => _host = host;

        /// <summary>The attempt completed and published a live session — nothing left to undo. A failure AFTER this
        /// point (a caller's continuation) belongs to logout, not to the bootstrap.</summary>
        public void Succeeded() => _succeeded = true;

        public async Task RollbackAsync(Exception ex)
        {
            if (_succeeded) return;
            bool cancelled = ex is OperationCanceledException;
            int seams = _wiring?.Installed.Count ?? 0;
            if (_wiring is null && _transport is null) return;   // failed before anything was built — nothing to undo
            _svc.Log.Event(cancelled ? WaveeLogLevel.Info : WaveeLogLevel.Warning, "connect", "golive.rollback",
                cancelled ? "Go-live cancelled — rolling the partial session back" : "Go-live failed — rolling the partial session back",
                ex: cancelled ? null : ex,
                fields: [WaveeLogField.Of("seams", seams), WaveeLogField.Of("host", _host is not null)]);

            // 1. Every seam back to its OFFLINE value first (LiveWiring.Uninstall is guarded and idempotent), so nothing
            //    the UI touches during the teardown reaches the half-built session.
            _wiring?.Uninstall();
            // 2. Then the transports. With a host, through the host: it orders the teardown (cancel → subscriptions →
            //    sync loop → connect → socket). Without one, the two pieces that were already running.
            if (_host is { } host)
            {
                try { await host.DisposeAsync().ConfigureAwait(false); }
                catch (Exception disposeEx) { _log.Error("go-live rollback: host dispose failed", disposeEx); }
            }
            else
            {
                try { _connect?.Dispose(); } catch (Exception disposeEx) { _log.Error("go-live rollback: connect dispose failed", disposeEx); }
                try { _transport?.Dispose(); } catch (Exception disposeEx) { _log.Error("go-live rollback: transport dispose failed", disposeEx); }
            }
            // 3. Finally drop the handles — ONLY if they are still ours. A racing sibling that won in the meantime has
            //    published its own ledger/host into the same fields, and stomping those would leave the app live with
            //    nothing able to undo it (Services.DetachWiring / DetachLive do the reference check).
            if (_wiring is { } w) _svc.DetachWiring(w);
            if (_host is { } h) _svc.DetachLive(h);
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Put every live seam back to its offline value FIRST, before anything it points at is disposed - otherwise a UI
        // action landing during teardown reaches a half-dead transport. Idempotent, so Services.GoOffline replaying the
        // same ledger after this (the logout order) is a no-op.
        _wiring.Uninstall();
        _cts.Cancel();           // stop background hydration / in-flight fetches before tearing the transport down
        _connSub?.Dispose();     // stop reconnect-resync triggers
        _friends?.Dispose();     // stop presence seed/deltas + watchdog
        _notifications?.Dispose();   // stop the gander in-flight fetch
        _whatsNew?.Dispose();        // stop the what's-new in-flight fetch
        _homeCache?.Dispose();       // stop publishing the Home feed epoch off this session's store
        _rootlistWatch?.Dispose();   // stop re-planning rootlist hydration off this session's store
        _hydrationPump?.Dispose();   // drop every queued prefetch/post-step with the session
        _router?.Dispose();      // stop decoding pushes
        if (_sync is not null) await _sync.DisposeAsync().ConfigureAwait(false);   // drain the loop to a stop before the transport
        _connect.Dispose();
        _transport.Dispose();
        _cts.Dispose();
    }

    // ── AuthState → LoginSnapshot projection ─────────────────────────────────────────────────────────────────────────
    /// <summary>Maps the backend reactive <see cref="AuthState"/> stream to UI <see cref="LoginSnapshot"/>s. The live
    /// AuthFlow only emits LoggedOut → AwaitingCredential → AwaitingUser(challenge) → ChallengeExpired; Finalizing /
    /// Authenticated / Failed / PremiumRequired are reported by the bootstrap (the AuthFlow never calls Connecting()).</summary>
    sealed class AuthStateAdapter(ILoginProgress progress, bool interactive, bool useBrowser, bool quiet = false) : IObserver<AuthState>
    {
        AuthPhase _last = AuthPhase.LoggedOut;

        public void OnNext(AuthState s)
        {
            _last = s.Phase;
            if (quiet) return;   // a racing sibling stays silent on the intermediate states (the two-pane owns them)
            switch (s.Phase)
            {
                case AuthPhase.AwaitingCredential:
                    progress.Report(new LoginSnapshot(!interactive ? LoginPhase.SilentResume : useBrowser ? LoginPhase.AwaitingBrowser : LoginPhase.RequestingCode));
                    break;
                case AuthPhase.AwaitingUser when s.Challenge is { } c:
                    progress.Report(new LoginSnapshot(LoginPhase.AwaitingApproval,
                        new LoginChallenge(c.UserCode, c.VerificationUri, c.VerificationUriComplete, c.Expiry)));
                    break;
                case AuthPhase.ChallengeExpired:
                    progress.Report(new LoginSnapshot(LoginPhase.ChallengeExpired));
                    break;
            }
        }

        public void OnError(Exception error) { }
        public void OnCompleted() { }

        /// <summary>The phase to show when LoginAsync returned null: a lapsed code → ChallengeExpired; a silent resume that
        /// found no usable credential → Welcome (LoggedOut); otherwise a genuine network/AP failure → Failed.</summary>
        public LoginSnapshot Terminal(bool credExisted) =>
            _last == AuthPhase.ChallengeExpired ? new LoginSnapshot(LoginPhase.ChallengeExpired)
          : (!interactive && !credExisted)      ? new LoginSnapshot(LoginPhase.LoggedOut)
          :                                       new LoginSnapshot(LoginPhase.Failed, Error: "We couldn't reach Spotify. Check your connection and try again.");
    }

    sealed class NullLoginProgress : ILoginProgress
    {
        public static readonly NullLoginProgress Instance = new();
        public void Report(LoginSnapshot snapshot) { }
    }

    // Diagnostic one-shot (WAVEE_PLAYPLAY_PROBE): fetch a file's PlayPlay obf+aes on the LIVE session and compare to the
    // reference ogg-vorbis-160 golden vector. Confirms whether the obf Spotify returns for OUR (bumped-version) request is
    // the vector's value — i.e. whether the existing 1.2.88.483 emulator derives the right key on a non-403 request.
    static readonly (string File, string HarObf)[] PlayPlayHarVectors =
    [
        ("5989137781b15a3275f8e312bceb096b7ef8f0a0", "4cc24d16068d90fe18c4e2e2cd2691d0"),
        ("1e90abc9cde41338a87c8da5be203218ac84a82c", "a7545790cfe4cae70dd5f51712df35a8"),
    ];

    static async Task ProbePlayPlayAsync(Wavee.SpotifyLive.Audio.AudioPlaybackStack audio, string probe, WaveeLogger log, CancellationToken ct)
    {
        try
        {
            if (audio.RuntimeAsset is null)
            {
                for (int i = 0; i < 10 && audio.RuntimeAsset is null && !ct.IsCancellationRequested; i++)
                    await Task.Delay(200, ct).ConfigureAwait(false);
            }
            if (audio.RuntimeAsset is null) { log.Info("PROBE: runtime not ready"); return; }

            IEnumerable<string> files = probe is "har" or "all" or "1" or "true"
                ? PlayPlayHarVectors.Select(h => h.File)
                : [probe.Trim().ToLowerInvariant()];

            foreach (var fileHex in files)
            {
                log.Info($"PROBE: full PlayPlay path for {fileHex}");
                var key = await audio.KeyResolver.GetKeyAsync(Convert.FromHexString(fileHex), new byte[16], ct).ConfigureAwait(false);
                log.Info($"PROBE RESULT {fileHex[..8]}...: aes={key.Length}B redacted");
            }
        }
        catch (Exception ex) { log.Info("PROBE failed: " + ex.Message); }
    }

    static async Task ProbeAudioFormatsAsync(AudioPlaybackStack audio, string probe, WaveeLogger log, CancellationToken ct)
    {
        try
        {
            var uri = probe.Trim();
            if (uri.Length == 22 && EntityUri.Parse(uri).Provider != EntityProviders.Spotify)
                uri = "spotify:track:" + uri;
            // Any PLAYABLE: LiveTrackResolver.FetchMetaAsync forks on the kind and has an episode arm, so probing a
            // podcast's audio files is exactly as meaningful as probing a song's. (A bare 22-char id stays a track —
            // it is ambiguous by construction and the probe has to guess something.)
            if (!EntityUri.Parse(uri).IsPlayable)
            {
                log.Info("AUDIO FORMAT PROBE: invalid probe '" + probe + "' (expected spotify:track:<id>, spotify:episode:<id> or a 22-char id)");
                return;
            }

            var id = EntityUri.IdOf(uri);
            var track = new Track(
                id, uri, "probe",
                Array.Empty<ArtistRef>(),
                new AlbumRef("", "", ""),
                0, false, null);

            log.Info("AUDIO FORMAT PROBE: resolving " + uri);
            await audio.TrackResolver.ResolveMetaAsync(track, ct).ConfigureAwait(false);
            log.Info("AUDIO FORMAT PROBE: metadata resolved for " + uri + "; waiting for background probe logs");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { log.Info("AUDIO FORMAT PROBE failed: " + ex.Message); }
    }

    // The real lyrics feed (docs/lyrics-aggregator-reranker-plan.md): fan out to AMLL (word-synced TTML by track id),
    // Spotify-native (the rerank reference + a line candidate, via the authed spclient), and LRCLIB (clean metadata
    // fallback); the reranker validates content/timing and picks the best. The request is resolved from the live
    // now-playing track (what the lyrics view asks for). Grey CJK/Musixmatch sources stay off by default (LyricsOptions).
    static Wavee.Backend.Lyrics.AggregatingLyricsProvider BuildLiveLyrics(
        Func<string> baseUrl, IPlaybackPlayer controller, Func<CancellationToken, Task<string>> token,
        Func<string, CancellationToken, Task<Track?>> resolveFull)
    {
        var http = new Wavee.Backend.Lyrics.SharedHttpLyricFetch();

        // Spotify color-lyrics auth — the proven WaveeMusic SpClient.GetLyricsAsync recipe: a raw bearer GET with
        // app-platform=ANDROID + spotify-app-version. The ANDROID platform is what lets the lyrics CDN serve WITHOUT a
        // client-token; WebPlayer/desktop platforms require a client-token and 403 without one. We must NOT route through
        // the shared spclient pipeline (it force-stamps App-Platform=Win32_x86_64 in ClientTokenMiddleware). The bearer is
        // the refreshing TokenProvider (survives the ~1h access-token expiry), so lyrics keep loading deep into a session.
        async Task<string?> SpotifyGet(string url, CancellationToken c)
        {
            try
            {
                string tok = await token(c).ConfigureAwait(false);
                if (string.IsNullOrEmpty(tok)) { Wavee.Backend.Lyrics.LyricsProbe.Note("spotify", "no access token (bearer refresh empty)"); return null; }
                using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + tok);
                req.Headers.TryAddWithoutValidation("app-platform", "Android");
                req.Headers.TryAddWithoutValidation("spotify-app-version", SpotifyClientIdentity.AppVersionHeader);
                req.Headers.TryAddWithoutValidation("Accept", "application/json");
                using var resp = await Wavee.Backend.Spotify.SharedHttp.Client.SendAsync(req, c).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) { Wavee.Backend.Lyrics.LyricsProbe.Note("spotify", $"color-lyrics HTTP {(int)resp.StatusCode}"); return null; }
                return await resp.Content.ReadAsStringAsync(c).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { Wavee.Backend.Lyrics.LyricsProbe.Note("spotify", $"color-lyrics error: {e.GetType().Name}"); return null; }
        }

        var sources = new System.Collections.Generic.List<Wavee.Backend.Lyrics.ILyricCandidateSource>
        {
            new Wavee.Backend.Lyrics.Sources.AmllTtmlDbSource(http),
            new Wavee.Backend.Lyrics.Sources.SpotifyNativeLyricsSource(SpotifyGet, baseUrl),
            new Wavee.Backend.Lyrics.Sources.LrcLibSource(http),
        };
        // Grey providers (docs plan §6) — ENABLED: widen word/syllable coverage beyond AMLL with the reverse-engineered
        // CJK APIs (QQ QRC, NetEase YRC, Kugou KRC) + Musixmatch richsync. The reranker still validates each against the
        // Spotify reference, so a wrong/ mistimed grey candidate can't win.
        var opt = Wavee.Backend.Lyrics.LyricsOptions.Default with
        {
            EnableGreyProviders = true,
            PerSourceTimeoutMs = 30000,
            TotalTimeoutMs = 30000,
            FirstHitGraceMs = 1200,
        };
        if (opt.EnableGreyProviders)
        {
            sources.Add(new Wavee.Backend.Lyrics.Sources.MusixmatchSource());
            sources.Add(new Wavee.Backend.Lyrics.Sources.QqMusicSource());
            sources.Add(new Wavee.Backend.Lyrics.Sources.NeteaseSource());
            sources.Add(new Wavee.Backend.Lyrics.Sources.KugouSource());
        }

        async Task<Wavee.Backend.Lyrics.LyricsRequest?> Resolve(string trackId, CancellationToken c)
        {
            var t = controller.State.CurrentTrack;
            if (t is null || (t.Id != trackId && t.Uri != "spotify:track:" + trackId)) return null;

            string uri = "spotify:track:" + trackId;
            // The cluster's now-playing track is often THIN (no artist / no ISRC) and may not be enriched yet when the
            // lyrics view first asks — so resolve the FULL track ourselves (the same extended-metadata + Pathfinder
            // resolver the player bar uses). This makes the search's artist + ISRC independent of the now-playing
            // enrichment race; otherwise every provider searches title-only (e.g. "fade away" with no artist → no match).
            bool thin = t.Artists.Count == 0 || string.IsNullOrEmpty(t.Artists[0].Name) || string.IsNullOrEmpty(t.Isrc);
            if (thin)
            {
                try
                {
                    var full = await resolveFull(uri, c).ConfigureAwait(false);
                    if (full is not null && (full.Uri == uri || full.Id == trackId)) t = full;
                }
                catch (OperationCanceledException) { throw; }
                catch { /* best-effort — fall back to the thin track */ }
            }

            // Skip blank artist names (a thin cluster track carries a single empty ArtistRef) so the request gets [] not [""].
            var artists = new System.Collections.Generic.List<string>(t.Artists.Count);
            foreach (var a in t.Artists) if (!string.IsNullOrEmpty(a.Name)) artists.Add(a.Name);
            return new Wavee.Backend.Lyrics.LyricsRequest(
                trackId, uri, t.Title, artists, t.Album.Name, t.DurationMs,
                Isrc: t.Isrc, Market: "from_token", HasSpotifyLyrics: null);
        }

        var lyricsLog = new WaveeLogger(WaveeLog.Instance, "lyrics");
        return new Wavee.Backend.Lyrics.AggregatingLyricsProvider(
            sources, Resolve, opt, referenceSourceId: "spotify",
            log: lyricsLog,
            // %LOCALAPPDATA%\Wavee\lyrics — read-through before the fan-out, so a track played in an earlier session
            // has its words with no network at all.
            diskCache: new Wavee.Backend.Lyrics.LyricsDiskCache(log: lyricsLog));
    }

    /// <summary>CLI demo (`--connect-live`): bring up the live session over a REAL Services and log the now-playing the
    /// bridge sees THROUGH the switchable backend, for ~25 s — proving the fake→live swap end-to-end, headlessly.</summary>
    public static async Task<int> RunAsync(WaveeLogger log, CancellationToken ct, string language = "en")
    {
        log.Info("Wavee live Connect probe — building the real backend + going live...");
        language = SpotifyHeaders.NormalizeLanguage(language);
        var svc = Services.CreateReal(appLocale: new AppLocale(language == "en" ? "en-US" : language, language));
        await using var host = await StartAsync(svc, log, ct).ConfigureAwait(false);
        if (host is null) { log.Info("Live session could not start."); return 1; }

        using var sub = svc.Player.State.Changes.Subscribe(Observers.From<Wavee.Core.IPlaybackState>(s =>
        {
            if (s.CurrentTrack is { } t)
                log.Info("  bridge now-playing: " + t.Title + " — " + (s.IsPlaying ? "playing" : "paused") + " (active=" + (s.ActiveDeviceId ?? "") + ")");
        }));
        // Observability proof: the realtime (dealer socket) link status — toggle your network to see Reconnecting → Online.
        using var connSub = svc.Connectivity.StatusChanged.Subscribe(Observers.From<Wavee.Core.ConnectionStatus>(
            s => log.Info("  realtime link: " + s)));
        log.Info("  realtime link: " + svc.Connectivity.Status);

        // Stage 1 verification: open the first rootlist playlist + an album through the catalog — which now goes through
        // THE hydration façade (IEntityHydrator.EnsureAsync → the per-kind ladder), not the deleted OnDemandFetch hook.
        string? plUri = null, alUri = null, arUri = null;
        if (svc.RealStore is { } st)
        {
            foreach (var e in st.Rootlist())
                if (e.Kind == 0 && EntityUri.KindOf(e.Uri) == EntityKind.Playlist) { plUri = e.Uri; break; }
            foreach (var u in st.SavedUris("albums")) { alUri = u; break; }
            foreach (var u in st.SavedUris("artists")) { arUri = u; break; }
        }
        if (plUri is not null)
        {
            var full = await svc.Library.GetPlaylistAsync(plUri, ct: ct).ConfigureAwait(false);
            log.Info($"  on-open playlist '{full?.Name}' → {full?.Tracks?.Count ?? 0} tracks");
        }
        if (alUri is not null)
        {
            var al = await svc.Library.GetAlbumAsync(alUri, ct: ct).ConfigureAwait(false);
            var t0 = al?.Tracks is { Count: > 0 } tl ? $"{tl[0].Title} ({tl[0].DurationMs}ms)" : "—";
            log.Info($"  on-open album '{al?.Name}' → {al?.Tracks?.Count ?? 0} tracks (first: {t0})");
        }
        if (arUri is not null)
        {
            var ar = await svc.Library.GetArtistAsync(arUri, ct: ct).ConfigureAwait(false);
            log.Info($"  on-open artist '{ar?.Name}' → {ar?.TopTracks?.Count ?? 0} top tracks, {ar?.TopAlbums?.Count ?? 0} releases, {ar?.MonthlyListeners ?? 0} listeners (Pathfinder)");
        }
        var home = await svc.Library.GetHomeAsync(null, ct).ConfigureAwait(false);   // the smoke read is the unfiltered feed
        log.Info($"  home → {home.Groups.Count} groups (editorial Pathfinder + library)");
        var sr = await svc.Library.SearchAsync("paul kim", ct).ConfigureAwait(false);
        log.Info($"  search 'paul kim' → {sr.Tracks.Count} tracks, {sr.Albums.Count} albums, {sr.Artists.Count} artists, {sr.Playlists.Count} playlists");
        var sg = await svc.Library.SuggestAsync("aras", ct).ConfigureAwait(false);
        log.Info($"  suggest 'aras' → {sg.Count}: {string.Join(" | ", System.Linq.Enumerable.Take(sg, 6))}");

        log.Info("SMOKE TEST — Wavee is now a live Connect device. In the next 90s:");
        log.Info("  1) open Spotify on your phone/web → device picker → confirm \"Wavee\" appears;");
        log.Info("  2) transfer to Wavee + play a playlist/album/Liked Songs → watch now-playing + the queue below;");
        log.Info("  3) pause/seek/next/shuffle/repeat from the phone → each logs an inbound 'connect command' + a put-state;");
        log.Info("  4) (optional) toggle airplane mode briefly → watch 'realtime link: Reconnecting' then 'Online'.");
        try { await Task.Delay(TimeSpan.FromSeconds(90), ct).ConfigureAwait(false); } catch { }
        return 0;
    }
}
