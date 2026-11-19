// ── Screens/SettingsSearch.cs — find a dial from the words a person has in mind (settings rework phase 2) ─────────────────────
//
// Role: CORE (handoff/settings-2026-11-18/DESIGN.md 4; owner decisions 4 and 5). No model, no network: the same answer on every machine,
// cheap enough to run on each keystroke on the UI thread. The Settings page's box (phase 4), the command palette's Settings group and the
// inspector's `settings q=` all ask this one index.
//
// THE DATA. Every dial the registry composes right now (the declared dials; the ACTIVE theme's option dials and the legacy rows its
// structure lists: another theme's options are never in the index) and the feature pointers (4.7, a place, not a control). Each carries
// its title, its invariant aliases (`SettingsDial.Aliases`), its authored terms (`settings.terms.<id>`, a phrase written with `+`), its
// choice labels (`SettingsDial.ChoiceKeys`), its caption, its group and its category. They are read in the UI language and, when that is
// not en-US, again in English through `Loc.TryGetIn("en-US", …)` (an English hit counts 0.9 of a UI-language one).
//
// NORMALISATION (`Fold`): lower case, the Latin-1 diacritics folded (`SidebarSearch.Fold`), `-` and `'` dropped inside a word ("hi-res"
// is "hires", and its parts are indexed too), a `.` between digits kept ("1.5x"), every other mark a separator. A light suffix stemmer
// per language (en ies→y, es, s, ing, ed, ion, er; nl heid, tje, je, en, s; ko the common particles) on words of 4+ letters, no
// dictionary. Stop words (`settings.search.stopWords`) are dropped from the query but never from a phrase; intent words
// (`settings.search.intentWords`) are SOFT: they never have to match and add when they do ("notifications off" is "notifications").
//
// MATCHING, per query term: the exact token 1.0 · the same stem 0.9 · a prefix (terms of 2+ letters) 0.6 + 0.3 × term/token · a typo,
// the restricted Damerau-Levenshtein distance against the tokens that share a trigram with the term (1 edit for 4-7 letters, 0.75;
// 2 for 8+, 0.55; Hangul compared by its jamo), tried only for a word the index does not know as typed ("flac" never reaches "flat").
// RANKING: a dial's score is the sum over terms of the best quality × field weight it reached (title 1.0, alias 1.0, terms 0.8, choice
// 0.7, caption 0.45, group 0.35, category 0.25), plus 1.2 × the field weight for each phrase whose words appear in order in the query
// (the last word may still be being typed), plus 0.15 when a term IS the title's first word, times the tier prior (common 1.0,
// advanced 0.85, developer 0.6). AND first: every required term matches the dial; when no dial passes, OR, each missing term costing
// 40 % ("close matches"). Ties keep the registry order. A greyed dial keeps its score (people look for what they cannot change yet; its
// row says why); a dial the facts compose away right now is no result. A child keeps its own rank: its hit names its parent when the
// parent is a result too (`SettingsHit.Under`), and its arrival opens the parent.
//
// COST LIMITS (review checks them): the index is built on first use, never at boot and never per frame, and rebuilt only when what it
// was built from changes (the UI culture, the active theme or its option values, Developer mode). A query on a built index allocates
// NOTHING of its own: the query is folded into a stack buffer, scores live in arrays the index owns (one slot per entry, cleared per
// query), candidate tokens in a fixed buffer of 64 per term (past it the worst are dropped, so the shortest prefixes stay), typo
// distances run on stack rows capped at 32 letters, and results land in the CALLER's buffer (the palette's pattern). A query past 64
// characters or 6 terms is cut. Nothing here runs per frame: a search runs only when its text changes.

using System.Numerics;
using FluentGpu.Localization;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee;

/// <summary>Where a result matched (the fields its best hits came from, plus a phrase).</summary>
[Flags]
public enum SettingsMatch : ushort
{
    None = 0,
    Title = 1 << 0,
    Alias = 1 << 1,
    Terms = 1 << 2,
    Choice = 1 << 3,
    Caption = 1 << 4,
    Group = 1 << 5,
    Category = 1 << 6,
    /// <summary>An authored phrase matched in order.</summary>
    Phrase = 1 << 7,
    /// <summary>A hit came from the English strings read beside another UI language.</summary>
    English = 1 << 8,
}

/// <summary>A place the search answers that is not a dial (design 4.7): a feature people name in a settings box. It opens a place and
/// writes nothing.</summary>
public sealed class SettingsPointer
{
    /// <summary>Stable id (<c>pointer.downloads</c>); its terms key is <c>settings.terms.&lt;id&gt;</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Its title (a loc key).</summary>
    public required string TitleKey { get; init; }

    /// <summary>The line under the title (a loc key), or null.</summary>
    public string? LineKey { get; init; }

    /// <summary>The loc key of its search terms.</summary>
    public string TermsKey => "settings.terms." + Id;

    /// <summary>A settings link it lands on (<see cref="Settings.Open(string?)"/>), or null when <see cref="Go"/> opens another place.</summary>
    public string? Link { get; init; }

    /// <summary>Opens a place outside Settings, or null.</summary>
    public Action? Go { get; init; }

    /// <summary>Whether the place exists under the active structure.</summary>
    public Func<bool> Visible { get; init; } = static () => true;

    /// <summary>Open its place.</summary>
    public void Open()
    {
        if (Go is { } go) go();
        else if (Link is { } link) Settings.Open(link);
    }
}

/// <summary>One search result: a dial or a pointer, its score and where it matched. A struct in the caller's buffer, so a query copies
/// references and numbers only.</summary>
public struct SettingsHit
{
    /// <summary>The dial, or null for a pointer.</summary>
    public SettingsDial? Dial;

    /// <summary>The pointer, or null for a dial.</summary>
    public SettingsPointer? Pointer;

    public float Score;

    public SettingsMatch Fields;

    /// <summary>The position (in the same results) of this dial's parent when the parent is a result too, else -1.</summary>
    public int Under;

    /// <summary>The title in the UI language (built with the index; no allocation per query).</summary>
    public string Title;

    /// <summary>Where it lives, "Category › Group" in the UI language (a pointer: its line, or empty).</summary>
    public string Place;

    /// <summary>The entry in the index that answered (what the labels are cached by).</summary>
    internal int Entry;

    /// <summary>The dial's id, or the pointer's.</summary>
    public readonly string Id => Dial?.Id ?? Pointer?.Id ?? "";
}

/// <summary>What a query came back with: how many results, and whether they are close matches (no dial matched every word).</summary>
public readonly record struct SettingsSearchResult(int Count, bool Close);

/// <summary>The settings search (see the file header).</summary>
public static class SettingsSearch
{
    /// <summary>A query is cut to this many characters.</summary>
    public const int MaxQuery = 64;

    /// <summary>A query is cut to this many words.</summary>
    public const int MaxTerms = 6;

    /// <summary>Candidate tokens one query word may reach.</summary>
    public const int MaxCandidates = 64;

    /// <summary>A token longer than this is matched exactly or by prefix, never by typo.</summary>
    public const int MaxWord = 32;

    /// <summary>The results page's cap.</summary>
    public const int PageResults = 30;

    /// <summary>A suggestion popup's cap.</summary>
    public const int PopupResults = 8;

    /// <summary>The features people name that are not dials (design 4.7). The sleep timer and the output device of the design's table
    /// are not here: this build has no sleep timer, and the output device is a dial (<c>sound.outputDevice</c>) whose own row answers.</summary>
    public static IReadOnlyList<SettingsPointer> Pointers { get; } =
    [
        new SettingsPointer
        {
            Id = "pointer.downloads", TitleKey = Strings.Settings.Pointers.Downloads.Title, LineKey = Strings.Settings.Pointers.Downloads.Line,
            Link = "storage/storage.cache.audio",
        },
        new SettingsPointer
        {
            // The theme's own way to change Home (WinUI's add band, Classic's Edit Home, Zune's customize: the palette's verb), offered only
            // while this build has one (`HomeCustomize`, owner 2026-11-18: off for the release).
            Id = "pointer.customizeHome", TitleKey = Strings.Home.Customizer.Title,
            Go = HomeUi.HomeVerbs.OpenCustomize, Visible = static () => HomeUi.HomeVerbs.CanCustomize,
        },
        new SettingsPointer
        {
            Id = "pointer.shortcuts", TitleKey = Strings.Settings.Groups.Help.Shortcuts, LineKey = Strings.Settings.Keyboard.Subtitle,
            Link = "help/help.shortcuts.playPause",
        },
    ];

    static SettingsIndex? s_index;

    /// <summary>The index for what the app shows right now: built on first use, rebuilt when its inputs changed.</summary>
    internal static SettingsIndex Index
    {
        get
        {
            var index = s_index;
            if (index is null || !index.IsCurrent()) s_index = index = SettingsIndex.Build();
            return index;
        }
    }

    /// <summary>Rank the dials and pointers for <paramref name="query"/> into the caller's <paramref name="results"/> (at most
    /// <see cref="PageResults"/>). Allocation-free on a built index.</summary>
    public static SettingsSearchResult Run(ReadOnlySpan<char> query, Span<SettingsHit> results) => Index.Run(query, results);

    /// <summary>"Did you mean": up to <paramref name="words"/>.Length indexed words nearest to the query's words (2 or 3 edits away),
    /// for the no-match state. Allocation-free on a built index (the words are the index's own strings).</summary>
    public static int Suggest(ReadOnlySpan<char> query, Span<string?> words) => Index.Suggest(query, words);

    /// <summary>The palette row's label for <paramref name="hit"/> ("Title — Category › Group"), built once per entry and kept.</summary>
    public static string PaletteLabel(in SettingsHit hit) => s_index is { } index ? index.PaletteLabel(hit.Entry) : hit.Title;

    /// <summary>How big the index is (the inspector's `settings`).</summary>
    public static (int Entries, int Tokens, int Postings, int Trigrams, int Phrases, double BuildMs, string Culture) Stats
        => Index.Stats;

    // ── normalisation ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Fold <paramref name="source"/> into <paramref name="destination"/>: words lower-cased and diacritic-folded, separated by
    /// one space; <c>-</c>, <c>'</c> and <c>’</c> dropped inside a word; a <c>.</c> between two digits kept; every other mark a separator.
    /// Returns the length written (cut at the destination's length).</summary>
    public static int Fold(ReadOnlySpan<char> source, Span<char> destination)
    {
        int w = 0;
        bool separate = false;
        for (int i = 0; i < source.Length && w < destination.Length; i++)
        {
            char c = source[i];
            if (c is '-' or '\'' or '’' or '­') continue;
            bool word = char.IsLetterOrDigit(c)
                || (c == '.' && i > 0 && i + 1 < source.Length && char.IsAsciiDigit(source[i - 1]) && char.IsAsciiDigit(source[i + 1]));
            if (!word)
            {
                separate = w > 0;
                continue;
            }
            if (separate)
            {
                destination[w++] = ' ';
                separate = false;
                if (w == destination.Length) break;
            }
            destination[w++] = SidebarSearch.Fold(c);
        }
        return w;
    }

    /// <summary>The runs of <paramref name="title"/> the query's words match, for a result's highlight (Zune's accent letters, design 7.3):
    /// a title word a query word is the start of marks the letters typed; one the query word equals by stem or reaches by a typo (the
    /// search's own bounds: 1 edit for 4-7 letters, 2 for 8+) marks the whole word. Title words are read as <see cref="Fold"/> reads them
    /// ("hi-res" is one word), and a span covers the title's own characters (a dropped hyphen inside it included). In title order, one span
    /// per word at most, at most <paramref name="spans"/>.Length; the count. Stack buffers only: no allocation.</summary>
    public static int TitleSpans(ReadOnlySpan<char> query, ReadOnlySpan<char> title, Span<SettingsTextSpan> spans)
    {
        if (spans.IsEmpty || title.IsEmpty) return 0;
        Span<char> q = stackalloc char[MaxQuery];
        int length = Fold(query.Length > MaxQuery ? query[..MaxQuery] : query, q);
        Span<int> starts = stackalloc int[MaxTerms];
        Span<int> lengths = stackalloc int[MaxTerms];
        int n = Words(q[..length], starts, lengths);
        if (n == 0) return 0;
        Lang lang = LangOf(Loc.CurrentCulture);
        Span<char> word = stackalloc char[MaxWord];
        Span<int> at = stackalloc int[MaxWord];       // folded letter k of the word came from title[at[k]]
        Span<char> stemWord = stackalloc char[MaxWord];
        Span<char> stemTerm = stackalloc char[MaxQuery];
        int count = 0, i = 0;
        while (i < title.Length && count < spans.Length)
        {
            int len = 0;
            bool tooLong = false;
            for (; i < title.Length; i++)
            {
                char c = title[i];
                if (c is '-' or '\'' or '’' or '­') continue;   // dropped inside a word, as Fold drops it
                bool letter = char.IsLetterOrDigit(c)
                    || (c == '.' && i > 0 && i + 1 < title.Length && char.IsAsciiDigit(title[i - 1]) && char.IsAsciiDigit(title[i + 1]));
                if (!letter)
                {
                    if (len > 0 || tooLong) break;
                    continue;
                }
                if (len == MaxWord) { tooLong = true; continue; }
                at[len] = i;
                word[len++] = SidebarSearch.Fold(c);
            }
            if (len == 0 || tooLong) continue;
            ReadOnlySpan<char> w = word[..len];
            int best = 0;   // letters of the word the best term covers (len = the whole word)
            for (int t = 0; t < n && best < len; t++)
            {
                ReadOnlySpan<char> term = q.Slice(starts[t], lengths[t]);
                if (term.Length <= len && w.StartsWith(term) && (term.Length >= 2 || term.Length == len))
                {
                    best = Math.Max(best, term.Length);
                    continue;
                }
                if (term.Length >= 4 && term.Length <= stemTerm.Length
                    && Stem(lang, w, stemWord) is var sw && Stem(lang, term, stemTerm) is var st
                    && stemWord[..sw].SequenceEqual(stemTerm[..st]))
                {
                    best = len;
                    continue;
                }
                int edits = term.Length >= 8 ? 2 : term.Length >= 4 ? 1 : 0;
                if (edits > 0 && Distance(term, w, edits) <= edits) best = len;
            }
            if (best == 0) continue;
            spans[count++] = new SettingsTextSpan(at[0], at[best - 1] + 1 - at[0]);
        }
        return count;
    }

    /// <summary>The words of a folded text: their starts and lengths (at most <paramref name="starts"/>.Length).</summary>
    internal static int Words(ReadOnlySpan<char> folded, Span<int> starts, Span<int> lengths)
    {
        int n = 0, i = 0;
        while (i < folded.Length && n < starts.Length)
        {
            while (i < folded.Length && folded[i] == ' ') i++;
            int s = i;
            while (i < folded.Length && folded[i] != ' ') i++;
            if (i > s) { starts[n] = s; lengths[n] = i - s; n++; }
        }
        return n;
    }

    /// <summary>The language a stemmer and the particle strip follow.</summary>
    internal enum Lang : byte { None, En, Nl, Ko }

    internal static Lang LangOf(string culture)
        => culture.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? Lang.En
            : culture.StartsWith("nl", StringComparison.OrdinalIgnoreCase) ? Lang.Nl
            : culture.StartsWith("ko", StringComparison.OrdinalIgnoreCase) ? Lang.Ko
            : Lang.None;

    /// <summary>The light stem of a folded word into <paramref name="destination"/> (at least as long as the word); its length. Words of
    /// fewer than 4 letters, and a strip that would leave fewer than 3, are kept whole.</summary>
    internal static int Stem(Lang lang, ReadOnlySpan<char> word, Span<char> destination)
    {
        word.CopyTo(destination);
        int n = word.Length;
        switch (lang)
        {
            case Lang.En:
                for (int round = 0; round < 2 && n >= 4; round++)
                {
                    int m = StripEn(destination, n);
                    if (m == n) break;
                    n = m;
                }
                break;
            case Lang.Nl:
                if (n >= 4) n = StripSuffix(destination, n, s_nlSuffixes);
                break;
            case Lang.Ko:
                foreach (string particle in s_koParticles)
                    if (n > particle.Length && destination[..n].EndsWith(particle)) { n -= particle.Length; break; }
                break;
        }
        return n;
    }

    static readonly string[] s_nlSuffixes = ["heid", "tje", "je", "en", "s"];
    static readonly string[] s_koParticles = ["으로", "은", "는", "이", "가", "을", "를", "의", "에", "로", "와", "과"];

    /// <summary>One English strip (the design's list, in its order): ies→y, es (after s, x, z, ch, sh: "boxes"), s (not ss), ing, ed,
    /// ion, er. Any other "-es" is a plain "-s" plural ("languages" is "language", never "languag").</summary>
    static int StripEn(Span<char> s, int n)
    {
        ReadOnlySpan<char> w = s[..n];
        if (w.EndsWith("ies") && n - 2 >= 3) { s[n - 3] = 'y'; return n - 2; }
        if (w.EndsWith("es") && n - 2 >= 3 && (w[n - 3] is 's' or 'x' or 'z' || w[..^2].EndsWith("ch") || w[..^2].EndsWith("sh"))) return n - 2;
        if (w.EndsWith("s") && !w.EndsWith("ss") && n - 1 >= 3) return n - 1;
        if (w.EndsWith("ing") && n - 3 >= 3) return n - 3;
        if (w.EndsWith("ed") && n - 2 >= 3) return n - 2;
        if (w.EndsWith("ion") && n - 3 >= 3) return n - 3;
        if (w.EndsWith("er") && n - 2 >= 3) return n - 2;
        return n;
    }

    static int StripSuffix(Span<char> s, int n, string[] suffixes)
    {
        foreach (string suffix in suffixes)
            if (s[..n].EndsWith(suffix) && n - suffix.Length >= 3) return n - suffix.Length;
        return n;
    }

    // ── the typo distance ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The restricted Damerau-Levenshtein distance (an adjacent transposition costs 1) between two folded words, or
    /// <paramref name="bound"/> + 1 as soon as it must exceed the bound. Hangul syllables are compared by their jamo (an arithmetic
    /// decomposition), so one mistyped jamo is one edit. Three stack rows, no allocation; a word past <see cref="MaxWord"/> letters is
    /// past every bound.</summary>
    internal static int Distance(ReadOnlySpan<char> a, ReadOnlySpan<char> b, int bound)
    {
        Span<int> x = stackalloc int[3 * MaxWord];
        Span<int> y = stackalloc int[3 * MaxWord];
        int n = Units(a, x), m = Units(b, y);
        if (n < 0 || m < 0 || Math.Abs(n - m) > bound) return bound + 1;
        Span<int> prev2 = stackalloc int[3 * MaxWord + 1];
        Span<int> prev = stackalloc int[3 * MaxWord + 1];
        Span<int> cur = stackalloc int[3 * MaxWord + 1];
        for (int j = 0; j <= m; j++) prev[j] = j;
        for (int i = 1; i <= n; i++)
        {
            cur[0] = i;
            int rowMin = i;
            for (int j = 1; j <= m; j++)
            {
                int cost = x[i - 1] == y[j - 1] ? 0 : 1;
                int d = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
                if (i > 1 && j > 1 && x[i - 1] == y[j - 2] && x[i - 2] == y[j - 1]) d = Math.Min(d, prev2[j - 2] + 1);
                cur[j] = d;
                if (d < rowMin) rowMin = d;
            }
            if (rowMin > bound) return bound + 1;
            Span<int> t = prev2; prev2 = prev; prev = cur; cur = t;
        }
        return prev[m];
    }

    /// <summary>A folded word as comparison units (a Hangul syllable as its two or three jamo); -1 when it is past the cap.</summary>
    internal static int Units(ReadOnlySpan<char> word, Span<int> units)
    {
        if (word.Length > MaxWord) return -1;
        int n = 0;
        foreach (char c in word)
        {
            if (c is >= '가' and <= '힣')
            {
                int s = c - 0xAC00;
                units[n++] = 0x1100 + s / 588;
                units[n++] = 0x1161 + s % 588 / 28;
                if (s % 28 > 0) units[n++] = 0x11A7 + s % 28;
            }
            else units[n++] = c;
        }
        return n;
    }
}

/// <summary>The built index and its per-query scratch (see the file header). Built by <see cref="Build"/>, asked through
/// <see cref="SettingsSearch"/>.</summary>
internal sealed class SettingsIndex
{
    // Field ids in a posting's byte (bits 0-2), the first-title-word flag (bit 3) and the English flag (bit 4).
    const int FTitle = 0, FAlias = 1, FTerms = 2, FChoice = 3, FCaption = 4, FGroup = 5, FCategory = 6;
    const byte TitleFirst = 1 << 3, EnglishBit = 1 << 4;
    static readonly float[] s_weights = [1.0f, 1.0f, 0.8f, 0.7f, 0.45f, 0.35f, 0.25f];
    static readonly SettingsMatch[] s_flags =
        [SettingsMatch.Title, SettingsMatch.Alias, SettingsMatch.Terms, SettingsMatch.Choice, SettingsMatch.Caption, SettingsMatch.Group, SettingsMatch.Category];

    const float PhraseFactor = 1.2f, TitleStartBonus = 0.15f, EnglishFactor = 0.9f, MissingTermKeeps = 0.6f;

    // ── what it was built from ──────────────────────────────────────────────────────────────────────────────────────
    readonly string _culture;
    readonly long _themeEpoch;
    readonly object? _values;
    readonly bool _developer;
    readonly double _buildMs;

    // ── entries, in registry order (the tie-break) ──────────────────────────────────────────────────────────────────
    readonly SettingsDial?[] _dials;
    readonly SettingsPointer?[] _pointers;
    readonly int[] _parent;
    readonly float[] _prior;
    readonly string[] _title, _place;
    readonly string?[] _label;

    // ── tokens (sorted ordinally), their postings, stems and trigrams ───────────────────────────────────────────────
    readonly string[] _tokens;
    readonly int[] _postStart, _postEntry;
    readonly byte[] _postField;
    readonly string[] _stems;
    readonly int[] _stemStart, _stemTokens;
    readonly Dictionary<long, int[]> _trigrams;
    readonly int[] _phraseStart, _phraseTokens, _phraseEntry;
    readonly byte[] _phraseField;
    readonly string[] _stop, _intent;
    readonly SettingsSearch.Lang _lang;

    // ── per-query scratch, owned and reused ─────────────────────────────────────────────────────────────────────────
    readonly float[] _score, _best;
    readonly byte[] _bestField;
    readonly SettingsMatch[] _fields;
    readonly int[] _matched;
    readonly bool[] _titleStart;
    readonly int[] _slot, _slotStamp, _seen;
    readonly int[] _cand = new int[SettingsSearch.MaxCandidates];
    readonly float[] _candQ = new float[SettingsSearch.MaxCandidates];
    readonly SettingsHit[] _ranked = new SettingsHit[SettingsSearch.PageResults];
    int _stamp, _candCount;

    SettingsIndex(Builder b, string culture, long themeEpoch, object? values, bool developer, double buildMs)
    {
        _culture = culture; _themeEpoch = themeEpoch; _values = values; _developer = developer; _buildMs = buildMs;
        _lang = SettingsSearch.LangOf(culture);
        int entries = b.Dials.Count;
        _dials = b.Dials.ToArray();
        _pointers = b.Pointers.ToArray();
        _prior = b.Prior.ToArray();
        _title = b.Title.ToArray();
        _place = b.Place.ToArray();
        _label = new string?[entries];
        _parent = new int[entries];
        var entryOf = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int e = 0; e < entries; e++)
            if (_dials[e] is { } d) entryOf.TryAdd(d.Id, e);
        for (int e = 0; e < entries; e++)
            _parent[e] = _dials[e]?.Parent is { } p && entryOf.TryGetValue(p, out int pe) ? pe : -1;

        // Tokens sorted ordinally, ids remapped.
        int tokenCount = b.Tokens.Count;
        var order = new int[tokenCount];
        for (int i = 0; i < tokenCount; i++) order[i] = i;
        var raw = b.Tokens.ToArray();
        Array.Sort(order, (l, r) => string.CompareOrdinal(raw[l], raw[r]));
        var remap = new int[tokenCount];
        _tokens = new string[tokenCount];
        for (int i = 0; i < tokenCount; i++) { _tokens[i] = raw[order[i]]; remap[order[i]] = i; }

        // Postings grouped per token (CSR).
        _postStart = new int[tokenCount + 1];
        foreach (var (token, _, _) in b.Postings) _postStart[remap[token] + 1]++;
        for (int i = 0; i < tokenCount; i++) _postStart[i + 1] += _postStart[i];
        _postEntry = new int[b.Postings.Count];
        _postField = new byte[b.Postings.Count];
        var fill = (int[])_postStart.Clone();
        foreach (var (token, entry, field) in b.Postings)
        {
            int at = fill[remap[token]]++;
            _postEntry[at] = entry;
            _postField[at] = field;
        }

        // Stems: each token's stem in the language it was read in; tokens grouped per stem (CSR over the sorted stems).
        var stemOfToken = new string[tokenCount];
        Span<char> buffer = stackalloc char[SettingsSearch.MaxWord * 2];
        for (int i = 0; i < tokenCount; i++)
        {
            string t = _tokens[i];
            if (t.Length > buffer.Length) { stemOfToken[i] = t; continue; }
            var lang = b.TokenEnglish[order[i]] ? SettingsSearch.Lang.En : _lang;
            int n = SettingsSearch.Stem(lang, t, buffer);
            stemOfToken[i] = n == t.Length ? t : new string(buffer[..n]);
        }
        var stems = new SortedSet<string>(stemOfToken, StringComparer.Ordinal);
        _stems = [.. stems];
        _stemStart = new int[_stems.Length + 1];
        var stemIndex = new int[tokenCount];
        for (int i = 0; i < tokenCount; i++)
        {
            stemIndex[i] = Array.BinarySearch(_stems, stemOfToken[i], StringComparer.Ordinal);
            _stemStart[stemIndex[i] + 1]++;
        }
        for (int i = 0; i < _stems.Length; i++) _stemStart[i + 1] += _stemStart[i];
        _stemTokens = new int[tokenCount];
        var stemFill = (int[])_stemStart.Clone();
        for (int i = 0; i < tokenCount; i++) _stemTokens[stemFill[stemIndex[i]]++] = i;

        // Trigrams of every token of 3+ letters, padded ("$ab", "abc", "bc$").
        var trigrams = new Dictionary<long, List<int>>();
        for (int i = 0; i < tokenCount; i++)
        {
            string t = _tokens[i];
            if (t.Length < 3) continue;
            for (int k = -1; k + 2 <= t.Length; k++)
            {
                long key = Trigram(t, k);
                if (!trigrams.TryGetValue(key, out var list)) trigrams[key] = list = new List<int>(4);
                if (list.Count == 0 || list[^1] != i) list.Add(i);
            }
        }
        _trigrams = new Dictionary<long, int[]>(trigrams.Count);
        foreach (var (key, list) in trigrams) _trigrams[key] = list.ToArray();

        // Phrases (CSR of token ids).
        _phraseStart = new int[b.Phrases.Count + 1];
        _phraseEntry = new int[b.Phrases.Count];
        _phraseField = new byte[b.Phrases.Count];
        int phraseTokens = 0;
        foreach (var (tokens, _, _) in b.Phrases) phraseTokens += tokens.Length;
        _phraseTokens = new int[phraseTokens];
        for (int p = 0, w = 0; p < b.Phrases.Count; p++)
        {
            var (tokens, entry, field) = b.Phrases[p];
            _phraseStart[p] = w;
            foreach (int t in tokens) _phraseTokens[w++] = remap[t];
            _phraseEntry[p] = entry;
            _phraseField[p] = field;
            _phraseStart[p + 1] = w;
        }

        _stop = b.Stop.ToArray();
        _intent = b.Intent.ToArray();

        _score = new float[entries];
        _best = new float[entries];
        _bestField = new byte[entries];
        _fields = new SettingsMatch[entries];
        _matched = new int[entries];
        _titleStart = new bool[entries];
        _slot = new int[tokenCount];
        _slotStamp = new int[tokenCount];
        _seen = new int[tokenCount];
    }

    /// <summary>Is this index still the one for what the app shows (the culture, the theme switch epoch and the active theme's option
    /// values, Developer mode)? Four reads, no allocation.</summary>
    public bool IsCurrent()
        => string.Equals(_culture, Loc.CurrentCulture, StringComparison.OrdinalIgnoreCase)
           && _themeEpoch == ThemeHost.RootEpoch.Peek()
           && ReferenceEquals(_values, ThemeHost.Values)
           && _developer == Platform.Developer.Enabled.Peek();

    public (int Entries, int Tokens, int Postings, int Trigrams, int Phrases, double BuildMs, string Culture) Stats
        => (_dials.Length, _tokens.Length, _postEntry.Length, _trigrams.Count, _phraseEntry.Length, _buildMs, _culture);

    public string PaletteLabel(int entry)
    {
        if ((uint)entry >= (uint)_label.Length) return "";
        return _label[entry] ??= _place[entry].Length == 0 ? _title[entry]
            : Strings.Palette.SettingsRow(_title[entry], _place[entry]);
    }

    // ── a query ─────────────────────────────────────────────────────────────────────────────────────────────────────

    public SettingsSearchResult Run(ReadOnlySpan<char> query, Span<SettingsHit> results)
    {
        int cap = Math.Min(results.Length, SettingsSearch.PageResults);
        if (cap == 0) return default;
        Span<char> q = stackalloc char[SettingsSearch.MaxQuery];
        int length = SettingsSearch.Fold(query.Length > SettingsSearch.MaxQuery ? query[..SettingsSearch.MaxQuery] : query, q);
        Span<int> starts = stackalloc int[SettingsSearch.MaxTerms];
        Span<int> lengths = stackalloc int[SettingsSearch.MaxTerms];
        int n = SettingsSearch.Words(q[..length], starts, lengths);
        if (n == 0) return default;

        // Stop words leave the per-term pass (never a phrase); intent words are soft. A query of only soft words asks for them; one of
        // only stop words asks for those.
        int stopMask = 0, softMask = 0;
        Span<int> exact = stackalloc int[SettingsSearch.MaxTerms];
        for (int i = 0; i < n; i++)
        {
            ReadOnlySpan<char> term = q.Slice(starts[i], lengths[i]);
            exact[i] = Find(_tokens, term);
            if (Find(_stop, term) >= 0) stopMask |= 1 << i;
            else if (Find(_intent, term) >= 0) softMask |= 1 << i;
        }
        int all = (1 << n) - 1;
        int required = all & ~stopMask & ~softMask;
        if (required == 0) { required = softMask; softMask = 0; }
        if (required == 0) { required = stopMask; stopMask = 0; }
        int scored = all & ~stopMask;

        int entries = _dials.Length;
        Array.Clear(_score, 0, entries);
        Array.Clear(_fields, 0, entries);
        Array.Clear(_matched, 0, entries);
        Array.Clear(_titleStart, 0, entries);

        PhrasePass(q, starts, lengths, n, exact);
        for (int i = 0; i < n; i++)
            if ((scored & (1 << i)) != 0) TermPass(q.Slice(starts[i], lengths[i]), exact[i], 1 << i);

        int found = Select(required, close: false, cap);
        bool close = false;
        if (found == 0)
        {
            found = Select(required, close: true, cap);
            close = found > 0;
        }

        for (int r = 0; r < found; r++)
        {
            ref SettingsHit hit = ref _ranked[r];
            hit.Under = -1;
            int parent = _parent[hit.Entry];
            if (parent >= 0)
                for (int k = 0; k < found; k++)
                    if (_ranked[k].Entry == parent) { hit.Under = k; break; }
            results[r] = hit;
        }
        return new SettingsSearchResult(found, close);
    }

    /// <summary>Each phrase whose words appear in order in the query (the last may be a prefix of the word still being typed) adds
    /// 1.2 × its field's weight to its entry, and those query words count as matched for it.</summary>
    void PhrasePass(ReadOnlySpan<char> q, ReadOnlySpan<int> starts, ReadOnlySpan<int> lengths, int n, ReadOnlySpan<int> exact)
    {
        if (n < 2) return;
        for (int p = 0; p < _phraseEntry.Length; p++)
        {
            int from = _phraseStart[p], count = _phraseStart[p + 1] - from;
            if (count > n) continue;
            for (int at = 0; at + count <= n; at++)
            {
                bool match = true;
                for (int k = 0; k < count && match; k++)
                {
                    int token = _phraseTokens[from + k];
                    if (exact[at + k] == token) continue;
                    bool typing = k == count - 1 && at + k == n - 1 && lengths[at + k] >= 2;
                    match = typing && _tokens[token].AsSpan().StartsWith(q.Slice(starts[at + k], lengths[at + k]));
                }
                if (!match) continue;
                int entry = _phraseEntry[p];
                byte field = _phraseField[p];
                _score[entry] += PhraseFactor * Weight(field);
                _fields[entry] |= SettingsMatch.Phrase | Flag(field);
                _matched[entry] |= ((1 << count) - 1) << at;
                break;
            }
        }
    }

    /// <summary>One query word: its candidate tokens, then each entry's best quality × field weight over their postings.</summary>
    void TermPass(ReadOnlySpan<char> term, int exact, int bit)
    {
        Gather(term, exact);
        for (int c = 0; c < _candCount; c++)
        {
            int token = _cand[c];
            float quality = _candQ[c];
            for (int p = _postStart[token]; p < _postStart[token + 1]; p++)
            {
                int entry = _postEntry[p];
                byte field = _postField[p];
                if (quality < 1f) field = (byte)(field & ~TitleFirst);   // the title-start bonus is for the word itself, not a stem, prefix or typo
                float w = quality * Weight(field);
                if (w > _best[entry] || (w == _best[entry] && (field & TitleFirst) != 0))
                {
                    _best[entry] = w;
                    _bestField[entry] = field;
                }
            }
        }
        for (int e = 0; e < _best.Length; e++)
        {
            float best = _best[e];
            if (best <= 0f) continue;
            byte field = _bestField[e];
            _score[e] += best;
            _fields[e] |= Flag(field);
            if ((field & TitleFirst) != 0) _titleStart[e] = true;
            _matched[e] |= bit;
            _best[e] = 0f;
        }
    }

    /// <summary>The candidate tokens of one query word, best quality kept per token: exact, same stem, prefix, then typo.</summary>
    void Gather(ReadOnlySpan<char> term, int exact)
    {
        _stamp++;
        _candCount = 0;
        if (exact >= 0) Add(exact, 1f);

        Span<char> stem = stackalloc char[SettingsSearch.MaxQuery];
        AddStem(term, _lang, stem);
        if (_lang != SettingsSearch.Lang.En) AddStem(term, SettingsSearch.Lang.En, stem);

        if (term.Length >= 2)
        {
            for (int t = LowerBound(_tokens, term); t < _tokens.Length && _tokens[t].AsSpan().StartsWith(term); t++)
                if (_tokens[t].Length > term.Length) Add(t, 0.6f + 0.3f * term.Length / _tokens[t].Length);
        }

        Span<int> units = stackalloc int[3 * SettingsSearch.MaxWord];
        int unitCount = SettingsSearch.Units(term, units);
        int maxEdits = unitCount >= 8 ? 2 : unitCount >= 4 ? 1 : 0;
        // A typo is a fallback: a word the index knows as typed ("flac") never reaches its neighbours ("flat").
        if (maxEdits == 0 || term.Length < 3 || exact >= 0) return;
        for (int k = -1; k + 2 <= term.Length; k++)
        {
            if (!_trigrams.TryGetValue(Trigram(term, k), out int[]? tokens)) continue;
            foreach (int t in tokens)
            {
                if (_seen[t] == _stamp) continue;
                _seen[t] = _stamp;
                if (Math.Abs(_tokens[t].Length - term.Length) > maxEdits + 2) continue;
                int d = SettingsSearch.Distance(term, _tokens[t], maxEdits);
                if (d >= 1 && d <= maxEdits) Add(t, d == 1 ? 0.75f : 0.55f);
            }
        }
    }

    void AddStem(ReadOnlySpan<char> term, SettingsSearch.Lang lang, Span<char> buffer)
    {
        if (term.Length > buffer.Length) return;
        int n = SettingsSearch.Stem(lang, term, buffer);
        int s = Find(_stems, buffer[..n]);
        if (s < 0) return;
        for (int i = _stemStart[s]; i < _stemStart[s + 1]; i++) Add(_stemTokens[i], 0.9f);
    }

    /// <summary>Keep <paramref name="token"/> at <paramref name="quality"/> (its best so far); a full buffer drops its worst candidate
    /// for a better one (the longer token on a tie, so the shortest prefixes stay).</summary>
    void Add(int token, float quality)
    {
        if (_slotStamp[token] == _stamp)
        {
            int at = _slot[token];
            if (quality > _candQ[at]) _candQ[at] = quality;
            return;
        }
        int slot = _candCount;
        if (slot == SettingsSearch.MaxCandidates)
        {
            slot = 0;
            for (int i = 1; i < _candCount; i++)
                if (_candQ[i] < _candQ[slot] || (_candQ[i] == _candQ[slot] && _tokens[_cand[i]].Length > _tokens[_cand[slot]].Length)) slot = i;
            if (quality <= _candQ[slot]) return;
            _slotStamp[_cand[slot]] = 0;
        }
        else _candCount++;
        _cand[slot] = token;
        _candQ[slot] = quality;
        _slot[token] = slot;
        _slotStamp[token] = _stamp;
    }

    /// <summary>Rank the entries into the scratch: AND (every required word matched), or OR with each missing word costing 40 %. Ties
    /// keep the registry order. An entry the facts compose away right now is skipped.</summary>
    int Select(int required, bool close, int cap)
    {
        int found = 0;
        ISettingsFacts facts = SettingsFacts.Live;
        for (int e = 0; e < _dials.Length; e++)
        {
            float score = _score[e];
            if (score <= 0f) continue;
            int missing = BitOperations.PopCount((uint)(required & ~_matched[e]));
            if (missing > 0 && !close) continue;
            if (_titleStart[e]) score += TitleStartBonus;
            for (int m = 0; m < missing; m++) score *= MissingTermKeeps;
            score *= _prior[e];
            if (found == cap && score <= _ranked[cap - 1].Score) continue;
            if (_dials[e] is { } dial ? !dial.Visible(facts) : !_pointers[e]!.Visible()) continue;
            int at = found < cap ? found : cap - 1;
            while (at > 0 && _ranked[at - 1].Score < score) at--;
            for (int k = Math.Min(found, cap - 1); k > at; k--) _ranked[k] = _ranked[k - 1];
            _ranked[at] = new SettingsHit
            {
                Dial = _dials[e], Pointer = _pointers[e], Score = score, Fields = _fields[e], Under = -1,
                Title = _title[e], Place = _place[e], Entry = e,
            };
            if (found < cap) found++;
        }
        return found;
    }

    // ── "did you mean" ──────────────────────────────────────────────────────────────────────────────────────────────

    public int Suggest(ReadOnlySpan<char> query, Span<string?> words)
    {
        if (words.Length == 0) return 0;
        Span<char> q = stackalloc char[SettingsSearch.MaxQuery];
        int length = SettingsSearch.Fold(query.Length > SettingsSearch.MaxQuery ? query[..SettingsSearch.MaxQuery] : query, q);
        Span<int> starts = stackalloc int[SettingsSearch.MaxTerms];
        Span<int> lengths = stackalloc int[SettingsSearch.MaxTerms];
        int n = SettingsSearch.Words(q[..length], starts, lengths);
        int cap = Math.Min(words.Length, 8);
        Span<int> best = stackalloc int[8];
        Span<int> bestD = stackalloc int[8];
        int count = 0;
        _stamp++;
        for (int i = 0; i < n; i++)
        {
            ReadOnlySpan<char> term = q.Slice(starts[i], lengths[i]);
            if (term.Length < 3 || Find(_stop, term) >= 0 || Find(_intent, term) >= 0) continue;
            for (int k = -1; k + 2 <= term.Length; k++)
            {
                if (!_trigrams.TryGetValue(Trigram(term, k), out int[]? tokens)) continue;
                foreach (int t in tokens)
                {
                    if (_seen[t] == _stamp) continue;
                    _seen[t] = _stamp;
                    if (Find(_stop, _tokens[t]) >= 0) continue;
                    int d = SettingsSearch.Distance(term, _tokens[t], 3);
                    if (d < 1 || d > 3) continue;
                    if (count == cap && d >= bestD[cap - 1]) continue;
                    int at = count < cap ? count : cap - 1;
                    while (at > 0 && bestD[at - 1] > d) at--;
                    for (int m = Math.Min(count, cap - 1); m > at; m--) { best[m] = best[m - 1]; bestD[m] = bestD[m - 1]; }
                    best[at] = t;
                    bestD[at] = d;
                    if (count < cap) count++;
                }
            }
        }
        for (int i = 0; i < count; i++) words[i] = _tokens[best[i]];
        return count;
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────────────────────

    static float Weight(byte field) => s_weights[field & 7] * ((field & EnglishBit) != 0 ? EnglishFactor : 1f);

    static SettingsMatch Flag(byte field) => s_flags[field & 7] | ((field & EnglishBit) != 0 ? SettingsMatch.English : SettingsMatch.None);

    /// <summary>The 3-letter window starting at <paramref name="k"/> of "$word$" (k = -1 is the leading pad) as one key.</summary>
    static long Trigram(ReadOnlySpan<char> word, int k)
    {
        long a = k < 0 ? '$' : word[k];
        long b = word[k + 1];
        long c = k + 2 < word.Length ? word[k + 2] : '$';
        return (a << 32) | (b << 16) | c;
    }

    /// <summary>The index of <paramref name="word"/> in an ordinally sorted array, or -1.</summary>
    static int Find(string[] sorted, ReadOnlySpan<char> word)
    {
        int lo = 0, hi = sorted.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            int c = sorted[mid].AsSpan().SequenceCompareTo(word);
            if (c == 0) return mid;
            if (c < 0) lo = mid + 1;
            else hi = mid - 1;
        }
        return -1;
    }

    /// <summary>The first index whose word is not ordinally below <paramref name="word"/>.</summary>
    static int LowerBound(string[] sorted, ReadOnlySpan<char> word)
    {
        int lo = 0, hi = sorted.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (sorted[mid].AsSpan().SequenceCompareTo(word) < 0) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    // ── the build ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Build the index for what the app shows right now: the registry's composed dials in order (the active theme's option
    /// dials where the options group lists them), then the pointers; each read in the UI language and, beside another language, in
    /// English.</summary>
    public static SettingsIndex Build()
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        string culture = Loc.CurrentCulture;
        long themeEpoch = ThemeHost.RootEpoch.Peek();
        object? values = ThemeHost.Values;
        bool developer = Platform.Developer.Enabled.Peek();
        bool english = !string.Equals(culture, "en-US", StringComparison.OrdinalIgnoreCase);
        var b = new Builder(english);
        ISettingsFacts facts = SettingsFacts.Live;

        ThemeOptionsModel options = ThemeOptionsSection.Current(developer);
        foreach (SettingsCategory category in SettingsSpec.Categories)
            foreach (SettingsGroup group in category.Groups)
            {
                if (group.Live is null)
                {
                    foreach (SettingsDial dial in group.Dials) b.AddDial(dial, category, group, null, null, facts);
                    continue;
                }
                // The "‹Theme› options" group: the legacy rows the active structure lists, then the theme's generated options (a reference
                // to a core preference is that core dial, listed in its own group).
                string optionsTitle = Loc.Format(group.TitleKey, ("theme", options.NameKey.Length > 0 ? Loc.Get(options.NameKey) : ""));
                foreach (SettingsDial dial in group.Dials)
                    if (Lists(options.Legacy, dial)) b.AddDial(dial, category, group, optionsTitle, null, facts);
                foreach (GeneratedOptionRow row in options.Rows)
                    if (row.Pref is null) b.AddDial(row.Row, category, group, optionsTitle, ThemeOptionsSection.GroupTitle(row.Group), facts);
                if (options.Reset is { } reset) b.AddDial(reset, category, group, optionsTitle, null, facts);
            }
        foreach (SettingsPointer pointer in SettingsSearch.Pointers) b.AddPointer(pointer);
        b.AddWordList(Strings.Settings.Search.StopWords, b.Stop);
        b.AddWordList(Strings.Settings.Search.IntentWords, b.Intent);

        double ms = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var index = new SettingsIndex(b, culture, themeEpoch, values, developer, ms);
        Log.Info("settings", "search index built: " + index._dials.Length + " entries, " + index._tokens.Length + " tokens, "
            + index._postEntry.Length + " postings, " + index._phraseEntry.Length + " phrases in "
            + ms.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " ms (" + culture + ")");
        return index;
    }

    static bool Lists(IReadOnlyList<LegacyOptionRow> legacy, SettingsDial dial)
    {
        if (dial.LegacyIds.Count == 0) return false;
        foreach (LegacyOptionRow row in legacy)
            if (string.Equals(row.RowId, dial.LegacyIds[0], StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>The build's growing tables (dropped once the index is made).</summary>
    sealed class Builder(bool english)
    {
        public readonly List<SettingsDial?> Dials = [];
        public readonly List<SettingsPointer?> Pointers = [];
        public readonly List<float> Prior = [];
        public readonly List<string> Title = [], Place = [];
        public readonly List<string> Tokens = [];
        public readonly List<bool> TokenEnglish = [];
        public readonly List<(int Token, int Entry, byte Field)> Postings = [];
        public readonly List<(int[] Tokens, int Entry, byte Field)> Phrases = [];
        public readonly SortedSet<string> Stop = new(StringComparer.Ordinal), Intent = new(StringComparer.Ordinal);
        readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal);
        readonly HashSet<long> _posted = [];
        readonly HashSet<string> _phrased = new(StringComparer.Ordinal);
        readonly List<int> _words = [];
        char[] _buffer = new char[256];

        /// <summary>A dial with its words. <paramref name="groupTitle"/> overrides the group's own title (the "‹Theme› options" group's,
        /// formatted with the theme's name); <paramref name="subgroup"/> is a theme option's own group ("Window").</summary>
        public void AddDial(SettingsDial dial, SettingsCategory category, SettingsGroup group, string? groupTitle, string? subgroup,
            ISettingsFacts facts)
        {
            groupTitle ??= Text(group.TitleKey);
            int e = Dials.Count;
            Dials.Add(dial);
            Pointers.Add(null);
            Prior.Add(dial.Tier switch { SettingsTier.Advanced => 0.85f, SettingsTier.Developer => 0.6f, _ => 1f });
            string title = Text(dial.TitleKey);
            string categoryTitle = Text(category.TitleKey);
            Title.Add(title);
            Place.Add(Strings.Settings.Search.Breadcrumb(categoryTitle, subgroup is null ? groupTitle : groupTitle + " › " + subgroup));

            AddText(e, title, FTitle, false);
            foreach (string alias in dial.Aliases) AddText(e, alias, FAlias, false);
            AddTerms(e, Has(dial.TermsKey) ? Loc.Get(dial.TermsKey) : null, false);
            foreach (string choice in dial.ChoiceKeys) AddText(e, Text(choice), FChoice, false);
            string? caption = dial.Caption(facts);
            if (caption is not null && Has(caption)) AddText(e, Loc.Get(caption), FCaption, false);
            AddText(e, groupTitle, FGroup, false);
            if (subgroup is not null) AddText(e, subgroup, FGroup, false);
            AddText(e, categoryTitle, FCategory, false);

            if (!english) return;
            AddText(e, InEnglish(dial.TitleKey), FTitle, true);
            AddTerms(e, InEnglish(dial.TermsKey), true);
            foreach (string choice in dial.ChoiceKeys) AddText(e, InEnglish(choice), FChoice, true);
            if (caption is not null) AddText(e, InEnglish(caption), FCaption, true);
            AddText(e, InEnglish(group.TitleKey), FGroup, true);
            AddText(e, InEnglish(category.TitleKey), FCategory, true);
        }

        public void AddPointer(SettingsPointer pointer)
        {
            int e = Dials.Count;
            Dials.Add(null);
            Pointers.Add(pointer);
            Prior.Add(1f);
            string title = Text(pointer.TitleKey);
            Title.Add(title);
            Place.Add(pointer.LineKey is { } line ? Text(line) : "");
            AddText(e, title, FTitle, false);
            AddTerms(e, Has(pointer.TermsKey) ? Loc.Get(pointer.TermsKey) : null, false);
            if (!english) return;
            AddText(e, InEnglish(pointer.TitleKey), FTitle, true);
            AddTerms(e, InEnglish(pointer.TermsKey), true);
        }

        public void AddWordList(string key, SortedSet<string> into)
        {
            AddWords(Has(key) ? Loc.Get(key) : null, into);
            if (english) AddWords(InEnglish(key), into);
        }

        void AddWords(string? text, SortedSet<string> into)
        {
            if (text is null) return;
            foreach (int token in Tokenize(text, splitJoined: false)) into.Add(Tokens[token]);
            _words.Clear();
        }

        static bool Has(string key) => Localization.Has(key);

        static string Text(string key) => Has(key) ? Loc.Get(key) : key;

        static string? InEnglish(string key) => Loc.TryGetIn("en-US", key, out string? value) ? value : null;

        /// <summary>Post every word of <paramref name="text"/> (and the parts of a hyphenated word) to the entry.</summary>
        void AddText(int entry, string? text, int field, bool fromEnglish)
        {
            if (string.IsNullOrEmpty(text)) return;
            bool first = field == FTitle;
            foreach (int token in Tokenize(text, splitJoined: false))
            {
                Post(token, entry, (byte)(field | (first ? TitleFirst : 0) | (fromEnglish ? EnglishBit : 0)), fromEnglish);
                first = false;
            }
            if (text.Contains('-') || text.Contains('\''))
                foreach (int token in Tokenize(text, splitJoined: true))
                    Post(token, entry, (byte)(field | (fromEnglish ? EnglishBit : 0)), fromEnglish);
            _words.Clear();
        }

        /// <summary>An authored terms string: space-separated words, a phrase written with <c>+</c> between its words. A phrase's words
        /// are posted as terms too.</summary>
        void AddTerms(int entry, string? text, bool fromEnglish)
        {
            if (string.IsNullOrEmpty(text)) return;
            byte field = (byte)(FTerms | (fromEnglish ? EnglishBit : 0));
            foreach (string item in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!item.Contains('+'))
                {
                    AddText(entry, item, FTerms, fromEnglish);
                    continue;
                }
                var phrase = new List<int>(4);
                foreach (string word in item.Split('+', StringSplitOptions.RemoveEmptyEntries))
                {
                    phrase.AddRange(Tokenize(word, splitJoined: false));
                    _words.Clear();
                    AddText(entry, word, FTerms, fromEnglish);
                }
                if (phrase.Count < 2) continue;
                string key = entry + ":" + string.Join(',', phrase);
                if (!_phrased.Add(key)) continue;   // the same phrase read twice (the UI language and English) counts once
                Phrases.Add((phrase.ToArray(), entry, field));
            }
        }

        void Post(int token, int entry, byte field, bool fromEnglish)
        {
            long key = ((long)token << 24) | ((long)entry << 8) | field;
            if (_posted.Add(key)) Postings.Add((token, entry, field));
            if (!fromEnglish) TokenEnglish[token] = false;
        }

        /// <summary>The token ids of a text's folded words (in the reused list); <paramref name="splitJoined"/> splits at <c>-</c> and
        /// <c>'</c> first, so "hi-res" also gives "hi" and "res".</summary>
        List<int> Tokenize(string text, bool splitJoined)
        {
            _words.Clear();
            if (_buffer.Length < text.Length) _buffer = new char[text.Length * 2];
            ReadOnlySpan<char> source = text;
            if (splitJoined) source = text.Replace('-', ' ').Replace('\'', ' ').Replace('’', ' ');
            int n = SettingsSearch.Fold(source, _buffer);
            ReadOnlySpan<char> folded = _buffer.AsSpan(0, n);
            int i = 0;
            while (i < folded.Length)
            {
                while (i < folded.Length && folded[i] == ' ') i++;
                int s = i;
                while (i < folded.Length && folded[i] != ' ') i++;
                if (i > s) _words.Add(Id(folded[s..i]));
            }
            return _words;
        }

        int Id(ReadOnlySpan<char> word)
        {
            var lookup = _ids.GetAlternateLookup<ReadOnlySpan<char>>();
            if (lookup.TryGetValue(word, out int id)) return id;
            string s = new(word);
            id = Tokens.Count;
            Tokens.Add(s);
            TokenEnglish.Add(english);
            _ids[s] = id;
            return id;
        }
    }
}
