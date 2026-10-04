// ── AiLyrics/AiLyrics.Align.cs ───────────────────────────────────────────────────────────────────────────────────────
// Vocab, LyricWords, Graph, OnlineViterbi, Snap, Fill, Assemble
//
// Role: CORE (pure: no I/O, no clock, no engine)
//
// Places the lyric words on the audio. Ported from the lab prototype (`aligner.py`, `refine.py`, `to_lrc.py`), where it
// was measured on 8 songs (plan §1). The model gives, every 20 ms, a log-probability per letter; this file finds the
// single best monotonic path of the lyric's letters through those frames (CTC forced alignment) and turns it into
// word timings and a word-synced Lyrics.Doc.
//
//   Graph          one token per letter, a separator between words; words in parentheses are OPTIONAL (backing vocals
//                  and ad-libs are often faint or missing in the separated vocals): a skip edge jumps over them.
//   OnlineViterbi  the forward pass advances frame by frame and is exact. Tokens are COMMITTED in order; a commit
//                  backtraces from the best state at the newest frame. The worker commits just in time (when the
//                  playhead gets close), so each decision has heard as much of the song as possible. A line-time
//                  prior (when the input is line-synced) keeps identical chorus lines from sliding a bar late.
//   Snap           a word that starts after silence moves back to where the vocal level rises.
//   Fill/Assemble  every word of the line text gets a time (skipped optional words share their gap) and the syllable
//                  texts join back to the line text, which the karaoke wipe measures against.

using System.Text.Json;
using System.Text.RegularExpressions;

namespace Wavee;

public static partial class AiLyrics
{
    public static partial class Align
    {
        public const double FrameSeconds = 0.02;          // wav2vec2: one frame per 320 samples at 16 kHz
        const double Neg = -1e30;

        // ── vocabulary ──────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The aligner's letter vocabulary (the model's vocab.json): letter -> class id. The blank is
        /// &lt;pad&gt;; "|" separates words. Upper-case vocabularies (the English model) get upper-cased text.</summary>
        public sealed class Vocab
        {
            readonly Dictionary<string, int> _ids;
            public readonly int Blank, Separator, Classes;
            public readonly bool Upper;

            public Vocab(Dictionary<string, int> ids)
            {
                _ids = ids;
                Blank = ids.TryGetValue("<pad>", out int b) || ids.TryGetValue("[PAD]", out b) ? b : 0;   // [PAD]: the Korean model's blank (1204)
                Separator = ids["|"];
                Classes = ids.Values.Max() + 1;
                Upper = ids.ContainsKey("E");
            }

            public static Vocab Parse(string json)
            {
                var ids = new Dictionary<string, int>(StringComparer.Ordinal);
                using var doc = JsonDocument.Parse(json);
                foreach (var p in doc.RootElement.EnumerateObject()) ids[p.Name] = p.Value.GetInt32();
                return new Vocab(ids);
            }

            /// <summary>The class ids of a word's letters; letters the model does not know are tried without their
            /// accent, then dropped. Empty when nothing is left (digits, symbols).</summary>
            public void Tokens(string word, List<int> into)
            {
                // NFC first: lyrics can carry decomposed Hangul (jamo) or decomposed accents; the vocabularies are composed
                string w = (Upper ? word.ToUpperInvariant() : word.ToLowerInvariant()).Replace('’', '\'').Normalize(System.Text.NormalizationForm.FormC);
                foreach (char c in w)
                {
                    string ch = c.ToString();
                    if (!_ids.ContainsKey(ch))
                    {
                        string bare = StripAccent(ch);
                        if (!_ids.ContainsKey(bare)) continue;
                        ch = bare;
                    }
                    if (ch == "|") continue;
                    into.Add(_ids[ch]);
                }
            }

            static string StripAccent(string s)
            {
                var sb = new System.Text.StringBuilder();
                foreach (char c in s.Normalize(System.Text.NormalizationForm.FormD))
                    if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark) sb.Append(c);
                return sb.ToString();
            }
        }

        // ── the words of a line ─────────────────────────────────────────────────────────────────────────────────────

        /// <summary>One word of a lyric line: its text, where it starts in the line text, and whether it sits inside
        /// parentheses (optional).</summary>
        public readonly record struct LyricWord(string Text, int Index, bool Optional);

        static readonly Regex s_word = new(@"[\w'’]+", RegexOptions.CultureInvariant);

        public static List<LyricWord> Words(string text)
        {
            var list = new List<LyricWord>();
            int depth = 0, pos = 0;
            foreach (Match m in s_word.Matches(text))
            {
                for (int i = pos; i < m.Index; i++)
                {
                    if (text[i] == '(') depth++;
                    else if (text[i] == ')') depth = Math.Max(0, depth - 1);
                }
                list.Add(new LyricWord(m.Value, m.Index, depth > 0));
                pos = m.Index + m.Length;
            }
            return list;
        }

        // ── the graph ───────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The lyric as CTC tokens. <see cref="OwnerLine"/>/<see cref="OwnerWord"/> are -1 on separators.
        /// Skip edges (<see cref="SkipSrc"/> -> <see cref="SkipDst"/>, both STATE indices) jump over an optional word:
        /// from the blank before its first letter to the blank after its last.</summary>
        public sealed class Graph
        {
            public readonly int[] Tokens, OwnerLine, OwnerWord, SkipSrc, SkipDst;
            public int Length => Tokens.Length;

            Graph(int[] tokens, int[] ol, int[] ow, int[] ss, int[] sd) { Tokens = tokens; OwnerLine = ol; OwnerWord = ow; SkipSrc = ss; SkipDst = sd; }

            public static Graph Build(IReadOnlyList<IReadOnlyList<LyricWord>> lines, Vocab vocab)
            {
                var tokens = new List<int>(); var ol = new List<int>(); var ow = new List<int>();
                var ss = new List<int>(); var sd = new List<int>();
                var buf = new List<int>();
                for (int li = 0; li < lines.Count; li++)
                {
                    var words = lines[li];
                    for (int wi = 0; wi < words.Count; wi++)
                    {
                        buf.Clear();
                        vocab.Tokens(words[wi].Text, buf);
                        if (buf.Count == 0) continue;
                        if (tokens.Count > 0) { tokens.Add(vocab.Separator); ol.Add(-1); ow.Add(-1); }
                        int k0 = tokens.Count;
                        foreach (int t in buf) { tokens.Add(t); ol.Add(li); ow.Add(wi); }
                        int k1 = tokens.Count - 1;
                        if (words[wi].Optional) { ss.Add(2 * k0); sd.Add(2 * (k1 + 1)); }
                    }
                }
                return new Graph(tokens.ToArray(), ol.ToArray(), ow.ToArray(), ss.ToArray(), sd.ToArray());
            }
        }

        // ── the online Viterbi ──────────────────────────────────────────────────────────────────────────────────────

        /// <summary>CTC forced alignment that advances one frame at a time. States: blank, t0, blank, t1, ..., blank.
        /// Backpointers are 2 bits per state per frame (stay, from s-1, from s-2, from a skip edge).</summary>
        public sealed class OnlineViterbi
        {
            readonly int _s, _l, _blank;
            readonly int[] _lab;
            readonly bool[] _skip2;
            readonly int[] _optSrc, _optDst;
            readonly Dictionary<int, int> _optFrom = new();
            readonly double[]? _winLo, _winHi;              // per state, seconds
            readonly double _priorPerS, _skipCost;
            double[] _dp, _next;
            readonly List<byte[]> _bp = new();
            readonly int _rowBytes;
            int _t, _committed;
            readonly int[] _first, _last;                   // committed spans; -1 = skipped
            readonly bool[] _done;
            readonly int[] _tmpFirst, _tmpLast;

            public int Frames => _t;
            public int Committed => _committed;
            public int TokenCount => _l;

            /// <param name="windows">Per token: the time range (s) its line is expected in, or null for no prior.</param>
            public OnlineViterbi(Graph g, int blank, IReadOnlyList<(double Lo, double Hi)>? windows = null,
                double priorPerS = 0.5, double skipCost = 2.0)
            {
                _l = g.Length; _s = 2 * _l + 1; _blank = blank;
                _lab = new int[_s];
                for (int s = 0; s < _s; s++) _lab[s] = s % 2 == 1 ? g.Tokens[s / 2] : blank;
                _skip2 = new bool[_s];
                for (int k = 1; k < _l; k++) _skip2[2 * k + 1] = g.Tokens[k] != g.Tokens[k - 1];
                _optSrc = g.SkipSrc; _optDst = g.SkipDst;
                for (int i = 0; i < _optDst.Length; i++) _optFrom[_optDst[i]] = _optSrc[i];
                _priorPerS = priorPerS; _skipCost = skipCost;
                if (windows is not null && _l > 0)
                {
                    _winLo = new double[_s]; _winHi = new double[_s];
                    for (int k = 0; k < _l; k++) { _winLo[2 * k + 1] = windows[k].Lo; _winHi[2 * k + 1] = windows[k].Hi; }
                    _winLo[0] = windows[0].Lo; _winHi[0] = windows[0].Hi;
                    _winLo[_s - 1] = windows[_l - 1].Lo; _winHi[_s - 1] = windows[_l - 1].Hi;
                    for (int k = 1; k < _l; k++)
                    {
                        _winLo[2 * k] = Math.Min(windows[k - 1].Lo, windows[k].Lo);
                        _winHi[2 * k] = Math.Max(windows[k - 1].Hi, windows[k].Hi);
                    }
                }
                _dp = new double[_s]; _next = new double[_s];
                _rowBytes = (_s + 3) / 4;
                _first = new int[_l]; _last = new int[_l]; _done = new bool[_l];
                _tmpFirst = new int[_l]; _tmpLast = new int[_l];
            }

            public bool IsCommitted(int token) => token < _committed;

            /// <summary>The committed span of a token in frames, or null if it was skipped (an optional word).</summary>
            public (int First, int Last)? Span(int token)
                => token < _committed && _first[token] >= 0 ? (_first[token], _last[token]) : null;

            /// <summary>Advance by the given frames of log-probabilities, row-major [frame * classes + class].</summary>
            public void Feed(ReadOnlySpan<float> logp, int classes)
            {
                int frames = logp.Length / classes;
                for (int f = 0; f < frames; f++)
                {
                    var row = logp.Slice(f * classes, classes);
                    byte[] bp = new byte[_rowBytes];
                    if (_t == 0)
                    {
                        Array.Fill(_dp, Neg);
                        _dp[0] = row[_lab[0]];
                        if (_s > 1) _dp[1] = row[_lab[1]];
                        for (int i = 0; i < _optSrc.Length; i++)
                            if (_optSrc[i] == 0) _dp[_optDst[i]] = Math.Max(_dp[_optDst[i]], row[_lab[_optDst[i]]] - _skipCost);
                    }
                    else
                    {
                        double ts = _t * FrameSeconds;
                        for (int s = 0; s < _s; s++)
                        {
                            double best = _dp[s]; int code = 0;
                            if (s >= 1 && _dp[s - 1] > best) { best = _dp[s - 1]; code = 1; }
                            if (s >= 2 && _skip2[s] && _dp[s - 2] > best) { best = _dp[s - 2]; code = 2; }
                            _next[s] = best;
                            if (code != 0) bp[s >> 2] |= (byte)(code << ((s & 3) * 2));
                        }
                        for (int i = 0; i < _optSrc.Length; i++)
                        {
                            double cand = _dp[_optSrc[i]] - _skipCost;
                            int d = _optDst[i];
                            if (cand > _next[d])
                            {
                                _next[d] = cand;
                                bp[d >> 2] = (byte)(bp[d >> 2] & ~(3 << ((d & 3) * 2)) | (3 << ((d & 3) * 2)));
                            }
                        }
                        for (int s = 0; s < _s; s++)
                        {
                            double v = _next[s] + row[_lab[s]];
                            if (_winLo is not null)
                            {
                                double dist = Math.Max(0, Math.Max(_winLo[s] - ts, ts - _winHi![s]));
                                v -= _priorPerS * dist;
                            }
                            _next[s] = v;
                        }
                        (_dp, _next) = (_next, _dp);
                    }
                    _bp.Add(bp);
                    _t++;
                }
            }

            int Code(int t, int s) => (_bp[t][s >> 2] >> ((s & 3) * 2)) & 3;

            /// <summary>Commit, in token order, every token whose last frame is before the horizon: t - lag, or, when a
            /// deadline is given (the playhead is about to reach those words), max(t - lag, deadline) capped at t.
            /// <paramref name="final"/> commits everything along the path that ends in the last state. Returns the
            /// number of newly committed tokens.</summary>
            public int Commit(int lagFrames, bool final = false, int? deadlineFrame = null)
            {
                if (_t == 0 || _committed >= _l) return 0;
                int s;
                if (final) s = _s >= 2 && _dp[_s - 1] < _dp[_s - 2] ? _s - 2 : _s - 1;
                else { s = 0; for (int i = 1; i < _s; i++) if (_dp[i] > _dp[s]) s = i; }

                // backtrace until the path reaches the committed tokens
                for (int k = _committed; k < _l; k++) { _tmpFirst[k] = -1; _tmpLast[k] = -1; }
                int maxK = -1;
                for (int t = _t - 1; t >= 0; t--)
                {
                    if ((s & 1) == 1)
                    {
                        int k = s >> 1;
                        if (k < _committed) break;
                        if (_tmpLast[k] < 0) _tmpLast[k] = t;
                        _tmpFirst[k] = t;
                        if (k > maxK) maxK = k;
                    }
                    int code = Code(t, s);
                    s = code == 3 ? _optFrom[s] : s - code;
                    if (s < 0) break;
                }

                int horizon = final ? _t : _t - lagFrames;
                if (deadlineFrame is int dl && !final) horizon = Math.Min(_t, Math.Max(horizon, dl));
                int start = _committed, kk = _committed;
                while (kk < _l)
                {
                    if (_tmpLast[kk] >= 0)
                    {
                        if (_tmpLast[kk] >= horizon) break;
                        _first[kk] = _tmpFirst[kk]; _last[kk] = _tmpLast[kk];
                    }
                    else
                    {
                        // skipped by the path: safe to commit once a later token on the path is committed too
                        bool later = false;
                        for (int j = kk + 1; j <= maxK; j++) if (_tmpLast[j] >= 0) { later = _tmpLast[j] < horizon; break; }
                        if (!later && !final) break;
                        _first[kk] = -1; _last[kk] = -1;
                    }
                    _done[kk] = true;
                    kk++;
                }
                _committed = kk;
                if (!final && kk > 0)
                    for (int i = 0; i < Math.Min(_s, 2 * kk); i++) _dp[i] = Neg;   // the path is past these tokens now
                return kk - start;
            }
        }

        // ── word times ──────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>A word's time in seconds; NaN when the path skipped it.</summary>
        public readonly record struct WordTime(double Start, double End)
        {
            public static readonly WordTime None = new(double.NaN, double.NaN);
            public bool IsSet => !double.IsNaN(Start);
        }

        /// <summary>The committed word times per line from the Viterbi's committed tokens. A word is set only when all
        /// its tokens are committed.</summary>
        public static void CollectWordTimes(Graph g, OnlineViterbi v, WordTime[][] into)
        {
            for (int k = 0; k < v.Committed; k++)
            {
                int li = g.OwnerLine[k], wi = g.OwnerWord[k];
                if (li < 0) continue;
                if (v.Span(k) is not (int f0, int f1)) continue;
                double s = f0 * FrameSeconds, e = (f1 + 1) * FrameSeconds;
                var cur = into[li][wi];
                into[li][wi] = cur.IsSet ? new WordTime(Math.Min(cur.Start, s), Math.Max(cur.End, e)) : new WordTime(s, e);
            }
        }

        // ── silence snap ────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Moves word starts back to the rise out of silence (at most <paramref name="maxBackS"/>), never before
        /// the previous word's end, also across lines. <paramref name="env"/> gives the level in dB per 10 ms frame.</summary>
        public static void SnapToOnsets(WordTime[][] lines, Func<int, float> env, int envFrames, float quiet, double maxBackS = 0.40)
        {
            const double hop = 0.01;
            double prevEnd = double.NaN;
            foreach (var words in lines)
            {
                for (int i = 0; i < words.Length; i++)
                {
                    var w = words[i];
                    if (!w.IsSet) continue;
                    double s = w.Start;
                    double lo = double.IsNaN(prevEnd) ? s - maxBackS : Math.Max(prevEnd, s - maxBackS);
                    int fLo = Math.Max(0, (int)(lo / hop)), fS = (int)(s / hop);
                    int fHi = Math.Min(envFrames - 1, fS + 5);
                    double ns = s;
                    if (fHi > fLo)
                    {
                        int lastQuiet = -1;
                        for (int f = fLo; f <= fHi; f++) if (env(f) < quiet) lastQuiet = f;
                        if (lastQuiet >= 0 && lastQuiet + 1 <= fHi) ns = (lastQuiet + 1) * hop;
                    }
                    ns = Math.Min(ns, s);
                    if (!double.IsNaN(prevEnd)) ns = Math.Max(ns, prevEnd);
                    words[i] = new WordTime(ns, w.End);
                    prevEnd = w.End;
                }
            }
        }

        // ── fill and assemble ───────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Gives every word of a line a time: runs of skipped words share the gap between their timed
        /// neighbours (never past <paramref name="nextStart"/>); a line with nothing timed starts at
        /// <paramref name="lineStart"/> at a natural pace. Returns false when the line has no words.</summary>
        public static bool Fill(WordTime[] words, double lineStart, double? nextStart)
        {
            int n = words.Length;
            if (n == 0) return false;
            int i = 0;
            while (i < n)
            {
                if (words[i].IsSet) { i++; continue; }
                int k = i;
                while (k < n && !words[k].IsSet) k++;
                double? left = i > 0 ? words[i - 1].End : null;
                double? right = k < n ? words[k].Start : null;
                int run = k - i;
                double l, r;
                if (left is null && right is null)
                {
                    l = lineStart; r = l + 0.4 * run;
                    if (nextStart is double ns) r = Math.Min(r, ns);
                }
                else if (left is null) { r = right!.Value; l = Math.Max(r - 0.3 * run, Math.Min(lineStart, r - 0.3 * run)); }
                else if (right is null)
                {
                    l = left.Value; r = l + 0.3 * run;
                    if (nextStart is double ns2) r = Math.Min(r, ns2);
                }
                else { l = left.Value; r = right.Value; }
                r = Math.Max(r, l);
                double step = (r - l) / run;
                for (int m = 0; m < run; m++) words[i + m] = new WordTime(l + m * step, l + (m + 1) * step);
                i = k;
            }
            return true;
        }

        /// <summary>A word-synced line from the provider's line and the word times: syllable texts are the line text cut
        /// at word starts, so they join back to <see cref="Lyrics.Line.Text"/> exactly (the wipe measures against it).</summary>
        public static Lyrics.Line WordSyncedLine(Lyrics.Line src, IReadOnlyList<LyricWord> words, WordTime[] times, double? nextStartS)
        {
            string text = src.Text;
            var syl = new List<Lyrics.Syllable>(words.Count);
            for (int i = 0; i < words.Count; i++)
            {
                int from = i == 0 ? 0 : words[i].Index;
                int to = i + 1 < words.Count ? words[i + 1].Index : text.Length;
                long s = (long)Math.Round(times[i].Start * 1000), e = (long)Math.Round(times[i].End * 1000);
                if (i + 1 == words.Count && nextStartS is double ns) e = Math.Min(e, (long)Math.Round(ns * 1000));
                e = Math.Max(e, s);
                syl.Add(new Lyrics.Syllable(s, e, text.Substring(from, to - from)));
            }
            return src with
            {
                StartMs = syl[0].StartMs,
                Syllables = syl,
                EndMs = src.EndMs ?? syl[^1].EndMs,
                IsWordByWord = true,
            };
        }
    }
}
