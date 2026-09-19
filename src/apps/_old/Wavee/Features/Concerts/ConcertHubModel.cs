using System;
using System.Collections.Generic;
using System.Globalization;
using Wavee.Core;

namespace Wavee.Features.Concerts;

/// <summary>The date-filter presets the when-pill offers. Custom is a user-picked from–to span (no preset window).</summary>
public enum ConcertWhenKind { Any, Today, ThisWeekend, NextWeekend, Custom }

/// <summary>The full when-filter tuple the bar edits. Name is the fused pill's segment text ("This weekend",
/// "August", "Weekend", "Custom"); Range is what goes on the wire AND renders as "Jul 17 – 19".</summary>
public sealed record ConcertWhen(ConcertWhenKind Kind, string Name, ConcertDateRange? Range)
{
    public static readonly ConcertWhen Any = new(ConcertWhenKind.Any, "", null);
}

/// <summary>The complete filter tuple the pinned bar edits and the page funnels into the ONE feed requery.</summary>
public sealed record ConcertHubFilters(
    ConcertPlace? Place, int RadiusKm, ConcertWhen When, IReadOnlyList<string> ConceptUris);

/// <summary>Pure (engine-free) decisions for the Concert Hub. Kept dependency-light (Wavee.Core + BCL) so the concept
/// toggle/survival semantics and label building are unit-tested without mounting an engine window.</summary>
public static class ConcertHub
{
    /// <summary>Zero-or-one concept selection: tapping the active concept clears it, tapping another selects it.</summary>
    public static string? ToggleConcept(string? selected, string tapped) =>
        string.Equals(selected, tapped, StringComparison.Ordinal) ? null : tapped;

    /// <summary>Multi-select concept toggle: returns a NEW list with the uri removed if present, else appended (the
    /// existing order is preserved so the token strip does not reshuffle on select).</summary>
    public static IReadOnlyList<string> ToggleConcept(IReadOnlyList<string> selected, string uri)
    {
        var result = new List<string>(selected.Count + 1);
        bool removed = false;
        foreach (string existing in selected)
        {
            if (string.Equals(existing, uri, StringComparison.Ordinal)) { removed = true; continue; }
            result.Add(existing);
        }
        if (!removed) result.Add(uri);
        return result;
    }

    /// <summary>The wire/render date window for a preset, resolved against <paramref name="now"/>. Any/Custom carry no
    /// preset window (null); Today is the single current day; a weekend is Fri–Sun — ThisWeekend is the one containing
    /// or next after now (a Sat/Sun now stays in the current weekend), NextWeekend is the Fri–Sun after that.</summary>
    public static ConcertDateRange? PresetRange(ConcertWhenKind kind, DateTimeOffset now)
    {
        switch (kind)
        {
            case ConcertWhenKind.Today:
                var day = DateOnly.FromDateTime(now.Date);
                return new ConcertDateRange(day, day);
            case ConcertWhenKind.ThisWeekend:
                var weekend = Weekend(now);
                return new ConcertDateRange(weekend.From, weekend.To);
            case ConcertWhenKind.NextWeekend:
                var next = Weekend(now);
                return new ConcertDateRange(next.From.AddDays(7), next.To.AddDays(7));
            default:
                return null;
        }
    }

    // The Fri–Sun weekend containing-or-next: a Saturday/Sunday "now" resolves to the CURRENT weekend's Friday,
    // otherwise the upcoming Friday (Fri itself included). DayOfWeek: Sun=0 … Fri=5 … Sat=6.
    static (DateOnly From, DateOnly To) Weekend(DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.Date);
        DateOnly friday = (int)now.DayOfWeek switch
        {
            6 => today.AddDays(-1),        // Saturday
            0 => today.AddDays(-2),        // Sunday
            var dow => today.AddDays(5 - dow),   // Mon(1)..Fri(5)
        };
        return (friday, friday.AddDays(2));
    }

    /// <summary>Rest-state tokens: the provider's weight order is ALREADY the response order, so take the first
    /// <paramref name="top"/> and always append any selected concept that fell outside that head (original order),
    /// capping none when <paramref name="expanded"/>.</summary>
    public static IReadOnlyList<ConcertConcept> TopConcepts(IReadOnlyList<ConcertConcept> all,
        IReadOnlyList<string> selected, bool expanded, int top = 3)
    {
        if (expanded) return all;
        int head = Math.Min(top, all.Count);
        var result = new List<ConcertConcept>(head);
        for (int i = 0; i < head; i++)
            result.Add(all[i]);
        for (int i = head; i < all.Count; i++)
            if (Contains(selected, all[i].Uri))
                result.Add(all[i]);
        return result;
    }

    /// <summary>The fused pill's range caption: "Jul 17 – 19" within a month, "Jul 31 – Aug 2" across months, "Jul 14"
    /// for a single day. Abbreviated month names come from <paramref name="culture"/>; the separator is an EN DASH.</summary>
    public static string WhenLabel(ConcertDateRange range, CultureInfo culture)
    {
        culture ??= CultureInfo.CurrentCulture;
        string fromMonth = range.From.ToString("MMM", culture);
        string fromDay = range.From.Day.ToString(culture);
        if (range.From == range.To)
            return fromMonth + " " + fromDay;
        if (range.From.Year == range.To.Year && range.From.Month == range.To.Month)
            return fromMonth + " " + fromDay + EnDash + range.To.Day.ToString(culture);
        return fromMonth + " " + fromDay + EnDash + range.To.ToString("MMM", culture) + " " + range.To.Day.ToString(culture);
    }

    const string EnDash = " – ";   // space + EN DASH + space

    static bool Contains(IReadOnlyList<string> uris, string uri)
    {
        foreach (string value in uris)
            if (string.Equals(value, uri, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>A selected concept survives a location change ONLY while the new location still offers it; a vanished
    /// concept clears (concept weights/availability differ per geo). No selection stays no selection.</summary>
    public static string? ReconcileConcept(string? selected, IReadOnlyList<ConcertConcept> offered)
    {
        if (selected is null) return null;
        foreach (var concept in offered)
            if (string.Equals(concept.Uri, selected, StringComparison.Ordinal)) return selected;
        return null;
    }

    /// <summary>The hub location-button label. An inferred (IP-derived) place is marked approximate so the user knows
    /// it is a guess, not their saved choice; no resolvable place prompts for one.</summary>
    public static string LocationLabel(ConcertPlace? place, bool? inferred)
    {
        if (place is null || string.IsNullOrWhiteSpace(place.Name)) return "Set location";
        return inferred == true ? "Near " + place.Name + " (approximate)" : place.Name;
    }

    /// <summary>Provider concept names are machine labels (usually lowercase). Present them as UI labels while keeping
    /// familiar music abbreviations intact.</summary>
    public static string ConceptLabel(string? name, string fallback = "Genre")
    {
        string value = name?.Trim() ?? string.Empty;
        if (value.Length == 0) return fallback;
        return value.ToLowerInvariant() switch
        {
            "edm" => "EDM",
            "r&b" => "R&B",
            "rnb" => "R&B",
            "hip hop" => "Hip Hop",
            _ => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(value.ToLower(CultureInfo.CurrentCulture)),
        };
    }

    /// <summary>Whether a feed page carries nothing renderable (null page, or every section empty of both concerts and
    /// playlist promotions) — drives the hub's empty state.</summary>
    public static bool IsFeedEmpty(ConcertFeedPage? page)
    {
        if (page is null) return true;
        foreach (var section in page.Sections)
            if (section.Concerts.Count > 0 || section.PlaylistPromotions is { Count: > 0 }) return false;
        return true;
    }
}
