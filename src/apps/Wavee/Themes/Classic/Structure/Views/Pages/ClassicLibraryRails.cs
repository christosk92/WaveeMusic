// ── Themes/Classic/Structure/Views/Pages/ClassicLibraryRails.cs — the library's word rails (themes plan 6.1, T10c1) ───────────────────────────
//
// From `Entities\User.UI.cs` §2 (W8 — Zune's text pivot; it REPLACED the sort pill, which is deleted): the library's ADAPTERS over `Controls.Words.Rail` — which words, which codes, what a
// re-tap means. The rail itself (the metrics, the two stacked runs and the underline) is the shared `Controls.Words`, which the podcast reader also draws; the navigator's sort rail, the artists
// reader's scope rail and its own sort rail are Classic's.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;

namespace Wavee.Themes.Classic;

/// <summary>Classic's library rails.</summary>
internal static class ClassicLibraryRails
{
    /// <summary>The navigator's sort rail: the kind's words (<see cref="LibraryWordRail.WordsFor"/>) in rail order, each carrying its PERSISTED code (codes are not positions). Tapping the
    /// ACTIVE word flips the direction and a 10-px chevron after it says which way; picking another word resets to ascending. The rail holds the two Signal INSTANCES, so nothing here is
    /// frozen at mount — the words themselves are a per-kind constant and are built once.</summary>
    public static Element WordRail(EntityKind kind, Signal<int> sort, Signal<bool> desc)
    {
        var codes = LibraryWordRail.WordsFor(kind);
        var words = new Controls.Words.Word[codes.Length];
        for (int i = 0; i < codes.Length; i++)
        {
            int c = (int)codes[i];
            words[i] = new Controls.Words.Word(Loc.Bind(LibraryWordRail.WordKey(codes[i])), Code: c,
                                               Chevron: () => sort.Value == c && desc.Value);
        }
        // FIT, not fill: the words that fit the navigator's head whole, the rest behind "more ▾" (a fill rail's feathered edge read as a clipped fourth word,
        // "rece…", at every size). The view toggle beside it keeps its cells.
        return Controls.Words.Rail(words, sort, fit: true,
            onReselect: _ => desc.Value = !desc.Peek(),
            onSelect: _ => desc.Value = false);
    }

    /// <summary>The reader's scope rail (W3/W4): "in your library" · "all releases · N" — the same words with no direction flip. The total rides a <see cref="Prop{T}"/> so the facets
    /// answering re-fires ONE text bind instead of re-rendering the rail, and the count is formatted through a hoisted cache (never inside the thunk).
    /// <para><paramref name="compact"/> is the narrow reader's word: "all · N" instead of "all releases · N" while it answers true. It is a PREDICATE read INSIDE the bound text, so a
    /// column drag across the breakpoint re-fires one text bind and never re-renders the rail; null — the default — is the long word forever. <paramref name="fill"/> forwards to
    /// <see cref="Controls.Words.Rail"/> — the reader's horizontal scroll viewport instead of a hard clip.</para></summary>
    public static Element ScopeRail(Signal<int> scope, Prop<int> total, Func<bool>? compact = null, bool fill = false)
    {
        // TWO caches, one per word: ONE cache keyed on the count alone would rewrite its entry on every crossing of the breakpoint (the same N, the other word), which is exactly the
        // formatting the cache exists to avoid.
        Prop<string> all = compact is { } isCompact
            ? Prop.Of(() => isCompact()
                ? s_allShort.Get(total.Current(), static n => AllShortText(n))
                : s_allReleases.Get(total.Current(), static n => AllReleasesText(n)))
            : Prop.Of(() => s_allReleases.Get(total.Current(), static n => AllReleasesText(n)));
        // Positions ARE the scope codes: 0 = in your library, 1 = all releases.
        return Controls.Words.Rail(
        [
            new Controls.Words.Word(Loc.Bind(Strings.Library.Scope.InLibrary)),
            new Controls.Words.Word(all),
        ], scope, fill: fill);
    }

    static readonly FormatCache<int> s_allReleases = new(), s_allShort = new();

    static string AllReleasesText(int total) => ScopeWordText(Strings.Library.Scope.AllReleases, total);

    static string AllShortText(int total) => ScopeWordText(Strings.Library.Scope.AllShort, total);

    /// <summary>"&lt;word&gt; · N", or the bare word while the total is 0 — a facet that has not answered says nothing rather than "· 0".</summary>
    static string ScopeWordText(string key, int total)
    {
        string word = Loc.Get(key);
        return total > 0 ? word + " · " + FormatCache.Int(total) : word;
    }

    /// <summary>The reader's own sort rail: newest · oldest · a–z (the reader-local <c>Artist.ReaderSort</c> codes, NOT <see cref="LibraryNavSort"/>, and they ARE positions) — no
    /// direction flip, so a word is a plain set.</summary>
    public static Element ReaderSortRail(Signal<int> sort)
    {
        var words = new Controls.Words.Word[ClassicLibraryRules.ReaderSortWords];
        for (int i = 0; i < words.Length; i++) words[i] = new Controls.Words.Word(Loc.Bind(ReaderSortKey(i)));
        return Controls.Words.Rail(words, sort);
    }

    static string ReaderSortKey(int code) => code switch
    {
        1 => Strings.Library.ReaderSort.Oldest,
        2 => Strings.Library.ReaderSort.Alphabetical,
        _ => Strings.Library.ReaderSort.Newest,
    };
}
