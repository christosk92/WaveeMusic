namespace Wavee.Core;

public enum AuthStatus { LoggedOut, Authenticating, Authenticated, Error }

public sealed record WaveeUser(string Id, string DisplayName, string? AvatarUrl, bool IsPremium, string? Email = null);

// ── Rich login projection (UI-facing, framework-neutral) ─────────────────────────────────────────────────────────────
// The coarse AuthStatus above drives the shell GATE (Authenticated ⇒ shell, else takeover); this richer projection drives
// the full-screen login takeover (WHICH device-code / pairing screen to show). It is a Core-level mirror of the backend's
// AuthFlow state so Wavee.Core stays free of any Wavee.Backend.Spotify dependency — the live bootstrap maps the backend
// AuthState → LoginSnapshot and reports it here through ILoginProgress.

/// <summary>The phase the login takeover renders. Maps from the backend AuthFlow (LoggedOut / AwaitingCredential /
/// AwaitingUser / ChallengeExpired) plus the bootstrap-reported Finalizing / Authenticated / Failed / PremiumRequired.</summary>
public enum LoginPhase
{
    LoggedOut, SilentResume, RequestingCode, AwaitingBrowser, AwaitingApproval, ChallengeExpired,
    Finalizing, Authenticated, Failed, PremiumRequired,
}

/// <summary>The device-code (pairing) challenge surfaced to the UI: the short user code, the verification URL (and the
/// code-embedded "complete" URL for a QR / one-click open), and the absolute expiry the countdown ticks toward.</summary>
public sealed record LoginChallenge(string UserCode, string VerificationUri, string? VerificationUriComplete, DateTimeOffset Expiry);

/// <summary>How far the Finalizing phase has got. Everything between "we have a credential" and "the shell is up" used to
/// sit under one frozen string, so a slow bootstrap looked identical to a hung one. Each value maps to a step row in the
/// login splash.
///
/// MUST only ever advance. <c>LoginSnapshot</c> is a record on a value-equality <c>Signal</c>, so re-reporting the same
/// step is silently dropped by <c>SetIfChanged</c> and the UI would never see it.</summary>
public enum LoginStep
{
    /// <summary>Resolving access points and opening the dealer connection.</summary>
    Connecting,
    /// <summary>Building the extended-metadata / context stack.</summary>
    Metadata,
    /// <summary>Bringing up the local audio stack.</summary>
    Audio,
    /// <summary>Fetching the account profile.</summary>
    Profile,
    /// <summary>Everything is up; the shell is about to take over.</summary>
    Done,
}

/// <summary>One immutable login snapshot the takeover projects: the phase, the optional pairing challenge, an optional
/// humanized error (Failed), the resolved account (Authenticated), and — during Finalizing — how far the bootstrap has got.</summary>
public sealed record LoginSnapshot(LoginPhase Phase, LoginChallenge? Challenge = null, string? Error = null, WaveeUser? User = null,
    LoginStep Step = LoginStep.Connecting);

/// <summary>The sink the live-login bootstrap reports progress to. The UI bridge implements it as a UI-thread-marshalled
/// signal write (<c>PlaybackBridge.Progress</c>); a null sink (the headless CLI) is a no-op.</summary>
public interface ILoginProgress { void Report(LoginSnapshot snapshot); }

/// <summary>The session / auth seam. Collapses WaveeMusic's <c>ISession</c> + auth-state surface
/// down to what the first scaffold needs: a status, the current user, and connect/logout.</summary>
public interface ISpotifySession
{
    AuthStatus Status { get; }
    WaveeUser? CurrentUser { get; }
    IObservable<AuthStatus> StatusChanged { get; }
    Task<bool> ConnectAsync(CancellationToken ct = default);
    Task LogoutAsync(CancellationToken ct = default);
}
