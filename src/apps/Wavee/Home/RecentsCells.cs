// ── Home/RecentsCells.cs — pure, engine-free (owner G, wave 1) ─────────────────────────────────────────────────────
//
// Role: CORE
// Owner: G
// Wave: 1
// Spec: docs/plans/wavee/home-rebuild-implementation.md §4 (Row item), §6.2, Appendix C
//
// One Recently played grid cell's presentation facts, pulled out of the old `Zones.UI.cs RecentGrid`/`Items.UI.cs
// RowItem` pairing so they're testable without a mounted component: `RecentGrid` (the surviving owner of the
// Recents subscription itself, per the file's own F16/F17 rule) builds the `whenByUri` map and the liked-songs
// count from its live tables, then hands both — plus `Zone.Items` (already capped at
// `ZonePlanner.RecentsCap` = 7) — to `Of` for the actual caption text.
//
// F16 boundary (kept from the old `Items.UI.cs`): "is this card playing right now" stays a per-card BOUND `Prop`
// (`Cards.NowPlaying`/the old `Items.NowPlaying`) wrapping `Playback.Current` — never a caller-computed bool baked
// into a render pass. `RecentsCell.When` is therefore always the plain calendar-ladder caption
// (`WhenCaption.Of(..., playingNow: false, ...)`); the "Playing now" swap and the equalizer badge are the UI
// layer's own reactive concern (`Cards.RowItem`), not this pure builder's.

using System;
using FluentGpu.Localization;
using System.Collections.Generic;
using System.Globalization;
using Wavee;

namespace Wavee.HomeUi;

/// <summary>The Recents grid's row/column arithmetic (Sixth pass, `Zones.UI.cs`'s `RecentGrid`): how many played
/// cells fill out the grid before the fixed history tile, given the responsive column count.</summary>
public static class RecentsPlan
{
    /// <summary>Cells before the trailing history tile, for a two-row grid: <c>2·max(1, cols) − 1</c> (one full row
    /// plus a second row missing its last slot, which the history tile occupies instead), clamped to
    /// <c>0..available</c> so a grid with fewer played cards than a full two rows never asks for more than it has.</summary>
    public static int Cells(int cols, int available)
    {
        int c = Math.Max(1, cols);
        int want = 2 * c - 1;
        if (want < 0) want = 0;
        if (want > available) want = available;
        return want;
    }
}

/// <summary>One Recently played grid cell (row 4 on the canvas): <see cref="Round"/> for an artist's circular art,
/// <see cref="Title"/> the card's own title, <see cref="Detail"/> the "Album · Tristam" / "Playlist · 70 songs" /
/// "1,284 songs" / "Artist" line, <see cref="When"/> the calendar-ladder caption (<see cref="WhenCaption"/>).</summary>
public readonly record struct RecentsCell(HomeCard Card, bool Round, string Title, string Detail, string When);

/// <summary>Builds the ≤7 <see cref="RecentsCell"/>s a <see cref="ZoneKind.RecentGrid"/> zone renders. Pure: every
/// live fact (when a card was played, the Liked Songs count) is a caller-supplied input, never read off a table
/// here — the caller (`RecentGrid`) is the one component allowed to touch `Recents`/`Library` directly.</summary>
public static class RecentsCells
{
    /// <summary>The grid never shows more than 7 played cards — the 8th cell is the fixed HistoryItem
    /// (<see cref="ZonePlanner.RecentsCap"/>'s own cap, re-asserted here so a caller that forgot to cap upstream
    /// still gets a correct grid).</summary>
    public const int MaxCells = 8;

    /// <summary><paramref name="whenByUri"/> maps a card's <see cref="HomeCard.Uri"/> to the Recents row's
    /// <c>PlayedAtMs</c> (a card with no entry — not found in the live Recents window — gets a blank
    /// <see cref="RecentsCell.When"/>). <paramref name="likedSongsCount"/> is the Liked table's row count
    /// (<c>User.Page.Liked.cs</c>), used only for a <see cref="HomeCardKind.Liked"/> card's Detail line.
    /// <paramref name="localize"/> defaults to <see cref="Loc.Get"/>, same optional-delegate shape as
    /// <see cref="WhenCaption.Of"/>.</summary>
    public static IReadOnlyList<RecentsCell> Of(IReadOnlyList<HomeCard> items, IReadOnlyDictionary<string, long> whenByUri,
        int likedSongsCount, long nowMs, TimeZoneInfo tz, CultureInfo culture, Func<string, string>? localize = null)
    {
        Func<string, string> resolve = localize ?? Loc.Get;
        int n = Math.Min(items.Count, MaxCells);
        if (n <= 0) return [];

        var cells = new RecentsCell[n];
        for (int i = 0; i < n; i++)
        {
            var card = items[i];
            string when = whenByUri.TryGetValue(card.Uri, out long playedMs)
                ? WhenCaption.Of(playedMs, nowMs, playingNow: false, tz, culture, localize)
                : "";
            cells[i] = new RecentsCell(card, card.Kind == HomeCardKind.Artist, card.Title,
                DetailOf(card, likedSongsCount, resolve), when);
        }
        return cells;
    }

    /// <summary>The history tile's live "playing now" caption (Sixth pass): a remote device name renders
    /// "playing on {device}" (<see cref="Strings.Home.PlayingOn"/>); no device (nothing playing remotely, or the
    /// active route is this app itself) falls back to the plain <see cref="Strings.Home.When.PlayingNow"/> phrase.
    /// <paramref name="localize"/> defaults to <see cref="Loc.Get"/>, the same optional-delegate shape as
    /// <see cref="WhenCaption.Of"/>.</summary>
    public static string NowCaption(string? device, Func<string, string>? localize = null)
    {
        Func<string, string> resolve = localize ?? Loc.Get;
        return device is { Length: > 0 } d ? Strings.Home.PlayingOn(d) : resolve(Strings.Home.When.PlayingNow);
    }

    /// <summary>"Single - SHAUN" → "Single · SHAUN": the recents feed's own "type - artists" line, or null when the
    /// subtitle is a plain artist line.</summary>
    public static string? TypedLine(string subtitle)
    {
        int dash = subtitle.IndexOf(" - ", StringComparison.Ordinal);
        return dash > 0 ? string.Concat(subtitle.AsSpan(0, dash), " · ", subtitle.AsSpan(dash + 3)) : null;
    }

    /// <summary>The "Album · Tristam" / "Playlist · 70 songs" / "1,284 songs" / "Artist" line (canvas row 4's
    /// caption, minus the trailing "· when" — <see cref="RecentsCell.When"/> is a separate field so the UI layer can
    /// style the "when" half tertiary while the rest stays secondary, per the plan's row-item spec).</summary>
    static string DetailOf(HomeCard card, int likedSongsCount, Func<string, string> resolve)
    {
        switch (card.Kind)
        {
            case HomeCardKind.Artist:
                return card.Subtitle is { Length: > 0 } artistLabel ? artistLabel : resolve(Strings.Home.Release.Artist);

            case HomeCardKind.Liked:
                return Strings.Detail.SongCount(Math.Max(0, likedSongsCount));

            case HomeCardKind.Album:
            {
                string label = resolve(Strings.Home.Release.Album);
                if (card.Subtitle is not { Length: > 0 } by) return label;
                // A recents card's section line already names the release type ("Single - SHAUN"): re-punctuate it
                // ("Single · SHAUN") instead of prefixing a second type word ("Album · Single - SHAUN").
                return TypedLine(by) ?? $"{label} · {by}";
            }

            case HomeCardKind.Playlist:
            {
                string label = resolve(Strings.Home.Release.Playlist);
                int tracks = card.TrackCount;
                return tracks > 0 ? $"{label} · {Strings.Detail.SongCount(tracks)}" : label;
            }

            case HomeCardKind.Track:
                return card.Subtitle is { Length: > 0 } artistLine ? artistLine : resolve(Strings.Home.Release.Song);

            case HomeCardKind.Episode:
            {
                string label = resolve(Strings.Home.Release.Episode);
                return card.Subtitle is { Length: > 0 } show ? $"{label} · {show}" : label;
            }

            case HomeCardKind.Podcast:
                return card.Subtitle is { Length: > 0 } publisher ? publisher : resolve(Strings.Home.Release.Podcast);

            case HomeCardKind.Audiobook:
                return card.Subtitle is { Length: > 0 } author ? author : resolve(Strings.Home.Release.Audiobook);

            default:
                return card.Subtitle ?? "";
        }
    }
}
