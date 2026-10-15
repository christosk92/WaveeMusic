// ── Themes/Classic/Structure/Views/Pages/ClassicLibraryArtistReader.cs — Classic's `Library.ArtistReader` (themes plan 6.1, T10c1) ──────────────────────
//
// From `Entities\Artist.Reader.cs` (the UI half): the lane (the keyed list host with the cover spine beside it), the artist band in both arms, the name column, the
// pinned sub-rail (scope and sort word rails), the spine and its dots, the two slot components of the one bound scroller (`ReaderItem`: the band, a block or the
// tail; `SpineDot`) and the tail. The block faces and their counted skeletons are `ClassicLibraryArtistReader.Blocks.cs`; the numbers are
// `Rules\ClassicLibraryReaderRules.cs` and the core's `Artist.ReaderShape` (the metrics the extent estimator reads too).
//
// WHAT THE CORE KEEPS. `Artist.Reader` (`[CoreHost]`) decides: the props channel, the shape memo and its block order, the catalogue gate, the demand of every block, the
// readiness of each block's cell, the measured-extent corrections, the scroll spy and the spine follow, the mount key, the layout and options of the mounted list, the two
// width arms (`ReaderShape.Narrow` / `Medium`: no raw width compares here, WVTH013) and every verb. This view DRAWS: it is handed the mount's layout and options and a host
// seam (`IArtistReaderHost`) its slots read live state through, in THEIR render, as the old nested components read the owner.
//
// THE PLANES ARE THE OLD TREE. The sub-rail is a RAW element returned straight into the scroller's content (a sticky root returned from a component never pins); every other
// slot is a component. The band's two arms and the list are keyed so a swap is a remount; the lane's two children are keyed so toggling the spine never pairs the spine against
// the list host. Stable delegates and the cached rail live in the view's `Holder` (a hook ref), the one place the old component kept them as fields.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scroll.Effects;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's library artists column.</summary>
internal static partial class ClassicLibraryArtistReader
{
    /// <summary>What the view keeps across renders: the current props, the bound slots' templates (frozen at mount, so they read <see cref="Props"/> live) and the cached sub-rail.</summary>
    sealed class Holder
    {
        public LibraryArtistReaderProps Props = null!;
        public Func<RowScope, Element>? ItemAt, DotAt;
        public Element? SubRail;
        public Signal<int>? RailScope, RailSort;
    }

    /// <summary>The registered template of <see cref="ViewKeys.LibraryArtistReader"/>.</summary>
    public static Element Template(LibraryArtistReaderProps props, in ViewContext ctx)
    {
        var h = ctx.Hooks.UseRef(new Holder()).Value;           // before the guard: one hook order on every path
        if (props.Vacant) return new BoxEl();
        h.Props = props;
        // The bound templates run ONCE per slot and are frozen at mount: they read the current props through the holder.
        h.ItemAt ??= scope => scope.Index.Peek() == 1 ? Rail(h) : Embed.Comp(() => new ReaderItem(h.Props.Host, scope));
        h.DotAt ??= scope => Embed.Comp(() => new SpineDot(h.Props.Host, scope));

        Element list = new BoxEl
        {
            Key = "list:" + props.ListKey, OnRealized = props.OnListMounted,       // R4: a TRUTHFUL mount signal — fires only on a real mount
            Grow = 1f, Basis = 0f, MinHeight = 0f, MinWidth = 0f, Direction = 1,
            Children = [ItemsView.CreateBound(props.ItemCount, h.ItemAt, props.Layout, props.Options!)],
        };
        // MEDIUM: under the edge the spine is GONE, not hidden — a collapsed node still renders. The two lane children are KEYED so that toggling the breakpoint never
        // positionally pairs the spine against the list host: an unkeyed pair would hand the list host's node to the spine on the way down and remount the whole list (a lost
        // offset and a cold realize) for a 24-DIP drag.
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

    /// <summary>The stable, UNKEYED parent the keyed list mounts inside. The mount is a hard cut — no `Animate`, so there is no overlap to hold open — but the wrapper stays: it is what
    /// lets <c>list</c>'s own `Key` swap (a real `ReconcileChildren` remount, old node out and new one in within the same flush) without touching the LANE's own structure.</summary>
    static Element ListHost(Element list) => new BoxEl
    {
        Key = "reader:lane",
        ZStack = true, Grow = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f, ClipToBounds = true, Children = [list],
    };

    // ── the band ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>72 avatar · the name LINK · "In your library: 3 albums · 35 songs" · the verbs. Painted from `ArtistFields.Identity` the moment the navigator's row carries it — the band never
    /// skeletons as a whole; an unnamed artist shows "…" in the title and nothing else (§5.7, W3).
    ///
    /// <para>UNDER <see cref="Artist.ReaderShape.MediumBelow"/> it STACKS (#158): row 1 is a 56 avatar beside the name column, the name taking up to two lines; row 2 is the SAME verb row as
    /// the wide arm, left-aligned. The reason is arithmetic rather than taste: one row spends <see cref="Artist.ReaderShape.WideBandFixedW"/> (460 DIP, with the 56-DIP spine beside it) on what
    /// never shrinks, and the name column — the only flexible child — is what pays.</para>
    ///
    /// <para>Each arm's root is KEYED ("band:w" / "band:s") as a CHILD of the slot's root — a single-child slot's own Key is inert — so the swap is a real remount; and each DECLARES
    /// <see cref="Artist.ReaderShape.BandHeight"/>, the number the extent table seeds and the core corrects to. With stated heights inside, the declaration restates the measurement rather
    /// than overriding it, and the core's chrome check says so if it ever does not.</para></summary>
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

    /// <summary>The band's ONE verb row (Y3): the accent Play all, the Follow toggle, then the quiet 32/r4 shuffle and ↗ icon buttons named by their tooltips,
    /// <see cref="Artist.ReaderShape.ActionGap"/> apart. WIDE it always fits (the medium edge is derived from it). STACKED it measures its own lane and, where the whole
    /// row would not fit (<see cref="ClassicLibraryTiers.FoldVerbs"/>), folds shuffle and ↗ into one ⋯ menu and lets Play all drop its 120 floor — so it never clips.</summary>
    static Element Verbs(IArtistReaderHost host, string uri, string? name, bool stacked)
        => stacked
            ? Responsive.Of("reader:verbs", w => VerbRow(host, uri, name, folded: ClassicLibraryTiers.FoldVerbs(w)))
            : VerbRow(host, uri, name, folded: false);

    static Element VerbRow(IArtistReaderHost host, string uri, string? name, bool folded)
    {
        var play = Controls.PlayButton(Tok.AccentDefault, host.PlayAll, Loc.Get(Strings.Library.PlayAll)) with { Shrink = 0f };
        Element[] kids = folded
            ?
            [
                play with { MinWidth = 0f },
                Follow(uri, name),
                Controls.Named(ClassicDetailKit.MoreButton(() => FoldedMenu(host), Artist.ReaderShape.IconActionW),
                               Loc.Get(Strings.Common.More)),
            ]
            :
            [
                play,
                Follow(uri, name),
                ClassicDetailKit.CommandCircle(Icons.Shuffle, Loc.Get(Strings.Detail.Shuffle), host.ShuffleAll),
                ClassicDetailKit.CommandCircle(Icons.OpenInNewWindow, Loc.Get(Strings.Detail.GoToArtist), host.GoArtist),
            ];
        return new BoxEl
        {
            Key = folded ? "verbs:folded" : "verbs:row",
            Direction = 0, Gap = Artist.ReaderShape.ActionGap, AlignItems = FlexAlign.Center, Shrink = 0f,
            Height = Artist.ReaderShape.ActionH, Children = kids,
        };
    }

    /// <summary>The folded row's ⋯: the two verbs it took in, in the row's order.</summary>
    static ContextMenuModel FoldedMenu(IArtistReaderHost host) => new(
    [
        new MenuFlyoutItem(Loc.Get(Strings.Detail.Shuffle), Icons.Shuffle, Invoke: host.ShuffleAll),
        new MenuFlyoutItem(Loc.Get(Strings.Detail.GoToArtist), Icons.OpenInNewWindow, Invoke: host.GoArtist),
    ]);

    static Element Avatar(Artist a, bool named, float edge) => new BoxEl
    {
        Width = edge, Height = edge, Shrink = 0f, Corners = Radii.Circle(edge),
        ClipToBounds = true, Shadow = Elevation.Card,
        Children = [Controls.Artwork(named ? Controls.ArtUrl(a.ImageId) : null, edge, edge, edge / 2f, decodePx: 144)],
    };

    /// <summary>The Follow toggle in a `Shrink 0` slot: a `ComponentEl` has no layout knobs of its own (`Shrink` is a `BoxEl` field), so the one box is what keeps the toggle out of the name
    /// column's width.
    /// <para>The slot is as wide as the LONGER face (<see cref="Artist.ReaderShape.FollowSlotW"/>) and the toggle FILLS it (a column that stretches its child), so shuffle and ↗ after it never
    /// shift ~9 DIP between a followed and an unfollowed artist on every navigator click, and the gap after it stays the row's one gap. A MinWidth, not a Width: a locale whose label is longer
    /// still grows it.</para></summary>
    static Element Follow(string uri, string? name) => new BoxEl
    {
        Shrink = 0f, MinWidth = Artist.ReaderShape.FollowSlotW, Direction = 1, AlignItems = FlexAlign.Stretch,
        Children = [Embed.Comp(() => new Controls.FollowToggle { Uri = uri, Name = name }) with { Key = "follow:" + uri }],
    };

    /// <summary>The name LINK over the "in your library" subline — the band row's only flexible child (`Grow 1 / Basis 0 / MinWidth 0` in BOTH arms; the avatar is `Shrink 0`), which is why every
    /// control beside it declares <c>Shrink = 0</c>.
    /// <para>STACKED, the name may take two 40-DIP lines, auto-fitting 32 → 28 first (<c>ArtistCompactTitle</c> carries <c>MinSize = 28</c>, and Wrap + MaxLines make it live — the artist hero's
    /// compact tier is the precedent), ellipsis after that. The column is the stated <see cref="Artist.ReaderShape.NameColumnH"/> and centres its content, so a one-line name sits level with the
    /// avatar and a two-line one fills the box: the band's height never depends on the name. WIDE, it is the one line it always was.</para>
    /// <para>Either way a TRIMMED name gets a tooltip and a whole one does not (<c>Controls.TrimTip</c>). The tooltip wrapper is a COLUMN child here, cross-stretched to the column's width,
    /// because a ToolTip wrapper is <c>Shrink 0</c> and can never shrink as a ROW child (the engine's rule 11); the link inside it shrinks.</para>
    /// <para>The subline counts RELEASES (saved + liked-only, <c>shape.Library</c>) and SONGS (<c>shape.Songs</c>), both off the shape so a liked-edge landing re-renders the band with them.
    /// An artist you only have loose tracks of reads "In your library: 7 songs" rather than the old "0 albums", and one with neither says nothing at all.</para></summary>
    static Element NameColumn(IArtistReaderHost host, Artist a, bool named, Artist.ReaderShapeKey shape, bool stacked)
    {
        string name = named ? a.Name : "…";
        TextEl nameText = R.Type.ArtistCompactTitle(name) with
        {
            Color = Tok.TextPrimary, HoverColor = Tok.AccentTextPrimary, BrushTransitionMs = Design.Motion.Faster,
            Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, LineHeight = Artist.ReaderShape.NameLine,
            Wrap = stacked ? TextWrap.Wrap : TextWrap.NoWrap,
            MaxLines = stacked ? Artist.ReaderShape.NameLines : 1,
        };
        // The ±8 padding and the −8 margins cancel, so the TEXT is exactly the column's width while the hover plate overhangs it by 8 on each side.
        Element link = new BoxEl
        {
            Corners = Radii.ControlAll, Shrink = 1f, MinWidth = 0f,
            Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f),
            Margin = new Edges4(-Spacing.S, 0f, -Spacing.S, 0f),
            Cursor = CursorId.Hand, Focusable = true, Role = AutomationRole.Button, OnClick = host.GoArtist,
            Children = [nameText],
        }.Interactive(Interaction.Subtle);
        return new BoxEl
        {
            Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = Artist.ReaderShape.NameSubGap,
            Height = stacked ? Artist.ReaderShape.NameColumnH : float.NaN,
            Justify = stacked ? FlexJustify.Center : FlexJustify.Start,
            Children =
            [
                Controls.TrimTip(link, name, nameText),
                R.Type.DenseMeta(named ? LibraryLine(shape) : "") with
                {
                    Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                },
            ],
        };
    }

    /// <summary>"In your library: 3 albums · 35 songs" — or, when there is no RELEASE at all and only loose liked tracks, "In your library: 7 songs"; empty when the artist is in your library
    /// for nothing.
    /// <para>No <c>== 1</c> arms: <c>nAlbums</c>/<c>nSongs</c>/<c>nSongsOnly</c> are ICU PLURALS and render "1 album" / "1 song" themselves — in every locale's own plural categories, which a
    /// hand-written English two-way branch cannot do (Polish has three, Arabic six).</para></summary>
    static string LibraryLine(Artist.ReaderShapeKey shape)
    {
        int albums = shape.Library, songs = shape.Songs;
        if (albums <= 0)
            return songs <= 0 ? "" : Strings.Library.InYourLibrary(Strings.Library.NSongsOnly(songs), "");
        return Strings.Library.InYourLibrary(Strings.Library.NAlbums(albums),
                                             songs > 0 ? Strings.Library.NSongs(songs) : "");
    }

    // ── the sub-rail ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>ITEM 1, cached per (scope, sort) pair of signal instances. Built ONCE: every state inside a rail word is a bind, and the release total rides a <see cref="Prop{T}"/>, so the facets
    /// answering re-fires one text bind rather than re-rendering the rail.
    /// <para>It is a plain Element and NOT a component, which is the one structural thing that makes the pin work. A sticky scroll effect with no named scope is a containing-block-clamped
    /// <c>position:sticky</c> against the node's IMMEDIATE parent. A component anchor is layout-TRANSPARENT and mirrors its child's size, so a scope-less sticky root returned from a component sits in
    /// a parent of its own height and never pins. Returned raw, the sub-rail IS the bound slot's root and its parent is the scroller's CONTENT node, whose height is the whole list.</para></summary>
    static Element Rail(Holder h)
    {
        var p = h.Props;
        if (p.Scope?.Invoke() is not { } scope || p.Sort?.Invoke() is not { } sort) return new BoxEl { Height = Artist.ReaderShape.SubRailH };
        if (h.SubRail is not null && ReferenceEquals(h.RailScope, scope) && ReferenceEquals(h.RailSort, sort)) return h.SubRail;
        h.RailScope = scope;
        h.RailSort = sort;
        return h.SubRail = SubRail(p.Host, scope, sort);
    }

    /// <summary>The two word rails — scope (in your library · all releases · N) — spacer — sort (newest · oldest · a–z), over the page's own Signal INSTANCES, PINNED at the viewport top. A PLAIN
    /// section on the content card (Y1): no band fill, the 1-DIP divider under it is the edge. A pinned direct child of the content paints ABOVE the blocks sliding under it
    /// (<c>NodeFlags.StickyPinned</c>); what stops a track row showing through it is the list's pinned clip inset (<see cref="Artist.ReaderShape.SubRailH"/>).</summary>
    static Element SubRail(IArtistReaderHost host, Signal<int> scope, Signal<int> sort) => new BoxEl
    {
        Direction = 1, Shrink = 0f, Height = Artist.ReaderShape.SubRailH, MinWidth = 0f,
        Children =
        [
            new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f, MinWidth = 0f,
                Padding = new Edges4(Spacing.XL, Artist.ReaderShape.SubRailPadTop, Spacing.XL, Artist.ReaderShape.SubRailPadBottom),
                Children =
                [
                    ClassicLibraryRails.ScopeRail(scope, host.ReleaseTotal, () => host.NarrowNow(), fill: true),   // the narrow arm folds "all · N"
                    ClassicLibraryRails.ReaderSortRail(sort),
                ],
            },
            new BoxEl { Height = Artist.ReaderShape.Divider, MinWidth = 0f, Shrink = 0f, Fill = Tok.StrokeDividerDefault },
        ],
    }.Sticky(0f);

    // ── the spine ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The cover spine (W3), a SECOND virtualized list that follows the first: one 36-px cover per block, the ring and the ink BOUND to the current block so the scroll-spy costs two compositor
    /// writes and no render, and a click glides that block to the top.
    ///
    /// <para>Why a list and not an eager column: the column built one Element per block on every reader re-render — 200 covers for a discography, all of them mounted, all of them decoding art, for the
    /// twelve you can see. As a list it realizes what fits plus an overscan, and the core's follow effect glides it so the current dot is always among them.</para>
    ///
    /// <para>The spine is the lane's SIBLING, not part of the scroller, so it simply runs the lane's full height beside the list: the band and the rail live in the list column only. Keeping its own
    /// padding is what lines the first dot up with the first block. Its <c>Key</c> is CONSTANT: the spine must not be positionally paired against the list host when the breakpoint toggles it, and it
    /// must not remount per artist — its count rides <c>ListOptions.CountSignal</c> and its dots re-render themselves.</para></summary>
    static Element Spine(LibraryArtistReaderProps props, Holder h) => new BoxEl
    {
        Key = "reader:spine",
        Width = Artist.ReaderShape.SpineW, Shrink = 0f, Direction = 1, MinHeight = 0f, ClipToBounds = true,
        Padding = new Edges4(Spacing.M, Spacing.M, 0f, Spacing.M),
        Children =
        [
            ItemsView.CreateBound(props.BlockCount, h.DotAt!, RepeatLayout.Stack(ClassicLibraryReaderRules.DotPitch), new ListOptions
            {
                SelectionMode = ItemsSelectionMode.None, Controller = props.SpineController, Grow = 1f,
                CountSignal = props.SpineCount,
                // No scrollbar and no scroll key: the spine is a READOUT of the reader's position, driven by the core's follow effect, so a conscious bar would invite a second, conflicting intent and a
                // restore key would fight the follow on the first frame. The alpha fade says "there is more above/below".
                Scroll = new ScrollOptions { SuppressScrollBar = true, AutoEdgeFade = true },
            }),
        ],
    };

    /// <summary>One dot. Everything that varies with the SPY is bound (opacity + the accent ring), so a scroll-spy edge is two compositor writes and no render at all; everything that varies with the
    /// BLOCK (the cover, the tooltip name, the saved dimming) is rebuilt by <see cref="SpineDot"/>'s own render, which happens on a recycle or when the album table publishes — never per frame.</summary>
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
            Width = ClassicLibraryReaderRules.DotEdge, Height = ClassicLibraryReaderRules.DotEdge, Shrink = 0f,
            Corners = CornerRadius4.All(ClassicLibraryReaderRules.DotRadius), ClipToBounds = true,
            Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true,
            OnClick = () => host.BringToTop(i),
            Opacity = Prop.Of(() => current.Value == i ? ClassicLibraryReaderRules.DotOpacityCurrent
                                  : saved ? ClassicLibraryReaderRules.DotOpacitySaved : ClassicLibraryReaderRules.DotOpacityCatalogue),
            HoverOpacity = ClassicLibraryReaderRules.DotOpacityHover,
            BorderWidth = ClassicLibraryReaderRules.DotRing,
            BorderColor = Prop.Of(() => current.Value == i ? Tok.AccentDefault : ColorF.Transparent),
            BrushTransitionMs = Design.Motion.Fast,
            Children = [Controls.Artwork(Controls.ArtUrl(a.ImageId), ClassicLibraryReaderRules.DotEdge, ClassicLibraryReaderRules.DotEdge,
                                         ClassicLibraryReaderRules.DotArtRadius, decodePx: 72)],
        };
        // No name yet, no tooltip: an empty tooltip is a hover that says nothing.
        return name.Length > 0 ? Controls.Named(dot, name) : dot;
    }

    // ── the tail ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The list's last item, over the core's three answers. Its root declares the height the extent table was already promised and nothing else: on the BOUND path an item root is stretched to the
    /// slot's cross axis by the content column, so a `Grow 1 / Basis 0` would now aim at the HEIGHT and collapse the item to nothing.
    /// <list type="bullet">
    /// <item><b>2, the VACANCY</b> — scope 0, nothing in your library, and the artist is named: "Nothing from {name} in your library yet" with one action that switches to all releases.</item>
    /// <item><b>1, the LINE</b> — "Fetching releases" while the catalogue gate is still closed, else "Fetching N more releases…" while a facet is Partial (scope 1), or "Everything {name} has released is
    /// already in your library".</item>
    /// <item><b>0, AIR</b> — the bottom gutter.</item>
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
                ? Loc.Get(Strings.Library.FetchingReleases)             // R3: the catalogue gate, not a partial page
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
            Padding = tail.Kind == 2 ? new Edges4() : new Edges4(Spacing.XL, Spacing.S, Spacing.XL, Spacing.XL),
            Children = body,
        };
    }

    // ── the two slot components ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>One slot of the reader's ONE viewport: the band (0), a block (<c>Prefix + b</c>) or the tail. Slot 1, the pinned sub-rail, never reaches here — the bound template hands that one back raw.
    ///
    /// <para>It is a component because a bound slot's template runs ONCE, so whatever has to change inside the slot has to be read by a COMPONENT that lives in it. Each arm subscribes to exactly what it
    /// paints from, through the host's reads — the band to the artist table, the shape memo and the MEDIUM arm; a block to the shape only (its region owns its own readiness signal, and the cell it comes
    /// from is refreshed by the core's settle effect); the tail to the shape, because its three states are decided from the counts. The index signal is read by every arm, which is what makes a recycle
    /// a re-render of this one slot.</para>
    ///
    /// <para>The root is a plain stretched column, NOT `Grow 1 / Basis 0`: a bound slot's root is mounted straight under the scroller's content node (through one layout-transparent component anchor),
    /// and that is a COLUMN with <c>AlignItems.Stretch</c> — the width comes for free and a zero flex-basis would aim at the height. The keyed region/tail/band bodies hang under it as CHILDREN so the
    /// keyed child diff can remount them.</para></summary>
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

    /// <summary>One slot of the SPINE. The dot itself is rebuilt here rather than cached per album slot on purpose: a cached Element cannot observe its cover landing, and the two things that DO change
    /// per frame — the ring and the ink — are binds inside it that never rebuild anything. So the render runs on a recycle (the index signal) and on an album publish that touches THIS dot's own
    /// album (a cover, a title) — never on every other album's publish, and never on a reshape that leaves this index's album untouched (the per-dot memo over the host's triple).</summary>
    sealed class SpineDot(IArtistReaderHost host, RowScope scope) : Component
    {
        readonly Func<(int Index, int Slot, uint Ver)> _watch = () => host.WatchDot(scope);

        public override Element Render()
        {
            int i = UseComputed(_watch).Value.Index;
            // The pitch is the ITEM's, not the layout's: `RepeatLayout.Stack` carries no gap, so the 8 DIP under the cover is this box's own bottom band.
            return new BoxEl
            {
                Direction = 1, MinWidth = 0f, Height = ClassicLibraryReaderRules.DotPitch,
                Children = host.TryBlockAt(i, out var b) ? new Element[] { Dot(host, i, in b) } : Array.Empty<Element>(),
            };
        }
    }
}
