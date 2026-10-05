// ── AiLyrics/AiLyrics.Ledger.cs ──────────────────────────────────────────────────────────────────────────────────────
// Ledger
//
// Role: PURE (the host's UI-thread bookkeeping of which job and which model load are current)
//
// Every result the worker posts back to the UI thread carries the ticket it was started with. A ticket that is no longer
// current (the job was cancelled or replaced, the feature went off, the files were removed) is dropped, so a stale post
// can never bring back "Timing words · x of y", flip the Settings card to Error, or mark a removed pack Ready.

namespace Wavee;

public static partial class AiLyrics
{
    public sealed class Ledger
    {
        /// <summary>The track of the current job; empty when no job runs.</summary>
        public string JobTrack { get; private set; } = "";

        /// <summary>The current job's ticket. Bumped by every start, cancel and end.</summary>
        public int JobEpoch { get; private set; }

        /// <summary>The current model load's ticket. Bumped by every load and by anything that makes a running load moot
        /// (feature off, files or a language removed, unload).</summary>
        public int LoadEpoch { get; private set; }

        /// <summary>When the worker last did something (a job ended, a load finished): the idle unload counts from here.</summary>
        public long LastActivityMs { get; private set; }

        public bool JobRunning => JobTrack.Length > 0;

        public int BeginJob(string trackId)
        {
            JobTrack = trackId;
            return ++JobEpoch;
        }

        /// <summary>The job ended, failed or was cancelled; its later posts are stale.</summary>
        public void EndJob(long nowMs)
        {
            JobTrack = "";
            JobEpoch++;
            LastActivityMs = nowMs;
        }

        public bool IsCurrentJob(int epoch, string trackId) => epoch == JobEpoch && JobTrack.Length > 0 && JobTrack == trackId;

        public int BeginLoad() => ++LoadEpoch;

        /// <summary>A load in flight no longer matters; its completion or failure must not change the Settings card.</summary>
        public void InvalidateLoads() => LoadEpoch++;

        public bool IsCurrentLoad(int epoch) => epoch == LoadEpoch;

        /// <summary>A load finished: the models are fresh, so the idle unload starts counting now (not from app start).</summary>
        public void Touch(long nowMs) => LastActivityMs = nowMs;

        /// <summary>Unload the NPU sessions after <see cref="Rules.IdleUnloadMs"/> without a job or a load.</summary>
        public bool ShouldUnload(bool loaded, long nowMs) => loaded && !JobRunning && Rules.UnloadAfterIdle(LastActivityMs, nowMs);
    }
}
