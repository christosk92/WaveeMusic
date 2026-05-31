using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.UI.Contracts;
using Wavee.UI.Models;

namespace Wavee.UI.Services.Playlists;

public enum SwipeDirection { Left, Right }      // Left = Remove, Right = Keep
public enum SwipeDecision { Keep, Remove }      // stored decisions only — "undecided" = absent
public enum RefreshPhase { Auditioning, Review, Applying, Done, Empty }

/// <summary>One unique song in the refresh deck.</summary>
public sealed record RefreshCard(
    string Uri, string Title, string ArtistName,
    string? ImageUrl, string? ImageSmallUrl, TimeSpan Duration);

/// <summary>An audited card plus its decision (<c>null</c> = skipped) — one row of the "Previously" rail.</summary>
public sealed record RefreshHistoryEntry(RefreshCard Card, SwipeDecision? Decision);

/// <summary>What changed upstream between the saved snapshot and the current playlist.</summary>
public readonly record struct RefreshDiffSummary(int Added, int Removed)
{
    public bool HasChanges => Added > 0 || Removed > 0;
}

public readonly record struct RefreshApplyResult(bool Success, int RemovedCount, string? Error);

/// <summary>Serialisable view of a session — what the durable store persists.</summary>
public sealed record RefreshSessionState(
    string PlaylistId,
    string? BaseRevision,
    IReadOnlyList<string> SnapshotUris,
    IReadOnlyDictionary<string, SwipeDecision> Decisions);

/// <summary>
/// Framework-neutral state machine for the "Refresh with swipes" session. Holds a
/// URI-deduped deck (current playlist order), per-URI keep/remove decisions, and a cursor
/// that walks only the <em>undecided</em> cards. Removals commit exactly once, in
/// <see cref="ApplyAsync"/> (batched semantics). Pure — no I/O, no UI; fully unit-testable.
/// </summary>
public sealed class RefreshPlaylistSession
{
    private readonly IPlaylistMutationService _mutation;
    private readonly List<RefreshCard> _deck = new();
    private readonly Dictionary<string, SwipeDecision> _decisions = new(StringComparer.Ordinal);
    private readonly List<int> _history = new();        // deck indices visited (decide/skip), for undo
    private int _cursor;

    private RefreshPlaylistSession(
        string playlistId,
        IReadOnlyList<PlaylistTrackDto> currentTracks,
        string? baseRevision,
        IReadOnlyDictionary<string, SwipeDecision>? seeded,
        RefreshDiffSummary diff,
        IPlaylistMutationService mutation)
    {
        PlaylistId = playlistId;
        BaseRevision = baseRevision;
        LastDiff = diff;
        _mutation = mutation;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in currentTracks)
        {
            if (string.IsNullOrEmpty(t.Uri) || !seen.Add(t.Uri)) continue;
            _deck.Add(new RefreshCard(t.Uri, t.Title, t.ArtistName, t.ImageUrl, t.ImageSmallUrl, t.Duration));
        }

        if (seeded is not null)
            foreach (var card in _deck)
                if (seeded.TryGetValue(card.Uri, out var d))
                    _decisions[card.Uri] = d;

        if (_deck.Count == 0)
        {
            Phase = RefreshPhase.Empty;
            return;
        }

        AdvanceCursorToUndecided();
        Phase = _cursor >= _deck.Count ? RefreshPhase.Review : RefreshPhase.Auditioning;
    }

    /// <summary>Begin a fresh session over the playlist's current tracks.</summary>
    public static RefreshPlaylistSession Start(
        string playlistId, IReadOnlyList<PlaylistTrackDto> currentTracks,
        string? baseRevision, IPlaylistMutationService mutation)
        => new(playlistId, currentTracks, baseRevision, seeded: null, default, mutation);

    /// <summary>
    /// Resume a saved session, reconciling against the playlist's <em>current</em> tracks:
    /// decisions survive by URI, decisions for vanished tracks are dropped, newly-added tracks
    /// become undecided (auditioned), and reorders are followed automatically. <see cref="LastDiff"/>
    /// reports what changed upstream.
    /// </summary>
    public static RefreshPlaylistSession Resume(
        IReadOnlyList<PlaylistTrackDto> currentTracks, string? baseRevision,
        RefreshSessionState saved, IPlaylistMutationService mutation)
    {
        var currentUris = new HashSet<string>(currentTracks.Select(t => t.Uri).Where(u => !string.IsNullOrEmpty(u)), StringComparer.Ordinal);
        var savedUris = new HashSet<string>(saved.SnapshotUris, StringComparer.Ordinal);
        var added = currentUris.Count(u => !savedUris.Contains(u));
        var removed = savedUris.Count(u => !currentUris.Contains(u));
        return new(saved.PlaylistId, currentTracks, baseRevision, saved.Decisions, new RefreshDiffSummary(added, removed), mutation);
    }

    public string PlaylistId { get; }
    public string? BaseRevision { get; }
    public RefreshPhase Phase { get; private set; }
    public RefreshDiffSummary LastDiff { get; }
    public IReadOnlyList<RefreshCard> Deck => _deck;

    /// <summary>Cursor position in the deck (the current card's index), or <c>Deck.Count</c> at the end.</summary>
    public int CurrentIndex => _cursor;
    public RefreshCard? CurrentCard => _cursor >= 0 && _cursor < _deck.Count ? _deck[_cursor] : null;

    public int RemovedCount => _decisions.Count(kv => kv.Value == SwipeDecision.Remove);
    public int KeptCount => _deck.Count - RemovedCount;          // everything not removed stays (kept + skipped)
    public int DecidedCount => _decisions.Count;
    /// <summary>Undecided cards still ahead of the cursor — the "N left" count.</summary>
    public int RemainingCount
    {
        get
        {
            var n = 0;
            for (var i = _cursor; i < _deck.Count; i++)
                if (!_decisions.ContainsKey(_deck[i].Uri)) n++;
            return n;
        }
    }
    public IReadOnlyList<RefreshCard> RemovedCards =>
        _deck.Where(c => _decisions.TryGetValue(c.Uri, out var d) && d == SwipeDecision.Remove).ToList();
    public IReadOnlyList<RefreshCard> UpNext(int count) =>
        _deck.Skip(_cursor + 1).Where(c => !_decisions.ContainsKey(c.Uri)).Take(count).ToList();

    /// <summary>Cards already audited this session, most-recent first, with how they were decided
    /// (<c>null</c> = skipped). Drives the "Previously" rail.</summary>
    public IReadOnlyList<RefreshHistoryEntry> Previous(int count)
    {
        var list = new List<RefreshHistoryEntry>(Math.Min(count, _history.Count));
        for (var i = _history.Count - 1; i >= 0 && list.Count < count; i--)
        {
            var card = _deck[_history[i]];
            SwipeDecision? decision = _decisions.TryGetValue(card.Uri, out var d) ? d : null;
            list.Add(new RefreshHistoryEntry(card, decision));
        }
        return list;
    }
    public bool HasStagedDecisions => _decisions.Count > 0;

    public event EventHandler? StateChanged;
    private void Raise() => StateChanged?.Invoke(this, EventArgs.Empty);

    private void AdvanceCursorToUndecided()
    {
        while (_cursor < _deck.Count && _decisions.ContainsKey(_deck[_cursor].Uri))
            _cursor++;
    }

    public void Decide(SwipeDirection direction)
    {
        if (Phase != RefreshPhase.Auditioning || CurrentCard is not { } card) return;
        _decisions[card.Uri] = direction == SwipeDirection.Right ? SwipeDecision.Keep : SwipeDecision.Remove;
        _history.Add(_cursor);
        _cursor++;
        AdvanceCursorToUndecided();
        if (_cursor >= _deck.Count) Phase = RefreshPhase.Review;
        Raise();
    }

    /// <summary>Advance past the current card without deciding — it stays (kept), revisitable on a later resume.</summary>
    public void Skip()
    {
        if (Phase != RefreshPhase.Auditioning || CurrentCard is null) return;
        _history.Add(_cursor);
        _cursor++;
        AdvanceCursorToUndecided();
        if (_cursor >= _deck.Count) Phase = RefreshPhase.Review;
        Raise();
    }

    public bool UndoLast()
    {
        if (_history.Count == 0) return false;
        var idx = _history[^1];
        _history.RemoveAt(_history.Count - 1);
        _decisions.Remove(_deck[idx].Uri);          // no-op for a skip
        _cursor = idx;
        if (Phase is RefreshPhase.Review) Phase = RefreshPhase.Auditioning;
        Raise();
        return true;
    }

    /// <summary>In Review, flip a removed card back to kept.</summary>
    public void UnRemove(string uri)
    {
        if (_decisions.TryGetValue(uri, out var d) && d == SwipeDecision.Remove)
        {
            _decisions[uri] = SwipeDecision.Keep;
            Raise();
        }
    }

    /// <summary>End auditioning early — jump straight to Review with the current decisions.</summary>
    public void Finish()
    {
        if (Phase == RefreshPhase.Auditioning) { Phase = RefreshPhase.Review; Raise(); }
    }

    public void Restart()
    {
        _decisions.Clear();
        _history.Clear();
        _cursor = 0;
        Phase = _deck.Count == 0 ? RefreshPhase.Empty : RefreshPhase.Auditioning;
        Raise();
    }

    public RefreshSessionState Snapshot() => new(
        PlaylistId, BaseRevision,
        _deck.Select(c => c.Uri).ToList(),
        new Dictionary<string, SwipeDecision>(_decisions, StringComparer.Ordinal));

    /// <summary>
    /// Commits removals — the only place the mutation service is called. URI-keyed
    /// (<c>items_as_key=true</c>), so it's order-independent and a no-op for any track that
    /// already vanished upstream. On failure the session returns to Review for retry.
    /// </summary>
    public async Task<RefreshApplyResult> ApplyAsync(CancellationToken ct = default)
    {
        var removed = RemovedCards.Select(c => c.Uri).ToList();
        Phase = RefreshPhase.Applying; Raise();
        try
        {
            if (removed.Count > 0)
                await _mutation.RemoveTracksFromPlaylistAsync(PlaylistId, removed, ct).ConfigureAwait(false);
            Phase = RefreshPhase.Done; Raise();
            return new RefreshApplyResult(true, removed.Count, null);
        }
        catch (Exception ex)
        {
            Phase = RefreshPhase.Review; Raise();
            return new RefreshApplyResult(false, removed.Count, ex.Message);
        }
    }
}
