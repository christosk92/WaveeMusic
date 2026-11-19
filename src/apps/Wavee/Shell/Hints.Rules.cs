// ── Shell/Hints.Rules.cs — every number the hint service decides by (design 6.2, 6.3), in one place ──────────────────────────────
//
// Role: CORE data (settings rework phase 3, handoff/settings-2026-11-18/DESIGN.md 6.2 the budget, 6.3 the triggers). The hint table
// (`Hints.cs`), the trigger facts (`Hints.Facts.cs`) and the blob (`Hints.State.cs`) read these; none of them writes a number of its own.

namespace Wavee;

/// <summary>The budget and the trigger thresholds (design 6.2, 6.3).</summary>
public static class HintRules
{
    // ── the budget (6.2) ─────────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>"×" / "Not now": not again for this many days.</summary>
    public const int SnoozeDays = 30;
    /// <summary>The dismissal that retires a hint for good.</summary>
    public const int RetiringDismissal = 3;
    /// <summary>No hint in the first minutes after launch.</summary>
    public const long LaunchGraceMs = 2 * 60_000;
    /// <summary>No hint while a track has just started (its first seconds).</summary>
    public const long TrackStartGraceMs = 10_000;
    /// <summary>The Settings home's Suggestions: at most this many (owner decision 14: the top three).</summary>
    public const int SuggestionCap = 3;
    /// <summary>A qualifying hint held back by a gate no event reopens (another overlay open, the window hidden) asks again this soon.</summary>
    public const long BlockedRecheckMs = 2_000;
    /// <summary>A slot never asks again sooner than this (a timed gate measured on the wall clock, its timer on the frame clock).</summary>
    public const long MinRecheckMs = 500;
    /// <summary>An inline action's confirmation ("On · Change") stays this long, then goes (its own close goes sooner).</summary>
    public const long ConfirmationMs = 8_000;

    // ── Recently changed (3.3, owner decision 11) ──────────────────────────────────────────────────────────────────────────
    /// <summary>The ring: the last five dials written, persisted across restarts.</summary>
    public const int RecentCap = 5;
    /// <summary>A second write of the head dial from the same source within this window moves the head in place (a slider drag through
    /// its writer per pointer move is one change, and writes the blob once).</summary>
    public const long RecentCoalesceMs = 2_000;

    // ── the triggers (6.3) ────────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>#1 AI lyrics: the lyrics have been on screen this long this session.</summary>
    public const long LyricsOpenMs = 30_000;
    /// <summary>#4 Crossfade: a skip this close to a track's end counts as cutting its tail by hand.</summary>
    public const long SkipNearEndMs = 5_000;
    /// <summary>#4 Crossfade: that many such skips this session.</summary>
    public const int SkipsNearEnd = 3;
    /// <summary>#5 Normalization: a volume move this early in a track counts.</summary>
    public const long VolumeEarlyMs = 15_000;
    /// <summary>#5 Normalization: on that many different tracks...</summary>
    public const int VolumeEarlyTracks = 3;
    /// <summary>#5 Normalization: ...within this window.</summary>
    public const long VolumeEarlyWindowMs = 10 * 60_000;
    /// <summary>#7 Windows notifications: the bell opened this many times.</summary>
    public const int BellOpens = 3;
    /// <summary>#8 Closing hides Wavee: started again this soon after quitting with the window's ✕...</summary>
    public const long QuickRelaunchMs = 15 * 60_000;
    /// <summary>#8: ...this many times in a day.</summary>
    public const int QuickRelaunchesPerDay = 2;
    /// <summary>#9 Start with Windows: started by hand this soon after signing in to Windows...</summary>
    public const long SignInLaunchMs = 5 * 60_000;
    /// <summary>#9: ...on this many days.</summary>
    public const int SignInLaunchDays = 3;
    /// <summary>#10 Fast navigation: Back or another page this soon after a page swap counts as waiting for the hand-over...</summary>
    public const long SwapImpatienceMs = 250;
    /// <summary>#10: ...this many times in a session.</summary>
    public const int SwapImpatience = 5;
    /// <summary>#10: two commits closer than this are one action (a redirect, a replace), not a person going elsewhere.</summary>
    public const long RedirectMs = 32;
    /// <summary>#13 Shortcuts: this many mouse clicks on play/pause in a session.</summary>
    public const int PlayPauseClicks = 20;
    /// <summary>#14 Sleep timer: an audiobook playing at or after this local hour...</summary>
    public const int BedtimeHour = 22;
    /// <summary>#14: ...until this hour of the next morning (the night after 22:00 runs past midnight).</summary>
    public const int BedtimeEndHour = 5;
    /// <summary>#14: ...once it has played this long.</summary>
    public const long AudiobookPlayedMs = 20 * 60_000;
    /// <summary>#16 Playback cache: this share of its budget used.</summary>
    public const double CacheFullShare = 0.95;
}
