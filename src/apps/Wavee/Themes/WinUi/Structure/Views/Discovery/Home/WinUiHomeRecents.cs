// ── Themes/WinUi/Structure/Views/Home/WinUiHomeRecents.cs — Classic's `Home.Zone.RecentGrid` (themes plan 6.1, T10a1) ─────
//
// From `Home\Zones.UI.cs` §2.2: the Recently-played grid. A `Surface(…, Shape.RowTile)` grid sized by the engine's own auto-fill count
// (`GridEl.AutoFillColumnCount` → `RecentsPlan.Cells`, always 2·cols − 1 played + the history tile = two full rows), each cell's subtitle
// carrying a live equalizer + "playing on {device}" swap through `Controls.RelatesNow`; the history tile is the same row in `Shape.RowOutline`
// with an `IconPlate` cover and a stock `SparkBars` 7-day strip.
//
// The body is a `Responsive.Of` box: the measured width picks the auto-fill column count through the ENGINE's own formula
// (`GridEl.AutoFillColumnCount`, the one the grid lays out with), and `RecentsPlan.Cells` takes 2·cols − 1 played cells so the history tile
// always closes the second row flush — never a ragged third row. The Recents subscription, the when-map and the week are the core host's
// (`RecentGrid`); this view draws what the props carry.
//
// THE SKELETON is the SAME tree at neutral live facts — no plays (blank "when" captions, an empty week), no liked-songs count — so it reads no
// signal and creates no hook (a proxy is a plain element factory). The seed's blank cards derive to the tile shape (48 art + two text lines)
// and the history tile closes the grid, exactly as the loaded page lays out. The "playing now" swap is a per-row BOUND prop in both paths,
// never a Render read.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.HomeUi;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

/// <summary>Wavee Classic's Recently-played grid.</summary>
internal static class WinUiHomeRecents
{
    static readonly Dictionary<string, long> NoPlays = new(StringComparer.Ordinal);
    static readonly Signal<bool> s_idle = new(false);
    static readonly Func<string?> s_noDevice = static () => null;

    /// <summary>The registered template of <see cref="ViewKeys.HomeZoneRecentGrid"/>.</summary>
    public static Element Template(HomeRecentsProps props, in ViewContext ctx)
    {
        var facts = ctx.Tier();
        // The first frame is seeded from the tier's own region estimate (the Huge tiles share their band with the hero), never a fixed 1100.
        return Tree(props, WinUiHomeTiers.Of(facts), WinUiRegion.EstimateHomeBand(facts, ctx.Hooks.UseContextSignal(Viewport.Size).Peek().Width, hero: false));
    }

    /// <summary>The embed's <see cref="ComponentEl.SkeletonProxy"/>: the SAME tree at neutral live facts.</summary>
    public static Element Skeleton(Zone zone, WinUiHomeLayout layout)
    {
        var cells = RecentsCells.Of(zone.Items, NoPlays, likedSongsCount: 0,
                                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), TimeZoneInfo.Local, System.Globalization.CultureInfo.CurrentCulture);
        return Tree(new HomeRecentsProps
        {
            Zone = zone, Cells = cells, Week = default, Playing = s_idle, RemoteDeviceName = s_noDevice,
        }, layout, firstWidth: MathF.Max(layout.Recents.MinCol, 160f) * layout.Recents.MaxCols + layout.RecentsGap * (layout.Recents.MaxCols - 1));
    }

    /// <summary>The whole grid: a <see cref="Responsive"/> box that rebuilds the grid at its measured width. The ONE builder both the
    /// template and the skeleton call.</summary>
    static Element Tree(HomeRecentsProps p, WinUiHomeLayout layout, float firstWidth)
        => Responsive.Of("home:recents", w => Grid(w, p, layout), fallback: firstWidth);

    /// <summary>The grid at one measured width: the tier's rows of columns, less the history tile (two flush rows; Huge fills its band).</summary>
    static Element Grid(float w, HomeRecentsProps p, WinUiHomeLayout layout)
    {
        var cells = p.Cells;
        var band = layout.Recents;
        int cols = GridEl.AutoFillColumnCount(w, band.Floor, layout.RecentsGap, band.MaxCols);
        int n = layout.RecentsCells(cols, cells.Count);
        var rows = new Element[n + 1];
        for (int i = 0; i < n; i++) rows[i] = Row(cells[i], i, p, layout);
        rows[n] = HistoryTile(p.Week, layout);
        return new GridEl
        {
            MinColWidth = band.Floor, MaxColumns = band.MaxCols,
            ColGap = layout.RecentsGap, RowGap = layout.RecentsGap, RowHeight = layout.RecentsRowH, Children = rows,
        };
    }

    /// <summary>The tile's shape in a tier: the tuned 48-art row, or Compact's 40 art in a 56 row.</summary>
    static SurfaceShape TileShape(SurfaceShape shape, WinUiHomeLayout layout)
        => layout.RecentsArt == shape.ArtEdge && layout.RecentsRowH == shape.MinHeight ? shape : shape with { ArtEdge = layout.RecentsArt, MinHeight = layout.RecentsRowH };

    /// <summary>One played tile: <c>Surface(…, Shape.RowTile)</c> whose subtitle is [equalizer · detail · when], the
    /// equalizer PRESENT and the tertiary tail swapped to "playing on {device}" / "Playing now" exactly while the
    /// card relates to what is playing (<see cref="Controls.RelatesNow"/> — a bound prop each, so a track change
    /// touches two node props and re-renders nothing). The equalizer box opts out of skeleton derivation: a
    /// shimmer never shows a "playing" pill. A blank seed card has no uri, so it is keyed by its
    /// <paramref name="index"/> (the shelf's <c>home-shelf-blank:{i}</c> rule) — seven blanks never share a key.</summary>
    static Element Row(RecentsCell cell, int index, HomeRecentsProps p, WinUiHomeLayout layout)
    {
        var c = cell.Card;
        string uri = c.Uri;
        var remoteDevice = p.RemoteDeviceName;

        // W-Home: the tile's second line is the detail alone ("Album · Edward Maya"); the "when" is a 12-px label at the tile's end ("Yesterday", "Tue"),
        // the accent "Playing now" (or "Playing on {device}") exactly while the card relates to what is playing (a bound prop each: a track change
        // touches node props and re-renders nothing). The equalizer rides the subtitle while it plays.
        var subtitle = new BoxEl
        {
            Direction = 0, Gap = Spacing.XS, AlignItems = FlexAlign.Center, MinWidth = 0f,
            Children =
            [
                new BoxEl
                {
                    Shrink = 0f, Visible = Prop.Of(() => Controls.RelatesNow(uri)),
                    Children = [Controls.Equalizer(p.Playing, Tok.AccentDefault, WinUiHomeRules.EqualizerH)],
                }.Skeletonized(false),
                WinUiHomeZones.Meta(cell.Detail) with { Shrink = 1f },
            ],
        };
        string when = cell.When;
        Element trailing = Ui.Caption("") with
        {
            Shrink = 0f, MaxLines = 1, Wrap = TextWrap.NoWrap,
            Color = Prop.Of(() => Controls.RelatesNow(uri) ? Tok.AccentTextPrimary : Tok.TextTertiary),
            Text = Prop.Of(() => Controls.RelatesNow(uri) ? RecentsCells.NowCaption(remoteDevice()) : when),
        };

        var data = new Controls.CardData(uri, cell.Title, subtitle, c.ImageUrl,
            () => HomeCardNav.Open(in c), () => HomeCardNav.Play(in c),
            Circular: cell.Round, Drag: HomeCardNav.DragOf(in c),
            CoverOverride: c.Kind == HomeCardKind.Liked ? Sidebar.Cover.Liked(layout.RecentsArt) : null)
        { Menu = HomeCardNav.MenuOf(in c), Trailing = trailing };
        return new BoxEl
        {
            Key = c.IsBlank ? "recent-blank:" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) : "recent:" + uri,
            Direction = 1, MinWidth = 0f,
            Children = [Controls.Surface(data, TileShape(Shape.RowTile, layout))],
        };
    }

    /// <summary>The grid's closing tile — the same row in the dashed <c>Shape.RowOutline</c>: a headphones
    /// <see cref="Controls.IconPlate"/> for a cover, the stock <see cref="SparkBars"/> 7-day strip (left = six days
    /// ago, right = today in the full accent) beside "{n} plays this week", a trailing chevron, no play FAB. Opens
    /// the listening history.</summary>
    static Element HistoryTile(RecentsWeek week, WinUiHomeLayout layout)
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        var today = DateTime.Today;
        var bars = new SparkBar[7];
        for (int i = 0; i < bars.Length; i++)
        {
            int daysAgo = 6 - i;
            int n = week[daysAgo];
            string day = culture.DateTimeFormat.GetAbbreviatedDayName(today.AddDays(-daysAgo).DayOfWeek);
            bars[i] = new SparkBar(n, day + " · " + Strings.Home.Plays(n), Accent: daysAgo == 0);
        }
        var strip = new BoxEl
        {
            Direction = 0, Width = WinUiHomeRules.HistoryStripW, Height = WinUiHomeRules.HistoryStripH, Shrink = 0f,
            Children =
            [
                SparkBars.Create(new SparkBarsModel(bars),
                                 SparkBars.DefaultStyle with
                                 {
                                     Height = WinUiHomeRules.HistoryStripH, Gap = WinUiHomeRules.SparkGap, MinBar = WinUiHomeRules.SparkMinBar,
                                 }),
            ],
        };
        var subtitle = new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0f,
            Children = [strip, WinUiHomeZones.Meta(Strings.Home.PlaysThisWeek(week.Total)) with { Shrink = 1f }],
        };
        var data = new Controls.CardData("home:history", Loc.Get(Strings.Home.ListeningHistory), subtitle, null,
            HomeVerbs.OpenRecents, OnPlay: null, ShowMenu: false,
            CoverOverride: Controls.IconPlate(Icons.Headphones, layout.RecentsArt, Tok.AccentSubtle, Tok.AccentTextPrimary))
        { Trailing = Ui.Icon(Icons.ChevronRight, 14f, Tok.TextTertiary) with { Shrink = 0f } };
        return new BoxEl
        {
            Key = "recent:history", Direction = 1, MinWidth = 0f,
            Children = [Controls.Surface(data, TileShape(Shape.RowOutline, layout))],
        };
    }
}
