// ── Home/Board/SectionIdentity.cs: which Spotify section is which, across answers (pinned-home design 2.2) ─────────
//
// key    = the section uri
// fp     = FNV-1a 64 of typename | title template | header entity uri | dominant card kind | dominant format
// family = typename | title template        ("hide all like this")
//
// The title template (`translatedBaseText`) is hashed as an opaque token and never matched against words. Which of key
// and fp leads is ONE constant (`KeyFirst`), set from Phase 0's identity log (HomeIdentLog.cs): key first until the log
// says uris are minted per request. The failure mode either way is "a hidden section shows once", never a lost layout.
// Pure.

using Wavee.HomeUi;

namespace Wavee;

public static class SectionIdentity
{
    /// <summary>Key leads, the fingerprint is the fallback. Flips to fingerprint-first (key the tie-break) if the identity
    /// log shows section uris minted per request.</summary>
    public const bool KeyFirst = true;

    /// <summary>How long a section stays remembered after its last answer, and how many are kept (design 1.2.1).</summary>
    public const long SeenTtlMs = 90L * 24 * 3600 * 1000;
    public const int MaxSeen = 200;

    public static SectionRef Of(SectionInput s)
    {
        string typeName = s.TypeName;
        string titleBase = s.TitleBase ?? "";
        DominantOf(s.Cards, out var kind, out string format);
        ulong h = Fnv.Offset;
        h = Fnv.Mix(h, typeName);
        h = Fnv.Mix(h, titleBase);
        h = Fnv.Mix(h, s.HeaderUri ?? "");
        h = Fnv.Mix(h, kind is { } k ? KindWord(k) : "");
        h = Fnv.Mix(h, format);
        return new SectionRef(s.Uri, h, typeName + "|" + titleBase);
    }

    /// <summary>The index in <paramref name="answer"/> of the section <paramref name="wanted"/> names: by key, then by
    /// fingerprint (or the other way round, <see cref="KeyFirst"/>), else -1.</summary>
    public static int Match(SectionRef wanted, IReadOnlyList<SectionRef> answer)
    {
        int byKey = -1, byFp = -1;
        for (int i = 0; i < answer.Count; i++)
        {
            if (byKey < 0 && wanted.Key.Length > 0 && string.Equals(answer[i].Key, wanted.Key, StringComparison.Ordinal)) byKey = i;
            if (byFp < 0 && wanted.Fp != 0 && answer[i].Fp == wanted.Fp) byFp = i;
        }
        return KeyFirst ? (byKey >= 0 ? byKey : byFp) : (byFp >= 0 ? byFp : byKey);
    }

    public static bool InFamily(SectionRef r, SectionRef family)
        => family.Family.Length > 0 && string.Equals(r.Family, family.Family, StringComparison.Ordinal);

    /// <summary>The seen list after an answer: every section of <paramref name="answer"/> stamped <paramref name="nowMs"/>
    /// (matched by key), the rest kept until <see cref="SeenTtlMs"/>, at most <see cref="MaxSeen"/>, newest first.</summary>
    public static System.Collections.Immutable.ImmutableArray<SeenRef> Remember(
        System.Collections.Immutable.ImmutableArray<SeenRef> seen, IReadOnlyList<SectionRef> answer, long nowMs)
    {
        var list = new List<SeenRef>(answer.Count + (seen.IsDefault ? 0 : seen.Length));
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < answer.Count; i++)
            if (keys.Add(answer[i].Key)) list.Add(new SeenRef(answer[i], nowMs));
        if (!seen.IsDefault)
            foreach (var s in seen)
                if (nowMs - s.LastSeenMs <= SeenTtlMs && keys.Add(s.Ref.Key)) list.Add(s);
        list.Sort(static (a, b) => b.LastSeenMs.CompareTo(a.LastSeenMs));
        if (list.Count > MaxSeen) list.RemoveRange(MaxSeen, list.Count - MaxSeen);
        return [.. list];
    }

    /// <summary>The most frequent card kind and format of a section (first seen wins a tie); null / "" when it has no card.</summary>
    static void DominantOf(IReadOnlyList<HomeCard> cards, out HomeCardKind? kind, out string format)
    {
        kind = null;
        format = "";
        if (cards.Count == 0) return;
        Span<int> kinds = stackalloc int[16];
        int best = -1;
        for (int i = 0; i < cards.Count; i++)
        {
            if (cards[i].IsBlank) continue;
            int k = (int)cards[i].Kind & 15;
            kinds[k]++;
            if (best < 0 || kinds[k] > kinds[best]) best = k;
        }
        if (best >= 0) kind = (HomeCardKind)best;
        Dictionary<string, int>? counts = null;
        int top = 0;
        for (int i = 0; i < cards.Count; i++)
        {
            if (cards[i].IsBlank || cards[i].Format is not { Length: > 0 } f) continue;
            counts ??= new Dictionary<string, int>(StringComparer.Ordinal);
            int n = counts[f] = counts.GetValueOrDefault(f) + 1;
            if (n > top) { top = n; format = f; }
        }
    }

    static string KindWord(HomeCardKind k) => k switch
    {
        HomeCardKind.Playlist => "playlist", HomeCardKind.Album => "album", HomeCardKind.Artist => "artist",
        HomeCardKind.Track => "track", HomeCardKind.Liked => "liked", HomeCardKind.Episode => "episode",
        HomeCardKind.Audiobook => "audiobook", HomeCardKind.Podcast => "podcast", _ => "",
    };

    /// <summary>FNV-1a 64 over UTF-16 code units, a 0x1F separator after each field (so "a|bc" and "ab|c" differ).</summary>
    static class Fnv
    {
        public const ulong Offset = 14695981039346656037UL;
        const ulong Prime = 1099511628211UL;
        public static ulong Mix(ulong h, string s)
        {
            foreach (char c in s) { h ^= (byte)c; h *= Prime; h ^= (byte)(c >> 8); h *= Prime; }
            h ^= 0x1F; h *= Prime;
            return h;
        }
    }
}
