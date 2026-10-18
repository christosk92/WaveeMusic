// ── Themes/Zune/Structure/Lists/ZuneTrackChrome.cs — Zune's track table chrome (themes plan 6.3; P5 Z3) ────────────────────────────
//
// The chrome half of Zune's `TrackListStyle` (Z-State-Rows' header row, Z-Album's "plays · time" header, the boards' command words):
//
//   header        the lanes' words in the 12/16 caption, tertiary and lowercased by the style's case (`R.Case`), the sorted lane in the
//                 primary ink with its caret; the same tracks, gutter and keys as the rows; the band's hairline. The Compact row form draws
//                 the hairline alone (its header height is 0).
//   command bar   flat words: no plates at rest, the 5 % wash on hover, the accent ink when active; square. The page bar's lead is the board's
//                 round accent play disc and the ringed shuffle with its word, then the divider before the chips.
//   "Row size"    the "..." menu's radio rows over Zune's three densities (compact 44 · standard 52 · comfortable 60). The checked row is the
//                 rung the style PUBLISHED (`StyleMetrics.TrackRows.Rung`), never a read of the style's option (WVTH005): Classic's density
//                 accessor reads the cell as `RowDensity` and throws under Zune, so nothing here or under it touches it. A pick is a request
//                 that lands in `ThemeHost.Apply` (the style applies it on the theme transaction).
//   filter card   the immediate-apply card: square, the Zune words, an accent-tinted combo on a filtering row.
//   placeholder   the blank row: the same tracks, gap, height and padding as the live row.
//   group head    a date group's name and count, on the rows' gutter.
//   unavailable   the "songs here can't be played" bar: a square panel, the accent info glyph, the sentence, "hide them"; its band reserved
//                 while shown and released with the disclosure reveal, so the rows ride it.
//
// WHAT THE CORE KEEPS. The sort / filter / selection state and every decision on it, the commands that fit, every overlay and its focus,
// the menus' items. A template draws what its args carry and calls the verbs it was handed (WVTH002). Numbers are `Rules\ZuneListRules.cs`.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Zune;

using CellKey = global::Wavee.Track.CellKey;
using ColumnSet = global::Wavee.Track.ColumnSet;
using CommandBarLayout = global::Wavee.Track.CommandBarLayout;
using DetailLayout = global::Wavee.Detail.VerticalLayout;
using Lane = global::Wavee.Track.Lane;
using RowMetrics = global::Wavee.Track.RowMetrics;
using SortColumn = global::Wavee.Track.SortColumn;
using SortSpec = global::Wavee.Track.SortSpec;
using TableRules = global::Wavee.Track.TableRules;

/// <summary>Zune's chrome for the track table.</summary>
internal static class ZuneTrackChrome
{
    /// <summary>The header's key (a test finds Zune's header by it).</summary>
    public const string HeaderKey = "zune.list.header";

    /// <summary>The chrome Zune registers as <c>TrackListStyle.Chrome</c>. Stateless.</summary>
    public static readonly TrackChromeTemplates Templates = new(Header, CommandKit.For(WidthTier.Standard), FilterCard, Placeholder, GroupHeader)
    {
        UnavailableBar = UnavailableBar,
        HidesChromeOnEmptyLead = true,   // Z-State-Empty-Playlist: the lead and its finder are the page, no sort, filter or header over nothing
        UnavailableBarExtent = static tiers => ZuneListRules.UnavailableExtent(tiers.Policy is null ? WidthTier.Standard : tiers.Window),
    };

    static WidthTier TierOf(in TierFacts tiers) => tiers.Policy is null ? WidthTier.Standard : tiers.Window;

    static TextEl Word(string text, ColorF ink, LabelKind kind = LabelKind.Label) => new(text)
    {
        Size = ZuneListRules.HeaderType, LineHeight = ZuneListRules.HeaderLine, Color = ink, Case = R.Case.For(kind),
        MinWidth = 0f, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis,
    };

    // ══ THE UNAVAILABLE-SONGS BAR ════════════════════════════════════════════════════════════════════════════════════════

    static Element UnavailableBar(TrackUnavailableBarArgs a)
    {
        var tier = TierOf(a.Tiers);
        bool compact = tier == WidthTier.Compact;
        var meta = R.Type.DenseMeta("");
        Element bar = new BoxEl
        {
            Key = "unavailable:bar", Direction = 0, AlignItems = FlexAlign.Center, Gap = ZuneListRules.UnavailableGap, MinWidth = 0f,
            Height = compact ? ZuneListRules.UnavailableCompactHeight : ZuneListRules.UnavailableHeight, Shrink = 0f,
            Padding = ZuneListRules.UnavailablePadding, Margin = new Edges4(0f, 0f, 0f, ZuneListRules.UnavailableBelow),
            Fill = Prop.Of(static () => R.Layer.FloatingPane),
            Children =
            [
                Icon(Icons.Info, ZuneListRules.UnavailableGlyph, R.Ink.AccentAction) with { Shrink = 0f },
                new TextEl(ZuneListRules.UnavailableExplains(tier) ? a.Explained : a.Summary)
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
            Height = a.Shown ? ZuneListRules.UnavailableExtent(tier) : 0f,
            Padding = new Edges4(a.InsetX, 0f, a.InsetX, 0f),
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
            Padding = new Edges4(padX, 0f, padX, 0f),
            Children = cells,
        };
    }

    // ══ THE DATE GROUP HEAD ══════════════════════════════════════════════════════════════════════════════════════════════

    static Element GroupHeader(Func<Track.DateGroupHead?> head, float padX)
    {
        var meta = R.Type.TrackMeta("");
        var group = R.Type.Heading("");
        return new BoxEl
        {
            Direction = 0, Height = ZuneListRules.GroupHeadHeight, AlignItems = FlexAlign.End, Gap = ZuneListRules.GroupHeadGap,
            MinWidth = 0f, ClipToBounds = true, Visible = Prop.Of(() => head() is not null),
            Padding = new Edges4(padX, 0f, padX, ZuneListRules.GroupHeadPadBottom),
            Children =
            [
                // The group's name in the heading voice's light face at 22 (Z-Liked "this week 26"), its count beside it in the meta voice.
                new TextEl(Prop.Of(() => head()?.Label ?? ""))
                {
                    FontFamily = group.FontFamily, Size = ZuneListRules.GroupHeadSize, LineHeight = ZuneListRules.GroupHeadLine, Weight = group.ResolvedWeight,
                    Color = Tok.TextPrimary, MaxLines = 1, Shrink = 0f,
                },
                new TextEl(Prop.Of(() => head()?.Count ?? ""))
                {
                    Size = meta.Size, LineHeight = meta.LineHeight, Color = Tok.TextTertiary,
                    MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
                },
            ],
        };
    }

    // ══ THE COLUMN HEADER ════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The header grid over the rows' tracks, keyed like the row cells (a lane that leaves is removed, never reconciled against its
    /// neighbour); shifted over while the check lane is in; the band's hairline under it. Zune's rows have no inset, so the header pays the
    /// rows' own gutter.</summary>
    static Element Header(TrackHeaderArgs a)
    {
        var set = a.Set;
        var sort = a.Sort;
        bool vertical = a.Vertical;
        var cells = new List<Element>(a.Tracks.Length);
        void Add(string key, Element cell) => cells.Add(cell with { Key = key });

        if (!set.IndexHidden) Add(CellKey.Num, IndexSortCell(a, sort));
        if (set.Heart) Add(CellKey.Heart, new BoxEl());
        if (set.Thumb) Add(CellKey.Art, new BoxEl());
        var sortSignal = a.SortSignal;
        bool artistLane = set.Artist;
        Element title = Embed.Comp(() => new LabelComponent(sortSignal, vertical, artistLane))
            with { Key = "sort-label:z" + (artistLane ? ":a" : "") + (vertical ? ":song" : "") };
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
        // Zune words the time lane ("time", Z-Album / Z-State-Rows), never a clock glyph.
        if (RowMetrics.ShowDuration(in set))
            Add(CellKey.Duration, SortCell(a, HLabel(Loc.Get(Strings.Detail.Column.Time), SortColumn.Duration, sort), SortColumn.Duration, sort, FlexJustify.End));
        if (set.Actions) Add(CellKey.More, new BoxEl());
        if (set.Expand) Add(CellKey.Expand, new BoxEl());

        float padX = RowMetrics.PadXFor(set.Tier);
        var rule = new BoxEl { Height = 1f, Fill = Prop.Of(static () => Tok.StrokeDividerDefault) };
        if (set.Compact)
            return new BoxEl { Key = HeaderKey, Direction = 1, MinWidth = 0f, Children = [rule] };
        return new BoxEl
        {
            Key = HeaderKey, Direction = 1, MinWidth = 0f,
            Children =
            [
                new BoxEl
                {
                    Direction = 1, ClipToBounds = true,
                    Padding = new Edges4(a.Checks ? ZuneListRules.CheckLaneShift : 0f, 0f, 0f, 0f),
                    Animate = ZuneListRules.HeaderShift,
                    Children =
                    [
                        new GridEl
                        {
                            Columns = a.Tracks, ColGap = RowMetrics.ColGapFor(set.Tier),
                            Padding = new Edges4(padX, 0f, padX, 0f),
                            RowHeight = TableRules.HeaderHeightFor(set.Classic), Children = cells.ToArray(),
                        },
                        rule,
                    ],
                },
            ],
        };
    }

    /// <summary>The sorted lane's word is primary; every other tertiary (Index, the default order, carries no indicator).</summary>
    static TextEl HLabel(string s, SortColumn col, SortSpec sort)
        => Word(s, TableRules.HeaderActive(col, sort.Column, false) ? Tok.TextPrimary : Tok.TextTertiary);

    static Element PlainHeader(string label, FlexJustify justify) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Justify = justify, MinWidth = 0f, ClipToBounds = true,
        Children = [Word(label, Tok.TextTertiary)],
    };

    /// <summary>The # header: Zune's number lane carries no word (Z-State-Rows), only the descending caret when the context's order is reversed;
    /// a click cycles the default order.</summary>
    static Element IndexSortCell(TrackHeaderArgs a, SortSpec sort)
    {
        bool caret = sort.Column == SortColumn.Index && sort.Descending;
        var signal = a.SortSignal;
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, MinWidth = 0f, ClipToBounds = true,
            HoverFill = Prop.Of(static () => R.Layer.RowHover), Role = AutomationRole.Button,
            OnClick = () => a.OnSort(SortColumn.Index, false),
            Children = caret ? [Embed.Comp(() => new CaretComponent(signal))] : [new BoxEl { Width = Lane.NumCaretSlot }],
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
            HoverFill = Prop.Of(static () => R.Layer.RowHover), Role = AutomationRole.Button,
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
            UseTransition(AnimChannel.ScaleX, ZuneListRules.CaretSeedScale, 1f, Expressive.Fast, Easing.Overshoot, "in");
            UseTransition(AnimChannel.ScaleY, ZuneListRules.CaretSeedScale, 1f, Expressive.Fast, Easing.Overshoot, "in");
            UseSpring(AnimChannel.Rotation, desc ? 180f : 0f, SpringParams.FromResponse(0.30f, 0.7f), desc);
            return new BoxEl
            {
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = [Icon(Icons.CaretSolidUp, ZuneListRules.CaretGlyph, Tok.TextPrimary)],
            };
        }
    }

    /// <summary>"title" (the hero arm's "song"), or "artist" while the title lane owns an artist sort; the word swap rises and fades.</summary>
    sealed class LabelComponent(IReadSignal<SortSpec> sort, bool song, bool artistColumn) : Component
    {
        public override Element Render()
        {
            var col = sort.Value.Column;
            string text = !artistColumn && col == SortColumn.Artist
                ? Loc.Get(Strings.Detail.Column.Artist)
                : Loc.Get(song ? Strings.Detail.Column.Song : Strings.Detail.Column.Title);
            UseTransition(AnimChannel.Opacity, 0f, 1f, Expressive.Fast, Easing.SmoothOut, text);
            UseTransition(AnimChannel.TranslateY, ZuneListRules.LabelRise, 0f, Expressive.Fast, Easing.SmoothOut, text);
            return Word(text, TableRules.HeaderActive(SortColumn.Title, col, artistColumn) ? Tok.TextPrimary : Tok.TextTertiary);
        }
    }

    // ══ THE COMMAND BAR ══════════════════════════════════════════════════════════════════════════════════════════════════

    static TemplateParts SplitParts(float h) => new()
    {
        [SplitButton.PartRoot] = e => e with
        {
            AlignSelf = FlexAlign.Center, MinHeight = h, Fill = ColorF.Transparent, BorderWidth = 0f, BorderBrush = null,
            Corners = CornerRadius4.All(0f),
        },
        [SplitButton.PartPrimaryButton] = e => e with { Grow = 0f, MinWidth = 0f, Height = h, Padding = ZuneListRules.CommandPad },
        [SplitButton.PartSecondaryButton] = e => e with { Width = h, Height = h, Padding = default, Justify = FlexJustify.Center },
        [SplitButton.PartDivider] = static e => e with
        {
            Width = 1f, Height = ZuneListRules.DividerHeight, AlignSelf = FlexAlign.Center, Fill = Prop.Of(static () => Tok.StrokeDividerDefault),
        },
    };

    static readonly TemplateParts s_split = SplitParts(ZuneListRules.CommandHeight);
    static readonly TemplateParts s_splitCompact = SplitParts(ZuneListRules.CompactTarget);

    /// <summary>The count badge on a command's corner: the accent, ringed in the ground.</summary>
    static readonly TemplateParts s_badgeCorner = new()
    {
        [InfoBadge.PartRoot] = static b => b with
        {
            MinWidth = ZuneListRules.BadgeSize, Height = ZuneListRules.BadgeSize, MaxHeight = ZuneListRules.BadgeSize,
            Corners = CornerRadius4.All(ZuneListRules.BadgeRadius), Shrink = 0f,
            AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.End, BorderWidth = ZuneListRules.BadgeRing, BorderColor = Tok.FillSolidBase,
            HitTestVisible = false,
        },
    };

    /// <summary>The same badge inline (the labelled Filter command's slot).</summary>
    static readonly TemplateParts s_badgeInline = new()
    {
        [InfoBadge.PartRoot] = static b => b with
        {
            MinWidth = ZuneListRules.BadgeSize, Height = ZuneListRules.BadgeSize, MaxHeight = ZuneListRules.BadgeSize,
            Corners = CornerRadius4.All(ZuneListRules.BadgeRadius), Shrink = 0f, AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Start,
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

        float Edge => ZuneListRules.CommandEdgeFor(_tier);

        static Prop<ColorF> Ink(bool active) => active ? Prop.Of(static () => R.Ink.AccentAction) : Prop.Of(static () => Tok.TextSecondary);

        /// <summary>The chromeless lane both modes share; the keyed mode box animates browse ↔ selection. At Compact the selection mode is
        /// tinted with the accent (Z-State-Rows-Compact).</summary>
        public Element Surface(string mode, Element content)
        {
            float padY = MathF.Max(0f, (DetailLayout.ToolbarRowHeight - Edge) / 2f);
            bool tinted = _tier == WidthTier.Compact && mode == "selection";
            return new BoxEl
            {
                Direction = 1, Height = DetailLayout.ToolbarRowHeight, MinWidth = 0f, ClipToBounds = true,
                // No left inset (A5): the bar's first glyph stands at the list's x with the heading and the header's hairline.
                Padding = new Edges4(0f, padY, DetailLayout.ToolbarSurfacePadX, padY),
                Fill = tinted ? Prop.Of(static () => R.Ink.AccentAction with { A = ZuneListRules.SelectionTint }) : (Prop<ColorF>)ColorF.Transparent,
                Children =
                [
                    new BoxEl
                    {
                        Key = "commandbar-mode:" + mode, Direction = 1, Grow = 1f, MinWidth = 0f,
                        Animate = ZuneListRules.ModeMotion, Children = [content],
                    },
                ],
            };
        }

        public float CommandEdge => Edge;

        public Element Command(string key, Element command, Action<float> onWidth) => new BoxEl
        {
            Key = key, Direction = 1, Shrink = 0f, Animate = ZuneListRules.CommandMotion,
            OnBoundsChanged = r => onWidth(r.W),
            Children = [command],
        };

        const string SortCommandKey = "cmd:sort";

        /// <summary>[play next] [shuffle] [tune], the gap (or the page bar's chips), then [filter] [sort], find, [insights] [...]. At Compact the
        /// sort word leads the bar.</summary>
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
            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = CommandBarLayout.Gap, Grow = 1f, MinWidth = 0f,
                Children =
                [
                    new BoxEl
                    {
                        Key = "bar:left", Direction = 0, AlignItems = FlexAlign.Center, Gap = CommandBarLayout.Gap, Shrink = 0f,
                        // The first command's own left pad (and the page bar's inset) are drawn outside the list's x, so its glyph lands on it.
                        Margin = new Edges4(ZuneListRules.BarLeadShift(left.Length > 0 && left[0].Key is { } k0 && k0.StartsWith(ZuneListRules.PageLeadKeyPrefix, StringComparison.Ordinal),
                                                                         RowMetrics.RowInset - DetailLayout.ToolbarSurfacePadX), 0f, 0f, 0f),
                        Children = left,
                    },
                    middle is null
                        ? new BoxEl { Key = "bar:gap", Grow = 1f, MinWidth = CommandBarLayout.GroupGap }
                        : new BoxEl
                        {
                            Key = "bar:chips", Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = CommandBarLayout.InlineChipsMin,
                            Margin = new Edges4(0f, 0f, CommandBarLayout.InlineChipsTrail, 0f), Children = [middle],
                        },
                    new BoxEl
                    {
                        Key = "bar:right", Direction = 0, AlignItems = FlexAlign.Center, Gap = CommandBarLayout.Gap, Shrink = 0f,
                        Children =
                        [
                            .. view,
                            new BoxEl
                            {
                                Key = "bar:find", Direction = 1, Shrink = 0f,
                                Margin = new Edges4(CommandBarLayout.SearchGap - CommandBarLayout.Gap, 0f, 0f, 0f), Children = [search],
                            },
                            .. tail,
                        ],
                    },
                ],
            };
        }

        /// <summary>The page bar's lead (Z-Liked, Z-State-Rows' band): the round accent play disc, the ringed shuffle with its word and the
        /// divider before the chips. Zune's disc is one target: "more ways to play" stays in the page's "..." menu. One home per verb (audit A4): where
        /// the window's band row 2 carries play and shuffle (Small up), the lead is empty and the chips lead the bar.</summary>
        public Element PageLead(EntityUri subject, Func<ColorF> accent, Action play, string shuffleLabel, Action shuffle)
        {
            if (ZuneBandRules.Band(_tier).TitleAndActions) return new BoxEl { Key = "lead:band", Shrink = 0f };
            float edge = Edge;
            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = CommandBarLayout.Gap, Shrink = 0f,
                Children =
                [
                    Controls.Named(new BoxEl
                    {
                        Key = "lead:play", Width = edge, Height = edge, Shrink = 0f, Corners = Radii.Circle(edge),
                        Fill = Prop.Of(static () => R.Ink.AccentAction), AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                        HoverScale = R.Motion.ScaleEmphatic.Hover, PressScale = R.Motion.ScaleEmphatic.Press,
                        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = play,
                        Children = [Icon(ZunePlayerLayout.PlaySolid, ZuneListRules.LeadGlyph) with { Color = Prop.Of(static () => ColorContrast.PickContrast(R.Ink.AccentAction)) }],
                    }, Loc.Get(Strings.Detail.Play)),
                    new BoxEl
                    {
                        Key = "lead:shuffle", Direction = 0, AlignItems = FlexAlign.Center, Gap = ZuneListRules.CommandGap, Height = edge, Shrink = 0f,
                        Padding = ZuneListRules.CommandPad, HoverFill = Prop.Of(static () => R.Layer.RowHover), PressedFill = Prop.Of(static () => R.Layer.RowPressed),
                        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = shuffle,
                        Children =
                        [
                            new BoxEl
                            {
                                Width = edge, Height = edge, Corners = Radii.Circle(edge), BorderWidth = ZuneListRules.RingStroke,
                                BorderColor = Prop.Of(static () => Tok.TextPrimary), AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                                Children = [Icon(Icons.Shuffle, ZuneListRules.LeadGlyph, Tok.TextPrimary)],
                            },
                            Word(shuffleLabel, Tok.TextPrimary, LabelKind.Command),
                        ],
                    },
                    new BoxEl
                    {
                        Key = "lead:divider", Width = ZuneListRules.LeadDividerSlot, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                        Children = [new BoxEl { Width = 1f, Height = ZuneListRules.DividerHeight, Fill = Prop.Of(static () => Tok.StrokeDividerDefault) }],
                    },
                ],
            };
        }

        public Element PlayNext(string label, Action onPlay, IReadOnlyList<MenuFlyoutItem> items)
        {
            // SplitButton owns its content after mount: the ink rides a binding so a theme flip still reaches it.
            Element content = new BoxEl
            {
                Direction = 0, Gap = ZuneListRules.CommandGap, AlignItems = FlexAlign.Center,
                Children =
                [
                    new TextEl(WaveeIcons.PlayNext) { Size = ZuneListRules.CommandGlyph, FontFamily = WaveeIcons.Font, Color = Prop.Of(static () => Tok.TextSecondary) },
                    Word(label, Tok.TextSecondary, LabelKind.Command) with { Size = R.Type.DenseMeta("").Size, Color = Prop.Of(static () => Tok.TextSecondary) },
                ],
            };
            return SplitButton.Create(content, onPlay, items, parts: _tier == WidthTier.Compact ? s_splitCompact : s_split);
        }

        BoxEl Plate(bool active, Action onClick, Action<NodeHandle>? onRealized) => new()
        {
            Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Height = Edge,
            Fill = ColorF.Transparent,
            HoverFill = Prop.Of(static () => R.Layer.RowHover), PressedFill = Prop.Of(static () => R.Layer.RowPressed),
            HoverDurationMs = Motion.ControlFaster, PressDurationMs = Motion.ControlFaster,
            Role = AutomationRole.Button, Focusable = true,
            OnClick = onClick, OnRealized = onRealized,
        };

        /// <summary>A labelled command: the glyph and the lowercased word, the accent ink when active.</summary>
        public Element Labeled(string glyph, string label, bool active, Action onClick, Action<NodeHandle>? onRealized, Element? trailing)
        {
            var ink = Ink(active);
            var word = Word(label, Tok.TextSecondary, LabelKind.Command) with { Size = R.Type.DenseMeta("").Size, Color = ink };
            Element[] kids = trailing is null
                ? [Icon(glyph, ZuneListRules.CommandGlyph) with { Color = ink }, word]
                : [Icon(glyph, ZuneListRules.CommandGlyph) with { Color = ink }, word, trailing];
            return Plate(active, onClick, onRealized) with { Gap = ZuneListRules.CommandGap, Padding = ZuneListRules.CommandPad, Children = kids };
        }

        public Element IconButton(string glyph, bool active, Action onClick, Action<NodeHandle>? onRealized)
            => Plate(active, onClick, onRealized) with { Width = Edge, Children = [Icon(glyph, ZuneListRules.CommandGlyph) with { Color = Ink(active) }] };

        public Element Chevron() => Icon(Icons.ChevronDown, ZuneListRules.CommandChevron, Tok.TextTertiary);

        // The row-size control: Zune's three densities. The checked row is the PUBLISHED rung (StyleMetrics.TrackRows, not the style's
        // option: WVTH005); a pick is a request for the style's `density` the app applies on the theme transaction.
        public IReadOnlyList<MenuFlyoutItem> DensityItems()
        {
            var current = ZuneListRules.DensityOfRung(ThemeHost.Metrics.Peek().TrackRows.Rung);
            var items = new List<MenuFlyoutItem>(ZuneListRules.Densities.Length);
            foreach (var density in ZuneListRules.Densities)
            {
                var d = density;
                items.Add(MenuFlyoutItem.RadioItem(Loc.Get(ZuneListRules.DensityLabelKey(d)), current == d, () => RequestDensity(d)));
            }
            return items;
        }

        // Requested for Zune's composition (a structure never asks which theme is active, WVTH004); a composition that reuses Zune's
        // structure under another id (Panorama) needs a style-side request accessor instead (see the lane report).
        static void RequestDensity(ZuneDensity density)
            => _ = ThemeHost.Apply(new OptionChange(ZuneTheme.Id, ZuneListRules.DensityQualifiedId, density));

        /// <summary>Filter: the funnel and its word (or the funnel alone at Compact), the accent ink and the count badge while filters are on.
        /// The labelled form always reserves the badge's slot, so a filter turning on moves nothing.</summary>
        public Element FilterCommand(int count, bool labelled, string label, Action onClick, Action<NodeHandle> onRealized)
        {
            bool active = count > 0;
            var ink = Ink(active);
            Element glyph = Icon(Icons.Filter, ZuneListRules.CommandGlyph) with { Color = ink };
            if (labelled && _tier != WidthTier.Compact)
                return Plate(active, onClick, onRealized) with
                {
                    Shrink = 0f, Gap = ZuneListRules.CommandGap, Padding = ZuneListRules.CommandPad,
                    Children =
                    [
                        glyph,
                        Word(label, Tok.TextSecondary, LabelKind.Command) with { Size = R.Type.DenseMeta("").Size, Color = ink },
                        new BoxEl
                        {
                            Width = ZuneListRules.FilterBadgeSlot, Height = ZuneListRules.BadgeSize, Shrink = 0f, AlignItems = FlexAlign.Center,
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
            Element glyph = Icon(Icons.Filter, ZuneListRules.CommandGlyph) with { Color = Ink(active) };
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
    /// (its word and a combo, accent-tinted while the row filters), then the status switches. Square, the Zune words.</summary>
    static Element FilterCard(TrackFilterCardArgs a)
    {
        var meta = R.Type.DenseMeta("");
        Element Label(string text, bool on) => new TextEl(text)
        {
            Size = meta.Size, LineHeight = meta.LineHeight, Color = on ? Tok.TextPrimary : Tok.TextSecondary, Grow = 1f, MinWidth = 0f,
            MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Case = R.Case.For(LabelKind.Label),
        };

        BoxEl Row(string key, bool on, string label, Element control) => new()
        {
            Key = key, Direction = 0, Height = ZuneListRules.FacetRowHeight, AlignItems = FlexAlign.Center, Gap = ZuneListRules.FacetGap,
            Padding = new Edges4(ZuneListRules.FacetRowPadX, 0f, ZuneListRules.FacetRowPadX, 0f),
            Children = [Label(label, on), control],
        };

        Element Pick(in TrackFilterFacet f)
        {
            bool on = f.On;
            return Row(f.Key, on, f.Label, new BoxEl
            {
                ZStack = true, Width = ZuneListRules.ComboWidth, Shrink = 0f,
                Children =
                [
                    ComboBox.Create(f.Options, f.Value, width: ZuneListRules.ComboWidth, placeholder: f.Placeholder, onChange: f.Changed)
                        with { Key = f.Key + ":combo" },
                    new BoxEl
                    {
                        Width = ZuneListRules.ComboWidth, AlignSelf = FlexAlign.Stretch, HitTestVisible = false, BorderWidth = 1f,
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
                Padding = new Edges4(ZuneListRules.GroupPadX, ZuneListRules.GroupPadTop, ZuneListRules.GroupPadX,
                                     toggles.Count > 0 ? ZuneListRules.GroupPadTop : ZuneListRules.GroupPadBottom),
                Children = rows.ToArray(),
            },
        };
        if (toggles.Count > 0)
        {
            body.Add(Divider());
            body.Add(new BoxEl
            {
                Direction = 1, MinWidth = 0f,
                Padding = new Edges4(ZuneListRules.GroupPadX, ZuneListRules.GroupPadTop, ZuneListRules.GroupPadX, ZuneListRules.GroupPadBottom),
                Children = toggles.ToArray(),
            });
        }

        return new BoxEl
        {
            Direction = 1, Width = ZuneListRules.CardWidth, MinWidth = ZuneListRules.CardMinWidth,
            MaxWidth = ZuneListRules.CardWidth, MaxHeight = ZuneListRules.CardMaxHeight, MinHeight = 0f, ClipToBounds = true,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = ZuneListRules.CardHeaderGap, Height = ZuneListRules.CardHeaderHeight,
                    Padding = new Edges4(ZuneListRules.CardHeaderPadLeft, 0f, ZuneListRules.CardHeaderPadRight, 0f),
                    Children =
                    [
                        R.Type.SheetTitle(a.Title) with { Case = R.Case.For(LabelKind.Heading), Color = Tok.TextPrimary, Shrink = 0f },
                        new TextEl(a.Status) { Size = meta.Size, LineHeight = meta.LineHeight, Color = Tok.TextSecondary, Grow = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                        HyperlinkButton.Create(a.ResetLabel, a.Reset, isEnabled: a.CanReset),
                    ],
                },
                Divider(),
                new ScrollEl
                {
                    ContentSized = true, Grow = 1f, MinHeight = 0f, MaxHeight = ZuneListRules.ScrollMaxHeight,
                    Content = new BoxEl { Direction = 1, MinWidth = 0f, Children = body.ToArray() },
                },
            ],
        };
    }
}
