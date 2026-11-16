// ── Themes/Zune/Structure/Views/Home/Board/ZuneHomeAllItems.cs — Zune's "all items" (spec 8.2 "Add", "Search for something to add") ─────────────
//
// The next section of Home's panorama, not a screen: the round arrow at the end of Home (or "add" in the app bar while customizing) slides the board
// away and this in under the SAME title (the page title does not move). Keyed `home:add`.
//
//   pivots   widgets · spotify · library: display words (40/48 light, lowercase) on ONE line that bleeds under a fade, never wrapped; then the round
//            search button and the back ring
//   field    the search: opens over the list; typing filters across all three pivots (the pivot words dim); "3 results in all items" under the list,
//            spoken; no match says anything with a page can be pinned from its own menu
//   rows     a 62 square (the cover, or the source's glyph on the accent plate), the name in the 26/32 light voice, one line that starts with what it
//            is ("widget · …"), and a round pin button. A tap pins and slides back to Home at the new tile (a row already on Home says so, and a tap
//            goes to its tile); the round button pins or unpins without leaving; a hold or a right-click opens its menu
//
// The rows are a one-column pack grid in the page's own scroller (`PackGrid`: only the rows near the viewport are realized), so a long library
// costs a screenful of rows. Escape: clears the search, then closes the field, then leaves all items.

using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Forms;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Input;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using B = Wavee.Themes.Zune.ZuneHomeBoardRules;
using K = Wavee.Themes.Zune.ZuneDiscoveryKit;

namespace Wavee.Themes.Zune;

/// <summary>Zune's all items.</summary>
internal static class ZuneHomeAllItems
{
    const string Widgets = "widgets", Spotify = "spotify", Library = "library";
    static readonly string[] s_pivots = [Widgets, Spotify, Library];
    static readonly string[] s_noSuggest = [];

    /// <summary>The search field is open (the round search button, "/").</summary>
    static readonly Signal<bool> s_searching = new(false);

    /// <summary>A request to focus the field (bumped by "/" and the search button).</summary>
    static readonly Signal<int> s_focusField = new(0);

    // ══ verbs ═════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Slide all items in (on the widgets pivot unless one is open).</summary>
    public static void Open(HomeBoardProps board)
    {
        var view = board.View;
        if (view.Add.Peek().Kind == HomeAddSurfaceKind.AllItems) return;
        if (view.OpenId.Peek() is not null) board.Verbs.Close(refocus: false);
        view.Add.Value = new HomeAddSurface(HomeAddSurfaceKind.AllItems, Widgets);
        // The next section of the panorama starts at its top, under the title.
        board.Scroll.ScrollTo(0d);
        board.Verbs.Say(Loc.Get(Strings.Theme.Zune.Home.AllItemsSaid));
    }

    /// <summary>Slide back to Home; <paramref name="revealId"/>, when given, is ringed and brought into view once the board is back.</summary>
    public static void Close(HomeBoardProps board, string? revealId)
    {
        var view = board.View;
        s_searching.Value = false;
        view.Query.Value = "";
        if (revealId is not null) view.RingId.Value = revealId;
        view.Add.Value = HomeAddSurface.Closed;
    }

    /// <summary>"/": all items with its search field open and focused.</summary>
    public static void OpenSearch(HomeBoardProps board)
    {
        Open(board);
        s_searching.Value = true;
        s_focusField.Value = s_focusField.Peek() + 1;
    }

    /// <summary>Escape inside all items: clear the search, then close the field, then leave. False when all items is not open.</summary>
    public static bool Escape(HomeBoardProps board)
    {
        var view = board.View;
        if (view.Add.Peek().Kind != HomeAddSurfaceKind.AllItems) return false;
        if (view.Query.Peek().Length > 0) view.Query.Value = "";
        else if (s_searching.Peek()) s_searching.Value = false;
        else Close(board, null);
        return true;
    }

    // ══ the end of Home ═══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The round arrow at the end of the board: "all items".</summary>
    public static Element EndRow(HomeBoardProps board, ZuneHomeBoardLayout lay) => new BoxEl
    {
        Key = B.EndKey, Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, MinWidth = 0f, Margin = new Edges4(0f, lay.EndRowTop, 0f, 0f),
        AlignSelf = FlexAlign.Start, Focusable = true, Role = AutomationRole.Button, Cursor = CursorId.Hand, OnClick = () => Open(board),
        Children =
        [
            K.RingDisc(Icons.Forward, primary: false, size: B.ItemsRing) with { HitTestVisible = false },
            R.Type.CardTitle(Loc.Get(Strings.Theme.Zune.Home.AllItems)) with { Case = R.Case.For(LabelKind.Command), Color = Tok.TextSecondary, MaxLines = 1 },
        ],
    };

    // ══ the section ═══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The section that slides in under the title.</summary>
    public static Element Section(HomeBoardProps board, ZuneHomeBoardLayout lay, bool rendered)
    {
        float slide = Design.Reduced ? 0f : B.SectionSlide;
        return Embed.Comp(new SectionProps(board, lay), static () => new SectionView()) with
        {
            Key = HomeBoardKeys.Add,
            Enter = rendered ? new EnterExit(Opacity: 0f, Dx: slide, Active: true) : null,
            Exit = new EnterExit(Opacity: 0f, Dx: slide, Active: true),
            Transition = MotionTokenDef.Eased(B.RecedeMs, Easing.FluentDecelerate, ReducedMotionPolicy.KeepFade),
        };
    }

    internal sealed record SectionProps(HomeBoardProps Board, ZuneHomeBoardLayout Lay);

    sealed class SectionView : Component
    {
        SectionProps? _p;
        PackGridOptions<HomeCandidate>? _options;
        Func<HomeCandidate, PackCell, Element>? _template;
        string _lastQuery = "";
        int _focusedAsk, _tries;
        Field<string>? _field;
        InputHooks? _hooks;
        TimerHandle _focusTimer;
        readonly Action _focusNow;

        public SectionView() => _focusNow = FocusNow;

        void FocusNow()
        {
            if (_field?.Node.Peek() is { IsNull: false } n) { _hooks?.FocusNode?.Invoke(n, true); return; }
            if (++_tries < B.FocusRetries) _focusTimer.RestartIn(B.FocusRetryMs);
        }

        public override Element Render()
        {
            var p = UseProps<SectionProps>();
            _p = p;
            var board = p.Board;
            var view = board.View;
            var lay = p.Lay;
            var hooks = UseContext(InputHooks.Current);
            var field = UseField(view.Query);
            int focusAsk = s_focusField.Value;
            // The field mounts with the request (the round button opens it): focus lands once its editor is realized (a short timer that retries
            // a few frames, never a render loop).
            _field = field;
            _hooks = hooks;
            _focusTimer = UseTimeout(_focusNow, B.FocusRetryMs);
            UseLayoutEffect(() => { if (focusAsk > _focusedAsk) { _focusedAsk = focusAsk; _tries = 0; _focusTimer.RestartIn(B.FocusRetryMs); } },
                DepKey.From(focusAsk));

            string pivot = view.Add.Value.Arg is { Length: > 0 } a ? a : Widgets;
            bool searching = s_searching.Value;
            string q = view.Query.Value.Trim();
            var rowsSig = UseComputed(() => RowsOf(_p!.Board));
            var rows = rowsSig.Value;

            _template ??= (c, cell) => Row(c);
            _options ??= new PackGridOptions<HomeCandidate>
            {
                KeyOf = static c => B.RowPrefix + c.Id,
                EntryOf = c => new PackEntry(PackEntryKind.Row, Extent: _p!.Lay.ItemRowH),
                Metrics = static () => new PackMetrics(1, 0f, B.RowGap, 0f),
                Scroll = board.Scroll,
                ArrowNavigation = false,
                Key = "home:add:list",
            };

            var kids = new List<Element>(4) { PivotRow(board, pivot, searching, q.Length > 0) };
            if (searching)
                kids.Add(new BoxEl
                {
                    Key = B.FieldKey, Direction = 0, MinWidth = 0f, MaxWidth = lay.ItemsMaxW, Margin = new Edges4(0f, 0f, 0f, B.FieldGap),
                    Enter = new EnterExit(Opacity: 0f, Active: true), Exit = new EnterExit(Opacity: 0f, Active: true),
                    Children =
                    [
                        AutoSuggestBox.Create(s_noSuggest, Loc.Get(Strings.Theme.Zune.Home.Search), text: view.Query, onChange: OnQuery, field: field,
                            queryIcon: Icons.Search, grow: 1f, maxFillWidth: lay.ItemsMaxW, minHeight: B.FieldH, debounceMs: 0f,
                            cornerRadius: 0f),
                    ],
                });
            // The list: keyed per pivot (or the search), so a switch slides the new rows in as a pivot does.
            string listKey = q.Length > 0 ? "home:add:list:search" : "home:add:list:" + pivot;
            float slide = Design.Reduced ? 0f : B.SectionSlide;
            kids.Add(new BoxEl
            {
                Key = listKey, Direction = 1, MinWidth = 0f, MaxWidth = lay.ItemsMaxW, AlignItems = FlexAlign.Stretch,
                Enter = new EnterExit(Opacity: 0f, Dx: slide, Active: true), Exit = new EnterExit(Opacity: 0f, Active: true),
                Transition = MotionTokenDef.Eased(B.RecedeMs, Easing.FluentDecelerate, ReducedMotionPolicy.KeepFade),
                Children = [PackGrid.Create(rowsSig, _template, _options)],
            });
            if (q.Length > 0)
                kids.Add(R.Type.DenseMeta(rows.Count == 0 ? Strings.Theme.Zune.Home.NoMatch(q: q) : Strings.Theme.Zune.Home.Results(n: rows.Count)) with
                {
                    Key = "home:add:count", Color = Tok.TextTertiary, Case = R.Case.For(LabelKind.Label), Wrap = TextWrap.Wrap, MinWidth = 0f,
                    MaxWidth = lay.ItemsMaxW, Margin = new Edges4(0f, B.HintTop, 0f, 0f),
                });
            kids.Add(new BoxEl { Height = lay.FootReserve, Shrink = 0f, HitTestVisible = false });
            return new BoxEl { Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Children = kids.ToArray() };
        }

        /// <summary>The rows: every match of the search across the pivots, else the pivot's candidates.</summary>
        static IReadOnlyList<HomeCandidate> RowsOf(HomeBoardProps board)
        {
            string pivot = board.View.Add.Value.Arg is { Length: > 0 } a ? a : Widgets;
            string q = board.View.Query.Value.Trim();
            var all = board.Catalog.All();
            if (q.Length > 0) return board.Catalog.Find(all, q);
            var g = GroupOf(pivot);
            var list = new List<HomeCandidate>(all.Count);
            foreach (var c in all) if (c.Group == g) list.Add(c);
            return list;
        }

        void OnQuery(string text)
        {
            if (_p is null) return;
            string q = text.Trim();
            if (q == _lastQuery) return;
            _lastQuery = q;
            if (q.Length == 0) return;
            var board = _p.Board;
            int n = board.Catalog.Find(board.Catalog.All(), q).Count;
            board.Verbs.Say(n > 0 ? Strings.Theme.Zune.Home.Results(n: n) : Strings.Theme.Zune.Home.NoMatch(q: q));
        }

        /// <summary>The pivots on one line (bleeding under a fade), the round search button and the back ring.</summary>
        Element PivotRow(HomeBoardProps board, string pivot, bool searching, bool filtering)
        {
            var lay = _p!.Lay;
            var words = new Element[s_pivots.Length];
            for (int i = 0; i < words.Length; i++)
            {
                string id = s_pivots[i];
                bool on = id == pivot && !filtering;
                words[i] = new BoxEl
                {
                    Key = B.PivotPrefix + id, Direction = 0, Shrink = 0f, Focusable = true, Role = AutomationRole.Tab, Cursor = CursorId.Hand,
                    OnClick = () =>
                    {
                        if (board.View.Query.Peek().Length > 0) board.View.Query.Value = "";
                        board.View.Add.Value = new HomeAddSurface(HomeAddSurfaceKind.AllItems, id);
                    },
                    Children =
                    [
                        R.Type.PageHero(Loc.Get(id switch
                        {
                            Spotify => Strings.Theme.Zune.Home.PivotSpotify, Library => Strings.Theme.Zune.Home.PivotLibrary, _ => Strings.Theme.Zune.Home.PivotWidgets,
                        })) with
                        {
                            Case = R.Case.For(LabelKind.Navigation), Color = on ? Tok.TextPrimary : Tok.TextTertiary, HoverColor = Tok.TextPrimary,
                            MaxLines = 1, Wrap = TextWrap.NoWrap,
                        },
                    ],
                };
            }
            return new BoxEl
            {
                Key = "home:add:head", Direction = 0, MinWidth = 0f, AlignItems = FlexAlign.Center, Gap = Spacing.L,
                Padding = new Edges4(0f, B.PivotRowTop, 0f, B.PivotRowBottom),
                Children =
                [
                    new BoxEl
                    {
                        Key = "home:add:pivots", Direction = 0, Gap = lay.PivotGap, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, ClipToBounds = true,
                        AlignItems = FlexAlign.Center, EdgeFade = new EdgeFadeSpec(EdgeMask.Right, 0f, 0f, B.TitleFade, 0f),
                        Children = words,
                    },
                    Ring(B.SearchRingKey, Icons.Search, Loc.Get(Strings.Theme.Zune.Home.Search), searching, () =>
                    {
                        if (s_searching.Peek()) { board.View.Query.Value = ""; s_searching.Value = false; }
                        else { s_searching.Value = true; s_focusField.Value = s_focusField.Peek() + 1; }
                    }),
                    Ring(B.BackRingKey, Icons.Back, Loc.Get(Strings.Theme.Zune.Home.Back), false, () => Close(board, null)),
                ],
            };
        }

        /// <summary>One row: the square, the name and its line, the round pin button.</summary>
        Element Row(HomeCandidate c)
        {
            var board = _p!.Board;
            var lay = _p.Lay;
            bool on = c.OnHome;
            string line = c.Group switch
            {
                HomeCatalogGroup.Widgets => Strings.Theme.Zune.Home.LineWidget(line: on ? Loc.Get(Strings.Theme.Zune.Home.OnHome) : c.Line),
                HomeCatalogGroup.Spotify => Strings.Theme.Zune.Home.LineSpotify(line: on ? Loc.Get(Strings.Theme.Zune.Home.OnHome) : c.Line),
                HomeCatalogGroup.Library => Strings.Theme.Zune.Home.LineLibrary(line: on ? Loc.Get(Strings.Theme.Zune.Home.OnHome) : c.Line),
                _ => Strings.Theme.Zune.Home.LineExtension(line: on ? Loc.Get(Strings.Theme.Zune.Home.OnHome) : c.Line),
            };
            NodeHandle anchor = default;
            var main = new BoxEl
            {
                Key = B.RowPrefix + c.Id + ":go", Direction = 0, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, AlignItems = FlexAlign.Center, Gap = B.ItemGap,
                Focusable = true, Role = AutomationRole.Button, Cursor = CursorId.Hand,
                OnRealized = h => anchor = h,
                OnClick = () => Tap(board, c),
                Hold = new HoldSpec(TouchMs: B.HoldTouchMs, MouseMs: B.HoldMouseMs),
                OnHold = _ => Menu(board, c, anchor),
                OnContextRequested = e => { if (e.Trigger != ContextRequestTrigger.Hold) Menu(board, c, anchor); },
                Children =
                [
                    Square(c, lay.ItemArt),
                    new BoxEl
                    {
                        Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
                        Children =
                        [
                            R.Type.Heading(c.Title) with
                            {
                                Case = c.Group == HomeCatalogGroup.Widgets ? R.Case.For(LabelKind.Heading) : TextCase.AsWritten,
                                MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                            },
                            R.Type.DenseMeta(line) with { Color = Tok.TextSecondary, Case = R.Case.For(LabelKind.Label), MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                        ],
                    },
                ],
            };
            return new BoxEl
            {
                Direction = 0, Grow = 1f, MinWidth = 0f, AlignItems = FlexAlign.Center, Gap = Spacing.M,
                Children =
                [
                    main,
                    Ring(B.RowPinPrefix + c.Id, on ? Icons.UnPin : Icons.Pin,
                        Loc.Get(on ? Strings.Theme.Zune.Home.UnpinFromHome : Strings.Theme.Zune.Home.PinToHome), on, () => Toggle(board, c)),
                ],
            };
        }
    }

    static HomeCatalogGroup GroupOf(string pivot) => pivot switch
    {
        Spotify => HomeCatalogGroup.Spotify, Library => HomeCatalogGroup.Library, _ => HomeCatalogGroup.Widgets,
    };

    /// <summary>A tap on a row: pin it and go back to Home at its new tile; a row already on Home goes to its tile.</summary>
    static void Tap(HomeBoardProps board, HomeCandidate c)
    {
        if (c.OnHome) { Close(board, c.EntryId); return; }
        board.Verbs.Add(c);
        Close(board, board.View.RingId.Peek());
    }

    /// <summary>The round button: pin or unpin without leaving.</summary>
    static void Toggle(HomeBoardProps board, HomeCandidate c)
    {
        if (!c.OnHome) { board.Verbs.Add(c); return; }
        if (c.Source is PinSource pin) board.Verbs.Unpin(pin.PinId);
        else if (c.EntryId is { } id) board.Verbs.Remove(id);
    }

    /// <summary>A row's menu (a hold, a right-click, the Menu key): pin or unpin, and go to the tile when it is on Home.</summary>
    static void Menu(HomeBoardProps board, HomeCandidate c, NodeHandle anchor)
    {
        if (board.Overlay is not { } overlay) return;
        var items = new List<MenuFlyoutItem>(2);
        if (c.OnHome)
        {
            items.Add(new MenuFlyoutItem(Loc.Get(Strings.Theme.Zune.Home.UnpinFromHome), Icons.UnPin, Invoke: () => Toggle(board, c)));
            if (c.EntryId is { } id) items.Add(new MenuFlyoutItem(Loc.Get(Strings.Theme.Zune.Home.GoToTile), Icons.Forward, Invoke: () => Close(board, id)));
        }
        else items.Add(new MenuFlyoutItem(Loc.Get(Strings.Theme.Zune.Home.PinToHome), Icons.Pin, Invoke: () => Toggle(board, c)));
        OverlayHandle? handle = null;
        handle = overlay.Open(() => anchor, () => MenuFlyout.Create(items, () => handle?.Close(), size: OverlayFit.MenuSize),
            FlyoutPlacement.BottomEdgeAlignedLeft, new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = true });
    }

    /// <summary>A row's square: the cover (round for a person), or the source's glyph on the accent plate.</summary>
    static Element Square(HomeCandidate c, float edge)
    {
        if (c.Cover is { Length: > 0 } url)
            return new BoxEl { Width = edge, Height = edge, Shrink = 0f, Children = [Controls.Artwork(url, edge, edge, c.Round ? R.Shape.For(ShapeRole.Avatar) : 0f)] };
        return new BoxEl
        {
            Width = edge, Height = edge, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Fill = Prop.Of(static () => R.Ink.AccentAction),
            Children = [new TextEl(GlyphOf(c)) { Size = B.ItemGlyph, FontFamily = Tok.Definition.Fonts.Icon, Color = Prop.Of(static () => ColorContrast.PickContrast(R.Ink.AccentAction)) }],
        };
    }

    static string GlyphOf(HomeCandidate c) => c.Source switch
    {
        WidgetSource { Widget: "nowPlaying" } => Icons.Play,
        WidgetSource { Widget: "daylist" } => Icons.Clock,
        WidgetSource { Widget: "recents" } => Icons.History,
        WidgetSource { Widget: "upNext" } => Icons.Queue,
        WidgetSource { Widget: "releases" } => Icons.RefineSparkle,
        SpotifySectionSource or SpotifyFeedSource => Icons.Library,
        _ => Icons.Pin,
    };

    /// <summary>A 44 ring button (filled in the accent when on), its glyph centred.</summary>
    static Element Ring(string key, string glyph, string label, bool on, Action act) => Controls.Named(new BoxEl
    {
        Key = key, Width = B.ItemsRing, Height = B.ItemsRing, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = CornerRadius4.All(B.ItemsRing / 2f), BorderWidth = B.BadgeStroke, BorderColor = on ? Prop.Of(static () => R.Ink.AccentAction) : Tok.TextPrimary,
        Fill = on ? Prop.Of(static () => R.Ink.AccentAction) : ColorF.Transparent,
        HoverFill = on ? Prop.Of(static () => R.Ink.AccentAction) : R.Layer.ChromeHover, PressedFill = R.Layer.ChromePressed,
        Focusable = true, Role = AutomationRole.Button, Cursor = CursorId.Hand, OnClick = act,
        Children =
        [
            new TextEl(glyph)
            {
                Size = B.BadgeGlyph, FontFamily = Tok.Definition.Fonts.Icon,
                Color = on ? Prop.Of(static () => ColorContrast.PickContrast(R.Ink.AccentAction)) : Tok.TextPrimary,
            },
        ],
    }, label);
}
