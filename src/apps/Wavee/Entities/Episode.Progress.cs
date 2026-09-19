// ── Entities/Episode.Progress.cs ───────────────────────────────────────────────────────────────────────────────────
// podcast progress, for real: the player's local mirror, Mark played / unplayed, and the decisions they share
//
// Role: SHELL (`Entities.MirrorEpisodeProgress`, `Entities.MarkEpisode`) with a CORE section (`EpisodeProgress`)
// Owner: T
// Wave: P2 (podcast rework)
// Budget: 200 lines
// Spec: docs/plans/wavee/podcast-show-rework-implementation.md §1.2 (defect 1), §5.8, §6.1, §6.2
//
// THREE WRITERS, ONE COLUMN PAIR. `EpisodeTable.ProgressMs` + `PlayedAt` (the Progress group) are written by exactly:
//   · the login hydrate — `Spotify.Telemetry.HydrateProgress`, herodotus's `ListCurrentStates` folded at Full;
//   · the player — `Playback.Host.Context.cs`'s play report mirrors every pause/end position (beside the herodotus
//     write, the SAME position and the SAME instant) and a 15 s tick while an episode plays, at Local;
//   · the listener — `MarkEpisode`, at Local, plus the matching herodotus revision.
// Each lands through the one legal path — stage → commit → publish → write behind — so a mirror is a bound row's repaint
// and the next launch's cold read, never a column poked from a callback.
//
// WHY LOCAL IS THE PLAYER'S RUNG (§6.2: Seed < Thin < Full(wire) < Local(player)). A server position can be minutes stale
// (herodotus hears this device on pause and end only); the row must never rewind to it under a listener who just played
// on. The one exception — the same account playing on ANOTHER device since — is `EpisodeProgress.Landing`'s to decide,
// and the hydrate's landing applies it.
//
// The CORE section is engine-free and clock-free: every decision takes its numbers as arguments, which is what lets
// `EpisodeProgressTests` pin them with no player, no session and no scope.

namespace Wavee;

/// <summary>The progress decisions wave P2 adds. PURE — no table, no clock, no wire.</summary>
public static class EpisodeProgress
{
    /// <summary>The local mirror's tick while an episode plays (P10, named): 15 s. Short enough that a row's "min left"
    /// moves visibly without a pause; long enough that a mirror is one pooled staging and one write-behind a quarter of
    /// a minute, never per position report.</summary>
    public const int MirrorIntervalMs = 15_000;

    /// <summary>What a mark writes into the ROW (<see cref="Entities.MarkEpisode"/>): played ⇒ the whole duration, or
    /// <see cref="int.MaxValue"/> while the duration is not resident — the very COMPLETED value the hydrate folds a
    /// marker-4 revision to (<see cref="Episode.Rules.ProgressOf"/>), which <c>Pct</c> clamps to 1 when the duration
    /// lands; unplayed ⇒ 0.</summary>
    public static int Marked(bool played, int durationMs) => played ? (durationMs > 0 ? durationMs : int.MaxValue) : 0;

    /// <summary>What a mark sends HERODOTUS (<see cref="Spotify.Telemetry.ResumePoint"/>), and whether it sends anything:
    /// unplayed ⇒ a position of 0 (the official "not started", <c>{}</c>); played ⇒ a position AT the duration, which
    /// every reader folds back through THE completion rule (<see cref="Episode.Rules.Completed"/>) — and with the
    /// duration not resident, NOTHING (false): a position cannot say "played" without one, and 0 would say "unplayed".
    /// Why a position and not marker 4: the official client's mark-played write has never been captured, and marker 4's
    /// "finished" rests on one timing sample (findings-podcast-wire.md §4.1) — too thin to write to an account. A played
    /// mark therefore reaches the other clients as a resume point at the end; how the official UI labels that is not
    /// observed (its 94 % was still IN_PROGRESS). Revisit when the mark-played capture exists.</summary>
    public static bool TryMarkedResumePoint(bool played, int durationMs, out int positionMs)
    {
        positionMs = played ? Math.Max(0, durationMs) : 0;
        return !played || durationMs > 0;
    }

    /// <summary>Unix milliseconds → the column's unix SECONDS (<see cref="EpisodeTable.PlayedAt"/>), clamped to the
    /// column's range.</summary>
    public static int UnixSeconds(long unixMs) => (int)Math.Clamp(unixMs / 1000, 0L, int.MaxValue);

    /// <summary>Where an episode load starts when its caller named no position (a Claim or an Advance at 0 — the
    /// reducer's load tail reads it through <see cref="Playback.EpisodeStartOf"/>, so the deck publishes it from the first
    /// frame). Unknown progress starts at 0 (§6.1: no state, never a guess). A FINISHED episode starts over —
    /// finished by THE completion rule (<see cref="Episode.Rules.Completed"/>, D-5: the row that reads "played" never
    /// resumes 20 s before its end), or by the completed marker with no duration yet. Anything else goes through
    /// <see cref="Playback.ResumeStart.For"/> (the 09-18 playback fix): only its <c>StartAt</c> verdict resumes; the
    /// "effectively over" and "nothing to resume" verdicts both start at 0 — on THIS row, because a load is already
    /// committed to it.</summary>
    public static int ResumeFromMs(bool knowsProgress, int progressMs, int durationMs)
    {
        if (!knowsProgress || progressMs == int.MaxValue || Episode.Rules.Completed(progressMs, durationMs)) return 0;
        return Math.Max(0, progressMs);
    }

    /// <summary>What the hydrate's landing does with one staged wire row that has a RESIDENT twin.</summary>
    public enum HydrateLanding : byte
    {
        /// <summary>Commit it as staged (Full): the resident row has no progress, or only a wire/seed rung's.</summary>
        Land,
        /// <summary>Raise it to Local: the resident progress is this device's own, and the wire's revision is strictly
        /// newer — the account played on elsewhere since.</summary>
        Promote,
        /// <summary>Withdraw it: this device's own position is as new or newer, and a stale server one must not rewind it.</summary>
        Skip,
    }

    /// <summary>The landing rule (§6.2 plus the cross-device refinement). <paramref name="residentPlayedAt"/> and
    /// <paramref name="incomingPlayedAt"/> are unix seconds — the local mirror's stamp is the very instant this device
    /// sent herodotus as <c>create_time</c>, which the fold reads back first, so this device's own revision compares
    /// EQUAL and is kept, never "promoted" over itself.</summary>
    public static HydrateLanding Landing(bool residentKnows, Authority residentAuthority, int residentPlayedAt, int incomingPlayedAt)
        => !residentKnows || residentAuthority < Authority.Local ? HydrateLanding.Land
         : incomingPlayedAt > residentPlayedAt ? HydrateLanding.Promote
         : HydrateLanding.Skip;
}

public static partial class Entities
{
    /// <summary>Stage one LOCAL progress write for <paramref name="id"/>: the Progress group only, at
    /// <see cref="Authority.Local"/>, <c>PlayedAt</c> = <paramref name="unixMs"/> in seconds. A negative position
    /// clamps to 0. Any thread may stage; only the UI thread commits (C1).</summary>
    public static void StageLocalProgress(Staging s, EntityId id, int progressMs, long unixMs)
    {
        ref StagedEpisode row = ref s.Episodes.RowFor(id, Authority.Local, (uint)EpisodeFields.Progress);
        row.ProgressMs = Math.Max(0, progressMs);
        row.PlayedAt = EpisodeProgress.UnixSeconds(unixMs);
        row.RevisionCreateSeconds = unixMs / 1000;
        row.RevisionCreateNanos = (int)(unixMs % 1000) * 1_000_000;
    }

    /// <summary>Write this device's own progress for one episode: stage (<see cref="StageLocalProgress"/>) → commit →
    /// publish → write behind, the host-door shape every non-planner answer lands through. UI THREAD. The player calls
    /// it beside each herodotus write (pause, end) and on its 15 s tick; <see cref="MarkEpisode"/> calls it for a mark.
    /// A no-op without a scope (a unit test's bare player) or for an id that is not an episode.</summary>
    public static void MirrorEpisodeProgress(EntityId id, int progressMs, long unixMs)
    {
        Scope? scope = Current;
        if (scope is null || id.Kind != EntityKind.Episode) return;
        Staging s = Staging.Rent();
        s.Epoch = scope.Epoch;
        StageLocalProgress(s, id, progressMs, unixMs);
        Commit(s);
        Publish();
        if (!Store.WriteBehind(s)) Staging.Return(s);         // write-behind owns it when it accepts
    }

    /// <summary>Mark an episode played or unplayed — the menu's <c>MarkPlayed</c>/<c>MarkUnplayed</c> rows and the show
    /// reader's "mark all played". UI THREAD. The row changes at once (<see cref="EpisodeProgress.Marked"/> at Local, so
    /// no stale server position can undo it), and a Spotify episode's mark goes to herodotus through the same 2 s write queue the
    /// player's positions use, as <see cref="EpisodeProgress.TryMarkedResumePoint"/> decides: played ⇒ a resume point at
    /// the duration, unplayed ⇒ one at 0, and a played mark with no resident duration stays LOCAL (<c>wire=0</c> in the
    /// log line). One instant stamps both, so a later hydrate reads this device's own revision back as equal and keeps the
    /// row.</summary>
    public static void MarkEpisode(Episode e, bool played)
    {
        if (Current is null || !e.IsValid) return;
        EntityId id = e.Id;
        long unixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // Explicit completion is independent of the resume cursor. A played mark never destroys its position.
        MirrorEpisodeCompletion(id, played, unixMs);
        if (!played) MirrorEpisodeProgress(id, 0, unixMs);
        if (id.Provider == EntityProvider.Spotify)
            Spotify.Telemetry.MarkEpisode(id.Text, played, unixMs);
    }

    public static void MirrorEpisodeCompletion(EntityId id, bool played, long unixMs)
    {
        Scope? scope = Current;
        if (scope is null || id.Kind != EntityKind.Episode) return;
        Staging s = Staging.Rent();
        s.Epoch = scope.Epoch;
        ref StagedEpisode row = ref s.Episodes.RowFor(id, Authority.Local, (uint)EpisodeFields.Completion);
        row.ExplicitCompleted = played;
        row.CompletionAtMs = unixMs;
        Commit(s);
        Publish();
        if (!Store.WriteBehind(s)) Staging.Return(s);
    }
}

/// <summary>Full wire precision; a locally unacknowledged write compares creation clocks until acknowledged.</summary>
public readonly record struct EpisodeRevisionStamp(long UpdateSeconds, int UpdateNanos, long CreateSeconds, int CreateNanos)
{
    public int CompareForLanding(EpisodeRevisionStamp other)
    {
        if (UpdateSeconds == 0 || other.UpdateSeconds == 0)
            return Compare(CreateSeconds, CreateNanos, other.CreateSeconds, other.CreateNanos);
        int update = Compare(UpdateSeconds, UpdateNanos, other.UpdateSeconds, other.UpdateNanos);
        return update != 0 ? update : Compare(CreateSeconds, CreateNanos, other.CreateSeconds, other.CreateNanos);
    }

    static int Compare(long leftSeconds, int leftNanos, long rightSeconds, int rightNanos)
        => leftSeconds != rightSeconds ? leftSeconds.CompareTo(rightSeconds) : leftNanos.CompareTo(rightNanos);
}
