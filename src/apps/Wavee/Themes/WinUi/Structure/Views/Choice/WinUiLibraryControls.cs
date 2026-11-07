// ── Themes/WinUi/Structure/Views/Choice/WinUiLibraryControls.cs — WinUI's library controls (`StyleKeys.LibraryControls`; winui menus/podcasts) ─────
//
// WinUI draws the library page through the stand-in (Classic's master-detail page, until W7 builds its own). The page's five choice controls were
// Zune's word rails there ("recents  alphabetical  more ▾", "alphabetical ⌄" in 13.5 semibold lowercase, a lowercase radio menu, the "View as"
// glyph grid and an "S M L" rail). This style is what WinUI registers so the borrowed page draws WinUI's instead (W-Flyouts, W-Library,
// W-LibraryList):
//
//   Sort        [Recently added ⌄]   → Sort by: Recents · Alphabetical · Artist · Recently added · Release date │ Ascending · Descending
//   Modes       Followed shows | Your Episodes                       (SelectorBar)
//   View        [▤|▦] [⋯]            → View: Compact list · List · Compact grid · Grid │ Size: Small · Medium · Large (grid only)
//   Scope       In your library | All releases · 42                 (SelectorBar)
//   ReaderSort  [Newest ⌄]           → Sort by: Newest · Oldest · Alphabetical
//
// Every signal is the core's persisted pref; the controls read and write them. Picking the sort word in force keeps its direction (WinUI states the
// direction as its own radio group instead of Classic's re-tap flip).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's library controls.</summary>
internal static class WinUiLibraryControls
{
    /// <summary>The style WinUI registers under <see cref="StyleKeys.LibraryControls"/>. Stateless.</summary>
    public static readonly LibraryControlsStyle Style = new(Sort, Modes, View, Scope, ReaderSort, Kinds, Head);

    // ══ THE SORT ═════════════════════════════════════════════════════════════════════════════════════════════════════

    static Element Sort(LibrarySortArgs a) => Embed.Comp(a, static () => new SortHost());

    /// <summary>Reads the sort and the direction (a change re-keys the button, so its label and its checked rows are the new ones).</summary>
    sealed class SortHost : Component
    {
        public override Element Render()
        {
            var a = UseProps<LibrarySortArgs>();
            var current = LibraryWordRail.Clamp(a.Kind, a.Sort.Value);
            bool desc = a.Desc.Value;
            var words = LibraryWordRail.WordsFor(a.Kind);
            var items = new List<MenuFlyoutItem>(words.Length + 5) { MenuFlyoutItem.Header(Loc.Get(Strings.Theme.Winui.Library.SortBy)) };
            var sort = a.Sort;
            var direction = a.Desc;
            for (int i = 0; i < words.Length; i++)
            {
                int code = (int)words[i];
                items.Add(MenuFlyoutItem.RadioItem(Loc.Get(WordKey(words[i])), code == (int)current, () => sort.Value = code));
            }
            items.Add(MenuFlyoutItem.Separator);
            items.Add(MenuFlyoutItem.RadioItem(Loc.Get(Strings.Theme.Winui.Library.Order.Ascending), !desc, () => direction.Value = false));
            items.Add(MenuFlyoutItem.RadioItem(Loc.Get(Strings.Theme.Winui.Library.Order.Descending), desc, () => direction.Value = true));
            string label = Loc.Get(WordKey(current));
            return WinUiChoice.Sort("lib:sort", FormatCache.Int((int)current) + (desc ? "d" : "a"), label, items, a.Height,
                                    Loc.Get(Strings.Theme.Winui.Library.SortBy));
        }
    }

    /// <summary>A sort word's sentence-case key (the rail's words are Zune's lowercase).</summary>
    static string WordKey(LibraryNavSort sort) => sort switch
    {
        LibraryNavSort.RecentlyAdded => Strings.Theme.Winui.Library.Sort.RecentlyAdded,
        LibraryNavSort.Alphabetical => Strings.Theme.Winui.Library.Sort.Alphabetical,
        LibraryNavSort.Creator => Strings.Theme.Winui.Library.Sort.Creator,
        LibraryNavSort.ReleaseDate => Strings.Theme.Winui.Library.Sort.ReleaseDate,
        LibraryNavSort.Albums => Strings.Theme.Winui.Library.Sort.Albums,
        _ => Strings.Theme.Winui.Library.Sort.Recents,
    };

    // ══ THE MODES ════════════════════════════════════════════════════════════════════════════════════════════════════

    static readonly string?[] s_modeKeys = ["lib:mode:0", "lib:mode:1"];

    static Element Modes(LibraryModesArgs a)
        => new BoxEl
        {
            Key = "lib:modes", Direction = 0, MinWidth = 0f,
            Children = [WinUiChoice.Pivot([Loc.Get(Strings.Podcast.Reader.FollowedShows), Loc.Get(Strings.Podcast.Reader.YourEpisodes)], a.Mode, a.Height,
                                          keys: s_modeKeys)],
        };

    // ══ THE PAGE HEAD (W-Library, W-LibraryList) ═════════════════════════════════════════════════════════════════════

    /// <summary>The page's head across its width (W-Library): "Library" (Title Large 40/52, the Display face) over a SelectorBar of the kinds ("Playlists Albums
    /// Artists Podcasts Audiobooks", the page on screen pilled) and the hairline under it, on the page's inset. At Compact the title steps down to Title
    /// 28/36 (W-Library-Compact). The route is the truth: a pick navigates, the next page re-seeds the selection.</summary>
    static Element Head(LibraryHeadArgs a) => Embed.Comp(new HeadProps(a), static () => new HeadView());

    sealed record HeadProps(LibraryHeadArgs Args);

    sealed class HeadView : Component
    {
        public override Element Render()
        {
            var a = UseProps<HeadProps>().Args;
            var facts = Wavee.Theming.TierContext.DrawnIn(UseContext(Wavee.Theming.ThemeFactsContext.Signal)).Value.Tiers;
            var tier = WinUiDetailTiers.Of(in facts).Tier;
            bool compact = tier == WidthTier.Compact;
            float inset = WinUiDetailRules.InsetOf(tier);
            // The insets step with the tier (16 / 24 / 36 / 48 across, 16 / 20 / 24 / 32 above): eased air, never padding, so a crossing glides the head and the
            // columns under it. The title keeps one size at every tier (a size step would move everything under it at once).
            return new BoxEl
            {
                Key = "lib:head", Direction = 1, Shrink = 0f, MinWidth = 0f,
                Children =
                [
                    WinUiDiscoveryMotion.AirV("lib:head:top", WinUiDetailRules.InsetTopOf(tier)),
                    new BoxEl
                    {
                        Direction = 0, MinWidth = 0f,
                        Children =
                        [
                            WinUiDiscoveryMotion.AirH("lib:head:lead", inset),
                            new BoxEl
                            {
                                Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = WinUiChoiceRules.HeadGap,
                                Children =
                                [
                                    new TextEl(Loc.Get(Strings.Theme.Winui.Library.Title))
                                    {
                                        FontFamily = WinUiShowRules.DisplayFace, Weight = WinUiShowRules.VoiceWeight, Color = Tok.TextPrimary,
                                        Size = WinUiChoiceRules.HeadTitle, LineHeight = WinUiChoiceRules.HeadTitleLine, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                                    },
                                    new BoxEl
                                    {
                                        Direction = 1, MinWidth = 0f,
                                        Children =
                                        [
                                            Kinds(new LibraryKindsArgs(a.Kinds, compact ? WinUiDetailRules.CompactTarget : WinUiChoiceRules.HeadPivot)),
                                            new BoxEl { Height = 1f, Shrink = 0f, Fill = Tok.StrokeDividerDefault },
                                        ],
                                    },
                                ],
                            },
                            WinUiDiscoveryMotion.AirH("lib:head:trail", inset),
                        ],
                    },
                ],
            };
        }
    }

    // ══ THE KINDS (the Compact head) ════════════════════════════════════════════════════════════════════════════════

    /// <summary>The Compact head's kinds as a SelectorBar (W-Library-Compact: "Playlists Albums Artists Podcasts Audiobooks", the page on screen pilled), in one
    /// scrolling line whose end fades where more wait. A fresh signal per render: the ROUTE is the truth; a pick navigates and the next page re-seeds it.</summary>
    static Element Kinds(LibraryKindsArgs a)
    {
        var kinds = a.Kinds;
        var labels = new string[kinds.Count];
        var keys = new string?[kinds.Count];
        int selected = 0;
        for (int i = 0; i < labels.Length; i++)
        {
            labels[i] = kinds[i].Title;
            keys[i] = "lib:kind:" + FormatCache.Int(i);
            if (kinds[i].Selected) selected = i;
        }
        Element bar = WinUiChoice.Pivot(labels, new Signal<int>(selected), a.Height, i => { if ((uint)i < (uint)kinds.Count) kinds[i].Open(); }, keys);
        return ScrollView(new BoxEl { Direction = 0, Shrink = 0f, Children = [bar] }, horizontal: true) with
        {
            Key = "lib:kinds", Grow = 0f, ContentSized = true, AutoEdgeFade = true, SuppressScrollBar = true, ScrollKey = "lib:kinds",
        };
    }

    // ══ THE VIEW ═════════════════════════════════════════════════════════════════════════════════════════════════════

    static Element View(LibraryViewArgs a) => Embed.Comp(a, static () => new ViewHost());

    /// <summary>The list / grid pair (W-Library: two 40 × 32 cells on one control plate, the one in force filled) and the "..." menu holding the four
    /// views and the grid's size. The pair keeps whichever compactness the persisted code carried, as Classic's toggle does.</summary>
    sealed class ViewHost : Component
    {
        public override Element Render()
        {
            var a = UseProps<LibraryViewArgs>();
            var view = a.View;
            var size = a.Size;
            int v = view.Value;
            bool grid = v >= 2;
            float h = MathF.Max(WinUiChoiceRules.ControlH, a.Edge);
            float cellH = h - 2f * WinUiChoiceRules.ViewStroke;
            Element pair = new BoxEl
            {
                Direction = 0, Shrink = 0f, Height = h, Corners = Radii.ControlAll, Padding = Edges4.All(WinUiChoiceRules.ViewStroke),
                BorderWidth = WinUiChoiceRules.ViewStroke, BorderColor = Tok.StrokeControlDefault,
                // W-Library: grid first, then list; the one in force on the subtle fill, the other bare.
                Children =
                [
                    Cell("lib:view:grid", Icons.ViewGrid, grid, () => view.Value = view.Peek() is 0 or 2 ? 2 : 3, Loc.Get(Strings.Library.View.Grid), cellH),
                    Cell("lib:view:list", Icons.ViewList, !grid, () => view.Value = view.Peek() is 0 or 2 ? 0 : 1, Loc.Get(Strings.Library.View.List), cellH),
                ],
            };
            Element more = IconButton.Menu(Icons.More, () => ViewItems(view, size), toolTip: Loc.Get(Strings.Theme.Winui.Library.ViewOptions),
                                           size: a.Edge > WinUiChoiceRules.ControlH ? ControlSize.Large : ControlSize.Medium);
            return new BoxEl
            {
                Key = "lib:view", Direction = 0, Gap = WinUiChoiceRules.ViewGap, Shrink = 0f, AlignItems = FlexAlign.Center,
                Children = [pair, more],
            };
        }

        static Element Cell(string key, string glyph, bool on, Action tap, string name, float height) => Controls.Named(new BoxEl
        {
            Key = key, Width = WinUiChoiceRules.ViewCellW, Height = height, Shrink = 0f,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.ControlAll,
            Fill = on ? Tok.FillSubtleSecondary : Tok.FillSubtleTransparent,
            HoverFill = on ? Tok.FillSubtleTertiary : Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            BrushTransitionMs = Design.Motion.Faster,
            Role = AutomationRole.RadioButton, Focusable = true, Cursor = CursorId.Hand, OnClick = tap,
            Children = [Icon(glyph, WinUiChoiceRules.ViewGlyph, on ? Tok.TextPrimary : Tok.TextSecondary)],
        }, name);
    }

    static readonly string[] s_viewKeys =
        [Strings.Library.View.CompactList, Strings.Library.View.List, Strings.Library.View.CompactGrid, Strings.Library.View.Grid];

    static readonly string[] s_sizeKeys =
        [Strings.Theme.Winui.Library.Sizes.Small, Strings.Theme.Winui.Library.Sizes.Medium, Strings.Theme.Winui.Library.Sizes.Large];

    /// <summary>The "..." menu, built at open from the live prefs: "View" and its four radio rows, then "Size" and its three (enabled for a grid only:
    /// a list has no size, and the rows stay in place so the menu never changes height).</summary>
    static IReadOnlyList<MenuFlyoutItem> ViewItems(Signal<int> view, Signal<int> size)
    {
        int v = view.Peek(), s = size.Peek();
        bool grid = v >= 2;
        var items = new List<MenuFlyoutItem>(s_viewKeys.Length + s_sizeKeys.Length + 3)
        {
            MenuFlyoutItem.Header(Loc.Get(Strings.Theme.Winui.Library.View)),
        };
        for (int i = 0; i < s_viewKeys.Length; i++)
        {
            int code = i;
            items.Add(MenuFlyoutItem.RadioItem(Loc.Get(s_viewKeys[i]), v == code, () => view.Value = code));
        }
        items.Add(MenuFlyoutItem.Separator);
        items.Add(MenuFlyoutItem.Header(Loc.Get(Strings.Theme.Winui.Library.Size)));
        for (int i = 0; i < s_sizeKeys.Length; i++)
        {
            int code = i;
            items.Add(MenuFlyoutItem.RadioItem(Loc.Get(s_sizeKeys[i]), s == code, () => size.Value = code, enabled: grid));
        }
        return items;
    }

    // ══ THE READER'S SCOPE AND SORT ══════════════════════════════════════════════════════════════════════════════════

    static Element Scope(LibraryScopeArgs a) => Embed.Comp(a, static () => new ScopeHost());

    /// <summary>"In your library | All releases · 42": the total re-renders the pivot when the facets answer (the SelectorBar's labels are re-pushed
    /// live); the narrow reader says "All · 42".</summary>
    sealed class ScopeHost : Component
    {
        public override Element Render()
        {
            var a = UseProps<LibraryScopeArgs>();
            int total = a.Total.Current();
            bool shortWord = a.Compact?.Invoke() ?? false;
            string all = Loc.Get(shortWord ? Strings.Theme.Winui.Library.Scope.AllShort : Strings.Theme.Winui.Library.Scope.AllReleases);
            string[] labels = [Loc.Get(Strings.Theme.Winui.Library.Scope.InLibrary), WinUiChoice.Counted(all, total > 0 ? FormatCache.Int(total) : "")];
            return new BoxEl
            {
                Key = "lib:scope", Direction = 0, MinWidth = 0f, Grow = 1f, Shrink = 1f, Basis = 0f,
                Children = [WinUiChoice.Pivot(labels, a.Scope, WinUiChoiceRules.PivotItemH)],
            };
        }
    }

    static readonly string[] s_readerSortKeys =
        [Strings.Theme.Winui.Library.ReaderSort.Newest, Strings.Theme.Winui.Library.ReaderSort.Oldest, Strings.Theme.Winui.Library.ReaderSort.Alphabetical];

    static Element ReaderSort(LibraryReaderSortArgs a) => Embed.Comp(a, static () => new ReaderSortHost());

    sealed class ReaderSortHost : Component
    {
        public override Element Render()
        {
            var a = UseProps<LibraryReaderSortArgs>();
            var sort = a.Sort;
            int current = Math.Clamp(sort.Value, 0, s_readerSortKeys.Length - 1);
            var items = new List<MenuFlyoutItem>(s_readerSortKeys.Length + 1) { MenuFlyoutItem.Header(Loc.Get(Strings.Theme.Winui.Library.SortBy)) };
            for (int i = 0; i < s_readerSortKeys.Length; i++)
            {
                int code = i;
                items.Add(MenuFlyoutItem.RadioItem(Loc.Get(s_readerSortKeys[i]), current == code, () => sort.Value = code));
            }
            return WinUiChoice.Sort("lib:reader-sort", FormatCache.Int(current), Loc.Get(s_readerSortKeys[current]), items, WinUiChoiceRules.ControlH,
                                    Loc.Get(Strings.Theme.Winui.Library.SortBy));
        }
    }
}
