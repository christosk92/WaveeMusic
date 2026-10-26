// ── Home/Zones.Hosts.cs — the two zone bodies that read core state: the Recents grid and the Charts block (T10a1) ─────────
//
// Role: CORE hosts (the pictures are Classic's `Home.Zone.RecentGrid` and `Home.Charts` views:
//       `Themes\Classic\Structure\Views\Home\ClassicHomeRecents.cs`, `ClassicHomeCharts.cs`; every other zone body is a pure function of its
//       `Zone` and is drawn by the Home page's own zone builders, `ClassicHomeZones`)
// Spec: docs/plans/wavee/home-rebuild-implementation.md "Fifth pass — stock controls" + "Sixth pass".
//
// WHAT STAYS HERE. `RecentGrid` owns the Recents subscription: a Recents write re-renders THIS host only. Cell text comes from
// <see cref="RecentsCells.Of"/>; `Zone.Items` is the planner's capped candidate list; the week's per-day play counts
// (<see cref="WeekSummary"/>) are folded once per Recents version alongside the when-map. `ChartsBlockView` demands the Featured Charts
// section once per scope through the query layer (<see cref="Home.EnsureSection"/> as a browse section — the demand Browse's Charts band
// and the section drill use) and derives the <see cref="ChartTilesModel"/>, a memo that re-renders only when a tile's target actually
// changes. The two chart titles are the same literals the previous pass carried — the loc table has captions for these tiles
// (<c>home.chartsCaption.*</c>) but no title keys yet.

using System.Collections.Generic;
using System.Globalization;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using Wavee;
using Wavee.Theming.Contracts;
using Slots = FluentGpu.Theming.Slots;

namespace Wavee.HomeUi;

/// <summary>Props for <see cref="RecentGrid"/>: the Recents zone by value (a re-plan with equal content is a no-op at the embed boundary).</summary>
public sealed record RecentGridProps(Zone Zone)
{
    public bool Equals(RecentGridProps? o) => o is not null && Zone.Equals(o.Zone);
    public override int GetHashCode() => Zone.GetHashCode();
}

/// <summary>The Recents grid's host: the one component that subscribes to Recents. It folds the plays into the played cells and the
/// week, and mounts `Home.Zone.RecentGrid` with them; the grid itself (a `Responsive` box that picks the
/// column count through the ENGINE's own formula, 2·cols − 1 played cells so the history tile closes the second row flush) is the view's.</summary>
[CoreHost]
public sealed class RecentGrid : Component
{
    sealed record Cache(Dictionary<string, long> When, RecentsWeek Week, uint Version);
    Cache? _cache;

    public override Element Render()
    {
        var p = UseProps<RecentGridProps>();
        var items = p.Zone.Items;

        var handle = Recents.Me;
        UseEffect(() => { if (handle.Slot > 0) Entities.EnsureEdge(FetchEdge.Recents, handle.Slot); });

        _ = Recents.Changed.Value;   // subscribe
        var cache = BuildCache(handle);

        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var cells = RecentsCells.Of(items, cache.When, User.Me.LikedTrackSlots.Length, nowMs, TimeZoneInfo.Local, CultureInfo.CurrentCulture);
        return Slots.View(ViewKeys.HomeZoneRecentGrid, new HomeRecentsProps
        {
            Zone = p.Zone, Cells = cells, Week = cache.Week, Playing = Playback.IsPlaying, RemoteDeviceName = RemoteDeviceName,
        });
    }

    /// <summary>The Connect device the bar itself says it is "Playing on", or null while playback is local (or
    /// nobody's). The PlayerBar idiom (Shell/Shell.PlayerBar.cs `DeviceRoster.RemoteSlot`); every read subscribes,
    /// so the bound caption follows a transfer.</summary>
    static string? RemoteDeviceName()
    {
        _ = Playback.Devices.Changed.Value;
        var rows = Playback.Devices.Rows;
        int slot = Shell.DeviceRoster.RemoteSlot(Playback.OwnerSignal.Value, Playback.ActiveDeviceSlot.Value, rows);
        return slot >= 0 ? rows[slot].Name : null;
    }

    Cache BuildCache(Recents handle)
    {
        uint version = handle.Version;
        if (_cache is { } c && c.Version == version) return c;

        var snapshot = RecentsSnapshot.Of(in handle);
        var when = new Dictionary<string, long>(snapshot.Count, StringComparer.Ordinal);
        for (int i = 0; i < snapshot.Rows.Length; i++)
        {
            var target = snapshot.Targets[i];
            string uri = Entities.TableFor(target.Kind) is { } table && (uint)target.Slot < (uint)table.Count
                ? table.Id[target.Slot].ToString() : "";
            if (uri.Length > 0) when[uri] = snapshot.Rows[i].PlayedAtMs;
        }
        var week = WeekSummary.Of(snapshot.Rows, DateOnly.FromDateTime(DateTime.Now), TimeZoneInfo.Local);
        return _cache = new Cache(when, week, version);
    }
}

/// <summary>The Charts block's host (RCA 2026-09-30): demands the Featured Charts section once per scope and renders from the derived
/// <see cref="ChartTilesModel"/>, a memo that re-renders only when a tile's target actually changes. "Charts" opens the Charts CATEGORY
/// page (<see cref="ChartPages.Charts"/>, the Browse directory's own route for it); "Top 50" / "Viral 50" open the actual playlists, picked
/// data-driven from the Featured Charts section's items (<see cref="ChartTilePick"/>), each on the category page until that section lands or
/// when it carries no match (<see cref="HomeVerbs.OpenChart"/>).</summary>
[CoreHost]
public sealed class ChartsBlockView : Component
{
    static readonly Action s_demand = static () => Home.EnsureSection(Entities.BrowseSection(ChartSections.Featured), browse: true);
    static readonly Func<ChartTilesModel> s_read = ReadChartTiles;

    public override Element Render()
    {
        uint epoch = Entities.ScopeEpoch.Value;
        UseEffect(s_demand, DepKey.From((int)epoch));
        var model = UseComputed(s_read);
        return Slots.View(ViewKeys.HomeCharts, new HomeChartsProps(model.Value));
    }

    /// <summary>The tile model from the Featured Charts section's items, in section order, through the Home model reader
    /// (<see cref="SectionReader.Of"/>). Subscribes to the section rows, their card edge and the playlist rows (a card's
    /// title lands on its playlist row); before the section lands the list is empty and both tiles read the fallback.</summary>
    static ChartTilesModel ReadChartTiles()
    {
        _ = Entities.ScopeEpoch.Value;
        var scope = Entities.Current;
        _ = scope.Sections.Changed.Value;
        _ = scope.Edges.SectionCards.Changed.Value;
        _ = scope.Playlists.Changed.Value;
        var featured = Entities.BrowseSection(ChartSections.Featured);
        if (!featured.IsValid) return ChartTilesModel.Fallback;
        var cards = SectionReader.Of(featured).Cards;
        if (cards.Count == 0) return ChartTilesModel.Fallback;
        var items = new FeaturedChartItem[cards.Count];
        for (int i = 0; i < items.Length; i++)
        {
            var c = cards[i];
            items[i] = c.IsBlank ? new FeaturedChartItem("", null) : new FeaturedChartItem(c.Uri, c.Title);
        }
        return ChartTilePick.Of(items);
    }
}
