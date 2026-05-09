using System;
using System.Collections.Generic;
using System.Globalization;

namespace Wavee.UI.WinUI.Data.DTOs;

/// <summary>
/// Per-track ranking-vs-last-week info pulled from a chart playlist's
/// <c>FormatAttributes</c> bag. Returns null for non-chart tracks so every
/// downstream UI path stays inert.
/// </summary>
public enum ChartStatus { New, Up, Down, Equal }

public sealed record ChartTrackInfo
{
    public required ChartStatus Status { get; init; }
    public required int CurrentPosition { get; init; }
    public int? PreviousPosition { get; init; }
    public long? Rank { get; init; }

    /// <summary>
    /// How many positions the track moved upward (positive = up, negative
    /// = down). Null when there's no previous position to compare against
    /// (e.g. NEW entries).
    /// </summary>
    public int? Delta => PreviousPosition is int prev ? prev - CurrentPosition : null;

    public static ChartTrackInfo? From(IReadOnlyDictionary<string, string>? attrs)
    {
        if (attrs is null) return null;
        if (!attrs.TryGetValue("status", out var s)) return null;
        if (!attrs.TryGetValue("current_pos", out var cp) ||
            !int.TryParse(cp, NumberStyles.Integer, CultureInfo.InvariantCulture, out var current))
            return null;

        var status = s switch
        {
            "UP"    => ChartStatus.Up,
            "DOWN"  => ChartStatus.Down,
            "EQUAL" => ChartStatus.Equal,
            "NEW"   => ChartStatus.New,
            _ => (ChartStatus?)null,
        };
        if (status is null) return null;

        int? previous = attrs.TryGetValue("previous_pos", out var pp) &&
            int.TryParse(pp, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p)
            ? p : null;
        long? rank = attrs.TryGetValue("rank", out var r) &&
            long.TryParse(r, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rv)
            ? rv : null;

        return new ChartTrackInfo
        {
            Status = status.Value,
            CurrentPosition = current,
            PreviousPosition = previous,
            Rank = rank,
        };
    }
}

/// <summary>
/// Playlist-level chart context — present only when the playlist's
/// <c>format</c> attribute is <c>"chart"</c>. Drives the header sub-stat
/// line ("Updated May 1 · 8 new entries") and the per-row badge gating.
/// </summary>
public sealed record ChartPlaylistInfo
{
    public required string RankType { get; init; }
    public required string EntityType { get; init; }
    public DateTimeOffset? LastUpdated { get; init; }
    public int? NewEntriesCount { get; init; }

    public static ChartPlaylistInfo? From(IReadOnlyDictionary<string, string>? attrs)
    {
        if (attrs is null) return null;
        if (!attrs.TryGetValue("format", out var fmt) ||
            !fmt.Equals("chart", StringComparison.OrdinalIgnoreCase))
            return null;

        attrs.TryGetValue("rank_type", out var rt);
        attrs.TryGetValue("chart_entity_type", out var et);

        DateTimeOffset? updated = attrs.TryGetValue("last_updated", out var lu) &&
            DateTimeOffset.TryParse(lu, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var when)
            ? when : null;
        int? newCount = attrs.TryGetValue("new_entries_count", out var nec) &&
            int.TryParse(nec, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ncv)
            ? ncv : null;

        return new ChartPlaylistInfo
        {
            RankType = rt ?? "",
            EntityType = et ?? "track",
            LastUpdated = updated,
            NewEntriesCount = newCount,
        };
    }
}
