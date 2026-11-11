// ── AiLyrics/AiLyrics.Align.cs ───────────────────────────────────────────────────────────────────────────────────────
// Vocab, LyricWords, Graph, OnlineViterbi, Snap, Ends, Fill, Assemble
//
// Role: CORE (pure: no I/O, no clock, no engine)
//
// Places the lyric words on the audio. Ported from the lab prototype (`aligner.py`, `refine.py`, `to_lrc.py`), where it
// was measured on 8 songs (plan §1). The model gives, every 20 ms, a log-probability per letter; this file finds the
// single best monotonic path of the lyric's letters through those frames (CTC forced alignment) and turns it into
// word timings and a word-synced Lyrics.Doc.
//
//   Units          the timed units of a line: words, or for Japanese single characters (no spaces to split on)
//   Graph          one token per letter, a separator between words (none for Japanese); words in parentheses are OPTIONAL (backing vocals
//                  and ad-libs are often faint or missing in the separated vocals): a skip edge jumps over them.
//   OnlineViterbi  the forward pass advances frame by frame and is exact. Tokens are COMMITTED in order; a commit
//                  backtraces from the best state at the newest frame. The worker commits just in time (when the
//                  playhead gets close), so each decision has heard as much of the song as possible. A line-time
//                  prior (when the input is line-synced) keeps identical chorus lines from sliding a bar late; for
//                  Japanese it also bounds a line by how long its text can take to sing (LinePrior), so a line
//                  before an instrumental gap or after a long intro cannot slide seconds into it.
//   Snap         a word that starts after silence moves back to where the vocal level rises.
//   Ends         a word ends where the voice goes quiet after its last letter, else at the next word (EndAtVoice): the
//                path's own end is the last letter's spike, the onset of the sound.
//   Fill/Assemble  a line the model timed (at least half its letters) gets a time for every word: an untimed word is
//                  interpolated between its timed neighbours or to the line's own bounds, never invented for a line
//                  with nothing timed (TrackJob.BuildDoc keeps that line as the provider's). The syllable texts join
//                  back to the line text, which the karaoke wipe measures against.

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
        /// &lt;pad&gt;; "|" separates words. Upper-case vocabularies (the English model) get upper-cased text.
        /// A vocabulary with kana (the Japanese model) is <see cref="Japanese"/>: text is NFKC-normalised (half- and
        /// full-width forms to the vocabulary's), never case-folded, and only kana and Han yield tokens — its few Latin
        /// letters and digits are not trusted (it was trained on Japanese text), so a Latin word in a Japanese lyric is
        /// untimed and interpolated between its timed neighbours (Fill). A second pass with a Latin model over those
        /// words would go in TrackJob; it is not built.</summary>
        public sealed class Vocab
        {
            readonly Dictionary<string, int> _ids;
            public readonly int Blank, Separator, Classes;
            public readonly bool Upper, Japanese;

            public Vocab(Dictionary<string, int> ids)
            {
                _ids = ids;
                Blank = ids.TryGetValue("<pad>", out int b) || ids.TryGetValue("[PAD]", out b) ? b : 0;   // [PAD]: the Korean model's blank (1204)
                Separator = ids["|"];
                Classes = ids.Values.Max() + 1;
                Upper = ids.ContainsKey("E");
                Japanese = ids.ContainsKey("あ");
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
                foreach (char c in Prepare(word)) if (TryId(c, out int id)) into.Add(id);
            }

            /// <summary>Whether every LETTER of a word yields a token (<see cref="Tokens"/> drops the ones that do not:
            /// that word is then timed only in part, or not at all). <paramref name="hasLetter"/>: the word has any.</summary>
            public bool Spells(string word, out bool hasLetter)
            {
                hasLetter = false;
                bool all = true;
                foreach (char c in Prepare(word))
                {
                    if (!char.IsLetter(c)) continue;
                    hasLetter = true;
                    if (!TryId(c, out _)) all = false;
                }
                return all;
            }

            // NFC first: lyrics can carry decomposed Hangul (jamo) or decomposed accents; the vocabularies are composed.
            // Japanese: NFKC (half-width katakana and their voicing marks compose to the full-width kana), no case.
            string Prepare(string word)
                => Japanese
                    ? word.Normalize(System.Text.NormalizationForm.FormKC)
                    : (Upper ? word.ToUpperInvariant() : word.ToLowerInvariant()).Replace('’', '\'').Normalize(System.Text.NormalizationForm.FormC);

            /// <summary>A character's class id: itself, else without its accent (a Hangul syllable without its final
            /// consonant; a voiced kana the model lacks, ぢ → ち, without its voicing mark); false when the model has
            /// neither, for the word separator, and (Japanese) for anything but kana and Han.</summary>
            bool TryId(char c, out int id)
            {
                id = 0;
                if (Japanese && !IsKana(c) && !IsHan(c)) return false;
                string ch = c.ToString();
                if (!_ids.TryGetValue(ch, out id))
                {
                    string bare = c is >= '가' and <= '힣' ? WithoutFinal(c) : StripAccent(ch);
                    if (!_ids.TryGetValue(bare, out id)) return false;
                    ch = bare;
                }
                return ch != "|";
            }

            /// <summary>A Hangul syllable without its final consonant (랗 → 라, 꽉 → 꽈): the Korean model knows 1,202 of
            /// the 11,172 syllables, and the open syllable is the nearest sound it has.</summary>
            public static string WithoutFinal(char syllable)
            {
                int index = syllable - '가';
                return ((char)('가' + index - index % 28)).ToString();
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

        // ── the timed units of a line ───────────────────────────────────────────────────────────────────────────────

        /// <summary>Whether a language is timed character by character (<see cref="Characters"/>) and aligned without
        /// the word separator (<see cref="Graph.Build"/>): Japanese, which is written without spaces (a whole phrase is
        /// one regex "word") and whose model was trained on text without "|".</summary>
        public static bool IsCharacterTimed(string? language) => language == "ja";

        // ── the line-time prior ─────────────────────────────────────────────────────────────────────────────────────

        /// <summary>A language's own shape of the line-time prior (<see cref="LineWindows"/>). A line's window ends at
        /// the next line's start, or <see cref="CapSeconds"/> + <see cref="CapSecondsPerToken"/> per token after its
        /// own start if that is sooner; and the blanks before the first line and after the last are free (an intro or an
        /// outro costs nothing, so the first and last lines are not pulled into them).</summary>
        public readonly record struct LinePrior(double CapSeconds, double CapSecondsPerToken);

        /// <summary>The sung-length prior of <paramref name="language"/>, or null for the plain one (a line's window runs
        /// to the next line's start). Japanese only: measured with the offline harness on 34 Japanese songs against
        /// human per-character timing (12,386 scored characters), it moved the p90 start error from 735 to 587 ms and the
        /// lines more than 1 s off from 58 to 29; 1 s + 0.8 s per token (+ the pad) still covers where the last character
        /// starts in all but 13 of the 1,145 human-timed lines, and only 3 lines got worse by more than 300 ms. English, Dutch and Korean have no audio to measure it on, so they keep the plain prior
        /// (their alignment is unchanged).</summary>
        /// <remarks>Kept apart from the word-end cap (<see cref="EndRuleOf"/>, Japanese 1.3 s per character) on purpose:
        /// this prior bounds where a line's tokens may be PLACED and was measured on starts (changing it moves starts); the
        /// end cap bounds how long a placed unit LASTS and was measured on human ends. Neither reads the other.</remarks>
        public static LinePrior? LinePriorOf(string? language) => language == "ja" ? new LinePrior(1.0, 0.8) : null;

        /// <summary>Per token of <paramref name="g"/>: the time range (s) its line is expected in, for a line-synced
        /// source with these line starts. A line's range is [its start - pad, the next line's start + pad] (after the
        /// last line: 30 s), its upper end bounded by the language's <see cref="LinePriorOf"/> when it has one; a
        /// separator takes the range of the token before it.</summary>
        public static (double Lo, double Hi)[] LineWindows(Graph g, IReadOnlyList<long> lineStartsMs, string? language, double padSeconds)
        {
            int n = lineStartsMs.Count;
            var w = new (double Lo, double Hi)[g.Length];
            if (n == 0) return w;
            var starts = new double[n + 1];
            for (int i = 0; i < n; i++) starts[i] = lineStartsMs[i] / 1000.0;
            starts[n] = starts[n - 1] + 30;                                    // after the last line: open-ended
            var hi = new double[n];
            for (int i = 0; i < n; i++) hi[i] = starts[i + 1];
            if (LinePriorOf(language) is { } prior)
            {
                var tokens = new int[n];
                for (int k = 0; k < g.Length; k++) if (g.OwnerLine[k] >= 0) tokens[g.OwnerLine[k]]++;
                for (int i = 0; i < n; i++)
                    if (tokens[i] > 0) hi[i] = Math.Min(hi[i], starts[i] + prior.CapSeconds + prior.CapSecondsPerToken * tokens[i]);
            }
            for (int k = 0; k < g.Length; k++)
            {
                int li = g.OwnerLine[k];
                if (li < 0) { w[k] = k > 0 ? w[k - 1] : (starts[0] - padSeconds, hi[0] + padSeconds); continue; }
                w[k] = (starts[li] - padSeconds, hi[li] + padSeconds);
            }
            return w;
        }

        /// <summary>The units a line is timed in for <paramref name="language"/>: its <see cref="Words"/>, or for a
        /// character-timed language its <see cref="Characters"/>. Every caller that cuts or replays timing uses this,
        /// so the units of a fresh result and of its replay are the same.</summary>
        public static List<LyricWord> Units(string text, string? language)
            => IsCharacterTimed(language) ? Characters(text) : Words(text);

        /// <summary>Japanese units: each kana and each Han character starts one; a prolonged-sound mark (ー), a small
        /// kana (ゃ, っ, ...) and a combining voicing mark attach to the unit before them (they lengthen or modify that
        /// sound); a run of other word characters (a Latin word, digits) is one unit, as are apostrophes inside it.
        /// Units never span a space or punctuation, and the text between units (spaces, punctuation, symbols) belongs
        /// to the unit before it when the line is cut (<see cref="WordSyncedLine"/>), so the cut texts join back to the
        /// line. Parentheses, ASCII or full-width, make the units inside optional.</summary>
        public static List<LyricWord> Characters(string text)
        {
            var list = new List<LyricWord>();
            int depth = 0, pos = 0;
            foreach (Match m in s_word.Matches(text))
            {
                for (int i = pos; i < m.Index; i++)
                {
                    if (text[i] is '(' or '（') depth++;
                    else if (text[i] is ')' or '）') depth = Math.Max(0, depth - 1);
                }
                pos = m.Index + m.Length;
                bool optional = depth > 0;
                int start = m.Index;
                bool runJa = IsJapaneseUnitChar(text[start]);
                for (int i = m.Index + 1; i < pos; i++)
                {
                    char c = text[i];
                    bool ja = IsJapaneseUnitChar(c);
                    bool joins = ja ? runJa && AttachesToPrevious(c)          // a mark joins the kana before it
                                    : !runJa || c is '\'' or '’';            // a Latin run goes on; an apostrophe joins anything
                    if (joins) continue;
                    list.Add(new LyricWord(text[start..i], start, optional));
                    start = i; runJa = ja;
                }
                list.Add(new LyricWord(text[start..pos], start, optional));
            }
            return list;
        }

        /// <summary>A character that is (part of) a Japanese unit: kana, Han, or a combining voicing mark.</summary>
        static bool IsJapaneseUnitChar(char c) => IsKana(c) || IsHan(c) || c is '゙' or '゚';

        /// <summary>Marks that lengthen or modify the sound before them: ー (and its half-width form), the small kana
        /// (sokuon, small vowels, ya/yu/yo/wa), the combining and half-width voicing marks.</summary>
        static bool AttachesToPrevious(char c) => c is 'ー' or 'ｰ' or '゙' or '゚' or 'ﾞ' or 'ﾟ'
            or 'ぁ' or 'ぃ' or 'ぅ' or 'ぇ' or 'ぉ' or 'っ' or 'ゃ' or 'ゅ' or 'ょ' or 'ゎ'
            or 'ァ' or 'ィ' or 'ゥ' or 'ェ' or 'ォ' or 'ッ' or 'ャ' or 'ュ' or 'ョ' or 'ヮ'
            or (>= 'ㇰ' and <= 'ㇿ') or (>= 'ｧ' and <= 'ｯ');

        /// <summary>Hiragana, katakana (with ー and the phonetic extensions) and half-width katakana.</summary>
        public static bool IsKana(char c) => c is (>= 'ぁ' and <= 'ゟ') or (>= '゠' and <= 'ヿ')
            or (>= 'ㇰ' and <= 'ㇿ') or (>= 'ｦ' and <= 'ﾟ');

        /// <summary>Han ideographs of the BMP (unified, extension A, compatibility) and the marks 々 and 〆.</summary>
        public static bool IsHan(char c) => c is (>= '一' and <= '鿿') or (>= '㐀' and <= '䶿')
            or (>= '豈' and <= '﫿') or '々' or '〆';

        // ── the graph ───────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The lyric as CTC tokens. <see cref="OwnerLine"/>/<see cref="OwnerWord"/> are -1 on separators.
        /// Skip edges (<see cref="SkipSrc"/> -> <see cref="SkipDst"/>, both STATE indices) jump over an optional word
        /// and the separator before it: from the blank before that separator (before its first letter for the first
        /// word) to the blank after its last letter. Consecutive optional words chain (one's target is the next's
        /// source). Without <c>wordSeparator</c> (a character-timed language, <see cref="IsCharacterTimed"/>) no "|"
        /// goes between units: the letters follow each other with only CTC blanks between them, and a skip edge runs
        /// from the blank before an optional unit's first letter to the blank after its last.</summary>
        public sealed class Graph
        {
            public readonly int[] Tokens, OwnerLine, OwnerWord, SkipSrc, SkipDst;
            public int Length => Tokens.Length;

            Graph(int[] tokens, int[] ol, int[] ow, int[] ss, int[] sd) { Tokens = tokens; OwnerLine = ol; OwnerWord = ow; SkipSrc = ss; SkipDst = sd; }

            public static Graph Build(IReadOnlyList<IReadOnlyList<LyricWord>> lines, Vocab vocab, bool wordSeparator = true)
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
                        int sep = -1;
                        if (wordSeparator && tokens.Count > 0) { sep = tokens.Count; tokens.Add(vocab.Separator); ol.Add(-1); ow.Add(-1); }
                        int k0 = tokens.Count;
                        foreach (int t in buf) { tokens.Add(t); ol.Add(li); ow.Add(wi); }
                        int k1 = tokens.Count - 1;
                        // From the blank before the word's own separator (state 2*sep) to the blank after its last letter:
                        // the skipped word takes its separator with it, so the path emits ONE "|" between its neighbours
                        // and skipping costs only the skip cost. A first word has no separator before it.
                        if (words[wi].Optional) { ss.Add(2 * (sep >= 0 ? sep : k0)); sd.Add(2 * (k1 + 1)); }
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
            readonly int _s, _l;
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

            /// <param name="windows">Per token: the time range (s) its line is expected in, or null for no prior.</param>
            /// <param name="freeEdges">The blank before the first token has no lower bound and the blank after the last
            /// token no upper bound (<see cref="LinePrior"/>): an intro or an outro of any length costs nothing.</param>
            public OnlineViterbi(Graph g, int blank, IReadOnlyList<(double Lo, double Hi)>? windows = null,
                double priorPerS = 0.5, double skipCost = 2.0, bool freeEdges = false)
            {
                _l = g.Length; _s = 2 * _l + 1;
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
                    if (freeEdges) { _winLo[0] = double.NegativeInfinity; _winHi[_s - 1] = double.PositiveInfinity; }
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

        // ── word ends ───────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>A language's word-end rule (<see cref="EndAtVoice"/>). <see cref="CapSecondsPerUnit"/>: the longest a
        /// word lasts per sung unit (<see cref="SungUnits"/>) when the voice never goes quiet before the next word;
        /// <see cref="QuietFrames"/> 10 ms envelope frames in a row below the silence threshold, or <see cref="DropDb"/>
        /// under the loudest level since the word's start, end it there.</summary>
        public readonly record struct EndRule(double CapSecondsPerUnit, int QuietFrames, float DropDb);

        /// <summary>The word-end rule of <paramref name="language"/>. The cap is the language's 95th percentile of the human
        /// duration per sung unit (provider word-synced lyrics in the lyric cache): Korean 937 ms per syllable block
        /// (19 songs, 3,195 words), Japanese 1,300 ms per character (39 songs, 15,343 units), English 739 ms per vowel group
        /// (8 songs, 2,301 words; Dutch has no human word timing and shares the Latin-script cap), rounded. The quiet test (12
        /// frames, 15 dB under the word's peak) was chosen on the offline harness (34 Japanese songs, audio): it moved the
        /// median end error of units before a human pause from +180 ms (next start only) to +70 ms.</summary>
        public static EndRule EndRuleOf(string? language) => language switch
        {
            "ko" => new EndRule(0.95, 12, 15f),
            "ja" => new EndRule(1.30, 12, 15f),
            _ => new EndRule(0.75, 12, 15f),
        };

        /// <summary>The sung units of a word the end cap counts: Hangul syllable blocks for Korean, one per character unit
        /// for a character-timed language (<see cref="IsCharacterTimed"/>), else vowel groups (a run of vowels is one); at
        /// least 1.</summary>
        public static int SungUnits(string word, string? language)
        {
            if (IsCharacterTimed(language)) return 1;
            int n = 0;
            if (language == "ko")
            {
                foreach (char c in word) if (c is >= '가' and <= '힣') n++;
                if (n > 0) return n;
            }
            bool prev = false;
            foreach (char c in word)
            {
                bool v = IsVowel(c);
                if (v && !prev) n++;
                prev = v;
            }
            return Math.Max(1, n);
        }

        static bool IsVowel(char c) => "aeiouyàáâäèéêëìíîïòóôöùúûüAEIOUYÀÁÂÄÈÉÊËÌÍÎÏÒÓÔÖÙÚÛÜ".Contains(c);

        /// <summary>Word ends that follow the voice. The CTC path puts a word's end at its last letter's spike, the onset
        /// of the sound, never its end: a one-syllable word lasted 20 ms and the wipe then stood still until the next word
        /// (Korean: a fifth of all words). A word now ends where the separated vocals go quiet after its last token
        /// (<see cref="EndRule.QuietFrames"/> frames below <paramref name="quiet"/> or <see cref="EndRule.DropDb"/> under
        /// the word's own peak), else at the next word's start, and never more than
        /// <see cref="EndRule.CapSecondsPerUnit"/> per <see cref="SungUnits"/> after its own start. The next word's start
        /// bounds it (nothing overlaps or reorders), and the line's last word ends no later than
        /// <paramref name="boundS"/> (the next line's start). The bounds limit the extension only: a word never ends
        /// before its last token. Starts are never moved. <paramref name="env"/>: the level in dB per 10 ms frame, as
        /// <see cref="SnapToOnsets"/>; only its first <paramref name="envFrames"/> frames are known.</summary>
        public static void EndAtVoice(WordTime[] words, IReadOnlyList<LyricWord> units, double? boundS, Func<int, float> env, int envFrames,
            float quiet, EndRule rule, string? language)
        {
            const double hop = 0.01;
            int n = words.Length;
            for (int i = 0; i < n; i++)
            {
                var w = words[i];
                if (!w.IsSet) continue;
                double s = w.Start, e = w.End;
                double? nb = i + 1 < n ? (words[i + 1].IsSet ? words[i + 1].Start : null) : boundS;
                double hi = Math.Max(e, s + rule.CapSecondsPerUnit * SungUnits(units[i].Text, language));
                if (nb is double b) hi = Math.Min(hi, b);
                hi = Math.Max(hi, e);
                double end = hi;
                int f0 = (int)(e / hop), f1 = Math.Min((int)(hi / hop), envFrames - 1), run = 0;
                float peak = float.MinValue;
                for (int f = (int)(s / hop); f <= Math.Min(f0, envFrames - 1); f++) peak = Math.Max(peak, env(f));
                for (int f = f0; f <= f1; f++)
                {
                    float lv = env(f);
                    peak = Math.Max(peak, lv);
                    if (lv < quiet || (rule.DropDb > 0 && lv < peak - rule.DropDb))
                    {
                        if (++run >= rule.QuietFrames) { end = (f - rule.QuietFrames + 1) * hop; break; }
                    }
                    else run = 0;
                }
                words[i] = new WordTime(s, Math.Max(end, e));
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
                else if (left is null) { r = right!.Value; l = Math.Min(r, Math.Max(lineStart, r - 0.3 * run)); }   // not before the line
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
        /// at word starts, so they join back to <see cref="Lyrics.Line.Text"/> exactly (the wipe measures against it).
        /// No word starts before <paramref name="prevStartMs"/> (the previous line's start): line starts stay in order,
        /// which the line resolve's binary search relies on. <paramref name="keepLineStart"/> (a line-synced source):
        /// the line keeps the provider's start unless its first word starts earlier (the provider's line times are
        /// right far more often than a word the aligner placed late); otherwise it starts with its first word.</summary>
        public static Lyrics.Line WordSyncedLine(Lyrics.Line src, IReadOnlyList<LyricWord> words, WordTime[] times, double? nextStartS,
            long? prevStartMs = null, bool keepLineStart = false)
        {
            string text = src.Text;
            var syl = new List<Lyrics.Syllable>(words.Count);
            for (int i = 0; i < words.Count; i++)
            {
                int from = i == 0 ? 0 : words[i].Index;
                int to = i + 1 < words.Count ? words[i + 1].Index : text.Length;
                long s = (long)Math.Round(times[i].Start * 1000), e = (long)Math.Round(times[i].End * 1000);
                if (prevStartMs is long ps) s = Math.Max(s, ps);
                if (i + 1 == words.Count && nextStartS is double ns) e = Math.Min(e, (long)Math.Round(ns * 1000));
                e = Math.Max(e, s);
                syl.Add(new Lyrics.Syllable(s, e, text.Substring(from, to - from)));
            }
            long start = keepLineStart ? Math.Min(src.StartMs, syl[0].StartMs) : syl[0].StartMs;
            if (prevStartMs is long p) start = Math.Max(start, p);
            return src with
            {
                StartMs = start,
                Syllables = syl,
                EndMs = src.EndMs ?? syl[^1].EndMs,
                IsWordByWord = true,
            };
        }
    }
}
