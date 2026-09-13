using System;
using Wavee.Core;

namespace Wavee.Backend;

// ── ENGINE ⑤ — SessionContext (the ambient key · gate · partition) ───────────────────────────────────────────────────
// One reactive value: it KEYS Resources (which cache dims matter), GATES features (premium), and PARTITIONS storage
// (per-account). A market/locale/tier change invalidates the scoped caches; sourced from login + the live ProductInfo.

public enum Tier { Free, Premium }

public sealed record SessionContext(
    string Account, string Market, string Catalogue, string Locale, Tier Tier, bool ExplicitFilter)
{
    public static SessionContext LoggedOut => new("", "US", "premium", "en", Tier.Free, false);

    // premium-gating helpers — the Playback resolve/reducer take these as input (no dead UI either).
    public bool CanSeek => Tier == Tier.Premium;
    public bool ShuffleOnly => Tier == Tier.Free;
}

public sealed class SessionContextHost
{
    readonly SimpleSubject<SessionContext> _subject;

    public SessionContextHost(SessionContext initial)
    {
        Current = initial;
        _subject = new SimpleSubject<SessionContext>(initial);
    }

    public SessionContext Current { get; private set; }
    public IObservable<SessionContext> Changes => _subject;

    public void Set(SessionContext ctx)
    {
        Current = ctx;
        _subject.OnNext(ctx);
    }
}

// ── Premium-only gate ────────────────────────────────────────────────────────────────────────────────────────────────
// Wavee requires a Spotify Premium account for now: on-demand playback on a third-party client requires Premium, so Free
// can't be supported yet. The refusal is a LOGIN-TIME decision (Seam.GoLive / SpotifyAuthSession / SpotifyLiveLogin read
// the real tier from ProductInfo) — there is no pre-window gate: the window comes up and the refusal surfaces in-app.
public static class SessionGate
{
    // The refusal copy. Still consumed by the live-login path (SpotifyLiveLogin) and the in-app premium-required surface.
    public const string WarningTitle = "Wavee needs Spotify Premium";
    public const string WarningBody =
        "Wavee doesn't support Spotify Free accounts yet.\n\n" +
        "On-demand playback on a third-party client requires a Premium subscription, so Wavee can't run on a Free " +
        "account right now.\n\n" +
        "Please sign in with a Premium account, or upgrade at spotify.com/premium.";

    public static bool IsAllowed(Tier tier) => tier == Tier.Premium;
}
