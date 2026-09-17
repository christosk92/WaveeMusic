// ── Entities/User.UI.cs ────────────────────────────────────────────────────────────────────────────────────────────
// the library's row shapes and small controls: the sort/view pill + its flyout panel, the crumb bar, the column grip,
// the BOUND navigator/discography rows and cards (and their grid selection chrome), the search-row shapes, "Go to
// artist", and the library mutation seam (`LibrarySeam`) the shared save/follow affordances read
//
// Role: UI
// Owner: O
// Wave: 5
// Budget: 600 lines (+30 % = 780)
// Spec: ch 15 §1-§6, §9 (0.2.9 LibrarySortView.cs, LibraryPage.cs row/card/grip/search-row builders); contract §2.6
//
// ZERO ALLOCATION ON A SCROLL FRAME. Every navigator/discography row is an `ItemsView.CreateBound` slot: the template
// runs ONCE per slot and every per-item value is a bind over `BoundItemScope<LibraryNavItem>` (a slot + its row
// version), so a recycle rewrites the slot's item signal and re-fires only those binds — no `List<Element>` per row,
// no remount. Handlers resolve the CURRENT item at invocation (`Peek`), never a mount-time capture.
//
// PROPS FREEZE AT MOUNT: the pill and its panel receive the four Signal INSTANCES plus two per-kind constants, which is
// the only shape that is safe to freeze (ch 15 §1.2 "four places it bites", item 1).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>One navigator / discography slot: the row's kind and slot plus its row VERSION, so a fact landing on the row
/// changes the item (and re-fires the slot's binds) even though the slot did not move.</summary>
public readonly record struct LibraryNavItem(EntityKind Kind, int Slot, uint Version)
{
    public static LibraryNavItem Of(EntityKind kind, int slot)
    {
        Table? table = Entities.TableFor(kind);
        return new(kind, slot, table is null || slot <= Table.None || slot >= table.Count ? 0u : table.Version[slot]);
    }
}

public readonly partial struct User
{
    // ══ 1. THE PERSISTED CODES (shared with the sidebar's Library V3 — never renumber) ═══════════════════════════════

    /// <summary>Sort code → its loc KEY. Codes 0..4 are persisted (<c>library.&lt;kind&gt;.sort</c>); unknown → Recents.</summary>
    public static string SortLabelKey(int code) => code switch
    {
        1 => Strings.Library.Sort.RecentlyAdded,
        2 => Strings.Library.Sort.Alphabetical,
        3 => Strings.Library.Sort.Creator,
        4 => Strings.Library.Sort.ReleaseDate,
        _ => Strings.Library.Sort.Recents,
    };

    public static string SortLabel(int code) => Loc.Get(SortLabelKey(code));

    /// <summary>View code (0 CompactList · 1 List · 2 CompactGrid · 3 Grid) → the pill's trailing glyph.</summary>
    public static string ViewGlyph(int view) => view >= 2 ? Icons.ViewGrid : Icons.ViewList;

    public static bool IsGridView(int view) => view >= 2;
    public static bool IsCompactView(int view) => view is 0 or 2;

    // ══ 2. THE SORT / VIEW PILL AND ITS PANEL (W6) ═══════════════════════════════════════════════════════════════════

    /// <summary>The pill: sort glyph · active label · direction chevron · divider · view glyph; a tap toggles the flyout.</summary>
    public static Element SortViewPill(Signal<int> sort, Signal<bool> desc, Signal<int> view, Signal<int> size,
                                       bool hasCreator, bool hasRelease)
        => Embed.Comp(() => new SortPillHost(sort, desc, view, size, hasCreator, hasRelease));

    /// <summary>The flyout body — its own component so the rows track the live signals.</summary>
    public static Element SortPanel(Signal<int> sort, Signal<bool> desc, Signal<int> view, Signal<int> size,
                                    bool hasCreator, bool hasRelease)
        => Embed.Comp(() => new SortPanelHost(sort, desc, view, size, hasCreator, hasRelease));

    sealed class SortPillHost(Signal<int> sort, Signal<bool> desc, Signal<int> view, Signal<int> size, bool hasCreator, bool hasRelease)
        : Component
    {
        NodeHandle _anchor;
        OverlayHandle? _handle;

        public override Element Render()
        {
            var overlay = UseContext(Overlay.Service);
            int s = sort.Value; bool d = desc.Value; int v = view.Value;   // subscribe → the pill reflects the state

            void Toggle()
            {
                if (Controls.IsNullOverlay(overlay)) return;
                if (_handle is { IsOpen: true } open) { open.Close(); return; }
                _handle = overlay.Open(
                    () => _anchor,
                    () => SortPanel(sort, desc, view, size, hasCreator, hasRelease),
                    FlyoutPlacement.BottomEdgeAlignedLeft,
                    new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup)
                    { ConstrainToRootBounds = false });
                _handle.ClosedAction = () => _handle = null;
            }

            return new BoxEl
            {
                Direction = 0, Height = 32f, AlignItems = FlexAlign.Center, Gap = 5f, Shrink = 0f,
                Padding = new Edges4(10f, 0f, 8f, 0f), Corners = Radii.ControlAll,
                Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
                OnRealized = h => _anchor = h, OnClick = Toggle,
                Children =
                [
                    Icon(Icons.Sort, 14f, Tok.TextSecondary),
                    new TextEl(SortLabel(s)) { Size = 14f, LineHeight = 20f, Weight = 600, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    Icon(d ? Icons.ChevronDown : Icons.ChevronUp, 12f, Tok.TextTertiary),
                    new BoxEl { Width = 1f, Height = 16f, Fill = Tok.StrokeDividerDefault },
                    Icon(ViewGlyph(v), 14f, Tok.TextSecondary),
                ],
            }.Interactive(Interaction.Subtle);
        }
    }

    sealed class SortPanelHost(Signal<int> sort, Signal<bool> desc, Signal<int> view, Signal<int> size, bool hasCreator, bool hasRelease)
        : Component
    {
        static readonly string[] SizeLabels = ["S", "M", "L"];
        static readonly float[] ViewGlyphSizes = [14f, 16f, 12f, 15f];

        public override Element Render()
        {
            int s = sort.Value; bool d = desc.Value; int v = view.Value; _ = size.Value;   // subscribe
            var rows = new List<Element>(12) { Header(Loc.Get(Strings.Library.SortBy)), SortRow(0, s, d), SortRow(1, s, d), SortRow(2, s, d) };
            if (hasCreator) rows.Add(SortRow(3, s, d));
            if (hasRelease) rows.Add(SortRow(4, s, d));
            rows.Add(new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault, Margin = Edges4.All(4f) });
            rows.Add(Header(Loc.Get(Strings.Library.ViewAs)));
            rows.Add(ViewToggles(v));
            if (v >= 2)
            {
                rows.Add(Header(Loc.Get(Strings.Library.Size)));
                rows.Add(SelectorBar.Create(SizeLabels, size));
            }
            // PopupChrome.Popup supplies the one WinUI FlyoutPresenter acrylic, stroke, corners and shadow.
            return new BoxEl { Direction = 1, Gap = 1f, MinWidth = 230f, Padding = Edges4.All(Spacing.XS), Children = rows.ToArray() };
        }

        // A different key → sort = key, desc = false; the active key → flip desc. The flyout stays open.
        Element SortRow(int key, int active, bool d)
        {
            bool on = active == key;
            return new BoxEl
            {
                Direction = 0, Height = 32f, AlignItems = FlexAlign.Center, Gap = Spacing.S,
                Padding = new Edges4(10f, 0f, 8f, 0f), Corners = CornerRadius4.All(5f),
                Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
                OnClick = () => { if (sort.Peek() == key) desc.Value = !desc.Peek(); else { sort.Value = key; desc.Value = false; } },
                Children =
                [
                    new TextEl(SortLabel(key)) { Size = 14f, Weight = (ushort)(on ? 600 : 400), Color = on ? Tok.AccentTextPrimary : Tok.TextPrimary, Grow = 1f, Basis = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    on ? Icon(d ? Icons.ChevronDown : Icons.ChevronUp, 11f, Tok.AccentTextPrimary) : new BoxEl(),
                    on ? Icon(Icons.Check, 12f, Tok.AccentTextPrimary) : new BoxEl { Width = 12f },
                ],
            }.Interactive(Interaction.Subtle);
        }

        // Glyph-only cells (ch 15 §3: the four view labels are resolved by 0.2.9 and never drawn — item 88 keeps it so).
        Element ViewToggles(int v)
        {
            var cells = new Element[4];
            for (int i = 0; i < 4; i++)
            {
                int idx = i; bool on = v == i;
                cells[i] = new BoxEl
                {
                    Width = 40f, Height = 30f, Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = CornerRadius4.All(5f),
                    Fill = on ? Tok.AccentDefault : Tok.FillSubtleSecondary, HoverFill = on ? Tok.AccentSecondary : Tok.FillSubtleTertiary,
                    Role = AutomationRole.Button, Cursor = CursorId.Hand,
                    OnClick = () => view.Value = idx,
                    Children = [Icon(ViewGlyph(i), ViewGlyphSizes[i], on ? Tok.TextOnAccentPrimary : Tok.TextSecondary)],
                };
            }
            return new BoxEl { Direction = 0, Gap = 4f, Padding = new Edges4(2f, 2f, 2f, 4f), Children = cells };
        }

        static Element Header(string t) => new BoxEl
        {
            Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.XXS),
            Children = [Design.Type.Eyebrow(t) with { Color = Tok.TextTertiary }],
        };
    }

    // ══ 3. CRUMBS, GRIP, PANES, LINKS ════════════════════════════════════════════════════════════════════════════════

    /// <summary>The collapsed layout's breadcrumb (W14-W17): on the navigator's layer rung, a 1-DIP divider under it.</summary>
    public static Element CrumbBar(IReadOnlyList<string> crumbs, Action<int> onPick) => new BoxEl
    {
        Direction = 1, Shrink = 0f, Fill = Tok.FillLayerDefault,
        Children =
        [
            new BoxEl { Padding = new Edges4(Spacing.M, Spacing.S, Spacing.M, Spacing.S), Children = [BreadcrumbBar.Create(crumbs, onPick)] },
            new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault },
        ],
    };

    /// <summary>THE column seam (W18): a 16-DIP grip strip with a 1-DIP <c>StrokeCardDefault</c> hairline centred behind
    /// the splitter's hover thumb. The seam brush is a LIVE thunk, so a theme swap re-resolves it without a render.</summary>
    public static Element ColumnGrip(Signal<float> width, float min, float max, Action onCommit) => new BoxEl
    {
        Width = Splitter.StripW, Shrink = 0f, ZStack = true,
        Children =
        [
            new BoxEl { Width = 1f, AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Center, HitTestVisible = false,
                        Fill = Prop.Of(static () => Tok.StrokeCardDefault) },
            new BoxEl { Direction = 1, AlignItems = FlexAlign.Stretch, AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch,
                        Children = [Splitter.Create(width, onCommit, new Splitter.SplitterOptions { Min = min, Max = max })] },
        ],
    };

    /// <summary>The navigator's layer rung and the reading panes' card rung (ch 15 §0 #2) — the seam does the separating.</summary>
    internal static BoxEl NavPanel => new() { Direction = 1, ClipToBounds = true, Fill = Tok.FillLayerDefault };
    internal static BoxEl ReadingPane => new() { Direction = 1, ClipToBounds = true, Fill = Tok.FillCardDefault };

    /// <summary>"Go to artist" — a HyperlinkButton (accent ink, 4-radius, trailing ↗), never a capsule.</summary>
    public static Element GoToArtistLink(Action onClick) => new BoxEl
    {
        Direction = 0, Gap = Spacing.XS, AlignItems = FlexAlign.Center, Corners = Radii.ControlAll,
        Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.XS),
        Fill = Tok.FillSubtleTransparent, HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
        BrushTransitionMs = Design.Motion.Faster,
        Role = AutomationRole.Hyperlink, Focusable = true, Cursor = CursorId.Hand,
        HoverScale = Design.Motion.ScaleStandard.Hover, PressScale = Design.Motion.ScaleStandard.Press,
        OnClick = onClick,
        Children =
        [
            new TextEl(Loc.Get(Strings.Detail.GoToArtist))
            {
                Size = 14f, LineHeight = 20f, Weight = 600,
                Color = Tok.AccentTextPrimary, HoverColor = Tok.AccentTextSecondary, PressedColor = Tok.AccentTextTertiary,
            },
            Icon(Icons.OpenInNewWindow, 14f, Tok.AccentTextPrimary),
        ],
    };

    // ══ 4. THE BOUND ROWS AND CARDS (W1-W5, W19, W22) ═══════════════════════════════════════════════════════════════

    static readonly FormatCache<int> s_discoSubtitles = new();

    /// <summary>A navigator LIST row (extent 40 compact / 60): 40×40 art (r 20 artist / 5 album, show) + title 14/20/600 +
    /// subtitle 12/16 — compact drops both the art and the subtitle. Wears the bound AccentPill chrome.</summary>
    public static Element NavRow(BoundItemScope<LibraryNavItem> scope, EntityKind kind, bool compact)
    {
        bool circular = kind == EntityKind.Artist;
        Element text = new BoxEl
        {
            Direction = 1, Grow = 1f, Basis = 0f, Gap = 1f, MinWidth = 0f,
            Children = compact
                ? [TitleText(scope, 14f, 20f), new BoxEl()]
                : [TitleText(scope, 14f, 20f),
                   kind == EntityKind.Artist
                       ? new TextEl(Loc.Get(Strings.Search.TypeArtist)) { Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }
                       : new TextEl(scope.Text(static it => LibraryRows.SubtitleOf(it.Kind, it.Slot))) { Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }],
        };
        Element content = new BoxEl
        {
            Direction = 0, Grow = 1f, AlignItems = FlexAlign.Center, Gap = Spacing.M, Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f),
            Draggable = DragOf(scope),
            Children = compact ? [text] : [ArtBox(scope, 40f, circular ? 20f : 5f), text],
        };
        return SelectorVisualsBound.AccentPill(scope.Row, content) with { Cursor = CursorId.Hand };
    }

    /// <summary>A navigator GRID card: the fill-width square cover (r Full artist / 6) + title 12/16/600 (compact drops it),
    /// pad 16 artist / 8. Wears the bound Border chrome.</summary>
    public static Element NavCard(BoundItemScope<LibraryNavItem> scope, EntityKind kind, bool compact)
    {
        bool circular = kind == EntityKind.Artist;
        float pad = circular ? 16f : Spacing.S;
        Element art = FillArt(scope, circular ? Radii.Full : 6f);
        Element content = new BoxEl
        {
            Direction = 1, Gap = Spacing.S, ClipToBounds = true, Padding = Edges4.All(pad), Draggable = DragOf(scope),
            Children = compact
                ? [art]
                : [art, TitleText(scope, 12f, 16f) with { AlignSelf = circular ? FlexAlign.Center : FlexAlign.Start }],
        };
        return BorderChrome(scope.Row, content);
    }

    /// <summary>A discography LIST row (extent 44 compact / 60): art 40 (wrapper r 4, art 5) + title + "2007 · ALBUM".</summary>
    public static Element DiscoRow(BoundItemScope<LibraryNavItem> scope, bool compact)
    {
        Element text = new BoxEl
        {
            Direction = 1, Grow = 1f, Basis = 0f, Gap = 1f, MinWidth = 0f,
            Children = [TitleText(scope, 14f, 20f), compact ? new BoxEl() : DiscoSubtitle(scope)],
        };
        Element content = new BoxEl
        {
            Direction = 0, Grow = 1f, AlignItems = FlexAlign.Center, Gap = Spacing.M, Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f),
            Draggable = DragOf(scope),
            Children = compact ? [text] : [ArtBox(scope, 40f, 5f), text],
        };
        return SelectorVisualsBound.AccentPill(scope.Row, content) with { Cursor = CursorId.Hand };
    }

    /// <summary>A discography GRID card: pad 4, gap 4, cover r 6, title, and "year · KIND" when not compact.</summary>
    public static Element DiscoCard(BoundItemScope<LibraryNavItem> scope, bool compact)
    {
        Element art = FillArt(scope, 6f);
        Element content = new BoxEl
        {
            Direction = 1, Gap = Spacing.XS, ClipToBounds = true, Padding = Edges4.All(Spacing.XS), Draggable = DragOf(scope),
            Children = compact ? [art, TitleText(scope, 12f, 16f)] : [art, TitleText(scope, 12f, 16f), DiscoSubtitle(scope)],
        };
        return BorderChrome(scope.Row, content);
    }

    static TextEl TitleText(in BoundItemScope<LibraryNavItem> scope, float size, float line)
        => new(scope.Text(static it => LibraryRows.TitleOf(it.Kind, it.Slot)))
        {
            Size = size, LineHeight = line, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
        };

    // "2007 · ALBUM" through a hoisted cache keyed by (year, kind): a recycle never concatenates.
    static TextEl DiscoSubtitle(in BoundItemScope<LibraryNavItem> scope)
        => new(scope.Text(static it => AlbumCode(it.Slot), s_discoSubtitles, static code => (code >> 3 > 0 ? (code >> 3) + " · " : "")
                                                                                          + Detail.Text.KindLabel((AlbumKind)(code & 7))))
        {
            Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
        };

    static int AlbumCode(int slot)
    {
        var a = new Album(slot);
        int year = a.IsValid && a.Knows(AlbumFields.Year) ? a.Year : 0;
        int kind = a.IsValid && a.Knows(AlbumFields.Kind) ? (int)a.Kind : (int)AlbumKind.Album;
        return (year << 3) | (kind & 7);
    }

    static string? ArtOf(in LibraryNavItem it) => Controls.ArtUrl(LibraryRows.ImageOf(it.Kind, it.Slot));

    static Element ArtBox(in BoundItemScope<LibraryNavItem> scope, float edge, float corners) => new BoxEl
    {
        Width = edge, Height = edge, Shrink = 0f, Corners = CornerRadius4.All(corners), ClipToBounds = true,
        Children =
        [
            new ImageEl
            {
                Source = scope.Image(static it => ArtOf(it)), Width = edge, Height = edge, Fit = ImageFit.Cover,
                Corners = CornerRadius4.All(corners), DecodePx = edge,
                Placeholder = scope.Color(static it => Design.PlaceholderFor(ArtOf(it))),
            },
        ],
    };

    static Element FillArt(in BoundItemScope<LibraryNavItem> scope, float corners) => new ImageEl
    {
        Source = scope.Image(static it => ArtOf(it)), Fit = ImageFit.Cover, AspectRatio = 1f, DecodePx = 256f,
        Corners = CornerRadius4.All(corners),
        Placeholder = scope.Color(static it => Design.PlaceholderFor(ArtOf(it))),
    };

    /// <summary>A library row is a drag SOURCE only, click-primary (×2 drag box); the payload is built at promotion from
    /// the slot's CURRENT item. Tracks resolve lazily through the sidebar's library seam.</summary>
    static DragSource DragOf(in BoundItemScope<LibraryNavItem> scope)
    {
        var item = scope.Item;
        return Drag.Source(() => PayloadOf(item.Peek()), clickPrimary: true);
    }

    internal static DragPayload PayloadOf(in LibraryNavItem it)
    {
        var id = LibraryRows.IdOf(it.Kind, it.Slot);
        string uri = id.Text;
        var kind = Drag.KindOf(it.Kind);
        string prefix = it.Kind switch
        {
            EntityKind.Artist => SidebarPinId.ArtistPrefix,
            EntityKind.Show => SidebarPinId.ShowPrefix,
            _ => SidebarPinId.AlbumPrefix,
        };
        Func<CancellationToken, Task<Track[]>>? resolver = null;
        if (uri.Length > 0 && kind is DragKind.Album or DragKind.Show && Sidebar.LibraryWrites?.ResolveTracks is { } resolve)
            resolver = ct => resolve(uri, ct);
        return new DragPayload(kind, prefix + uri, uri, LibraryRows.TitleOf(it.Kind, it.Slot), new EntityRef(it.Kind, it.Slot),
                               TrackResolver: resolver, ArtUrl: ArtOf(it));
    }

    /// <summary>The ItemContainer BORDER selection chrome, bound (W19 right): a 3-DIP accent ring + a 1-DIP inner stroke
    /// inset 2 revealed by the slot's IsSelected predicate (167 ms brush fade), the subtle hover/press plate above the
    /// content, and the container's tap/keys/focus wiring — shape-stable, so selection never re-renders a card.</summary>
    static BoxEl BorderChrome(in RowScope row, Element content)
    {
        Func<bool> isSel = row.IsSelected;
        var interact = row.OnInteraction;
        return new BoxEl
        {
            ZStack = true, Corners = Radii.ControlAll, Fill = Tok.FillSubtleTransparent,
            Focusable = false, FocusVisualMargin = Edges4.All(0f), Role = AutomationRole.Button, Cursor = CursorId.Hand,
            OnPointerReleased = args => interact(args.ClickCount >= 2 ? ItemContainerTrigger.DoubleTap : ItemContainerTrigger.Tap, args.Mods),
            OnKeyDown = args =>
            {
                if (args.KeyCode == Keys.Enter) { interact(ItemContainerTrigger.EnterKey, args.Mods); args.Handled = true; }
                else if (args.KeyCode == Keys.Space && !args.IsRepeat) { interact(ItemContainerTrigger.SpaceKey, args.Mods); args.Handled = true; }
            },
            OnFocusChanged = row.OnFocusChanged,
            Children =
            [
                new BoxEl { Key = "ic-content", Direction = 1, Children = [content] },
                new BoxEl
                {
                    Key = "ic-ring", HitTestVisible = false, Corners = Radii.ControlAll, BorderWidth = ItemContainer.SelectionVisualThickness,
                    BorderColor = Prop.Of(() => isSel() ? Tok.AccentDefault : ColorF.Transparent), BrushTransitionMs = Design.Motion.Fast,
                },
                new BoxEl
                {
                    Key = "ic-common", HitTestVisible = false, Corners = Radii.ControlAll,
                    HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary, HoverDurationMs = 0f, PressDurationMs = 0f,
                    Margin = Edges4.All(ItemContainer.SelectedInnerMargin), BorderWidth = ItemContainer.SelectedInnerThickness,
                    BorderColor = Prop.Of(() => isSel() ? Tok.FillControlSolid : ColorF.Transparent),
                },
            ],
        };
    }

    // ══ 5. THE SEARCH-ROW SHAPES (W11-W13, W23, W26) ════════════════════════════════════════════════════════════════

    /// <summary>Retained hits stay put; only a real insert/remove/reorder animates (90 ms in, 70 ms out).</summary>
    internal static readonly LayoutTransition SearchRowChange = new(
        TransitionChannels.Position | TransitionChannels.Opacity,
        TransitionDynamics.Tween(90f, Easing.SmoothOut),
        Enter: new EnterExit(Dy: 3f, Opacity: 0f, Active: true),
        Exit: new EnterExit(Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Tween(70f, Easing.SmoothOut));

    /// <summary>An artist / album hit (h 56): cover 44 (r Full / 4) · optional "why" eyebrow · highlighted title · subtitle.
    /// The browse-list selection language (subtle fills), never an accent tint.</summary>
    public static Element SearchRow(string key, string? cover, bool circular, string title, int matchStart, int matchLen,
                                    string subtitle, string? eyebrow, bool selected, Action onClick)
    {
        float r = circular ? Radii.Full : Radii.Control;
        var text = new List<Element>(3);
        if (!string.IsNullOrEmpty(eyebrow))
            text.Add(new TextEl(eyebrow) { Size = 12f, LineHeight = 16f, Weight = 600, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis });
        text.Add(Controls.SearchHighlight(title, matchStart, matchLen, 14f, 600, Tok.TextPrimary));
        if (subtitle.Length > 0)
            text.Add(new TextEl(subtitle) { Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis });
        return new BoxEl
        {
            Key = key, Animate = SearchRowChange,
            Direction = 0, Height = 56f, AlignItems = FlexAlign.Center, Gap = Spacing.M, ClipToBounds = true,
            Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f), Corners = Radii.ControlAll,
            Fill = selected ? Tok.FillSubtleSecondary : ColorF.Transparent,
            HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = onClick,
            Children =
            [
                new BoxEl { Width = 44f, Height = 44f, Shrink = 0f, Corners = CornerRadius4.All(r), ClipToBounds = true,
                    SkeletonOverride = CoverSkeleton(44f, r), Children = [Controls.Artwork(cover, 44f, 44f, r)] },
                new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, Gap = 1f, ClipToBounds = true, MinWidth = 0f, Children = text.ToArray() },
            ],
        };
    }

    /// <summary>A track hit (h 44): 36 cover (dropped when track artwork is hidden — part of the key) + a 13/600
    /// highlighted title. A click plays in place.</summary>
    public static Element TrackHitRow(string key, string? cover, string title, int matchStart, int matchLen, bool showArt, Action onClick)
    {
        Element titleBox = new BoxEl
        {
            Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, ClipToBounds = true,
            Children = [Controls.SearchHighlight(title, matchStart, matchLen, 13f, 600, Tok.TextPrimary)],
        };
        return new BoxEl
        {
            Key = key + (showArt ? ":art=True" : ":art=False"), Animate = SearchRowChange,
            Direction = 0, Height = 44f, AlignItems = FlexAlign.Center, Gap = Spacing.M, ClipToBounds = true,
            Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f), Corners = Radii.ControlAll,
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = onClick,
            Children = showArt
                ? [new BoxEl { Width = 36f, Height = 36f, Shrink = 0f, Corners = Radii.ControlAll, ClipToBounds = true,
                       SkeletonOverride = CoverSkeleton(36f, Radii.Control), Children = [Controls.Artwork(cover, 36f, 36f, Radii.Control)] },
                   titleBox]
                : [titleBox],
        }.Interactive(Interaction.Subtle);
    }

    /// <summary>The cover slot's honest skeleton: a same-sized tile in the shimmer's bar colour.</summary>
    static Element CoverSkeleton(float size, float corners) => new BoxEl
    {
        Width = size, Height = size, Shrink = 0f, Corners = CornerRadius4.All(corners),
        Fill = SkeletonStyle.Default.BarColor, IsEnabled = false, HitTestVisible = false,
    };

    /// <summary>A facet header: eyebrow + count (hidden when &lt; 0; faded to 40 % while a newer answer is coming).</summary>
    public static Element FacetHeader(string label, int count, bool refining) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS, Shrink = 0f,
        Padding = new Edges4(Spacing.M, Spacing.M, Spacing.M, Spacing.S),
        Children =
        [
            Design.Type.Eyebrow(label) with { Color = Tok.TextTertiary },
            count >= 0
                ? new TextEl(FormatCache.Int(count))
                  {
                      Size = 12f, LineHeight = 16f, Weight = 600, BrushTransitionMs = Design.Motion.Faster,
                      Color = refining ? Tok.TextTertiary with { A = Tok.TextTertiary.A * 0.4f } : Tok.TextTertiary,
                  }
                : new BoxEl(),
        ],
    };

    /// <summary>The left search column's zero-hit copy (14/20 tertiary, left-aligned) — NOT the browse EmptyState.</summary>
    public static Element SearchMessage(string text) => new BoxEl
    {
        Padding = new Edges4(Spacing.M, Spacing.XL, Spacing.M, Spacing.XL),
        Children = [new TextEl(text) { Size = 14f, LineHeight = 20f, Color = Tok.TextTertiary }],
    };

    // ══ 6. THE LIBRARY SEAM (contract §2.6 → Controls.Library) ═══════════════════════════════════════════════════════

    /// <summary>The save/follow affordances' one seam. <c>IsSaved</c> SUBSCRIBES (the scope epoch + the relation's
    /// <c>Changed</c>) so a heart re-skins the frame the optimistic edge lands; a pending remove reads unsaved. Tracks,
    /// albums, artists and shows go through <see cref="Add"/>/<see cref="Remove"/>; a playlist follows through the
    /// rootlist (<c>Spotify.Library.FollowPlaylist</c>); the liked collection reads saved and is not toggled.</summary>
    public static Controls.LibrarySeam LibrarySeam { get; } = new(IsSavedUri, ToggleSavedUri);

    static bool IsSavedUri(string uri)
    {
        _ = Entities.ScopeEpoch.Value;
        var scope = Entities.Current;
        if (scope is null || string.IsNullOrEmpty(uri) || scope.MeSlot <= Table.None) return false;
        if (EntityUri.IsLikedCollection(uri)) return true;
        var id = EntityUri.Parse(uri).Id;
        var table = RelationOf(id.Kind, scope);
        if (id.Kind == EntityKind.Playlist)
        {
            _ = scope.Edges.Rootlist.Changed.Value;
            return scope.Playlists.TryGetSlot(id, out int p) && scope.Edges.Rootlist.Contains(scope.MeSlot, p);
        }
        if (table is null || Entities.TableFor(id.Kind) is not { } rows) return false;
        _ = table.Changed.Value;
        return rows.TryGetSlot(id, out int slot) && table.Contains(scope.MeSlot, slot)
               && table.PendingOf(scope.MeSlot, slot) != EdgePending.Remove;
    }

    static void ToggleSavedUri(string uri, string? name)
    {
        var scope = Entities.Current;
        if (scope is null || string.IsNullOrEmpty(uri) || EntityUri.IsLikedCollection(uri)) return;
        var me = Me;
        if (!me.IsValid) return;
        var id = EntityUri.Parse(uri).Id;
        if (id.Kind == EntityKind.Playlist)
        {
            bool following = scope.Playlists.TryGetSlot(id, out int p) && scope.Edges.Rootlist.Contains(me.Slot, p);
            Spotify.Library.FollowPlaylist(uri, !following);
            return;
        }
        if (Entities.TableFor(id.Kind) is not { } rows) return;
        LibraryEdgeKind kind = id.Kind switch
        {
            EntityKind.Album => LibraryEdgeKind.SavedAlbums,
            EntityKind.Artist => LibraryEdgeKind.FollowedArtists,
            EntityKind.Show => LibraryEdgeKind.SavedShows,
            EntityKind.Track => LibraryEdgeKind.Liked,
            _ => LibraryEdgeKind.Pins,
        };
        if (kind == LibraryEdgeKind.Pins) return;   // an episode / user / concert has no library relation
        int slot = rows.Slot(id);
        if (me.Has(kind, slot) && me.PendingOf(kind, slot) != EdgePending.Remove) me.Remove(kind, slot, id);
        else me.Add(kind, slot, id);
        Entities.Publish();
    }

    static EdgeTable<LibraryEdge>? RelationOf(EntityKind kind, Scope scope) => kind switch
    {
        EntityKind.Track => scope.Edges.Liked,
        EntityKind.Album => scope.Edges.SavedAlbums,
        EntityKind.Artist => scope.Edges.FollowedArtists,
        EntityKind.Show => scope.Edges.SavedShows,
        _ => null,
    };
}
