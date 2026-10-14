// ── Themes/Classic/Structure/Views/Home/ClassicHomeRecents.cs — Classic's `Home.Zone.RecentGrid` (themes plan 6.1, T10a1) ─────
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
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.HomeUi;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's Recently-played grid.</summary>
internal static class ClassicHomeRecents
{
    static readonly Dictionary<string, long> NoPlays = new(StringComparer.Ordinal);
    static readonly Signal<bool> s_idle = new(false);
    static readonly Func<string?> s_noDevice = static () => null;

    /// <summary>The registered template of <see cref="ViewKeys.HomeZoneRecentGrid"/>.</summary>
    public static Element Template(HomeRecentsProps props, in ViewContext ctx) => Tree(props);

    /// <summary>The embed's <see cref="ComponentEl.SkeletonProxy"/>: the SAME tree at neutral live facts.</summary>
    public static Element Skeleton(Zone zone)
    {
        var cells = RecentsCells.Of(zone.Items, NoPlays, likedSongsCount: 0,
                                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), TimeZoneInfo.Local, System.Globalization.CultureInfo.CurrentCulture);
        return Tree(new HomeRecentsProps
        {
            Zone = zone, Cells = cells, Week = default, Playing = s_idle, RemoteDeviceName = s_noDevice,
        });
    }

    /// <summary>The whole grid: a <see cref="Responsive"/> box that rebuilds the grid at its measured width. The ONE builder both the
    /// template and the skeleton call.</summary>
    static Element Tree(HomeRecentsProps p)
        => Responsive.Of("home:recents", w => Grid(w, p), fallback: HomeModuleLayout.FallbackWidth);

    /// <summary>The grid at one measured width: 2·cols − 1 played tiles + the history tile, two flush rows.</summary>
    static Element Grid(float w, HomeRecentsProps p)
    {
        var cells = p.Cells;
        int cols = GridEl.AutoFillColumnCount(w, ClassicHomeRules.RecentsMinCol, Spacing.M, ClassicHomeRules.RecentsMaxCols);
        int n = RecentsPlan.Cells(cols, cells.Count);
        var rows = new Element[n + 1];
        for (int i = 0; i < n; i++) rows[i] = Row(cells[i], i, p);
        rows[n] = HistoryTile(p.Week);
        return new GridEl
        {
            MinColWidth = ClassicHomeRules.RecentsMinCol, MaxColumns = ClassicHomeRules.RecentsMaxCols,
            ColGap = Spacing.M, RowGap = Spacing.S, RowHeight = ClassicHomeRules.RowH, Children = rows,
        };
    }

    /// <summary>One played tile: <c>Surface(…, Shape.RowTile)</c> whose subtitle is [equalizer · detail · when], the
    /// equalizer PRESENT and the tertiary tail swapped to "playing on {device}" / "Playing now" exactly while the
    /// card relates to what is playing (<see cref="Controls.RelatesNow"/> — a bound prop each, so a track change
    /// touches two node props and re-renders nothing). The equalizer box opts out of skeleton derivation: a
    /// shimmer never shows a "playing" pill. A blank seed card has no uri, so it is keyed by its
    /// <paramref name="index"/> (the shelf's <c>home-shelf-blank:{i}</c> rule) — seven blanks never share a key.</summary>
    static Element Row(RecentsCell cell, int index, HomeRecentsProps p)
    {
        var c = cell.Card;
        string uri = c.Uri;
        string whenTail = cell.When.Length > 0 ? " · " + cell.When : "";
        var remoteDevice = p.RemoteDeviceName;

        var subtitle = new BoxEl
        {
            Direction = 0, Gap = Spacing.XS, AlignItems = FlexAlign.Center, MinWidth = 0f,
            Children =
            [
                new BoxEl
                {
                    Shrink = 0f, Visible = Prop.Of(() => Controls.RelatesNow(uri)),
                    Children = [Controls.Equalizer(p.Playing, Tok.AccentDefault, ClassicHomeRules.EqualizerH)],
                }.Skeletonized(false),
                ClassicHomeZones.Meta(cell.Detail) with { Shrink = 1f },
                ClassicHomeZones.Meta("") with
                {
                    Color = Tok.TextTertiary, Shrink = 0f,
                    Text = Prop.Of(() => Controls.RelatesNow(uri)
                        ? " · " + RecentsCells.NowCaption(remoteDevice())
                        : whenTail),
                },
            ],
        };

        var data = new Controls.CardData(uri, cell.Title, subtitle, c.ImageUrl,
            () => HomeCardNav.Open(in c), () => HomeCardNav.Play(in c),
            Circular: cell.Round, Drag: HomeCardNav.DragOf(in c),
            CoverOverride: c.Kind == HomeCardKind.Liked ? Sidebar.Cover.Liked(Design.Size.Thumb48) : null)
        { Menu = HomeCardNav.MenuOf(in c) };
        return new BoxEl
        {
            Key = c.IsBlank ? "recent-blank:" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) : "recent:" + uri,
            Direction = 1, MinWidth = 0f,
            Children = [Controls.Surface(data, Shape.RowTile)],
        };
    }

    /// <summary>The grid's closing tile — the same row in the dashed <c>Shape.RowOutline</c>: a headphones
    /// <see cref="Controls.IconPlate"/> for a cover, the stock <see cref="SparkBars"/> 7-day strip (left = six days
    /// ago, right = today in the full accent) beside "{n} plays this week", a trailing chevron, no play FAB. Opens
    /// the listening history.</summary>
    static Element HistoryTile(RecentsWeek week)
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
            Direction = 0, Width = ClassicHomeRules.HistoryStripW, Height = ClassicHomeRules.HistoryStripH, Shrink = 0f,
            Children =
            [
                SparkBars.Create(new SparkBarsModel(bars),
                                 SparkBars.DefaultStyle with
                                 {
                                     Height = ClassicHomeRules.HistoryStripH, Gap = ClassicHomeRules.SparkGap, MinBar = ClassicHomeRules.SparkMinBar,
                                 }),
            ],
        };
        var subtitle = new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0f,
            Children = [strip, ClassicHomeZones.Meta(Strings.Home.PlaysThisWeek(week.Total)) with { Shrink = 1f }],
        };
        var data = new Controls.CardData("home:history", Loc.Get(Strings.Home.ListeningHistory), subtitle, null,
            HomeVerbs.OpenRecents, OnPlay: null, ShowMenu: false,
            CoverOverride: Controls.IconPlate(Icons.Headphones, Design.Size.Thumb48, Tok.AccentSubtle, Tok.AccentTextPrimary))
        { Trailing = Ui.Icon(Icons.ChevronRight, 14f, Tok.TextTertiary) with { Shrink = 0f } };
        return new BoxEl
        {
            Key = "recent:history", Direction = 1, MinWidth = 0f,
            Children = [Controls.Surface(data, Shape.RowOutline)],
        };
    }
}
