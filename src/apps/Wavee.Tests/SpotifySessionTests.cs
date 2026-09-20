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
//
// Gap batch B5 added: the Premium gate (D12), the refusal verdicts (D24), the SignedOut effect (G-036/G-037), the
// client-token refresh fold (G-035), `session.lastAccount` (G-031) and the sign-in request a resume-less login makes (G-030).
//
// B4 (the transports decoupled): the single `Dropped`/`Retry` became `ApDropped`/`DealerDropped` and `ApRetry`/`DealerRetry`,
// each transport with its own `LinkPhase`, epoch and backoff. An AP reset keeps the dealer, its connection id and the
// session epoch (no re-announce); a dealer drop owes the hello on its new id (B3); a stale epoch's word is refused without
// touching the other transport; Online = signed in with the dealer up. The step helper stamps each event with the epoch a
// LIVE thread of its transport would carry, so a fact that wants a stale word says so with `epoch:`.
//
// B6 (the AP keepalive, whose own fold is `ApKeepAliveTests`) closed two gaps B4 left in this fold: an AP reconnect whose
// product packet came late keeps the known tier instead of ending the login, and "the same connection id" means the same
// BYTES — so the facts that need two distinct ids write real text into the session arena (`Spotify.AddSessionText`) instead
// of pointing at made-up offsets. The read timeout's rule became "the backstop outlasts every keepalive window".

using FluentGpu.Foundation;
using Wavee;
using Xunit;

namespace Wavee.Tests;

public class SessionStepTests
{
    static Spotify.SessionEffects Step(ref Spotify.Session s, Spotify.SessionEventKind kind,
        Spotify.TokenRef text = default, Spotify.TokenRef text2 = default, long number = 0, bool flag = false, uint? epoch = null)
        => Spotify.Step(ref s, new Spotify.SessionEvent(kind, Text: text, Text2: text2, Number: number, Flag: flag,
            Epoch: epoch ?? Live(in s, kind)));

    /// <summary>The epoch a LIVE producer stamps on <paramref name="kind"/> (B4): the AP thread's words carry the AP epoch
    /// it started under, the dealer's the session (dealer) epoch, a retry the epoch it was armed for — for a thread or a
    /// timer that is still current, the session's own. The login's words carry none.</summary>
    static uint Live(in Spotify.Session s, Spotify.SessionEventKind kind) => Spotify.LinkOf(kind) switch
    {
        Spotify.SessionLink.Ap => s.ApEpoch,
        Spotify.SessionLink.Dealer => s.Epoch,
        _ => 0u,
    };

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

    /// <summary>A login up to the point the AP answers the credential.</summary>
    static Spotify.Session Handshaken()
    {
        var s = default(Spotify.Session);
        Step(ref s, Spotify.SessionEventKind.Login, flag: true);
        Step(ref s, Spotify.SessionEventKind.Hosts);
        Step(ref s, Spotify.SessionEventKind.Connected);
        Step(ref s, Spotify.SessionEventKind.HandshakeOk);
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
        var fx = Step(ref s, Spotify.SessionEventKind.Welcome, number: (long)Spotify.Tier.Premium);

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
        // Real ids in the session arena: "the same id" is compared by its bytes (B6's follow-up on B3).
        Spotify.TokenRef first = Spotify.AddSessionText("conn-first-" + Guid.NewGuid().ToString("N"));
        Spotify.TokenRef other = Spotify.AddSessionText("conn-other-" + Guid.NewGuid().ToString("N"));
        Spotify.TokenRef next = Spotify.AddSessionText("conn-next--" + Guid.NewGuid().ToString("N"));

        // The first connection id: the hello (headless plan §1.6 item 5).
        Assert.Equal(Spotify.SessionEffects.AnnounceDevice, Step(ref s, Spotify.SessionEventKind.DealerOnline, text: first));
        // B3: an EXACT repeat of the id already held (idempotent redelivery) is the only silent case.
        Assert.Equal(Spotify.SessionEffects.None, Step(ref s, Spotify.SessionEventKind.DealerOnline, text: first));
        // B3: a DIFFERENT connection id while already Online is adopted but was silently swallowed before this fix
        // (`wasOnline ? None : AnnounceDevice` — Spotify.cs:319, the "other devices do not show Wavee" bug) — the
        // hello is owed to the id, not the phase transition.
        Assert.Equal(Spotify.SessionEffects.AnnounceDevice, Step(ref s, Spotify.SessionEventKind.DealerOnline, text: other));
        Assert.Equal(other, s.ConnectionId);

        // A DEALER drop and its reconnect is a new connection id, and a new hello. (B4 changed this leg: the old single
        // `Dropped` → `Retry` → `Hosts` walk re-ran the whole AP ladder to get here; the dealer now reconnects alone, on
        // its own retry, and B3's rule — the hello is owed to the new id — is what still announces.)
        Step(ref s, Spotify.SessionEventKind.DealerDropped, number: (long)Spotify.SessionFault.Network);
        Assert.Equal(Spotify.SessionEffects.OpenDealer, Step(ref s, Spotify.SessionEventKind.DealerRetry));
        Assert.Equal(Spotify.SessionEffects.AnnounceDevice, Step(ref s, Spotify.SessionEventKind.DealerOnline, text: next));
        Assert.Equal(next, s.ConnectionId);
    }

    /// <summary>B6's follow-up on B3: "an exact repeat of the id already held" is a repeat of its BYTES. The dealer thread
    /// writes every pusher hello's id into the session arena afresh, so a redelivered identical id arrives as a NEW slice —
    /// and the first cut compared the slices (<c>==</c> on the <c>TokenRef</c>), so it announced that repeat every time.</summary>
    [Fact]
    public void A_redelivered_connection_id_is_a_repeat_whatever_slice_carries_it()
    {
        string id = "conn-" + Guid.NewGuid().ToString("N");
        Spotify.TokenRef held = Spotify.AddSessionText(id);
        Spotify.TokenRef again = Spotify.AddSessionText(id);                       // the same text, learnt a second time
        Spotify.TokenRef other = Spotify.AddSessionText("conn-" + Guid.NewGuid().ToString("N"));   // same length, other bytes
        Assert.NotEqual(held, again);                                               // two slices…
        Assert.True(Spotify.SameText(held, again));                                 // …one id
        Assert.Equal(held.Length, other.Length);
        Assert.False(Spotify.SameText(held, other));
        Assert.False(Spotify.SameText(default, held));                             // no id held is never "the same"
        Assert.True(Spotify.SameText(default, default));

        var s = Handshaken();
        Step(ref s, Spotify.SessionEventKind.Welcome, number: (long)Spotify.Tier.Premium);
        Step(ref s, Spotify.SessionEventKind.ClientTokenMinted);
        Step(ref s, Spotify.SessionEventKind.AccessTokenMinted);

        Assert.Equal(Spotify.SessionEffects.AnnounceDevice, Step(ref s, Spotify.SessionEventKind.DealerOnline, text: held));
        Assert.Equal(Spotify.SessionEffects.None, Step(ref s, Spotify.SessionEventKind.DealerOnline, text: again));
        Assert.True(s.IsOnline);
        Assert.Equal(Spotify.SessionEffects.AnnounceDevice, Step(ref s, Spotify.SessionEventKind.DealerOnline, text: other));
        Assert.Equal(other, s.ConnectionId);
    }

    [Fact]
    public void A_disconnect_closes_everything_but_keeps_the_credential_and_the_boot_identity()
    {
        var s = Online();
        s.DeviceId = new Spotify.TokenRef(100, 8);
        s.ClientId = new Spotify.TokenRef(108, 8);
        s.Locale = new Spotify.TokenRef(116, 2);
        uint epoch = s.Epoch, apEpoch = s.ApEpoch;
        var fx = Step(ref s, Spotify.SessionEventKind.Disconnect);

        Assert.Equal(Spotify.SessionEffects.CloseAll, fx);                  // never ClearCredential: that is Logout's alone
        Assert.Equal(Spotify.SessionPhase.Offline, s.Phase);
        Assert.Equal(epoch + 1, s.Epoch);
        Assert.Equal(apEpoch + 1, s.ApEpoch);                               // the login ended: BOTH transports' epochs move
        Assert.Equal(Spotify.LinkPhase.Down, s.Ap);
        Assert.Equal(Spotify.LinkPhase.Down, s.Dealer);
        Assert.False(s.LoggedIn);
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

    /// <summary>G-065: `--fake` presents as signed in through a PURE fact, not a login — no socket effect, no epoch
    /// bump (nothing was ever in flight to abandon) and no credential-slot effect (the slot is never touched, so a
    /// real credential on disk survives a `--fake` run untouched).</summary>
    [Fact]
    public void FakeOnline_presents_as_signed_in_with_no_effect_and_no_epoch_bump()
    {
        var s = default(Spotify.Session);
        var e = new Spotify.SessionEvent(Spotify.SessionEventKind.FakeOnline,
            Id: new StringId(1), Id2: new StringId(2), Id3: new StringId(3));
        var fx = Spotify.Step(ref s, e);

        Assert.Equal(Spotify.SessionEffects.None, fx);
        Assert.Equal(Spotify.SessionPhase.Online, s.Phase);
        Assert.True(s.IsOnline);
        Assert.Equal(Spotify.SessionFault.None, s.Fault);
        Assert.Equal(Spotify.Tier.Premium, s.Tier);          // the demo previews every gated surface (D12 does not apply)
        Assert.True(s.HasCredential);                        // the auth fold (Shell.FoldAuth) must read this as signed in
        Assert.Equal(0u, s.Epoch);                            // nothing was abandoned — this never opened a socket
        Assert.Equal(0u, s.ApEpoch);
        Assert.Equal(Spotify.LinkPhase.Down, s.Ap);           // the one Online that owns no transport (B4)
        Assert.Equal(Spotify.LinkPhase.Down, s.Dealer);
        Assert.Equal(new StringId(1), s.Username);
        Assert.Equal(new StringId(2), s.Country);
        Assert.Equal(new StringId(3), s.Product);
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
    public void A_definitive_rejection_clears_the_credential_signs_out_and_is_terminal()
    {
        var s = default(Spotify.Session);
        Step(ref s, Spotify.SessionEventKind.Login, flag: true);
        var fx = Step(ref s, Spotify.SessionEventKind.AuthRejected, number: (long)Spotify.RejectVerdict.Definitive);

        Assert.Equal(Spotify.SessionPhase.Failed, s.Phase);
        Assert.Equal(Spotify.SessionFault.CredentialRejected, s.Fault);
        Assert.False(s.HasCredential);
        Assert.Equal(Spotify.SessionEffects.ClearCredential | Spotify.SessionEffects.CloseAll | Spotify.SessionEffects.SignedOut, fx);

        // Terminal: nothing but a fresh Login moves it again — neither transport's retry.
        Assert.Equal(Spotify.SessionEffects.None, Step(ref s, Spotify.SessionEventKind.ApRetry));
        Assert.Equal(Spotify.SessionEffects.None, Step(ref s, Spotify.SessionEventKind.DealerRetry));
        Assert.Equal(Spotify.SessionEffects.None, Step(ref s, Spotify.SessionEventKind.Welcome, number: (long)Spotify.Tier.Premium));
        Assert.Equal(Spotify.SessionPhase.Failed, s.Phase);
    }

    /// <summary>D24: a refusal that is not a verdict on the credential — and the DEFAULT verdict, so an unclassified one —
    /// stops the login and keeps the credential (the chip then offers Reconnect, never a sign-in).</summary>
    [Fact]
    public void A_transient_or_unclassified_refusal_keeps_the_credential()
    {
        var s = default(Spotify.Session);
        Step(ref s, Spotify.SessionEventKind.Login, flag: true);
        uint epoch = s.Epoch;
        var fx = Step(ref s, Spotify.SessionEventKind.AuthRejected);

        Assert.Equal(Spotify.SessionPhase.Failed, s.Phase);
        Assert.Equal(Spotify.SessionFault.LoginRefused, s.Fault);
        Assert.True(s.HasCredential);
        Assert.Equal(epoch + 1, s.Epoch);
        Assert.Equal(Spotify.SessionEffects.CloseAll, fx);
    }

    [Fact]
    public void The_ap_saying_premium_required_clears_the_credential_as_not_premium()
    {
        var s = default(Spotify.Session);
        Step(ref s, Spotify.SessionEventKind.Login, flag: true);
        var fx = Step(ref s, Spotify.SessionEventKind.AuthRejected, number: (long)Spotify.RejectVerdict.NotPremium);

        Assert.Equal(Spotify.SessionFault.NotPremium, s.Fault);
        Assert.False(s.HasCredential);
        Assert.Equal(Spotify.SessionEffects.ClearCredential | Spotify.SessionEffects.CloseAll | Spotify.SessionEffects.SignedOut, fx);
    }

    /// <summary>D12, 0.2.9's Premium gate: a known Free account is refused at the welcome — never saved, never minted for —
    /// and its credential wiped, so the next launch cannot resume into the same wall.</summary>
    [Fact]
    public void A_free_account_is_refused_at_the_welcome_and_its_credential_cleared()
    {
        var s = Handshaken();
        uint epoch = s.Epoch;
        var fx = Step(ref s, Spotify.SessionEventKind.Welcome, number: (long)Spotify.Tier.Free);

        Assert.Equal(Spotify.SessionPhase.Failed, s.Phase);
        Assert.Equal(Spotify.SessionFault.NotPremium, s.Fault);
        Assert.Equal(Spotify.Tier.Free, s.Tier);
        Assert.False(s.HasCredential);
        Assert.Equal(epoch + 1, s.Epoch);
        Assert.Equal(Spotify.SessionEffects.CloseAll | Spotify.SessionEffects.ClearCredential | Spotify.SessionEffects.SignedOut, fx);
    }

    /// <summary>An UNKNOWN tier on a FIRST login (the product packet missed its window) is refused too — never optimistically
    /// Premium — but a late packet is not a verdict on the account: the credential stays, and the next attempt may well
    /// pass. (A reconnect's late packet is the fact below.)</summary>
    [Fact]
    public void An_unknown_tier_is_refused_but_its_credential_is_kept()
    {
        var s = Handshaken();
        var fx = Step(ref s, Spotify.SessionEventKind.Welcome, number: (long)Spotify.Tier.Unknown);

        Assert.Equal(Spotify.SessionPhase.Failed, s.Phase);
        Assert.Equal(Spotify.SessionFault.NotPremium, s.Fault);
        Assert.True(s.HasCredential);
        Assert.Equal(Spotify.SessionEffects.CloseAll, fx);
    }

    /// <summary>B6's follow-up on B4: an AP RECONNECT's welcome whose ProductInfo (0x50) missed its 3 s trailer window
    /// carries an Unknown tier, and under the D12 gate that ended the WHOLE login — after B4, the one way left for an AP
    /// reset (~29 a day) to take the session out of Online. The account's tier was settled by this login's first welcome and
    /// a late packet is no verdict, so the known tier stands, and with it the product the UI paints; nothing closes.</summary>
    [Fact]
    public void An_ap_reconnect_whose_product_packet_came_late_keeps_the_known_tier()
    {
        var s = Online();
        var product = new StringId(7);
        s.Product = product;                                                // what the first welcome's 0x50 said
        uint epoch = s.Epoch;
        Step(ref s, Spotify.SessionEventKind.ApDropped, number: (long)Spotify.SessionFault.Network);
        Step(ref s, Spotify.SessionEventKind.ApRetry);
        Step(ref s, Spotify.SessionEventKind.Hosts, text: new(0, 4), text2: new(4, 4));
        Step(ref s, Spotify.SessionEventKind.Connected);
        Step(ref s, Spotify.SessionEventKind.HandshakeOk);

        var fx = Spotify.Step(ref s, new Spotify.SessionEvent(Spotify.SessionEventKind.Welcome,
            Id3: new StringId(9), Number: (long)Spotify.Tier.Unknown, Epoch: s.ApEpoch));

        Assert.Equal(Spotify.SessionEffects.SaveCredential | Spotify.SessionEffects.MintClientToken | Spotify.SessionEffects.Welcome, fx);
        Assert.Equal(Spotify.Tier.Premium, s.Tier);
        Assert.Equal(product, s.Product);
        Assert.Equal(Spotify.SessionPhase.Online, s.Phase);
        Assert.Equal(Spotify.SessionFault.None, s.Fault);
        Assert.True(s.LoggedIn);
        Assert.True(s.HasCredential);
        Assert.Equal(Spotify.LinkPhase.Up, s.Ap);
        Assert.Equal(Spotify.LinkPhase.Up, s.Dealer);
        Assert.Equal(epoch, s.Epoch);                                       // nothing re-syncs, nothing re-announces
        Assert.Equal(new Spotify.TokenRef(16, 4), s.ConnectionId);
    }

    /// <summary>…but a VERDICT is a verdict whichever welcome carries it: a reconnect that says Free ends the login and wipes
    /// the credential exactly as a first login's would (D12).</summary>
    [Fact]
    public void An_ap_reconnect_that_says_free_still_ends_the_login()
    {
        var s = Online();
        Step(ref s, Spotify.SessionEventKind.ApDropped, number: (long)Spotify.SessionFault.Network);
        Step(ref s, Spotify.SessionEventKind.ApRetry);
        Step(ref s, Spotify.SessionEventKind.Hosts);
        Step(ref s, Spotify.SessionEventKind.Connected);
        Step(ref s, Spotify.SessionEventKind.HandshakeOk);
        var fx = Step(ref s, Spotify.SessionEventKind.Welcome, number: (long)Spotify.Tier.Free);

        Assert.Equal(Spotify.SessionEffects.CloseAll | Spotify.SessionEffects.ClearCredential | Spotify.SessionEffects.SignedOut, fx);
        Assert.Equal(Spotify.SessionPhase.Failed, s.Phase);
        Assert.Equal(Spotify.SessionFault.NotPremium, s.Fault);
        Assert.Equal(Spotify.Tier.Free, s.Tier);
        Assert.False(s.LoggedIn);
        Assert.False(s.HasCredential);
    }

    [Fact]
    public void Only_a_lost_account_signs_out()
    {
        var online = Online();
        Assert.True(SignsOut(online, Spotify.SessionEventKind.Logout));
        Assert.False(SignsOut(online, Spotify.SessionEventKind.Disconnect));
        Assert.False(SignsOut(online, Spotify.SessionEventKind.ApDropped, (long)Spotify.SessionFault.Network));
        Assert.False(SignsOut(online, Spotify.SessionEventKind.DealerDropped, (long)Spotify.SessionFault.Network));
        Assert.False(SignsOut(Handshaken(), Spotify.SessionEventKind.AuthRejected, (long)Spotify.RejectVerdict.Transient));
        Assert.True(SignsOut(Handshaken(), Spotify.SessionEventKind.AuthRejected, (long)Spotify.RejectVerdict.Definitive));

        static bool SignsOut(Spotify.Session s, Spotify.SessionEventKind kind, long number = 0)
            => (Step(ref s, kind, number: number) & Spotify.SessionEffects.SignedOut) != 0;
    }

    /// <summary>G-035: a client-token refresh while online replaces the attestation alone; only the login's first
    /// attestation gates a bearer mint.</summary>
    [Fact]
    public void A_client_token_refresh_while_online_mints_nothing_else()
    {
        var s = Online();
        var fx = Step(ref s, Spotify.SessionEventKind.ClientTokenMinted, text: new(40, 4), number: 99_000);
        Assert.Equal(Spotify.SessionEffects.None, fx);
        Assert.Equal(new Spotify.TokenRef(40, 4), s.ClientToken);
        Assert.Equal(99_000, s.ClientTokenExpiresAtMs);
        Assert.Equal(Spotify.SessionPhase.Online, s.Phase);
    }

    /// <summary>Either transport's drop: never the credential, never a sign-out. (B4 split this fact in two: the old single
    /// `Dropped` also asserted `CloseAll | Backoff` and an emptied connection id, which is now true of the DEALER's drop
    /// only — see the two facts below.)</summary>
    [Fact]
    public void A_network_drop_never_clears_the_credential()
    {
        foreach (var kind in new[] { Spotify.SessionEventKind.ApDropped, Spotify.SessionEventKind.DealerDropped })
        {
            var s = Online();
            var fx = Step(ref s, kind, number: (long)Spotify.SessionFault.Network);

            Assert.True(s.HasCredential);
            Assert.True(s.LoggedIn);                                        // a drop never signs the account out
            Assert.Equal(Spotify.SessionEffects.None, fx & (Spotify.SessionEffects.ClearCredential | Spotify.SessionEffects.SignedOut));
        }
    }

    /// <summary>B4, the headline: an AP reset (~29 a day, <c>SocketException 10054</c>) reconnects the AP ALONE. The dealer,
    /// its connection id, the session epoch and the phase all stay — so no <c>NewDevice</c> re-announce, no library resync,
    /// no chrome flicker — and the AP's whole re-login walks under <c>s.Ap</c> without touching any of them.</summary>
    [Fact]
    public void An_ap_drop_keeps_the_dealer_and_its_connection_id_and_owes_no_announce()
    {
        var s = Online();
        uint epoch = s.Epoch, apEpoch = s.ApEpoch;

        var fx = Step(ref s, Spotify.SessionEventKind.ApDropped, number: (long)Spotify.SessionFault.Network);
        Assert.Equal(Spotify.SessionEffects.CloseAp | Spotify.SessionEffects.ApBackoff, fx);     // never CloseDealer
        Assert.Equal(Spotify.SessionPhase.Online, s.Phase);
        Assert.Equal(Spotify.SessionFault.None, s.Fault);
        Assert.Equal(Spotify.LinkPhase.Waiting, s.Ap);
        Assert.Equal(Spotify.LinkPhase.Up, s.Dealer);
        Assert.Equal(new Spotify.TokenRef(16, 4), s.ConnectionId);
        Assert.Equal(epoch, s.Epoch);                                       // the Connect mailbox and the library keep theirs
        Assert.Equal(apEpoch + 1, s.ApEpoch);
        Assert.Equal(1u, s.ApAttempt);

        // The AP's retry re-runs its whole procedure; nothing in it re-opens the dealer or re-announces the device.
        var all = Step(ref s, Spotify.SessionEventKind.ApRetry);
        Assert.Equal(Spotify.SessionEffects.ResolveHosts, all);
        all |= Step(ref s, Spotify.SessionEventKind.Hosts, text: new(0, 4), text2: new(4, 4));
        all |= Step(ref s, Spotify.SessionEventKind.Connected);
        all |= Step(ref s, Spotify.SessionEventKind.HandshakeOk);
        Assert.Equal(Spotify.SessionPhase.Online, s.Phase);                 // the AP's ladder is its own now
        all |= Step(ref s, Spotify.SessionEventKind.Welcome, number: (long)Spotify.Tier.Premium);
        all |= Step(ref s, Spotify.SessionEventKind.ClientTokenMinted, text: new(30, 4), number: 5_000);
        all |= Step(ref s, Spotify.SessionEventKind.AccessTokenMinted, text: new(34, 4), number: 6_000);

        Assert.Equal(Spotify.SessionEffects.None, all & (Spotify.SessionEffects.AnnounceDevice
            | Spotify.SessionEffects.OpenDealer | Spotify.SessionEffects.CloseDealer));
        Assert.Equal(Spotify.LinkPhase.Up, s.Ap);
        Assert.Equal(0u, s.ApAttempt);                                      // its own success resets its own ladder
        Assert.Equal(Spotify.SessionPhase.Online, s.Phase);
        Assert.Equal(new Spotify.TokenRef(16, 4), s.ConnectionId);
        Assert.Equal(epoch, s.Epoch);
        Assert.Equal(new Spotify.TokenRef(34, 4), s.AccessToken);           // the re-login's bearer is the session's
    }

    /// <summary>B4 + B3: a dealer drop reconnects the dealer ALONE — the AP channel, its epoch and its keys stay up — and its
    /// connection id goes with its socket, so the new one owes the hello. The session leaves Online for it: the consumers
    /// keyed on Online's edge and on the (dealer) epoch re-sync what that connection may have carried.</summary>
    [Fact]
    public void A_dealer_drop_keeps_the_ap_and_owes_an_announce_on_the_new_connection_id()
    {
        var s = Online();
        uint epoch = s.Epoch, apEpoch = s.ApEpoch;

        var fx = Step(ref s, Spotify.SessionEventKind.DealerDropped, number: (long)Spotify.SessionFault.Network);
        Assert.Equal(Spotify.SessionEffects.CloseDealer | Spotify.SessionEffects.DealerBackoff, fx);   // never CloseAp
        Assert.Equal(Spotify.SessionPhase.Reconnecting, s.Phase);
        Assert.False(s.IsOnline);
        Assert.Equal(Spotify.SessionFault.Network, s.Fault);
        Assert.True(s.ConnectionId.IsEmpty);                                // the id belonged to the socket that died
        Assert.Equal(Spotify.LinkPhase.Waiting, s.Dealer);
        Assert.Equal(Spotify.LinkPhase.Up, s.Ap);
        Assert.Equal(epoch + 1, s.Epoch);
        Assert.Equal(apEpoch, s.ApEpoch);

        Assert.Equal(Spotify.SessionEffects.OpenDealer, Step(ref s, Spotify.SessionEventKind.DealerRetry));
        Assert.Equal(Spotify.LinkPhase.Opening, s.Dealer);
        Assert.Equal(Spotify.SessionEffects.AnnounceDevice, Step(ref s, Spotify.SessionEventKind.DealerOnline, text: new(24, 4)));
        Assert.Equal(Spotify.SessionPhase.Online, s.Phase);
        Assert.Equal(Spotify.SessionFault.None, s.Fault);
        Assert.Equal(new Spotify.TokenRef(24, 4), s.ConnectionId);
        Assert.Equal(0u, s.DealerAttempt);
        Assert.Equal(apEpoch, s.ApEpoch);
    }

    /// <summary>Both sockets down at once (the host's network blipped — 10:10:59 on 2026-09-19 dropped both in the same
    /// millisecond): each reconnects on its own ladder, neither waits for the other, and the one that ends Online is the
    /// dealer's hello.</summary>
    [Fact]
    public void Both_transports_dropping_reconnect_each_on_its_own_ladder()
    {
        var s = Online();
        Assert.Equal(Spotify.SessionEffects.CloseDealer | Spotify.SessionEffects.DealerBackoff,
            Step(ref s, Spotify.SessionEventKind.DealerDropped, number: (long)Spotify.SessionFault.Network));
        Assert.Equal(Spotify.SessionEffects.CloseAp | Spotify.SessionEffects.ApBackoff,
            Step(ref s, Spotify.SessionEventKind.ApDropped, number: (long)Spotify.SessionFault.Network));
        Assert.Equal(Spotify.SessionPhase.Reconnecting, s.Phase);
        Assert.Equal(Spotify.LinkPhase.Waiting, s.Ap);
        Assert.Equal(Spotify.LinkPhase.Waiting, s.Dealer);
        Assert.Equal(1u, s.ApAttempt);
        Assert.Equal(1u, s.DealerAttempt);

        // The dealer's retry does not wait on the AP (its bearer is the login's, not the AP socket's), and the AP's does
        // not restart the login ladder (the account is still signed in).
        Assert.Equal(Spotify.SessionEffects.OpenDealer, Step(ref s, Spotify.SessionEventKind.DealerRetry));
        Assert.Equal(Spotify.SessionEffects.ResolveHosts, Step(ref s, Spotify.SessionEventKind.ApRetry));
        Assert.Equal(Spotify.SessionPhase.Reconnecting, s.Phase);

        Step(ref s, Spotify.SessionEventKind.Hosts);
        Step(ref s, Spotify.SessionEventKind.Connected);
        Step(ref s, Spotify.SessionEventKind.HandshakeOk);
        Assert.Equal(Spotify.SessionPhase.Reconnecting, s.Phase);
        Assert.Equal(Spotify.SessionEffects.SaveCredential | Spotify.SessionEffects.MintClientToken | Spotify.SessionEffects.Welcome,
            Step(ref s, Spotify.SessionEventKind.Welcome, number: (long)Spotify.Tier.Premium));
        Step(ref s, Spotify.SessionEventKind.ClientTokenMinted);
        // The dealer is Opening under its own retry: the re-login's bearer must not open a second one.
        Assert.Equal(Spotify.SessionEffects.None, Step(ref s, Spotify.SessionEventKind.AccessTokenMinted));
        Assert.Equal(Spotify.LinkPhase.Up, s.Ap);
        Assert.False(s.IsOnline);

        Assert.Equal(Spotify.SessionEffects.AnnounceDevice, Step(ref s, Spotify.SessionEventKind.DealerOnline, text: new(24, 4)));
        Assert.True(s.IsOnline);
        Assert.Equal(Spotify.LinkPhase.Up, s.Dealer);
    }

    /// <summary>B4, the epochs: each transport's words carry its own epoch, and a word from an epoch of THAT transport already
    /// abandoned folds to nothing — the other transport never hears of it. (Replaces "every drop bumps the epoch": the one
    /// shared epoch could not tell a dead AP thread's late drop from a live one, and every drop tore both sockets down.)</summary>
    [Fact]
    public void A_stale_epochs_words_are_ignored_without_touching_the_other_transport()
    {
        var s = Online();
        uint deadAp = s.ApEpoch;
        Step(ref s, Spotify.SessionEventKind.ApDropped, number: (long)Spotify.SessionFault.Network);
        Step(ref s, Spotify.SessionEventKind.ApRetry);
        Spotify.Session before = s;

        // The dead AP thread's late words — a second drop, a refusal, a Free welcome — each of which would end something.
        Assert.Equal(Spotify.SessionEffects.None,
            Step(ref s, Spotify.SessionEventKind.ApDropped, number: (long)Spotify.SessionFault.Network, epoch: deadAp));
        Assert.Equal(Spotify.SessionEffects.None,
            Step(ref s, Spotify.SessionEventKind.AuthRejected, number: (long)Spotify.RejectVerdict.Definitive, epoch: deadAp));
        Assert.Equal(Spotify.SessionEffects.None,
            Step(ref s, Spotify.SessionEventKind.Welcome, number: (long)Spotify.Tier.Free, epoch: deadAp));
        Assert.Equal(before, s);                                            // not one field moved
        Assert.True(s.IsOnline);
        Assert.Equal(new Spotify.TokenRef(16, 4), s.ConnectionId);

        // …and the mirror image: a dead dealer's late drop and late hello leave the live dealer and the AP alone.
        uint deadDealer = s.Epoch;
        Step(ref s, Spotify.SessionEventKind.DealerDropped, number: (long)Spotify.SessionFault.Network);
        Step(ref s, Spotify.SessionEventKind.DealerRetry);
        Step(ref s, Spotify.SessionEventKind.DealerOnline, text: new(28, 4));
        before = s;
        Assert.Equal(Spotify.SessionEffects.None,
            Step(ref s, Spotify.SessionEventKind.DealerDropped, number: (long)Spotify.SessionFault.Network, epoch: deadDealer));
        Assert.Equal(Spotify.SessionEffects.None,
            Step(ref s, Spotify.SessionEventKind.DealerOnline, text: new(99, 4), epoch: deadDealer));
        Assert.Equal(before, s);
        Assert.Equal(new Spotify.TokenRef(28, 4), s.ConnectionId);

        // A retry armed before a sign-out never outlives it, whichever transport armed it.
        uint armedAp = s.ApEpoch, armedDealer = s.Epoch;
        Step(ref s, Spotify.SessionEventKind.Logout);
        Step(ref s, Spotify.SessionEventKind.Login, flag: true);
        Assert.True(Spotify.IsStale(in s, new Spotify.SessionEvent(Spotify.SessionEventKind.ApRetry, Epoch: armedAp)));
        Assert.True(Spotify.IsStale(in s, new Spotify.SessionEvent(Spotify.SessionEventKind.DealerRetry, Epoch: armedDealer)));
        Assert.Equal(Spotify.SessionEffects.None, Step(ref s, Spotify.SessionEventKind.ApRetry, epoch: armedAp));
        Assert.Equal(Spotify.SessionEffects.None, Step(ref s, Spotify.SessionEventKind.DealerRetry, epoch: armedDealer));
    }

    /// <summary>A drop moves its own transport's epoch and no other (C4, per transport).</summary>
    [Fact]
    public void Each_drop_bumps_only_its_own_transports_epoch()
    {
        var s = Online();
        uint epoch = s.Epoch, apEpoch = s.ApEpoch;
        Step(ref s, Spotify.SessionEventKind.ApDropped, number: (long)Spotify.SessionFault.Network);
        Assert.Equal(apEpoch + 1, s.ApEpoch);
        Assert.Equal(epoch, s.Epoch);
        Step(ref s, Spotify.SessionEventKind.ApRetry);
        Step(ref s, Spotify.SessionEventKind.ApDropped, number: (long)Spotify.SessionFault.Network);
        Assert.Equal(apEpoch + 2, s.ApEpoch);
        Assert.Equal(epoch, s.Epoch);

        Step(ref s, Spotify.SessionEventKind.DealerDropped, number: (long)Spotify.SessionFault.Network);
        Assert.Equal(epoch + 1, s.Epoch);
        Assert.Equal(apEpoch + 2, s.ApEpoch);
        Step(ref s, Spotify.SessionEventKind.DealerRetry);
        Step(ref s, Spotify.SessionEventKind.DealerDropped, number: (long)Spotify.SessionFault.Network);
        Assert.Equal(epoch + 2, s.Epoch);
        Assert.Equal(apEpoch + 2, s.ApEpoch);
    }

    /// <summary>Which epoch each word must carry (B4): the AP thread's ladder, verdicts, drop and retry carry the AP epoch;
    /// the dealer's hello, drop and retry the session (dealer) epoch; a login, a sign-out, a disconnect and the two token
    /// mints — the login's, not a socket's — carry none and are never refused as stale.</summary>
    [Fact]
    public void Every_transport_word_carries_its_own_transports_epoch()
    {
        foreach (var kind in new[]
                 {
                     Spotify.SessionEventKind.Hosts, Spotify.SessionEventKind.Connected, Spotify.SessionEventKind.HandshakeOk,
                     Spotify.SessionEventKind.Welcome, Spotify.SessionEventKind.AuthRejected, Spotify.SessionEventKind.ApDropped,
                     Spotify.SessionEventKind.ApRetry,
                 })
            Assert.Equal(Spotify.SessionLink.Ap, Spotify.LinkOf(kind));
        foreach (var kind in new[]
                 {
                     Spotify.SessionEventKind.DealerOnline, Spotify.SessionEventKind.DealerDropped, Spotify.SessionEventKind.DealerRetry,
                 })
            Assert.Equal(Spotify.SessionLink.Dealer, Spotify.LinkOf(kind));
        foreach (var kind in new[]
                 {
                     Spotify.SessionEventKind.Login, Spotify.SessionEventKind.ClientTokenMinted, Spotify.SessionEventKind.AccessTokenMinted,
                     Spotify.SessionEventKind.Logout, Spotify.SessionEventKind.Disconnect, Spotify.SessionEventKind.FakeOnline,
                 })
        {
            Assert.Equal(Spotify.SessionLink.None, Spotify.LinkOf(kind));
            Assert.False(Spotify.IsStale(default, new Spotify.SessionEvent(kind, Epoch: 99)));
        }
    }

    /// <summary>What Online means now (B4, <c>SessionPhase.Online</c>'s doc): the account signed in — this login's Premium
    /// welcome — AND the dealer holding a connection id. The AP is part of it only until the login is done.</summary>
    [Fact]
    public void Online_is_the_signed_in_account_with_the_dealer_up_not_the_ap()
    {
        var s = Handshaken();
        Step(ref s, Spotify.SessionEventKind.Welcome, number: (long)Spotify.Tier.Premium);
        Step(ref s, Spotify.SessionEventKind.ClientTokenMinted);
        Assert.Equal(Spotify.SessionEffects.OpenDealer, Step(ref s, Spotify.SessionEventKind.AccessTokenMinted));
        Assert.True(s.LoggedIn);
        Assert.Equal(Spotify.LinkPhase.Up, s.Ap);
        Assert.Equal(Spotify.LinkPhase.Opening, s.Dealer);
        Assert.Equal(Spotify.SessionPhase.Minting, s.Phase);
        Assert.False(s.IsOnline);                                           // however far the AP got: no dealer, no Online

        Step(ref s, Spotify.SessionEventKind.DealerOnline, text: new(16, 4));
        Assert.True(s.IsOnline);

        Step(ref s, Spotify.SessionEventKind.ApDropped, number: (long)Spotify.SessionFault.Network);
        Assert.True(s.IsOnline);                                            // the AP is not part of it once signed in…
        Assert.Equal(Spotify.LinkPhase.Waiting, s.Ap);                      // …and `Ap` is where that shows

        Step(ref s, Spotify.SessionEventKind.DealerDropped, number: (long)Spotify.SessionFault.Network);
        Assert.False(s.IsOnline);                                           // the dealer is
        Assert.Equal(Spotify.SessionPhase.Reconnecting, s.Phase);
        Assert.True(s.LoggedIn);
    }

    /// <summary>Before this login's welcome the AP's ladder IS the session's: a drop there is the whole session
    /// reconnecting, and its retry starts the ladder again from the top — the old single-transport behaviour, kept where it
    /// is still true.</summary>
    [Fact]
    public void An_ap_drop_before_the_welcome_restarts_the_login_ladder()
    {
        var s = Handshaken();
        var fx = Step(ref s, Spotify.SessionEventKind.ApDropped, number: (long)Spotify.SessionFault.Network);
        Assert.Equal(Spotify.SessionEffects.CloseAp | Spotify.SessionEffects.ApBackoff, fx);
        Assert.Equal(Spotify.SessionPhase.Reconnecting, s.Phase);
        Assert.Equal(Spotify.SessionFault.Network, s.Fault);
        Assert.Equal(Spotify.LinkPhase.Down, s.Dealer);
        Assert.False(s.LoggedIn);

        Assert.Equal(Spotify.SessionEffects.ResolveHosts, Step(ref s, Spotify.SessionEventKind.ApRetry));
        Assert.Equal(Spotify.SessionPhase.Resolving, s.Phase);
        Assert.Equal(Spotify.SessionEffects.OpenAp, Step(ref s, Spotify.SessionEventKind.Hosts));
        Assert.Equal(Spotify.SessionPhase.Connecting, s.Phase);
    }

    /// <summary>The dealer opens ONCE per login, on a bearer minted after this login's welcome. A 401 re-mint on an api
    /// thread can land mid-ladder (the previous login's blob still mints) and must not open a dealer under a login the AP
    /// has not welcomed; a re-mint under a dealer that is opening or serving its own backoff never opens a second one.</summary>
    [Fact]
    public void The_dealer_opens_only_on_the_first_bearer_after_this_logins_welcome()
    {
        var s = Handshaken();
        Assert.Equal(Spotify.SessionEffects.None, Step(ref s, Spotify.SessionEventKind.AccessTokenMinted, text: new(12, 4), number: 2_000));
        Assert.Equal(Spotify.LinkPhase.Down, s.Dealer);

        Step(ref s, Spotify.SessionEventKind.Welcome, number: (long)Spotify.Tier.Premium);
        Assert.Equal(Spotify.SessionEffects.OpenDealer, Step(ref s, Spotify.SessionEventKind.AccessTokenMinted, text: new(12, 4), number: 2_000));
        Assert.Equal(Spotify.SessionEffects.None, Step(ref s, Spotify.SessionEventKind.AccessTokenMinted, text: new(20, 4), number: 3_000));
        Step(ref s, Spotify.SessionEventKind.DealerOnline, text: new(16, 4));
        Step(ref s, Spotify.SessionEventKind.DealerDropped, number: (long)Spotify.SessionFault.Network);
        Assert.Equal(Spotify.SessionEffects.None, Step(ref s, Spotify.SessionEventKind.AccessTokenMinted, text: new(24, 4), number: 4_000));
        Assert.Equal(Spotify.LinkPhase.Waiting, s.Dealer);                  // its own retry owns the next open
    }

    /// <summary>A login over a session still running is a restart: both transports close first — their threads' later words
    /// would be refused as stale, and a dealer left running would hold its socket with no fold listening to it.</summary>
    [Fact]
    public void A_login_over_a_running_session_closes_both_transports_first()
    {
        var s = Online();
        uint epoch = s.Epoch, apEpoch = s.ApEpoch;
        var fx = Step(ref s, Spotify.SessionEventKind.Login, flag: true);

        Assert.Equal(Spotify.SessionEffects.CloseAll | Spotify.SessionEffects.ResolveHosts, fx);
        Assert.Equal(Spotify.SessionPhase.Resolving, s.Phase);
        Assert.Equal(epoch + 1, s.Epoch);
        Assert.Equal(apEpoch + 1, s.ApEpoch);
        Assert.Equal(Spotify.LinkPhase.Opening, s.Ap);
        Assert.Equal(Spotify.LinkPhase.Down, s.Dealer);
        Assert.False(s.LoggedIn);
        Assert.True(s.ConnectionId.IsEmpty);
    }

    [Fact]
    public void The_backoff_ladder_is_3_6_12_24_capped_at_30_seconds()
    {
        Assert.Equal(3_000, Spotify.BackoffMs(0));       // no attempt yet
        int[] expected = [3_000, 6_000, 12_000, 24_000, 30_000, 30_000];
        for (int i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], Spotify.BackoffMs((uint)(i + 1)));
    }

    /// <summary>One ladder, two climbers (B4): a flapping AP never lengthens the dealer's wait, and each transport's own
    /// success — the AP's welcome, the dealer's hello — resets its own attempt count only.</summary>
    [Fact]
    public void Each_transport_climbs_its_own_backoff_ladder()
    {
        var s = Online();
        Step(ref s, Spotify.SessionEventKind.ApDropped, number: (long)Spotify.SessionFault.Network);
        Step(ref s, Spotify.SessionEventKind.ApRetry);
        Step(ref s, Spotify.SessionEventKind.ApDropped, number: (long)Spotify.SessionFault.Network);
        Assert.Equal(2u, s.ApAttempt);
        Assert.Equal(0u, s.DealerAttempt);
        Assert.Equal(6_000, Spotify.BackoffMs(s.ApAttempt));

        Step(ref s, Spotify.SessionEventKind.DealerDropped, number: (long)Spotify.SessionFault.Network);
        Assert.Equal(1u, s.DealerAttempt);
        Assert.Equal(3_000, Spotify.BackoffMs(s.DealerAttempt));

        Step(ref s, Spotify.SessionEventKind.DealerRetry);
        Step(ref s, Spotify.SessionEventKind.DealerOnline, text: new(24, 4));
        Assert.Equal(0u, s.DealerAttempt);
        Assert.Equal(2u, s.ApAttempt);

        Step(ref s, Spotify.SessionEventKind.ApRetry);
        Step(ref s, Spotify.SessionEventKind.Welcome, number: (long)Spotify.Tier.Premium);
        Assert.Equal(0u, s.ApAttempt);
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
        Step(ref s, Spotify.SessionEventKind.Welcome, number: (long)Spotify.Tier.Premium);
        Step(ref s, Spotify.SessionEventKind.ClientTokenMinted);
        Assert.Equal(Spotify.SessionEffects.OpenDealer, Step(ref s, Spotify.SessionEventKind.AccessTokenMinted));
    }

    [Fact]
    public void A_logout_forgets_the_account_but_keeps_the_epoch_and_the_boot_identity()
    {
        var s = Online();
        s.DeviceId = new Spotify.TokenRef(100, 8);
        uint epoch = s.Epoch, apEpoch = s.ApEpoch;
        var fx = Step(ref s, Spotify.SessionEventKind.Logout);

        Assert.Equal(Spotify.SessionEffects.CloseAll | Spotify.SessionEffects.ClearCredential | Spotify.SessionEffects.SignedOut, fx);
        Assert.Equal(Spotify.SessionPhase.Offline, s.Phase);
        Assert.Equal(epoch + 1, s.Epoch);           // a monotonic epoch: nothing in flight may come back
        Assert.Equal(apEpoch + 1, s.ApEpoch);       // …on either transport (B4)
        Assert.True(s.AccessToken.IsEmpty);
        Assert.True(s.ClientToken.IsEmpty);
        Assert.False(s.HasCredential);
        Assert.Equal(Spotify.Tier.Unknown, s.Tier);
        Assert.Equal(new Spotify.TokenRef(100, 8), s.DeviceId);   // a sign-in after a sign-out is the same device
    }

    /// <summary>G-035's clock, as an argument: a token is due inside the refresh lead, and one never held always is.</summary>
    [Fact]
    public void A_token_is_due_inside_the_refresh_lead_and_when_none_is_held()
    {
        long expires = 1_000_000;
        Assert.False(Spotify.TokenDue(expires - Spotify.TokenRefreshLeadMs - 1, expires));
        Assert.True(Spotify.TokenDue(expires - Spotify.TokenRefreshLeadMs, expires));
        Assert.True(Spotify.TokenDue(expires + 5, expires));
        Assert.True(Spotify.TokenDue(0, 0));
    }

    /// <summary>D24's ladder: the first bad-credentials answer buys exactly one retry against a fresh access point, the
    /// second is the verdict; "premium required" stops as NotPremium; "try another AP" is failover; anything else — an
    /// unreadable failure included — stops with the credential kept.</summary>
    [Fact]
    public void The_refusal_ladder_believes_bad_credentials_only_twice()
    {
        Assert.Equal(Spotify.RejectStep.RetryFreshAccessPoint, Spotify.OnApReject(Spotify.ApErrorBadCredentials, 0, out var first));
        Assert.Equal(Spotify.RejectVerdict.Definitive, first);
        Assert.Equal(Spotify.RejectStep.Stop, Spotify.OnApReject(Spotify.ApErrorBadCredentials, 1, out var second));
        Assert.Equal(Spotify.RejectVerdict.Definitive, second);

        Assert.Equal(Spotify.RejectStep.Stop, Spotify.OnApReject(Spotify.ApErrorPremiumRequired, 0, out var premium));
        Assert.Equal(Spotify.RejectVerdict.NotPremium, premium);

        Assert.Equal(Spotify.RejectStep.NextAccessPoint, Spotify.OnApReject(Spotify.ApErrorTryAnotherAp, 1, out var another));
        Assert.Equal(Spotify.RejectVerdict.Transient, another);

        foreach (int code in new[] { -1, 0x0, 0x5, 0x9, 0xd, 0xf, 0x10, 0x11 })
        {
            Assert.Equal(Spotify.RejectStep.Stop, Spotify.OnApReject(code, 0, out var verdict));
            Assert.Equal(Spotify.RejectVerdict.Transient, verdict);
        }
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

/// <summary>G-031: `session.lastAccount` — written when the account that went live is new or different, never otherwise, and
/// compared the way usernames compare (case-insensitively), over a memory settings store.</summary>
public class AccountMemoryTests
{
    [Fact]
    public void The_first_account_is_remembered()
    {
        var settings = new MemoryAppSettings();
        Assert.Equal(Spotify.AccountChange.First, Spotify.RememberAccount(settings, "someone"));
        Assert.Equal("someone", settings.Get(Platform.Keys.LastAccount));
    }

    [Fact]
    public void The_same_account_writes_nothing_whatever_its_casing()
    {
        var settings = new MemoryAppSettings();
        settings.Set(Platform.Keys.LastAccount, "someone");
        int written = settings.WrittenCount;

        Assert.Equal(Spotify.AccountChange.Same, Spotify.RememberAccount(settings, "SomeOne "));
        Assert.Equal("someone", settings.Get(Platform.Keys.LastAccount));
        Assert.Equal(written, settings.WrittenCount);
    }

    [Fact]
    public void A_different_account_is_a_switch()
    {
        var settings = new MemoryAppSettings();
        settings.Set(Platform.Keys.LastAccount, "someone");
        Assert.Equal(Spotify.AccountChange.Switched, Spotify.RememberAccount(settings, "somebody-else"));
        Assert.Equal("somebody-else", settings.Get(Platform.Keys.LastAccount));
    }

    [Fact]
    public void An_anonymous_welcome_never_touches_the_key()
    {
        var settings = new MemoryAppSettings();
        Assert.Equal(Spotify.AccountChange.Same, Spotify.RememberAccount(settings, ""));
        Assert.Equal(Spotify.AccountChange.Same, Spotify.RememberAccount(settings, null));
        Assert.False(settings.WasWritten(Platform.Keys.LastAccount));
    }

    /// <summary>A reconnect's late country packet keeps the held market (the scope must not switch over a late packet);
    /// a first login, or another account, takes what arrived — empty included.</summary>
    [Fact]
    public void A_welcome_without_a_country_keeps_the_market_only_for_the_same_account()
    {
        var held = new CatalogScope("spotify", "alice", "en-US", "NL", 1, true);

        Assert.Equal("NL", Spotify.WelcomeMarket("", held, "alice"));        // reconnect, packet late
        Assert.Equal("DE", Spotify.WelcomeMarket("DE", held, "alice"));      // the country moved: take it
        Assert.Equal("", Spotify.WelcomeMarket("", held, "bob"));            // another account: nothing to keep
        Assert.Equal("", Spotify.WelcomeMarket("", default, "alice"));       // first login: nothing held
        Assert.Equal("", Spotify.WelcomeMarket("", held, ""));               // no account: nothing to key on
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
        // v2, with the wire audio format as its own segment: v1 was keyed by file id alone, so it signed every id into
        // the Ogg namespace and a lossless body 404'd at byte 0. `Fold` leaves Number unset, hence the 0 here; the
        // per-format segment is pinned properly by SpotifyAudioLadderTests.
        Assert.Equal("/storage-resolve/v2/files/audio/interactive/0/deadbeef?product=0",
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
    public void The_radio_seed_route_keeps_the_seeds_colons_and_asks_for_json()
    {
        // G-251: the seed is our own `spotify:<kind>:<base62>` — the captured client sends the colons literally.
        var r = Fold(Spotify.RequestKind.RadioSeed, "spotify:track:7idegBIikag5rTZP4WZihP");
        Assert.Equal("/inspiredby-mix/v2/seed_to_playlist/spotify:track:7idegBIikag5rTZP4WZihP?response-format=json", r.Path);
        Assert.Equal(Spotify.Verb.Get, r.Verb);
        Assert.Equal(Spotify.ApiHost.Spclient, r.Host);
        Assert.True(r.Headers.HasFlag(Spotify.HeaderSet.AcceptJson));
        Assert.True(r.Headers.HasFlag(Spotify.HeaderSet.Bearer));
        Assert.False(r.Headers.HasFlag(Spotify.HeaderSet.ContentJson));
        Assert.Equal("/inspiredby-mix/v2/seed_to_playlist/spotify:artist:4Z8W4fKeB5YxbusRsdQVPb?response-format=json",
            Fold(Spotify.RequestKind.RadioSeed, "spotify:artist:4Z8W4fKeB5YxbusRsdQVPb").Path);
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

    /// <summary>G-030: every "Sign in" affordance calls <c>Spotify.Login()</c>; with nothing to resume that is a request for
    /// the sign-in surface — one request per call, so a second press re-opens a surface the user closed.</summary>
    [Fact]
    public void A_login_with_nothing_to_resume_requests_the_sign_in_surface_each_time()
    {
        int before = Spotify.SignIn.Requests.Value;

        Spotify.Login();
        Assert.Equal(before + 1, Spotify.SignIn.Requests.Value);
        Spotify.Login();
        Assert.Equal(before + 2, Spotify.SignIn.Requests.Value);
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

/// <summary>The AP channel's read timeout (Spotify.Session.cs §7) is, since B6, only the backstop behind the keepalive: it
/// must outlast every window the keepalive waits, or the read timeout — not the keepalive — tears a live idle channel down
/// and the device flaps on Connect, as the first cut's 90 s did every two to three minutes (2026-09-16).</summary>
public class ApChannelRulesTests
{
    [Fact]
    public void The_read_timeout_outlasts_every_keepalive_window()
    {
        Assert.True(Spotify.ApReadTimeoutMs > Spotify.ApKeepAlive.PingTimeoutMs, "timeout=" + Spotify.ApReadTimeoutMs);
        Assert.True(Spotify.ApReadTimeoutMs > Spotify.ApKeepAlive.PongDelayMs + Spotify.ApKeepAlive.PongAckTimeoutMs,
            "timeout=" + Spotify.ApReadTimeoutMs);
        Assert.True(Spotify.ApReadTimeoutMs > Spotify.ApKeepAlive.FirstPingTimeoutMs, "timeout=" + Spotify.ApReadTimeoutMs);
    }
}
