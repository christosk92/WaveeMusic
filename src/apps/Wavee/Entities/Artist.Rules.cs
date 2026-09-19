// ── Entities/Artist.Rules.cs — CORE: the artist page's ported pure rules and its commit-time derivations ─────────────
//
// Role: CORE (the rules are engine-free with pure overloads; §6 is the commit half — UI thread, inside the drain)
// Owner: N (stream N-B)
// Wave: 5
// Budget: the named partial of Artist.cs (plan §2 · Artist.cs 550, split because the file passed 715 — WP-5.N contract §0)
// Spec: ch 08 §7 (readiness), §8 (FirstSentence/StripHtml, ArtistPopularTracks, KindMatches); ch 31 §8 (TourBannerFor,
//       KindMatches, TopTracksOf); WP-5.N contract §4, §6
//
// FIVE RULES, one class each, every one a DECISION rather than a rendering:
//
//   ArtistReadiness      what the page may paint yet — the overview unit, the chart gate (the owner's "no rows without
//                        play counts"), a shelf's state.
//   ArtistText           the hero's one-sentence bio, verbatim from ArtistPage.Hero.cs:262-282, plus the UTF-8 twin the
//                        decoder runs ONCE into `BioLead` (P11) so the hero never strips HTML on a render.
//   ArtistPopularTracks  the chart's ORDER contract (seed keeps its order and counts at the head, extension-only tracks
//                        append, uri dedupe with the seed winning, cap 50) over slots.
//   ArtistTour           the tour-banner ladder (FakeData.TourBannerFor): 1 date → a show, 2-3 → dates, ≥ 4 with the
//                        next within 7 days → on tour now, else an upcoming tour.
//   ArtistCatalog        the one facet ↔ kind filter (AggregateCatalog.KindMatches: Singles ⇒ Single OR EP), the wire's
//                        release-type word, and the external-link glyph classifier.
//
// AND THE THREE COMMIT-TIME DERIVATIONS (§6), which live beside their rules rather than in Artist.cs (it passed 715):
//
//   CommitPopular        the staged seed/extension chart lists merged against each other AND the committed chart, landed
//                        as one appended `Relation.ArtistPopular` run — so it rides after any legacy overview run.
//   CommitArtistExtras   the six payload relations (gallery, playlists, videos, merch, cities, links): retain the
//                        incoming text, release the list being replaced, `Replace` Complete (empty included).
//   Artist.DeriveTour    the tour banner off the concert edge, loc-keyed, called by the concert commit and the seed.

using System.Globalization;
using FluentGpu.Foundation;
using FluentGpu.Localization;

namespace Wavee;

// ══ 1. READINESS ═════════════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>What the artist page may paint yet (ch 08 §7's readiness column). A predicate that is false renders the
/// derived skeleton, never a half-filled row.</summary>
public static class ArtistReadiness
{
    /// <summary>The overview renders as a UNIT: stats, bio, pick, upcoming, latest and tour, or none of them.</summary>
    public static bool Overview(Artist a) => a.IsValid && a.Knows(ArtistFields.Overview);

    /// <summary>THE CHART GATE: the chart transport answered (<see cref="ArtistFields.Chart"/>), the popular edge is
    /// complete, and every row has the fields the chart paints. TrackV4 carries the title, cover, duration and availability; play
    /// counts arrive through a separate extension and are allowed to fill into an already visible row. Holding the
    /// complete chart behind that unrelated extension made small artwork wait behind a second network round trip.</summary>
    public static bool Chart(Artist a)
    {
        if (!a.IsValid) return false;
        var edges = Entities.Current.Edges.ArtistPopular;
        var tracks = Entities.Current.Tracks;
        var targets = edges.Targets(a.Slot);
        if (!Chart(a.Knows(ArtistFields.Chart), edges.State(a.Slot))) return false;
        for (int i = 0; i < targets.Length; i++)
            if (targets[i] <= Table.None || !tracks.Knows(targets[i], (uint)TrackFields.Row)) return false;
        return true;
    }

    /// <summary>The chart gate's edge half, pure: answered AND complete.</summary>
    public static bool Chart(bool knowsChart, EdgeState popularState) => knowsChart && popularState == EdgeState.Complete;

    /// <summary>The chart gate over already-read known bits (the pure form the tests pin).</summary>
    public static bool Chart(bool knowsChart, EdgeState popularState, ReadOnlySpan<uint> targetKnown)
    {
        if (!Chart(knowsChart, popularState)) return false;
        for (int i = 0; i < targetKnown.Length; i++)
            if ((targetKnown[i] & (uint)TrackFields.Row) != (uint)TrackFields.Row) return false;
        return true;
    }

    /// <summary>THE CHART'S FAILURE GATE (the twin of <see cref="Chart(bool,EdgeState,ReadOnlySpan{uint})"/>, pure over
    /// the four marks): has the chart stopped coming? True when the shimmer must become the Retry vacancy, because
    /// nothing in flight can still make <see cref="Chart(Artist)"/> true. Search.Page.cs's four-way readiness, applied
    /// to the three things the chart waits on:
    /// <list type="bullet">
    /// <item>the popular EDGE: <see cref="EdgeState.Failed"/> (Unknown and the last ask failed, or answered with no
    /// list) ⇒ failed;</item>
    /// <item>the artist's <see cref="ArtistFields.Chart"/> group: known ⇒ fine; not known and no longer asked (the
    /// planner un-asked it: a 401 on the top-tracks REST beside a 200 overview, or a terminal failure) ⇒ failed; asked
    /// with nothing in flight (the batch settled without it — a 404) ⇒ failed; asked and in flight ⇒ still pending;</item>
    /// <item>every target's <see cref="TrackFields.Row"/>: the same reading, per row. A target whose missing groups
    /// are still asked and in flight keeps the chart pending; one nothing is coming for fails it.</item>
    /// </list>
    /// The caller passes the target marks only once IT has asked for the targets — before that, "not asked" means
    /// "not asked yet", not "un-asked" (Search's <c>askedByPage</c>). Rows have no Failed state of their own (the
    /// 2026-09-16 defect: the chart's <c>_failed</c> read only the edge, so a refused top-tracks route left
    /// <c>_pending</c> true for the life of the page); this rule IS that state, derived from the marks.</summary>
    public static bool ChartFailed(bool knowsChart, uint artistAsked, uint artistInflight, EdgeState popularReadiness,
                                   ReadOnlySpan<uint> targetKnown, ReadOnlySpan<uint> targetAsked, ReadOnlySpan<uint> targetInflight)
    {
        if (popularReadiness == EdgeState.Failed) return true;
        uint chart = (uint)ArtistFields.Chart;
        if (NothingComing(knowsChart ? chart : 0u, artistAsked, artistInflight, chart)) return true;
        uint row = (uint)TrackFields.Row;
        for (int i = 0; i < targetKnown.Length && i < targetAsked.Length && i < targetInflight.Length; i++)
            if (NothingComing(targetKnown[i], targetAsked[i], targetInflight[i], row)) return true;
        return false;
    }

    /// <summary>Is a group of one row past hope? Nothing missing ⇒ no (it is known). Otherwise something is still coming
    /// only while EVERY missing bit is asked AND a request for the row is out; a missing bit nobody asks for any more
    /// (un-asked by a failure) or a row whose request settled without it (asked, <c>Inflight == 0</c>) has concluded.</summary>
    static bool NothingComing(uint known, uint asked, uint inflight, uint group)
    {
        uint missing = group & ~known;
        if (missing == 0) return false;
        return (asked & missing) != missing || inflight == 0;
    }

    /// <summary>A shelf's state: the edge's <see cref="EdgeTableBase.Readiness"/> — Unknown ⇒ skeleton, Failed ⇒ the
    /// Retry vacancy, Complete ⇒ content, and a Complete list with 0 rows IS Complete (the section is simply absent).</summary>
    public static EdgeState Shelf(EdgeTableBase edge, int parent) => edge.Readiness(parent);

    /// <summary>Is the section present at all? Complete-and-empty is absent; Unknown/Failed/Partial are present (they
    /// render their skeleton, vacancy or first page).</summary>
    public static bool ShelfPresent(EdgeState readiness, int count) => readiness != EdgeState.Complete || count > 0;

    /// <summary>Bug F (2026-09-15 handoff §4), restored to 0.2.10's model after a same-day regression: the WHOLE
    /// artist page — hero photo, hero text (name, verified badge, bio lead, stats), the magazine and the chart — is
    /// ONE reveal unit, pending until the OVERVIEW is known (Artist.Rules.cs:43's "renders as a UNIT" contract). There
    /// is no separate hero gate any more: an intermediate fix OR'd in the hero's own latched-art usability
    /// (<c>Detail.CoverLatch.IsUsable(_heroUrl)</c>) so the hero photo would show before the rest of the page, but
    /// that let the hero's TEXT (verified/bio/stats, all Overview-sourced) render with whatever was known at that
    /// moment and then pop in piecemeal as the live answer landed — the exact "renders as a UNIT" violation this bug
    /// is about, just moved inside the hero instead of below it. `Artist.Page.cs`'s `_bodyReady` is this predicate's
    /// negation directly (<c>_bodyReady = _ready = Overview(a)</c>); this is the PURE form a test pins against it,
    /// and the name is kept from the (now removed) magazine-only version of this fix for continuity with the
    /// 2026-09-15 handoff's own wording.</summary>
    public static bool MagazinePending(Artist a) => !Overview(a);
}

/// <summary>Bug D (2026-09-15 handoff §5): the discography demand plan, pure. The artist page's tabs are a
/// scroll-spy over three sections stacked inline (Albums, Singles, Compilations are simultaneously on screen the
/// moment the page mounts, not a view switch), so every facet's FIRST page asks at the same
/// <see cref="FetchPriority"/> regardless of facet identity. A prior revision demoted Singles/Compilations to
/// <see cref="FetchPriority.Prefetch"/>; because re-asking an already-asked entry at a HIGHER priority is a no-op
/// (Fetch's `WasAsked` dedup, its `~Asked` mask, the bucket key packing priority into its shape word, and
/// `s_active` carrying no priority the runner re-reads), the facet the user was actually looking at stayed
/// Prefetch forever. See <c>Artist.Discography.cs</c>'s <c>DemandDiscography</c>, the only caller.</summary>
public static class DiscographyRules
{
    /// <summary>Every facet's page-ONE priority. Uniformly <see cref="FetchPriority.Visible"/> — no facet-identity
    /// demotion (bug D). Page-2+ paging (<c>DemandNextPage</c>) is a SEPARATE, scroll-paced decision and keeps its
    /// own priority argument; this rule covers only the first page every facet asks for on demand.</summary>
    public static FetchPriority FirstPagePriority(DiscoFacet facet) => FetchPriority.Visible;
}

// ══ 2. TEXT ══════════════════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>The hero's one-sentence bio (ch 08 §8, ArtistPage.Hero.cs:262-282 verbatim). The string pair is the
/// verbatim port; <see cref="Lead"/> is its UTF-8 twin, which the decoder runs once into <c>StagedArtist.BioLead</c>.</summary>
public static class ArtistText
{
    /// <summary>The cut is at the first <c>". "</c>, and only when that index is past this — so "Mr. Brightside" and
    /// "Dr. Dre" never truncate a bio to three characters.</summary>
    public const int MinSentenceIndex = 20;

    /// <summary>Strip the HTML, then cut at the first <c>". "</c> past <see cref="MinSentenceIndex"/> (keeping the dot).
    /// Empty for null / whitespace / a bio that strips to nothing (0.2.9 answered null; a column cannot).</summary>
    public static string FirstSentence(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        string plain = StripHtml(text);
        if (plain.Length == 0) return "";
        int end = plain.IndexOf(". ", StringComparison.Ordinal);
        return end > MinSentenceIndex ? plain[..(end + 1)] : plain;
    }

    /// <summary>Drop every <c>&lt;…&gt;</c> run and every CR/LF, decode the entities <see cref="HtmlEntities"/> knows
    /// (unknown ones stay literal, and a decoded <c>&amp;lt;</c> never opens a tag), then trim — one pass.</summary>
    public static string StripHtml(string? html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var result = new System.Text.StringBuilder(html.Length);
        bool tag = false;
        for (int i = 0; i < html.Length; i++)
        {
            char c = html[i];
            if (c == '<') { tag = true; continue; }
            if (c == '>') { tag = false; continue; }
            if (tag || c is '\r' or '\n') continue;
            if (c == '&')
            {
                int cp = HtmlEntities.Decode(html.AsSpan(i), out int consumed);
                if (cp >= 0)
                {
                    HtmlEntities.AppendCodePoint(result, cp);
                    i += consumed - 1;
                    continue;
                }
            }
            result.Append(c);
        }
        return result.ToString().Trim();
    }

    /// <summary><see cref="FirstSentence"/> over UTF-8, allocation-free: strip the tags and CR/LF into
    /// <paramref name="into"/>, decode the entities <see cref="HtmlEntities"/> knows (the same set <see cref="StripHtml"/>
    /// decodes; a decoded code point never needs more UTF-8 bytes than the entity text it replaces, so a buffer sized to
    /// the html always holds the result), trim, and cut at the first <c>". "</c> whose CHARACTER index is past
    /// <see cref="MinSentenceIndex"/> — characters, not bytes, so a non-Latin bio cuts where the string rule does.
    /// Returns the bytes written (0 when <paramref name="into"/> is too small to hold the stripped text's first sentence).</summary>
    public static int Lead(ReadOnlySpan<byte> html, Span<byte> into)
    {
        int n = 0;
        bool tag = false;
        for (int i = 0; i < html.Length; i++)
        {
            byte b = html[i];
            if (b == (byte)'<') { tag = true; continue; }
            if (b == (byte)'>') { tag = false; continue; }
            if (tag || b is (byte)'\r' or (byte)'\n') continue;
            if (b == (byte)'&')
            {
                int cp = HtmlEntities.Decode(html[i..], out int consumed);
                if (cp >= 0)
                {
                    int written = HtmlEntities.EncodeUtf8(cp, into[n..]);
                    if (written == 0) return 0;
                    n += written;
                    i += consumed - 1;
                    continue;
                }
            }
            if (n >= into.Length) break;                     // a bio longer than the buffer: its first sentence still fits
            into[n++] = b;
        }

        // trim
        int start = 0, end = n;
        while (start < end && IsSpace(into[start])) start++;
        while (end > start && IsSpace(into[end - 1])) end--;
        if (start > 0) into[start..end].CopyTo(into);
        n = end - start;

        // the FIRST ". " decides, as in FirstSentence's IndexOf: past the character index it cuts, otherwise nothing does
        int chars = 0;
        for (int i = 0; i + 1 < n; i++)
        {
            if (into[i] == (byte)'.' && into[i + 1] == (byte)' ') return chars > MinSentenceIndex ? i + 1 : n;
            if ((into[i] & 0xC0) != 0x80) chars++;
        }
        return n;

        static bool IsSpace(byte b) => b is (byte)' ' or (byte)'\t';
    }
}

// ══ 3. THE CHART'S ORDER CONTRACT ════════════════════════════════════════════════════════════════════════════════════

/// <summary>The artist "Popular" chart's pure merge and caps (0.2.9 <c>Wavee.Core/Library/ArtistPopularTracks.cs</c>),
/// over SLOTS. In 0.3 the merge is the commit-time rewrite of <c>Edges.ArtistPopular</c> (<c>Entities.CommitPopular</c>,
/// Artist.cs); the ORDER contract is unchanged and keeps its tests.</summary>
public static class ArtistPopularTracks
{
    /// <summary>What <c>queryArtistOverview</c> returns. A committed chart longer than this IS an extended list.</summary>
    public const int OverviewSeedCap = 10;

    /// <summary>The extended ceiling (Spotify serves ~50).</summary>
    public const int ExtendedCap = 50;

    /// <summary>Fold the extension onto the seed into <paramref name="into"/> (≥ <see cref="ExtendedCap"/> long). The SEED
    /// KEEPS ITS ORDER at the head, extension-only tracks append in extension order, duplicates collapse with the seed
    /// winning, a slot ≤ 0 (a uri the wire named and did not identify) is dropped, and the whole run caps at
    /// <see cref="ExtendedCap"/>. An EMPTY extension copies the seed verbatim (capped) — a failed or empty step two must
    /// never reorder a painted chart. Returns the rows written.</summary>
    public static int Merge(ReadOnlySpan<int> seed, ReadOnlySpan<int> extension, Span<int> into)
    {
        int cap = Math.Min(ExtendedCap, into.Length);
        if (extension.IsEmpty)
        {
            int copy = Math.Min(seed.Length, cap);
            seed[..copy].CopyTo(into);
            return copy;
        }
        int n = 0;
        for (int i = 0; i < seed.Length && n < cap; i++)
            if (seed[i] > Table.None && into[..n].IndexOf(seed[i]) < 0) into[n++] = seed[i];
        for (int i = 0; i < extension.Length && n < cap; i++)
            if (extension[i] > Table.None && into[..n].IndexOf(extension[i]) < 0) into[n++] = extension[i];
        return n;
    }

    /// <summary>Step three (kind 185): hand the rows with NO count the incoming count, parallel by index. A row that
    /// already carries a positive count is never touched (the overview head stays authoritative), and a non-positive
    /// incoming count is never applied. Returns false — and writes nothing new — when nothing changes.</summary>
    public static bool WithPlayCounts(ReadOnlySpan<uint> chart, ReadOnlySpan<uint> incoming, Span<uint> into)
    {
        bool changed = false;
        for (int i = 0; i < chart.Length && i < into.Length; i++)
        {
            uint have = chart[i];
            uint next = have == 0 && i < incoming.Length && incoming[i] > 0 ? incoming[i] : have;
            changed |= next != have;
            into[i] = next;
        }
        return changed;
    }

    /// <summary>The rows that still have no play count — what step three asks kind 185 for. Returns the count written.</summary>
    public static int WithoutPlayCount(ReadOnlySpan<int> slots, ReadOnlySpan<uint> counts, Span<int> into)
    {
        int n = 0;
        for (int i = 0; i < slots.Length && i < counts.Length && n < into.Length; i++)
            if (counts[i] == 0 && slots[i] > Table.None) into[n++] = slots[i];
        return n;
    }

    /// <summary>How many of <paramref name="merged"/> came from beyond the seed.</summary>
    public static int AppendedCount(int seedCount, int mergedCount) => Math.Max(0, mergedCount - seedCount);

    /// <summary><c>FakeData.TopTracksOf</c> (ch 31 §8): the highest-played, TITLE-deduped rows, play-count descending —
    /// the one ordered set the chart and the artist play context share. <paramref name="titleKeys"/> is any stable
    /// per-title identity (an interned title id). Ties keep candidate order. Writes candidate INDICES; returns the count.</summary>
    public static int TopByPlays(ReadOnlySpan<uint> plays, ReadOnlySpan<int> titleKeys, int count, Span<int> into)
    {
        int limit = Math.Min(count, into.Length);
        int n = 0;
        Span<bool> taken = plays.Length <= 512 ? stackalloc bool[plays.Length] : new bool[plays.Length];
        while (n < limit)
        {
            int best = -1;
            for (int i = 0; i < plays.Length; i++)
            {
                if (taken[i]) continue;
                if (best < 0 || plays[i] > plays[best]) best = i;
            }
            if (best < 0) break;
            taken[best] = true;
            bool dup = false;
            for (int k = 0; k < n; k++)
                if (titleKeys[into[k]] == titleKeys[best]) { dup = true; break; }
            if (!dup) into[n++] = best;
        }
        return n;
    }
}

// ══ 4. THE TOUR BANNER LADDER ════════════════════════════════════════════════════════════════════════════════════════

/// <summary>Which tour-banner arm an artist's concert list selects (ch 08 W20, GAP 15).</summary>
public enum TourArm : byte { None, UpcomingShow, UpcomingDates, OnTourNow, UpcomingTour }

/// <summary>The tour banner, ported from <c>FakeData.TourBannerFor</c> (FakeData.cs:170-180) — which 0.2.9 also ran on
/// the live path — with the wall clock replaced by an argument (ch 31 §7.3). Derived at commit by
/// <c>Artist.DeriveTour</c>; the UI never recomputes it.</summary>
public static class ArtistTour
{
    /// <summary>"Soon" is the next date within this many milliseconds, and not already past.</summary>
    public const long SoonMs = 7L * 86_400_000L;

    /// <summary>The ladder: none for 0 dates, 1 → a show, 2-3 → dates, ≥ 4 with the next within 7 days → on tour now,
    /// else an upcoming tour.</summary>
    public static TourArm ArmFor(int count, long nextDateMs, long nowMs)
    {
        if (count <= 0) return TourArm.None;
        if (count == 1) return TourArm.UpcomingShow;
        if (count <= 3) return TourArm.UpcomingDates;
        return IsSoon(nextDateMs, nowMs) ? TourArm.OnTourNow : TourArm.UpcomingTour;
    }

    /// <summary>0.2.9's <c>TourBanner.IsLive</c> is the SOON verdict, whatever the count (verbatim).</summary>
    public static bool IsSoon(long nextDateMs, long nowMs) => nextDateMs >= nowMs && nextDateMs - nowMs <= SoonMs;

    /// <summary>The index of the earliest date (the first on a tie), or -1 for an empty span.</summary>
    public static int NextIndex(ReadOnlySpan<long> datesMs)
    {
        int next = -1;
        for (int i = 0; i < datesMs.Length; i++)
            if (next < 0 || datesMs[i] < datesMs[next]) next = i;
        return next;
    }

    /// <summary>The eyebrow's loc key (batch-loc WP-5.N-B.json). Sentence case: the eyebrow role does not caps-transform
    /// (Design.Type.Eyebrow), so 0.2.9's "ON TOUR NOW" is the key's English, not a ToUpper.</summary>
    public static string EyebrowKey(TourArm arm) => arm switch
    {
        TourArm.UpcomingShow => "artist.tour.upcomingShow",
        TourArm.UpcomingDates => "artist.tour.upcomingDates",
        TourArm.OnTourNow => "artist.tour.onTourNow",
        TourArm.UpcomingTour => "artist.tour.upcomingTour",
        _ => "",
    };

    /// <summary>The headline's loc key: <c>{name} — on tour</c>.</summary>
    public const string HeadlineKey = "artist.tour.headline";

    /// <summary>The subline's loc key: <c>Next: {date} · {venue} · {city} · {count} dates total</c>.</summary>
    public const string SublineKey = "artist.tour.subline";

    /// <summary>The next date in the PROVIDER'S local clock (Concert.cs header note 1), "MMM d" in the app culture.</summary>
    public static string DateLabel(long dateMs, short offsetMinutes, CultureInfo culture)
        => DateTimeOffset.FromUnixTimeMilliseconds(dateMs).ToOffset(TimeSpan.FromMinutes(offsetMinutes))
                         .ToString("MMM d", culture);
}

// ══ 5. THE CATALOG FILTER ════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>The one facet ↔ release-kind filter (0.2.9 <c>AggregateCatalog.KindMatches</c>, AggregateCatalog.cs:94-99),
/// the wire's release-type word, and the external-link glyph classifier.</summary>
public static class ArtistCatalog
{
    /// <summary>Singles ⇒ Single OR EP; Compilations ⇒ Compilation; Albums ⇒ Album. Shared by the decoder (a facet page
    /// coerces a contradicting row) and the seed (which splits its releases by it), so the counts agree.</summary>
    public static bool KindMatches(AlbumKind kind, DiscoFacet facet) => facet switch
    {
        DiscoFacet.Singles => kind is AlbumKind.Single or AlbumKind.EP,
        DiscoFacet.Compilations => kind == AlbumKind.Compilation,
        _ => kind == AlbumKind.Album,
    };

    /// <summary>The kind a facet's contradicting row is coerced to (the facet the server listed it under wins).</summary>
    public static AlbumKind CanonicalKind(DiscoFacet facet, int trackCount) => facet switch
    {
        DiscoFacet.Singles => trackCount >= EpMinTracks ? AlbumKind.EP : AlbumKind.Single,
        DiscoFacet.Compilations => AlbumKind.Compilation,
        _ => AlbumKind.Album,
    };

    /// <summary>The wire lists an EP as <c>SINGLE</c>; the mapper's rule (SpotifyExportMapper.MapRelease) calls a single
    /// of four or more tracks an EP.</summary>
    public const int EpMinTracks = 4;

    /// <summary>The wire's release-type word → the kind (SpotifyExportMapper.MapRelease verbatim).</summary>
    public static AlbumKind KindOf(ReadOnlySpan<byte> type, int trackCount)
    {
        if (Is(type, "SINGLE"u8)) return trackCount >= EpMinTracks ? AlbumKind.EP : AlbumKind.Single;
        if (Is(type, "EP"u8)) return AlbumKind.EP;
        if (Is(type, "COMPILATION"u8)) return AlbumKind.Compilation;
        return AlbumKind.Album;
    }

    /// <summary>Which glyph an external link wears (<see cref="LinkEdge.Kind"/>).</summary>
    public enum LinkKind : byte { Generic, Instagram, Twitter, Facebook, YouTube, Wikipedia, TikTok }

    /// <summary>SpotifyExportMapper.ClassifyLink over the name and the url, ASCII-case-insensitive.</summary>
    public static LinkKind ClassifyLink(ReadOnlySpan<byte> name, ReadOnlySpan<byte> url)
    {
        if (Has(name, url, "instagram"u8)) return LinkKind.Instagram;
        if (Has(name, url, "twitter"u8) || Has(name, url, "x.com"u8)) return LinkKind.Twitter;
        if (Has(name, url, "facebook"u8)) return LinkKind.Facebook;
        if (Has(name, url, "youtube"u8)) return LinkKind.YouTube;
        if (Has(name, url, "wikipedia"u8)) return LinkKind.Wikipedia;
        if (Has(name, url, "tiktok"u8)) return LinkKind.TikTok;
        return LinkKind.Generic;

        static bool Has(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, ReadOnlySpan<byte> needle)
            => Contains(a, needle) || Contains(b, needle);
    }

    /// <summary>SpotifyExportMapper.TitleCase over UTF-8 ("INSTAGRAM" → "Instagram"), ASCII letters only. Writes into
    /// <paramref name="into"/> and returns the length.</summary>
    public static int TitleCase(ReadOnlySpan<byte> name, Span<byte> into)
    {
        int n = Math.Min(name.Length, into.Length);
        for (int i = 0; i < n; i++)
        {
            byte b = name[i];
            if (i == 0) into[i] = b is >= (byte)'a' and <= (byte)'z' ? (byte)(b - 32) : b;
            else into[i] = b is >= (byte)'A' and <= (byte)'Z' ? (byte)(b + 32) : b;
        }
        return n;
    }

    static bool Is(ReadOnlySpan<byte> token, ReadOnlySpan<byte> upper)
    {
        if (token.Length != upper.Length) return false;
        for (int i = 0; i < token.Length; i++)
        {
            int a = token[i];
            if (a is >= 'a' and <= 'z') a -= 32;
            if (a != upper[i]) return false;
        }
        return true;
    }

    static bool Contains(ReadOnlySpan<byte> hay, ReadOnlySpan<byte> lowerNeedle)
    {
        for (int i = 0; i + lowerNeedle.Length <= hay.Length; i++)
        {
            int k = 0;
            for (; k < lowerNeedle.Length; k++)
            {
                int c = hay[i + k];
                if (c is >= 'A' and <= 'Z') c += 32;
                if (c != lowerNeedle[k]) break;
            }
            if (k == lowerNeedle.Length) return true;
        }
        return false;
    }
}

// ══ 6. THE COMMIT-TIME DERIVATIONS ═══════════════════════════════════════════════════════════════════════════════════

public static partial class Entities
{
    // The commit's scratch. UI thread only (C1); grown to the widest run seen and never shrunk (P8).
    static int[] s_popularSeed = new int[64];
    static int[] s_popularExtension = new int[64];
    static readonly int[] s_popularMerged = new int[ArtistPopularTracks.ExtendedCap];
    static int[] s_extraTargets = new int[64];
    static StringId[] s_extraText = new StringId[64];
    static VideoEdge[] s_extraVideo = new VideoEdge[64];
    static CityEdge[] s_extraCity = new CityEdge[64];
    static LinkEdge[] s_extraLink = new LinkEdge[64];
    static NoEdge[] s_extraNone = new NoEdge[64];

    /// <summary>THE CHART MERGE (ch 08 §8, the ArtistPopularTracks order contract). Two answers write the chart — the
    /// overview's top tracks (the SEED, with play counts) and the extended list (the EXTENSION, bare uris) — and they may
    /// share one batch (<c>Api.AnswerRows</c> runs both for one artist) or land in either order across two. Neither is an
    /// edge run; this merges them per artist:
    /// <list type="bullet">
    /// <item>seed = the staged seed, else the committed chart's first <see cref="ArtistPopularTracks.OverviewSeedCap"/>;</item>
    /// <item>extension = the staged extension, else — when a new seed lands over an ALREADY-EXTENDED chart
    /// (<c>Knows(Chart)</c> before this batch) — the committed chart, so a re-answered overview keeps its tail;</item>
    /// <item>the merge lands as ONE <c>Relation.ArtistPopular</c> run APPENDED to the batch's edges, so it is committed
    /// after every other run for the same parent (a legacy <c>Decode.ArtistOverview</c> run included) and wins.</item>
    /// </list>
    /// Runs BEFORE the artist rows, so <c>Knows(Chart)</c> reads the state the batch found.</summary>
    static void CommitPopular(Staging s)
    {
        var runs = s.PopularRunsOrNull;
        if (runs is null || runs.Count == 0) return;
        var ids = s.PopularTracksOrNull is { } list ? list.Span : default;
        var span = runs.Span;
        var artists = Current.Artists;
        var chart = Current.Edges.ArtistPopular;
        var tracks = Current.Tracks;

        for (int i = 0; i < span.Length; i++)
        {
            int parent = s.Slot(artists, in span[i].Parent);
            if (parent == Table.None || SeenParent(s, span, i, parent)) continue;

            int seedRun = -1, extensionRun = -1;
            for (int j = i; j < span.Length; j++)
            {
                if (s.Slot(artists, in span[j].Parent) != parent) continue;
                if (span[j].Extension) extensionRun = j; else seedRun = j;
            }

            // Copied out BEFORE any write: the span rule (Edges.cs) — nothing here holds a Targets() span across a write.
            var committed = chart.Targets(parent);
            bool extended = artists.Knows(parent, (uint)ArtistFields.Chart);
            int seedN, extensionN;
            if (seedRun >= 0) seedN = ResolvePopular(s, ids, in span[seedRun], ref s_popularSeed);
            else seedN = CopyPopular(committed[..Math.Min(committed.Length, ArtistPopularTracks.OverviewSeedCap)], ref s_popularSeed);
            if (extensionRun >= 0) extensionN = ResolvePopular(s, ids, in span[extensionRun], ref s_popularExtension);
            else extensionN = extended ? CopyPopular(committed, ref s_popularExtension) : 0;

            int n = ArtistPopularTracks.Merge(s_popularSeed.AsSpan(0, seedN), s_popularExtension.AsSpan(0, extensionN),
                                              s_popularMerged);
            var edges = s.Edges;
            int start = edges.Count;
            for (int k = 0; k < n; k++) edges.Add().Target = new StagedId(tracks.Id[s_popularMerged[k]]);
            edges.Run(Relation.ArtistPopular, in span[i].Parent, start, n, EdgeState.Complete, n);
        }

        static bool SeenParent(Staging s, ReadOnlySpan<StagedPopularRun> span, int upTo, int parent)
        {
            for (int k = 0; k < upTo; k++)
                if (s.Slot(Current.Artists, in span[k].Parent) == parent) return true;
            return false;
        }

        static int ResolvePopular(Staging s, ReadOnlySpan<StagedId> ids, in StagedPopularRun run, ref int[] into)
        {
            if (run.Start < 0 || run.Length <= 0 || run.Start + run.Length > ids.Length) return 0;
            if (into.Length < run.Length) into = new int[Math.Max(run.Length, into.Length * 2)];
            for (int k = 0; k < run.Length; k++) into[k] = s.Slot(Current.Tracks, in ids[run.Start + k]);
            return run.Length;
        }

        static int CopyPopular(ReadOnlySpan<int> from, ref int[] into)
        {
            if (into.Length < from.Length) into = new int[Math.Max(from.Length, into.Length * 2)];
            from.CopyTo(into);
            return from.Length;
        }
    }

    /// <summary>Land every staged artist payload relation (ch 08 GAP 9-14). Each is a whole-list <c>Replace</c>,
    /// Complete, EMPTY INCLUDED. One answer may stage several runs for the same (artist, relation) — the overview's three
    /// playlist lists (<c>playlistsV2</c>, <c>featuringV2</c>, <c>discoveredOnV2</c>) and its two video envelopes — and
    /// they CONCATENATE, in staging order, into one replace; a later run never erases an earlier one. The payload text
    /// is OWNED by the edge (Edges.cs header): the incoming list is retained FIRST, then the list being replaced is
    /// released, then the relation is replaced — so a string both lists share is never reclaimed in between. A target
    /// that repeats (a playlist both featured and discovered on) lands once.</summary>
    static void CommitArtistExtras(Staging s)
    {
        var runs = s.ArtistExtraRunsOrNull;
        if (runs is null || runs.Count == 0) return;
        var rows = s.ArtistExtraRowsOrNull is { } list ? list.Span : default;
        var span = runs.Span;
        var artists = Current.Artists;
        var e = Current.Edges;

        for (int i = 0; i < span.Length; i++)
        {
            var kind = span[i].Kind;
            int parent = s.Slot(artists, in span[i].Parent);
            if (parent == Table.None || SeenExtra(s, span, i, kind, parent)) continue;

            int total = 0;
            for (int j = i; j < span.Length; j++)
                if (Matches(s, rows, in span[j], kind, parent)) total += span[j].Length;
            GrowExtras(total);
            int n = 0;

            for (int j = i; j < span.Length; j++)
            {
                if (!Matches(s, rows, in span[j], kind, parent)) continue;
                var page = rows.Slice(span[j].Start, span[j].Length);
                switch (kind)
                {
                    case ArtistExtraKind.Gallery:
                        for (int k = 0; k < page.Length; k++)
                        {
                            var image = s.Intern(page[k].T0);
                            if (image.IsEmpty || ContainsText(s_extraText.AsSpan(0, n), image)) continue;
                            s_extraTargets[n] = Table.None;
                            s_extraText[n++] = Retained(image);
                        }
                        break;
                    case ArtistExtraKind.Playlists:
                        for (int k = 0; k < page.Length; k++)
                        {
                            int target = s.Slot(Current.Playlists, in page[k].Target);
                            if (target == Table.None || s_extraTargets.AsSpan(0, n).Contains(target)) continue;
                            s_extraTargets[n] = target;
                            s_extraText[n++] = Retained(s.Intern(page[k].T0));
                        }
                        break;
                    case ArtistExtraKind.Videos:
                        for (int k = 0; k < page.Length; k++)
                        {
                            int target = s.Slot(Current.Tracks, in page[k].Target);
                            if (target == Table.None || s_extraTargets.AsSpan(0, n).Contains(target)) continue;
                            s_extraTargets[n] = target;
                            s_extraVideo[n++] = new VideoEdge(Retained(s.Intern(page[k].T0)), page[k].I0);
                        }
                        break;
                    case ArtistExtraKind.Merch:
                        // Merch rows are a bump-allocated side slab (Edges.cs §5): a re-answer takes new rows and hands
                        // the old rows' TEXT back — the bytes that matter — rather than recycling slots.
                        for (int k = 0; k < page.Length; k++)
                        {
                            var name = s.Intern(page[k].T0);
                            if (name.IsEmpty) continue;
                            int row = e.Merch.Alloc();
                            ref var m = ref e.Merch.Row[row];
                            RetainText(ref m.Name, name);
                            RetainText(ref m.Price, s.Intern(page[k].T1));
                            RetainText(ref m.ImageId, s.Intern(page[k].T2));
                            RetainText(ref m.ShopUrl, s.Intern(page[k].T3));
                            s_extraTargets[n++] = row;
                        }
                        break;
                    case ArtistExtraKind.Cities:
                        for (int k = 0; k < page.Length; k++)
                        {
                            var city = s.Intern(page[k].T0);
                            if (city.IsEmpty) continue;
                            s_extraTargets[n] = Table.None;
                            s_extraCity[n++] = new CityEdge(Retained(city), Retained(s.Intern(page[k].T1)), page[k].U0);
                        }
                        break;
                    case ArtistExtraKind.Links:
                        for (int k = 0; k < page.Length; k++)
                        {
                            var url = s.Intern(page[k].T1);
                            if (url.IsEmpty) continue;
                            s_extraTargets[n] = Table.None;
                            s_extraLink[n++] = new LinkEdge(Retained(s.Intern(page[k].T0)), Retained(url), page[k].B0);
                        }
                        break;
                }
            }

            // Release AFTER every retain above, then replace once.
            var targets = s_extraTargets.AsSpan(0, n);
            switch (kind)
            {
                case ArtistExtraKind.Gallery:
                    e.ReleaseArtistGalleryText(parent);
                    e.ArtistGallery.Replace(parent, targets, s_extraText.AsSpan(0, n), EdgeState.Complete, n);
                    break;
                case ArtistExtraKind.Playlists:
                    e.ReleaseArtistPlaylistText(parent);
                    e.ArtistPlaylists.Replace(parent, targets, s_extraText.AsSpan(0, n), EdgeState.Complete, n);
                    break;
                case ArtistExtraKind.Videos:
                    e.ReleaseArtistVideoText(parent);
                    e.ArtistVideos.Replace(parent, targets, s_extraVideo.AsSpan(0, n), EdgeState.Complete, n);
                    break;
                case ArtistExtraKind.Merch:
                    e.ReleaseArtistMerchText(parent);
                    e.ArtistMerch.Replace(parent, targets, s_extraNone.AsSpan(0, n), EdgeState.Complete, n);
                    break;
                case ArtistExtraKind.Cities:
                    e.ReleaseArtistCityText(parent);
                    e.ArtistCities.Replace(parent, targets, s_extraCity.AsSpan(0, n), EdgeState.Complete, n);
                    break;
                case ArtistExtraKind.Links:
                    e.ReleaseArtistLinkText(parent);
                    e.ArtistLinks.Replace(parent, targets, s_extraLink.AsSpan(0, n), EdgeState.Complete, n);
                    break;
            }
        }

        static bool Matches(Staging s, ReadOnlySpan<StagedArtistExtra> rows, in StagedArtistExtraRun run,
                            ArtistExtraKind kind, int parent)
            => run.Kind == kind && run.Start >= 0 && run.Length >= 0 && run.Start + run.Length <= rows.Length
               && s.Slot(Current.Artists, in run.Parent) == parent;

        static bool SeenExtra(Staging s, ReadOnlySpan<StagedArtistExtraRun> span, int upTo, ArtistExtraKind kind, int parent)
        {
            for (int k = 0; k < upTo; k++)
                if (span[k].Kind == kind && s.Slot(Current.Artists, in span[k].Parent) == parent) return true;
            return false;
        }
    }

    static bool ContainsText(ReadOnlySpan<StringId> ids, StringId id)
    {
        for (int i = 0; i < ids.Length; i++) if (ids[i].Value == id.Value) return true;
        return false;
    }

    static void GrowExtras(int n)
    {
        if (n <= s_extraTargets.Length) return;
        int size = s_extraTargets.Length;
        while (size < n) size *= 2;
        s_extraTargets = new int[size];
        s_extraText = new StringId[size];
        s_extraVideo = new VideoEdge[size];
        s_extraCity = new CityEdge[size];
        s_extraLink = new LinkEdge[size];
        s_extraNone = new NoEdge[size];
    }
}

public sealed partial class Edges
{
    // The text the artist payload relations OWN, given back per parent before a replace (Entities.CommitArtistExtras)
    // and for every parent when the scope retires (ReleaseArtistPayloadText — Edges.ReleaseText must call it; reported
    // as a shared-file patch, because Edges.cs's walk lists every owned-text relation by name).

    internal void ReleaseArtistGalleryText(int parent)
    {
        var rows = ArtistGallery.Payload(parent);
        for (int i = 0; i < rows.Length; i++) Entities.Strings.Release(rows[i]);
    }

    internal void ReleaseArtistPlaylistText(int parent)
    {
        var rows = ArtistPlaylists.Payload(parent);
        for (int i = 0; i < rows.Length; i++) Entities.Strings.Release(rows[i]);
    }

    internal void ReleaseArtistVideoText(int parent)
    {
        var rows = ArtistVideos.Payload(parent);
        for (int i = 0; i < rows.Length; i++) Entities.Strings.Release(rows[i].Thumb);
    }

    internal void ReleaseArtistCityText(int parent)
    {
        var rows = ArtistCities.Payload(parent);
        for (int i = 0; i < rows.Length; i++)
        {
            Entities.Strings.Release(rows[i].City);
            Entities.Strings.Release(rows[i].Country);
        }
    }

    internal void ReleaseArtistLinkText(int parent)
    {
        var rows = ArtistLinks.Payload(parent);
        for (int i = 0; i < rows.Length; i++)
        {
            Entities.Strings.Release(rows[i].Name);
            Entities.Strings.Release(rows[i].Url);
        }
    }

    internal void ReleaseArtistMerchText(int parent)
    {
        var rows = ArtistMerch.Targets(parent);
        for (int i = 0; i < rows.Length; i++)
        {
            if ((uint)rows[i] >= (uint)Merch.Count) continue;
            ref var m = ref Merch.Row[rows[i]];
            Entities.ReleaseText(ref m.Name);
            Entities.ReleaseText(ref m.Price);
            Entities.ReleaseText(ref m.ImageId);
            Entities.ReleaseText(ref m.ShopUrl);
        }
    }

    /// <summary>Every parent's artist payload text — the scope-retirement walk (G-052's rule: a relation that AddRefs its
    /// payload is listed in <c>Edges.ReleaseText</c>).</summary>
    internal void ReleaseArtistPayloadText()
    {
        for (int p = 0; p < ArtistGallery.ParentCount; p++) ReleaseArtistGalleryText(p);
        for (int p = 0; p < ArtistPlaylists.ParentCount; p++) ReleaseArtistPlaylistText(p);
        for (int p = 0; p < ArtistVideos.ParentCount; p++) ReleaseArtistVideoText(p);
        for (int p = 0; p < ArtistCities.ParentCount; p++) ReleaseArtistCityText(p);
        for (int p = 0; p < ArtistLinks.ParentCount; p++) ReleaseArtistLinkText(p);
        for (int p = 0; p < ArtistMerch.ParentCount; p++) ReleaseArtistMerchText(p);
    }
}

public readonly partial struct Artist
{
    /// <summary>THE TOUR BANNER, DERIVED (ch 08 GAP 15, WP-5.N contract §6). Reads the artist's concert edge and the
    /// concert rows, runs <see cref="ArtistTour"/>'s ladder against <paramref name="nowUnixMs"/>, and writes the three
    /// loc-keyed strings through <see cref="Table.SetText"/>, the <see cref="ArtistFlags.TourLive"/> bit, and
    /// <see cref="ArtistFields.Tour"/> at the overview authority the row already holds (<see cref="Authority.Seed"/> when
    /// none). An artist with no concerts gets three empty strings — the banner is absent.
    /// <para>Called by the concert commit when it lands an <c>ArtistConcerts</c> run (with
    /// <c>Store.ToUnix(Entities.Now) * 1000</c>) and by the seed (with its fixed clock). UI thread only (C1).</para></summary>
    public static void DeriveTour(int artistSlot, long nowUnixMs)
    {
        var scope = Entities.Current;
        var t = scope.Artists;
        if (artistSlot <= Table.None || artistSlot >= t.Count) return;
        var concerts = scope.Concerts;
        var targets = scope.Edges.ArtistConcerts.Targets(artistSlot);

        int count = 0, next = Table.None;
        long nextDate = 0;
        for (int i = 0; i < targets.Length; i++)
        {
            int c = targets[i];
            if (c <= Table.None || c >= concerts.Count) continue;
            count++;
            if (next == Table.None || concerts.Date[c] < nextDate) { next = c; nextDate = concerts.Date[c]; }
        }

        var arm = ArtistTour.ArmFor(count, nextDate, nowUnixMs);
        StringId eyebrow = StringId.Empty, headline = StringId.Empty, subline = StringId.Empty;
        bool live = false;
        if (arm != TourArm.None)
        {
            var strings = Entities.Strings;
            string name = strings.Resolve(t.Name[artistSlot]);
            string date = ArtistTour.DateLabel(nextDate, concerts.OffsetMinutes[next], TourCulture());
            eyebrow = strings.Intern(Loc.Get(ArtistTour.EyebrowKey(arm)));
            headline = strings.Intern(Loc.Format(ArtistTour.HeadlineKey, ("name", name)));
            subline = strings.Intern(Loc.Format(ArtistTour.SublineKey, ("date", date),
                ("venue", strings.Resolve(concerts.Venue[next])), ("city", strings.Resolve(concerts.City[next])),
                ("count", count)));
            live = ArtistTour.IsSoon(nextDate, nowUnixMs);
        }

        t.SetText(ref t.TourEyebrow, artistSlot, eyebrow);
        t.SetText(ref t.TourHeadline, artistSlot, headline);
        t.SetText(ref t.TourSubline, artistSlot, subline);
        t.Flags[artistSlot] = live ? t.Flags[artistSlot] | (uint)ArtistFlags.TourLive
                                   : t.Flags[artistSlot] & ~(uint)ArtistFlags.TourLive;
        var authority = (Authority)t.OverviewAuthority[artistSlot];
        t.Applied(artistSlot, (uint)ArtistFields.Tour, authority == Authority.None ? Authority.Seed : authority,
                  ref t.OverviewAuthority);
    }

    static CultureInfo TourCulture()
    {
        try { return CultureInfo.GetCultureInfo(Loc.CurrentCulture); }
        catch (CultureNotFoundException) { return CultureInfo.InvariantCulture; }
    }
}
