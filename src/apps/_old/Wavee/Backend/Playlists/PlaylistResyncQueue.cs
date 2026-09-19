using System;
using System.Collections.Generic;

namespace Wavee.Backend.Playlists;

/// <summary>Invariant I4 — "never advance a revision past ops you did not apply", carried across the drain boundary.
/// <para>A playlist <c>/changes</c> 200 can come back saying the accepted delta cannot be folded into our baseline:
/// <c>multiple_heads</c>, <c>changes_require_resync</c>, or a <c>sync_result</c> whose ops tear against the local list.
/// The replay strategy runs INSIDE <see cref="MutationEngine.Drain"/> and must not advance the stored revision in that
/// case — but it also has no business fetching, and the sync loop is the single writer for network reads. So it drops
/// the uri here and the loop revalidates it right after the drain.</para>
/// <para>ONE instance is shared by <c>OpRebaseStrategy</c> and <c>LibrarySync</c> at the composition root — a required
/// ctor dependency on both, never an optional/nullable one (an unwired queue would silently lose convergence).</para></summary>
public sealed class PlaylistResyncQueue
{
    readonly object _gate = new();
    readonly HashSet<string> _uris = new(StringComparer.Ordinal);

    public void Mark(string playlistUri)
    {
        if (string.IsNullOrEmpty(playlistUri)) return;
        lock (_gate) _uris.Add(playlistUri);
    }

    /// <summary>Take and clear every marked uri. Empty is the common case (one allocation-free early-out).</summary>
    public IReadOnlyList<string> TakeAll()
    {
        lock (_gate)
        {
            if (_uris.Count == 0) return Array.Empty<string>();
            var all = new List<string>(_uris);
            _uris.Clear();
            return all;
        }
    }
}
