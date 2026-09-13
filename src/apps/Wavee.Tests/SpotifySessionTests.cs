// ── Wavee.Tests/SpotifySessionTests.cs — the session fold and the request fold ───────────────────────────────────
//
// Wave 2's gate for the two PURE decisions in Spotify/Spotify.cs that the shell is built on (plan §5 Wave 2:
// "request builders return the request (pure)"): `Step`, which is the whole login/reconnect state machine, and
// `Build`, which is every route `Spotify.Api.cs` will call. No socket, no clock, no Scope — "post these events,
// assert the session and the effects", which is §4.15's shape.
//
// The third class is the SHELL's one offline seam: `Spotify.Session.cs` reads and wipes `Platform`'s credential slot
// directly (no delegate), so the two paths that touch it without opening a socket — a login with an empty slot, and
// the logout that erases it — are pinned here against a real `FileLocalStore` in a temp directory with the protector
// swapped for a no-op, exactly as `PlatformTests`' `CredentialSlotTests` does it. Nothing here reaches the network or
// the user's profile.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class SessionStepTests
{
    static Spotify.SessionEffects Step(ref Spotify.Session s, Spotify.SessionEventKind kind,
        Spotify.TokenRef text = default, Spotify.TokenRef text2 = default, long number = 0, bool flag = false)
        => Spotify.Step(ref s, new Spotify.SessionEvent(kind, Text: text, Text2: text2, Number: number, Flag: flag));

    /// <summary>The happy path, in the order the AP thread walks it.</summary>
    static Spotify.Session Online()
    {
        var s = default(Spotify.Session);
        Step(ref s, Spotify.SessionEventKind.Login, flag: true);
        Step(ref s, Spotify.SessionEventKind.Hosts, text: new(0, 4), text2: new(4, 4));
        Step(ref s, Spotify.SessionEventKind.Connected);
        Step(ref s, Spotify.SessionEventKind.HandshakeOk);
        Step(ref s, Spotify.SessionEventKind.Welcome, number: (long)Spotify.Tier.Premium);
        Step(ref s, Spotify.SessionEventKind.ClientTokenMinted, text: new(8, 4), number: 1_000);
        Step(ref s, Spotify.SessionEventKind.AccessTokenMinted, text: new(12, 4), number: 2_000);
        Step(ref s, Spotify.SessionEventKind.DealerOnline, text: new(16, 4));
        return s;
    }

    [Fact]
    public void A_login_without_a_credential_fails_immediately_and_asks_for_nothing()
    {
        var s = default(Spotify.Session);
        var fx = Step(ref s, Spotify.SessionEventKind.Login, flag: false);
        Assert.Equal(Spotify.SessionPhase.Failed, s.Phase);
        Assert.Equal(Spotify.SessionFault.NoCredential, s.Fault);
        Assert.Equal(Spotify.SessionEffects.None, fx);
    }

    [Fact]
    public void A_login_with_a_credential_resolves_hosts_and_bumps_the_epoch()
    {
        var s = default(Spotify.Session);
        var fx = Step(ref s, Spotify.SessionEventKind.Login, flag: true);
        Assert.Equal(Spotify.SessionPhase.Resolving, s.Phase);
        Assert.Equal(1u, s.Epoch);
        Assert.Equal(Spotify.SessionEffects.ResolveHosts, fx);
    }

    [Fact]
    public void The_happy_path_ends_online_with_both_tokens_and_a_connection_id()
    {
        var s = Online();
        Assert.Equal(Spotify.SessionPhase.Online, s.Phase);
        Assert.True(s.IsOnline);
        Assert.Equal(Spotify.Tier.Premium, s.Tier);
        Assert.Equal(new Spotify.TokenRef(12, 4), s.AccessToken);
        Assert.Equal(new Spotify.TokenRef(16, 4), s.ConnectionId);
        Assert.Equal(2_000, s.AccessExpiresAtMs);
        Assert.True(s.CanRequest(1_999));
        Assert.False(s.CanRequest(2_000));
    }

    [Fact]
    public void A_welcome_saves_the_credential_and_asks_for_the_attestation_first()
    {
        var s = default(Spotify.Session);
        Step(ref s, Spotify.SessionEventKind.Login, flag: true);
        Step(ref s, Spotify.SessionEventKind.Hosts);
        Step(ref s, Spotify.SessionEventKind.Connected);
        Step(ref s, Spotify.SessionEventKind.HandshakeOk);
        var fx = Step(ref s, Spotify.SessionEventKind.Welcome, number: (long)Spotify.Tier.Free);

        Assert.Equal(Spotify.SessionPhase.Minting, s.Phase);
        Assert.True(s.HasCredential);
        // The welcome is also where the account reaches the app: its market and its catalog scope (headless plan §1.6
        // item 2) — one effect, executed by `Spotify.Apply` on the UI thread.
        Assert.Equal(Spotify.SessionEffects.SaveCredential | Spotify.SessionEffects.MintClientToken
                     | Spotify.SessionEffects.Welcome, fx);
        // …and the bearer's mint is gated on the attestation, never parallel with it.
        Assert.Equal(Spotify.SessionEffects.MintAccessToken, Step(ref s, Spotify.SessionEventKind.ClientTokenMinted));
    }

    [Fact]
    public void Going_online_announces_the_device_once_per_connection()
    {
        var s = default(Spotify.Session);
        Step(ref s, Spotify.SessionEventKind.Login, flag: true);
        Step(ref s, Spotify.SessionEventKind.Hosts);
        Step(ref s, Spotify.SessionEventKind.Connected);
        Step(ref s, Spotify.SessionEventKind.HandshakeOk);
        Step(ref s, Spotify.SessionEventKind.Welcome, number: (long)Spotify.Tier.Premium);
        Step(ref s, Spotify.SessionEventKind.ClientTokenMinted);
        Step(ref s, Spotify.SessionEventKind.AccessTokenMinted);

        // The transition into Online is the hello (headless plan §1.6 item 5): the connection id is in the session box.
        Assert.Equal(Spotify.SessionEffects.AnnounceDevice, Step(ref s, Spotify.SessionEventKind.DealerOnline, text: new(16, 4)));
        // A second pusher frame on the live socket is not a new device.
        Assert.Equal(Spotify.SessionEffects.None, Step(ref s, Spotify.SessionEventKind.DealerOnline, text: new(20, 4)));

        // A drop and a reconnect is a new connection id, and a new hello.
        Step(ref s, Spotify.SessionEventKind.Dropped, number: (long)Spotify.SessionFault.Network);
        Step(ref s, Spotify.SessionEventKind.Retry);
        Step(ref s, Spotify.SessionEventKind.Hosts);
        Assert.Equal(Spotify.SessionEffects.AnnounceDevice, Step(ref s, Spotify.SessionEventKind.DealerOnline, text: new(24, 4)));
        Assert.Equal(new Spotify.TokenRef(24, 4), s.ConnectionId);
    }

    [Fact]
    public void A_disconnect_closes_everything_but_keeps_the_credential_and_the_boot_identity()
    {
        var s = Online();
        s.DeviceId = new Spotify.TokenRef(100, 8);
        s.ClientId = new Spotify.TokenRef(108, 8);
        s.Locale = new Spotify.TokenRef(116, 2);
        uint epoch = s.Epoch;
        var fx = Step(ref s, Spotify.SessionEventKind.Disconnect);

        Assert.Equal(Spotify.SessionEffects.CloseAll, fx);                  // never ClearCredential: that is Logout's alone
        Assert.Equal(Spotify.SessionPhase.Offline, s.Phase);
        Assert.Equal(epoch + 1, s.Epoch);
        Assert.True(s.HasCredential);
        Assert.True(s.AccessToken.IsEmpty);
        Assert.True(s.ClientToken.IsEmpty);
        Assert.True(s.ConnectionId.IsEmpty);
        Assert.Equal(new Spotify.TokenRef(100, 8), s.DeviceId);
        Assert.Equal(new Spotify.TokenRef(108, 8), s.ClientId);
        Assert.Equal(new Spotify.TokenRef(116, 2), s.Locale);

        // Offline, so a late answer from the closed epoch folds to nothing; a fresh Login starts again.
        Assert.Equal(Spotify.SessionEffects.None, Step(ref s, Spotify.SessionEventKind.DealerOnline));
        Assert.Equal(Spotify.SessionEffects.ResolveHosts, Step(ref s, Spotify.SessionEventKind.Login, flag: true));
    }

    [Fact]
    public void The_welcome_scope_is_the_account_market_and_tier_over_the_current_locale_and_filter()
    {
        var booted = new CatalogScope("spotify", "someone", "nl-NL", "", Tier: 0, AllowExplicit: false);
        CatalogScope signedIn = Spotify.WelcomeScope(booted, "someone", "NL", Spotify.Tier.Premium);
        Assert.Equal(new CatalogScope("spotify", "someone", "nl-NL", "NL", (byte)Spotify.Tier.Premium, false), signedIn);

        // A reconnect's welcome for the same account, market and tier is the SAME scope: the shell does not rebuild the
        // table set for it.
        Assert.Equal(signedIn, Spotify.WelcomeScope(signedIn, "someone", "NL", Spotify.Tier.Premium));

        // The offline demo scope (no credential at boot) becomes the provider's; an empty username keeps the one known.
        CatalogScope fromFake = Spotify.WelcomeScope(CatalogScope.Fake("en-US"), "someone", "SE", Spotify.Tier.Free);
        Assert.Equal("spotify", fromFake.Provider);
        Assert.Equal("someone", fromFake.Account);
        Assert.Equal("en-US", fromFake.Locale);
        Assert.Equal("SE", fromFake.Market);
        Assert.Equal("someone", Spotify.WelcomeScope(signedIn, "", "NL", Spotify.Tier.Premium).Account);
    }

    [Fact]
    public void A_rejection_clears_the_credential_and_is_terminal()
    {
        var s = default(Spotify.Session);
        Step(ref s, Spotify.SessionEventKind.Login, flag: true);
        var fx = Step(ref s, Spotify.SessionEventKind.AuthRejected);

        Assert.Equal(Spotify.SessionPhase.Failed, s.Phase);
        Assert.Equal(Spotify.SessionFault.CredentialRejected, s.Fault);
        Assert.False(s.HasCredential);
        Assert.Equal(Spotify.SessionEffects.ClearCredential | Spotify.SessionEffects.CloseAll, fx);

        // Terminal: nothing but a fresh Login moves it again.
        Assert.Equal(Spotify.SessionEffects.None, Step(ref s, Spotify.SessionEventKind.Retry));
        Assert.Equal(Spotify.SessionEffects.None, Step(ref s, Spotify.SessionEventKind.Welcome));
        Assert.Equal(Spotify.SessionPhase.Failed, s.Phase);
    }

    [Fact]
    public void A_network_drop_never_clears_the_credential()
    {
        var s = Online();
        var fx = Step(ref s, Spotify.SessionEventKind.Dropped, number: (long)Spotify.SessionFault.Network);

        Assert.Equal(Spotify.SessionPhase.Reconnecting, s.Phase);
        Assert.True(s.HasCredential);
        Assert.Equal(0, (int)(fx & Spotify.SessionEffects.ClearCredential));
        Assert.Equal(Spotify.SessionEffects.CloseAll | Spotify.SessionEffects.Backoff, fx);
        Assert.True(s.ConnectionId.IsEmpty);    // the id belonged to the socket that died
    }

    [Fact]
    public void Every_drop_bumps_the_epoch_so_in_flight_work_is_abandoned()
    {
        var s = Online();
        uint epoch = s.Epoch;
        Step(ref s, Spotify.SessionEventKind.Dropped, number: (long)Spotify.SessionFault.Network);
        Assert.Equal(epoch + 1, s.Epoch);
        Step(ref s, Spotify.SessionEventKind.Retry);
        Step(ref s, Spotify.SessionEventKind.Dropped, number: (long)Spotify.SessionFault.Network);
        Assert.Equal(epoch + 2, s.Epoch);
    }

    [Fact]
    public void The_backoff_ladder_is_3_6_12_24_capped_at_30_seconds()
    {
        var s = default(Spotify.Session);
        Assert.Equal(3_000, Spotify.BackoffMs(s));       // no attempt yet
        int[] expected = [3_000, 6_000, 12_000, 24_000, 30_000, 30_000];
        for (int i = 0; i < expected.Length; i++)
        {
            s.Attempt = (uint)(i + 1);
            Assert.Equal(expected[i], Spotify.BackoffMs(s));
        }
    }

    [Fact]
    public void A_token_refresh_while_online_does_not_re_open_the_dealer()
    {
        var s = Online();
        var fx = Step(ref s, Spotify.SessionEventKind.AccessTokenMinted, text: new(20, 4), number: 9_000);
        Assert.Equal(Spotify.SessionEffects.None, fx);
        Assert.Equal(Spotify.SessionPhase.Online, s.Phase);
        Assert.Equal(9_000, s.AccessExpiresAtMs);
    }

    [Fact]
    public void The_first_access_token_opens_the_dealer()
    {
        var s = default(Spotify.Session);
        Step(ref s, Spotify.SessionEventKind.Login, flag: true);
        Step(ref s, Spotify.SessionEventKind.Hosts);
        Step(ref s, Spotify.SessionEventKind.Connected);
        Step(ref s, Spotify.SessionEventKind.HandshakeOk);
        Step(ref s, Spotify.SessionEventKind.Welcome);
        Step(ref s, Spotify.SessionEventKind.ClientTokenMinted);
        Assert.Equal(Spotify.SessionEffects.OpenDealer, Step(ref s, Spotify.SessionEventKind.AccessTokenMinted));
    }

    [Fact]
    public void A_logout_forgets_everything_except_the_epoch()
    {
        var s = Online();
        uint epoch = s.Epoch;
        var fx = Step(ref s, Spotify.SessionEventKind.Logout);

        Assert.Equal(Spotify.SessionEffects.CloseAll | Spotify.SessionEffects.ClearCredential, fx);
        Assert.Equal(Spotify.SessionPhase.Offline, s.Phase);
        Assert.Equal(epoch + 1, s.Epoch);           // a monotonic epoch: nothing in flight may come back
        Assert.True(s.AccessToken.IsEmpty);
        Assert.True(s.ClientToken.IsEmpty);
        Assert.False(s.HasCredential);
        Assert.Equal(Spotify.Tier.Unknown, s.Tier);
    }

    [Fact]
    public void An_event_for_a_phase_that_has_moved_on_is_ignored()
    {
        var s = default(Spotify.Session);
        Assert.Equal(Spotify.SessionEffects.None, Step(ref s, Spotify.SessionEventKind.Connected));
        Assert.Equal(Spotify.SessionEffects.None, Step(ref s, Spotify.SessionEventKind.HandshakeOk));
        Assert.Equal(Spotify.SessionEffects.None, Step(ref s, Spotify.SessionEventKind.Hosts));
        Assert.Equal(Spotify.SessionPhase.Offline, s.Phase);
    }
}

public class RequestFoldTests
{
    static (string Path, Spotify.Verb Verb, Spotify.ApiHost Host, Spotify.HeaderSet Headers, string SyncReason)
        Fold(Spotify.RequestKind kind, string id = "", string id2 = "", bool flag = false)
    {
        var session = default(Spotify.Session);
        var args = new Spotify.RequestArgs { Id = id, Id2 = id2, Flag = flag };
        Span<char> path = stackalloc char[512];
        var request = Spotify.Build(session, kind, args, path);
        return (new string(request.Path), request.Verb, request.Host, request.Headers, new string(request.SyncReason));
    }

    [Fact]
    public void Extended_metadata_is_one_batched_protobuf_post()
    {
        var r = Fold(Spotify.RequestKind.ExtendedMetadata);
        Assert.Equal("/extended-metadata/v0/extended-metadata", r.Path);
        Assert.Equal(Spotify.Verb.Post, r.Verb);
        Assert.Equal(Spotify.ApiHost.Spclient, r.Host);
        Assert.True(r.Headers.HasFlag(Spotify.HeaderSet.ContentProtobuf));
        Assert.True(r.Headers.HasFlag(Spotify.HeaderSet.Bearer));
        Assert.True(r.Headers.HasFlag(Spotify.HeaderSet.ClientToken));
    }

    [Fact]
    public void The_pathfinder_picks_its_platform_header_from_the_one_flag()
    {
        var desktop = Fold(Spotify.RequestKind.Pathfinder);
        Assert.Equal("/pathfinder/v2/query", desktop.Path);
        Assert.Equal(Spotify.ApiHost.Pathfinder, desktop.Host);
        Assert.True(desktop.Headers.HasFlag(Spotify.HeaderSet.PathfinderDesktop));
        Assert.False(desktop.Headers.HasFlag(Spotify.HeaderSet.PathfinderWeb));

        var web = Fold(Spotify.RequestKind.Pathfinder, flag: true);
        Assert.True(web.Headers.HasFlag(Spotify.HeaderSet.PathfinderWeb));
    }

    [Fact]
    public void A_playlist_read_and_its_diff()
    {
        Assert.Equal("/playlist/v2/playlist/37i9dQZF1DXcBWIGoYBM5M",
            Fold(Spotify.RequestKind.PlaylistRead, "37i9dQZF1DXcBWIGoYBM5M").Path);
        Assert.Equal("/playlist/v2/playlist/abc/diff?revision=12%2Cdead",
            Fold(Spotify.RequestKind.PlaylistDiff, "abc", "12,dead").Path);
    }

    [Fact]
    public void A_mutation_carries_the_form_content_type_and_its_sync_reason()
    {
        // The gateway gates these on the captured tuple: a request missing it 200-OKs against a passive handler that
        // never mutates state, which is the silent no-op this flag exists to prevent.
        var edit = Fold(Spotify.RequestKind.PlaylistChanges, "abc");
        Assert.Equal("/playlist/v2/playlist/abc/changes", edit.Path);
        Assert.Equal(Spotify.Verb.Post, edit.Verb);
        Assert.True(edit.Headers.HasFlag(Spotify.HeaderSet.ContentForm));
        Assert.False(edit.Headers.HasFlag(Spotify.HeaderSet.ContentProtobuf));
        Assert.True(edit.Headers.HasFlag(Spotify.HeaderSet.Identity));
        Assert.Equal("CAk=", edit.SyncReason);

        Assert.Equal("CAw=", Fold(Spotify.RequestKind.PlaylistCreate, "abc").SyncReason);
        Assert.Equal("CA8QAQ==", Fold(Spotify.RequestKind.PlaylistSignals, "abc").SyncReason);
    }

    [Fact]
    public void The_rootlist_routes_hang_off_the_username()
    {
        Assert.Equal("/playlist/v2/user/bob/rootlist?decorate=revision",
            Fold(Spotify.RequestKind.RootlistRead, "bob").Path);
        var changes = Fold(Spotify.RequestKind.RootlistChanges, "bob");
        Assert.Equal("/playlist/v2/user/bob/rootlist/changes", changes.Path);
        Assert.Equal("CAk=", changes.SyncReason);
    }

    [Fact]
    public void The_recents_list_has_a_cold_and_a_diff_flavour()
    {
        var cold = Fold(Spotify.RequestKind.RecentsPage);
        Assert.Equal("/playlist/v2/list/recents/page", cold.Path);
        Assert.Equal("CAwQAQ==", cold.SyncReason);
        Assert.True(cold.Headers.HasFlag(Spotify.HeaderSet.AcceptListItems));
        Assert.False(cold.Headers.HasFlag(Spotify.HeaderSet.AppliedLenses));

        var diff = Fold(Spotify.RequestKind.RecentsDiff);
        Assert.Equal("/playlist/v2/list/recents/page/diff", diff.Path);
        Assert.Equal("CAEQAQ==", diff.SyncReason);
        Assert.True(diff.Headers.HasFlag(Spotify.HeaderSet.AppliedLenses));
    }

    [Fact]
    public void Connect_state_routes_name_both_devices()
    {
        var put = Fold(Spotify.RequestKind.ConnectStatePut, "device-1");
        Assert.Equal("/connect-state/v1/devices/device-1", put.Path);
        Assert.Equal(Spotify.Verb.Put, put.Verb);
        Assert.True(put.Headers.HasFlag(Spotify.HeaderSet.ConnectionId));
        Assert.True(put.Headers.HasFlag(Spotify.HeaderSet.GzipBody));

        Assert.Equal("/connect-state/v1/connect/transfer/from/a/to/b",
            Fold(Spotify.RequestKind.ConnectStateTransfer, "a", "b").Path);
        Assert.Equal("/connect-state/v1/player/command/from/a/to/b",
            Fold(Spotify.RequestKind.ConnectStateCommand, "a", "b").Path);
        Assert.Equal("/connect-state/v1/connect/volume/from/a/to/b",
            Fold(Spotify.RequestKind.ConnectStateVolume, "a", "b").Path);
    }

    [Fact]
    public void The_remaining_read_routes()
    {
        Assert.Equal("/collection/v2/paging", Fold(Spotify.RequestKind.CollectionPage).Path);
        Assert.Equal("/collection/v2/delta", Fold(Spotify.RequestKind.CollectionDelta).Path);
        Assert.Equal("/collection/v2/write", Fold(Spotify.RequestKind.CollectionWrite).Path);
        Assert.Equal("/context-resolve/v1/spotify%3Aalbum%3Ax",
            Fold(Spotify.RequestKind.ContextResolve, "spotify:album:x").Path);
        Assert.Equal("/context-resolve/v1/autoplay", Fold(Spotify.RequestKind.Autoplay).Path);
        Assert.Equal("/context-resolve/v1/autopodcast", Fold(Spotify.RequestKind.Autoplay, flag: true).Path);
        Assert.Equal("/storage-resolve/files/audio/interactive/deadbeef",
            Fold(Spotify.RequestKind.StorageResolve, "deadbeef").Path);
        Assert.Equal("/melody/v1/time", Fold(Spotify.RequestKind.ServerTime).Path);
        Assert.Equal("/user-profile-view/v3/profile/bob", Fold(Spotify.RequestKind.Profile, "bob").Path);
        Assert.Equal("/popcount/v2/playlist/abc/count", Fold(Spotify.RequestKind.Popcount, "abc").Path);
    }

    [Fact]
    public void An_id_is_percent_encoded_into_its_path_segment()
    {
        // A playlist id is base62, but a device id and a user name are not guaranteed to be url-safe, and a '/' in an
        // id would otherwise change which route the request reaches.
        Assert.Equal("/playlist/v2/playlist/a%2Fb%20c", Fold(Spotify.RequestKind.PlaylistRead, "a/b c").Path);
    }

    [Fact]
    public void A_custom_request_is_taken_verbatim()
    {
        var session = default(Spotify.Session);
        var args = new Spotify.RequestArgs
        {
            Path = "/some/new/route?x=1",
            Host = Spotify.ApiHost.SpclientWg,
            Verb = Spotify.Verb.Delete,
            Headers = Spotify.HeaderSet.Bearer | Spotify.HeaderSet.AcceptJson,
        };
        Span<char> path = stackalloc char[256];
        var request = Spotify.Build(session, Spotify.RequestKind.Custom, args, path);

        Assert.Equal("/some/new/route?x=1", new string(request.Path));
        Assert.Equal(Spotify.ApiHost.SpclientWg, request.Host);
        Assert.Equal(Spotify.Verb.Delete, request.Verb);
        Assert.Equal(Spotify.HeaderSet.Bearer | Spotify.HeaderSet.AcceptJson, request.Headers);
    }

    [Fact]
    public void The_body_the_caller_passed_is_the_body_that_comes_back()
    {
        var session = default(Spotify.Session);
        byte[] body = [1, 2, 3];
        var args = new Spotify.RequestArgs { Body = body };
        Span<char> path = stackalloc char[128];
        var request = Spotify.Build(session, Spotify.RequestKind.ExtendedMetadata, args, path);
        Assert.Equal(body, request.Body.ToArray());
    }
}

/// <summary>The credential slot, from the session's side. Joins `PlatformCollection` because it swaps the AMBIENT
/// slot, which is process state that `CredentialSlotTests` also owns — two of those racing would be two tests
/// fighting over one static.</summary>
[Collection(PlatformCollection.Name)]
public class SessionCredentialTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "wavee-spotify-tests", Guid.NewGuid().ToString("N"));

    public SessionCredentialTests()
    {
        Directory.CreateDirectory(_dir);
        Platform.UseCredentialSlot(new FileLocalStore(Path.Combine(_dir, "store.json")), new NoOpProtector());
    }

    public void Dispose()
    {
        // Put the process back: an empty slot inside this class's own temp directory (never the real profile), and a
        // session that is Offline rather than whatever this fact left behind.
        Spotify.Logout();
        Platform.UseCredentialSlot(new FileLocalStore(Path.Combine(_dir, "reset.json")), new NoOpProtector());
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>The map between the two credential types is a CAST, so the numbers have to agree member for member —
    /// `Platform/Platform.cs` §5 says so from its side and this is the assertion behind it.</summary>
    [Fact]
    public void The_two_credential_kinds_are_numbered_alike()
    {
        Assert.Equal((byte)CredentialKind.None, (byte)Spotify.CredentialKind.None);
        Assert.Equal((byte)CredentialKind.ReusableBlob, (byte)Spotify.CredentialKind.ReusableBlob);
        Assert.Equal((byte)CredentialKind.OAuthToken, (byte)Spotify.CredentialKind.OAuthToken);
    }

    [Fact]
    public void A_login_with_an_empty_slot_fails_with_NoCredential_and_opens_nothing()
    {
        Spotify.Login();

        Assert.Equal(Spotify.SessionPhase.Failed, Spotify.Current.Phase);
        Assert.Equal(Spotify.SessionFault.NoCredential, Spotify.Current.Fault);
        Assert.Equal(Spotify.SessionPhase.Failed, Spotify.Status.Value);
        Assert.False(Spotify.Current.HasCredential);
    }

    [Fact]
    public void A_stored_credential_is_seen_by_the_session()
    {
        Platform.SaveCredential(new Credential(CredentialKind.ReusableBlob, "someone", "c2VjcmV0", null));
        Assert.True(Platform.TryLoadCredential(out var loaded));
        Assert.Equal("someone", loaded.Username);
        Assert.Equal(CredentialKind.ReusableBlob, loaded.Kind);
        // The session's own login is NOT driven here: a credential present means the AP thread would open a socket,
        // and these tests are offline. `SessionStepTests` covers what the fold does with Flag: true.
    }

    /// <summary>The one session effect that reaches the disk. The other half of the rule — that a DROP must never
    /// wipe the slot, so a connectivity blip cannot cost the user their stored login — is the fold's, and
    /// `SessionStepTests.A_network_drop_never_clears_the_credential` pins it there: the effect is never returned, so
    /// it can never be dispatched.</summary>
    [Fact]
    public void A_logout_wipes_the_slot()
    {
        Platform.SaveCredential(new Credential(CredentialKind.ReusableBlob, "someone", "c2VjcmV0", null));
        Assert.True(Platform.TryLoadCredential(out _));

        Spotify.Logout();

        Assert.False(Platform.TryLoadCredential(out _));
        Assert.Equal(Spotify.SessionPhase.Offline, Spotify.Current.Phase);
    }

    /// <summary>The other way to end a session: every socket down, the slot untouched — what a shutdown or a headless
    /// run's exit calls, so the next launch resumes without a sign-in.</summary>
    [Fact]
    public void A_disconnect_keeps_the_slot()
    {
        Platform.SaveCredential(new Credential(CredentialKind.ReusableBlob, "someone", "c2VjcmV0", null));

        Spotify.Disconnect();

        Assert.True(Platform.TryLoadCredential(out var kept));
        Assert.Equal("someone", kept.Username);
        Assert.Equal(Spotify.SessionPhase.Offline, Spotify.Current.Phase);
        Assert.Equal(Spotify.SessionPhase.Offline, Spotify.Status.Value);
    }
}
