// ── Themes/Classic/Structure/Lists/ClassicTrackChrome.cs — Classic's track table chrome (themes plan 6.1, T8b) ──────────────────
//
// From `Entities/Track.Table.Chrome.cs`: the pictures of the column header (cells, sort carets, the title label's word swap, the
// check lane's shift, the hairline), the command bar (the chromeless lane both modes share, the measured command slots, the
// labelled / glyph / split / filter buttons, the "…" slot and the count badges), the immediate-apply filter card and the blank
// placeholder row. `ClassicTrackTable.Style` carries them as `TrackListStyle.Chrome`.
//
// WHAT THE CORE KEEPS. `Track.TableHost` and its chrome components decide: the sort / filter / selection state and every decision
// on it (`TableRules.NextSort`, which commands fit, the bar's hold while the card is open, which facets the list can earn, the
// counts), every overlay's open / close and focus, and the menus' items. A template only DRAWS what its args carry and calls the
// verbs it was handed; it runs inside the core component's own render, so signals read here subscribe that component exactly as the
// source did and no key, motion or hit-test changes.
//
// The numbers are `Rules\ClassicTrackChromeLayout.cs` (WVTH003 allows no literal look here).

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

using CellKey = global::Wavee.Track.CellKey;
using ColumnSet = global::Wavee.Track.ColumnSet;
using CommandBarLayout = global::Wavee.Track.CommandBarLayout;
using DetailLayout = global::Wavee.Detail.VerticalLayout;
using Lane = global::Wavee.Track.Lane;
using RowMetrics = global::Wavee.Track.RowMetrics;
using SortColumn = global::Wavee.Track.SortColumn;
using SortSpec = global::Wavee.Track.SortSpec;
using TableRules = global::Wavee.Track.TableRules;

/// <summary>Classic's chrome for the track table: the header, the command bar's kit, the filter card and the placeholder row.</summary>
internal static class ClassicTrackChrome
{
    /// <summary>The chrome Classic registers as <c>TrackListStyle.Chrome</c>. Stateless.</summary>
    public static readonly TrackChromeTemplates Templates = new(Header, CommandKit.For(ClassicDetailTiers.Standard), FilterCard, Placeholder, GroupHeader)
    {
        UnavailableBar = UnavailableBar,
        UnavailableBarExtent = static tiers => ClassicDetailTiers.Of(in tiers).Unavailable.Extent,
    };

    // ══ THE UNAVAILABLE-SONGS BAR ════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The unavailable-songs bar (boards C-Unavailable, C-Unavailable-Compact; contract 1.22): the card plate with the accent info glyph, the
    /// sentence (the short one at Compact and Small, the one with its reasons from Standard) and "Hide them", 40 tall at Compact. The slot is mounted
    /// while the list could show the bar; its height moves between the tier's band (the bar and the air under it) and 0 with the disclosure reveal, its
    /// content clipped, so the rows below ride it instead of jumping when it comes or goes. Hidden, the button is disabled (no focus, no click).</summary>
    static Element UnavailableBar(TrackUnavailableBarArgs a)
    {
        var g = ClassicDetailTiers.Of(a.Tiers).Unavailable;
        Element bar = new BoxEl
        {
            Key = "unavailable:bar", Direction = 0, AlignItems = FlexAlign.Center, Gap = g.Gap, MinWidth = 0f, Grow = 1f, Basis = 0f,
            Height = g.Height, Shrink = 1f, Padding = g.Padding, Margin = new Edges4(0f, 0f, 0f, g.GapBelow),
            Corners = Radii.ControlAll, BorderWidth = 1f,
            Fill = Prop.Of(static () => Tok.FillCardDefault), BorderColor = Prop.Of(static () => Tok.StrokeCardDefault),
            Children =
            [
                Icon(Icons.Info, g.Glyph, Tok.AccentDefault) with { Shrink = 0f },
                new TextEl(g.Explains ? a.Explained : a.Summary)
                {
                    Size = g.TextSize, LineHeight = g.TextLine, Color = Tok.TextPrimary, Grow = 1f, Shrink = 1f, MinWidth = 0f,
                    MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                },
                Button.Create(a.ActionLabel, a.Hide, ButtonAppearance.Standard, g.ButtonSize, isEnabled: a.Shown) with { Key = "unavailable:hide", Shrink = 0f },
            ],
        };
        // The bar stands between two easing gutters (not a padding: a padding cannot ease), so a crossing that moves the rows' inset (the detail page's
        // hero form ↔ its columns form, audit B9) glides the bar with them.
        return new BoxEl
        {
            Key = "unavailable", Direction = 1, MinWidth = 0f, ClipToBounds = true, Shrink = 0f,
            Height = a.Shown ? g.Extent : 0f,
            Animate = Design.Reduced ? null : ClassicUnavailableBar.Motion,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, MinWidth = 0f, AlignItems = FlexAlign.Start,
                    Children = [InsetGutter("unavailable:lead", a.InsetX), bar, InsetGutter("unavailable:trail", a.InsetX)],
                },
            ],
        };
    }

    /// <summary>The unavailable bar's side gutter: the rows' inset, easing its width when a crossing moves it.</summary>
    static Element InsetGutter(string key, float width)
        => new BoxEl { Key = key, Width = width, Shrink = 0f, HitTestVisible = false, Layout = ClassicTierMotion.SizeReflow };

    // ══ THE PLACEHOLDER ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The blank placeholder row a slot shows before its reveal edge (ch 01 W10, ch 03 §9.4): a <c>GridEl</c>
    /// with the SAME tracks, gap, height and inner padding as the live row and one empty box per track, so the crossing
    /// is a cross-fade and never a reflow. It stays a <c>GridEl</c> on purpose — the real row mounts inside a
    /// <c>BoxEl</c> crossing wrapper, and the TYPE change is what makes the crossing a remount (ch 01 §9 trap 3).
    /// <paramref name="padX"/> is the grid's resolved inner padding, passed by the core.</summary>
    internal static Element Placeholder(in ColumnSet set, TrackSize[] tracks, float rowH, float padX)
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

    // ══ THE DATE GROUP HEAD ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The head of a date group (tuned Classic D16, board C-TrackList's liked groups): a 40-DIP band whose name (13/18 semibold)
    /// and count (13/18, secondary) sit on its floor, 6 above the row they lead, at the rows' own gutter.</summary>
    static Element GroupHeader(Func<Track.DateGroupHead?> head, float padX) => new BoxEl
    {
        Direction = 0, Height = ClassicTrackChromeLayout.GroupHeadHeight, AlignItems = FlexAlign.End, Gap = ClassicTrackChromeLayout.GroupHeadGap,
        MinWidth = 0f, ClipToBounds = true, Visible = Prop.Of(() => head() is not null),
        Padding = new Edges4(padX, 0f, padX, ClassicTrackChromeLayout.GroupHeadPadBottom),
        Children =
        [
            new TextEl(Prop.Of(() => head()?.Label ?? ""))
            {
                Size = ClassicTrackChromeLayout.GroupHeadSize, LineHeight = ClassicTrackChromeLayout.GroupHeadLine, Weight = 600,
                Color = Tok.TextPrimary, MaxLines = 1, Shrink = 0f,
            },
            new TextEl(Prop.Of(() => head()?.Count ?? ""))
            {
                Size = ClassicTrackChromeLayout.GroupHeadSize, LineHeight = ClassicTrackChromeLayout.GroupHeadLine, Color = Tok.TextSecondary,
                MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
            },
        ],
    };

    // ══ THE COLUMN HEADER ════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The header grid over the SAME TrackSize[] the rows use, keyed exactly like the row cells so a lane that
    /// leaves at a breakpoint is removed rather than reconciled against its neighbour. Shifts +28 while the check lane
    /// is in (333 ms), with a 1-DIP divider as the band's single hairline. It pays its own horizontal padding (the core's
    /// chrome adds none), so the rule can share the row plates' left edge.</summary>
    static Element Header(TrackHeaderArgs a)
    {
        var set = a.Set;
        var sort = a.Sort;
        bool checks = a.Checks;
        bool vertical = a.Vertical;
        bool classic = set.Classic;
        var cells = new List<Element>(a.Tracks.Length);
        void Add(string key, Element cell) => cells.Add(cell with { Key = key });

        // Classic's # cell is EMPTY: no clickable #, no caret slots; Sort → Custom order is the route back (W22).
        if (!set.IndexHidden) Add(CellKey.Num, classic ? new BoxEl() : IndexSortCell(a, sort));
        if (set.Heart) Add(CellKey.Heart, new BoxEl());
        if (set.Thumb) Add(CellKey.Art, new BoxEl());
        // Title owns Artist while the artist is folded into its subline; with a dedicated Artist lane each cell
        // runs its own cycle. The label component freezes its flags, so they are in its key.
        var sortSignal = a.SortSignal;
        bool artistLane = set.Artist;
        Element title = Embed.Comp(() => new LabelComponent(sortSignal, vertical, artistLane, classic))
            with { Key = "sort-label:" + (classic ? "c" : "m") + (set.Artist ? ":a" : "") + (vertical ? ":song" : "") };
        Add(CellKey.Title, SortCell(a, title, SortColumn.Title, sort, FlexJustify.Start, set.Artist));
        if (set.Artist)
            Add(CellKey.Artist, SortCell(a, HLabel(Loc.Get(Strings.Detail.Column.Artist), SortColumn.Artist, sort, classic),
                SortColumn.Artist, sort, FlexJustify.Start, artistColumn: true));
        if (set.Album)
            Add(CellKey.Album, SortCell(a, HLabel(Loc.Get(Strings.Detail.Column.Album), SortColumn.Album, sort, classic),
                SortColumn.Album, sort, FlexJustify.Start));
        if (set.By) Add(CellKey.By, PlainHeader(Loc.Get(Strings.Detail.Column.AddedBy), FlexJustify.Start, classic));
        if (set.Date)
            Add(CellKey.Date, SortCell(a, HLabel(Loc.Get(Strings.Detail.Column.DateAdded), SortColumn.DateAdded, sort, classic),
                SortColumn.DateAdded, sort, FlexJustify.Start));
        if (set.Plays)
            Add(CellKey.Plays, SortCell(a, HLabel(Loc.Get(Strings.Detail.Column.Plays), SortColumn.Plays, sort, classic),
                SortColumn.Plays, sort, FlexJustify.End));
        // BPM · Key is not sortable: tempo lands asynchronously per row, a sort would reorder under the cursor.
        if (RowMetrics.ShowTempo(in set)) Add(CellKey.Tempo, PlainHeader(Loc.Get(Strings.Detail.Column.Tempo), FlexJustify.End, classic));
        if (RowMetrics.ShowHeartTrailing(in set)) Add(CellKey.HeartTrailing, new BoxEl());
        if (RowMetrics.ShowDuration(in set))
            Add(CellKey.Duration, SortCell(a, vertical
                    ? HLabel(Loc.Get(Strings.Detail.Column.Time), SortColumn.Duration, sort, classic)
                    : Icon(Icons.Clock, ClassicTrackChromeLayout.ClockGlyph, sort.Column == SortColumn.Duration ? Tok.TextSecondary : Tok.TextTertiary),
                SortColumn.Duration, sort, FlexJustify.End));
        if (set.Actions) Add(CellKey.More, new BoxEl());
        if (set.Expand) Add(CellKey.Expand, new BoxEl());

        // ONE left edge (tuned Classic D9 / V10): the rule starts where the row plates start (a Modern row's RowInset margin, a Classic
        // or a quiet row's 0 — Liked's rule runs the list's full width, D-9), and the cells sit at the rows' own gutter inside it, so "#" stays over the numbers.
        float padX = RowMetrics.PadXFor(set.Tier);
        float edge = ClassicTrackChromeLayout.HeaderRuleInset(classic || set.Quiet, vertical, padX);
        // The Compact row form has no column header (board C-TrackList compact): only its hairline stays, so the list keeps its one rule
        // under the bar and the chrome's extent is the hairline (`TableRules.HeaderHeightFor(set)` is 0).
        if (set.Compact)
            return new BoxEl
            {
                Direction = 1, MinWidth = 0f, Padding = new Edges4(edge, 0f, edge, 0f),
                Children = [new BoxEl { Height = 1f, Fill = Prop.Of(static () => Tok.StrokeDividerDefault) }],
            };
        return new BoxEl
        {
            Direction = 1, MinWidth = 0f, Padding = new Edges4(edge, 0f, edge, 0f),
            Children =
            [
                new BoxEl
                {
                    Direction = 1, ClipToBounds = true,
                    Padding = new Edges4(checks ? ClassicTrackChromeLayout.CheckLaneShift : 0f, 0f, 0f, 0f),
                    Animate = ClassicTrackChromeLayout.HeaderShift,
                    Children =
                    [
                        new GridEl
                        {
                            Columns = a.Tracks, ColGap = RowMetrics.ColGapFor(set.Tier),
                            Padding = new Edges4(padX - edge, 0f, padX - edge, 0f),
                            RowHeight = TableRules.HeaderHeightFor(classic), Children = cells.ToArray(),
                        },
                        new BoxEl { Height = 1f, Fill = Prop.Of(static () => Tok.StrokeDividerDefault) },
                    ],
                },
            ],
        };
    }

    /// <summary>The owning header brightens — except Index, the default order, which carries no indicator.</summary>
    static TextEl HLabel(string s, SortColumn col, SortSpec sort, bool classic)
    {
        var t = classic ? R.Type.MicroMeta(s) : Ui.Caption(s);
        return t with
        {
            Weight = 600,
            Color = TableRules.HeaderActive(col, sort.Column, false) ? Tok.TextSecondary : Tok.TextTertiary,
            MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
        };
    }

    static Element PlainHeader(string label, FlexJustify justify, bool classic)
    {
        var t = classic ? R.Type.MicroMeta(label) : Ui.Caption(label);
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Justify = justify, MinWidth = 0f, ClipToBounds = true,
            Children =
            [
                t with
                {
                    Weight = 600, Color = Tok.TextTertiary, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                },
            ],
        };
    }

    /// <summary>The # sits at the exact centre of its lane: the SAME caret slot is reserved on both sides and the
    /// descending caret lives only in the right one, so the indicator never nudges # off the row numbers.</summary>
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
            Corners = Radii.ControlAll, HoverFill = Prop.Of(static () => Tok.FillSubtleSecondary),
            Role = AutomationRole.Button,
            OnClick = () => a.OnSort(SortColumn.Index, false),
            Children =
            [
                Side(false),
                new BoxEl
                {
                    Grow = 1f, MinWidth = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children = [HLabel(Loc.Get(Strings.Detail.Column.Number), SortColumn.Index, sort, false)],
                },
                Side(true),
            ],
        };
    }

    /// <summary>A clickable header: the NextSort cycle, the caret after the label (before it on an end-aligned
    /// lane). Shrinkable + clipped, the same squeeze contract as the row cells.</summary>
    static Element SortCell(TrackHeaderArgs a, Element content, SortColumn col, SortSpec sort, FlexJustify justify,
                            bool artistColumn = false)
    {
        bool caret = TableRules.HeaderActive(col, sort.Column, artistColumn) && (col != SortColumn.Index || sort.Descending);
        var signal = a.SortSignal;
        Element[] kids = !caret ? [content]
            : justify == FlexJustify.End ? [Embed.Comp(() => new CaretComponent(signal)), content]
            : [content, Embed.Comp(() => new CaretComponent(signal))];
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Justify = justify, Gap = Spacing.XS, MinWidth = 0f, ClipToBounds = true,
            Corners = Radii.ControlAll, HoverFill = Prop.Of(static () => Tok.FillSubtleSecondary),
            Role = AutomationRole.Button,
            OnClick = () => a.OnSort(col, artistColumn),
            Children = kids,
        };
    }

    /// <summary>The caret pops in when its column becomes the sort and SPRINGS its rotation 0°↔180° on every flip, so
    /// Title↑ → Title↓ → Artist↑ → Artist↓ reads as one continuous rotation, never a glyph swap.</summary>
    sealed class CaretComponent(IReadSignal<SortSpec> sort) : Component
    {
        public override Element Render()
        {
            bool desc = sort.Value.Descending;
            UseTransition(AnimChannel.Opacity, 0f, 1f, Expressive.Fast, Easing.EaseInOut, "in");
            UseTransition(AnimChannel.ScaleX, ClassicTrackChromeLayout.CaretSeedScale, 1f, Expressive.Fast, Easing.Overshoot, "in");
            UseTransition(AnimChannel.ScaleY, ClassicTrackChromeLayout.CaretSeedScale, 1f, Expressive.Fast, Easing.Overshoot, "in");
            UseSpring(AnimChannel.Rotation, desc ? 180f : 0f, SpringParams.FromResponse(0.30f, 0.7f), desc);
            return new BoxEl
            {
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = [Icon(Icons.CaretSolidUp, ClassicTrackChromeLayout.CaretGlyph, Tok.TextSecondary)],
            };
        }
    }

    /// <summary>"Title" (the hero arm's "Song"), or "Artist" while the Title header owns an artist sort; the word swap
    /// rises 4 DIP and fades, keyed on the text.</summary>
    sealed class LabelComponent(IReadSignal<SortSpec> sort, bool song, bool artistColumn, bool classic) : Component
    {
        public override Element Render()
        {
            var col = sort.Value.Column;
            string text = !artistColumn && col == SortColumn.Artist
                ? Loc.Get(Strings.Detail.Column.Artist)
                : Loc.Get(song ? Strings.Detail.Column.Song : Strings.Detail.Column.Title);
            UseTransition(AnimChannel.Opacity, 0f, 1f, Expressive.Fast, Easing.SmoothOut, text);
            UseTransition(AnimChannel.TranslateY, ClassicTrackChromeLayout.LabelRise, 0f, Expressive.Fast, Easing.SmoothOut, text);
            var t = classic ? R.Type.MicroMeta(text) : Ui.Caption(text);
            return t with
            {
                Weight = 600,
                Color = TableRules.HeaderActive(SortColumn.Title, col, artistColumn) ? Tok.TextSecondary : Tok.TextTertiary,
                MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            };
        }
    }

    // ══ THE COMMAND BAR ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Real SplitButton behaviour (two hit targets, keyboard chords) wearing CommandBar visuals: the joined
    /// root is ghosted at rest instead of painting the boxed form-control surface.</summary>
    static readonly TemplateParts s_splitParts = SplitParts(ClassicTrackChromeLayout.CommandHeight);

    /// <summary>The Compact bar's split (TC5): the same parts at the 40 target, the chevron half 40 square.</summary>
    static readonly TemplateParts s_splitPartsCompact = SplitParts(ClassicTrackChromeLayout.CommandHeightFor(ClassicDetailTiers.CompactTarget));

    static TemplateParts SplitParts(float h) => new()
    {
        [SplitButton.PartRoot] = e => e with
        {
            AlignSelf = FlexAlign.Center, MinHeight = h, Fill = ColorF.Transparent, BorderWidth = 0f, BorderBrush = null,
            Corners = Radii.ControlAll,
        },
        [SplitButton.PartPrimaryButton] = e => e with
        {
            Grow = 0f, MinWidth = 0f, Height = h, Padding = new Edges4(9f, 0f, 8f, 0f),
        },
        [SplitButton.PartSecondaryButton] = e => e with
        {
            Width = MathF.Max(ClassicTrackChromeLayout.SplitSecondaryWidth, h > ClassicTrackChromeLayout.CommandHeight ? h : 0f), Height = h, Padding = default,
            Justify = FlexJustify.Center,
        },
        [SplitButton.PartDivider] = static e => e with
        {
            Width = 1f, Height = ClassicTrackChromeLayout.SplitDividerHeight, AlignSelf = FlexAlign.Center,
            Fill = Prop.Of(static () => Tok.StrokeDividerDefault),
        },
    };

    /// <summary>The count badge's corner placement: top-right of a 32×32 button, ringed in the page's base so it reads over a plate.
    /// Shared by the funnel and by "…" (an active filter badges the button that now owns "Filter…").</summary>
    static readonly TemplateParts s_badgeCorner = new()
    {
        [InfoBadge.PartRoot] = static b => b with
        {
            AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.End, BorderWidth = ClassicTrackChromeLayout.BadgeRing, BorderColor = Tok.FillSolidBase,
        },
    };

    /// <summary>The same corner badge nudged out by 3 DIP, so on "…" (a centred row of dots) it clears the glyph.</summary>
    static readonly TemplateParts s_badgeCornerMore = new()
    {
        [InfoBadge.PartRoot] = static b => b with
        {
            AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.End, BorderWidth = ClassicTrackChromeLayout.BadgeRing, BorderColor = Tok.FillSolidBase,
            OffsetX = ClassicTrackChromeLayout.BadgeNudgeMore, OffsetY = -ClassicTrackChromeLayout.BadgeNudgeMore,
        },
    };

    /// <summary>The count badge of the command bar's Filter button: 18 DIP round for one digit, accent, growing rightward for two
    /// (the labelled button's <see cref="CommandBarLayout.FilterBadgeSlot"/> is wide enough). Inline it sits at the slot's left edge.</summary>
    static readonly TemplateParts s_badgeCommand = new()
    {
        [InfoBadge.PartRoot] = static b => b with
        {
            MinWidth = ClassicTrackChromeLayout.BadgeSize, Height = ClassicTrackChromeLayout.BadgeSize, MaxHeight = ClassicTrackChromeLayout.BadgeSize,
            Corners = CornerRadius4.All(ClassicTrackChromeLayout.BadgeRadius), Shrink = 0f,
            AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Start,
        },
    };

    /// <summary>The same 18-DIP badge on the icon-only button's top-right corner, ringed in the page's base, nudged out by 4 DIP (never past the bar's gap to the next button).</summary>
    static readonly TemplateParts s_badgeCommandCorner = new()
    {
        [InfoBadge.PartRoot] = static b => b with
        {
            MinWidth = ClassicTrackChromeLayout.BadgeSize, Height = ClassicTrackChromeLayout.BadgeSize, MaxHeight = ClassicTrackChromeLayout.BadgeSize,
            Corners = CornerRadius4.All(ClassicTrackChromeLayout.BadgeRadius), Shrink = 0f,
            AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.End, BorderWidth = ClassicTrackChromeLayout.BadgeRing, BorderColor = Tok.FillSolidBase,
            OffsetX = MathF.Min(ClassicTrackChromeLayout.BadgeNudgeCommand, CommandBarLayout.Gap), OffsetY = -ClassicTrackChromeLayout.BadgeNudgeCommand,
            HitTestVisible = false,
        },
    };

    /// <summary>The command bar's pictures (the contract is <see cref="ITrackCommandKit"/>).</summary>
    sealed class CommandKit : ITrackCommandKit
    {
        /// <summary>The window tier's record this kit draws (Tiers.Detail.cs): the table hands its own tiers through <see cref="In"/>, so the kit follows
        /// the facts the table is drawn in, never the process's last window. One kit per record, built once.</summary>
        readonly ClassicDetailTier _tier;
        static readonly Dictionary<ClassicDetailTier, CommandKit> s_byTier = new();

        CommandKit(ClassicDetailTier tier) => _tier = tier;

        public static CommandKit For(ClassicDetailTier tier)
        {
            if (!s_byTier.TryGetValue(tier, out var kit)) s_byTier[tier] = kit = new CommandKit(tier);
            return kit;
        }

        public ITrackCommandKit In(in global::Wavee.Theming.TierFacts tiers)
        {
            var tier = ClassicDetailTiers.Of(tiers);
            return Equals(tier, _tier) ? this : For(tier);
        }

        /// <summary>The chromeless 44-DIP lane both modes share; the keyed mode box is what animates browse ↔ selection. At Compact (the tier record)
        /// the lane's padding gives its 40 targets the room (the lane keeps the 44 the hero's band reserves), and the selection mode is tinted with
        /// the accent (board C-MultiSelect-Compact).</summary>
        public Element Surface(string mode, Element content)
        {
            var tier = _tier;
            float padY = ClassicTrackChromeLayout.SurfacePadYFor(tier.Target);
            bool tinted = tier.SelectionTinted && mode == "selection";
            return new BoxEl
            {
                Direction = 1, Height = DetailLayout.ToolbarRowHeight, MinWidth = 0f, ClipToBounds = true,
                Padding = new Edges4(DetailLayout.ToolbarSurfacePadX, padY, DetailLayout.ToolbarSurfacePadX, padY),
                Fill = tinted ? Prop.Of(static () => Tok.AccentDefault with { A = ClassicTrackChromeLayout.SelectionTint }) : (Prop<ColorF>)ColorF.Transparent,
                Corners = Radii.ControlAll,
                Children =
                [
                    new BoxEl
                    {
                        Key = "commandbar-mode:" + mode, Direction = 1, Grow = 1f, MinWidth = 0f,
                        Animate = ClassicTrackChromeLayout.ModeMotion, Children = [content],
                    },
                ],
            };
        }

        /// <summary>The square commands' edge at the window tier on screen (the tier record's target: 40 at Compact).</summary>
        public float CommandEdge => ClassicTrackChromeLayout.PlateFor(_tier.Target);

        public Element Command(string key, Element command, Action<float> onWidth) => new BoxEl
        {
            Key = key, Direction = 1, Shrink = 0f, Animate = ClassicTrackChromeLayout.CommandMotion,
            OnBoundsChanged = r => onWidth(r.W),
            Children = [command],
        };

        /// <summary>The 44 list bar (tuned Classic D3, board <c>C-Album</c> / <c>C-Playlist</c>): [Play next │ ⌄] [Shuffle] [Tune], the flexible
        /// gap, then [Filter (n)] [Sort ⌄], Find led by its own 6 DIP, [Insights] [⋯] — every command 2 DIP from the next. The page bar
        /// (board <c>C-Liked</c>, tuned Classic D8) puts its chips in the gap's place: they take what the commands leave (never under 120) and
        /// end 8 DIP before the tools, where the chip rail's own 24-DIP edge fade says there is more.
        /// <para>At Compact (board C-Album-Compact) the sort word LEADS the bar and the tools after the gap are glyphs: the sort command moves
        /// from the view group to the left one.</para></summary>
        public Element Bar(Element[] left, Element[] view, Element search, Element[] tail, Element? middle)
        {
            if (_tier.SortLeadsBar)
            {
                var lead = new List<Element>(left.Length + 1);
                var rest = new List<Element>(view.Length);
                lead.AddRange(left);
                foreach (var v in view) (v.Key == SortCommandKey ? lead : rest).Add(v);
                left = lead.ToArray();
                view = rest.ToArray();
            }
            // ONE row of keyed commands (audit B9): the sort word that leads the Compact bar is reordered in place, never re-parented into another
            // group's box (which rebuilt it). The groups are the order: [left] [gap or chips] [view] [find] [tail]; an empty left group keeps its slot.
            var kids = new List<Element>(left.Length + view.Length + tail.Length + 3);
            if (left.Length == 0) kids.Add(new BoxEl { Key = "bar:left", Shrink = 0f });
            kids.AddRange(left);
            kids.Add(middle is null
                ? new BoxEl { Key = "bar:gap", Grow = 1f, MinWidth = CommandBarLayout.GroupGap }
                : new BoxEl
                {
                    Key = "bar:chips", Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = CommandBarLayout.InlineChipsMin,
                    Margin = new Edges4(0f, 0f, CommandBarLayout.InlineChipsTrail, 0f), Children = [middle],
                });
            kids.AddRange(view);
            kids.Add(new BoxEl
            {
                Key = "bar:find", Direction = 1, Shrink = 0f,
                Margin = new Edges4(CommandBarLayout.SearchGap - CommandBarLayout.Gap, 0f, 0f, 0f), Children = [search],
            });
            kids.AddRange(tail);
            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = CommandBarLayout.Gap, Grow = 1f, MinWidth = 0f,
                Children = kids.ToArray(),
            };
        }

        /// <summary>The core's key for the measured sort command (<c>cmd:sort</c>).</summary>
        const string SortCommandKey = "cmd:sort";

        /// <summary>The page bar's lead (board <c>C-Liked</c>, tuned Classic D16): the detail header's own Play split (D20: one Play, never a
        /// fork), 8 DIP on the bordered standard Shuffle with its word, and the 17-DIP slot of the 20-DIP divider before the chips.</summary>
        public Element PageLead(EntityUri subject, Func<ColorF> accent, Action play, string shuffleLabel, Action shuffle) => new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = CommandBarLayout.Gap, Shrink = 0f,
            Children =
            [
                ClassicDetailKit.PlaySplit(subject, accent, play, _tier.Target),
                Button.Create(shuffleLabel, shuffle, ButtonAppearance.Standard, glyph: Icons.Shuffle) with
                {
                    Key = "lead:shuffle", Shrink = 0f, Height = ClassicTrackChromeLayout.CommandHeightFor(_tier.Target),
                    Margin = new Edges4(ClassicTrackChromeLayout.PageShuffleLead, 0f, 0f, 0f),
                },
                new BoxEl
                {
                    Key = "lead:divider", Width = ClassicTrackChromeLayout.PageDividerSlot, Shrink = 0f,
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children =
                    [
                        new BoxEl
                        {
                            Width = 1f, Height = ClassicTrackChromeLayout.SplitDividerHeight, Fill = Prop.Of(static () => Tok.StrokeDividerDefault),
                        },
                    ],
                },
            ],
        };

        public Element PlayNext(string label, Action onPlay, IReadOnlyList<MenuFlyoutItem> items)
        {
            // SplitButton owns its content after mount: the ink rides a binding so a theme flip still reaches it.
            Element content = new BoxEl
            {
                Direction = 0, Gap = ClassicTrackChromeLayout.CommandGap, AlignItems = FlexAlign.Center,
                Children =
                [
                    new TextEl(WaveeIcons.PlayNext)
                    {
                        Size = ClassicTrackChromeLayout.PlayNextGlyph, FontFamily = WaveeIcons.Font,
                        Color = Prop.Of(static () => Tok.TextSecondary),
                    },
                    new TextEl(label) { Size = Ui.Caption("").Size, Weight = 600, Color = Prop.Of(static () => Tok.TextSecondary) },
                ],
            };
            return SplitButton.Create(content, onPlay, items,
                parts: _tier.Target > ClassicTrackChromeLayout.CommandHeight ? s_splitPartsCompact : s_splitParts);
        }

        /// <summary>The labeled (icon + text) command — every inline command is labeled; one that does not fit is evicted,
        /// never shrunk to a glyph (ch 04 §0.10).</summary>
        public Element Labeled(string glyph, string label, bool active, Action onClick, Action<NodeHandle>? onRealized, Element? trailing)
        {
            Prop<ColorF> ink = active ? Prop.Of(static () => Tok.AccentTextPrimary) : Prop.Of(static () => Tok.TextSecondary);
            Element[] kids = trailing is null
                ? [Icon(glyph, ClassicTrackChromeLayout.CommandGlyph) with { Color = ink }, Ui.Caption(label) with { Weight = 600, Color = ink }]
                : [Icon(glyph, ClassicTrackChromeLayout.CommandGlyph) with { Color = ink }, Ui.Caption(label) with { Weight = 600, Color = ink }, trailing];
            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = ClassicTrackChromeLayout.CommandGap,
                Height = ClassicTrackChromeLayout.CommandHeightFor(_tier.Target), Padding = new Edges4(9f, 0f, 10f, 0f),
                Corners = Radii.ControlAll,
                Fill = active ? Prop.Of(static () => Tok.AccentTextPrimary with { A = 0.11f }) : (Prop<ColorF>)ColorF.Transparent,
                HoverFill = active ? Prop.Of(static () => Tok.AccentTextPrimary with { A = 0.17f }) : Prop.Of(static () => Tok.FillSubtleSecondary),
                PressedFill = active ? Prop.Of(static () => Tok.AccentTextPrimary with { A = 0.08f }) : Prop.Of(static () => Tok.FillSubtleTertiary),
                HoverDurationMs = Motion.ControlFaster, PressDurationMs = Motion.ControlFaster,
                Role = AutomationRole.Button, Focusable = true,
                OnClick = onClick, OnRealized = onRealized,
                Children = kids,
            };
        }

        /// <summary>The 32×32 toolbar glyph button: active → the accent@0.11 plate + accent glyph; idle → ghost.</summary>
        public Element IconButton(string glyph, bool active, Action onClick, Action<NodeHandle>? onRealized) => new BoxEl
        {
            Width = ClassicTrackChromeLayout.PlateFor(_tier.Target), Height = ClassicTrackChromeLayout.PlateFor(_tier.Target),
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = Radii.ControlAll,
            Fill = active ? Prop.Of(static () => Tok.AccentTextPrimary with { A = 0.11f }) : (Prop<ColorF>)ColorF.Transparent,
            HoverFill = active ? Prop.Of(static () => Tok.AccentTextPrimary with { A = 0.17f }) : Prop.Of(static () => Tok.FillSubtleSecondary),
            PressedFill = active ? Prop.Of(static () => Tok.AccentTextPrimary with { A = 0.08f }) : Prop.Of(static () => Tok.FillSubtleTertiary),
            HoverDurationMs = Motion.ControlFaster, PressDurationMs = Motion.ControlFaster,
            Role = AutomationRole.Button, Focusable = true,
            OnClick = onClick, OnRealized = onRealized,
            Children = [Icon(glyph, ClassicTrackChromeLayout.CommandGlyph) with { Color = active ? Prop.Of(static () => Tok.AccentTextPrimary) : Prop.Of(static () => Tok.TextSecondary) }],
        };

        public Element Chevron() => Icon(Icons.ChevronDown, ClassicTrackChromeLayout.ChevronGlyph, Tok.TextTertiary);

        /// <summary>The Sort command: the labelled one (glyph, the order's word, the chevron), or where the tier record draws it as a glyph (Small: C-Album-Small,
        /// C-Playlist-Small, C-Liked-Small) the sort glyph and the chevron alone, 4 apart, the order's word in its tooltip.</summary>
        public Element SortCommand(string label, bool active, Action onClick, Action<NodeHandle> onRealized)
        {
            var tier = _tier;
            if (!tier.SortGlyph) return Labeled(Icons.Sort, label, active, onClick, onRealized, Chevron());
            Prop<ColorF> ink = active ? Prop.Of(static () => Tok.AccentTextPrimary) : Prop.Of(static () => Tok.TextSecondary);
            return ToolTip.Wrap(new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = ClassicTrackChromeLayout.SortGlyphGap,
                Height = ClassicTrackChromeLayout.CommandHeightFor(tier.Target), Padding = ClassicTrackChromeLayout.SortGlyphPad,
                Corners = Radii.ControlAll,
                Fill = active ? Prop.Of(static () => Tok.AccentTextPrimary with { A = 0.11f }) : (Prop<ColorF>)ColorF.Transparent,
                HoverFill = active ? Prop.Of(static () => Tok.AccentTextPrimary with { A = 0.17f }) : Prop.Of(static () => Tok.FillSubtleSecondary),
                PressedFill = active ? Prop.Of(static () => Tok.AccentTextPrimary with { A = 0.08f }) : Prop.Of(static () => Tok.FillSubtleTertiary),
                HoverDurationMs = Motion.ControlFaster, PressDurationMs = Motion.ControlFaster,
                Role = AutomationRole.Button, Focusable = true,
                OnClick = onClick, OnRealized = onRealized,
                Children = [Icon(Icons.Sort, ClassicTrackChromeLayout.CommandGlyph) with { Color = ink }, Chevron()],
            }, Strings.Detail.Sort.Named(label));
        }

        // The row-size control (M3): the style's density option, in "…" only (D3).
        public IReadOnlyList<MenuFlyoutItem> DensityItems() => ClassicTrackDensity.Items();

        /// <summary>The command bar's Filter button: a subtle button, funnel + "Filter" (<paramref name="labelled"/>) or the funnel alone,
        /// 32 DIP tall. While <paramref name="count"/> filters are on it takes the accent tint (14% fill, accent border, accent ink) and
        /// an 18-DIP count badge. The border is always laid (transparent at rest) and the labelled form always reserves the badge's
        /// slot, so a filter turning on or off changes colour and nothing else: the fit never sees the count.</summary>
        public Element FilterCommand(int count, bool labelled, string label, Action onClick, Action<NodeHandle> onRealized)
        {
            bool active = count > 0;
            Prop<ColorF> ink = active ? Prop.Of(static () => Tok.AccentTextPrimary) : Prop.Of(static () => Tok.TextSecondary);
            Element glyph = Icon(Icons.Filter, ClassicTrackChromeLayout.CommandGlyph) with { Color = ink };
            float target = _tier.Target;
            var box = new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Height = ClassicTrackChromeLayout.CommandHeightFor(target), Shrink = 0f,
                Corners = Radii.ControlAll, BorderWidth = 1f,
                Fill = active ? Prop.Of(static () => Tok.AccentDefault with { A = 0.14f }) : (Prop<ColorF>)ColorF.Transparent,
                HoverFill = active ? Prop.Of(static () => Tok.AccentDefault with { A = 0.22f }) : Prop.Of(static () => Tok.FillSubtleSecondary),
                PressedFill = active ? Prop.Of(static () => Tok.AccentDefault with { A = 0.10f }) : Prop.Of(static () => Tok.FillSubtleTertiary),
                BorderColor = active ? Prop.Of(static () => Tok.AccentDefault) : (Prop<ColorF>)ColorF.Transparent,
                HoverDurationMs = Motion.ControlFaster, PressDurationMs = Motion.ControlFaster, BrushTransitionMs = Motion.ControlFaster,
                Role = AutomationRole.Button, Focusable = true,
                OnClick = onClick, OnRealized = onRealized,
            };
            // The tier record may draw it as the funnel alone whatever the bar's fit (Compact: C-Album-Compact, C-Playlist-Compact, C-Liked-Compact).
            if (labelled && !_tier.FilterGlyph)
                return box with
                {
                    Gap = ClassicTrackChromeLayout.CommandGap, Padding = new Edges4(9f, 0f, 6f, 0f),
                    Children =
                    [
                        glyph,
                        Ui.Caption(label) with { Weight = 600, Color = ink },
                        new BoxEl
                        {
                            Width = CommandBarLayout.FilterBadgeSlot, Height = ClassicTrackChromeLayout.BadgeSize, Shrink = 0f, AlignItems = FlexAlign.Center,
                            Children = active ? [InfoBadge.Count(count, parts: s_badgeCommand)] : [],
                        },
                    ],
                };
            // Icon-only: the funnel carries more ink above its midpoint (+1 optical offset at rest); with a badge it steps (-4, +4) so glyph
            // and pill never share ink.
            return box with
            {
                ZStack = true, Width = MathF.Max(CommandBarLayout.FilterIconWidth, target), Justify = FlexJustify.Center,
                Children =
                [
                    new BoxEl
                    {
                        Width = ClassicTrackChromeLayout.FunnelGlyph, Height = ClassicTrackChromeLayout.FunnelGlyph,
                        AlignSelf = FlexAlign.Center, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                        OffsetX = active ? ClassicTrackChromeLayout.FunnelBadgedX : 0f,
                        OffsetY = active ? ClassicTrackChromeLayout.FunnelBadgedY : ClassicTrackChromeLayout.FunnelRestY,
                        Children = [glyph],
                    },
                    .. active ? new Element[] { InfoBadge.Count(count, parts: s_badgeCommandCorner) } : [],
                ],
            };
        }

        public Element FilterFunnel(int count, Action onClick, Action<NodeHandle> onRealized)
        {
            bool active = count > 0;
            ColorF accent = Tok.AccentTextPrimary;
            // The funnel carries more ink above its midpoint: +1 optical offset at rest; with a badge it steps (−4, +4)
            // so glyph and pill never share ink. Accent belongs to the plate and badge, never the glyph.
            Element glyph = new BoxEl
            {
                Width = ClassicTrackChromeLayout.FunnelGlyph, Height = ClassicTrackChromeLayout.FunnelGlyph,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                OffsetX = active ? ClassicTrackChromeLayout.FunnelBadgedX : 0f,
                OffsetY = active ? ClassicTrackChromeLayout.FunnelBadgedY : ClassicTrackChromeLayout.FunnelRestY,
                Children = [Icon(Icons.Filter, ClassicTrackChromeLayout.FunnelGlyph, active ? Tok.TextPrimary : Tok.TextSecondary)],
            };
            return new BoxEl
            {
                ZStack = true, Width = ClassicTrackChromeLayout.PlateSize, Height = ClassicTrackChromeLayout.PlateSize, AlignSelf = FlexAlign.Center,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.ControlAll,
                Fill = active ? accent with { A = 0.16f } : ColorF.Transparent,
                HoverFill = active ? accent with { A = 0.24f } : Tok.FillSubtleSecondary,
                PressedFill = active ? accent with { A = 0.12f } : Tok.FillSubtleTertiary,
                Role = AutomationRole.Button, Focusable = true,
                OnClick = onClick, OnRealized = onRealized,
                Children = active ? [glyph, InfoBadge.Count(count, parts: s_badgeCorner)] : [glyph],
            };
        }

        /// <summary>The button's slot never changes size: the badge is an overlay on a fixed 32-DIP box, so a filter turning on moves nothing.</summary>
        public Element MoreSlot(Element more, int filterCount) => new BoxEl
        {
            ZStack = true, Width = ClassicTrackChromeLayout.PlateFor(_tier.Target),
            Height = ClassicTrackChromeLayout.PlateFor(_tier.Target), Shrink = 0f, AlignSelf = FlexAlign.Center,
            Children = filterCount > 0
                ? [more, InfoBadge.Count(filterCount, parts: s_badgeCornerMore) with { HitTestVisible = false, Key = "more:filter-badge" }]
                : [more],
        };
    }

    // ══ THE FILTER CARD ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The immediate-apply filter card ("Flyout B"): 360 wide, a header ("Filter", what the list shows against what it has,
    /// "Reset"), a divider, then ONE 44-DIP row per facet - its word on the left, a 168-DIP <see cref="ComboBox"/> on the right
    /// (a row off its default tints its combo with the accent and its word turns primary) - a divider, and the two status
    /// <see cref="ToggleSwitch"/> rows. The card normally fits whole; the scroll region (&lt;= 500) is the fallback only.</summary>
    static Element FilterCard(TrackFilterCardArgs a)
    {
        Element Label(string text, bool on) => new TextEl(text)
        {
            Size = ClassicTrackChromeLayout.FacetLabelSize, Color = on ? Tok.TextPrimary : Tok.TextSecondary, Grow = 1f, MinWidth = 0f,
            MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
        };

        BoxEl Row(string key, bool on, string label, Element control) => new()
        {
            Key = key, Direction = 0, Height = ClassicTrackChromeLayout.FacetRowHeight, AlignItems = FlexAlign.Center, Gap = ClassicTrackChromeLayout.FacetGap,
            Padding = new Edges4(ClassicTrackChromeLayout.FacetRowPadX, 0f, ClassicTrackChromeLayout.FacetRowPadX, 0f),
            Children = [Label(label, on), control],
        };

        // One facet: the label and a combo whose tint (accent 14% fill + accent border, laid over the stock field, which has no
        // style seam) reads "this row is filtering". The overlay is always there (transparent at rest), so a value change
        // never restructures the row and the combo keeps its focus.
        Element Pick(in TrackFilterFacet f)
        {
            bool on = f.On;
            return Row(f.Key, on, f.Label, new BoxEl
            {
                ZStack = true, Width = ClassicTrackChromeLayout.ComboWidth, Shrink = 0f,
                Children =
                [
                    ComboBox.Create(f.Options, f.Value, width: ClassicTrackChromeLayout.ComboWidth, placeholder: f.Placeholder, onChange: f.Changed)
                        with { Key = f.Key + ":combo" },
                    new BoxEl
                    {
                        Width = ClassicTrackChromeLayout.ComboWidth, AlignSelf = FlexAlign.Stretch, Corners = Radii.ControlAll, HitTestVisible = false, BorderWidth = 1f,
                        Fill = on ? Prop.Of(static () => Tok.AccentDefault with { A = 0.14f }) : (Prop<ColorF>)ColorF.Transparent,
                        BorderColor = on ? Prop.Of(static () => Tok.AccentDefault) : (Prop<ColorF>)ColorF.Transparent,
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
                Padding = new Edges4(ClassicTrackChromeLayout.GroupPadX, ClassicTrackChromeLayout.GroupPadTop, ClassicTrackChromeLayout.GroupPadX,
                                     toggles.Count > 0 ? ClassicTrackChromeLayout.GroupPadTop : ClassicTrackChromeLayout.GroupPadBottom),
                Children = rows.ToArray(),
            },
        };
        if (toggles.Count > 0)
        {
            body.Add(Divider());
            body.Add(new BoxEl
            {
                Direction = 1, MinWidth = 0f,
                Padding = new Edges4(ClassicTrackChromeLayout.GroupPadX, ClassicTrackChromeLayout.GroupPadTop, ClassicTrackChromeLayout.GroupPadX,
                                     ClassicTrackChromeLayout.GroupPadBottom),
                Children = toggles.ToArray(),
            });
        }

        return new BoxEl
        {
            Direction = 1, Width = ClassicTrackChromeLayout.CardWidth, MinWidth = ClassicTrackChromeLayout.CardMinWidth,
            MaxWidth = ClassicTrackChromeLayout.CardWidth, MaxHeight = ClassicTrackChromeLayout.CardMaxHeight, MinHeight = 0f,
            ClipToBounds = true,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = ClassicTrackChromeLayout.CardHeaderGap, Height = ClassicTrackChromeLayout.CardHeaderHeight,
                    Padding = new Edges4(ClassicTrackChromeLayout.CardHeaderPadLeft, 0f, ClassicTrackChromeLayout.CardHeaderPadRight, 0f),
                    Children =
                    [
                        new TextEl(a.Title) { Size = ClassicTrackChromeLayout.CardTitleSize, Weight = 600, Color = Tok.TextPrimary },
                        Ui.Caption(a.Status) with { Color = Tok.TextSecondary, Grow = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                        HyperlinkButton.Create(a.ResetLabel, a.Reset, isEnabled: a.CanReset),
                    ],
                },
                Divider(),
                new ScrollEl
                {
                    ContentSized = true, Grow = 1f, MinHeight = 0f, MaxHeight = ClassicTrackChromeLayout.ScrollMaxHeight,
                    Content = new BoxEl { Direction = 1, MinWidth = 0f, Children = body.ToArray() },
                },
            ],
        };
    }
}
