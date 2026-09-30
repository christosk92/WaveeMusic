// ── Shell/Lyrics.Match.cs ──────────────────────────────────────────────────────────────────────────────────────────
// Stage-0 METADATA MATCH for the search-matched sources (lrclib, kugou, qq, netease, musixmatch): "is this search hit
// the song we asked for?" — decided on title / artist / album / duration BEFORE any lyric text is fetched or ranked.
//
// Role: CORE (pure — no clock, no socket, no file; allocates freely, it runs a handful of times per track)
// Plan: docs/plans/wavee/lyrics-grey-sources-implementation.md, "Reranker v2 design §1" + the FROZEN CONTRACTS section.
//
// The scheme is Unilyric's take on Lyricify's CompareHelper: every field scores on a 7-point band (Perfect = 7 …
// VeryLow = 1, 0 = no match), the fields BOTH sides carry are summed with their weights (title 1, artist 1, album 0.4,
// duration 1) and renormalised into a confidence in [0,1], and that confidence maps back onto a `MatchBand`. Hard
// gates (|Δduration| > 5 s, a live/remix/cover/instrumental… marker on only one side) force `MatchBand.None` with a
// reason, and a hit that reports no duration drops the dimension and is capped at High — in 0.2.9 such a hit won
// automatically.
//
// Text comparison runs on `CjkFold.Fold` (full-width → ASCII, half-width kana → full-width, lowercase,
// Traditional → Simplified, katakana → hiragana). Hand-written by design: `string.Normalize(NFKC)` is not used because
// the build runs with InvariantGlobalization + NativeAOT (plan correction 6). The fold is COMPARISON-ONLY; nothing here
// ever produces display text.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Wavee;

public static partial class Lyrics
{
    /// <summary>How well a search hit's metadata matches the request, worst to best. <see cref="None"/> is also what a
    /// hard gate (duration, version marker) produces.</summary>
    public enum MatchBand : byte { None, VeryLow, Low, Medium, PrettyHigh, High, VeryHigh, Perfect }

    /// <summary>One search hit, source-neutral. <paramref name="DurationGrainMs"/> is the source's duration precision
    /// (1000 for a source that reports whole seconds, 0 for millisecond precision) — half of it is forgiven before the
    /// duration Gaussian, so rounding alone never costs a band. <paramref name="DurationMs"/> 0 = unknown.</summary>
    public readonly record struct Hit(string Id, string Title, IReadOnlyList<string> Artists, string? Album,
                                      long DurationMs, int DurationGrainMs);

    /// <summary>A hit's verdict. <paramref name="Title"/>/<paramref name="Artist"/>/<paramref name="Album"/>/
    /// <paramref name="Duration"/> are the per-field points on the 7-point scale, −1 when that field was not compared
    /// (absent on either side). <paramref name="Confidence"/> is the renormalised weighted score in [0,1] (0 when a
    /// hard gate fired). <paramref name="Reason"/> leads with the duration delta and carries the title rule, any cap and
    /// any gate — it is the tail of <see cref="MetadataMatch.Describe"/>.</summary>
    public readonly record struct MatchScore(MatchBand Band, double Confidence, double Title, double Artist,
                                             double Album, double Duration, string Reason);

    /// <summary>The stage-0 scorer and picker. Pure.</summary>
    public static partial class MetadataMatch
    {
        // Field weights (Lyricify/Unilyric): album is weak evidence — singles, compilations and regional releases
        // routinely disagree on it.
        const double WTitle = 1.0, WArtist = 1.0, WAlbum = 0.4, WDuration = 1.0;
        const double Points = 7.0;

        /// <summary>σ of the duration Gaussian, in ms.</summary>
        public const double DurationSigmaMs = 700.0;

        /// <summary>|Δduration| beyond this is a different recording (the same limit lrclib has always used).</summary>
        public const long DurationGateMs = 5000;

        // Artist names are the same person when one contains the other or their Levenshtein similarity reaches this.
        const double ArtistSimilarity = 0.88;

        /// <summary>Scores one hit against the request.</summary>
        public static MatchScore Score(Request req, in Hit hit)
        {
            string reqTitle = req.Title ?? "";
            string hitTitle = hit.Title ?? "";

            // ── title (always compared) ──
            double title = TitlePoints(reqTitle, hitTitle, out string titleRule);

            // ── artist ──
            double artist = -1;
            var reqArtists = SplitArtists(req.Artists);
            var hitArtists = SplitArtists(hit.Artists);
            if (reqArtists.Count > 0 && hitArtists.Count > 0) artist = ArtistPoints(reqArtists, hitArtists);

            // ── album ──
            double album = -1;
            if (!string.IsNullOrWhiteSpace(req.Album) && !string.IsNullOrWhiteSpace(hit.Album))
                album = TitlePoints(req.Album, hit.Album!, out _);

            // ── duration ──
            double duration = -1;
            long delta = 0;
            bool haveDuration = req.DurationMs > 0 && hit.DurationMs > 0;
            if (haveDuration)
            {
                delta = hit.DurationMs - req.DurationMs;
                duration = DurationPoints(delta, hit.DurationGrainMs);
            }

            // ── renormalise over the fields both sides carry ──
            double sum = WTitle * title, max = WTitle * Points;
            if (artist >= 0) { sum += WArtist * artist; max += WArtist * Points; }
            if (album >= 0) { sum += WAlbum * album; max += WAlbum * Points; }
            if (duration >= 0) { sum += WDuration * duration; max += WDuration * Points; }
            double confidence = Math.Clamp(sum / max, 0d, 1d);

            var reason = new StringBuilder(64);
            if (haveDuration)
            {
                reason.Append("dur=").Append(delta >= 0 ? "+" : "").Append(delta.ToString(CultureInfo.InvariantCulture)).Append("ms");
                if (hit.DurationGrainMs > 0) reason.Append(" (g").Append(hit.DurationGrainMs.ToString(CultureInfo.InvariantCulture)).Append(')');
            }
            else reason.Append("dur=?");
            reason.Append(" t:").Append(titleRule);

            // ── hard gates ──
            if (haveDuration && Math.Abs(delta) > DurationGateMs)
            {
                reason.Append(" gate: |Δ|>").Append(DurationGateMs.ToString(CultureInfo.InvariantCulture)).Append("ms");
                return new MatchScore(MatchBand.None, 0d, title, artist, album, duration, reason.ToString());
            }
            if (VersionMarkers.Mismatch(reqTitle, hitTitle, out string marker))
            {
                reason.Append(" gate: '").Append(marker).Append("' on one side");
                return new MatchScore(MatchBand.None, 0d, title, artist, album, duration, reason.ToString());
            }

            // ── band + caps ──
            MatchBand band = BandOf(confidence);
            MatchBand cap = MatchBand.Perfect;
            string? capWhy = null;
            if (title <= 1) { cap = MatchBand.Low; capWhy = "title"; }
            else if (title <= 2) { cap = MatchBand.Medium; capWhy = "title"; }
            if (artist == 0 && cap > MatchBand.Medium) { cap = MatchBand.Medium; capWhy = "artist"; }
            if (!haveDuration && cap > MatchBand.High) { cap = MatchBand.High; capWhy = "no duration"; }
            if (band > cap)
            {
                band = cap;
                reason.Append(" cap ").Append(cap).Append(": ").Append(capWhy);
            }
            if (band == MatchBand.None) reason.Append(" weak");

            return new MatchScore(band, confidence, title, artist, album, duration, reason.ToString());
        }

        /// <returns>index of the best hit at or above <paramref name="floor"/>, or -1.</returns>
        /// <remarks>Best = highest band, then highest confidence, then the earliest index (deterministic). A gated hit
        /// (<see cref="MatchBand.None"/>) never qualifies, whatever the floor. <paramref name="best"/> is the best
        /// score seen even when nothing qualifies (so a miss can still be logged), <c>default</c> for no hits.</remarks>
        public static int Pick(Request req, IReadOnlyList<Hit> hits, MatchBand floor, out MatchScore best)
        {
            best = default;
            int bestIndex = -1;
            bool any = false;
            for (int i = 0; i < hits.Count; i++)
            {
                Hit h = hits[i];
                MatchScore s = Score(req, in h);
                if (!any || s.Band > best.Band || (s.Band == best.Band && s.Confidence > best.Confidence))
                {
                    best = s;
                    bestIndex = i;
                    any = true;
                }
            }
            if (!any || best.Band == MatchBand.None || best.Band < floor) return -1;
            return bestIndex;
        }

        /// <summary>"a1b2 VeryHigh title=6 artist=7 dur=+320ms (g1000) t:suffix" — the inspector/log breadcrumb.</summary>
        public static string Describe(in Hit hit, in MatchScore s)
        {
            string id = hit.Id ?? "";
            if (id.Length > 12) id = id[..12];
            var sb = new StringBuilder(96);
            sb.Append(id.Length > 0 ? id : "?").Append(' ').Append(s.Band)
              .Append(" title=").Append(Pts(s.Title))
              .Append(" artist=").Append(Pts(s.Artist));
            if (s.Album >= 0) sb.Append(" album=").Append(Pts(s.Album));
            if (s.Reason is { Length: > 0 } reason) sb.Append(' ').Append(reason);
            return sb.ToString();

            static string Pts(double p) => p < 0 ? "-" : p.ToString("0.#", CultureInfo.InvariantCulture);
        }

        // ── bands ───────────────────────────────────────────────────────────────────────────────────────────────────

        static MatchBand BandOf(double c) => c switch
        {
            >= 0.985 => MatchBand.Perfect,
            >= 0.92 => MatchBand.VeryHigh,
            >= 0.84 => MatchBand.High,
            >= 0.75 => MatchBand.PrettyHigh,
            >= 0.62 => MatchBand.Medium,
            >= 0.48 => MatchBand.Low,
            >= 0.30 => MatchBand.VeryLow,
            _ => MatchBand.None,
        };

        // ── duration ────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>7·exp(−d²/2σ²), d = max(0, |Δ| − grain/2).</summary>
        static double DurationPoints(long deltaMs, int grainMs)
        {
            double d = Math.Max(0d, Math.Abs((double)deltaMs) - Math.Max(0, grainMs) / 2d);
            return Points * Math.Exp(-(d * d) / (2d * DurationSigmaMs * DurationSigmaMs));
        }

        // ── title cascade ───────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The title cascade, on folded text: exact → Perfect; feat-only difference, "x - y" ≡ "x (y)" or a
        /// deluxe/explicit/remaster/feat suffix on one side → VeryHigh; same base with a bracket on one side → Low;
        /// ≥ 80 % positional equality at the same length → High; else LCS-ratio bands.</summary>
        static double TitlePoints(string a, string b, out string rule)
        {
            string na = NormTitle(a), nb = NormTitle(b);
            if (na.Length == 0 || nb.Length == 0) { rule = "empty"; return 0; }
            if (string.Equals(na, nb, StringComparison.Ordinal)) { rule = "exact"; return 7; }

            // Featured-artist clauses differ only (Query.StripFeat is the one feat grammar).
            string fa = NormTitle(Query.StripFeat(na)), fb = NormTitle(Query.StripFeat(nb));
            if (fa.Length > 0 && string.Equals(fa, fb, StringComparison.Ordinal)) { rule = "feat"; return 6; }

            var da = Decompose(fa);
            var db = Decompose(fb);

            // "x - y" ≡ "x (y)": the same head and the same decorations, however they were written.
            if (string.Equals(da.Canonical, db.Canonical, StringComparison.Ordinal)) { rule = "dash≡bracket"; return 6; }

            // A deluxe / explicit / remaster / feat … decoration on one side only.
            if (string.Equals(da.WithoutSuffixes, db.WithoutSuffixes, StringComparison.Ordinal)) { rule = "suffix"; return 6; }

            // Same base, a bracket (or dash tail) on one side or different ones.
            if (da.Head.Length > 0 && string.Equals(da.Head, db.Head, StringComparison.Ordinal)) { rule = "bracket"; return 2; }

            string ka = Key(fa), kb = Key(fb);
            if (ka.Length == 0 || kb.Length == 0) { rule = "none"; return 0; }
            if (string.Equals(ka, kb, StringComparison.Ordinal)) { rule = "punct"; return 6; }

            if (ka.Length == kb.Length && ka.Length >= 3)
            {
                int same = 0;
                for (int i = 0; i < ka.Length; i++) if (ka[i] == kb[i]) same++;
                if (same >= 0.8 * ka.Length) { rule = "positional"; return 5; }
            }

            double r = (double)Lcs(ka, kb) / Math.Max(ka.Length, kb.Length);
            rule = "lcs" + r.ToString("0.00", CultureInfo.InvariantCulture);
            return r switch
            {
                >= 0.90 => 4,
                >= 0.80 => 3,
                >= 0.68 => 2,
                >= 0.55 => 1,
                _ => 0,
            };
        }

        /// <summary>Folded title with brackets unified to "()", typographic quotes/dashes straightened, a single
        /// space before "(" and none inside the brackets, whitespace collapsed.</summary>
        static string NormTitle(string s)
        {
            string f = CjkFold.Fold(s ?? "");
            var sb = new StringBuilder(f.Length + 4);
            foreach (char c in f)
            {
                char n = c switch
                {
                    '[' or '{' or '【' or '〔' or '〖' => '(',
                    ']' or '}' or '】' or '〕' or '〗' => ')',
                    '‘' or '’' or '`' or '´' => '\'',
                    '“' or '”' => '"',
                    '–' or '—' or '―' or '‐' or '−' => '-',
                    _ => char.IsWhiteSpace(c) ? ' ' : c,
                };
                if (n == '(')
                {
                    if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
                    sb.Append('(');
                    continue;
                }
                if (n == ' ' && (sb.Length == 0 || sb[^1] == ' ' || sb[^1] == '(')) continue;
                if (n == ')' && sb.Length > 0 && sb[^1] == ' ') sb.Length--;
                sb.Append(n);
            }
            while (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
            return sb.ToString();
        }

        /// <summary>A normalised title split into its head and its decorations (every "(…)" group and a " - tail").
        /// <c>Canonical</c> writes every decoration as "(d)"; <c>WithoutSuffixes</c> does the same after
        /// dropping the version-neutral ones (remaster, deluxe, explicit, feat …).</summary>
        readonly record struct Parts(string Head, string Canonical, string WithoutSuffixes);

        static Parts Decompose(string s)
        {
            var head = new StringBuilder(s.Length);
            var decorations = new List<string>(2);

            // A " - tail" at bracket depth 0 is a decoration (Spotify's "Song - Remastered 2011").
            string body = s, dashTail = "";
            int depth = 0;
            for (int i = 0; i + 2 < s.Length; i++)
            {
                char c = s[i];
                if (c == '(') depth++;
                else if (c == ')') depth = Math.Max(0, depth - 1);
                else if (depth == 0 && c == ' ' && s[i + 1] == '-' && s[i + 2] == ' ')
                {
                    body = s[..i];
                    dashTail = s[(i + 3)..].Trim();
                    break;
                }
            }

            depth = 0;
            var cur = new StringBuilder();
            foreach (char c in body)
            {
                if (c == '(')
                {
                    if (depth > 0) cur.Append(c);
                    depth++;
                }
                else if (c == ')' && depth > 0)
                {
                    depth--;
                    if (depth == 0) { AddDecoration(decorations, cur.ToString()); cur.Clear(); }
                    else cur.Append(c);
                }
                else if (depth > 0) cur.Append(c);
                else head.Append(c);
            }
            if (depth > 0) AddDecoration(decorations, cur.ToString());      // unbalanced "(…" runs to the end
            if (dashTail.Length > 0)
            {
                // A dash tail may itself carry brackets: "Song - Live (2011)" → decorations "live", "2011"-ish; keep it whole.
                AddDecoration(decorations, dashTail.Replace("(", " ").Replace(")", " "));
            }

            string h = CollapseSpaces(head.ToString());
            var canon = new StringBuilder(h);
            var kept = new StringBuilder(h);
            foreach (string d in decorations)
            {
                canon.Append(" (").Append(d).Append(')');
                if (!IsSuffix(d)) kept.Append(" (").Append(d).Append(')');
            }
            return new Parts(h, canon.ToString(), kept.ToString());

            static void AddDecoration(List<string> into, string d)
            {
                d = CollapseSpaces(d);
                if (d.Length > 0) into.Add(d);
            }
        }

        // Decorations that do not change the recording's lyrics: remasters, editions, explicit/clean, radio/single/album
        // versions, featured artists, "from <film>" / CJK theme-song credits, a bare year. Matched as substrings of the
        // FOLDED decoration (so the CJK entries are written simplified).
        static readonly string[] SuffixWords =
        [
            "remaster", "deluxe", "explicit", "clean", "radio edit", "radio version", "single version", "album version",
            "original version", "bonus", "mono", "stereo", "edition", "anniversary", "expanded", "feat", "ft.", "ft ",
            "featuring", "with ", "from ", "prod.", "prod ", "soundtrack", "theme",
            "主题曲", "插曲", "片尾曲", "片头曲", "推广曲", "原声带", "电视剧", "电影",
        ];

        static bool IsSuffix(string d)
        {
            if (d == "ft" || d == "explicit" || d == "clean") return true;
            bool allDigits = true;
            foreach (char c in d) if (!char.IsAsciiDigit(c) && c != ' ') { allDigits = false; break; }
            if (allDigits) return true;                                              // a bare year: "Song - 2011"
            foreach (string w in SuffixWords)
                if (d.Contains(w, StringComparison.Ordinal)) return true;
            return false;
        }

        static string CollapseSpaces(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (c == ' ' && (sb.Length == 0 || sb[^1] == ' ')) continue;
                sb.Append(c);
            }
            while (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
            return sb.ToString();
        }

        /// <summary>Letters and digits only — the key the character-level measures run on.</summary>
        static string Key(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s) if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.ToString();
        }

        const int MaxCompareChars = 256;

        static int Lcs(string a, string b)
        {
            if (a.Length > MaxCompareChars) a = a[..MaxCompareChars];
            if (b.Length > MaxCompareChars) b = b[..MaxCompareChars];
            var prev = new int[b.Length + 1];
            var cur = new int[b.Length + 1];
            for (int i = 1; i <= a.Length; i++)
            {
                for (int j = 1; j <= b.Length; j++)
                    cur[j] = a[i - 1] == b[j - 1] ? prev[j - 1] + 1 : Math.Max(prev[j], cur[j - 1]);
                (prev, cur) = (cur, prev);
            }
            return prev[b.Length];
        }

        static int Levenshtein(string a, string b)
        {
            if (a.Length > MaxCompareChars) a = a[..MaxCompareChars];
            if (b.Length > MaxCompareChars) b = b[..MaxCompareChars];
            var prev = new int[b.Length + 1];
            var cur = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                cur[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
                }
                (prev, cur) = (cur, prev);
            }
            return prev[b.Length];
        }

        // ── artists ─────────────────────────────────────────────────────────────────────────────────────────────────

        // Separators inside one artist credit, on FOLDED text (so "＆", "，", "；" are already ASCII and "與" is "与").
        [GeneratedRegex(@"\s*(?:\b(?:feat|ft)\b\.?|\bfeaturing\b|[&,、;/和与])\s*", RegexOptions.CultureInvariant)]
        private static partial Regex ArtistSepRx();

        /// <summary>Every credited name, folded, split on feat./ft./&amp;/,/、/;/和/与 and reduced to its letters and
        /// digits (so "Jay Chou" ≡ "jaychou"); deduped, order kept.</summary>
        static List<string> SplitArtists(IReadOnlyList<string>? artists)
        {
            var outv = new List<string>(4);
            if (artists is null) return outv;
            foreach (string raw in artists)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                foreach (string part in ArtistSepRx().Split(CjkFold.Fold(raw)))
                {
                    string k = Key(part);
                    if (k.Length == 0) k = part.Trim();                              // a symbol-only name keeps itself
                    if (k.Length > 0 && !outv.Contains(k)) outv.Add(k);
                }
            }
            return outv;
        }

        /// <summary>Jaccard over a greedy fuzzy one-to-one match (containment, or Levenshtein similarity ≥ 0.88),
        /// mapped onto the 7-point scale.</summary>
        static double ArtistPoints(List<string> req, List<string> hit)
        {
            var used = new bool[hit.Count];
            int matched = 0;
            foreach (string r in req)
            {
                int pick = -1;
                for (int j = 0; j < hit.Count && pick < 0; j++)
                    if (!used[j] && string.Equals(r, hit[j], StringComparison.Ordinal)) pick = j;
                for (int j = 0; j < hit.Count && pick < 0; j++)
                    if (!used[j] && SameArtist(r, hit[j])) pick = j;
                if (pick >= 0) { used[pick] = true; matched++; }
            }
            double jaccard = (double)matched / (req.Count + hit.Count - matched);
            return jaccard switch
            {
                >= 0.999 => 7,
                >= 0.66 => 6,
                >= 0.5 => 5,
                >= 0.33 => 4,
                > 0 => 3,
                _ => 0,
            };
        }

        static bool SameArtist(string a, string b)
        {
            // Containment needs a meaningful overlap: a lone CJK character or two Latin letters inside a longer name is
            // not the same artist.
            int shorter = Math.Min(a.Length, b.Length);
            bool meaningful = shorter >= 4 || (shorter >= 2 && ContainsCjk(a) && ContainsCjk(b));
            if (meaningful && (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal))) return true;
            int longer = Math.Max(a.Length, b.Length);
            if (longer == 0) return false;
            return 1.0 - (double)Levenshtein(a, b) / longer >= ArtistSimilarity;
        }

        static bool ContainsCjk(string s)
        {
            foreach (char c in s) if (CjkFold.IsCjk(c)) return true;
            return false;
        }
    }

    /// <summary>Version markers — a live / remix / cover / instrumental / karaoke / acoustic / sped-up / slowed /
    /// nightcore recording carries different (or no) vocals and a different clock, so a marker must appear on BOTH
    /// titles or on NEITHER (lx-music's rule). Markers are grouped: "(Instrumental)" and "(伴奏)" are the same marker.
    /// The request title's own markers count — a Spotify "Song - Live" requires a live hit.</summary>
    public static class VersionMarkers
    {
        // (group, variants). Latin variants match on ASCII word boundaries; any variant with a non-ASCII character is a
        // substring match. Variants are folded once at type init (so katakana entries match their hiragana fold and the
        // CJK entries match their Traditional spellings).
        static readonly (string Group, string[] Variants)[] Raw =
        [
            ("live", new[] { "live", "ライブ", "ライヴ", "现场", "现场版", "live版" }),
            ("remix", new[] { "remix", "remixed", "rmx", "dj版", "リミックス" }),
            ("cover", new[] { "cover", "covered", "翻唱", "翻自", "カバー" }),
            ("instrumental", new[] { "instrumental", "inst", "伴奏", "纯音乐", "インスト", "off vocal", "off-vocal", "オフボーカル",
                              "karaoke", "カラオケ", "卡拉ok", "backing track" }),
            ("acoustic", new[] { "acoustic", "unplugged", "不插电", "アコースティック" }),
            ("sped up", new[] { "sped up", "speed up", "加速版" }),
            ("slowed", new[] { "slowed", "降速", "降速版", "慢速版" }),
            ("nightcore", new[] { "nightcore" }),
        ];

        static readonly (string Group, string[] Variants)[] Folded = FoldAll();

        static (string, string[])[] FoldAll()
        {
            var outv = new (string, string[])[Raw.Length];
            for (int i = 0; i < Raw.Length; i++)
            {
                var vs = new string[Raw[i].Variants.Length];
                for (int j = 0; j < vs.Length; j++) vs[j] = CjkFold.Fold(Raw[i].Variants[j]);
                outv[i] = (Raw[i].Group, vs);
            }
            return outv;
        }

        /// <summary>True when a marker group is present on exactly one of the two titles; <paramref name="marker"/>
        /// names that group ("" when false).</summary>
        public static bool Mismatch(string requestTitle, string hitTitle, out string marker)
        {
            string a = CjkFold.Fold(requestTitle ?? "");
            string b = CjkFold.Fold(hitTitle ?? "");
            foreach (var (group, variants) in Folded)
            {
                if (Has(a, variants) != Has(b, variants)) { marker = group; return true; }
            }
            marker = "";
            return false;
        }

        static bool Has(string hay, string[] variants)
        {
            foreach (string v in variants)
                if (Contains(hay, v)) return true;
            return false;
        }

        static bool Contains(string hay, string needle)
        {
            if (needle.Length == 0 || hay.Length < needle.Length) return false;
            bool ascii = true;
            foreach (char c in needle) if (c > 0x7F) { ascii = false; break; }
            if (!ascii) return hay.Contains(needle, StringComparison.Ordinal);

            int from = 0;
            while (from <= hay.Length - needle.Length)
            {
                int i = hay.IndexOf(needle, from, StringComparison.Ordinal);
                if (i < 0) return false;
                int end = i + needle.Length;
                bool left = i == 0 || !char.IsAsciiLetterOrDigit(hay[i - 1]);
                bool right = end == hay.Length || !char.IsAsciiLetterOrDigit(hay[end]);
                if (left && right) return true;
                from = i + 1;
            }
            return false;
        }
    }

    /// <summary>The COMPARISON-ONLY text fold for CJK-aware matching (never display): full-width ASCII FF01–FF5E →
    /// ASCII, ideographic space → space, half-width katakana (FF61–FF9F, voiced marks composed) → full-width,
    /// lowercase (invariant), Traditional → Simplified (single-char OpenCC table, <c>Lyrics.CjkTable.cs</c>), katakana
    /// 30A1–30F6 → hiragana (−0x60). Hand-written rather than NFKC: the build runs InvariantGlobalization + NativeAOT.</summary>
    public static partial class CjkFold
    {
        // Half-width forms FF61–FF9D → full-width (。「」、・ then the katakana). FF9E/FF9F are the voiced marks.
        const string HalfWidthKana =
            "。「」、・ヲァィゥェォャュョッーアイウエオカキクケコサシスセソタチツテトナニヌネノハヒフヘホマミムメモヤユヨラリルレロワン";

        public static string Fold(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char c0 in s)
            {
                char c = c0;
                if (c >= '！' && c <= '～') c = (char)(c - 0xFEE0);           // full-width ASCII → ASCII
                else if (c == '　') c = ' ';                                    // ideographic space
                else if (c >= '｡' && c <= 'ﾝ') c = HalfWidthKana[c - 0xFF61];
                else if (c is 'ﾞ' or '゙' or '゛') { Voice(sb, handakuten: false); continue; }
                else if (c is 'ﾟ' or '゚' or '゜') { Voice(sb, handakuten: true); continue; }

                c = char.ToLowerInvariant(c);
                if (c >= '㐀' && TradToSimp.TryGetValue(c, out char simp)) c = simp;
                if (c >= 'ァ' && c <= 'ヶ') c = (char)(c - 0x60);             // katakana → hiragana
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>Composes a (half-width or combining) voiced mark onto the hiragana already appended; a mark with
        /// nothing to compose onto is dropped.</summary>
        static void Voice(StringBuilder sb, bool handakuten)
        {
            if (sb.Length == 0) return;
            char p = sb[^1];
            if (handakuten)
            {
                if (p is 'は' or 'ひ' or 'ふ' or 'へ' or 'ほ') sb[^1] = (char)(p + 2);
                return;
            }
            if (p == 'う') { sb[^1] = 'ゔ'; return; }
            if (p is 'か' or 'き' or 'く' or 'け' or 'こ' or 'さ' or 'し' or 'す' or 'せ' or 'そ'
                  or 'た' or 'ち' or 'つ' or 'て' or 'と' or 'は' or 'ひ' or 'ふ' or 'へ' or 'ほ')
                sb[^1] = (char)(p + 1);
        }

        /// <summary>Han (CJK Unified + Extension A + compatibility ideographs), kana (incl. half-width and the
        /// phonetic extensions) or Hangul (syllables and jamo).</summary>
        public static bool IsCjk(char c) =>
            (c >= '一' && c <= '鿿') ||      // CJK Unified Ideographs
            (c >= '㐀' && c <= '䶿') ||      // Extension A
            (c >= '豈' && c <= '﫿') ||      // Compatibility Ideographs
            (c >= '぀' && c <= 'ヿ') ||      // Hiragana + Katakana
            (c >= 'ㇰ' && c <= 'ㇿ') ||      // Katakana Phonetic Extensions
            (c >= 'ｦ' && c <= 'ﾟ') ||      // Half-width katakana
            (c >= '가' && c <= '힯') ||      // Hangul Syllables
            (c >= 'ᄀ' && c <= 'ᇿ') ||      // Hangul Jamo
            (c >= '㄰' && c <= '㆏');        // Hangul Compatibility Jamo
    }
}
