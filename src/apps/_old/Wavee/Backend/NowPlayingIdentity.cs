using System;
using Wavee.Core;

namespace Wavee.Backend;

// ── The now-playing IDENTITY tripwire (#139) ─────────────────────────────────────────────────────────────────────────
// A pure shape check of the row the projection is about to PUBLISH, behind the always-on `[playback] nowplaying.identity`
// line (NowPlayingProjection.FireChanges logs it once per (uri, shape), naming who last wrote the row). IDENTITY ONLY:
// nothing here changes a title. The "LP / LP" and "Damiano David / Damiano David" reports were the player bar's
// reconciliation — its unkeyed title/artist marquees reused across the inserted "Playing on …" line (fixed with keys in
// PlayerBar.cs) — not the data: every published row in those incidents carried the right title. This stays as the
// data-side proof, for the next report, of whether the repeat is in the published row or only on screen. A legitimately
// self-titled track ("Weezer" by Weezer) trips TitleEqualsArtist once and is left exactly as it is — the heuristic that
// blanked such titles is precisely what must never come back.

/// <summary>What looks wrong about a published now-playing row, most severe first.</summary>
public enum IdentitySuspicion
{
    None,
    /// <summary>No title at all (null / whitespace) — the bar paints a blank line or falls back to something else.</summary>
    TitleEmpty,
    /// <summary>The title IS the uri — the placeholder thin writers seed before real metadata resolves.</summary>
    TitleIsUri,
    /// <summary>The title equals one of the artist names (case- and edge-whitespace-insensitive) — "LP / LP".</summary>
    TitleEqualsArtist,
    /// <summary>An artist with no name — a cluster row carrying <c>artist_uri</c> but no <c>artist_name</c>, not yet
    /// enriched. (No artists at all is not suspicious: a podcast episode has a show, not artists.)</summary>
    ArtistsUnnamed,
}

public static class NowPlayingIdentity
{
    /// <summary>The first (most severe) suspicious shape of <paramref name="t"/>, or <see cref="IdentitySuspicion.None"/>.
    /// Pure and allocation-free: it runs on every publish.</summary>
    public static IdentitySuspicion Suspicion(Track? t)
    {
        if (t is null) return IdentitySuspicion.None;
        if (string.IsNullOrWhiteSpace(t.Title)) return IdentitySuspicion.TitleEmpty;
        if (string.Equals(t.Title, t.Uri, StringComparison.Ordinal)) return IdentitySuspicion.TitleIsUri;
        ReadOnlySpan<char> title = t.Title.AsSpan().Trim();
        bool unnamed = false;
        for (int i = 0; i < t.Artists.Count; i++)
        {
            ReadOnlySpan<char> name = (t.Artists[i].Name ?? "").AsSpan().Trim();
            if (name.IsEmpty) { unnamed = true; continue; }
            if (title.Equals(name, StringComparison.OrdinalIgnoreCase)) return IdentitySuspicion.TitleEqualsArtist;
        }
        return unnamed ? IdentitySuspicion.ArtistsUnnamed : IdentitySuspicion.None;
    }
}
