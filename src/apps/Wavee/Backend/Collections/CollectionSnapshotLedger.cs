using System;
using System.Collections.Generic;

namespace Wavee.Backend.Collections;

// ── The verified-snapshot ledger ──────────────────────────────────────────────────────────────────────────────────────
// A full collection2v2 page walk is the ONLY thing that may mark-and-sweep a library set or advance its sync token, and
// both are catastrophic on a walk that silently lost its tail: a truncated snapshot deletes the newest members and then
// stores the CURRENT server revision, so no later delta ever re-ships them (the 274-vs-293 Liked Songs drift — the
// missing 19 were exactly the newest likes). collection2v2 has no count endpoint, so the walk has to prove its own
// completeness. The ledger records what every page said and turns that into one Verdict:
//   NoTerminal — the loop ended without a page whose next_page_token was empty (an exception mid-walk lands here too);
//   NoToken    — no page carried a sync token, so there is nothing safe to store;
//   Overlap    — a uri appeared on two pages: the cursor shifted under us (an add/remove mid-walk), so the walk may
//                equally have SKIPPED a uri — the sweep cannot be trusted;
//   Verified   — terminal seen, a token in hand, no overlap.
// Token is the EARLIEST sync token the walk saw (the first page's, in practice — TokenPage says which): the next delta
// then re-ships anything that changed while later pages were walking; re-applying is idempotent through the store's
// SetSavedCore no-op elision, so "at-or-before the walk" is the conservative cursor, never "at the end of it".
public enum SnapshotVerdict : byte { NoTerminal, NoToken, Overlap, Verified }

public sealed class CollectionSnapshotLedger
{
    readonly HashSet<string> _uris = new(StringComparer.Ordinal);
    readonly List<CollectionItem> _items = new();

    public CollectionSnapshotLedger(string wireSet, long startedAtMs)
    {
        WireSet = wireSet;
        StartedAtMs = startedAtMs;
    }

    /// <summary>The wire set the walk paged ("collection"/"artist"/"show"/"listenlater").</summary>
    public string WireSet { get; }
    /// <summary>Unix ms when the first page was requested — the recency shield's reference clock (a member added within
    /// <see cref="CollectionSweepPolicy.RecencyShieldMs"/> of this instant is never swept).</summary>
    public long StartedAtMs { get; }
    public int Pages { get; private set; }
    /// <summary>Live uris seen on more than one page — a cursor shift, which is why any value above zero is
    /// <see cref="SnapshotVerdict.Overlap"/>.</summary>
    public int Duplicates { get; private set; }
    /// <summary>Pages that carried zero live items yet promised a next page. Not a verdict by itself (a cursor that steps
    /// over a run of deletions is a legal server shape) but always worth a diagnostic line.</summary>
    public int EmptyNonTerminalPages { get; private set; }
    public bool TerminalSeen { get; private set; }
    /// <summary>The sync token to store for this walk — the earliest one seen; null when no page carried one.</summary>
    public string? Token { get; private set; }
    /// <summary>1-based page the token came from (0 = none). "first" is the normal case; a later page means the server
    /// stamped the token late and the cursor is correspondingly later.</summary>
    public int TokenPage { get; private set; }
    public string TokenSource => TokenPage == 0 ? "none" : TokenPage == 1 ? "first" : "page" + TokenPage;
    /// <summary>Distinct live uris across every page.</summary>
    public int ItemCount => _uris.Count;
    /// <summary>Every live item, first occurrence wins (a duplicate's timestamp is discarded with it).</summary>
    public IReadOnlyList<CollectionItem> Items => _items;

    /// <summary>Record one page: its items, whether it was terminal (empty <paramref name="nextPageToken"/>) and the
    /// sync token it carried (null/empty = none).</summary>
    public void AddPage(IReadOnlyList<CollectionItem> items, string? nextPageToken, string? syncToken)
    {
        if (TerminalSeen) throw new InvalidOperationException("A page was added after the terminal page of the walk.");
        Pages++;
        int live = 0;
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (it.Removed) continue;   // a snapshot page never ships removals; a delta would, and deltas do not walk
            live++;
            if (!_uris.Add(it.Uri)) { Duplicates++; continue; }
            _items.Add(it);
        }
        bool terminal = string.IsNullOrEmpty(nextPageToken);
        if (terminal) TerminalSeen = true;
        else if (live == 0) EmptyNonTerminalPages++;
        if (Token is null && !string.IsNullOrEmpty(syncToken)) { Token = syncToken; TokenPage = Pages; }
    }

    public SnapshotVerdict Verdict =>
        !TerminalSeen ? SnapshotVerdict.NoTerminal
        : Token is null ? SnapshotVerdict.NoToken
        : Duplicates > 0 ? SnapshotVerdict.Overlap
        : SnapshotVerdict.Verified;

    public bool IsVerified => Verdict == SnapshotVerdict.Verified;

    public bool Contains(string uri) => _uris.Contains(uri);

    /// <summary>The walked uris that belong to one LOGICAL set: those starting with <paramref name="prefix"/>
    /// (<see cref="CollectionSets.UriPrefix"/>), or every uri when the set has no prefix (artists/shows/episodes).</summary>
    public HashSet<string> UrisFor(string? prefix)
    {
        if (prefix is null) return new HashSet<string>(_uris, StringComparer.Ordinal);
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var uri in _uris)
            if (uri.StartsWith(prefix, StringComparison.Ordinal)) set.Add(uri);
        return set;
    }
}
