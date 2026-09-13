using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scroll;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>The rich, immediate-apply detail-list filter surface. Each selector owns a concrete signal for the lifetime
/// of one flyout session; its callback projects the changed facet back into the aggregate list filter signal.
///
/// LAYOUT BUDGET is a real constraint here, not a nicety: the card caps its scroll region, so every facet that stacks
/// its label above its control instead of sitting beside it pushes the next one below the fold. A user should be able
/// to see the whole filter surface at once on a normal window and only scroll when several facets are expanded — which
/// is why the scope picker is one segmented row rather than a 2x2 grid of buttons, the trait facets put their label and
/// their control on the SAME line, and Status folds into Content instead of carrying its own title and divider.</summary>
sealed class TrackFilterFlyout : Component
{
    readonly IReadSignal<TrackFilterState> _filters;
    readonly Action<TrackFilterState> _setFilters;
    readonly TrackFilterCapabilities _caps;
    readonly Signal<int> _scope;
    readonly Signal<int> _explicit;
    readonly Signal<int> _video;
    readonly Signal<bool> _liked;
    readonly Signal<bool> _playable;
    readonly Signal<int> _duration;
    readonly Signal<int> _added;
    readonly Signal<int> _tempo;
    readonly Signal<int> _origin;

    /// <summary>One collapsible "more filters" facet: its open state, the root node it realized into (so an expand can
    /// bring it into view) and the template parts that capture that node. These live on the INSTANCE rather than in
    /// hooks because WHICH sections render depends on capabilities — a hook per section would shift hook order the
    /// moment enrichment adds the Tempo facet mid-session.</summary>
    sealed class Section
    {
        public readonly Signal<bool> Open = new(false);
        public NodeHandle Node;
        public TemplateParts Parts = null!;
    }

    readonly Section _durationSec = new();
    readonly Section _addedSec = new();
    readonly Section _tempoSec = new();
    readonly Section _originSec = new();
    readonly Section[] _sections;
    NodeHandle _scrollNode;

    /// <summary>The chromeless-disclosure restyle. Built per section so each can capture its own root node; everything
    /// except that capture is identical across the four.</summary>
    static TemplateParts DisclosurePartsFor(Section s) => new()
    {
        [Expander.PartRoot] = r => r with { OnRealized = h => s.Node = h },
        [Expander.PartHeader] = static h => h with
        {
            MinHeight = 42f,
            Padding = new Edges4(8f, 0f, 0f, 0f),
            Fill = ColorF.Transparent,
            BorderWidth = 0f,
            Corners = Radii.ControlAll,
        },
        [Expander.PartChevron] = static c => c with
        {
            Width = 28f,
            Height = 28f,
            Margin = new Edges4(8f, 0f, 4f, 0f),
        },
        [Expander.PartContent] = static c => c with
        {
            Padding = new Edges4(10f, 2f, 8f, 10f),
            MinHeight = 0f,
            Fill = ColorF.Transparent,
            BorderWidth = 0f,
            Margin = default,
            Corners = default,
        },
    };

    // Card geometry. The scroll cap is the binding constraint (the outer cap only ever clips the header/footer chrome),
    // so it is the number that decides whether the surface scrolls at rest.
    const float CardWidth = 368f;
    const float CardMaxHeight = 620f;
    const float ScrollMaxHeight = 500f;
    /// <summary>Width reserved for a trait facet's three-way control, leaving the rest of the row to its label.</summary>
    const float TraitControlWidth = 150f;
    /// <summary>Fired after a sibling's 167ms collapse has settled — see <see cref="Section"/> handling in Render.</summary>
    const float RevealDelayMs = 170f;

    public TrackFilterFlyout(IReadSignal<TrackFilterState> filters, Action<TrackFilterState> setFilters,
        TrackFilterCapabilities caps)
    {
        _filters = filters;
        _setFilters = setFilters;
        _caps = caps;
        var f = filters.Peek();
        _scope = new((int)f.SearchScope);
        _explicit = new((int)f.ExplicitMode);
        _video = new((int)f.VideoMode);
        _liked = new(f.LikedOnly);
        _playable = new(f.PlayableOnly);
        _duration = new((int)f.Duration);
        _added = new((int)f.Added);
        _tempo = new((int)f.Tempo);
        _origin = new((int)f.Origin);
        _sections = [_durationSec, _addedSec, _tempoSec, _originSec];
        foreach (var s in _sections) s.Parts = DisclosurePartsFor(s);
    }

    /// <summary>Index of the single open section, or -1. Drives the reveal timer's dep key.</summary>
    int OpenSectionIndex()
    {
        for (int i = 0; i < _sections.Length; i++) if (_sections[i].Open.Value) return i;
        return -1;
    }

    public override Element Render()
    {
        var current = _filters.Value;
        ColorF accent = Tok.AccentTextPrimary;
        string status = current.ActiveCount == 0
            ? Loc.Get(Strings.Detail.Filter.AllTracks)
            : Strings.Detail.Filter.ActiveCount(current.ActiveCount.ToString());

        // Expanding a section also COLLAPSES its sibling, and that 167ms shrink slides everything below it upward — so
        // arming the scroll at click time would chase a rect that is still moving. Waiting for the collapse to settle
        // makes the header's position final, and the chase then parks it under the card's own header. Unconditional
        // hook, re-armed by the dep key whenever the open section changes (-1 = all closed, fires and does nothing).
        Context.UseTimeout(() =>
        {
            int i = OpenSectionIndex();
            if (i < 0) return;
            // Target the section ROOT's top edge, not its expanded body: the body reflows for 333ms after this, so its
            // extent is not settled, while the top edge is exactly where the header already is.
            ScrollIntoView.BringInto(Context, _scrollNode, _sections[i].Node,
                margin: Spacing.S, alignmentRatio: 0f, animate: true);
        }, RevealDelayMs, DepKey.From(OpenSectionIndex()));

        Element SectionTitle(string text) => WaveeType.Eyebrow(text) with
        {
            Color = Tok.TextTertiary,
            Margin = new Edges4(4f, 0f, 4f, 8f),
        };

        Element Group(string title, params Element[] children) => new BoxEl
        {
            Direction = 1,
            Gap = 8f,
            Padding = new Edges4(12f, 10f, 12f, 10f),
            Children = [SectionTitle(title), .. children],
        };

        // ONE segmented row, not a 2x2 grid of buttons: the four scopes are mutually exclusive short words, which is
        // exactly what Segmented is for, and it costs one 34-DIP line instead of two rows plus their gap.
        Element ScopePicker() => Segmented.Create(
            [
                new SegmentedItem(Loc.Get(Strings.Detail.Filter.Everything)),
                new SegmentedItem(Loc.Get(Strings.Detail.Filter.TitleOnly)),
                new SegmentedItem(Loc.Get(Strings.Detail.Filter.ArtistOnly)),
                new SegmentedItem(Loc.Get(Strings.Detail.Filter.AlbumOnly)),
            ],
            _scope,
            onChange: value => _setFilters(_filters.Peek() with { SearchScope = (TrackSearchScope)value }));

        // Label and control on ONE line. Stacking them read as a heading over a widget and cost ~23 DIP per facet for
        // no added clarity — the icon plus a short label leaves plenty of room beside a three-way control.
        Element TraitFacet(string glyph, string label, Signal<int> signal, Action<int> changed) => new BoxEl
        {
            Direction = 0,
            Gap = 9f,
            MinHeight = 32f,
            AlignItems = FlexAlign.Center,
            Padding = new Edges4(4f, 0f, 0f, 0f),
            Children =
            [
                Icon(glyph, 16f, Tok.TextTertiary),
                new TextEl(label)
                {
                    Size = 13f, Weight = 600, Color = Tok.TextSecondary,
                    Grow = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                },
                new BoxEl
                {
                    Width = TraitControlWidth,
                    Shrink = 0f,
                    Children =
                    [
                        Segmented.Create(
                        [
                            new SegmentedItem(Loc.Get(Strings.Detail.Filter.All)),
                            new SegmentedItem(Loc.Get(Strings.Detail.Filter.Hide)),
                            new SegmentedItem(Loc.Get(Strings.Detail.Filter.Only)),
                        ],
                        signal,
                        onChange: changed),
                    ],
                },
            ],
        };

        Element StatusChoice(string glyph, string label, Signal<bool> signal, Action<bool> changed)
        {
            var checkStyle = CheckBox.DefaultStyle with
            {
                MinWidth = 0f,
                MinHeight = 30f,
                FontSize = 13f,
                ContentGap = 7f,
            };
            return new BoxEl
            {
                Direction = 0,
                Gap = 8f,
                MinHeight = 32f,
                Padding = new Edges4(9f, 1f, 7f, 1f),
                AlignItems = FlexAlign.Center,
                Corners = Radii.ControlAll,
                Fill = Tok.FillSubtleTransparent,
                HoverFill = Tok.FillSubtleSecondary,
                BorderWidth = 1f,
                BorderColor = Tok.StrokeControlDefault,
                BrushTransitionMs = WaveeMotion.Faster,
                Children =
                [
                    Icon(glyph, 15f, Tok.TextTertiary),
                    CheckBox.Create(label, signal, changed, style: checkStyle),
                ],
            };
        }

        Element Disclosure(string glyph, string label, Signal<int> value, Section section,
            IReadOnlyList<string> labels, Action<int> changed)
        {
            var header = new BoxEl
            {
                Direction = 0,
                Gap = 9f,
                AlignItems = FlexAlign.Center,
                Children =
                [
                    Icon(glyph, 16f, Tok.TextTertiary),
                    new TextEl(label) { Size = 13f, Weight = 600, Color = Tok.TextPrimary },
                    new BoxEl
                    {
                        Grow = 1f,
                        AlignItems = FlexAlign.End,
                        Children =
                        [
                            new TextEl(Prop.Of(() => labels[value.Value]))
                            { Size = 12f, Color = Tok.TextTertiary },
                        ],
                    },
                ],
            };
            var choices = RadioButtons.Create(labels, value, changed, maxColumns: 2);
            return Embed.Comp(
                new Expander.ExpanderSlots(header, choices, section.Parts),
                () => new Expander
            {
                IsExpanded = section.Open,
                OnChange = open => OpenOnly(section, open),
            });
        }

        void SetFlag(TrackFilterFlags flag, bool enabled)
        {
            var f = _filters.Peek();
            var flags = enabled ? f.Flags | flag : f.Flags & ~flag;
            _setFilters(f with { Flags = flags });
        }

        void OpenOnly(Section target, bool open)
        {
            if (!open) return;
            foreach (var s in _sections)
                if (!ReferenceEquals(s, target)) s.Open.Value = false;
        }

        void ClearAll()
        {
            _scope.Value = (int)TrackSearchScope.Everything;
            _explicit.Value = (int)TrackTraitMode.All;
            _video.Value = (int)TrackTraitMode.All;
            _liked.Value = false;
            _playable.Value = false;
            _duration.Value = (int)TrackDurationRange.Any;
            _added.Value = (int)TrackAddedRange.Any;
            _tempo.Value = (int)TrackTempoBand.Any;
            _origin.Value = (int)TrackOriginFilter.Any;
            foreach (var s in _sections) s.Open.Value = false;
            _setFilters(TrackFilterState.Default);
        }

        // ── Content: the traits, plus Status folded in. Status was a section of its own with a title and a divider for
        // two checkboxes; it is the same idea as the traits (does this list include X?) and reads fine under one head.
        var contentChildren = new List<Element>(4)
        {
            TraitFacet(Icons.Important, Loc.Get(Strings.Detail.Filter.ExplicitContent), _explicit,
                value => _setFilters(_filters.Peek() with { ExplicitMode = (TrackTraitMode)value })),
            TraitFacet(Icons.Movie, Loc.Get(Strings.Detail.Filter.VideoTracks), _video,
                value => _setFilters(_filters.Peek() with { VideoMode = (TrackTraitMode)value })),
        };

        var statusChoices = new List<Element>(2);
        if (_caps.HasLibrary || current.LikedOnly)
            statusChoices.Add(StatusChoice(Icons.Heart, Loc.Get(Strings.Detail.Filter.LikedOnly), _liked,
                value => SetFlag(TrackFilterFlags.LikedOnly, value)));
        if (_caps.HasUnavailable || current.PlayableOnly)
            statusChoices.Add(StatusChoice(Icons.Accept, Loc.Get(Strings.Detail.Filter.PlayableOnly), _playable,
                value => SetFlag(TrackFilterFlags.PlayableOnly, value)));
        if (statusChoices.Count > 0)
        {
            var columns = statusChoices.Count > 1
                ? new[] { TrackSize.Star(), TrackSize.Star() }
                : new[] { TrackSize.Star() };
            contentChildren.Add(new GridEl
            {
                Columns = columns,
                ColGap = 5f,
                RowGap = 5f,
                Children = statusChoices.ToArray(),
            });
        }

        var more = new List<Element>(4)
        {
            Disclosure(Icons.Clock, Loc.Get(Strings.Detail.Filter.Duration), _duration, _durationSec,
                [
                    Loc.Get(Strings.Detail.Filter.AnyDuration),
                    Loc.Get(Strings.Detail.Filter.UnderThree),
                    Loc.Get(Strings.Detail.Filter.ThreeToFive),
                    Loc.Get(Strings.Detail.Filter.OverFive),
                ],
                value => _setFilters(_filters.Peek() with { Duration = (TrackDurationRange)value })),
        };
        if (_caps.HasDateAdded || current.Added != TrackAddedRange.Any)
            more.Add(Disclosure(Icons.Calendar, Loc.Get(Strings.Detail.Filter.DateAdded), _added, _addedSec,
                [
                    Loc.Get(Strings.Detail.Filter.AnyTime),
                    Loc.Get(Strings.Detail.Filter.LastSevenDays),
                    Loc.Get(Strings.Detail.Filter.LastThirtyDays),
                    Loc.Get(Strings.Detail.Filter.LastSixMonths),
                    Loc.Get(Strings.Detail.Filter.LastYear),
                ],
                // WithAddedRange, not a bare `with`: the rail's sparkline can set an explicit (after, before] window
                // for the SAME question, and picking a preset here has to retire it rather than AND the two.
                value => _setFilters(_filters.Peek().WithAddedRange((TrackAddedRange)value))));
        // Tempo sits with the other "more filters" facets rather than as a chip: it is a refinement you reach for, not
        // a mode you toggle. Key is deliberately NOT exposed as a 24-way picker here — a Camelot code is set from a
        // track's own row (the versions drawer), where you have a reference to match against, which is the only moment
        // "same key as this" is a question anyone asks.
        if (_caps.HasTempo || current.Tempo != TrackTempoBand.Any)
            more.Add(Disclosure(Icons.MusicNote, Loc.Get(Strings.Detail.Filter.Tempo), _tempo, _tempoSec,
                [
                    Loc.Get(Strings.Detail.Filter.AnyTempo),
                    Loc.Get(Strings.Detail.Filter.TempoUnder90),
                    Loc.Get(Strings.Detail.Filter.Tempo90To119),
                    Loc.Get(Strings.Detail.Filter.Tempo120To139),
                    Loc.Get(Strings.Detail.Filter.Tempo140Up),
                ],
                value => _setFilters(_filters.Peek() with { Tempo = (TrackTempoBand)value })));
        if (_caps.HasMixedOrigin || current.Origin != TrackOriginFilter.Any)
            more.Add(Disclosure(Icons.MusicNote, Loc.Get(Strings.Detail.Filter.Source), _origin, _originSec,
                [
                    Loc.Get(Strings.Detail.Filter.AnySource),
                    Loc.Get(Strings.Detail.Filter.Streamed),
                    Loc.Get(Strings.Detail.Filter.Local),
                ],
                value => _setFilters(_filters.Peek() with { Origin = (TrackOriginFilter)value })));

        var bodyChildren = new List<Element>(6)
        {
            Group(Loc.Get(Strings.Detail.Filter.SearchIn), ScopePicker()),
            new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault },
            Group(Loc.Get(Strings.Detail.Filter.Content), contentChildren.ToArray()),
            new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault },
            Group(Loc.Get(Strings.Detail.Filter.MoreFilters), more.ToArray()),
        };

        return new BoxEl
        {
            Direction = 1,
            Width = CardWidth,
            MinWidth = 320f,
            MaxWidth = CardWidth,
            MaxHeight = CardMaxHeight,
            MinHeight = 0f,
            ClipToBounds = true,
            Children =
            [
                new BoxEl
                {
                    Direction = 0,
                    AlignItems = FlexAlign.Center,
                    Gap = 11f,
                    Padding = new Edges4(14f, 12f, 14f, 10f),
                    Children =
                    [
                        new BoxEl
                        {
                            Width = 34f,
                            Height = 34f,
                            AlignItems = FlexAlign.Center,
                            Justify = FlexJustify.Center,
                            Corners = CornerRadius4.All(10f),
                            Fill = accent with { A = 0.14f },
                            Children = [Icon(Icons.Filter, 17f, accent)],
                        },
                        new BoxEl
                        {
                            Direction = 1,
                            Gap = 1f,
                            Grow = 1f,
                            Children =
                            [
                                new TextEl(Loc.Get(Strings.Detail.Filter.Title))
                                { Size = 15f, Weight = 650, Color = Tok.TextPrimary },
                                new TextEl(status) { Size = 12f, Color = Tok.TextSecondary },
                            ],
                        },
                    ],
                },
                new BoxEl { Height = 1f, Margin = new Edges4(8f, 0f, 8f, 0f), Fill = Tok.StrokeDividerDefault },
                new ScrollEl
                {
                    ContentSized = true,
                    Grow = 1f,
                    MinHeight = 0f,
                    MaxHeight = ScrollMaxHeight,
                    OnRealized = h => _scrollNode = h,
                    Content = new BoxEl
                    {
                        Direction = 1,
                        MinWidth = 0f,
                        Children = bodyChildren.ToArray(),
                    },
                },
                new BoxEl { Height = 1f, Margin = new Edges4(8f, 0f, 8f, 0f), Fill = Tok.StrokeDividerDefault },
                new BoxEl
                {
                    Direction = 0,
                    Height = 46f,
                    Padding = new Edges4(14f, 6f, 10f, 6f),
                    AlignItems = FlexAlign.Center,
                    Children =
                    [
                        new TextEl(current.IsDefault
                            ? Loc.Get(Strings.Detail.Filter.NoFiltersApplied)
                            : status)
                        {
                            Size = 11f,
                            Color = Tok.TextTertiary,
                            Grow = 1f,
                            MinWidth = 0f,
                            MaxLines = 1,
                            Trim = TextTrim.CharacterEllipsis,
                        },
                        Button.Standard(
                            Loc.Get(Strings.Detail.Filter.ClearFilters),
                            ClearAll,
                            isEnabled: !current.IsDefault),
                    ],
                },
            ],
        };
    }
}
