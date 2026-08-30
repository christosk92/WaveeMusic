using System;
using System.Collections.Generic;
using System.Linq;
using Wavee.Core;
using static Wavee.Backend.Lyrics.LyricsText;

namespace Wavee.Backend.Lyrics;

/// <summary>Per-candidate decision record (docs plan §9) — kept so a wrong pick is explainable (which provider, why it
/// won/lost, what offset was applied).</summary>
public sealed record LyricsDecision(
    string ProviderId, LyricsSyncKind Sync, double Score,
    double TextAgreement, double Coverage, double TimingScore, long AppliedOffsetMs, string Reason,
    // The sync tier AFTER any gate demotion — the one score component that is not simply a function of the document,
    // and the one a "why did the word-synced candidate lose?" question always turns out to hinge on.
    double SyncScore = 0d);

public sealed record RankedLyrics(LyricsDocument? Winner, LyricsDecision? Best, IReadOnlyList<LyricsDecision> All);

/// <summary>The reranker (docs/lyrics-aggregator-reranker-plan.md §9): score every candidate together against the
/// Spotify-native reference (text agreement + timing coherence + sync tier + coverage + provider prior), correct a
/// constant timing offset on the winner, and gate word-synced candidates so a WRONG word-synced lyric can't beat a
/// correct line-synced one. Pure + deterministic → unit-testable.</summary>
public static class LyricsReranker
{
    const double WText = 0.40, WSync = 0.25, WTiming = 0.20, WCoverage = 0.10, WPrior = 0.05;
    const double LineMatchThreshold = 0.5;   // token overlap to call two lines "the same line"
    const double DriftToleranceMs = 600;     // MAD above this ⇒ timing is locally wrong
    const long TimingFallbackBucketMs = 100;
    const long TimingFallbackPairToleranceMs = 600;
    // Sync-gate thresholds. The text bar is a low FLOOR (was 0.80 — which rejected correct-but-divergent word-sync); the
    // timing/coverage guardrails do the real work of rejecting a wrong song. See the gate in Rank for the reasoning.
    const double SyncGateTextFloor = 0.15, SyncGateTiming = 0.5, SyncGateCoverage = 0.6;
    // TIER-PROMOTION bar — deliberately much stricter than the sync gate above, because the CONSEQUENCE is much
    // stronger. Clearing the sync gate merely preserves a candidate's sync weight (worth +0.10 in the blended score);
    // clearing this one lets a candidate WIN OUTRIGHT over a better-scoring line lyric. Reusing the sync-gate floors
    // here would promote a marginal word-sync (text 0.16) over a perfect line lyric — it used to score ~0.51 against
    // 0.877 and lose, which was the right answer. A candidate that cannot clear this bar is not rejected; it simply
    // falls back to the blended score, where a genuinely strong word-sync can still win on its own merits.
    //
    // COVERAGE is min/max LINE COUNT, which is only meaningful once both documents count LYRICS rather than padding.
    // It used to sit at an emergency 0.35 because Spotify pads with blank/♪ rows and Kugou does not: a KRC matching 34
    // of its 35 lines scored coverage 0.565 purely on the padding. LyricsClean now strips those at the fetch chokepoint
    // (same rule, both sides), the same pair measures 0.872, and the bar can be a real one again.
    const double TierTextFloor = 0.50, TierTiming = 0.70, TierCoverage = 0.60;

    public static RankedLyrics Rank(IReadOnlyList<LyricsCandidate> candidates, LyricsDocument? reference)
    {
        if (candidates.Count == 0) return new RankedLyrics(null, null, Array.Empty<LyricsDecision>());

        List<string[]>? refTokens = reference is null ? null
            : reference.Lines.Select(l => Tokens(l.Text)).ToList();

        var decisions = new List<LyricsDecision>(candidates.Count);
        // Stage-one bookkeeping for the tier pass below: is this candidate corroborated as the right recording, and what
        // sync tier does it ACTUALLY deliver (a gated word-sync delivers line, because that is what it gets stripped to).
        var verified = new bool[candidates.Count];
        var tiers = new int[candidates.Count];
        int bestIdx = -1; double bestScore = double.NegativeInfinity;

        for (int i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            bool trusted = c.Basis is MatchBasis.Identity or MatchBasis.Isrc;
            var candTokens = c.Document.Lines.Select(l => Tokens(l.Text)).ToList();

            double text, coverage, timing; long applied = 0; string reason;
            if (refTokens is { Count: > 0 } && candTokens.Count > 0)
            {
                var (lcs, pairs) = LcsAlign(candTokens, refTokens);
                bool timingFallback = false;
                if (pairs.Count < 3 && trusted && TryTimingFallbackPairs(c.Document, reference!, out var fallbackPairs))
                {
                    pairs = fallbackPairs;
                    timingFallback = true;
                }
                int overlap = Math.Min(candTokens.Count, refTokens.Count);
                text = lcs / (double)Math.Max(1, overlap);
                coverage = overlap / (double)Math.Max(1, Math.Max(candTokens.Count, refTokens.Count));
                (timing, applied) = TimingScoreVsRef(c.Document, reference!, pairs);
                reason = $"ref-align lcs={lcs}/{overlap} off={applied}ms";
                if (timingFallback) reason += " [timing-fallback]";
            }
            else
            {
                // No reference: cannot verify content. Credit coverage + intrinsic timing sanity; stay text-neutral so a
                // word-synced, internally-coherent candidate can still win on sync/timing.
                text = candTokens.Count > 0 ? 0.6 : 0.0;
                coverage = Math.Min(1.0, candTokens.Count / 10.0);
                timing = IntrinsicTimingSanity(c.Document);
                reason = "no-reference";
            }

            double sync = c.Sync switch { LyricsSyncKind.Syllable => 1.0, LyricsSyncKind.Line => 0.6, LyricsSyncKind.Unsynced => 0.2, _ => 0.2 };
            // Sync gate: a word-synced candidate keeps its sync advantage unless it looks like a DIFFERENT song. An
            // identity/ISRC-matched source IS the exact recording (AMLL / Spotify-native / Musixmatch-ISRC), so it is never
            // demoted on fuzzy text disagreement with Spotify's own (often sparse/romanized) line lyric. Otherwise apply a
            // low text FLOOR plus the timing/coverage guardrails: correct-but-divergent karaoke (romanization, CJK,
            // ad-libs) scores ~0.15–0.5 text and must survive, while a truly-wrong song has incoherent timing (high MAD →
            // timing < 0.5) and/or mismatched line counts (coverage < 0.6) and is still demoted below a clean line candidate.
            if (c.Sync == LyricsSyncKind.Syllable && refTokens is { Count: > 0 } && !trusted
                && !(text >= SyncGateTextFloor && timing >= SyncGateTiming && coverage >= SyncGateCoverage))
            {
                sync = 0.45;
                reason += " [sync-gate:demoted]";
            }
            // WORD-TIMING gate — a different question from the sync gate above, and deliberately NOT exempted by trust.
            // An identity/ISRC match proves the candidate is the right RECORDING; it says nothing about whether its word
            // offsets are physically singable. A body whose lines fire twelve words in 167ms is word-synced in name only,
            // and the reranker cannot see it any other way: TimingScoreVsRef aligns LINE STARTS, which such a document
            // gets perfectly right. Without this it takes the full syllable tier (1.0) and beats a clean line-synced
            // candidate — exactly how a broken karaoke body wins over correct lyrics. Demote to the LINE tier (0.6, not
            // the sync gate's 0.45): it IS the right song, delivered at line resolution, which is what the winner is then
            // repaired down to (see LyricsTiming.StripWordTiming below).
            int impossible = 0, judged = 0;   // the && short-circuits for a non-syllable candidate, so seed them
            bool wordTimingRejected = c.Sync == LyricsSyncKind.Syllable
                && LyricsTiming.HasImplausibleWordTiming(c.Document, out impossible, out judged);
            if (wordTimingRejected)
            {
                sync = Math.Min(sync, 0.6);
                reason += $" [word-timing-gate: {impossible}/{judged} lines impossibly fast]";
            }

            double score = WText * text + WSync * sync + WTiming * timing + WCoverage * coverage + WPrior * Math.Clamp(c.Prior, 0, 1);
            decisions.Add(new LyricsDecision(c.ProviderId, c.Sync, score, text, coverage, timing, applied, reason, sync));
            if (score > bestScore) { bestScore = score; bestIdx = i; }

            // VERIFIED = corroborated well enough to override a better-scoring candidate on tier alone. Identity/ISRC is
            // verified by construction (it IS the exact recording); a fuzzy metadata match has to clear the strict
            // TierXxx bar — NOT the sync gate's floors, which exist for a much weaker consequence.
            verified[i] = trusted
                || (refTokens is { Count: > 0 } && text >= TierTextFloor && timing >= TierTiming && coverage >= TierCoverage);
            // The tier it actually DELIVERS — a rejected word-sync delivers line, because that is what the winner
            // repair strips it to.
            tiers[i] = wordTimingRejected ? 2 : c.Sync switch
            {
                LyricsSyncKind.Syllable => 3,
                LyricsSyncKind.Line => 2,
                LyricsSyncKind.Unsynced => 1,
                _ => 0,
            };
        }

        // ── stage two: among the VERIFIED candidates, richness decides ───────────────────────────────────────────────
        // The weighted sum answers two questions at once and therefore answers neither well: it mixes "is this the right
        // song?" with "is this the better lyric?". Those are different kinds of judgement — the first is a THRESHOLD,
        // the second an ORDERING. (BetterLyrics splits them explicitly: a metadata-similarity threshold for trust, then
        // the user's provider order for quality. It has no content reranker at all, which is why it cannot detect a
        // fabricated richsync — but the separation is the right idea.)
        //
        // Conflating them is what made a genuine word-synced candidate lose: sync is weighted 0.25, so syllable over
        // line is worth only +0.10, and a provider with a perfect text match beats that on WText alone. Concretely,
        // Musixmatch's line LRC scored 0.885 against a real Kugou KRC's ~0.74 — the karaoke lost to the paragraph.
        //
        // So: once a candidate is verified, prefer the richest tier it actually delivers, and use the score only to
        // break ties WITHIN a tier. Only with a reference to verify against — unverifiable candidates keep the plain
        // score ordering, because promoting an unchecked word-sync on tier alone is precisely the wrong-song failure
        // the sync gate exists to prevent.
        int chosenIdx = bestIdx;
        if (refTokens is { Count: > 0 })
        {
            int bestTier = -1; double bestTierScore = double.NegativeInfinity;
            for (int i = 0; i < candidates.Count; i++)
            {
                if (!verified[i]) continue;
                if (tiers[i] > bestTier || (tiers[i] == bestTier && decisions[i].Score > bestTierScore))
                { bestTier = tiers[i]; bestTierScore = decisions[i].Score; chosenIdx = i; }
            }
        }

        var winnerCand = candidates[chosenIdx];
        long bestOffset = decisions[chosenIdx].AppliedOffsetMs;
        // Repair before publishing: if the winner only won BECAUSE nothing better existed, it must still not reach the
        // view claiming word timing it does not have. Its line starts are kept (they are the part that is right) and the
        // unsingable syllables/ends are dropped, leaving a document shaped like any other line-synced one.
        var winnerDoc = winnerCand.Document;
        if (winnerDoc.Sync == LyricsSyncKind.Syllable && LyricsTiming.HasImplausibleWordTiming(winnerDoc, out _, out _))
            winnerDoc = LyricsTiming.StripWordTiming(winnerDoc);
        var winner = ApplyOffset(winnerDoc, bestOffset);
        return new RankedLyrics(winner, decisions[chosenIdx], decisions);
    }

    // ── text agreement: fuzzy line-sequence LCS ──────────────────────────────────────────────────────────────────────

    static string[] Tokens(string text)
    {
        var n = Normalize(text);
        return n.Length == 0 ? Array.Empty<string>() : n.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    static bool LineMatch(string[] a, string[] b)
    {
        if (a.Length == 0 && b.Length == 0) return true;
        if (a.Length == 0 || b.Length == 0) return false;
        var set = new HashSet<string>(a);
        int inter = 0;
        foreach (var t in b) if (set.Contains(t)) inter++;
        double overlap = inter / (double)Math.Min(a.Length, b.Length);   // overlap coefficient (length-robust)
        return overlap >= LineMatchThreshold;
    }

    /// <summary>LCS over the two line sequences with fuzzy line equality; returns the LCS length and the matched index
    /// pairs (candIndex, refIndex) in order — the pairs drive the timing-offset estimate.</summary>
    static (int Lcs, List<(int C, int R)> Pairs) LcsAlign(List<string[]> cand, List<string[]> reff)
    {
        int n = cand.Count, m = reff.Count;
        var dp = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                dp[i, j] = LineMatch(cand[i], reff[j]) ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);

        var pairs = new List<(int, int)>();
        for (int i = 0, j = 0; i < n && j < m;)
        {
            if (LineMatch(cand[i], reff[j])) { pairs.Add((i, j)); i++; j++; }
            else if (dp[i + 1, j] >= dp[i, j + 1]) i++;
            else j++;
        }
        return (dp[0, 0], pairs);
    }

    // ── timing ───────────────────────────────────────────────────────────────────────────────────────────────────────

    static bool TryTimingFallbackPairs(LyricsDocument cand, LyricsDocument reff, out List<(int C, int R)> pairs)
    {
        pairs = new List<(int C, int R)>();
        if (cand.Lines.Count < 3 || reff.Lines.Count < 3) return false;

        var buckets = new Dictionary<long, int>();
        for (int c = 0; c < cand.Lines.Count; c++)
        {
            long cs = cand.Lines[c].StartMs;
            for (int r = 0; r < reff.Lines.Count; r++)
            {
                long bucket = DeltaBucket(cs - reff.Lines[r].StartMs);
                buckets.TryGetValue(bucket, out int n);
                buckets[bucket] = n + 1;
            }
        }

        long bestBucket = 0;
        int bestCount = 0;
        bool tied = false;
        foreach (var kv in buckets)
        {
            if (kv.Value > bestCount)
            {
                bestBucket = kv.Key;
                bestCount = kv.Value;
                tied = false;
            }
            else if (kv.Value == bestCount)
            {
                tied = true;
            }
        }
        if (bestCount < 3 || tied) return false;

        int nextRef = 0;
        for (int c = 0; c < cand.Lines.Count && nextRef < reff.Lines.Count; c++)
        {
            int bestRef = -1;
            long bestErr = long.MaxValue;
            long cs = cand.Lines[c].StartMs;
            for (int r = nextRef; r < reff.Lines.Count; r++)
            {
                long err = Math.Abs((cs - reff.Lines[r].StartMs) - bestBucket);
                if (err < bestErr)
                {
                    bestErr = err;
                    bestRef = r;
                }
                if (reff.Lines[r].StartMs > cs - bestBucket + TimingFallbackPairToleranceMs && bestErr <= TimingFallbackPairToleranceMs)
                    break;
            }
            if (bestRef < 0 || bestErr > TimingFallbackPairToleranceMs) continue;
            pairs.Add((c, bestRef));
            nextRef = bestRef + 1;
        }

        return pairs.Count >= 3;
    }

    static long DeltaBucket(long delta)
        => (long)Math.Round(delta / (double)TimingFallbackBucketMs, MidpointRounding.AwayFromZero) * TimingFallbackBucketMs;

    static (double Score, long AppliedOffsetMs) TimingScoreVsRef(LyricsDocument cand, LyricsDocument reff, List<(int C, int R)> pairs)
    {
        if (pairs.Count < 3) return (0.6, 0);   // too few matches → neutral, don't auto-correct
        var deltas = new long[pairs.Count];
        for (int k = 0; k < pairs.Count; k++)
            deltas[k] = cand.Lines[pairs[k].C].StartMs - reff.Lines[pairs[k].R].StartMs;
        long median = Median(deltas);
        var absDev = deltas.Select(d => Math.Abs(d - median)).ToArray();
        long mad = Median(absDev);
        double score = Math.Clamp(1.0 - mad / DriftToleranceMs, 0.0, 1.0);
        // Correct the constant offset only when internally coherent (low drift); otherwise the times are locally wrong and
        // shifting wouldn't help — leave it and let the low score demote it.
        long applied = mad <= DriftToleranceMs ? -median : 0;
        return (score, applied);
    }

    static double IntrinsicTimingSanity(LyricsDocument doc)
    {
        if (doc.Lines.Count < 2) return 0.4;
        int monotonic = 0;
        for (int i = 1; i < doc.Lines.Count; i++)
            if (doc.Lines[i].StartMs >= doc.Lines[i - 1].StartMs) monotonic++;
        return 0.3 + 0.6 * (monotonic / (double)(doc.Lines.Count - 1));
    }

    static long Median(long[] v)
    {
        if (v.Length == 0) return 0;
        var s = (long[])v.Clone();
        Array.Sort(s);
        return s.Length % 2 == 1 ? s[s.Length / 2] : (s[s.Length / 2 - 1] + s[s.Length / 2]) / 2;
    }

    /// <summary>Shift every line + syllable timestamp by <paramref name="offsetMs"/> and record it on the document.</summary>
    public static LyricsDocument ApplyOffset(LyricsDocument doc, long offsetMs)
    {
        if (offsetMs == 0) return doc;
        long Shift(long t) => Math.Max(0, t + offsetMs);
        var lines = doc.Lines.Select(l => l with
        {
            StartMs = Shift(l.StartMs),
            EndMs = l.EndMs is { } e ? Shift(e) : (long?)null,
            Syllables = l.Syllables.Count == 0 ? l.Syllables
                : l.Syllables.Select(s => new LyricSyllable(Shift(s.StartMs), Shift(s.EndMs), s.Text)).ToList(),
        }).ToList();
        return doc with { Lines = lines, OffsetMsApplied = offsetMs };
    }
}
