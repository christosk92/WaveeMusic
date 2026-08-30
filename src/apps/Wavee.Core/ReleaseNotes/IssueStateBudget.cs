using System;
using System.Collections.Generic;

namespace Wavee.Core.ReleaseNotes;

/// <summary>The rate-limit policy for refreshing issue chips. Wavee ships no GitHub token, so the REST budget is 60
/// requests per hour per IP — a What's-new page with forty references would burn it on one opening.
/// <para>Pure: it decides WHICH keys to fetch and WHEN to give up; the HTTP half lives in the app's store.</para></summary>
public sealed class IssueStateBudget
{
    public const long OneDayMs = 24L * 60L * 60L * 1000L;

    /// <param name="maxPerOpen">How many issue fetches one opening of the page may spend.</param>
    /// <param name="ttlMs">How long a cached state stays good; inside it, an issue is never re-fetched.</param>
    public IssueStateBudget(int maxPerOpen = 20, long ttlMs = OneDayMs)
    {
        MaxPerOpen = maxPerOpen < 0 ? 0 : maxPerOpen;
        TtlMs = ttlMs < 0 ? 0 : ttlMs;
    }

    public int MaxPerOpen { get; }
    public long TtlMs { get; }

    /// <summary>The keys worth fetching now: input order, duplicates collapsed, anything still fresh in
    /// <paramref name="cache"/> dropped, and the result capped at <see cref="MaxPerOpen"/>. Empty is a valid plan (and
    /// the common one on a second visit) — the page then renders the snapshot states with its "as of" footer.</summary>
    public string[] Plan(IEnumerable<string> keys, IssueStateCache cache, long nowMs)
    {
        if (keys is null || MaxPerOpen == 0) return [];

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var plan = new List<string>(Math.Min(MaxPerOpen, 16));
        foreach (var key in keys)
        {
            if (string.IsNullOrEmpty(key)) continue;
            if (!seen.Add(key)) continue;
            if (cache is not null && cache.IsFresh(key, nowMs, TtlMs)) continue;
            plan.Add(key);
            if (plan.Count >= MaxPerOpen) break;
        }
        return plan.ToArray();
    }

    /// <summary>Stop the whole refresh, not just this request: GitHub answered 403 (rate limited or UA-rejected), or the
    /// response says the remaining quota is zero. Anything else — including a 404 for a deleted issue — is a per-issue
    /// problem the caller skips past.</summary>
    public bool ShouldStop(int statusCode, string? rateLimitRemainingHeader)
    {
        if (statusCode == 403 || statusCode == 429) return true;
        if (string.IsNullOrWhiteSpace(rateLimitRemainingHeader)) return false;
        return int.TryParse(rateLimitRemainingHeader.Trim(), System.Globalization.NumberStyles.None,
                            System.Globalization.CultureInfo.InvariantCulture, out int remaining) && remaining <= 0;
    }
}
