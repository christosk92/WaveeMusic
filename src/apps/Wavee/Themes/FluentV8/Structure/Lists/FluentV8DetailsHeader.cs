// ── Themes/FluentV8/Structure/Lists/FluentV8DetailsHeader.cs — the track table's column header as a v8 DetailsHeader (SPIKE) ─────────────
//
// The one piece of the track table FluentV8 draws itself: the column header. The rows, the command bar, the filter card, the placeholder and
// the group head stay the base structure's (`FluentV8Structure.Register` rebuilds the base `TrackListStyle` with this header). A v8
// DetailsHeader names each column in 14 semibold neutralPrimary (not WinUI's 12 secondary caption), shows SortUp / SortDown after the sorted
// column's name, fills a column neutralLighter under the pointer with square corners, and rules the band with the 1-px bodyDivider. The grid
// is the rows' own (the same tracks, column gap and inset), so every name sits over its lane.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.FluentV8;

using CellKey = global::Wavee.Track.CellKey;
using RowMetrics = global::Wavee.Track.RowMetrics;
using SortColumn = global::Wavee.Track.SortColumn;
using SortSpec = global::Wavee.Track.SortSpec;
using TableRules = global::Wavee.Track.TableRules;

/// <summary>The v8 DetailsHeader over the base rows.</summary>
internal static class FluentV8DetailsHeader
{
    /// <summary>The header's key.</summary>
    public const string HeaderKey = "fluentv8.list.header";

    /// <summary>The base table style with this header in place of the base one.</summary>
    public static TrackListStyle Over(TrackListStyle b)
    {
        var c = b.Chrome;
        var chrome = new TrackChromeTemplates(Header, c.Commands, c.FilterCard, c.Placeholder, c.GroupHeader)
        {
            UnavailableBar = c.UnavailableBar,
            UnavailableBarExtent = c.UnavailableBarExtent,
            HidesChromeOnEmptyLead = c.HidesChromeOnEmptyLead,
        };
        return new TrackListStyle(b.Stamp, b.Modern, b.Classic, chrome);
    }

    static TextEl Name(string text) => new(text)
    {
        Size = FluentV8ListRules.HeaderText, LineHeight = FluentV8ListRules.HeaderLine, Weight = FluentV8ListRules.HeaderWeight,
        Color = Tok.TextPrimary, MinWidth = 0f, Shrink = 1f, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis,
    };

    static Element Header(TrackHeaderArgs a)
    {
        var set = a.Set;
        var sort = a.Sort;
        var cells = new List<Element>(a.Tracks.Length);
        void Add(string key, Element cell) => cells.Add(cell with { Key = key, Layout = Design.Reduced ? null : FluentV8ListRules.CellArrange });

        if (!set.IndexHidden) Add(CellKey.Num, Column(a, Loc.Get(Strings.Detail.Column.Number), SortColumn.Index, sort, FlexJustify.Center));
        if (set.Heart) Add(CellKey.Heart, new BoxEl());
        if (set.Thumb) Add(CellKey.Art, new BoxEl());
        bool artistLane = set.Artist;
        string title = !artistLane && sort.Column == SortColumn.Artist ? Loc.Get(Strings.Detail.Column.Artist) : Loc.Get(Strings.Detail.Column.Title);
        Add(CellKey.Title, Column(a, title, SortColumn.Title, sort, FlexJustify.Start, artistLane));
        if (set.Artist) Add(CellKey.Artist, Column(a, Loc.Get(Strings.Detail.Column.Artist), SortColumn.Artist, sort, FlexJustify.Start, artistColumn: true));
        if (set.Album) Add(CellKey.Album, Column(a, Loc.Get(Strings.Detail.Column.Album), SortColumn.Album, sort, FlexJustify.Start));
        if (set.By) Add(CellKey.By, Plain(Loc.Get(Strings.Detail.Column.AddedBy), FlexJustify.Start));
        if (set.Date) Add(CellKey.Date, Column(a, Loc.Get(Strings.Detail.Column.DateAdded), SortColumn.DateAdded, sort, FlexJustify.Start));
        if (set.Plays) Add(CellKey.Plays, Column(a, Loc.Get(Strings.Detail.Column.Plays), SortColumn.Plays, sort, FlexJustify.End));
        if (RowMetrics.ShowTempo(in set)) Add(CellKey.Tempo, Plain(Loc.Get(Strings.Detail.Column.Tempo), FlexJustify.End));
        if (RowMetrics.ShowHeartTrailing(in set)) Add(CellKey.HeartTrailing, new BoxEl());
        if (RowMetrics.ShowDuration(in set)) Add(CellKey.Duration, Column(a, Loc.Get(Strings.Detail.Column.Time), SortColumn.Duration, sort, FlexJustify.End));
        if (set.Actions) Add(CellKey.More, new BoxEl());
        if (set.Expand) Add(CellKey.Expand, new BoxEl());

        float padX = FluentV8ListRules.RowPad;
        float headerH = TableRules.HeaderHeightFor(set.Classic);
        return new BoxEl
        {
            Key = HeaderKey, Direction = 1, MinWidth = 0f,
            Children =
            [
                new BoxEl
                {
                    Key = "fluentv8.list.header-band", Direction = 1, ClipToBounds = true, MinWidth = 0f, Shrink = 0f,
                    Height = set.Compact ? 0f : headerH, Animate = FluentV8ListRules.HeightReflow,
                    Children =
                    [
                        new BoxEl
                        {
                            Direction = 1, Shrink = 0f,
                            Padding = new Edges4(a.Checks ? FluentV8ListRules.CheckLaneShift : 0f, 0f, 0f, 0f),
                            Animate = FluentV8ListRules.HeaderShift,
                            Children =
                            [
                                new GridEl
                                {
                                    Columns = a.Tracks, ColGap = RowMetrics.ColGapFor(set.Tier),
                                    Padding = new Edges4(padX, 0f, padX, 0f),
                                    RowHeight = headerH, Children = cells.ToArray(),
                                },
                            ],
                        },
                    ],
                },
                new BoxEl { Key = "fluentv8.list.header-rule", Height = FluentV8ListRules.Divider, Shrink = 0f, Fill = Prop.Of(static () => Tok.StrokeDividerDefault) },
            ],
        };
    }

    static Element Plain(string label, FlexJustify justify) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Justify = justify, MinWidth = 0f, ClipToBounds = true,
        Children = [Name(label)],
    };

    /// <summary>A sortable column: its name, then the sort arrow while it is the sort (SortUp ascending, SortDown descending); a click sorts.</summary>
    static Element Column(TrackHeaderArgs a, string label, SortColumn col, SortSpec sort, FlexJustify justify, bool artistColumn = false)
    {
        bool sorted = TableRules.HeaderActive(col, sort.Column, artistColumn) && (col != SortColumn.Index || sort.Descending);
        var name = Name(label);
        Element[] kids = !sorted ? [name]
            : [name, Ui.Icon(sort.Descending ? FluentV8ListRules.SortDownGlyph : FluentV8ListRules.SortUpGlyph, FluentV8ListRules.SortGlyph, Tok.TextSecondary) with { Shrink = 0f }];
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Justify = justify, Gap = FluentV8ListRules.SortGap, MinWidth = 0f, ClipToBounds = true,
            HoverFill = Prop.Of(static () => R.Layer.RowHover), PressedFill = Prop.Of(static () => R.Layer.RowPressed),
            Role = AutomationRole.Button, Cursor = CursorId.Hand,
            OnClick = () => a.OnSort(col, artistColumn),
            Children = kids,
        };
    }
}
