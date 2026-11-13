// ── Themes/Classic/Structure/Views/Home/ClassicHomeRecents.cs — Classic's `Home.Zone.RecentGrid` (themes plan 6.1, T10a1) ─────
//
// From `Home\Zones.UI.cs` §2.2: the Recently-played grid. A `Surface(…, Shape.RowTile)` grid sized by the engine's own auto-fill count
// (`GridEl.AutoFillColumnCount` → `RecentsPlan.Cells`, always 2·cols − 1 played + the history tile = two full rows), each cell's subtitle
// carrying a live equalizer + "playing on {device}" swap through `Controls.RelatesNow`; the history tile is the same row in `Shape.RowOutline`
// with an `IconPlate` cover and a stock `SparkBars` 7-day strip.
//
// The body is a `Responsive.Of` box: the measured width picks the auto-fill column count through the ENGINE's own formula
// (`GridEl.AutoFillColumnCount`; the grid lays out exactly that count), and `RecentsPlan.Cells` takes 2·cols − 1 played cells so the history tile
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

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's Recently-played grid.</summary>
internal static class ClassicHomeRecents
{
    static readonly Dictionary<string, long> NoPlays = new(StringComparer.Ordinal);
    static readonly Signal<bool> s_idle = new(false);
    static readonly Func<string?> s_noDevice = static () => null;

    /// <summary>The registered template of <see cref="ViewKeys.HomeZoneRecentGrid"/>.</summary>
    public static Element Template(HomeRecentsProps props, in ViewContext ctx)
    {
        var facts = ctx.Tier();
        var layout = ClassicHomeTiers.Of(facts);
        // The first frame is seeded from the tier's own region estimate (the Huge tiles share their band with the hero), never a fixed 1100.
        float estimate = ClassicRegion.EstimateHomeBand(facts, ctx.Hooks.UseContextSignal(Viewport.Size).Peek().Width, hero: false);
        // A FORM CHANGE (the 1920 crossing turns the tiles beside the hero into a band under it) re-renders this template before the grid's box
        // is measured again: built at the old measure under the new record, the grid counted the columns of the narrow band beside the hero
        // (2 at Standard's rule, so 3 tiles) and dropped four tiles for a frame, which remounted them when the measure landed. Until the box
        // reports a new width the grid builds at the new form's estimate, so the tiles it keeps are the ones it will keep.
        var hooks = ctx.Hooks;
        var lastForm = hooks.UseRef((layout.Tier, layout.DaylistBeside));
        var measured = hooks.UseRef(float.NaN);
        var stale = hooks.UseRef(float.NaN);
        var seed = hooks.UseRef(float.NaN);
        if (lastForm.Value != (layout.Tier, layout.DaylistBeside))
        {
            seed.Value = estimate;
            stale.Value = measured.Value;
        }
        lastForm.Value = (layout.Tier, layout.DaylistBeside);
        return Tree(props, layout, estimate, hooks, w =>
        {
            if (!float.IsNaN(seed.Value))
            {
                if (w == stale.Value) w = seed.Value;
                else seed.Value = float.NaN;
            }
            measured.Value = w;
            return w;
        });
    }

    /// <summary>The embed's <see cref="ComponentEl.SkeletonProxy"/>: the SAME tree at neutral live facts.</summary>
    public static Element Skeleton(Zone zone, ClassicHomeLayout layout)
    {
        var cells = RecentsCells.Of(zone.Items, NoPlays, likedSongsCount: 0,
                                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), TimeZoneInfo.Local, System.Globalization.CultureInfo.CurrentCulture);
        return Tree(new HomeRecentsProps
        {
            Zone = zone, Cells = cells, Week = default, Playing = s_idle, RemoteDeviceName = s_noDevice,
        }, layout, firstWidth: layout.Recents.MinCol * layout.Recents.MaxCols + Spacing.M * (layout.Recents.MaxCols - 1), hooks: null, width: null);
    }

    /// <summary>The whole grid: a <see cref="Responsive"/> box that rebuilds the grid when its measured width (through <paramref name="width"/>: the
    /// template's form-change seed) changes its COLUMN count, the only thing the grid reads from the width (the grid fits its own tracks): a width
    /// that keeps the count re-lays the tiles out and re-renders nothing (audit B3bc: every tick of a docking pane rebuilt the grid). The ONE builder
    /// both the template and the skeleton call.
    /// <para>Every tile is built here, once per what it shows (<see cref="ItemMemo"/> over the template's <paramref name="hooks"/>; null for the
    /// proxy), and the grid takes the count its columns keep: a re-render that changes nothing a tile shows (the tier's) hands each tile back
    /// unchanged.</para></summary>
    static Element Tree(HomeRecentsProps p, ClassicHomeLayout layout, float firstWidth, RenderContext? hooks, Func<float, float>? width)
    {
        var cells = p.Cells;
        var shape = TileShape(Shape.RowTile, layout);
        var tiles = new Element[cells.Count];
        for (int i = 0; i < tiles.Length; i++)
            tiles[i] = ItemMemo.Of(hooks, new RowKey(cells[i], i, p.Playing, p.RemoteDeviceName, layout.RecentsArt, shape), s_row);
        var history = ItemMemo.Of(hooks, new HistoryKey(p.Week, DateTime.Today, layout.RecentsArt, TileShape(Shape.RowOutline, layout)), s_history);
        return Responsive.Of("home:recents", w => ColumnsAt(width is null ? w : width(w), layout), cols => Grid(cols, tiles, history, layout),
                             fallback: firstWidth);
    }

    /// <summary>The grid's column count at width <paramref name="w"/>. An auto-fill band counts its columns with the engine's own formula; a
    /// fixed band (Huge, C-Home-Huge: two columns beside the hero) lays out exactly its count as equal star tracks.</summary>
    static int ColumnsAt(float w, ClassicHomeLayout layout)
    {
        var band = layout.Recents;
        return band.MinCol <= 0f ? band.Columns(w) : GridEl.AutoFillColumnCount(w, band.MinCol, Spacing.M, band.MaxCols);
    }

    /// <summary>The grid at <paramref name="cols"/> columns: the tier's rows of columns, less the history tile (two flush rows; Huge fills its band).
    /// The grid lays out exactly the <paramref name="cols"/> it was built for, as equal star tracks, never a count of its own: the tiles it keeps are
    /// chosen for that count, so a grid that counted its own auto-fill columns took a docking panel's landing count on the slide's first frame
    /// while the tiles were still the old count's, and stood a frame at twice its rows over the zone under it (2 columns of 8 tiles).</summary>
    static Element Grid(int cols, Element[] tiles, Element history, ClassicHomeLayout layout)
    {
        int n = layout.RecentsCells(cols, tiles.Length);
        var rows = new Element[n + 1];
        Array.Copy(tiles, rows, n);
        rows[n] = history;
        return new GridEl
        {
            Columns = StarTracks(cols),
            ColGap = Spacing.M, RowGap = Spacing.S, RowHeight = layout.RecentsRowH, Children = rows,
        };
    }

    static TrackSize[] StarTracks(int cols)
    {
        var tracks = new TrackSize[cols];
        for (int i = 0; i < cols; i++) tracks[i] = TrackSize.Star();
        return tracks;
    }

    /// <summary>The tile's shape in a tier: the tuned 48-art row, or Compact's 40 art in a 56 row.</summary>
    static SurfaceShape TileShape(SurfaceShape shape, ClassicHomeLayout layout)
        => layout.RecentsArt == shape.ArtEdge ? shape : shape with { ArtEdge = layout.RecentsArt, MinHeight = layout.RecentsRowH };

    /// <summary>Everything a played tile shows (its <see cref="ItemMemo"/> key): the cell, its place (a blank's key), the live facts it binds, and the
    /// tier's art and shape.</summary>
    readonly record struct RowKey(RecentsCell Cell, int Index, IReadSignal<bool> Playing, Func<string?> RemoteDevice, float Art, SurfaceShape Shape);

    static readonly Func<RowKey, Element> s_row = static k => Row(in k);

    /// <summary>One played tile: <c>Surface(…, Shape.RowTile)</c> whose subtitle is [equalizer · detail · when], the
    /// equalizer PRESENT and the tail swapped to "playing on {device}" / "Playing now" exactly while the
    /// card relates to what is playing (<see cref="Controls.RelatesNow"/> — a bound prop each, so a track change
    /// touches two node props and re-renders nothing). The equalizer box opts out of skeleton derivation: a
    /// shimmer never shows a "playing" pill. A blank seed card has no uri, so it is keyed by its
    /// index (<c>RowKey.Index</c>: the shelf's <c>home-shelf-blank:{i}</c> rule) — seven blanks never share a key.</summary>
    static Element Row(in RowKey k)
    {
        var cell = k.Cell;
        int index = k.Index;
        var playing = k.Playing;
        var c = cell.Card;
        string uri = c.Uri;
        string whenTail = cell.When.Length > 0 ? " · " + cell.When : "";
        var remoteDevice = k.RemoteDevice;

        var subtitle = new BoxEl
        {
            Direction = 0, Gap = Spacing.XS, AlignItems = FlexAlign.Center, MinWidth = 0f,
            Children =
            [
                new BoxEl
                {
                    Shrink = 0f, Visible = Prop.Of(() => Controls.RelatesNow(uri)),
                    Children = [Controls.Equalizer(Controls.PlayingWhenRelates(playing, uri), Tok.AccentDefault, ClassicHomeRules.EqualizerH)],
                }.Skeletonized(false),
                ClassicHomeZones.Meta(cell.Detail) with { Shrink = 1f },
                // The tail wears the line's own secondary ink: the boards draw the whole subtitle in one ink ("Playlist · 6 min ago", C-Home),
                // and the tertiary tail read 3.3-4.4 : 1 (audit B8).
                ClassicHomeZones.Meta("") with
                {
                    Shrink = 0f,
                    Text = Prop.Of(() => Controls.RelatesNow(uri)
                        ? " · " + RecentsCells.NowCaption(remoteDevice())
                        : whenTail),
                },
            ],
        };

        var data = new Controls.CardData(uri, cell.Title, subtitle, c.ImageUrl,
            () => HomeCardNav.Open(in c), () => HomeCardNav.Play(in c),
            Circular: cell.Round, Drag: HomeCardNav.DragOf(in c),
            CoverOverride: c.Kind == HomeCardKind.Liked ? Sidebar.Cover.Liked(k.Art) : null)
        { Menu = HomeCardNav.MenuOf(in c) };
        return new BoxEl
        {
            Key = c.IsBlank ? "recent-blank:" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) : "recent:" + uri,
            Direction = 1, MinWidth = 0f,
            Children = [Controls.Surface(data, k.Shape)],
        };
    }

    /// <summary>Everything the history tile shows (its <see cref="ItemMemo"/> key): the week, the day it ends on, the tier's art and shape.</summary>
    readonly record struct HistoryKey(RecentsWeek Week, DateTime Today, float Art, SurfaceShape Shape);

    static readonly Func<HistoryKey, Element> s_history = static k => HistoryTile(in k);

    /// <summary>The grid's closing tile — the same row in the dashed <c>Shape.RowOutline</c>: a headphones
    /// <see cref="Controls.IconPlate"/> for a cover, the stock <see cref="SparkBars"/> 7-day strip (left = six days
    /// ago, right = today in the full accent) beside "{n} plays this week", a trailing chevron, no play FAB. Opens
    /// the listening history.</summary>
    static Element HistoryTile(in HistoryKey k)
    {
        var week = k.Week;
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        var today = k.Today;
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
            CoverOverride: Controls.IconPlate(Icons.Headphones, k.Art, Tok.AccentSubtle, Tok.AccentTextPrimary))
        { Trailing = Ui.Icon(Icons.ChevronRight, 14f, Tok.TextTertiary) with { Shrink = 0f } };
        return new BoxEl
        {
            Key = "recent:history", Direction = 1, MinWidth = 0f,
            Children = [Controls.Surface(data, k.Shape)],
        };
    }
}
