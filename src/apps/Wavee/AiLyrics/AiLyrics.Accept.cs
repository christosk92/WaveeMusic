// ── AiLyrics/AiLyrics.Accept.cs ──────────────────────────────────────────────────────────────────────────────────────
// Acceptance: the check every AI result passes before it is shown, saved or replayed
//
// Role: CORE (pure: no I/O, no clock reads, no engine)
//
// The aligner always returns SOME path, so its output is checked against the provider's document it was built over:
//
//   per line  word starts never go back; at most 10 % of the words last zero time; for a line-synced source the words
//             lie inside [the line's provider start - 0.6 s, the next line's provider start + 0.6 s] and the line starts
//             no earlier than 0.6 s before its provider start; line starts never go back. A line that fails is DEMOTED:
//             it becomes the provider's line again (line-level highlight).
//   whole     fewer than 60 % of the lines that have letters end up word-synced: the result is REJECTED (not shown,
//             not saved), and the track keeps the provider's line sync. While a job runs, the lines timed so far are
//             judged once there are 20 with letters, against a lower bar (50 %): measured on the 187 cached results
//             that still match their provider document, that stops 19 of the 21 results the end rejects and none
//             that the end accepts (60 % from 10 lines on stopped 10 good ones).
//
// Unlike the provider gate (Lyrics.Timing.HasUnusableWordTiming), a line whose words all last zero time is judged too.

using System.Globalization;

namespace Wavee;

public static partial class AiLyrics
{
    public static class Acceptance
    {
        /// <summary>How far before its provider line a word may start (and after the next line's start it may end).</summary>
        public const long LeadMs = 600;

        /// <summary>A line with more than this share of zero-duration words is demoted.</summary>
        public const double MaxZeroShare = 0.10;

        /// <summary>A result with fewer word-synced lines than this share of its lines with letters is rejected.</summary>
        public const double MinSyncedShare = 0.60;

        /// <summary>While a job runs, the whole-result share is judged only from this many lines with letters on…</summary>
        public const int MinLinesWhileRunning = 20;

        /// <summary>…and against this lower bar (an early verse can be harder than the song).</summary>
        public const double MinSyncedShareWhileRunning = 0.50;

        /// <summary>The source has provider line times worth keeping (not plain text).</summary>
        public static bool IsLineSynced(Lyrics.Doc doc) => doc.IsSynced && doc.Sync != Lyrics.SyncKind.Unsynced;

        /// <summary>What the check found. <see cref="Eligible"/>: judged lines with letters; <see cref="Synced"/>: of
        /// those, the ones word-synced by the aligner after the check; the three causes count demoted lines.</summary>
        public readonly record struct Verdict(int Judged, int Eligible, int Synced, int Demoted, int Disorder, int Zero, int Window, bool Rejected)
        {
            public string Describe()
            {
                var ci = CultureInfo.InvariantCulture;
                return $"judged={Judged.ToString(ci)} eligible={Eligible.ToString(ci)} synced={Synced.ToString(ci)} " +
                       $"share={(Eligible == 0 ? 0 : (double)Synced / Eligible).ToString("0.00", ci)} demoted={Demoted.ToString(ci)} " +
                       $"(order={Disorder.ToString(ci)} zero={Zero.ToString(ci)} window={Window.ToString(ci)})";
            }
        }

        /// <summary>The result with every failing line demoted, or null when the whole result is rejected.
        /// <paramref name="judgedLines"/>: the leading lines the result has timed (the rest are the provider's anyway);
        /// <paramref name="final"/>: the whole song is in (the share is judged however few lines there are).</summary>
        public static Lyrics.Doc? Check(Lyrics.Doc source, Lyrics.Doc result, int judgedLines, bool final, out Verdict verdict)
        {
            int n = Math.Min(source.Lines.Count, result.Lines.Count);
            int judged = Math.Min(judgedLines, n);
            bool synced = IsLineSynced(source);
            int eligible = 0, wordSynced = 0, demoted = 0, disorder = 0, zero = 0, window = 0;
            var lines = new List<Lyrics.Line>(result.Lines.Count);
            long prevStart = long.MinValue;
            for (int i = 0; i < result.Lines.Count; i++)
            {
                var line = result.Lines[i];
                if (i >= judged) { lines.Add(line); continue; }
                var src = source.Lines[i];
                bool hasLetters = HasLetter(src.Text);
                if (hasLetters) eligible++;
                bool ours = !ReferenceEquals(line, src) && line.IsWordByWord && line.Syllables.Count > 0;
                if (!ours)
                {
                    // the provider's line, or (plain-text source) a line-level line: only its start must not go back
                    if (!synced && line.StartMs < prevStart) line = line with { StartMs = prevStart };
                    lines.Add(line);
                    prevStart = Math.Max(prevStart, line.StartMs);
                    continue;
                }
                var syl = line.Syllables;
                bool ordered = line.StartMs >= prevStart;
                int zeros = 0;
                for (int k = 0; k < syl.Count; k++)
                {
                    if (k > 0 && syl[k].StartMs < syl[k - 1].StartMs) ordered = false;
                    if (syl[k].EndMs <= syl[k].StartMs) zeros++;
                }
                bool fewZeros = zeros <= MaxZeroShare * syl.Count;
                bool inside = true;
                if (synced)
                {
                    long lo = src.StartMs - LeadMs;
                    long hi = i + 1 < source.Lines.Count ? source.Lines[i + 1].StartMs + LeadMs : long.MaxValue;
                    if (line.StartMs < lo) inside = false;
                    for (int k = 0; k < syl.Count && inside; k++)
                        if (syl[k].StartMs < lo || syl[k].EndMs > hi) inside = false;
                }
                if (ordered && fewZeros && inside)
                {
                    if (hasLetters) wordSynced++;
                    lines.Add(line);
                    prevStart = Math.Max(prevStart, line.StartMs);
                    continue;
                }
                demoted++;
                if (!ordered) disorder++;
                else if (!fewZeros) zero++;
                else window++;
                var back = synced ? src : line with { Syllables = [], IsWordByWord = false, StartMs = Math.Max(prevStart, line.StartMs) };
                lines.Add(back);
                prevStart = Math.Max(prevStart, back.StartMs);
            }
            bool rejected = final
                ? eligible == 0 || wordSynced < MinSyncedShare * eligible
                : eligible >= MinLinesWhileRunning && wordSynced < MinSyncedShareWhileRunning * eligible;
            verdict = new Verdict(judged, eligible, wordSynced, demoted, disorder, zero, window, rejected);
            return rejected ? null : result with { Lines = lines };
        }

        static bool HasLetter(string? text)
        {
            if (text is null) return false;
            foreach (char c in text) if (char.IsLetter(c)) return true;
            return false;
        }
    }
}
