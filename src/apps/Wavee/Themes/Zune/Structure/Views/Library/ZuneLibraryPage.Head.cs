// ── Themes/Zune/Structure/Views/Library/ZuneLibraryPage.Head.cs — the library's head: sort, view, find, count (themes plan 6.3; P5 Z7) ───────
//
// Boards Z-Library-*: one row of WORDS under the band, 28 tall, 24 between groups, 13 type: "sort" and the kind's words (the lit one primary; a re-tap of it flips the
// direction and a chevron says which way), a rule, "view" with grid and list (and a "…" menu for the compact variants and the S/M/L steps), then at the end the find
// field (a bottom rule, no plate) and the count. Podcasts' first group is its mode words (followed shows · your episodes). Small draws the sort as ONE button with its
// menu (the active word and a chevron); Compact stacks the head in two 40-tall rows (every control a target). The count is a BOUND text: a row landing re-fires one
// property instead of the head.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using K = Wavee.Themes.Zune.ZuneLibraryKit;
using LR = Wavee.Themes.Zune.ZuneLibraryRules;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Zune;

internal static partial class ZuneLibraryPage
{
    static readonly string[] s_noSuggest = [];

    /// <summary>The head. ONE tree per arm; the tier record's heights and the sort's form are live (the sort button and the kind's words swap under the shared cross-fade).</summary>
    static Element Head(LibraryPageProps p, PageCache c)
    {
        var l = c.Layout;
        bool saved = p.SavedEpisodesMode;
        Element lead = Lead(p, l);
        Element? find = saved ? null : Find(p, l);
        Element? count = saved ? null : Count(p);
        Element? view = saved ? null : Embed.Comp(l, () => new ViewWords(p.View(), p.Size())) with { Key = "lib:view" };

        if (l.Single)
        {
            // Compact: row one the sort and the view, row two the find field and the count; every control 40.
            var first = new List<Element>(3) { lead, new BoxEl { Grow = 1f, MinWidth = 0f } };
            if (view is not null) first.Add(view);
            var second = new List<Element>(2);
            if (find is not null) second.Add(find);
            if (count is not null) second.Add(count);
            var rows = new List<Element>(2)
            {
                new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = l.HeadGap, MinWidth = 0f, Height = l.HeadH, Children = first.ToArray() },
            };
            if (second.Count > 0)
                rows.Add(new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = l.HeadGap, MinWidth = 0f, Height = l.HeadH, Children = second.ToArray() });
            return new BoxEl { Key = LR.HeadKey, Direction = 1, Gap = l.HeadGap, Shrink = 0f, MinWidth = 0f, Children = rows.ToArray() };
        }

        var kids = new List<Element>(6) { lead };
        if (view is not null) { kids.Add(K.Divider()); kids.Add(view); }
        kids.Add(new BoxEl { Grow = 1f, MinWidth = 0f });
        if (find is not null) kids.Add(find);
        if (count is not null) kids.Add(count);
        return new BoxEl
        {
            Key = LR.HeadKey, Direction = 0, Height = l.HeadH, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = LR.GroupGap, MinWidth = 0f,
            Children = kids.ToArray(),
        };
    }

    /// <summary>The head's first group: Podcasts' mode words, the sort button (Small, Compact), or the kind's sort words. The button and the words are different trees in ONE
    /// slot: a crossing swaps them under the shared cross-fade in a slot that RESERVES the row's height, so nothing under it jumps. Only the kinds that can show either form swap
    /// (Podcasts keeps its mode words at every tier).</summary>
    static Element Lead(LibraryPageProps p, ZuneLibraryLayout l)
    {
        if (p.IsPodcasts)
            return Embed.Comp(l, () => new ModeWords(p.PodcastsMode())) with { Key = "lib:lead:mode" };
        bool menu = l.SortMenu;
        Element form = menu
            ? Embed.Comp(l, () => new SortButton(p.Entity, p.Sort(), p.Desc())) with { Key = "lib:lead:button" }
            : Embed.Comp(l, () => new SortWords(p.Entity, p.Sort(), p.Desc())) with { Key = "lib:lead:words" };
        // The slot's ONE child is an unkeyed column that holds the keyed form: the engine reconciles a slot's lone child in place (key ignored), which would patch the
        // button into the words instead of swapping them; a children LIST matches by key, so the leaving form exits and the entering one enters.
        return new BoxEl
        {
            Key = "lib:lead", Direction = 1, MinWidth = 0f, MinHeight = l.HeadH, Shrink = 0f,
            Children =
            [
                new BoxEl
                {
                    Direction = 1, MinWidth = 0f,
                    Children = [form with { Enter = ZuneMotion.SwapFade, Exit = ZuneMotion.SwapFade }],
                },
            ],
        };
    }

    /// <summary>The find field: a plain field with the magnifier, the library's filter signal. A bottom rule in the board; the engine's field is the Zune control style's.</summary>
    static Element Find(LibraryPageProps p, ZuneLibraryLayout l)
        => new BoxEl
        {
            Key = "lib:find", Direction = 0, AlignItems = FlexAlign.Center, Shrink = 1f, MinWidth = 0f, Basis = LR.FindW, Grow = l.Single ? 1f : 0f,
            Children =
            [
                AutoSuggestBox.Create(s_noSuggest, Loc.Get(Strings.Library.Filter), text: p.Filter(), queryIcon: Icons.Search, grow: 1f,
                    maxFillWidth: LR.FindW, minHeight: l.HeadH, cornerRadius: R.Shape.For(ShapeRole.Control)),
            ],
        };

    /// <summary>The count as ONE bound text (the host's "312 albums").</summary>
    static Element Count(LibraryPageProps p) => R.Type.DenseMeta("") with
    {
        Text = p.CountText, Color = Tok.TextTertiary, MaxLines = 1, Wrap = TextWrap.NoWrap, Shrink = 0f,
    };

    // ══ THE SORT WORDS ═══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>"sort" and the kind's words in rail order, each carrying its PERSISTED code (codes are not positions). The lit word is the clamped signal; tapping it flips the
    /// direction (a chevron after it says which way); another word resets to ascending. The signals are the page's INSTANCES, read here so the words follow them.</summary>
    sealed class SortWords(EntityKind kind, Signal<int> sort, Signal<bool> desc) : Component
    {
        public override Element Render()
        {
            float height = UseProps<ZuneLibraryLayout>().HeadH;
            var current = LibraryWordRail.Clamp(kind, sort.Value);
            bool descending = desc.Value;
            var codes = LibraryWordRail.WordsFor(kind);
            var kids = new Element[codes.Length + 1];
            kids[0] = K.LabelText(Loc.Get(Strings.Theme.Zune.Collection.Library.Sort));
            for (int i = 0; i < codes.Length; i++)
            {
                var word = codes[i];
                int code = (int)word;
                bool on = word == current;
                kids[i + 1] = K.Word("lib:sort:" + code.ToString(System.Globalization.CultureInfo.InvariantCulture), Loc.Get(LibraryWordRail.WordKey(word)), on, () =>
                {
                    if (code == (int)LibraryWordRail.Clamp(kind, sort.Peek())) desc.Value = !desc.Peek();
                    else { sort.Value = code; desc.Value = false; }
                }, height, on ? (descending ? Icons.ChevronUp : Icons.ChevronDown) : null);
            }
            return new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = LR.WordGap, MinWidth = 0f, Shrink = 0f, Children = kids };
        }
    }

    /// <summary>The sort as ONE button (the board's "recently added ▾"): the active word and a chevron saying which way, opening a radio menu of the kind's words. Picking the
    /// active word flips the direction, any other resets it to ascending, exactly as the words do.</summary>
    sealed class SortButton(EntityKind kind, Signal<int> sort, Signal<bool> desc) : Component
    {
        NodeHandle _anchor;
        OverlayHandle? _handle;

        public override Element Render()
        {
            var overlay = UseContext(Overlay.Service);
            float height = UseProps<ZuneLibraryLayout>().HeadH;
            var current = LibraryWordRail.Clamp(kind, sort.Value);
            bool descending = desc.Value;

            void Flyout()
            {
                if (Controls.IsNullOverlay(overlay)) return;
                if (_handle is { IsOpen: true } open) { open.Close(); return; }
                _handle = overlay.Open(
                    () => _anchor,
                    () => MenuFlyout.Create(Items(), () => _handle?.Close(), size: OverlayFit.MenuSize),
                    FlyoutPlacement.BottomEdgeAlignedLeft,
                    new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup) { ConstrainToRootBounds = false });
                _handle.ClosedAction = () => _handle = null;
            }

            return new BoxEl
            {
                Key = "lib:sort", Direction = 0, AlignItems = FlexAlign.Center, Gap = LR.WordChevronGap, Shrink = 0f, Height = height,
                Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, OnClick = Flyout, OnRealized = h => _anchor = h,
                Children =
                [
                    K.WordText(Loc.Get(LibraryWordRail.WordKey(current)), active: true),
                    Icon(descending ? Icons.ChevronUp : Icons.ChevronDown, LR.ChevronGlyph, Tok.TextTertiary),
                ],
            };
        }

        IReadOnlyList<MenuFlyoutItem> Items()
        {
            var words = LibraryWordRail.WordsFor(kind);
            var items = new MenuFlyoutItem[words.Length];
            int active = (int)LibraryWordRail.Clamp(kind, sort.Peek());
            for (int i = 0; i < items.Length; i++)
            {
                var word = words[i];
                int code = (int)word;
                items[i] = MenuFlyoutItem.RadioItem(Loc.Get(LibraryWordRail.WordKey(word)), code == active, () =>
                {
                    if (code == (int)LibraryWordRail.Clamp(kind, sort.Peek())) desc.Value = !desc.Peek();
                    else { sort.Value = code; desc.Value = false; }
                });
            }
            return items;
        }
    }

    /// <summary>Podcasts' two mode words (followed shows · your episodes); the selection is the persisted signal, read here so the words follow it.</summary>
    sealed class ModeWords(Signal<int> mode) : Component
    {
        public override Element Render()
        {
            float height = UseProps<ZuneLibraryLayout>().HeadH;
            int selected = mode.Value;
            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = LR.WordGap, MinWidth = 0f, Shrink = 0f,
                Children =
                [
                    K.Word("lib:mode:0", Loc.Get(Strings.Podcast.Reader.FollowedShows), selected == 0, () => mode.Value = 0, height),
                    K.Word("lib:mode:1", Loc.Get(Strings.Podcast.Reader.YourEpisodes), selected == 1, () => mode.Value = 1, height),
                ],
            };
        }
    }

    // ══ THE VIEW WORDS ═══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>"view", then grid and list (each keeps whichever compactness the persisted code already carried), then a "…" opening the four views and the S/M/L steps as a
    /// radio menu. View codes stay 0..3 and stay persisted.</summary>
    sealed class ViewWords(Signal<int> view, Signal<int> size) : Component
    {
        NodeHandle _anchor;
        OverlayHandle? _handle;

        public override Element Render()
        {
            var overlay = UseContext(Overlay.Service);
            float height = UseProps<ZuneLibraryLayout>().HeadH;
            int v = view.Value;   // subscribe: the lit word follows the persisted code
            _ = size.Value;
            bool grid = User.IsGridView(v);

            void Flyout()
            {
                if (Controls.IsNullOverlay(overlay)) return;
                if (_handle is { IsOpen: true } open) { open.Close(); return; }
                _handle = overlay.Open(
                    () => _anchor,
                    () => MenuFlyout.Create(Items(), () => _handle?.Close(), size: OverlayFit.MenuSize),
                    FlyoutPlacement.BottomEdgeAlignedRight,
                    new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup) { ConstrainToRootBounds = false });
                _handle.ClosedAction = () => _handle = null;
            }

            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = LR.WordGap, MinWidth = 0f, Shrink = 0f, OnRealized = h => _anchor = h,
                Children =
                [
                    K.LabelText(Loc.Get(Strings.Theme.Zune.Collection.Library.View)),
                    K.Word("lib:view:grid", Loc.Get(Strings.Library.View.Grid), grid, () => view.Value = User.IsCompactView(view.Peek()) ? 2 : 3, height),
                    K.Word("lib:view:list", Loc.Get(Strings.Library.View.List), !grid, () => view.Value = User.IsCompactView(view.Peek()) ? 0 : 1, height),
                    new BoxEl
                    {
                        Key = "lib:view:more", Width = Math.Max(LR.ViewMoreEdge, height), Height = height, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                        Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, OnClick = Flyout,
                        Children = [Icon(Icons.More, LR.ViewMoreGlyph, Tok.TextSecondary)],
                    },
                ],
            };
        }

        IReadOnlyList<MenuFlyoutItem> Items()
        {
            int v = view.Peek(), s = size.Peek();
            bool grid = User.IsGridView(v);
            string[] views = [Strings.Library.View.CompactList, Strings.Library.View.List, Strings.Library.View.CompactGrid, Strings.Library.View.Grid];
            string[] sizes = [Strings.Theme.Zune.Collection.Library.SizeSmall, Strings.Theme.Zune.Collection.Library.SizeMedium, Strings.Theme.Zune.Collection.Library.SizeLarge];
            var items = new List<MenuFlyoutItem>(views.Length + sizes.Length + 1);
            for (int i = 0; i < views.Length; i++)
            {
                int code = i;
                items.Add(MenuFlyoutItem.RadioItem(Loc.Get(views[i]), v == code, () => view.Value = code));
            }
            items.Add(MenuFlyoutItem.Separator);
            for (int i = 0; i < sizes.Length; i++)
            {
                int step = i;
                items.Add(MenuFlyoutItem.RadioItem(Loc.Get(sizes[i]), s == step, () => size.Value = step, enabled: grid));
            }
            return items;
        }
    }
}
