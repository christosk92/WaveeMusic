// ── Podcasts/Audiobook.Facts.cs — the audiobook facts Wavee's entity tables do not hold yet (credits, edition, price, language, similar) ──────────
//
// A book's credits arrive on the wire as AUDIOBOOK_SPECIFICS (kind 52: authors, narrators and the real publisher as separate lists, the edition, the
// release date, the title split), its price as AUDIOBOOK_PRICE (64), its access as SHOW_ACCESS (31) and its similar books from `similarAudiobooks`.
// Wavee does not request any of these yet (the data lane's contract, `PodcastPages.AudiobookOf`, will). Until it does, the audiobook page reads what
// the show row holds (the publisher column is the first author, the description's "Narrator(s):" header line the narrators) and lets this small
// store override it per book: the --fake fixtures fill it, and the data lane's answers slot in here field by field (or replace the store whole).
//
// Every field is optional: an empty list / string or a null means "not stated", and the page leaves that row off rather than guess.

using System.Collections.Immutable;
using FluentGpu.Signals;

namespace Wavee;

/// <summary>One credited person: a name and, when known, a <c>spotify:author:</c> uri (empty when not).</summary>
public readonly record struct AudiobookCredit(string Name, string Uri);

/// <summary>What a book states beyond its show row (AUDIOBOOK_SPECIFICS, AUDIOBOOK_PRICE, SHOW_ACCESS, similarAudiobooks). <see cref="Included"/> null =
/// access unknown (treated as included); false = for sale (<see cref="Price"/> / <see cref="ListPrice"/>, "Buy audiobook").</summary>
public sealed record AudiobookFacts(
    ImmutableArray<AudiobookCredit> Authors, ImmutableArray<AudiobookCredit> Narrators, string Publisher, string Edition, string Language,
    DateOnly? Released, bool? Included, string Price, string ListPrice, string BuyUrl, ImmutableArray<string> Similar)
{
    public static AudiobookFacts None { get; } = new([], [], "", "", "", null, null, "", "", "", []);
}

/// <summary>The per-book store of <see cref="AudiobookFacts"/>, keyed by the show uri. <see cref="Changed"/> ticks on every write (read it to follow).</summary>
public static class AudiobookFactsStore
{
    static readonly Dictionary<string, AudiobookFacts> s_facts = new(StringComparer.Ordinal);
    static readonly Signal<int> s_changed = new(0);

    /// <summary>Bumps on every <see cref="Put"/>: a reactive read of the store reads this first.</summary>
    public static IReadSignal<int> Changed => s_changed;

    /// <summary>The facts stated for <paramref name="uri"/>, or <see cref="AudiobookFacts.None"/>. Subscribes to <see cref="Changed"/>.</summary>
    public static AudiobookFacts Of(string uri)
    {
        _ = s_changed.Value;
        lock (s_facts) return s_facts.TryGetValue(uri, out var f) ? f : AudiobookFacts.None;
    }

    public static void Put(string uri, AudiobookFacts facts)
    {
        lock (s_facts) s_facts[uri] = facts;
        s_changed.Value = s_changed.Peek() + 1;
    }
}

/// <summary>The plan's monthly audiobook allowance (never the hours LEFT: Wavee cannot read that, owner 2026-11-17). The capping answer
/// (capping-api PutConsumption) is the data lane's; until it lands the page states the Premium plan's published 12 h and the next refill on the
/// 12th, the day the fixtures and the concept use. <c>null</c> = the account has no audiobook allowance (the line then reads "Included in Premium").</summary>
public static class AudiobookHours
{
    /// <summary>The Premium plan's monthly hours (Spotify's published allowance) until the capping answer states the account's own.</summary>
    public const int PlanHours = 12;
    /// <summary>The refill day of the month until the capping answer states the real refill instant (a stand-in: see the class summary).</summary>
    public const int RefillDay = 12;

    public const string HoursLeftUrl = "https://www.spotify.com/account/audiobook-listening-time/";
    public const string TopUpUrl = "https://audiobooks.spotify.com/top-up";   // the data lane's contract §1

    /// <summary>The next refill after <paramref name="today"/>: the <see cref="RefillDay"/> of this month when still ahead, else of the next.</summary>
    public static DateOnly NextRefill(DateOnly today)
    {
        var here = new DateOnly(today.Year, today.Month, RefillDay);
        return here > today ? here : here.AddMonths(1);
    }
}
