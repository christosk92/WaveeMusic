// ── Home/Zones.Rules.cs — pure rules the zone bodies (ClassicHomeZones.cs) are built from (Wave 3, agent B2) ──────────
//
// Role: CORE
// Owner: B2
// Wave: 3
// Spec: docs/plans/wavee/home-redesign-implementation.md "Workstream H", "Zone → controls → metrics" sheet
//        (ReleaseList row 9 .rel "type badge" + "date col 88"; CoverShelf/RadioShelf row 6 "two-cell wide lead").
//
// Two small pure fragments pulled out of the zone bodies so they are unit-testable with no engine running (the
// "no source-text tests" rule — these are exercised directly, not by grepping the UI file):
//   • ReleaseListRules — the Releases list row's type badge label and trailing date column text.
//   • ShelfLead        — merges a Zone's separately-carried Lead card back into its Items array for a PagedShelf's
//                         item list, tolerating either ZonePlanner shape (AddCoverShelf keeps the lead AT items[0]
//                         already; RadioMerge removes it from items and carries it only via Zone.Lead).
//
// Engine-free, pure: no hooks, no elements, no I/O. HomeCardKind/HomeCard are plain data reads (Entities/Home.cs),
// the same convention Home/ZonePlanner.cs already uses for its own pure rules.

using System.Collections.Generic;
using System.Globalization;
using FluentGpu.Localization;

namespace Wavee.HomeUi;

/// <summary>The Releases list row's two caller-owned text cells (<see cref="Items.ListRow"/>'s <c>typeLabel</c>/
/// <c>dateLabel</c> params) — kept out of the zone bodies so the ladder-vs-short-date choice is independently
/// testable.</summary>
public static class ReleaseListRules
{
    /// <summary>The row's type badge (sheet row 9 .rel: a small filled pill naming what kind of release this is). An album says what
    /// it is (Single, EP, Album, Compilation: <c>albumType</c>); a pre-save says Upcoming (home-gap P7).</summary>
    public static string TypeLabel(in HomeCard card, Func<string, string>? localize = null, bool upcomingFirst = true)
    {
        Func<string, string> resolve = localize ?? Loc.Get;
        if (upcomingFirst && card.Kind == HomeCardKind.Album && card.IsPreRelease) return resolve(Strings.Home.Release.Upcoming);
        if (card.Kind == HomeCardKind.Album && card.AlbumKind is { } k)
            return resolve(k switch
            {
                AlbumKind.Single => Strings.Home.Release.Single,
                AlbumKind.EP => Strings.Home.Release.Ep,
                AlbumKind.Compilation => Strings.Home.Release.Compilation,
                _ => Strings.Home.Release.Album,
            });
        return card.Kind switch
        {
            HomeCardKind.Album => resolve(Strings.Home.Release.Album),
            HomeCardKind.Playlist => resolve(Strings.Home.Release.Playlist),
            HomeCardKind.Artist => resolve(Strings.Home.Release.Artist),
            HomeCardKind.Track => resolve(Strings.Home.Release.Song),
            HomeCardKind.Episode => resolve(Strings.Home.Release.Episode),
            HomeCardKind.Podcast => resolve(Strings.Home.Release.Podcast),
            HomeCardKind.Audiobook => resolve(Strings.Home.Release.Audiobook),
            _ => "",
        };
    }

    /// <summary>The row's date column for <paramref name="card"/>: a pre-save's release day ("5 Dec", from <c>preReleaseEndDateTime</c>),
    /// else <see cref="DateLabel(long, long, TimeZoneInfo, CultureInfo)"/> over the album's own release.</summary>
    public static string DateLabel(in HomeCard card, long nowMs, TimeZoneInfo tz, CultureInfo culture)
        => card.IsPreRelease && card.PreReleaseAtMs > 0
            ? ShortDate(card.PreReleaseAtMs, tz, culture)
            : DateLabel(card.ReleasedAtMs, nowMs, tz, culture);

    static string ShortDate(long ms, TimeZoneInfo tz, CultureInfo culture)
    {
        DateTime local = TimeZoneInfo.ConvertTimeFromUtc(DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime, tz);
        return $"{local.Day} {culture.DateTimeFormat.GetAbbreviatedMonthName(local.Month)}";
    }

    /// <summary>The trailing 88-wide date column. <paramref name="releasedAtMs"/> ≤ 0 (unknown) renders blank. A
    /// FUTURE release (<see cref="HomeReleaseDetect"/>'s "Upcoming" case: the date is after <paramref name="nowMs"/>)
    /// renders a short calendar date directly rather than through <see cref="WhenCaption"/>'s played-relative ladder,
    /// which has no rung for a date that hasn't happened yet; a past release reuses that same ladder (so a release
    /// from a few days ago reads "3 d ago"-shaped, consistent with every other "when" caption on the page).</summary>
    public static string DateLabel(long releasedAtMs, long nowMs, TimeZoneInfo tz, CultureInfo culture)
    {
        if (releasedAtMs <= 0) return "";
        if (releasedAtMs > nowMs)
        {
            DateTime local = TimeZoneInfo.ConvertTimeFromUtc(
                DateTimeOffset.FromUnixTimeMilliseconds(releasedAtMs).UtcDateTime, tz);
            return $"{local.Day} {culture.DateTimeFormat.GetAbbreviatedMonthName(local.Month)}";
        }
        return WhenCaption.Of(releasedAtMs, nowMs, playingNow: false, tz, culture);
    }
}

/// <summary>Merges a <see cref="Zone"/>'s separately-carried <see cref="Zone.Lead"/> back into the item list a
/// PagedShelf renders, tolerating either shape <c>Home/ZonePlanner.cs</c> produces: <c>AddCoverShelf</c> (Made for
/// you) inserts the lead INTO <c>Items[0]</c> already; <c>RadioMerge</c> (Radio &amp; mixes) removes it from
/// <c>Items</c> and carries it only via <see cref="Zone.Lead"/>. A caller that always fed <c>Zone.Items</c> straight
/// to a shelf would render the lead twice for the first shape and not at all for the second — this normalises both
/// to "lead at index 0, exactly once".</summary>
public static class ShelfLead
{
    /// <summary>The side of one of the <paramref name="span"/> ordinary cover squares a two-cell-wide lead sits
    /// beside, given the lead's own column width <paramref name="leadW"/> and the shelf's inter-cell
    /// <paramref name="gap"/>: the <paramref name="span"/> squares plus their <c>span − 1</c> internal gaps fill the
    /// same <paramref name="leadW"/> the lead column occupies.</summary>
    public static float SquareWidth(float leadW, float gap, int span) => (leadW - gap * (span - 1)) / span;

    /// <summary>The lead cover's own aspect ratio so its rendered height exactly matches the stacked squares'
    /// height (row 6's "two-cell wide lead"): both covers share the same <paramref name="cardPad"/> inset, so
    /// <c>(leadW − 2·pad) / aspect == squareWidth − 2·pad</c> solved for <c>aspect</c>.</summary>
    public static float LeadAspect(float leadW, float gap, int span, float cardPad)
        => (leadW - 2f * cardPad) / (SquareWidth(leadW, gap, span) - 2f * cardPad);

    public static IReadOnlyList<HomeCard> Merge(IReadOnlyList<HomeCard> items, HomeCard? lead)
    {
        if (lead is not { } l) return items;
        if (items.Count > 0 && items[0].Equals(l)) return items;

        var merged = new HomeCard[items.Count + 1];
        merged[0] = l;
        for (int i = 0; i < items.Count; i++) merged[i + 1] = items[i];
        return merged;
    }
}

/// <summary>Which empty-facet words a zone gets, from the planner's own zone key (<c>home:audiobooks:empty</c>,
/// <c>podcasts:empty</c>, <c>home:empty:{facet}</c>). Pure, so the mapping is testable without an element; the empty-facet view
/// (Classic's `Home.Zone.EmptyFacet`) looks the three loc keys up.</summary>
public static class EmptyFacetCopy
{
    public static (string Title, string Body, string Action) Keys(string zoneKey)
    {
        if (zoneKey.StartsWith("home:audiobooks", StringComparison.Ordinal))
            return (Strings.Home.Empty.AudiobooksTitle, Strings.Home.Empty.AudiobooksBody, Strings.Home.Empty.AudiobooksAction);
        if (zoneKey.StartsWith("podcasts", StringComparison.Ordinal))
            return (Strings.Home.Empty.PodcastsTitle, Strings.Home.Empty.PodcastsBody, Strings.Home.Empty.PodcastsAction);
        return (Strings.Home.Empty.GenericTitle, Strings.Home.Empty.GenericBody, Strings.Home.Empty.GenericAction);
    }

    /// <summary>The same three words for a Library show tab with nothing saved (Podcasts or Audiobooks, no filter typed): the library page's empty state
    /// says what the kind is and offers the way to find some, as Home's empty facet does (audit B7 r2, V22).</summary>
    public static (string Title, string Body, string Action) LibraryShows(bool podcasts)
        => podcasts ? Keys("podcasts:empty") : Keys("home:audiobooks:empty");
}

/// <summary>The caption lines the section templates of home-gap P4, P6, P8 and the video card draw, from data already decoded. Pure.</summary>
public static class SectionCopy
{
    /// <summary>A book's facts line: "★ 4.7 · 20 hr 56 min" (the rating only where the server shows one, the length only when known).</summary>
    public static string BookFacts(in HomeCard book, CultureInfo culture)
    {
        string rating = book.Rating > 0d ? Strings.Home.Rated(book.Rating.ToString("0.0", culture)) : "";
        string length = HomeCardText.Duration(book.DurationMs);
        return rating.Length > 0 && length.Length > 0 ? rating + " · " + length : rating + length;
    }

    /// <summary>A mood plate's small line: the first sentence of the mix's description ("Spring music for you."), "" when none.</summary>
    public static string PlateLine(in HomeCard mix) => HomeCardText.FirstSentence(mix.Subtitle);

    /// <summary>A preview card's facts line: "Playlist · Spotify · 50 songs" (the kind, the owner, the count, each where known).</summary>
    public static string PreviewFacts(in HomeCard card, Func<string, string>? localize = null)
    {
        string kind = ReleaseListRules.TypeLabel(in card, localize);
        string rest = CardMeta.Of(card.OwnerName, card.TrackCount) ?? "";
        return kind.Length > 0 && rest.Length > 0 ? kind + " · " + rest : kind + rest;
    }

    /// <summary>An episode's facts line: "20 Jul · 1 hr 24 min · not started", or "… · 32 min left" while in progress.</summary>
    public static string EpisodeFacts(in HomeCard e, long nowMs, TimeZoneInfo tz, CultureInfo culture)
    {
        var parts = new List<string>(3);
        string date = EpisodeCaption.Date(e.ReleasedAtMs, nowMs, tz, culture);
        if (date.Length > 0) parts.Add(date);
        string length = EpisodeCaption.Duration(e.DurationMs);
        if (length.Length > 0) parts.Add(length);
        if (e.PlayedState == 2 && e.ResumeMs > 0 && e.DurationMs > 0) parts.Add(EpisodeCaption.Remaining(e.DurationMs, e.ResumeMs));
        else if (e.PlayedState == 1) parts.Add(Loc.Get(Strings.Home.NotStarted));
        return string.Join(" · ", parts);
    }

    /// <summary>The episode's progress, 0..1 (0 unless it is in progress).</summary>
    public static float EpisodeProgress(in HomeCard e)
        => e.PlayedState == 2 ? (float)EpisodeCaption.ProgressFraction(e.ResumeMs, e.DurationMs) : 0f;

    /// <summary>A daylist's "Based on pop and western" line, "" when nothing says it. The playlist row's words first (written with the
    /// edition's window by the newest answer: the feed card or the playlist's own header read), then the feed card's facts.</summary>
    public static string DaylistBasis(in HomeCard daylist)
    {
        var terms = SplitTerms(daylist.DaylistBasisText);
        if (terms.Length == 0 && daylist.Basis is { Count: > 0 } facts) terms = [.. facts];
        if (terms.Length == 0) return "";
        return terms.Length == 2 ? Strings.Home.BasedOnTwo(terms[0], terms[1]) : Strings.Home.BasedOn(string.Join(", ", terms));
    }

    /// <summary>A daylist's tags and the playlist each opens (null when a tag has none), at most <paramref name="max"/>, from the freshest
    /// source that has any: the playlist row's <c>localized_terms</c> (the edition's own, written with its window), then the anchors of
    /// the row's description (what the playlist page shows), then the feed card's facts (the answer the card came in, which a later
    /// header read outdates). Cold: once per render of the card.</summary>
    public static (string[] Tags, string?[] Links) DaylistTags(in HomeCard daylist, int max)
    {
        var tags = new List<string>(max);
        var links = new List<string?>(max);
        var terms = SplitTerms(daylist.DaylistTermsText);
        if (terms.Length > 0)
        {
            var anchors = HomeCardText.AnchorLinks(daylist.DaylistLinksText);
            for (int i = 0; i < terms.Length && tags.Count < max; i++) { tags.Add(terms[i]); links.Add(LinkOf(anchors, terms[i])); }
            return (tags.ToArray(), links.ToArray());
        }
        var described = HomeCardText.AnchorLinks(daylist.PlaylistDescription);
        if (described.Count > 0)
        {
            for (int i = 0; i < described.Count && tags.Count < max; i++) { tags.Add(described[i].Text); links.Add(described[i].Uri); }
            return (tags.ToArray(), links.ToArray());
        }
        var seedLinks = daylist.SeedLinks;
        for (int i = 0, n = daylist.SeedCount; i < n && tags.Count < max; i++)
        {
            string tag = daylist.SeedAt(i);
            if (tag.Length == 0) continue;
            tags.Add(tag);
            links.Add(seedLinks is not null && i < seedLinks.Count ? seedLinks[i] : null);
        }
        return (tags.ToArray(), links.ToArray());
    }

    static string[] SplitTerms(string? csv)
        => string.IsNullOrWhiteSpace(csv) ? [] : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    static string? LinkOf(IReadOnlyList<(string Uri, string Text)> anchors, string tag)
    {
        for (int k = 0; k < anchors.Count; k++)
            if (string.Equals(anchors[k].Text, tag, StringComparison.OrdinalIgnoreCase)) return anchors[k].Uri;
        return null;
    }
}
