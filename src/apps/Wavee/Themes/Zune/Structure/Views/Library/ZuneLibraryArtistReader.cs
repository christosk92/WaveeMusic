// ── Themes/Zune/Structure/Views/Library/ZuneLibraryArtistReader.cs — Zune's `Library.ArtistReader` (themes plan 6.3; P5 Z7) ──────────────────
//
// Board Z-Library-Artists (the column right of the 220 list): the artist's BAND (the avatar, the name in the light voice, the "in your library" line and the verbs as words:
// play all, follow, shuffle, go to the artist), the pinned sub-rail (scope: in your library · all releases · N, and the reader's sort: newest · oldest · a-z, as words), then
// one block per release (cover, "2022 · Album", the title link with its meta and verbs, the songs as the shared eager rows) and a tail, with a cover spine beside the list.
//
// WHAT THE CORE KEEPS. `Artist.Reader` (`[CoreHost]`) decides: the props channel, the shape memo and its block order, the catalogue gate, the demand of every block, the readiness
// of each block's cell, the measured-extent corrections, the scroll spy and the spine follow, the mount key, the layout and options of the mounted list, the two width arms and
// every verb. The block, head and band METRICS the extent estimator reads too (`Artist.ReaderShape`: the avatar, the circles, the paddings, the stated heights) stay the
// core's and this view reads them: renderer == estimator by identity. This view DRAWS: it is handed the mount's layout and options and a host seam
// (`IArtistReaderHost`) its slots read live state through, in THEIR render.
//
// THE PLANES. The sub-rail is a RAW element returned straight into the scroller's content (a sticky root returned from a component never pins); every other slot is a
// component. The band's two arms and the list are keyed so a swap is a remount; the lane's two children are keyed so toggling the spine never pairs the spine against the list
// host. Stable delegates and the cached rail live in the view's `Holder` (a hook ref).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scroll.Effects;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using K = Wavee.Themes.Zune.ZuneLibraryKit;
using LR = Wavee.Themes.Zune.ZuneLibraryRules;

namespace Wavee.Themes.Zune;

/// <summary>Zune's library artists column.</summary>
internal static partial class ZuneLibraryArtistReader
{
    /// <summary>What the view keeps across renders: the current props, the bound slots' templates (frozen at mount, so they read <see cref="Props"/> live) and the cached sub-rail.</summary>
    sealed class Holder
    {
        public LibraryArtistReaderProps Props = null!;
        public Func<RowScope, Element>? ItemAt, DotAt;
        public Element? SubRail;
        public Signal<int>? RailScope, RailSort;
        public bool RailNarrow;
    }

    /// <summary>The registered template of <see cref="ViewKeys.LibraryArtistReader"/>.</summary>
    public static Element Template(LibraryArtistReaderProps props, in ViewContext ctx)
    {
        var h = ctx.Hooks.UseRef(new Holder()).Value;           // before the guard: one hook order on every path
        if (props.Vacant) return new BoxEl();
        // The split tiers draw the board (hero, albums grid, the picked album's songs); the one-column drill-in keeps the block list.
        var tier = ctx.Tier();
        var layout = ZuneLibraryTiers.Of(tier, single: tier.Content == WidthTier.Compact);
        if (!layout.Single) return Board(props, layout);
        h.Props = props;
        // The bound templates run ONCE per slot and are frozen at mount: they read the current props through the holder.
        h.ItemAt ??= scope => scope.Index.Peek() == 1 ? Rail(h) : Embed.Comp(() => new ReaderItem(h.Props.Host, scope));
        h.DotAt ??= scope => Embed.Comp(() => new SpineDot(h.Props.Host, scope));

        Element list = new BoxEl
        {
            Key = "list:" + props.ListKey, OnRealized = props.OnListMounted,       // a TRUTHFUL mount signal: fires only on a real mount
            Grow = 1f, Basis = 0f, MinHeight = 0f, MinWidth = 0f, Direction = 1,
            Children = [ItemsView.CreateBound(props.ItemCount, h.ItemAt, props.Layout, props.Options!)],
        };
        // MEDIUM: under the edge the spine is GONE, not hidden: a collapsed node still renders. The two lane children are KEYED so that toggling the breakpoint never positionally
        // pairs the spine against the list host: an unkeyed pair would hand the list host's node to the spine on the way down and remount the whole list.
        Element[] lane = props.Spine
            ? new Element[] { Spine(props, h), ListHost(list) }
            : new Element[] { ListHost(list) };
        // The reader's outer tree IS the lane: the band and the sub-rail are items 0 and 1 of the list.
        return new BoxEl
        {
            Direction = 0, Grow = 1f, Basis = 0f, MinHeight = 0f, MinWidth = 0f, ClipToBounds = true,
            AlignItems = FlexAlign.Stretch, OnBoundsChanged = props.OnBounds, Children = lane,
        };
    }

    /// <summary>The stable, UNKEYED parent the keyed list mounts inside: what lets <c>list</c>'s own `Key` swap (a real remount) without touching the LANE's structure.</summary>
    static Element ListHost(Element list) => new BoxEl
    {
        Key = "reader:lane",
        ZStack = true, Grow = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f, ClipToBounds = true, Children = [list],
    };

    // ── the band ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The avatar, the name LINK, "In your library: 3 albums · 35 songs" and the verbs. Painted from `ArtistFields.Identity` the moment the navigator's row carries it:
    /// the band never skeletons as a whole; an unnamed artist shows "…" in the title and nothing else.
    /// <para>UNDER <see cref="Artist.ReaderShape.WideBandLaneW"/> (the medium arm) it STACKS: row 1 is the avatar beside the name column, the name taking up to two lines; row 2 is
    /// the SAME verb row as the wide arm, left-aligned.</para>
    /// <para>Each arm's root is KEYED ("band:w" / "band:s") as a CHILD of the slot's root, so the swap is a real remount; and each DECLARES
    /// <see cref="Artist.ReaderShape.BandHeight"/>, the number the extent table seeds and the core corrects to.</para></summary>
    static Element Band(IArtistReaderHost host, in ArtistReaderBand band)
    {
        var a = band.Artist;
        bool named = band.Named;
        string uri = a.IsValid ? a.Uri.Text : "";
        string? name = named ? a.Name : null;
        if (!band.Stacked)
            return new BoxEl
            {
                Key = "band:w", Direction = 0, Gap = Artist.ReaderShape.BandGap, AlignItems = FlexAlign.Center, Shrink = 0f,
                Height = Artist.ReaderShape.BandH, MinWidth = 0f, OnBoundsChanged = host.OnBandBounds,
                Padding = new Edges4(Artist.ReaderShape.BandPadX, Artist.ReaderShape.BandPadTop, Artist.ReaderShape.BandPadX, Artist.ReaderShape.BandPadBottom),
                Children =
                [
                    Avatar(a, named, Artist.ReaderShape.AvatarEdge),
                    NameColumn(host, a, named, band.Shape, stacked: false),
                    Verbs(host, uri, name, stacked: false),
                ],
            };
        return new BoxEl
        {
            Key = "band:s", Direction = 1, Gap = Artist.ReaderShape.BandStackRowGap, Shrink = 0f,
            Height = Artist.ReaderShape.BandStackedH, MinWidth = 0f, OnBoundsChanged = host.OnBandBounds,
            Padding = new Edges4(Artist.ReaderShape.BandStackPadX, Artist.ReaderShape.BandStackPadTop, Artist.ReaderShape.BandStackPadX, Artist.ReaderShape.BandPadBottom),
            Children =
            [
                new BoxEl
                {
                    Direction = 0, Gap = Artist.ReaderShape.BandStackGap, AlignItems = FlexAlign.Center, MinWidth = 0f,
                    Height = Artist.ReaderShape.NameColumnH,
                    Children = [Avatar(a, named, Artist.ReaderShape.AvatarEdgeStacked), NameColumn(host, a, named, band.Shape, stacked: true)],
                },
                Verbs(host, uri, name, stacked: true),
            ],
        };
    }

    /// <summary>The band's ONE verb row: play all (the accent ring and its word), the Follow toggle, then the shuffle and go-to rings named by their tooltips,
    /// <see cref="Artist.ReaderShape.ActionGap"/> apart. WIDE it always fits (the medium edge is derived from it). STACKED it measures its own lane and, where the whole row would
    /// not fit (<see cref="LR.FoldVerbs"/>), folds shuffle and go-to into one "…" menu, so it never clips.</summary>
    static Element Verbs(IArtistReaderHost host, string uri, string? name, bool stacked)
        => stacked
            ? Responsive.Of("reader:verbs", w => VerbRow(host, uri, name, folded: LR.FoldVerbs(w)))
            : VerbRow(host, uri, name, folded: false);

    static Element VerbRow(IArtistReaderHost host, string uri, string? name, bool folded)
    {
        Element play = ZuneDiscoveryKit.Ring("reader-play-all", Icons.Play, Loc.Get(Strings.Library.PlayAll), showLabel: true, primary: true, host.PlayAll);
        Element[] kids = folded
            ?
            [
                play,
                Follow(uri, name),
                Controls.Named(K.MoreButton(() => FoldedMenu(host), Artist.ReaderShape.IconActionW), Loc.Get(Strings.Common.More)),
            ]
            :
            [
                play,
                Follow(uri, name),
                ZuneDiscoveryKit.Ring("reader-shuffle", Icons.Shuffle, Loc.Get(Strings.Detail.Shuffle), showLabel: false, primary: false, host.ShuffleAll),
                ZuneDiscoveryKit.Ring("reader-go-artist", Icons.OpenInNewWindow, Loc.Get(Strings.Detail.GoToArtist), showLabel: false, primary: false, host.GoArtist),
            ];
        return new BoxEl
        {
            Key = folded ? "verbs:folded" : "verbs:row",
            Direction = 0, Gap = Artist.ReaderShape.ActionGap, AlignItems = FlexAlign.Center, Shrink = 0f,
            Height = Artist.ReaderShape.ActionH, Children = kids,
        };
    }

    /// <summary>The folded row's "…": the two verbs it took in, in the row's order.</summary>
    static ContextMenuModel FoldedMenu(IArtistReaderHost host) => new(
    [
        new MenuFlyoutItem(Loc.Get(Strings.Detail.Shuffle), Icons.Shuffle, Invoke: host.ShuffleAll),
        new MenuFlyoutItem(Loc.Get(Strings.Detail.GoToArtist), Icons.OpenInNewWindow, Invoke: host.GoArtist),
    ]);

    static Element Avatar(Artist a, bool named, float edge) => new BoxEl
    {
        Width = edge, Height = edge, Shrink = 0f, Corners = Radii.Circle(edge), ClipToBounds = true,
        Children = [Controls.Artwork(named ? Controls.ArtUrl(a.ImageId) : null, edge, edge, R.Shape.For(ShapeRole.Avatar), decodePx: LR.AvatarDecodePx)],
    };

    /// <summary>The Follow toggle in a `Shrink 0` slot: a `ComponentEl` has no layout knobs of its own, so the one box is what keeps the toggle out of the name column's width. The
    /// slot is as wide as the LONGER face (<see cref="Artist.ReaderShape.FollowSlotW"/>) and the toggle FILLS it, so the rings after it never shift between a followed and an
    /// unfollowed artist. A MinWidth, not a Width: a locale whose label is longer still grows it.</summary>
    static Element Follow(string uri, string? name) => new BoxEl
    {
        Shrink = 0f, MinWidth = Artist.ReaderShape.FollowSlotW, Direction = 1, AlignItems = FlexAlign.Stretch,
        Children = [Embed.Comp(() => new Controls.FollowToggle { Uri = uri, Name = name }) with { Key = "follow:" + uri }],
    };

    /// <summary>The name LINK over the "in your library" subline: the band row's only flexible child in BOTH arms, which is why every control beside it declares
    /// <c>Shrink = 0</c>. STACKED, the name may take two lines, auto-fitting first, ellipsis after that. The column is the stated <see cref="Artist.ReaderShape.NameColumnH"/> and
    /// centres its content, so the band's height never depends on the name. Either way a TRIMMED name gets a tooltip and a whole one does not (<c>Controls.TrimTip</c>).
    /// <para>The subline counts RELEASES (saved + liked-only) and SONGS, both off the shape so a liked-edge landing re-renders the band with them.</para></summary>
    static Element NameColumn(IArtistReaderHost host, Artist a, bool named, Artist.ReaderShapeKey shape, bool stacked)
    {
        string name = named ? a.Name : "…";
        TextEl nameText = R.Type.ArtistCompactTitle(name) with
        {
            Color = Tok.TextPrimary, HoverColor = Tok.TextSecondary, BrushTransitionMs = Design.Motion.Faster,
            Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, LineHeight = Artist.ReaderShape.NameLine,
            // The shape's exact line (Zune's compact title is the 40 voice, whose natural line is taller than the 40 the column states): two lines stand in the
            // stated column and never run into the "in your library" line under them.
            LineStacking = LineStacking.BlockLineHeight,
            Wrap = stacked ? TextWrap.Wrap : TextWrap.NoWrap,
            MaxLines = stacked ? Artist.ReaderShape.NameLines : 1,
        };
        Element link = new BoxEl
        {
            Shrink = 1f, MinWidth = 0f, Cursor = CursorId.Hand, Focusable = true, AllowFocusOnInteraction = false, Role = AutomationRole.Button,
            OnClick = host.GoArtist, Children = [nameText],
        };
        return new BoxEl
        {
            Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = Artist.ReaderShape.NameSubGap,
            Height = stacked ? Artist.ReaderShape.NameColumnH : float.NaN,
            Justify = stacked ? FlexJustify.Center : FlexJustify.Start,
            Children =
            [
                Controls.TrimTip(link, name, nameText),
                R.Type.DenseMeta(named ? LibraryLine(shape) : "") with { Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            ],
        };
    }

    /// <summary>"In your library: 3 albums · 35 songs", or, when there is no RELEASE at all and only loose liked tracks, "In your library: 7 songs"; empty when the artist is in
    /// your library for nothing. No <c>== 1</c> arms: the counts are ICU PLURALS and render "1 album" / "1 song" themselves, in every locale's own plural categories.</summary>
    static string LibraryLine(Artist.ReaderShapeKey shape)
    {
        int albums = shape.Library, songs = shape.Songs;
        if (albums <= 0)
            return songs <= 0 ? "" : Strings.Library.InYourLibrary(Strings.Library.NSongsOnly(songs), "");
        return Strings.Library.InYourLibrary(Strings.Library.NAlbums(albums), songs > 0 ? Strings.Library.NSongs(songs) : "");
    }

    // ── the sub-rail ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>ITEM 1, cached per (scope, sort) pair of signal instances and arm. Built ONCE per arm: every state inside a word is a bind, and the release total rides a
    /// <see cref="Prop{T}"/>, so the facets answering re-fires one text bind rather than re-rendering the rail. The narrow arm is a list mount input, so its flip
    /// re-asks this slot.
    /// <para>It is a plain Element and NOT a component, which is the one structural thing that makes the pin work: a sticky scroll effect with no named scope is a
    /// containing-block-clamped <c>position:sticky</c> against the node's IMMEDIATE parent, and a component anchor is layout-TRANSPARENT.</para></summary>
    static Element Rail(Holder h)
    {
        var p = h.Props;
        if (p.Scope?.Invoke() is not { } scope || p.Sort?.Invoke() is not { } sort) return new BoxEl { Height = Artist.ReaderShape.SubRailH };
        bool narrow = p.Host.NarrowNow();
        if (h.SubRail is not null && ReferenceEquals(h.RailScope, scope) && ReferenceEquals(h.RailSort, sort) && h.RailNarrow == narrow) return h.SubRail;
        h.RailScope = scope;
        h.RailSort = sort;
        h.RailNarrow = narrow;
        return h.SubRail = SubRail(p.Host, scope, sort, narrow);
    }

    /// <summary>The two word groups, scope (in your library · all releases · N), a spacer, sort (newest · oldest · a-z), over the page's own Signal INSTANCES, PINNED at the
    /// viewport top. A plain band: the 1-DIP divider under it is the edge. A pinned direct child of the content paints ABOVE the blocks sliding under it; what stops a track
    /// row showing through it is the list's pinned clip inset (<see cref="Artist.ReaderShape.SubRailH"/>). In the NARROW arm (a Compact drill-in) the sort is its lit word
    /// and a chevron opening the three as a radio menu: the six words of the wide rail ran into each other there.</summary>
    static Element SubRail(IArtistReaderHost host, Signal<int> scope, Signal<int> sort, bool narrow) => new BoxEl
    {
        Direction = 1, Shrink = 0f, Height = Artist.ReaderShape.SubRailH, MinWidth = 0f,
        Children =
        [
            new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = LR.WordGap, Shrink = 0f, MinWidth = 0f,
                Padding = new Edges4(LR.ReaderInset, Artist.ReaderShape.SubRailPadTop, LR.ReaderInset, Artist.ReaderShape.SubRailPadBottom),
                Children =
                [
                    ScopeWords(scope, host.ReleaseTotal, () => host.NarrowNow()), new BoxEl { Grow = 1f, MinWidth = 0f },
                    narrow ? Embed.Comp(sort, static () => new SortMenuWord()) with { Key = "reader:sort" } : SortWords(sort),
                ],
            },
            new BoxEl { Height = Artist.ReaderShape.Divider, MinWidth = 0f, Shrink = 0f, Fill = Tok.StrokeDividerDefault },
        ],
    }.Sticky(0f);

    static readonly FormatCache<int> s_allReleases = new(), s_allShort = new();

    /// <summary>The scope words: "in your library" and "all releases · N" (the narrow arm folds it to "all · N"). Positions ARE the scope codes (0 in your library, 1 all releases).
    /// Every state is a bind, so the facets answering and a column drag across the edge re-fire one text bind and never re-render the rail.</summary>
    static Element ScopeWords(Signal<int> scope, Prop<int> total, Func<bool> narrow)
    {
        Prop<string> all = Prop.Of(() => narrow()
            ? s_allShort.Get(total.Current(), static n => ScopeWordText(Strings.Library.Scope.AllShort, n))
            : s_allReleases.Get(total.Current(), static n => ScopeWordText(Strings.Library.Scope.AllReleases, n)));
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = LR.WordGap, MinWidth = 0f, Shrink = 1f,
            Children = [BoundWord("reader:scope:0", Loc.Bind(Strings.Library.Scope.InLibrary), scope, 0), BoundWord("reader:scope:1", all, scope, 1)],
        };
    }

    /// <summary>"&lt;word&gt; · N", or the bare word while the total is 0: a facet that has not answered says nothing rather than "· 0".</summary>
    static string ScopeWordText(string key, int total)
    {
        string word = Loc.Get(key);
        return total > 0 ? word + " · " + FormatCache.Int(total) : word;
    }

    /// <summary>The reader's own sort words: newest · oldest · a-z (the reader-local <c>Artist.ReaderSort</c> codes, NOT <see cref="LibraryNavSort"/>, and they ARE positions): a
    /// word is a plain set.</summary>
    static Element SortWords(Signal<int> sort)
    {
        var words = new Element[LR.ReaderSortWords];
        for (int i = 0; i < words.Length; i++)
            words[i] = BoundWord("reader:sort:" + FormatCache.Int(i), Loc.Bind(ReaderSortKey(i)), sort, i);
        return new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = LR.WordGap, MinWidth = 0f, Shrink = 0f, Children = words };
    }

    static readonly ContextMenuOptions s_sortMenu = new()
    {
        PointerPlacement = FlyoutPlacement.BottomEdgeAlignedRight,
        KeyboardPlacement = FlyoutPlacement.BottomEdgeAlignedRight,
    };

    /// <summary>The narrow rail's sort: the lit word (a bind) and a chevron, a click opening newest · oldest · a-z as a radio menu that sets the same signal.</summary>
    sealed class SortMenuWord : Component
    {
        Signal<int>? _sort;
        Func<ContextMenuModel?>? _menu;

        public override Element Render()
        {
            var sort = _sort = UseProps<Signal<int>>();
            var overlay = UseContext(Overlay.Service);
            _menu ??= () =>
            {
                var s = _sort!;
                int current = s.Peek();
                var rows = new MenuFlyoutItem[LR.ReaderSortWords];
                for (int i = 0; i < rows.Length; i++)
                {
                    int code = i;
                    rows[i] = MenuFlyoutItem.RadioItem(Loc.Get(ReaderSortKey(code)), current == code, () => s.Value = code);
                }
                return new ContextMenuModel(rows);
            };
            var box = new BoxEl
            {
                Key = "reader:sort:menu", Direction = 0, AlignItems = FlexAlign.Center, Gap = LR.WordChevronGap, Shrink = 0f, Height = LR.HeadH,
                Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, ClickRequestsContext = true,
                Children =
                [
                    R.Type.DenseMeta("") with
                    {
                        Text = Prop.Of(() => Loc.Get(ReaderSortKey(sort.Value))), Color = Tok.TextPrimary, Case = R.Case.For(LabelKind.Command), MaxLines = 1, Wrap = TextWrap.NoWrap,
                    },
                    Ui.Icon(Icons.ChevronDown, LR.ChevronGlyph, Tok.TextTertiary),
                ],
            };
            return Controls.IsNullOverlay(overlay) ? box : ContextSheets.Attach(box, overlay, _menu, s_sortMenu);
        }
    }

    static string ReaderSortKey(int code) => code switch
    {
        1 => Strings.Library.ReaderSort.Oldest,
        2 => Strings.Library.ReaderSort.Alphabetical,
        _ => Strings.Library.ReaderSort.Newest,
    };

    /// <summary>One word over a signal: lit (primary) while the signal holds <paramref name="code"/>, the secondary ink otherwise; a tap sets it. The ink is a bind.</summary>
    static Element BoundWord(string key, Prop<string> label, Signal<int> selected, int code) => new BoxEl
    {
        Key = key, Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f, Height = LR.HeadH,
        Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, OnClick = () => selected.Value = code,
        Children =
        [
            R.Type.DenseMeta("") with
            {
                Text = label, Color = Prop.Of(() => selected.Value == code ? Tok.TextPrimary : Tok.TextSecondary), HoverColor = Tok.TextPrimary,
                BrushTransitionMs = Design.Motion.Fast, Case = R.Case.For(LabelKind.Command), MaxLines = 1, Wrap = TextWrap.NoWrap,
            },
        ],
    };

    // ── the spine ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The cover spine, a SECOND virtualized list that follows the first: one cover per block, the ring and the ink BOUND to the current block so the scroll-spy costs two
    /// compositor writes and no render, and a click glides that block to the top. The spine is the lane's SIBLING, not part of the scroller: it runs the lane's full height beside
    /// the list; the band and the rail live in the list column only. Its <c>Key</c> is CONSTANT: it must not be positionally paired against the list host when the breakpoint
    /// toggles it, and it must not remount per artist: its count rides <c>ListOptions.CountSignal</c> and its dots re-render themselves.</summary>
    static Element Spine(LibraryArtistReaderProps props, Holder h) => new BoxEl
    {
        Key = "reader:spine",
        Width = Artist.ReaderShape.SpineW, Shrink = 0f, Direction = 1, MinHeight = 0f, ClipToBounds = true,
        Padding = new Edges4(Spacing.M, Spacing.M, 0f, Spacing.M),
        Children =
        [
            ItemsView.CreateBound(props.BlockCount, h.DotAt!, RepeatLayout.Stack(LR.DotPitch), new ListOptions
            {
                SelectionMode = ItemsSelectionMode.None, Controller = props.SpineController, Grow = 1f,
                CountSignal = props.SpineCount,
                // No scrollbar and no scroll key: the spine is a READOUT of the reader's position, driven by the core's follow effect, so a conscious bar would invite a second,
                // conflicting intent and a restore key would fight the follow on the first frame. The alpha fade says "there is more above/below".
                Scroll = new ScrollOptions { SuppressScrollBar = true, AutoEdgeFade = true },
            }),
        ],
    };

    /// <summary>One dot. Everything that varies with the SPY is bound (opacity + the accent ring), so a scroll-spy edge is two compositor writes and no render; everything that varies
    /// with the BLOCK (the cover, the tooltip name, the saved dimming) is rebuilt by <see cref="SpineDot"/>'s own render, which happens on a recycle or when the album table
    /// publishes, never per frame.</summary>
    static Element Dot(IArtistReaderHost host, int index, in Artist.ReaderBlock b)
    {
        int i = index;
        var a = new Album(b.AlbumSlot);
        bool saved = b.Saved;
        var current = host.Current;
        string name = a.IsValid && a.Knows(AlbumFields.Title) ? a.Title : "";
        Element dot = new BoxEl
        {
            Key = "dot:" + a.Slot.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Width = LR.DotEdge, Height = LR.DotEdge, Shrink = 0f,
            Corners = R.Shape.All(ShapeRole.Cover), ClipToBounds = true,
            Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true,
            OnClick = () => host.BringToTop(i),
            Opacity = Prop.Of(() => current.Value == i ? LR.DotOpacityCurrent
                                  : saved ? LR.DotOpacitySaved : LR.DotOpacityCatalogue),
            HoverOpacity = LR.DotOpacityHover,
            BorderWidth = LR.DotRing,
            BorderColor = Prop.Of(() => current.Value == i ? Tok.AccentDefault : ColorF.Transparent),
            BrushTransitionMs = Design.Motion.Fast,
            Children = [Controls.Artwork(Controls.ArtUrl(a.ImageId), LR.DotEdge, LR.DotEdge, LR.DotArtRadius, decodePx: LR.DotDecodePx)],
        };
        // No name yet, no tooltip: an empty tooltip is a hover that says nothing.
        return name.Length > 0 ? Controls.Named(dot, name) : dot;
    }

    // ── the tail ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The list's last item, over the core's three answers. Its root declares the height the extent table was already promised and nothing else.
    /// <list type="bullet">
    /// <item><b>2, the VACANCY</b>: scope 0, nothing in your library, and the artist is named: "Nothing from {name} in your library yet" with one action that switches to all releases.</item>
    /// <item><b>1, the LINE</b>: "Fetching releases" while the catalogue gate is still closed, else "Fetching N more releases…" while a facet is Partial (scope 1), or "Everything
    /// {name} has released is already in your library".</item>
    /// <item><b>0, AIR</b>: the bottom gutter.</item>
    /// </list></summary>
    static Element Trailing(in ArtistReaderTail tail)
    {
        Element[] body;
        if (tail.Kind == 2)
        {
            body =
            [
                Controls.Vacancy(Controls.VacancyVoice.Empty, Controls.VacancyScale.Compact,
                    title: Strings.Library.EmptyLibraryTitle(tail.Name), subtitle: "",
                    actionLabel: Loc.Get(Strings.Library.EmptyLibraryAction),
                    onAction: tail.ShowAll),
            ];
        }
        else if (tail.Kind == 1)
        {
            string text = tail.Gated
                ? Loc.Get(Strings.Library.FetchingReleases)             // the catalogue gate, not a partial page
                : tail.Partial && tail.Missing > 0
                    ? Strings.Library.FetchingMore(tail.Missing)
                    : Strings.Library.AllInLibrary(tail.Name);
            body = [R.Type.DenseMeta(text) with { Color = Tok.TextTertiary }];
        }
        else body = Array.Empty<Element>();

        return new BoxEl
        {
            Direction = 1, Shrink = 0f, MinWidth = 0f,
            Height = tail.Height,
            // The vacancy owns its own padding (and centres inside it); the line and the air take the list's gutter.
            Padding = tail.Kind == 2 ? new Edges4() : new Edges4(LR.ReaderInset, Spacing.S, LR.ReaderInset, Spacing.XL),
            Children = body,
        };
    }

    // ── the two slot components ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>One slot of the reader's ONE viewport: the band (0), a block (<c>Prefix + b</c>) or the tail. Slot 1, the pinned sub-rail, never reaches here: the bound template
    /// hands that one back raw. It is a component because a bound slot's template runs ONCE, so whatever has to change inside the slot has to be read by a COMPONENT that lives in
    /// it. Each arm subscribes to exactly what it paints from, through the host's reads; the index signal is read by every arm, which is what makes a recycle a re-render of this
    /// one slot. The root is a plain stretched column, NOT `Grow 1 / Basis 0`: a bound slot's root is mounted straight under the scroller's content node, a COLUMN with
    /// <c>AlignItems.Stretch</c>, so the width comes for free and a zero flex-basis would aim at the height.</summary>
    sealed class ReaderItem(IArtistReaderHost host, RowScope scope) : Component
    {
        public override Element Render()
        {
            int i = scope.Index.Value;
            Element body;
            if (i == 0)
            {
                var band = host.ReadBand();
                body = Band(host, in band);
            }
            else
            {
                var block = host.ReadBlock(i - Artist.ReaderShape.Prefix);
                body = block is not null ? BlockBody(block) : Trailing(host.ReadTail());
            }
            return new BoxEl { Direction = 1, MinWidth = 0f, Children = [body] };
        }
    }

    /// <summary>One slot of the SPINE. The dot itself is rebuilt here rather than cached per album slot on purpose: a cached Element cannot observe its cover landing, and the two
    /// things that DO change per frame (the ring and the ink) are binds inside it that never rebuild anything. So the render runs on a recycle and on an album publish that touches
    /// THIS dot's own album, never on every other album's publish.</summary>
    sealed class SpineDot(IArtistReaderHost host, RowScope scope) : Component
    {
        readonly Func<(int Index, int Slot, uint Ver)> _watch = () => host.WatchDot(scope);

        public override Element Render()
        {
            int i = UseComputed(_watch).Value.Index;
            // The pitch is the ITEM's, not the layout's: `RepeatLayout.Stack` carries no gap, so the band under the cover is this box's own bottom.
            return new BoxEl
            {
                Direction = 1, MinWidth = 0f, Height = LR.DotPitch,
                Children = host.TryBlockAt(i, out var b) ? new Element[] { Dot(host, i, in b) } : Array.Empty<Element>(),
            };
        }
    }
}
