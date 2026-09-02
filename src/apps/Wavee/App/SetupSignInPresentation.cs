using Wavee.Core;

namespace Wavee;

/// <summary>Pure presentation facts derived from <see cref="SetupSignInPhase"/> for the setup wizard's sign-in page.
/// ENGINE-FREE BY CONSTRUCTION — no <c>FluentGpu.*</c>/<c>Loc</c>/<c>Signal&lt;T&gt;</c> reference — exactly like
/// <c>SetupGating.cs</c>: this file is source-included by <c>Wavee.Tests</c> so a theory test drives the REAL
/// projection, never a copy of it.
///
/// <para>Rise's sign-in page has no stage/decision split any more (that composition is gone with the tier ladder):
/// the page is a single content column — a lead line, two <c>SettingsCard</c>s (browser / scan-QR) while Idle, and
/// per-facet swaps for the other five phases. <see cref="ShowsIdleCards"/> is the one fact both the page and its own
/// theory tests read instead of re-deriving the "which facets keep the two option cards visible under an InfoBar"
/// rule inline.</para></summary>
static class SetupSignInPresentation
{
    /// <summary>Whether the page shows the two Idle option cards (browser / scan-QR) — Idle itself, plus the three
    /// terminal facets that retry IN PLACE rather than replacing the whole page with a dead end: Failed/Expired (try
    /// again — the same two paths are still valid) and Premium (upgrade, but the cards stay so a different account
    /// can still sign in). Busy and Done each replace the cards outright (a live step ladder / the account
    /// confirmation), so neither shows them.</summary>
    public static bool ShowsIdleCards(SetupSignInPhase phase) =>
        phase is SetupSignInPhase.Idle or SetupSignInPhase.Failed or SetupSignInPhase.Expired or SetupSignInPhase.Premium;

    /// <summary>The account-card display name (§ the "raw Spotify id" bug): <paramref name="live"/>
    /// (<c>PlaybackBridge.User</c>) wins whenever its <c>DisplayName</c> is a real name rather than a stand-in for
    /// the id (<c>Switchable.LogIn</c>/<c>SpotifyAuthSession</c> both default <c>DisplayName</c> to the account id
    /// until the real profile lands); <paramref name="snapshot"/> (<c>LoginSnapshot.User</c>, frozen at the moment
    /// auth completed) is the fallback for the same reason. Returns <c>null</c> when NEITHER carries a name that
    /// differs from its own id — the caller falls back to whatever it has (typically the id, as today) rather than
    /// this pure function inventing a placeholder string.</summary>
    public static string? DisplayNameFor(WaveeUser? live, WaveeUser? snapshot)
    {
        if (live is { } l && !string.Equals(l.DisplayName, l.Id, System.StringComparison.Ordinal)) return l.DisplayName;
        if (snapshot is { } s && !string.Equals(s.DisplayName, s.Id, System.StringComparison.Ordinal)) return s.DisplayName;
        return null;
    }
}
