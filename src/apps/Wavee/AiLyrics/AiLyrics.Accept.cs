// ── AiLyrics/AiLyrics.Accept.cs ──────────────────────────────────────────────────────────────────────────────────────
// Acceptance: the check every AI result passes before it is shown, saved or replayed
//
// Role: CORE (pure: no I/O, no clock reads, no engine)
//
// The aligner always returns SOME path, so its output is checked against the provider's document it was built over:
//
//   per line  word starts never go back; at most 10 % of the words last zero time (a character-timed language: at most
//             one unit or 10 %, whichever is more); for a line-synced source the words lie inside [the line's provider
//             start - 0.6 s, the next line's provider start + 0.6 s], that window WIDENED by the result's own whole-track
//             offset (TrackOffsetMs), the line starts no earlier than the window, and its first word starts at most 2 s
//             after the line's provider start moved by that offset (the words did not drift into the gap after the
//             line); line starts never go back. A line that fails is DEMOTED: it becomes the provider's line again
//             (line-level highlight).
//   whole     fewer than 60 % of the lines that have letters end up word-synced, or more than 25 % of the words on the
//             word-synced lines last 40 ms or less (spikes: the voice was quiet right after the word, Align.EndAtVoice,
//             so the words sit where nobody sings): the result is REJECTED (not shown, not saved), and the track keeps
//             the provider's line sync. While a job runs, the lines timed so far are
//             judged once there are 20 with letters, against a lower bar (50 %): measured on the 187 cached results
//             that still match their provider document, that stops 19 of the 21 results the end rejects and none
//             that the end accepts (60 % from 10 lines on stopped 10 good ones).
//
// Unlike the provider gate (Lyrics.Timing.HasUnusableWordTiming), a line whose words all last zero time is judged too.
//
// Measured (2026-11-11) with the offline harness on 34 Japanese songs against human per-character timing, and without
// audio on the 191 cached English/Dutch/Korean results that still match their provider document:
//   offset    two Japanese songs whose provider lines sit ~0.9 s off the audio lost 28 lines timed within 100 ms of the
//             human timing to the window and were rejected; widened by the offset they publish (one from 6 to 37 of
//             39 lines). en/nl/ko: 4 rejected results publish (3 with the drift rule too; one sits a steady 1.95 s
//             off, one 1.5 s), and no line that passes without it is demoted (the window only grows). The provider's line starts are kept as they are (Align.WordSyncedLine): the lines that stay
//             line-level keep the provider's timeline, and shifting a whole document is the provider reranker's job
//             (Lyrics.Doc.OffsetMsApplied), not the aligner's.
//   zero      Japanese: one zero-length kana or kanji demoted a good line of under 10 units (good lines demoted for it:
//             4 -> 0; a collapsed line, two or more zero units, is still caught). Words keep 10 %: allowing one zero-
//             length word would publish 5 rejected en/ko results, 4 of them ones the earlier audit found doubtful.
//   spikes    after the word-end rule: Japanese (harness, audio) at most 8 % of a song's accepted units last <= 40 ms
//             (median 1.7 %); human word timing 0 %. en/nl/ko cached results with the rule replayed without the
//             envelope: no verdict changes. Before the rule a fifth of all Korean words were 20 ms spikes.
//   drift     Japanese: demotes 5 lines more than 1 s off (accepted lines that far off on published songs: 10 -> 6) and
//             no line within 300 ms of the human timing. en/nl/ko: 40 lines that pass without it are demoted, most of
//             them a last line drifted into the outro or a line whose words are crammed into the second before the
//             next line; no result that passes without it is rejected.

using System.Globalization;

namespace Wavee;

public static partial class AiLyrics
{
    public static class Acceptance
    {
        /// <summary>How far before its provider line a word may start (and after the next line's start it may end).</summary>
        public const long LeadMs = 600;

        /// <summary>A line with more than this share of zero-duration words is demoted (a character-timed language
        /// allows one zero-length unit however short the line).</summary>
        public const double MaxZeroShare = 0.10;

        /// <summary>A line whose first word starts more than this after its provider start (moved by the result's
        /// offset) is demoted: its words drifted into the gap after the line.</summary>
        public const long DriftMs = 2000;

        /// <summary><see cref="TrackOffsetMs"/> needs at least this many word-synced lines (fewer: no offset).</summary>
        public const int OffsetMinLines = 5;

        /// <summary>A word lasting this long or less is a spike (<see cref="MaxSpikeShare"/>): two aligner frames.</summary>
        public const long SpikeMs = 40;

        /// <summary>A result whose word-synced lines carry more than this share of spikes is rejected: its words were placed
        /// where the voice is quiet (Align.EndAtVoice gives a sung word its sound).</summary>
        public const double MaxSpikeShare = 0.25;

        /// <summary>A result with fewer word-synced lines than this share of its lines with letters is rejected.</summary>
        public const double MinSyncedShare = 0.60;

        /// <summary>While a job runs, the whole-result share is judged only from this many lines with letters on…</summary>
        public const int MinLinesWhileRunning = 20;

        /// <summary>…and against this lower bar (an early verse can be harder than the song).</summary>
        public const double MinSyncedShareWhileRunning = 0.50;

        /// <summary>The source has provider line times worth keeping (not plain text).</summary>
        public static bool IsLineSynced(Lyrics.Doc doc) => doc.IsSynced && doc.Sync != Lyrics.SyncKind.Unsynced;

        /// <summary>What the check found. <see cref="Eligible"/>: judged lines with letters; <see cref="Synced"/>: of
        /// those, the ones word-synced by the aligner after the check; the four causes count demoted lines;
        /// <see cref="Words"/>/<see cref="Spikes"/>: the words on the accepted word-synced lines, and those of them lasting
        /// <see cref="SpikeMs"/> or less;
        /// <see cref="OffsetMs"/>: the result's whole-track offset (<see cref="TrackOffsetMs"/>) the window was widened by.</summary>
        public readonly record struct Verdict(int Judged, int Eligible, int Synced, int Demoted, int Disorder, int Zero, int Window, int Drift,
            long OffsetMs, bool Rejected, int Words = 0, int Spikes = 0)
        {
            public string Describe()
            {
                var ci = CultureInfo.InvariantCulture;
                return $"judged={Judged.ToString(ci)} eligible={Eligible.ToString(ci)} synced={Synced.ToString(ci)} " +
                       $"share={(Eligible == 0 ? 0 : (double)Synced / Eligible).ToString("0.00", ci)} demoted={Demoted.ToString(ci)} " +
                       $"(order={Disorder.ToString(ci)} zero={Zero.ToString(ci)} window={Window.ToString(ci)} drift={Drift.ToString(ci)}) " +
                       $"offset={OffsetMs.ToString(ci)}ms spikes={Spikes.ToString(ci)}/{Words.ToString(ci)}";
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
            bool characters = Align.IsCharacterTimed(result.Language);
            long offset = synced ? TrackOffsetMs(source, result, judged) : 0;
            int eligible = 0, wordSynced = 0, demoted = 0, disorder = 0, zero = 0, window = 0, drift = 0, words = 0, spikes = 0;
            var lines = new List<Lyrics.Line>(result.Lines.Count);
            long prevStart = long.MinValue;
            for (int i = 0; i < result.Lines.Count; i++)
            {
                var line = result.Lines[i];
                if (i >= judged) { lines.Add(line); continue; }
                var src = source.Lines[i];
                bool hasLetters = HasLetter(src.Text);
                if (hasLetters) eligible++;
                if (!IsOurs(line, src))
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
                bool fewZeros = zeros <= (characters ? Math.Max(1, MaxZeroShare * syl.Count) : MaxZeroShare * syl.Count);
                bool inside = true, near = true;
                if (synced)
                {
                    long lo = src.StartMs + Math.Min(0, offset) - LeadMs;
                    long hi = i + 1 < source.Lines.Count ? source.Lines[i + 1].StartMs + Math.Max(0, offset) + LeadMs : long.MaxValue;
                    if (line.StartMs < lo) inside = false;
                    for (int k = 0; k < syl.Count && inside; k++)
                        if (syl[k].StartMs < lo || syl[k].EndMs > hi) inside = false;
                    near = syl[0].StartMs <= src.StartMs + offset + DriftMs;
                }
                if (ordered && fewZeros && inside && near)
                {
                    if (hasLetters) wordSynced++;
                    words += syl.Count;
                    for (int k = 0; k < syl.Count; k++) if (syl[k].EndMs - syl[k].StartMs <= SpikeMs) spikes++;
                    lines.Add(line);
                    prevStart = Math.Max(prevStart, line.StartMs);
                    continue;
                }
                demoted++;
                if (!ordered) disorder++;
                else if (!fewZeros) zero++;
                else if (!inside) window++;
                else drift++;
                var back = synced ? src : line with { Syllables = [], IsWordByWord = false, StartMs = Math.Max(prevStart, line.StartMs) };
                lines.Add(back);
                prevStart = Math.Max(prevStart, back.StartMs);
            }
            bool spiky = spikes > MaxSpikeShare * words;
            bool rejected = final
                ? eligible == 0 || wordSynced < MinSyncedShare * eligible || spiky
                : eligible >= MinLinesWhileRunning && (wordSynced < MinSyncedShareWhileRunning * eligible || spiky);
            verdict = new Verdict(judged, eligible, wordSynced, demoted, disorder, zero, window, drift, offset, rejected, words, spikes);
            return rejected ? null : result with { Lines = lines };
        }

        /// <summary>The result's whole-track offset from the provider's line times (ms; positive: the singing comes
        /// later): over the first <paramref name="judgedLines"/> lines, the median of (first word start - provider line
        /// start) over the word-synced lines with letters, the lower middle of an even count; 0 with fewer than
        /// <see cref="OffsetMinLines"/> such lines. Without it, a provider whose lines sit a steady second off the audio
        /// demotes most lines of a good result.</summary>
        public static long TrackOffsetMs(Lyrics.Doc source, Lyrics.Doc result, int judgedLines)
        {
            int n = Math.Min(Math.Min(source.Lines.Count, result.Lines.Count), judgedLines);
            var d = new List<long>(n);
            for (int i = 0; i < n; i++)
            {
                var src = source.Lines[i];
                var line = result.Lines[i];
                if (IsOurs(line, src) && HasLetter(src.Text)) d.Add(line.Syllables[0].StartMs - src.StartMs);
            }
            if (d.Count < OffsetMinLines) return 0;
            d.Sort();
            return d[(d.Count - 1) / 2];
        }

        /// <summary>A line the aligner timed: not the provider's own line, word by word, with words.</summary>
        static bool IsOurs(Lyrics.Line line, Lyrics.Line src) => !ReferenceEquals(line, src) && line.IsWordByWord && line.Syllables.Count > 0;

        static bool HasLetter(string? text)
        {
            if (text is null) return false;
            foreach (char c in text) if (char.IsLetter(c)) return true;
            return false;
        }
    }
}
