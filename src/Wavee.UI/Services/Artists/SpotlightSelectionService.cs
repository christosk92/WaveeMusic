using System;
using System.Collections.Generic;
using System.Linq;
using Wavee.UI.Contracts;

namespace Wavee.UI.Services.Artists;

/// <summary>
/// Which kind of content the hero spotlight card is showing. The
/// SpotlightReleaseCard control consumes this to toggle Save/Share button
/// visibility and pulse-dot animation; latest releases pulse to read as
/// "fresh", pinned items / popular releases sit still.
/// </summary>
public enum SpotlightMode
{
    Pinned,
    LatestRelease,
    PopularRelease,
}

/// <summary>
/// Framework-neutral inputs for the spotlight selection. The caller projects
/// VM state into this snapshot so the service stays clear of any WinUI types.
/// </summary>
public readonly record struct SpotlightSelectionInputs(
    ArtistPinnedItemResult? PinnedItem,
    ArtistLatestReleaseResult? LatestRelease,
    IReadOnlyList<SpotlightPopularRelease> PopularReleases);

/// <summary>
/// Minimal view of a popular release entry — only what the spotlight
/// selection logic actually inspects (Uri/Name/Image/Type/Year/TrackCount).
/// Keeps the service decoupled from the WinUI <c>ArtistReleaseVm</c>.
/// </summary>
public sealed record SpotlightPopularRelease(
    string? Uri,
    string? Name,
    string? ImageUrl,
    string? Type,
    int Year,
    int TrackCount);

/// <summary>
/// Selected spotlight (hero) plus the projected "Popular releases" column
/// alongside it. The popular-column projection is mode-dependent — when the
/// hero is pinned, the latest release moves into the column as a synthetic
/// row; when the hero IS the latest release, the column shows the top 3
/// popular releases; when the hero is a popular release, the column skips
/// the first popular entry so the cover doesn't appear twice.
/// </summary>
public sealed record SpotlightSelection(
    SpotlightMode Mode,
    string? Uri,
    string? Name,
    string? ImageUrl,
    string? Subtitle,
    string TagText,
    string EyebrowText,
    string? Comment,
    int TrackCount,
    IReadOnlyList<SpotlightPopularRelease> PopularReleasesDisplayed,
    SpotlightPopularRelease? VirtualLatestReleaseRow);

/// <summary>
/// Pure decision service: given the pinned item, latest release and popular
/// release candidates for an artist, decide which one drives the hero
/// spotlight card and project the matching popular-releases column. Replaces
/// the spread-out chain of <c>SpotlightCardMode</c> / <c>SpotlightReleaseName</c>
/// / <c>PopularReleasesDisplayed</c> getters that previously lived on
/// <c>ArtistViewModel</c> as 18+ stacked ternary fall-throughs.
/// </summary>
public sealed class SpotlightSelectionService
{
    /// <summary>
    /// Compute the spotlight selection for the supplied inputs. Returns a
    /// record with all fields the hero card binds against; never throws,
    /// returns an <see cref="SpotlightMode.PopularRelease"/> result with
    /// empty fields when nothing is selectable.
    /// </summary>
    public SpotlightSelection Select(SpotlightSelectionInputs inputs)
    {
        var hasPinnedItem = inputs.PinnedItem is not null;
        var hasLatestRelease =
            !string.IsNullOrEmpty(inputs.LatestRelease?.Name)
            && !string.IsNullOrEmpty(inputs.LatestRelease?.Uri);

        SpotlightPopularRelease? firstPopular =
            inputs.PopularReleases.Count > 0 ? inputs.PopularReleases[0] : null;

        // ── Hero pick ──────────────────────────────────────────────────────
        if (hasPinnedItem)
        {
            var pinned = inputs.PinnedItem!;
            return new SpotlightSelection(
                Mode: SpotlightMode.Pinned,
                Uri: pinned.Uri,
                Name: pinned.Title,
                ImageUrl: pinned.ImageUrl,
                // Some pinned-item flavours don't carry a track count — keep
                // the hero card's skeleton row count at 0 in that case.
                TrackCount: 0,
                Subtitle: pinned.Subtitle ?? string.Empty,
                TagText: "Pinned",
                EyebrowText: "Pinned",
                Comment: pinned.Comment,
                PopularReleasesDisplayed: BuildPopularColumn(SpotlightMode.Pinned, inputs, hasLatestRelease),
                VirtualLatestReleaseRow: BuildVirtualLatestReleaseRow(inputs, hasLatestRelease, requireVirtual: true));
        }

        if (hasLatestRelease)
        {
            var latest = inputs.LatestRelease!;
            return new SpotlightSelection(
                Mode: SpotlightMode.LatestRelease,
                Uri: latest.Uri,
                Name: latest.Name,
                ImageUrl: latest.ImageUrl,
                TrackCount: latest.TrackCount,
                Subtitle: BuildLatestReleaseSubtitle(latest),
                TagText: "Latest release",
                EyebrowText: "Latest release",
                Comment: null,
                PopularReleasesDisplayed: BuildPopularColumn(SpotlightMode.LatestRelease, inputs, hasLatestRelease),
                VirtualLatestReleaseRow: null);
        }

        return new SpotlightSelection(
            Mode: SpotlightMode.PopularRelease,
            Uri: firstPopular?.Uri,
            Name: firstPopular?.Name,
            ImageUrl: firstPopular?.ImageUrl,
            TrackCount: firstPopular?.TrackCount ?? 0,
            Subtitle: firstPopular is null ? string.Empty : FormatPopularSubtitle(firstPopular),
            TagText: "Popular now",
            EyebrowText: "Popular release",
            Comment: null,
            PopularReleasesDisplayed: BuildPopularColumn(SpotlightMode.PopularRelease, inputs, hasLatestRelease),
            VirtualLatestReleaseRow: null);
    }

    /// <summary>
    /// Project the up-to-3 popular-releases column rows next to the spotlight
    /// card. Pinned mode prepends a synthetic latest-release row; popular
    /// mode skips the FIRST popular release (because it's already in the hero);
    /// latest mode shows the unmodified top 3.
    /// </summary>
    private static IReadOnlyList<SpotlightPopularRelease> BuildPopularColumn(
        SpotlightMode mode,
        SpotlightSelectionInputs inputs,
        bool hasLatestRelease)
    {
        if (inputs.PopularReleases.Count == 0)
            return Array.Empty<SpotlightPopularRelease>();

        if (mode == SpotlightMode.Pinned && hasLatestRelease)
        {
            var virtualLatest = BuildVirtualLatestReleaseRow(inputs, hasLatestRelease, requireVirtual: true);
            if (virtualLatest is not null)
            {
                var list = new List<SpotlightPopularRelease>(3) { virtualLatest };
                list.AddRange(inputs.PopularReleases.Take(2));
                return list;
            }
        }

        // PopularRelease mode (no pinned item, no latest release) means the
        // hero IS the first popular release — skip it in the column.
        if (mode == SpotlightMode.PopularRelease)
            return inputs.PopularReleases.Skip(1).Take(3).ToList();

        return inputs.PopularReleases.Take(3).ToList();
    }

    /// <summary>
    /// Build a synthetic popular-release row from the latest-release scalars
    /// so the row template can render it without a special-case binding. Used
    /// in Pinned mode where the latest release is displaced from the hero and
    /// needs to surface in the column instead.
    /// </summary>
    private static SpotlightPopularRelease? BuildVirtualLatestReleaseRow(
        SpotlightSelectionInputs inputs,
        bool hasLatestRelease,
        bool requireVirtual)
    {
        if (!hasLatestRelease) return null;
        var latest = inputs.LatestRelease!;
        if (string.IsNullOrEmpty(latest.Uri) || string.IsNullOrEmpty(latest.Name))
            return null;
        var year = 0;
        if (!string.IsNullOrEmpty(latest.FormattedDate))
        {
            // FormattedDate may be "May 1, 2026" or "2026"; pull the last 4-digit token.
            var m = System.Text.RegularExpressions.Regex.Match(latest.FormattedDate!, @"\b(\d{4})\b");
            if (m.Success) int.TryParse(m.Groups[1].Value, out year);
        }
        return new SpotlightPopularRelease(
            Uri: latest.Uri,
            Name: latest.Name,
            ImageUrl: latest.ImageUrl,
            Type: latest.Type ?? string.Empty,
            Year: year,
            TrackCount: latest.TrackCount);
    }

    private static string BuildLatestReleaseSubtitle(ArtistLatestReleaseResult latest)
    {
        var parts = new List<string>(3);
        if (!string.IsNullOrEmpty(latest.Type)) parts.Add(latest.Type!);
        if (!string.IsNullOrEmpty(latest.FormattedDate)) parts.Add(latest.FormattedDate!);
        if (latest.TrackCount > 0)
            parts.Add(latest.TrackCount == 1 ? "1 track" : $"{latest.TrackCount} tracks");
        return string.Join(" - ", parts);
    }

    private static string FormatPopularSubtitle(SpotlightPopularRelease release)
        => Wavee.UI.Formatters.ReleaseSubtitleFormatter.Format(
            release.Type ?? string.Empty,
            release.Year > 0 ? release.Year : null,
            release.TrackCount > 0 ? release.TrackCount : null,
            Wavee.UI.Formatters.ReleaseSubtitleFormatter.CountNoun.Track);
}
