namespace Wavee.LocPack;

public enum LocVoice { Sentence, TitleCase, AllCaps, Lowercase, Acronym, Ambiguous }

public static class LocCasing
{
    public static LocVoice Classify(string value) => ClassifyCore(StripIcu(value));

    public static IReadOnlyList<(string Key, string Value, LocVoice Voice, string Why)> Violations(
        IReadOnlyDictionary<string, string> flat,
        IReadOnlyCollection<string> acronyms,
        IReadOnlyCollection<string> lowercaseKeyPrefixes)
        => Violations(flat, acronyms, lowercaseKeyPrefixes, properNouns: []);

    public static IReadOnlyList<(string Key, string Value, LocVoice Voice, string Why)> Violations(
        IReadOnlyDictionary<string, string> flat,
        IReadOnlyCollection<string> acronyms,
        IReadOnlyCollection<string> lowercaseKeyPrefixes,
        IReadOnlyCollection<string> properNouns)
    {
        var acronymSet = new HashSet<string>(acronyms, StringComparer.Ordinal);
        var properSet = new HashSet<string>(properNouns, StringComparer.Ordinal);
        var list = new List<(string, string, LocVoice, string)>();
        foreach (var kv in flat.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            string stripped = StripIcu(kv.Value);
            LocVoice voice = ClassifyCore(stripped);
            if (voice == LocVoice.AllCaps && (acronymSet.Contains(stripped) || acronymSet.Contains(kv.Value)))
                voice = LocVoice.Acronym;

            switch (voice)
            {
                case LocVoice.AllCaps:
                    list.Add((kv.Key, kv.Value, voice, "phrase shout (all caps)"));
                    break;
                case LocVoice.TitleCase:
                    if (properSet.Contains(kv.Value) || properSet.Contains(stripped))
                        break;
                    list.Add((kv.Key, kv.Value, voice, "title case (use sentence case)"));
                    break;
                case LocVoice.Lowercase:
                    // ICU templates that start with a placeholder strip to a lowercase tail ("{n} saves" → "saves").
                    if (kv.Value.Contains('{', StringComparison.Ordinal))
                        break;
                    // "1 album", "3–5 minutes", "7″ single" — measurements, not Zune rails.
                    if (stripped.Length > 0 && !char.IsLetter(stripped[0]))
                        break;
                    if (!SanctionedLowercaseKey(kv.Key, lowercaseKeyPrefixes))
                        list.Add((kv.Key, kv.Value, voice, "lowercase outside Zune rail / home eyebrow"));
                    break;
            }
        }
        return list;
    }

    public static IReadOnlyList<(string A, string B)> Contradictions(
        IReadOnlyDictionary<string, string> flat,
        IReadOnlyCollection<string> lowercaseKeyPrefixes)
    {
        var byNs = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (string key in flat.Keys)
        {
            string ns = TopSegment(key);
            if (!byNs.TryGetValue(ns, out List<string>? keys))
            {
                keys = [];
                byNs[ns] = keys;
            }
            keys.Add(key);
        }

        var pairs = new HashSet<(string A, string B)>();
        foreach (var keys in byNs.Values)
        {
            for (int i = 0; i < keys.Count; i++)
            {
                for (int j = i + 1; j < keys.Count; j++)
                {
                    string a = keys[i];
                    string b = keys[j];
                    if (!SameLastSegment(a, b)) continue;

                    string va = flat[a];
                    string vb = flat[b];
                    if (string.Equals(va, vb, StringComparison.Ordinal)) continue;
                    if (!string.Equals(va, vb, StringComparison.OrdinalIgnoreCase)) continue;

                    bool sa = SanctionedLowercaseKey(a, lowercaseKeyPrefixes);
                    bool sb = SanctionedLowercaseKey(b, lowercaseKeyPrefixes);
                    if (sa != sb) continue;

                    if (string.Compare(a, b, StringComparison.Ordinal) > 0)
                        (a, b) = (b, a);
                    pairs.Add((a, b));
                }
            }
        }

        return pairs.OrderBy(p => p.A, StringComparer.Ordinal).ThenBy(p => p.B, StringComparer.Ordinal).ToList();
    }

    public static IReadOnlyList<(string Value, IReadOnlyList<string> Keys)> SharedValueClusters(
        IReadOnlyDictionary<string, string> flat,
        int minKeys = 2)
    {
        var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var kv in flat)
        {
            if (!groups.TryGetValue(kv.Value, out List<string>? keys))
            {
                keys = [];
                groups[kv.Value] = keys;
            }
            keys.Add(kv.Key);
        }

        return groups
            .Where(g => g.Value.Count >= minKeys)
            .OrderByDescending(g => g.Value.Count)
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (g.Key, (IReadOnlyList<string>)g.Value.OrderBy(k => k, StringComparer.Ordinal).ToList()))
            .ToList();
    }

    internal static string StripIcu(string value)
    {
        string s = value;
        while (true)
        {
            int open = FindInnermostBraceOpen(s);
            if (open < 0) break;
            int close = s.IndexOf('}', open + 1);
            if (close < 0) break;
            s = s.Remove(open, close - open + 1);
        }
        return s.Trim();
    }

    static int FindInnermostBraceOpen(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '{') continue;
            bool inner = true;
            for (int j = i + 1; j < s.Length; j++)
            {
                if (s[j] == '{') { inner = false; break; }
                if (s[j] == '}') { if (inner) return i; break; }
            }
        }
        return -1;
    }

    static LocVoice ClassifyCore(string stripped)
    {
        if (stripped.Length == 0 || !stripped.Any(char.IsLetter))
            return LocVoice.Ambiguous;

        var letters = stripped.Where(char.IsLetter).ToArray();
        if (letters.All(char.IsLower))
            return LocVoice.Lowercase;

        if (letters.All(char.IsUpper))
            return ClassifyAllCaps(stripped);

        var words = ExtractWords(stripped);
        if (words.Count == 1 && IsCapitalizedWord(words[0]))
            return LocVoice.Ambiguous;

        if (words.Count >= 2 && words.All(IsCapitalizedWord))
            return LocVoice.TitleCase;

        return LocVoice.Sentence;
    }

    static LocVoice ClassifyAllCaps(string stripped)
    {
        var words = ExtractWords(stripped);
        if (words.Count >= 2)
            return LocVoice.AllCaps;

        if (words.Count == 1 && LooksLikeAcronymToken(stripped))
            return LocVoice.Acronym;

        if (words.Count == 0 && LooksLikeAcronymToken(stripped))
            return LocVoice.Acronym;

        return LocVoice.AllCaps;
    }

    static bool LooksLikeAcronymToken(string token)
    {
        int letterCount = token.Count(char.IsLetter);
        if (letterCount == 0) return false;
        if (token.Any(c => char.IsDigit(c) || c == '-'))
            return true;
        return letterCount >= 2 && letterCount <= 6;
    }

    static List<string> ExtractWords(string s)
    {
        var words = new List<string>();
        int i = 0;
        while (i < s.Length)
        {
            if (!char.IsLetter(s[i])) { i++; continue; }
            int start = i;
            i++;
            while (i < s.Length && (char.IsLetter(s[i]) || s[i] == '\''))
                i++;
            words.Add(s[start..i]);
        }
        return words;
    }

    static bool IsCapitalizedWord(string word)
        => word.Length > 0 && char.IsUpper(word[0]) && word.Skip(1).All(c => !char.IsLetter(c) || char.IsLower(c));

    static bool SanctionedLowercaseKey(string key, IReadOnlyCollection<string> lowercaseKeyPrefixes)
    {
        foreach (string prefix in lowercaseKeyPrefixes)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
                return true;
        }
        if (key.StartsWith("home.", StringComparison.Ordinal) && key.EndsWith("Eye", StringComparison.Ordinal))
            return true;
        // Tonic quality follows the key name, not the sentence-case glossary.
        return key is "detail.trackFacts.major" or "detail.trackFacts.minor" or "auth.pairUrl";
    }

    static string TopSegment(string key)
    {
        int dot = key.IndexOf('.');
        return dot < 0 ? key : key[..dot];
    }

    static bool SameLastSegment(string a, string b)
    {
        string la = LastSegment(a);
        string lb = LastSegment(b);
        return string.Equals(la, lb, StringComparison.OrdinalIgnoreCase);
    }

    static string LastSegment(string key)
    {
        int dot = key.LastIndexOf('.');
        return dot < 0 ? key : key[(dot + 1)..];
    }
}
