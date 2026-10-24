// ── Themes/WinUi/Structure/Lists/WinUiTrackChrome.cs — WinUI's track table chrome (themes plan 6.2; P6 W3) ────────────────────────────────
//
// The chrome half of WinUI's `TrackListStyle` (W-Album's header row and its Find / sort / Filter / Select bar, W-MultiSelect's selection bar, W-Unavailable's bar):
//
//   header        the lanes' words in Caption 12 and the secondary ink (the sorted lane primary, with its caret), sentence case as written; "#" is a word and a
//                 sort target (W-Album: "# Title ... Plays Time"); the same tracks, gutter and keys as the rows; the band's hairline across the list. The Compact
//                 row form draws the hairline alone (its header height is 0).
//   command bar   flat words (Body 14, the secondary ink): no plates at rest, the subtle fill on hover, the accent text when active; r4. Find, the order's word
//                 with its chevron, Filter, Select. The selection mode is the card (white 5 %, r8: W-MultiSelect's toolbar; the accent tint at Compact).
//   "Row size"    the "..." menu's radio rows over WinUI's two densities (Default 40 / 48, Compact 32 / 40). The checked row is the rung the style PUBLISHED
//                 (`StyleMetrics.TrackRows.Rung`), never a read of the style's option (WVTH005); a pick is a request for the style's `rowDensity` (the id the
//                 qualified `style.rowDensity` names, NOT Classic's `density`: Classic's stand-in sent that type and was a no-op) which the app applies on the theme
//                 transaction.
//   filter card   the immediate-apply card: r8, the WinUI words, a tinted combo on a filtering row.
//   placeholder   the blank row: the same tracks, gap, height and padding as the live row.
//   group head    a date group's name and count, on the rows' gutter.
//   unavailable   the "songs here can't be played" bar (W-Unavailable's InfoBar): the card, the accent info glyph, the sentence, "Hide them"; its band reserved
//                 while shown and released with the disclosure reveal, so the rows ride it.
//
// WHAT THE CORE KEEPS. The sort / filter / selection state and every decision on it, the commands that fit, every overlay and its focus, the menus' items. A
// template draws what its args carry and calls the verbs it was handed (WVTH002). Numbers are `Rules\WinUiListRules.cs`.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.WinUi;

using CellKey = global::Wavee.Track.CellKey;
using ColumnSet = global::Wavee.Track.ColumnSet;
using CommandBarLayout = global::Wavee.Track.CommandBarLayout;
using DetailLayout = global::Wavee.Detail.VerticalLayout;
using Lane = global::Wavee.Track.Lane;
using RowMetrics = global::Wavee.Track.RowMetrics;
using SortColumn = global::Wavee.Track.SortColumn;
using SortSpec = global::Wavee.Track.SortSpec;
using TableRules = global::Wavee.Track.TableRules;

/// <summary>WinUi's chrome for the track table.</summary>
internal static class WinUiTrackChrome
{
    /// <summary>The header's key (a test finds WinUI's header by it).</summary>
    public const string HeaderKey = "winui.list.header";

    /// <summary>The chrome WinUi registers as <c>TrackListStyle.Chrome</c>. Stateless.</summary>
    public static readonly TrackChromeTemplates Templates = new(Header, CommandKit.For(WidthTier.Standard), FilterCard, Placeholder, GroupHeader)
    {
        UnavailableBar = UnavailableBar,
        HidesChromeOnEmptyLead = true,   // Z-State-Empty-Playlist: the lead and its finder are the page, no sort, filter or header over nothing
        UnavailableBarExtent = static tiers => WinUiListRules.UnavailableExtent(tiers.Policy is null ? WidthTier.Standard : tiers.Window),
    };

    static WidthTier TierOf(in TierFacts tiers) => tiers.Policy is null ? WidthTier.Standard : tiers.Window;

    /// <summary>A header word: Caption 12 (the ramp's rung), sentence case as written.</summary>
    static TextEl Word(string text, ColorF ink)
    {
        var caption = R.Type.DenseMeta("");
        return new TextEl(text)
        {
            Size = caption.Size, LineHeight = caption.LineHeight, Color = ink,
            MinWidth = 0f, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis,
        };
    }

    /// <summary>A command word: Body 14 (W-Album's Find, order, Filter, Select), sentence case as written.</summary>
    static TextEl CommandWord(string text, ColorF ink) => R.Type.DenseTitle(text) with
    {
        Color = ink, MinWidth = 0f, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis,
    };

    // ══ THE UNAVAILABLE-SONGS BAR ════════════════════════════════════════════════════════════════════════════════════════

    static Element UnavailableBar(TrackUnavailableBarArgs a)
    {
        var tier = TierOf(a.Tiers);
        bool compact = tier == WidthTier.Compact;
        var meta = R.Type.DenseTitle("");
        // The core's inset is the Modern plates' 8; WinUI's plates are flush with the list, so the bar is too (the hero arm's gutter keeps what it adds).
        float inset = MathF.Max(0f, a.InsetX - RowMetrics.RowInset);
        Element bar = new BoxEl
        {
            Key = "unavailable:bar", Direction = 0, AlignItems = FlexAlign.Center, Gap = WinUiListRules.UnavailableGap, MinWidth = 0f,
            Height = compact ? WinUiListRules.UnavailableCompactHeight : WinUiListRules.UnavailableHeight, Shrink = 0f,
            // The Compact bar's taller target eases through real layout (audit B9), the rows under it riding it.
            Animate = Design.Reduced ? null : WinUiDiscoveryMotion.HeightReflow,
            Padding = WinUiListRules.UnavailablePadding, Margin = new Edges4(0f, 0f, 0f, WinUiListRules.UnavailableBelow),
            Corners = CornerRadius4.All(WinUiListRules.CommandRadius), BorderWidth = 1f,
            Fill = Prop.Of(static () => Tok.FillCardDefault), BorderColor = Prop.Of(static () => Tok.StrokeCardDefault),
            Children =
            [
                Icon(Icons.Info, WinUiListRules.UnavailableGlyph, R.Ink.AccentAction) with { Shrink = 0f },
                new TextEl(WinUiListRules.UnavailableExplains(tier) ? a.Explained : a.Summary)
                {
                    Size = meta.Size, LineHeight = meta.LineHeight, Color = Tok.TextPrimary, Grow = 1f, Shrink = 1f, MinWidth = 0f,
                    MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                },
                Button.Create(a.ActionLabel, a.Hide, ButtonAppearance.Standard, isEnabled: a.Shown) with { Key = "unavailable:hide", Shrink = 0f },
            ],
        };
        return new BoxEl
        {
            Key = "unavailable", Direction = 1, MinWidth = 0f, ClipToBounds = true, Shrink = 0f,
            Height = a.Shown ? WinUiListRules.UnavailableExtent(tier) : 0f,
            Padding = new Edges4(inset, 0f, inset, 0f),
            Animate = Design.Reduced ? null : Design.Reveal.Resize,
            Children = [bar],
        };
    }

    // ══ THE PLACEHOLDER ══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The blank row before a slot's reveal edge: a <c>GridEl</c> with the live row's tracks, gap, height and padding and one empty box
    /// per track (the type change against the live row's box is what makes the crossing a remount).</summary>
    static Element Placeholder(in ColumnSet set, TrackSize[] tracks, float rowH, float padX)
    {
        var cells = new Element[tracks.Length];
        for (int i = 0; i < cells.Length; i++) cells[i] = new BoxEl();
        return new GridEl
        {
            Key = "row:shim",
            Columns = tracks, ColGap = RowMetrics.ColGapFor(set.Tier), RowHeight = rowH, Grow = 1f,
            Padding = new Edges4(WinUiListRules.RowPad, 0f, WinUiListRules.RowPad, 0f),   // the live rows' own inset, not the core's gutter
            Children = cells,
        };
    }

    // ══ THE DATE GROUP HEAD ══════════════════════════════════════════════════════════════════════════════════════════════

    static Element GroupHeader(Func<Track.DateGroupHead?> head, float padX)
    {
        var body = R.Type.DenseTitle("");
        return new BoxEl
        {
            Direction = 0, Height = WinUiListRules.GroupHeadHeight, AlignItems = FlexAlign.End, Gap = WinUiListRules.GroupHeadGap,
            MinWidth = 0f, ClipToBounds = true, Visible = Prop.Of(() => head() is not null),
            Padding = new Edges4(WinUiListRules.RowPad, 0f, WinUiListRules.RowPad, WinUiListRules.GroupHeadPadBottom),
            Children =
            [
                // The group's name in Body Strong 14/20, its count beside it in the secondary ink.
                new TextEl(Prop.Of(() => head()?.Label ?? ""))
                {
                    Size = WinUiListRules.GroupHeadSize, LineHeight = WinUiListRules.GroupHeadLine, Weight = 600,
                    Color = Tok.TextPrimary, MaxLines = 1, Shrink = 0f,
                },
                new TextEl(Prop.Of(() => head()?.Count ?? ""))
                {
                    Size = body.Size, LineHeight = body.LineHeight, Color = Tok.TextSecondary,
                    MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
                },
            ],
        };
    }

    // ══ THE COLUMN HEADER ════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The header grid over the rows' tracks, keyed like the row cells (a lane that leaves is removed, never reconciled against its
    /// neighbour); shifted over while the check lane is in; the band's hairline under it, across the list. The header pays the rows' own
    /// inset (<see cref="WinUiListRules.RowPad"/>), so "#" stays over the numbers.</summary>
    static Element Header(TrackHeaderArgs a)
    {
        var set = a.Set;
        var sort = a.Sort;
        var cells = new List<Element>(a.Tracks.Length);
        void Add(string key, Element cell) => cells.Add(cell with { Key = key });

        if (!set.IndexHidden) Add(CellKey.Num, IndexSortCell(a, sort));
        if (set.Heart) Add(CellKey.Heart, new BoxEl());
        if (set.Thumb) Add(CellKey.Art, new BoxEl());
        var sortSignal = a.SortSignal;
        bool artistLane = set.Artist;
        Element title = Embed.Comp(() => new LabelComponent(sortSignal, artistLane))
            with { Key = "sort-label:w" + (artistLane ? ":a" : "") };
        Add(CellKey.Title, SortCell(a, title, SortColumn.Title, sort, FlexJustify.Start, artistLane));
        if (set.Artist)
            Add(CellKey.Artist, SortCell(a, HLabel(Loc.Get(Strings.Detail.Column.Artist), SortColumn.Artist, sort), SortColumn.Artist, sort, FlexJustify.Start, artistColumn: true));
        if (set.Album)
            Add(CellKey.Album, SortCell(a, HLabel(Loc.Get(Strings.Detail.Column.Album), SortColumn.Album, sort), SortColumn.Album, sort, FlexJustify.Start));
        if (set.By) Add(CellKey.By, PlainHeader(Loc.Get(Strings.Detail.Column.AddedBy), FlexJustify.Start));
        if (set.Date)
            Add(CellKey.Date, SortCell(a, HLabel(Loc.Get(Strings.Detail.Column.DateAdded), SortColumn.DateAdded, sort), SortColumn.DateAdded, sort, FlexJustify.Start));
        if (set.Plays)
            Add(CellKey.Plays, SortCell(a, HLabel(Loc.Get(Strings.Detail.Column.Plays), SortColumn.Plays, sort), SortColumn.Plays, sort, FlexJustify.End));
        if (RowMetrics.ShowTempo(in set)) Add(CellKey.Tempo, PlainHeader(Loc.Get(Strings.Detail.Column.Tempo), FlexJustify.End));
        if (RowMetrics.ShowHeartTrailing(in set)) Add(CellKey.HeartTrailing, new BoxEl());
        // WinUI words the time lane ("Time", W-Album), never a clock glyph.
        if (RowMetrics.ShowDuration(in set))
            Add(CellKey.Duration, SortCell(a, HLabel(Loc.Get(Strings.Detail.Column.Time), SortColumn.Duration, sort), SortColumn.Duration, sort, FlexJustify.End));
        if (set.Actions) Add(CellKey.More, new BoxEl());
        if (set.Expand) Add(CellKey.Expand, new BoxEl());

        float padX = WinUiListRules.RowPad;
        float headerH = TableRules.HeaderHeightFor(set.Classic);
        // ONE tree in every form (audit B9): the Compact row form draws no column header, only its hairline, so the header's band eases to nothing
        // there (the rows under it ride the reflow) and back, instead of the header being rebuilt around its hairline.
        return new BoxEl
        {
            Key = HeaderKey, Direction = 1, MinWidth = 0f,
            Children =
            [
                new BoxEl
                {
                    Key = "winui.list.header-band", Direction = 1, ClipToBounds = true, MinWidth = 0f, Shrink = 0f,
                    Height = set.Compact ? 0f : headerH, Animate = WinUiDiscoveryMotion.HeightReflow,
                    Children =
                    [
                        new BoxEl
                        {
                            Direction = 1, Shrink = 0f,
                            Padding = new Edges4(a.Checks ? WinUiListRules.CheckLaneShift : 0f, 0f, 0f, 0f),
                            Animate = WinUiListRules.HeaderShift,
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
                new BoxEl { Key = "winui.list.header-rule", Height = 1f, Shrink = 0f, Fill = Prop.Of(static () => Tok.StrokeDividerDefault) },
            ],
        };
    }

    /// <summary>The sorted lane's word is primary; every other the secondary ink (W-Album's #c5c5c5; Index, the default order, carries no indicator).</summary>
    static TextEl HLabel(string s, SortColumn col, SortSpec sort)
        => Word(s, TableRules.HeaderActive(col, sort.Column, false) ? Tok.TextPrimary : Tok.TextSecondary);

    static Element PlainHeader(string label, FlexJustify justify) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Justify = justify, MinWidth = 0f, ClipToBounds = true,
        Children = [Word(label, Tok.TextSecondary)],
    };

    /// <summary>The # header: the word "#" (W-Album) with the descending caret after it when the context's order is reversed; a click cycles the default order.
    /// The same two caret slots flank the word as the numbers' lane, so the indicator never nudges "#" off them.</summary>
    static Element IndexSortCell(TrackHeaderArgs a, SortSpec sort)
    {
        bool caret = sort.Column == SortColumn.Index && sort.Descending;
        var signal = a.SortSignal;
        Element Side(bool trailing) => new BoxEl
        {
            Width = Lane.NumCaretSlot, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Children = trailing && caret ? [Embed.Comp(() => new CaretComponent(signal))] : [],
        };
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f, ClipToBounds = true,
            Corners = Radii.ControlAll, HoverFill = Prop.Of(static () => R.Layer.RowHover), Role = AutomationRole.Button,
            OnClick = () => a.OnSort(SortColumn.Index, false),
            Children =
            [
                Side(false),
                new BoxEl
                {
                    Grow = 1f, MinWidth = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children = [HLabel(Loc.Get(Strings.Detail.Column.Number), SortColumn.Index, sort)],
                },
                Side(true),
            ],
        };
    }

    static Element SortCell(TrackHeaderArgs a, Element content, SortColumn col, SortSpec sort, FlexJustify justify, bool artistColumn = false)
    {
        bool caret = TableRules.HeaderActive(col, sort.Column, artistColumn) && (col != SortColumn.Index || sort.Descending);
        var signal = a.SortSignal;
        Element[] kids = !caret ? [content]
            : justify == FlexJustify.End ? [Embed.Comp(() => new CaretComponent(signal)), content]
            : [content, Embed.Comp(() => new CaretComponent(signal))];
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Justify = justify, Gap = Spacing.XS, MinWidth = 0f, ClipToBounds = true,
            Corners = Radii.ControlAll, HoverFill = Prop.Of(static () => R.Layer.RowHover), Role = AutomationRole.Button,
            OnClick = () => a.OnSort(col, artistColumn),
            Children = kids,
        };
    }

    /// <summary>The caret pops in when its lane becomes the sort and springs 0° ↔ 180° on every flip.</summary>
    sealed class CaretComponent(IReadSignal<SortSpec> sort) : Component
    {
        public override Element Render()
        {
            bool desc = sort.Value.Descending;
            UseTransition(AnimChannel.Opacity, 0f, 1f, Expressive.Fast, Easing.EaseInOut, "in");
            UseTransition(AnimChannel.ScaleX, WinUiListRules.CaretSeedScale, 1f, Expressive.Fast, Easing.Overshoot, "in");
            UseTransition(AnimChannel.ScaleY, WinUiListRules.CaretSeedScale, 1f, Expressive.Fast, Easing.Overshoot, "in");
            UseSpring(AnimChannel.Rotation, desc ? 180f : 0f, SpringParams.FromResponse(0.30f, 0.7f), desc);
            return new BoxEl
            {
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = [Icon(Icons.CaretSolidUp, WinUiListRules.CaretGlyph, Tok.TextPrimary)],
            };
        }
    }

    /// <summary>"Title" (the hero arm's "Song"), or "Artist" while the title lane owns an artist sort; the word swap rises and fades.</summary>
    sealed class LabelComponent(IReadSignal<SortSpec> sort, bool artistColumn) : Component
    {
        public override Element Render()
        {
            var col = sort.Value.Column;
            string text = !artistColumn && col == SortColumn.Artist
                ? Loc.Get(Strings.Detail.Column.Artist)
                : Loc.Get(Strings.Detail.Column.Title);   // "Title" in both arms (W-Playlist, W-LikedSongs, W-Album): never the hero arm's "Song"
            UseTransition(AnimChannel.Opacity, 0f, 1f, Expressive.Fast, Easing.SmoothOut, text);
            UseTransition(AnimChannel.TranslateY, WinUiListRules.LabelRise, 0f, Expressive.Fast, Easing.SmoothOut, text);
            return Word(text, TableRules.HeaderActive(SortColumn.Title, col, artistColumn) ? Tok.TextPrimary : Tok.TextSecondary);
        }
    }

    // ══ THE COMMAND BAR ══════════════════════════════════════════════════════════════════════════════════════════════════

    static TemplateParts SplitParts(float h) => new()
    {
        [SplitButton.PartRoot] = e => e with
        {
            AlignSelf = FlexAlign.Center, MinHeight = h, Fill = ColorF.Transparent, BorderWidth = 0f, BorderBrush = null,
            Corners = CornerRadius4.All(WinUiListRules.CommandRadius),
        },
        [SplitButton.PartPrimaryButton] = e => e with { Grow = 0f, MinWidth = 0f, Height = h, Padding = WinUiListRules.CommandPad },
        [SplitButton.PartSecondaryButton] = e => e with { Width = h, Height = h, Padding = default, Justify = FlexJustify.Center },
        [SplitButton.PartDivider] = static e => e with
        {
            Width = 1f, Height = WinUiListRules.CommandHeight * 0.5f, AlignSelf = FlexAlign.Center, Fill = Prop.Of(static () => Tok.StrokeDividerDefault),
        },
    };

    static readonly TemplateParts s_split = SplitParts(WinUiListRules.CommandHeight);
    static readonly TemplateParts s_splitCompact = SplitParts(WinUiListRules.CompactTarget);

    /// <summary>The count badge on a command's corner: the accent, ringed in the ground.</summary>
    static readonly TemplateParts s_badgeCorner = new()
    {
        [InfoBadge.PartRoot] = static b => b with
        {
            MinWidth = WinUiListRules.BadgeSize, Height = WinUiListRules.BadgeSize, MaxHeight = WinUiListRules.BadgeSize,
            Corners = CornerRadius4.All(WinUiListRules.BadgeRadius), Shrink = 0f,
            AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.End, BorderWidth = WinUiListRules.BadgeRing, BorderColor = Tok.FillSolidBase,
            HitTestVisible = false,
        },
    };

    /// <summary>The same badge inline (the labelled Filter command's slot).</summary>
    static readonly TemplateParts s_badgeInline = new()
    {
        [InfoBadge.PartRoot] = static b => b with
        {
            MinWidth = WinUiListRules.BadgeSize, Height = WinUiListRules.BadgeSize, MaxHeight = WinUiListRules.BadgeSize,
            Corners = CornerRadius4.All(WinUiListRules.BadgeRadius), Shrink = 0f, AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Start,
        },
    };

    /// <summary>The command bar's pictures. One kit per window tier (the Compact 40 targets), built once; the table hands its own tiers
    /// through <see cref="In"/>, so the kit follows the facts the table is drawn in.</summary>
    sealed class CommandKit : ITrackCommandKit
    {
        static readonly Dictionary<WidthTier, CommandKit> s_byTier = new();
        readonly WidthTier _tier;

        CommandKit(WidthTier tier) => _tier = tier;

        public static CommandKit For(WidthTier tier)
        {
            lock (s_byTier)
            {
                if (!s_byTier.TryGetValue(tier, out var kit)) s_byTier[tier] = kit = new CommandKit(tier);
                return kit;
            }
        }

        public ITrackCommandKit In(in TierFacts tiers)
        {
            var tier = TierOf(in tiers);
            return tier == _tier ? this : For(tier);
        }

        float Edge => WinUiListRules.CommandEdgeFor(_tier);

        static Prop<ColorF> Ink(bool active) => active ? Prop.Of(static () => Tok.AccentTextPrimary) : Prop.Of(static () => Tok.TextPrimary);

        /// <summary>The lane both modes share; the keyed mode box animates browse to selection. The browse mode is chromeless (W-Album's bar: flat words on the
        /// page); the selection mode is the card (W-MultiSelect: white 5 %, r8, 12 in on the check side and 8 on the close), tinted with the accent at Compact
        /// (W-MultiSelect-Compact).</summary>
        public Element Surface(string mode, Element content)
        {
            float padY = MathF.Max(0f, (DetailLayout.ToolbarRowHeight - Edge) / 2f);
            bool selection = mode == "selection";
            bool tinted = selection && _tier == WidthTier.Compact;
            return new BoxEl
            {
                Direction = 1, Height = DetailLayout.ToolbarRowHeight, MinWidth = 0f, ClipToBounds = true,
                // No left inset in the browse mode: the bar's first plate stands on the list's x, where the header's hairline and the row plates begin.
                Padding = selection ? new Edges4(12f, padY, 8f, padY) : new Edges4(0f, padY, 0f, padY),
                Corners = selection ? CornerRadius4.All(WinUiListRules.SelectionBarRadius) : default,
                BorderWidth = selection && !tinted ? 1f : 0f,
                BorderColor = selection && !tinted ? Prop.Of(static () => Tok.StrokeCardDefault) : (Prop<ColorF>)ColorF.Transparent,
                Fill = tinted ? Prop.Of(static () => R.Ink.AccentAction with { A = WinUiListRules.SelectionTint })
                    : selection ? Prop.Of(static () => Tok.FillCardDefault) : (Prop<ColorF>)ColorF.Transparent,
                Children =
                [
                    new BoxEl
                    {
                        Key = "commandbar-mode:" + mode, Direction = 1, Grow = 1f, MinWidth = 0f,
                        Animate = WinUiListRules.ModeMotion, Children = [content],
                    },
                ],
            };
        }

        public float CommandEdge => Edge;

        public Element Command(string key, Element command, Action<float> onWidth) => new BoxEl
        {
            Key = key, Direction = 1, Shrink = 0f, Animate = WinUiListRules.CommandMotion,
            OnBoundsChanged = r => onWidth(r.W),
            Children = [command],
        };

        const string SortCommandKey = "cmd:sort";

        /// <summary>W-Album's bar: Find first, [play next] [shuffle] [tune] after it where the page has them, the gap (or the page bar's chips), then [order] [Filter]
        /// and the tail ([Select] [insights] [...]). At Compact (W-Playlist-Compact) the order's word leads the bar after the glyph tools.</summary>
        public Element Bar(Element[] left, Element[] view, Element search, Element[] tail, Element? middle)
        {
            if (_tier == WidthTier.Compact)
            {
                var lead = new List<Element>(left.Length + 1);
                var rest = new List<Element>(view.Length);
                lead.AddRange(left);
                foreach (var v in view) (v.Key == SortCommandKey ? lead : rest).Add(v);
                left = lead.ToArray();
                view = rest.ToArray();
            }
            // ONE row of keyed commands (audit B9): the sort word that leads the Compact bar is reordered in place, never re-parented into another
            // group's box (which rebuilt it). The groups are the order: [find] [left] [gap or chips] [view] [tail]; an empty right group keeps its slot.
            var kids = new List<Element>(left.Length + view.Length + tail.Length + 3)
            {
                new BoxEl { Key = "bar:find", Direction = 1, Shrink = 0f, Children = [search] },
            };
            kids.AddRange(left);
            kids.Add(middle is null
                ? new BoxEl { Key = "bar:gap", Grow = 1f, MinWidth = CommandBarLayout.GroupGap }
                : new BoxEl
                {
                    Key = "bar:chips", Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = CommandBarLayout.InlineChipsMin,
                    Margin = new Edges4(0f, 0f, CommandBarLayout.InlineChipsTrail, 0f), Children = [middle],
                });
            if (view.Length + tail.Length == 0) kids.Add(new BoxEl { Key = "bar:right", Shrink = 0f });
            kids.AddRange(view);
            kids.AddRange(tail);
            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = CommandBarLayout.Gap, Grow = 1f, MinWidth = 0f,
                Children = kids.ToArray(),
            };
        }

        /// <summary>The page bar's lead: nothing. WinUI's play, shuffle and the rest are the page's hero actions (W-Album's horizontal hero); the chips lead the bar.</summary>
        public Element PageLead(EntityUri subject, Func<ColorF> accent, Action play, string shuffleLabel, Action shuffle)
            => new BoxEl { Key = "lead:band", Shrink = 0f };

        public Element PlayNext(string label, Action onPlay, IReadOnlyList<MenuFlyoutItem> items)
        {
            // SplitButton owns its content after mount: the ink rides a binding so a theme flip still reaches it.
            Element content = new BoxEl
            {
                Direction = 0, Gap = WinUiListRules.CommandGap, AlignItems = FlexAlign.Center,
                Children =
                [
                    new TextEl(WaveeIcons.PlayNext) { Size = WinUiListRules.CommandGlyph, FontFamily = WaveeIcons.Font, Color = Prop.Of(static () => Tok.TextPrimary) },
                    CommandWord(label, Tok.TextPrimary) with { Color = Prop.Of(static () => Tok.TextPrimary) },
                ],
            };
            return SplitButton.Create(content, onPlay, items, parts: _tier == WidthTier.Compact ? s_splitCompact : s_split);
        }

        BoxEl Plate(bool active, Action onClick, Action<NodeHandle>? onRealized) => new()
        {
            Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Height = Edge,
            Corners = CornerRadius4.All(WinUiListRules.CommandRadius),
            Fill = active ? Prop.Of(static () => R.Layer.RowHover) : (Prop<ColorF>)ColorF.Transparent,
            HoverFill = Prop.Of(static () => R.Layer.RowHover), PressedFill = Prop.Of(static () => R.Layer.RowPressed),
            HoverDurationMs = Motion.ControlFaster, PressDurationMs = Motion.ControlFaster,
            Role = AutomationRole.Button, Focusable = true,
            OnClick = onClick, OnRealized = onRealized,
        };

        /// <summary>A labelled command: the glyph and the word, the accent text when active (W-Album: 16 glyph, 8 gap, 10 padding).</summary>
        public Element Labeled(string glyph, string label, bool active, Action onClick, Action<NodeHandle>? onRealized, Element? trailing)
        {
            var ink = Ink(active);
            var word = CommandWord(label, Tok.TextPrimary) with { Color = ink };
            Element[] kids = trailing is null
                ? [Icon(glyph, WinUiListRules.CommandGlyph) with { Color = ink }, word]
                : [Icon(glyph, WinUiListRules.CommandGlyph) with { Color = ink }, word, trailing];
            return Plate(active, onClick, onRealized) with { Gap = WinUiListRules.CommandGap, Padding = WinUiListRules.CommandPad, Children = kids };
        }

        /// <summary>Find as a word at Standard and up (W-Album, W-Album-Huge: the glyph and "Find"); the glyph alone below (W-Album-Small).</summary>
        public Element? FindCommand(string label, bool active, Action onClick, Action<NodeHandle> onRealized)
            => WinUiListRules.WordedBar(_tier) ? Labeled(Icons.Search, label, active, onClick, onRealized, null) : null;

        /// <summary>"Select" as a flat word on the bar at Standard and up (W-Album: no glyph, before "..."); in "..." below.</summary>
        public Element? SelectCommand(string label, bool active, Action onClick)
            => WinUiListRules.WordedBar(_tier)
                ? Plate(active, onClick, null) with { Padding = WinUiListRules.CommandPad, Children = [CommandWord(label, Tok.TextPrimary) with { Color = Ink(active) }] }
                : null;

        public Element IconButton(string glyph, bool active, Action onClick, Action<NodeHandle>? onRealized)
            => Plate(active, onClick, onRealized) with { Width = Edge, Children = [Icon(glyph, WinUiListRules.CommandGlyph) with { Color = Ink(active) }] };

        public Element Chevron() => Icon(Icons.ChevronDown, WinUiListRules.CommandChevron, Tok.TextPrimary);

        // The row-size control: WinUI's two densities (Default 40 / 48, Compact 32 / 40). The checked row is the PUBLISHED rung (StyleMetrics.TrackRows, not the
        // style's option: WVTH005); a pick is a request for the style's `rowDensity` the app applies on the theme transaction.
        public IReadOnlyList<MenuFlyoutItem> DensityItems()
        {
            var current = WinUiListRules.DensityOfRung(ThemeHost.Metrics.Peek().TrackRows.Rung);
            var items = new List<MenuFlyoutItem>(WinUiListRules.Densities.Length);
            foreach (var density in WinUiListRules.Densities)
            {
                var d = density;
                items.Add(MenuFlyoutItem.RadioItem(Loc.Get(WinUiListRules.DensityLabelKey(d)), current == d, () => RequestDensity(d)));
            }
            return items;
        }

        // Requested for WinUI's composition (a structure never asks which theme is active, WVTH004). The qualified id is the WinUI style's `rowDensity`: Classic's
        // stand-in menu sent Classic's `density` (a `RowDensity`), which WinUI's cell does not hold, so its "Row size" was a no-op under WinUI.
        static void RequestDensity(WinUiDensity density)
            => _ = ThemeHost.Apply(new OptionChange(WinUiTheme.Id, WinUiListRules.DensityQualifiedId, density));

        /// <summary>Filter: the funnel and its word (or the funnel alone at Compact), the accent text when active and the count badge while filters are on.
        /// The labelled form always reserves the badge's slot, so a filter turning on moves nothing.</summary>
        public Element FilterCommand(int count, bool labelled, string label, Action onClick, Action<NodeHandle> onRealized)
        {
            bool active = count > 0;
            var ink = Ink(active);
            Element glyph = Icon(Icons.Filter, WinUiListRules.CommandGlyph) with { Color = ink };
            if (labelled && _tier != WidthTier.Compact)
                return Plate(active, onClick, onRealized) with
                {
                    Shrink = 0f, Gap = WinUiListRules.CommandGap, Padding = WinUiListRules.CommandPad,
                    Children =
                    [
                        glyph,
                        CommandWord(label, Tok.TextPrimary) with { Color = ink },
                        new BoxEl
                        {
                            Width = WinUiListRules.FilterBadgeSlot, Height = WinUiListRules.BadgeSize, Shrink = 0f, AlignItems = FlexAlign.Center,
                            Children = active ? [InfoBadge.Count(count, parts: s_badgeInline)] : [],
                        },
                    ],
                };
            return Plate(active, onClick, onRealized) with
            {
                ZStack = true, Shrink = 0f, Width = MathF.Max(CommandBarLayout.FilterIconWidth, Edge),
                Children = active ? [glyph, InfoBadge.Count(count, parts: s_badgeCorner)] : [glyph],
            };
        }

        public Element FilterFunnel(int count, Action onClick, Action<NodeHandle> onRealized)
        {
            bool active = count > 0;
            Element glyph = Icon(Icons.Filter, WinUiListRules.CommandGlyph) with { Color = Ink(active) };
            return Plate(active, onClick, onRealized) with
            {
                ZStack = true, Width = Edge, AlignSelf = FlexAlign.Center,
                Children = active ? [glyph, InfoBadge.Count(count, parts: s_badgeCorner)] : [glyph],
            };
        }

        /// <summary>The "..." slot never changes size: the badge is an overlay on a fixed box.</summary>
        public Element MoreSlot(Element more, int filterCount) => new BoxEl
        {
            ZStack = true, Width = Edge, Height = Edge, Shrink = 0f, AlignSelf = FlexAlign.Center,
            Children = filterCount > 0
                ? [more, InfoBadge.Count(filterCount, parts: s_badgeCorner) with { Key = "more:filter-badge" }]
                : [more],
        };
    }

    // ══ THE FILTER CARD ══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The immediate-apply filter card: the header (the title, what the list shows against what it has, "reset"), one 44 row per facet
    /// (its word and a combo, tinted while the row filters), then the status switches. The WinUI words.</summary>
    static Element FilterCard(TrackFilterCardArgs a)
    {
        var meta = R.Type.DenseTitle("");
        Element Label(string text, bool on) => new TextEl(text)
        {
            Size = meta.Size, LineHeight = meta.LineHeight, Color = on ? Tok.TextPrimary : Tok.TextSecondary, Grow = 1f, MinWidth = 0f,
            MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
        };

        BoxEl Row(string key, bool on, string label, Element control) => new()
        {
            Key = key, Direction = 0, Height = WinUiListRules.FacetRowHeight, AlignItems = FlexAlign.Center, Gap = WinUiListRules.FacetGap,
            Padding = new Edges4(WinUiListRules.FacetRowPadX, 0f, WinUiListRules.FacetRowPadX, 0f),
            Children = [Label(label, on), control],
        };

        Element Pick(in TrackFilterFacet f)
        {
            bool on = f.On;
            return Row(f.Key, on, f.Label, new BoxEl
            {
                ZStack = true, Width = WinUiListRules.ComboWidth, Shrink = 0f,
                Children =
                [
                    ComboBox.Create(f.Options, f.Value, width: WinUiListRules.ComboWidth, placeholder: f.Placeholder, onChange: f.Changed)
                        with { Key = f.Key + ":combo" },
                    new BoxEl
                    {
                        Width = WinUiListRules.ComboWidth, AlignSelf = FlexAlign.Stretch, HitTestVisible = false, BorderWidth = 1f,
                        Corners = Radii.ControlAll,
                        Fill = on ? Prop.Of(static () => R.Layer.SelectedRest) : (Prop<ColorF>)ColorF.Transparent,
                        BorderColor = on ? Prop.Of(static () => R.Ink.AccentAction) : (Prop<ColorF>)ColorF.Transparent,
                    },
                ],
            });
        }

        Element Switch(in TrackFilterToggle t)
            => Row(t.Key, t.On, t.Label, ToggleSwitch.Create(t.Value, onChange: t.Changed,
                style: ToggleSwitch.DefaultStyle with { MinWidth = 0f }) with { Key = t.Key + ":switch" });

        var rows = new List<Element>(a.Facets.Count);
        foreach (var f in a.Facets) rows.Add(Pick(f));
        var toggles = new List<Element>(a.Toggles.Count);
        foreach (var t in a.Toggles) toggles.Add(Switch(t));

        static BoxEl Divider() => new() { Height = 1f, Fill = Tok.StrokeDividerDefault };
        var body = new List<Element>(3)
        {
            new BoxEl
            {
                Direction = 1, MinWidth = 0f,
                Padding = new Edges4(WinUiListRules.GroupPadX, WinUiListRules.GroupPadTop, WinUiListRules.GroupPadX,
                                     toggles.Count > 0 ? WinUiListRules.GroupPadTop : WinUiListRules.GroupPadBottom),
                Children = rows.ToArray(),
            },
        };
        if (toggles.Count > 0)
        {
            body.Add(Divider());
            body.Add(new BoxEl
            {
                Direction = 1, MinWidth = 0f,
                Padding = new Edges4(WinUiListRules.GroupPadX, WinUiListRules.GroupPadTop, WinUiListRules.GroupPadX, WinUiListRules.GroupPadBottom),
                Children = toggles.ToArray(),
            });
        }

        return new BoxEl
        {
            Direction = 1, Width = WinUiListRules.CardWidth, MinWidth = WinUiListRules.CardMinWidth,
            MaxWidth = WinUiListRules.CardWidth, MaxHeight = WinUiListRules.CardMaxHeight, MinHeight = 0f, ClipToBounds = true,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = WinUiListRules.CardHeaderGap, Height = WinUiListRules.CardHeaderHeight,
                    Padding = new Edges4(WinUiListRules.CardHeaderPadLeft, 0f, WinUiListRules.CardHeaderPadRight, 0f),
                    Children =
                    [
                        R.Type.SheetTitle(a.Title) with { Color = Tok.TextPrimary, Shrink = 0f },
                        new TextEl(a.Status) { Size = meta.Size, LineHeight = meta.LineHeight, Color = Tok.TextSecondary, Grow = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                        HyperlinkButton.Create(a.ResetLabel, a.Reset, isEnabled: a.CanReset),
                    ],
                },
                Divider(),
                new ScrollEl
                {
                    ContentSized = true, Grow = 1f, MinHeight = 0f, MaxHeight = WinUiListRules.ScrollMaxHeight,
                    Content = new BoxEl { Direction = 1, MinWidth = 0f, Children = body.ToArray() },
                },
            ],
        };
    }
}
