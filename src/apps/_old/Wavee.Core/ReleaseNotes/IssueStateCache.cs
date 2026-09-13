using System;
using System.Collections.Generic;

namespace Wavee.Core.ReleaseNotes;

/// <summary>One issue's live GitHub state, as last fetched.</summary>
public sealed class IssueState
{
    /// <summary>open | closed.</summary>
    public string State { get; set; } = "open";
    /// <summary>completed | reopened | not_planned | duplicate | null.</summary>
    public string? StateReason { get; set; }
    public string Title { get; set; } = "";
    /// <summary>Unix-ms of the fetch that produced this entry — the TTL is measured from here.</summary>
    public long FetchedAtMs { get; set; }
}

/// <summary>The persisted "issue chips" cache: <c>"{repo}#{number}"</c> → its last known state.
/// <para>Wavee ships no GitHub token, so the REST budget is 60 requests/hour per IP; this cache plus
/// <see cref="IssueStateBudget"/> is what keeps a What's-new page open all day from burning it. Serialized with
/// <see cref="ReleaseNotesJsonContext"/> — dictionary keys are NOT camel-cased, so the repo slug survives verbatim.</para></summary>
public sealed class IssueStateCache
{
    public Dictionary<string, IssueState> Entries { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The canonical cache key for an issue or PR reference.</summary>
    public static string Key(string repo, int number)
        => repo + "#" + number.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The live state for a document's issue reference, or null when nothing has been fetched for it.</summary>
    public IssueState? Lookup(ReleaseIssue issue)
        => issue is null ? null : Lookup(Key(issue.Repo, issue.Number));

    /// <summary>The live state for a raw <c>"{repo}#{number}"</c> key, or null.</summary>
    public IssueState? Lookup(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        var entries = Entries;
        if (entries is null) return null;
        return entries.TryGetValue(key, out var state) ? state : null;
    }

    /// <summary>True when the key has an entry younger than <paramref name="ttlMs"/> — i.e. re-fetching it would be
    /// waste. An entry stamped in the future (clock skew) counts as fresh rather than triggering a refetch storm.</summary>
    public bool IsFresh(string key, long nowMs, long ttlMs)
        => Lookup(key) is { } e && nowMs - e.FetchedAtMs < ttlMs;

    /// <summary>Records (or replaces) one fetched state.</summary>
    public void Set(string key, IssueState state)
    {
        if (string.IsNullOrEmpty(key) || state is null) return;
        Entries ??= new Dictionary<string, IssueState>(StringComparer.Ordinal);
        Entries[key] = state;
    }
}
