using System;
using System.Collections.Generic;
using Wavee.Core;

namespace Wavee;

/// <summary>
/// WHICH playlists a track can be filed into, and in WHAT ORDER — the one answer, shared by the context-menu
/// "Add to playlist ▸" submenu, the "Move to playlist ▸" submenu and the playlist picker flyout. Engine-free by
/// construction (System + Wavee.Core only, no FluentGpu, no <c>IAppSettings</c>) so the rules are unit-tested without a
/// GPU, a window, or a settings store — the <c>PlaylistReorderRules</c> / <c>WaveeTipsCore</c> precedent.
///
/// <para><b>Why this exists as one function.</b> The eligibility test was written out THREE times
/// (<c>PlaylistPicker.IsRealPlaylist</c>, <c>Menus.PlaylistDepositItem</c>, <c>TabDropRules.IsDepositablePlaylistUri</c>)
/// and the last one's comment already warned that changing one means changing all three. And the ORDER was rootlist
/// order truncated to the first ten — which for anyone with more than ten playlists is the same ten forever, very often
/// not including the one they are reaching for. Recency fixes that: the playlists you actually file into rise to the top,
/// so the common case is one hop instead of "More playlists… → search".</para>
/// </summary>
static class PlaylistDepositTargets
{
    /// <summary>How many playlists a context-menu submenu lists inline before deferring to the picker. Ten is about as
    /// many rows as a submenu can carry before it stops being scannable; the rest are reachable through
    /// "More playlists…", which opens the searchable picker over the SAME ordering.</summary>
    public const int MaxInline = 10;

    /// <summary>How many recent deposit targets are remembered. Eight covers the "few playlists I'm actively curating"
    /// working set without the tail going stale enough to be misleading.</summary>
    public const int MaxRecent = 8;

    /// <summary>Is this a real, writable Spotify playlist uri? The ONE eligibility predicate. Excludes pseudo-playlists
    /// (Liked Songs, a daylist route, a local <c>wavee:playlist:</c> from the offline source) — a deposit against those
    /// has nowhere to land.</summary>
    public static bool IsDepositable(string? uri)
        // Kind + provider through the ONE parser (hydration-facade-design.md §1.1): a Spotify PLAYLIST, which excludes
        // Liked (Collection), a route key (Unknown) and the offline `wavee:playlist:*` source (EntityProviders.User).
        => uri is { Length: > 0 } && EntityUri.Parse(uri) is { IsSpotify: true, Kind: EntityKind.Playlist };

    /// <summary>Can the user write items into <paramref name="p"/>? Owned or collaborative — <c>CanEdit</c> already folds
    /// <c>CanEditItems || IsOwner</c> at the projection (StoreLibrarySource), so a followed editorial playlist is out.</summary>
    public static bool IsEligible(in PlaylistSummary p, string? excludeUri = null)
        => IsDepositable(p.Uri)
           && p.CanEdit
           && !(excludeUri is { Length: > 0 } && string.Equals(p.Uri, excludeUri, StringComparison.Ordinal));

    /// <summary>The eligible playlists, MOST-RECENTLY-DEPOSITED FIRST, then rootlist order for everything else.
    /// <para><paramref name="recentUris"/> is the MRU list newest-first (see <see cref="Remember"/>). A remembered uri
    /// that is no longer eligible (unfollowed, permissions changed, not loaded yet) is simply skipped — the MRU is a
    /// preference, never an assertion that the playlist still exists. <paramref name="query"/> is an
    /// ordinal-case-insensitive substring filter over the name; empty means no filter. Stable: two calls with the same
    /// inputs produce the same order, so a submenu does not reshuffle under the pointer.</para></summary>
    public static List<PlaylistSummary> Order(
        IReadOnlyList<PlaylistSummary>? playlists,
        IReadOnlyList<string>? recentUris = null,
        string? excludeUri = null,
        string? query = null)
    {
        var ordered = new List<PlaylistSummary>(playlists?.Count ?? 0);
        if (playlists is not { Count: > 0 }) return ordered;

        // Pass 1 — the MRU, in remembered order. Linear scans: MaxRecent is 8 and a rootlist is hundreds at most, so this
        // is bounded work on a cold path (opening a menu), and it keeps the type free of any hashing/collection ceremony.
        if (recentUris is { Count: > 0 })
        {
            for (int r = 0; r < recentUris.Count; r++)
            {
                string uri = recentUris[r];
                if (!IsDepositable(uri)) continue;
                for (int i = 0; i < playlists.Count; i++)
                {
                    var p = playlists[i];
                    if (!string.Equals(p.Uri, uri, StringComparison.Ordinal)) continue;
                    if (IsEligible(in p, excludeUri) && Matches(p.Name, query) && !AlreadyOrdered(ordered, p.Uri))
                        ordered.Add(p);
                    break;
                }
            }
        }

        // Pass 2 — everything else in rootlist order (the order the sidebar shows, which is the user's own arrangement).
        for (int i = 0; i < playlists.Count; i++)
        {
            var p = playlists[i];
            if (!IsEligible(in p, excludeUri) || !Matches(p.Name, query)) continue;
            if (!AlreadyOrdered(ordered, p.Uri)) ordered.Add(p);
        }
        return ordered;
    }

    /// <summary>The MRU with <paramref name="uri"/> promoted to the front, deduped and capped at
    /// <see cref="MaxRecent"/>. Returns the input unchanged when there is nothing to record, so a caller can compare and
    /// skip the write.</summary>
    public static List<string> Remember(IReadOnlyList<string>? recentUris, string? uri)
    {
        var next = new List<string>(MaxRecent);
        if (IsDepositable(uri)) next.Add(uri!);
        if (recentUris is { Count: > 0 })
            for (int i = 0; i < recentUris.Count && next.Count < MaxRecent; i++)
            {
                string u = recentUris[i];
                if (!IsDepositable(u)) continue;
                bool dupe = false;
                for (int j = 0; j < next.Count; j++)
                    if (string.Equals(next[j], u, StringComparison.Ordinal)) { dupe = true; break; }
                if (!dupe) next.Add(u);
            }
        return next;
    }

    /// <summary>The MRU codec — newline-joined, exactly like <c>WaveeTipsCore</c> (the settings store round-trips scalars
    /// only, and a newline cannot occur in a Spotify uri). Empty segments are dropped on read and never written.</summary>
    public static List<string> Parse(string? stored)
    {
        var uris = new List<string>();
        if (string.IsNullOrEmpty(stored)) return uris;
        int i = 0;
        while (i <= stored.Length)
        {
            int end = stored.IndexOf('\n', i);
            if (end < 0) end = stored.Length;
            if (end > i) uris.Add(stored[i..end]);
            i = end + 1;
        }
        return uris;
    }

    public static string Serialize(IReadOnlyList<string>? uris)
    {
        if (uris is not { Count: > 0 }) return "";
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < uris.Count; i++)
        {
            if (!IsDepositable(uris[i])) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(uris[i]);
        }
        return sb.ToString();
    }

    /// <summary>The next unused "<c>{base} #N</c>" name, N counting from 1 — the numbering the user expects from a
    /// one-click "New playlist" (Spotify's "My Playlist #6" shape). Deliberately a first-unused SEARCH rather than
    /// max-suffix+1 and never a regex: it needs no parsing of existing names, so it is culture-safe for any localized
    /// base, and it reuses a gap left by a deleted playlist instead of climbing forever.</summary>
    public static string NextDefaultName(IReadOnlyList<PlaylistSummary>? playlists, string baseName)
    {
        if (string.IsNullOrWhiteSpace(baseName)) baseName = "Playlist";
        // Bounded by the playlist count: with N playlists at most N candidates can be taken, so N+1 always terminates.
        int limit = (playlists?.Count ?? 0) + 1;
        for (int n = 1; n <= limit; n++)
        {
            string candidate = baseName + " #" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!NameTaken(playlists, candidate)) return candidate;
        }
        return baseName + " #" + limit.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    static bool NameTaken(IReadOnlyList<PlaylistSummary>? playlists, string name)
    {
        if (playlists is not { Count: > 0 }) return false;
        for (int i = 0; i < playlists.Count; i++)
            // Case-insensitive: "my playlist #2" and "My Playlist #2" would read as the same name in the sidebar, and
            // handing the user a second one is the confusion this exists to avoid.
            if (string.Equals(playlists[i].Name, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static bool Matches(string? name, string? query)
        => string.IsNullOrEmpty(query)
           || (name is not null && name.Contains(query, StringComparison.OrdinalIgnoreCase));

    static bool AlreadyOrdered(List<PlaylistSummary> ordered, string uri)
    {
        for (int i = 0; i < ordered.Count; i++)
            if (string.Equals(ordered[i].Uri, uri, StringComparison.Ordinal)) return true;
        return false;
    }
}
