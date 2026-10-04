// ── Shell/Verse.cs ─────────────────────────────────────────────────────────────────────────────────────────────────
// Verse.Words (syllables → words), Song (the per-song tables), Memory (ordinals, top words), Sections (chorus detection,
// echo), Emphasis (size/weight per word), Timing (word / line / static, density), Flow (rows from measured widths), Land /
// Hold / Echo / CountIn envelopes, Coupling (the audio bounds), Lanes (motion demand), Geometry (per aspect class)
//
// Role: CORE
// Owner: K
// Wave: 7
// Budget: 800 lines
// Spec: verse-plan.md §3.1 (the v1 pick), §4 (layout), §5 (architecture), §6 (edge cases)
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// EVERY VERSE DECISION, AND NOTHING THAT PAINTS. Verse is the lyrics-driven face: words land when they are sung, held
// words are set larger before they are sung, the song's memory stacks above the live line, a returning chorus lights the
// place it was sung before, and in the silences the song's own vocabulary takes the stage. This file decides all of that
// from the lyric document; `Verse.UI.cs` lays out and drives nodes.
//
// THREE THINGS THIS FILE KEEPS TRUE:
//
//   1. NEVER LIE ABOUT TIMING. A line with word timing lands word by word; a line with line timing lands WHOLE at its
//      start (`Timing.LineMode`), and an unsynced document is a still poem block. The lyrics surface refuses to fake per-
//      word timing (Lyrics.cs AdvancePastInterlude) and so does this one.
//   2. ONE DEFINITION. The sung fraction inside a word is `Lyrics.Wipe.ComputeSplit` over a synthetic per-word line built
//      once per song; the held-note envelope is `Lyrics.Wipe.HeldSyllableGlow` over the same line; the line resolve and
//      the break are `Lyrics.ResolveLine` / `AdvancePastInterlude`. Nothing here re-derives a lyric rule.
//   3. PER SONG, PER LINE, PER FRAME. `Song.Build` allocates (once per document — the sanctioned exception, like the lyrics
//      pipeline); `Flow.Layout` and `Emphasis.Line` run per line into caller spans; every envelope (`Land`, `Hold`,
//      `Echo`, `CountIn`, `Coupling`) is a scalar function the per-frame driver calls allocation-free.
//
// Rules: `System`-only apart from the lyric model records (`Lyrics.Line/Doc/Syllable`) and the stage allocator
// (`Stage.Layout`) it reads — no Element, no signal, no entity read — so Wavee.Tests drives the real arithmetic.

using System.Text;

namespace Wavee;

public static partial class Verse
{
    // ── 1. words ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One word of a line. <see cref="Timed"/> is the synthetic one-word line (the word's own syllables) that
    /// <c>Lyrics.Wipe.ComputeSplit</c> and <c>HeldSyllableGlow</c> read — null for a line-timed word, which has no syllables.</summary>
    /// <param name="FirstSyl">The word's first syllable in the line (−1 for a line-timed word).</param>
    /// <param name="HeldMs">The longest syllable the word holds, overrun-trimmed (<see cref="Words.HeldSpan"/>).</param>
    /// <param name="Token">The word's id in <see cref="Song.Tokens"/>, or −1 (too short, a stop word).</param>
    /// <param name="Ordinal">How many times the song has sung this token up to and including this word (0 = uncounted).</param>
    public readonly record struct Word(
        string Text, long StartMs, long EndMs, int FirstSyl, int SylCount, long HeldMs, int Token, int Ordinal, Lyrics.Line? Timed);

    public static class Words
    {
        /// <summary>A syllable whose end runs this far past the next one's start is a provider overrun (some QRC/KRC docs), not
        /// a held note: its span is trimmed to the next start (verse-plan risk 5).</summary>
        public const long OverrunMs = 50L;

        /// <summary>The words of one line. Word timing groups syllables by the line's own text: a new word begins where the
        /// line text has whitespace between two syllables, where a syllable carries its own space, or across a CJK ideograph or
        /// kana (CJK providers time per character, so each provider syllable is a word). Line timing splits on whitespace and
        /// every word shares the line's start.</summary>
        public static Word[] Build(Lyrics.Line line)
        {
            var syl = line.Syllables;
            if (!(line.IsWordByWord && syl.Count > 0)) return Plain(line);
            string text = line.Text;
            var words = new List<Word>(Math.Min(syl.Count, 32));
            var core = new StringBuilder();
            int cursor = 0, first = -1, count = 0, textFrom = -1, textTo = -1;
            long start = 0L, end = 0L, held = 0L;
            bool aligned = true, pendingBreak = false;
            char prevLast = '\0';
            for (int i = 0; i < syl.Count; i++)
            {
                string raw = syl[i].Text;
                string c = raw.Trim();
                if (c.Length == 0) { pendingBreak = true; continue; }   // a pure-space syllable is a boundary, never a word
                int idx = cursor <= text.Length ? text.IndexOf(c, cursor, StringComparison.Ordinal) : -1;
                bool gapSpace = false;
                if (idx >= 0)
                    for (int k = cursor; k < idx; k++)
                        if (char.IsWhiteSpace(text[k])) { gapSpace = true; break; }
                bool boundary = pendingBreak || char.IsWhiteSpace(raw[0]) || gapSpace || BreaksPerSyllable(prevLast) || BreaksPerSyllable(c[0]);
                if (boundary && count > 0) { words.Add(Make()); count = 0; }
                if (count == 0) { first = i; start = syl[i].StartMs; textFrom = idx; aligned = idx >= 0; core.Clear(); held = 0L; }
                count++;
                end = syl[i].EndMs;
                long span = HeldSpan(syl, i);
                if (span > held) held = span;
                core.Append(c);
                if (idx >= 0) { textTo = idx + c.Length; cursor = textTo; }
                else aligned = false;
                pendingBreak = char.IsWhiteSpace(raw[^1]);
                prevLast = c[^1];
            }
            if (count > 0) words.Add(Make());
            return words.ToArray();

            Word Make()
            {
                string w = aligned && textFrom >= 0 && textTo > textFrom ? text[textFrom..textTo] : core.ToString();
                var slice = new Lyrics.Syllable[count];
                for (int k = 0; k < count; k++) slice[k] = syl[first + k];
                var timed = new Lyrics.Line(start, w, slice, end, IsWordByWord: true);
                return new Word(w, start, end, first, count, held, -1, 0, timed);
            }
        }

        static Word[] Plain(Lyrics.Line line)
        {
            var parts = line.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            long end = line.EndMs ?? line.StartMs;
            var words = new Word[parts.Length];
            for (int i = 0; i < parts.Length; i++) words[i] = new Word(parts[i], line.StartMs, end, -1, 0, 0L, -1, 0, null);
            return words;
        }

        /// <summary>A syllable's held span: its own duration, trimmed to the next syllable's start when it overruns it by more
        /// than <see cref="OverrunMs"/> (a provider overlap is not a held note).</summary>
        public static long HeldSpan(IReadOnlyList<Lyrics.Syllable> syl, int i)
        {
            var s = syl[i];
            long e = s.EndMs;
            if (i + 1 < syl.Count && e > syl[i + 1].StartMs + OverrunMs) e = syl[i + 1].StartMs;
            return Math.Max(0L, e - s.StartMs);
        }

        /// <summary>The sung fraction of a word at <paramref name="nowMs"/> — the lyrics wipe's char-weighted split over the
        /// word's own syllables; a line-timed word is all-or-nothing at its start.</summary>
        public static float Split(in Word w, long nowMs)
            => w.Timed is { } l ? Lyrics.Wipe.ComputeSplit(l, nowMs) : nowMs >= w.StartMs ? 1f : 0f;

        /// <summary>Han ideographs and kana: a CJK provider times per character and the script has no spaces, so every
        /// provider syllable is its own word. Hangul is spaced and groups by the spaces like Latin.</summary>
        public static bool BreaksPerSyllable(char c)
            => c is (>= '぀' and <= 'ヿ') or (>= '㐀' and <= '䶿') or (>= '一' and <= '鿿')
                 or (>= '豈' and <= '﫿') or (>= 'ｦ' and <= 'ﾟ');

        /// <summary>Thai, Lao, Khmer, Myanmar: no spaces and no reliable provider word boundaries — such a line lands whole.</summary>
        public static bool HasUnbrokenScript(string text)
        {
            foreach (char c in text)
                if (c is (>= '฀' and <= '໿') or (>= 'ក' and <= '៿') or (>= 'က' and <= '႟')) return true;
            return false;
        }

        /// <summary>The line's reading direction from its first strong character (Hebrew / Arabic ⇒ right to left).</summary>
        public static bool IsRtl(string text)
        {
            foreach (char c in text)
            {
                if (c is (>= '֐' and <= 'ࣿ') or (>= 'יִ' and <= '﷿') or (>= 'ﹰ' and <= '﻿')) return true;
                if (char.IsLetter(c)) return false;
            }
            return false;
        }
    }

    // ── 2. the per-song tables ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Everything Verse knows about one document, built ONCE per document identity (an upgrade held to the next
    /// hand-off rebuilds it there). Allocates by design; nothing per line or per frame does.</summary>
    public sealed class Song
    {
        public readonly Lyrics.Doc Doc;
        public readonly TimingMode Mode;
        /// <summary>The words of every line, parallel to <c>Doc.Lines</c>; ordinals assigned across the whole song.</summary>
        public readonly Word[][] Words;
        public readonly SectionMap Sections;
        /// <summary>The counted vocabulary in order of first appearance (<see cref="Word.Token"/> indexes it).</summary>
        public readonly string[] Tokens;
        internal readonly int[] Counts;   // TopWordsAt's scratch, one slot per token

        Song(Lyrics.Doc doc, TimingMode mode, Word[][] words, SectionMap sections, string[] tokens)
        {
            Doc = doc; Mode = mode; Words = words; Sections = sections; Tokens = tokens;
            Counts = new int[tokens.Length];
        }

        public static Song Build(Lyrics.Doc doc)
        {
            var lines = doc.Lines;
            var words = new Word[lines.Count][];
            for (int i = 0; i < lines.Count; i++) words[i] = Verse.Words.Build(lines[i]);
            var tokens = Memory.Assign(words);
            return new Song(doc, Timing.Mode(doc), words, Verse.Sections.Detect(lines), tokens);
        }
    }

    // ── 3. memory: ordinals and the most-sung words ─────────────────────────────────────────────────────────────────

    public static class Memory
    {
        public const int MinTokenLength = 3;

        /// <summary>Function words and vocables that would otherwise win every count. Small on purpose: the cloud and the
        /// repetition growth should surface the song's own words, not grammar.</summary>
        static readonly HashSet<string> s_stop = new(StringComparer.Ordinal)
        {
            "the", "and", "you", "your", "yours", "but", "for", "with", "that", "this", "these", "those", "are", "was", "were",
            "have", "has", "had", "not", "all", "can", "will", "just", "what", "when", "where", "who", "why", "how", "its",
            "it's", "i'm", "you're", "we're", "they're", "don't", "can't", "won't", "ain't", "i'll", "i've", "she", "her", "his",
            "him", "our", "out", "get", "got", "from", "into", "there", "then", "than", "they", "them", "too", "let", "let's",
            "ooh", "yeah", "woah", "whoa", "hey",
        };

        public static bool IsStop(string token) => s_stop.Contains(token);

        /// <summary>The counted form of a word: lower-case letters, digits and inner apostrophes; null when it is shorter than
        /// <see cref="MinTokenLength"/> (two for CJK) or a stop word.</summary>
        public static string? Normalise(string word)
        {
            var sb = new StringBuilder(word.Length);
            bool cjk = false;
            foreach (char ch in word)
            {
                char c = ch == '’' ? '\'' : ch;
                if (char.IsLetterOrDigit(c)) { sb.Append(char.ToLowerInvariant(c)); cjk |= Words.BreaksPerSyllable(c); }
                else if (c == '\'' && sb.Length > 0) sb.Append('\'');
            }
            while (sb.Length > 0 && sb[^1] == '\'') sb.Length--;
            if (sb.Length < (cjk ? 2 : MinTokenLength)) return null;
            string t = sb.ToString();
            return IsStop(t) ? null : t;
        }

        /// <summary>Assign every word its token and running ordinal, song order. Returns the vocabulary.</summary>
        public static string[] Assign(Word[][] words)
        {
            var ids = new Dictionary<string, int>(StringComparer.Ordinal);
            var tokens = new List<string>();
            var counts = new List<int>();
            for (int l = 0; l < words.Length; l++)
            {
                var line = words[l];
                for (int i = 0; i < line.Length; i++)
                {
                    string? t = Normalise(line[i].Text);
                    if (t is null) continue;
                    if (!ids.TryGetValue(t, out int id)) { id = tokens.Count; ids.Add(t, id); tokens.Add(t); counts.Add(0); }
                    int n = counts[id] + 1;
                    counts[id] = n;
                    line[i] = line[i] with { Token = id, Ordinal = n };
                }
            }
            return tokens.ToArray();
        }

        /// <summary>The most-sung tokens among the words that started at or before <paramref name="nowMs"/>, by count
        /// (ties: first sung first), written into the two spans; returns how many were written. Runs per break, never per
        /// frame; allocation-free over the song's scratch.</summary>
        public static int TopWordsAt(Song song, long nowMs, Span<int> tokens, Span<int> counts)
        {
            var c = song.Counts;
            Array.Clear(c);
            var words = song.Words;
            for (int l = 0; l < words.Length; l++)
            {
                var line = words[l];
                if (line.Length > 0 && line[0].StartMs > nowMs) break;
                for (int i = 0; i < line.Length; i++)
                {
                    if (line[i].StartMs > nowMs) break;
                    if (line[i].Token >= 0) c[line[i].Token]++;
                }
            }
            int k = Math.Min(tokens.Length, counts.Length), n = 0;
            for (; n < k; n++)
            {
                int best = -1;
                for (int t = 0; t < c.Length; t++)
                    if (c[t] > 0 && (best < 0 || c[t] > c[best])) best = t;
                if (best < 0) break;
                tokens[n] = best;
                counts[n] = c[best];
                c[best] = 0;
            }
            return n;
        }
    }

    // ── 4. sections: the chorus knows it has been sung ──────────────────────────────────────────────────────────────

    public enum Part : byte { Verse, Chorus, Bridge }

    /// <summary>Per-line structure, parallel to the document's lines.</summary>
    /// <param name="Keys">The normalised-text id per line; a negative id is unique (an empty line never matches).</param>
    /// <param name="Group">The repeated block a chorus line belongs to (−1 = none).</param>
    /// <param name="Instance">Which occurrence of its block the line is in, 1-based (0 = not a chorus).</param>
    /// <param name="EchoOf">For a chorus line in a later occurrence, the same line of the FIRST occurrence; else −1.</param>
    public sealed record SectionMap(Part[] Parts, int[] Keys, int[] Group, int[] Instance, int[] EchoOf)
    {
        /// <summary>The first line of a chorus occurrence — the moment Verse "knows".</summary>
        public bool IsChorusEntry(int i)
            => (uint)i < (uint)Parts.Length && Parts[i] == Part.Chorus
               && (i == 0 || Group[i - 1] != Group[i] || Instance[i - 1] != Instance[i]);
    }

    public static class Sections
    {
        /// <summary>A single line repeated this often is a hook even without a second line around it.</summary>
        public const int SingleLineRepeats = 4;
        /// <summary>A non-repeating run this long between the second and the last chorus is a bridge.</summary>
        public const int BridgeMinLines = 2;
        /// <summary>Past this many lines (a transcript, not a song) the cubic block search is skipped: every line is verse.</summary>
        public const int MaxLines = 400;

        static readonly HashSet<string> s_fillers = new(StringComparer.Ordinal) { "oh", "ooh", "yeah", "ah", "hey", "woah", "whoa", "uh", "mm", "na", "la" };

        /// <summary>The comparison form of a line: lower-case letters and digits, parenthesised asides dropped, whitespace
        /// collapsed, trailing vocables ("…, oh yeah") stripped — unless the whole line is vocables.</summary>
        public static string Normalise(string line)
        {
            var sb = new StringBuilder(line.Length);
            int depth = 0;
            foreach (char ch in line)
            {
                if (ch is '(' or '[') { depth++; continue; }
                if (ch is ')' or ']') { depth = Math.Max(0, depth - 1); continue; }
                if (depth > 0) continue;
                if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
                else if ((char.IsWhiteSpace(ch) || ch == '-') && sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
            }
            var tokens = sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int n = tokens.Length;
            while (n > 0 && s_fillers.Contains(tokens[n - 1])) n--;
            if (n == 0) n = tokens.Length;
            return string.Join(' ', tokens, 0, n);
        }

        /// <summary>Repeated contiguous blocks of ≥ 2 lines that occur ≥ 2 times are choruses (greedy, longest first, never
        /// overlapping); a single line repeated <see cref="SingleLineRepeats"/> times is one too. A non-repeating run of
        /// ≥ <see cref="BridgeMinLines"/> after the second chorus begins and before the last chorus is a bridge; everything
        /// else is verse.</summary>
        public static SectionMap Detect(IReadOnlyList<Lyrics.Line> lines)
        {
            int n = lines.Count;
            var keys = new int[n];
            var ids = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < n; i++)
            {
                string k = Normalise(lines[i].Text);
                if (k.Length == 0) { keys[i] = -(i + 1); continue; }
                if (!ids.TryGetValue(k, out int id)) { id = ids.Count; ids.Add(k, id); }
                keys[i] = id;
            }
            var parts = new Part[n];
            var group = new int[n];
            var instance = new int[n];
            var echo = new int[n];
            Array.Fill(group, -1);
            Array.Fill(echo, -1);
            var starts = new List<int>();
            int groups = 0;
            for (int len = n <= MaxLines ? n / 2 : 0; len >= 1; len--)
            {
                int need = len == 1 ? SingleLineRepeats : 2;
                for (int i = 0; i + len <= n; i++)
                {
                    if (!Free(i, len)) continue;
                    starts.Clear();
                    starts.Add(i);
                    for (int j = i + len; j + len <= n; j++)
                        if (Free(j, len) && Same(i, j, len)) { starts.Add(j); j += len - 1; }
                    if (starts.Count < need) continue;
                    for (int s = 0; s < starts.Count; s++)
                        for (int o = 0; o < len; o++)
                        {
                            int at = starts[s] + o;
                            parts[at] = Part.Chorus; group[at] = groups; instance[at] = s + 1;
                            echo[at] = s == 0 ? -1 : i + o;
                        }
                    groups++;
                }
            }
            // the bridge: after the second chorus occurrence begins, before the last one
            int second = int.MaxValue, last = -1;
            for (int i = 0; i < n; i++)
            {
                if (parts[i] != Part.Chorus) continue;
                if (instance[i] == 2 && (i == 0 || instance[i - 1] != 2 || group[i - 1] != group[i])) second = Math.Min(second, i);
                if (i == 0 || group[i - 1] != group[i] || instance[i - 1] != instance[i]) last = Math.Max(last, i);
            }
            for (int i = 0; i < n;)
            {
                if (parts[i] != Part.Verse || keys[i] < 0) { i++; continue; }
                int j = i;
                while (j < n && parts[j] == Part.Verse && keys[j] >= 0) j++;
                if (j - i >= BridgeMinLines && i > second && j <= last)
                    for (int k = i; k < j; k++) parts[k] = Part.Bridge;
                i = j;
            }
            return new SectionMap(parts, keys, group, instance, echo);

            bool Free(int at, int len)
            {
                for (int k = at; k < at + len; k++) if (group[k] >= 0 || keys[k] < 0) return false;
                return true;
            }
            bool Same(int a, int b, int len)
            {
                for (int k = 0; k < len; k++) if (keys[a + k] != keys[b + k]) return false;
                return true;
            }
        }

        /// <summary>The ghost to light for the live line: the depth (1..<paramref name="depth"/>) of the most recent earlier
        /// line in the visible stack with the SAME normalised text, or 0. Conservative on purpose — a lit ghost is pleasant
        /// even when the guess is wrong; a re-laid-out section is not.</summary>
        public static int EchoGhost(SectionMap map, int line, int depth)
        {
            if ((uint)line >= (uint)map.Keys.Length) return 0;
            int key = map.Keys[line];
            if (key < 0) return 0;
            for (int d = 1; d <= depth; d++)
            {
                int j = line - d;
                if (j < 0) break;
                if (map.Keys[j] == key) return d;
            }
            return 0;
        }
    }

    // ── 5. emphasis: the line's shape is decided before it is sung ──────────────────────────────────────────────────

    /// <summary>One word's typographic role: its size over the line's base, its weight, and whether it is a held word
    /// (set larger, swells and blooms while held).</summary>
    public readonly record struct Role(float Scale, ushort Weight, bool Held);

    public static class Emphasis
    {
        /// <summary>A syllable held at least this long is a held note — the lyrics bloom's own threshold.</summary>
        public const float HeldMinMs = Lyrics.Wipe.HeldGlowMinMs;
        public const float HeldScale = 1.3f, HeldScalePortrait = 1.25f;
        public const ushort Weight = 600, HeldWeight = 700;
        /// <summary>Repetition: +7 % per earlier occurrence of the word, capped at +40 %.</summary>
        public const float RepeatStep = 0.07f, RepeatCap = 0.40f;
        /// <summary>A slow ballad holds most words; only the two longest of a line read as held.</summary>
        public const int MaxHeldPerLine = 2;

        public static float Repetition(int ordinal) => ordinal <= 1 ? 0f : MathF.Min(RepeatCap, RepeatStep * (ordinal - 1));

        /// <summary>One word's role. A dense line is neutral (every word 1.0 / 600, nothing held).</summary>
        public static Role For(in Word w, bool dense, bool heldAllowed, float heldScale)
        {
            if (dense) return new Role(1f, Weight, false);
            bool held = heldAllowed && w.HeldMs >= HeldMinMs;
            return new Role((held ? heldScale : 1f) + Repetition(w.Ordinal), held ? HeldWeight : Weight, held);
        }

        /// <summary>Every word's role for one line, into <paramref name="roles"/>: held only for the
        /// <see cref="MaxHeldPerLine"/> longest held words.</summary>
        public static void Line(ReadOnlySpan<Word> words, bool dense, float heldScale, Span<Role> roles)
        {
            int a = -1, b = -1;   // the two longest held candidates
            if (!dense)
                for (int i = 0; i < words.Length; i++)
                {
                    if (words[i].HeldMs < HeldMinMs) continue;
                    if (a < 0 || words[i].HeldMs > words[a].HeldMs) { b = a; a = i; }
                    else if (b < 0 || words[i].HeldMs > words[b].HeldMs) b = i;
                }
            for (int i = 0; i < words.Length && i < roles.Length; i++)
                roles[i] = For(in words[i], dense, i == a || i == b, heldScale);
        }
    }

    // ── 6. timing: word, line or static ─────────────────────────────────────────────────────────────────────────────

    public enum TimingMode : byte { Static, Line, Word }

    public static class Timing
    {
        /// <summary>A line above this many words per second (or with most gaps under <see cref="DenseGapMs"/>) is dense:
        /// alpha-only landing, no held emphasis, no swell — the hairline carries the motion.</summary>
        public const float DenseWordsPerSec = 3.5f;
        public const long DenseGapMs = 120L;
        /// <summary>More words than this and the line is set as ONE wrapped run that lands whole.</summary>
        public const int MaxWords = 16;
        /// <summary>Words this far apart on average make a slow line: the landing lengthens.</summary>
        public const long SlowGapMs = 1500L;

        /// <summary>The document's mode: untimed ⇒ Static; any real word timing ⇒ Word (a transcript's gap fillers are
        /// line-timed rows inside a word-timed document); else Line.</summary>
        public static TimingMode Mode(Lyrics.Doc doc)
        {
            if (!Lyrics.IsTimed(doc) || doc.Lines.Count == 0) return TimingMode.Static;
            return Lyrics.Authority.Richness(doc) >= 3 ? TimingMode.Word : TimingMode.Line;
        }

        /// <summary>One line's mode inside its document: word timing only where the line itself carries it, fits the word
        /// budget and has word boundaries to land on.</summary>
        public static TimingMode LineMode(TimingMode song, Lyrics.Line line, ReadOnlySpan<Word> words)
        {
            if (song == TimingMode.Static) return TimingMode.Static;
            if (song == TimingMode.Line || !(line.IsWordByWord && line.Syllables.Count > 0)) return TimingMode.Line;
            if (words.Length == 0 || words.Length > MaxWords || Words.HasUnbrokenScript(line.Text)) return TimingMode.Line;
            return TimingMode.Word;
        }

        public static bool IsDense(ReadOnlySpan<Word> words)
        {
            if (words.Length < 3) return false;
            long span = words[^1].StartMs - words[0].StartMs;
            if (span <= 0L) return false;
            if ((words.Length - 1) * 1000f / span > DenseWordsPerSec) return true;
            int tight = 0;
            for (int i = 1; i < words.Length; i++) if (words[i].StartMs - words[i - 1].StartMs < DenseGapMs) tight++;
            return tight * 2 > words.Length - 1;
        }

        /// <summary>The landing length for a line: short and alpha-only when dense, longer on a slow line.</summary>
        public static float LandMsFor(ReadOnlySpan<Word> words, bool dense)
        {
            if (dense) return Land.DenseMs;
            if (words.Length >= 2 && (words[^1].StartMs - words[0].StartMs) / (words.Length - 1) >= SlowGapMs) return Land.SlowMs;
            return Land.Ms;
        }
    }

    // ── 7. flow: rows from measured widths ──────────────────────────────────────────────────────────────────────────

    /// <summary>A flowed line: how many rows, the type scale that made it fit, whether it fits at all, and its widest row
    /// (at that scale — the hairline's width).</summary>
    public readonly record struct Flowed(int Rows, float Scale, bool Fits, float WidestRow);

    public static class Flow
    {
        public const int MaxRows = 3;
        /// <summary>The smallest type scale a line may shrink to before it gives up word nodes for one wrapped run.</summary>
        public const float MinScale = 0.6f;
        public const float ScaleStep = 0.05f;

        /// <summary>Greedy rows, never splitting a word: at scale 1 if that fits in <paramref name="maxRows"/> rows of
        /// <paramref name="maxW"/>, else the largest 5 %-step scale down to <see cref="MinScale"/> that does. Writes each word's
        /// row into <paramref name="rowOf"/>. Not fitting even at the floor (a word wider than the column, too many rows)
        /// reports <c>Fits = false</c> with the floor's rows — the caller then sets the line as one wrapped run.
        /// Deterministic: the same widths always give the same rows.</summary>
        public static Flowed Layout(ReadOnlySpan<float> widths, float gap, float maxW, int maxRows, Span<int> rowOf)
        {
            if (widths.Length == 0) return new Flowed(0, 1f, true, 0f);
            for (int step = 0; ; step++)
            {
                float scale = MathF.Max(MinScale, 1f - step * ScaleStep);
                var (rows, widest, fits) = Greedy(widths, gap, maxW, scale, rowOf);
                if (fits && rows <= maxRows) return new Flowed(rows, scale, true, widest);
                if (scale <= MinScale) return new Flowed(rows, scale, false, widest);
            }
        }

        static (int Rows, float Widest, bool Fits) Greedy(ReadOnlySpan<float> widths, float gap, float maxW, float scale, Span<int> rowOf)
        {
            int row = 0;
            float x = 0f, widest = 0f;
            bool fits = true, empty = true;
            for (int i = 0; i < widths.Length; i++)
            {
                float w = widths[i] * scale, g = gap * scale;
                if (w > maxW) fits = false;
                if (!empty && x + g + w > maxW) { widest = MathF.Max(widest, x); row++; x = 0f; empty = true; }
                x += (empty ? 0f : g) + w;
                empty = false;
                if (i < rowOf.Length) rowOf[i] = row;
            }
            widest = MathF.Max(widest, x);
            return (row + 1, widest, fits);
        }
    }

    // ── 8. the envelopes ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The word landing: from faint, 18 DIP above and σ 8 to sharp, in place and opaque, ending EXACTLY on the
    /// word's start. Reduced motion zeroes the drop and the blur (values); the faint→ink step stays, on time.</summary>
    public static class Land
    {
        public const float Ms = 350f, SlowMs = 450f, DenseMs = 120f;
        public const float DropDip = 18f, Sigma = 8f, WaitAlpha = 0.12f;
        /// <summary>At most this many words animate at once; an older one still in flight completes instantly.</summary>
        public const int MaxInFlight = 2;
        /// <summary>The resolved blur strength under which the landing blur is off (a weak GPU's default is 40).</summary>
        public const int BlurMinStrength = 60;

        /// <summary>0 at <c>start − landMs</c>, 1 at <c>start</c>, monotone, clamped.</summary>
        public static float Progress(long nowMs, long startMs, float landMs)
            => landMs <= 0f ? (nowMs >= startMs ? 1f : 0f) : Math.Clamp((nowMs - startMs + landMs) / landMs, 0f, 1f);

        public static float Ease(float p) { float q = 1f - Math.Clamp(p, 0f, 1f); return 1f - q * q * q; }
        public static float Alpha(float p) => p >= 1f ? 1f : WaitAlpha + (1f - WaitAlpha) * Ease(p);
        /// <summary>The vertical offset (negative = above). A waiting word sits in place, a landing word starts high.</summary>
        public static float Drop(float p, bool reduced) => reduced || p <= 0f || p >= 1f ? 0f : -DropDip * (1f - Ease(p));
        /// <summary>σ only while in flight — a waiting word is never its own blur layer.</summary>
        public static float Blur(float p, bool reduced, bool enabled) => !enabled || reduced || p <= 0f || p >= 1f ? 0f : Sigma * (1f - Ease(p));
        /// <summary>The in-flight cap: with <paramref name="newerInFlight"/> newer words already animating, this one lands now.</summary>
        public static float Capped(float p, int newerInFlight) => newerInFlight >= MaxInFlight && p > 0f ? 1f : p;
    }

    /// <summary>The held word: a swell over the note and a bloom under it, from the lyrics bloom envelope.</summary>
    public static class Hold
    {
        public const float SwellMax = 0.05f, BassSwell = 0.03f;
        public const float BloomBase = 0.55f, BloomLevel = 0.35f, PausedBloom = 0.3f;

        /// <summary><c>Lyrics.Wipe.HeldSyllableGlow</c> over the word's own syllables (0 for a line-timed word).</summary>
        public static float Glow(in Word w, long nowMs) => w.Timed is { } l ? Lyrics.Wipe.HeldSyllableGlow(l, nowMs) : 0f;
        public static float Swell(float glow, float low, bool reduced, bool calm)
            => reduced ? 1f : 1f + glow * (SwellMax + BassSwell * Math.Clamp(low, 0f, 1f) * (calm ? Coupling.CalmK : 1f));
        /// <summary>The bloom's layer alpha: the envelope lit by the energy; paused, a held word keeps a quiet 0.3.</summary>
        public static float Bloom(float glow, float level, bool playing)
            => playing ? Math.Clamp(glow * (BloomBase + BloomLevel * Math.Clamp(level, 0f, 1f)), 0f, 1f) : glow > 0f ? PausedBloom : 0f;
    }

    /// <summary>The chorus echo: the earlier ghost lights a beat before the live line lands and fades over two beats.
    /// Reduced motion is a colour STEP over the same window.</summary>
    public static class Echo
    {
        public const float Peak = 0.6f;
        public static float Alpha(long nowMs, long startMs, float beatMs, bool reduced)
        {
            float b = beatMs > 0f ? beatMs : CountIn.FallbackBeatMs;
            float t = nowMs - startMs;
            if (t < -b || t > 2f * b) return 0f;
            if (reduced) return 1f;
            return t < 0f ? 1f + t / b : 1f - t / (2f * b);
        }
        /// <summary>The instant the echo is over (its lane's end).</summary>
        public static long EndMs(long startMs, float beatMs) => startMs + (long)(2f * (beatMs > 0f ? beatMs : CountIn.FallbackBeatMs));
    }

    /// <summary>The count-in: three dots light over the last six beats before the next line, one every two beats — a
    /// drummer's count on the beat grid (the tempo grid stands in; without either, a 500 ms beat).</summary>
    public static class CountIn
    {
        public const int Dots = 3, BeatsPerDot = 2;
        public const float FallbackBeatMs = 500f;

        public static int Lit(long nowMs, long nextStartMs, float beatMs)
        {
            float b = beatMs > 0f ? beatMs : FallbackBeatMs;
            float left = (nextStartMs - nowMs) / b;
            if (left <= 0f) return Dots;
            return Math.Clamp(Dots + 1 - (int)MathF.Ceiling(left / BeatsPerDot), 0, Dots);
        }

        /// <summary>The next media instant <see cref="Lit"/> changes (a dot lights), or <see cref="Lanes.None"/>.</summary>
        public static long NextEdgeMs(long nowMs, long nextStartMs, float beatMs)
        {
            float b = beatMs > 0f ? beatMs : FallbackBeatMs;
            for (int k = Dots; k >= 1; k--)
            {
                long edge = nextStartMs - (long)(k * BeatsPerDot * b);
                if (edge > nowMs) return edge;
            }
            return Lanes.None;
        }
    }

    /// <summary>The music's hold on the type, bounded: the kick nudges the live line ≤ 2 DIP, the bass sinks the ghost
    /// stack ≤ 6 DIP; Calm halves both, reduced motion zeroes both.</summary>
    public static class Coupling
    {
        public const float KickDip = 2f, BassSinkDip = 6f, CalmK = 0.5f;
        public static float Kick(float kick, bool calm, bool reduced) => reduced ? 0f : Math.Clamp(kick, 0f, 1f) * KickDip * (calm ? CalmK : 1f);
        public static float BassSink(float low, bool calm, bool reduced) => reduced ? 0f : Math.Clamp(low, 0f, 1f) * BassSinkDip * (calm ? CalmK : 1f);
    }

    // ── 9. motion demand: the ticker mounts only while a lane moves ─────────────────────────────────────────────────

    /// <summary>One step's lanes, as the driver reports them (the lyrics surface's <c>MotionLanes</c> shape).</summary>
    /// <param name="Voice">A line is being sung: its hairline (and a word's wipe) move every frame.</param>
    /// <param name="Landing">A word (or a line-timed line) is mid-landing.</param>
    /// <param name="Held">A held word's swell/bloom envelope is non-zero.</param>
    /// <param name="Echo">A chorus echo is fading.</param>
    /// <param name="NextEventMs">The next media instant the picture changes on its own (<see cref="Lanes.NextEventMs"/>).</param>
    public readonly record struct LaneState(bool Playing, bool Voice, bool Landing, bool Held, bool Echo, long NowMs, long NextEventMs);

    public static class Lanes
    {
        public const long None = Lyrics.MotionDemand.None;

        public static bool Moving(in LaneState l) => l.Playing && (l.Voice || l.Landing || l.Held || l.Echo);

        /// <summary>Ticks now, or the media instant to wake at (<c>NextEventMs − ArmLeadMs</c>); paused ⇒ neither (the
        /// transport wakes the face).</summary>
        public static Lyrics.MotionDecision Decide(in LaneState l)
        {
            if (Moving(in l)) return new Lyrics.MotionDecision(true, None);
            if (!l.Playing || l.NextEventMs >= None) return new Lyrics.MotionDecision(false, None);
            if (l.NextEventMs - l.NowMs <= Lyrics.MotionDemand.ArmLeadMs) return new Lyrics.MotionDecision(true, None);
            return new Lyrics.MotionDecision(false, l.NextEventMs - Lyrics.MotionDemand.ArmLeadMs);
        }

        /// <summary>The next instant the face changes on its own: a line's hand-off (<c>start − LeadMs</c>, where its first
        /// word is already landing) or start, and inside a break the next count-in dot.</summary>
        public static long NextEventMs(IReadOnlyList<Lyrics.Line> lines, int fromLine, long nowMs, long countInEdgeMs)
            => Math.Min(Lyrics.MotionDemand.NextEventMs(lines, fromLine, nowMs, Lyrics.LeadMs), countInEdgeMs);
    }

    // ── 10. geometry per stage class ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Where Verse composes inside the face box, and how much of it there is room for.</summary>
    /// <param name="ColumnX">The live column's left edge (the line is LEFT-aligned inside it — a ragged right reads better
    /// while words land).</param>
    /// <param name="AnchorY">The live block's vertical centre.</param>
    /// <param name="Base">The live line's type size at scale 1.</param>
    /// <param name="MaxGhosts">The memory depth the class allows (<see cref="Geometry.GhostsThatFit"/> trims to the room).</param>
    /// <param name="GhostTop">Nothing is set above this (the now-playing card's foot while the chrome shows).</param>
    /// <param name="Bottom">Nothing is set below this (the transport's gutter).</param>
    public readonly record struct Region(
        Stage.Aspect Aspect, float ColumnX, float ColumnW, float AnchorY, float Base, float HeldScale,
        int MaxGhosts, float GhostTop, float Bottom, bool ReadAhead, bool Translation, bool Cloud, bool LandBlur)
    {
        /// <summary>A monotone "how much is on screen" score for the narrowing-never-adds test.</summary>
        public int Richness => MaxGhosts + (ReadAhead ? 1 : 0) + (Translation ? 1 : 0) + (Cloud ? 1 : 0) + (LandBlur ? 1 : 0);
    }

    public static class Geometry
    {
        public const float AnchorFrac = 0.56f, ColumnFrac = 0.72f, ColumnMaxW = 1000f, DesktopTypeK = 1.15f;
        /// <summary>The line box over the type size, the gap between ghosts, the live block's air above and below.</summary>
        public const float LineK = 1.25f, GhostGap = 6f, BlockAir = 14f, HairlineH = 3f, BottomGap = 48f;
        /// <summary>The read-ahead and the translation run at half the live size.</summary>
        public const float ReadAheadK = 0.5f;
        /// <summary>A stage this tall keeps a fifth ghost.</summary>
        public const float TallH = 1300f;
        public const int DesktopGhosts = 4, TallGhosts = 5, PortraitGhosts = 3;

        public static float GhostScale(int d) => d switch { 1 => 0.78f, 2 => 0.62f, 3 => 0.52f, 4 => 0.46f, _ => 0.42f };
        /// <summary>Depth by alpha (no blur — the stage never depth-blurs): the dark arm one rung brighter.</summary>
        public static float GhostAlpha(int d, bool dark) => (d switch { 1 => 0.30f, 2 => 0.18f, 3 => 0.13f, 4 => 0.10f, _ => 0.08f }) + (dark ? 0.03f : 0f);
        public static ushort GhostWeight(int d) => d switch { 1 => (ushort)600, 2 => (ushort)500, _ => (ushort)400 };
        /// <summary>The share a ghost's tint mixes toward the cover palette.</summary>
        public const float GhostTint = 0.35f;

        public static float GhostPitch(in Region r, int d) => MathF.Round(r.Base * GhostScale(d) * LineK) + GhostGap;

        /// <summary>How many ghosts fit between the live block's top and <see cref="Region.GhostTop"/>, ≤ the class's depth.</summary>
        public static int GhostsThatFit(in Region r, float liveTop)
        {
            float y = liveTop - BlockAir;
            int n = 0;
            for (int d = 1; d <= r.MaxGhosts; d++)
            {
                y -= GhostPitch(in r, d);
                if (y < r.GhostTop) break;
                n++;
            }
            return n;
        }

        /// <summary>The region for a face box (<paramref name="faceW"/> × <paramref name="faceH"/>, the open gallery's inset
        /// already excluded by the host) on a stage layout.</summary>
        public static Region For(in Stage.Layout L, float faceW, float faceH, bool galleryOpen, bool chrome)
        {
            float caption = L.CaptionFont.Size;
            float cardFoot = Stage.Layout.NowPlayingCardY + Stage.Layout.NowPlayingCardH + Stage.Layout.Pad;
            float top = chrome ? cardFoot : Stage.Layout.TopBarH + Stage.Layout.Pad;
            switch (L.Aspect)
            {
                case Stage.Aspect.Compact:
                {
                    float lineH = MathF.Round(caption * LineK);
                    float anchor = L.TransportTop - Stage.Layout.Pad - HairlineH - BlockAir - lineH * 0.5f;
                    return new Region(L.Aspect, L.TransportLeft, L.TransportW, MathF.Round(anchor), caption, Emphasis.HeldScalePortrait,
                        1, Stage.Layout.TopBarH + 8f, L.TransportTop - 8f, ReadAhead: false, Translation: false, Cloud: false, LandBlur: false);
                }
                case Stage.Aspect.Portrait:
                {
                    float colW = MathF.Max(0f, faceW - 2f * L.PadX);
                    if (galleryOpen && L.ShowGallery)
                    {
                        // the gallery is a full-width sheet over the lower 55 %: Verse keeps the top, one ghost, no read-ahead
                        float bottom = faceH - L.GalleryH - Stage.Layout.Pad;
                        float anchor = MathF.Max(top + caption * 2f, bottom - caption * LineK * 1.5f - BlockAir);
                        return new Region(L.Aspect, L.PadX, colW, MathF.Round(anchor), caption, Emphasis.HeldScalePortrait,
                            1, top, bottom, ReadAhead: false, Translation: false, Cloud: false, LandBlur: true);
                    }
                    return new Region(L.Aspect, L.PadX, colW, MathF.Round(AnchorFrac * faceH), caption, Emphasis.HeldScalePortrait,
                        PortraitGhosts, top, L.TransportTop - BottomGap, ReadAhead: true, Translation: true, Cloud: true, LandBlur: true);
                }
                default:
                {
                    float size = MathF.Round(caption * DesktopTypeK * 0.5f) * 2f;
                    float colW = Stage.Layout.Q4(MathF.Min(ColumnFrac * faceW, ColumnMaxW * L.LyricsTypeScale));
                    float colX = MathF.Round((faceW - colW) * 0.5f);
                    return new Region(L.Aspect, colX, colW, MathF.Round(AnchorFrac * faceH), size, Emphasis.HeldScale,
                        faceH >= TallH ? TallGhosts : DesktopGhosts, top, L.TransportTop - BottomGap,
                        ReadAhead: true, Translation: true, Cloud: true, LandBlur: true);
                }
            }
        }
    }
}
