using System;

namespace Wavee.Backend.Wiring;

// ── The go-live seam roster (hydration-facade-design.md §2.6) ────────────────────────────────────────────────────────
// The names every live install registers under, as CONSTANTS rather than string literals at the call sites: a typo in
// LiveSessionHost is then a compile error, not a seam that silently misses `AssertCovers`. `All` is what
// `Services.LiveSeams` exposes and what `LiveWiring.AssertCovers` is run against at the end of go-live.
//
// It lives under Backend/ (not beside Services) for ONE reason: Backend\** is source-included by Wavee.Tests, so the
// roster is unit-testable (no duplicates, no blanks) while Services.cs — which drags the whole engine in — is not.
//
// Adding a live install? Add its const here, register it through LiveWiring.Set/Swap at the install site, and add it to
// `All`. Skipping the last step means AssertCovers cannot notice the seam went missing; skipping the middle one means
// AssertCovers throws at go-live. Both are the intended pressure.
public static class LiveSeams
{
    // ── Services.GoLive — the switchable facades the PlaybackBridge and the shell bind to ──
    public const string Player = "Player";
    public const string Devices = "Devices";
    public const string Session = "Session";
    public const string Connectivity = "Connectivity";
    public const string Lyrics = "Lyrics";

    // ── the hydration façade + the online-read seam (design §2.3 / §2.7) ──
    public const string SpotifyHydration = "SpotifyHydration";
    public const string OnlineCatalog = "OnlineCatalog";
    public const string HomeFeedRevalidate = "HomeFeedRevalidate";
    public const string HomeFacet = "HomeFacet";

    // ── the return-only catalogue services (design §3) ──
    public const string AlbumEnrichment = "AlbumEnrichment";
    public const string PreRelease = "PreRelease";
    public const string TrackCredits = "TrackCredits";
    public const string TrackExpansion = "TrackExpansion";
    public const string PlaylistPopcount = "PlaylistPopcount";
    public const string ContentFilters = "ContentFilters";
    public const string Concerts = "Concerts";
    public const string Browse = "Browse";
    public const string WhatsNew = "WhatsNew";
    public const string HomeSections = "HomeSections";
    public const string Recents = "Recents";
    public const string Friends = "Friends";
    public const string SpotifyNotifications = "SpotifyNotifications";
    public const string UserTop = "UserTop";

    // ── the playback bridge's session-scoped hooks (the ones §8.2 #18 found had no teardown at all) ──
    public const string PlaybackResolveVideoSource = "Playback.ResolveVideoSource";
    public const string PlaybackRepublishConnectState = "Playback.RepublishConnectState";
    public const string PlaybackVideoMedia = "Playback.VideoMedia";
    public const string PlaybackStore = "Playback.Store";
    public const string PlaybackLocalOutputs = "Playback.LocalOutputs";
    public const string PlaybackLocalPlaybackSupported = "Playback.LocalPlaybackSupported";
    /// <summary>The local-audio runtime/provisioning status feed (AudioRuntimeStatusService.Changed → the bridge's
    /// RuntimeStatus signal, which drives the setup banner + the "runtime missing" toast). Session-scoped in both
    /// directions: the event source dies with the audio stack, and the STATUS left on the bridge would otherwise keep
    /// offering a Set-up flow for a session that no longer exists.</summary>
    public const string PlaybackRuntimeStatus = "Playback.RuntimeStatus";

    // ── process-wide planes a session installs into ──
    public const string CoverColorFiller = "CoverColorPlane.Filler";
    public const string HomeBaselinePreviews = "HomeBaselinePreviews";

    // ── the live session's own handles + the write lane ──
    public const string LiveHost = "LiveHost";
    public const string LiveHttp = "LiveHttp";
    public const string MutTransport = "MutTransport";
    public const string SessionAccount = "SessionAccount";
    public const string RealSync = "RealSync";
    public const string PlaylistTuning = "PlaylistTuning";
    public const string MutationScheduleDrain = "RealMutationSource.ScheduleDrain";
    public const string PlaylistMutationsHttp = "RealPlaylistMutations.Http";
    public const string PlaylistMutationsScheduleDrain = "RealPlaylistMutations.ScheduleDrain";
    public const string SpclientBaseUrl = "SpclientBaseUrl";
    /// <summary>The Spotify half of the module→host services (<c>host/auth/token</c>, <c>host/auth/context</c>,
    /// <c>spotify/audioKey</c>, all gated on <c>permission:auth.spotify</c>). Session-scoped by construction: they
    /// answer from the live spclient handle and the AP socket, so logout must take them back off the registry — a
    /// module asking afterwards gets "not offered", never a dead session.</summary>
    public const string ModuleHostServices = "ModuleHostServices";

    // ── the local-audio stack's app-level handles ──
    public const string PlayPlayProvisioner = "PlayPlayProvisioner";
    public const string AudioBodyCache = "AudioBodyCache";
    public const string AudioLicenseCache = "AudioLicenseCache";
    public const string AudioBodyDiskArena = "Residency.AudioBodyDisk";

    /// <summary>Every seam that MUST be installed — with its teardown — by a successful go-live.</summary>
    public static readonly string[] All =
    [
        Player, Devices, Session, Connectivity, Lyrics,
        SpotifyHydration, OnlineCatalog, HomeFeedRevalidate, HomeFacet,
        AlbumEnrichment, PreRelease, TrackCredits, TrackExpansion, PlaylistPopcount, ContentFilters,
        Concerts, Browse, WhatsNew, HomeSections, Recents, Friends, SpotifyNotifications, UserTop,
        PlaybackResolveVideoSource, PlaybackRepublishConnectState, PlaybackVideoMedia, PlaybackStore,
        PlaybackLocalOutputs, PlaybackLocalPlaybackSupported, PlaybackRuntimeStatus,
        CoverColorFiller, HomeBaselinePreviews,
        LiveHost, LiveHttp, MutTransport, SessionAccount, RealSync, PlaylistTuning,
        MutationScheduleDrain, PlaylistMutationsHttp, PlaylistMutationsScheduleDrain, SpclientBaseUrl,
        ModuleHostServices,
        PlayPlayProvisioner, AudioBodyCache, AudioLicenseCache, AudioBodyDiskArena,
    ];
}
