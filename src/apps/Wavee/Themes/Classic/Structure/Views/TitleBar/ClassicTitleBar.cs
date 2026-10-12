// ── Themes/Classic/Structure/Views/TitleBar/ClassicTitleBar.cs — Classic's `Shell.TitleBar` (themes plan 6.1, T4b) ─────────────
//
// From `Shell.UI.cs`: `ChromeRow`, `TabsIsland`, `BuildTabStrip`, `CaptionLeadingIsland`, `TrailingIsland`, `ChromeButton`,
// `AuthChip`, `PinButton`, `NavHistoryButton` and `BellButton`. The core owns the allocation of the row (`Shell.Chrome`, the pressure
// allocator that takes `ChromePolicy`), the tab workspace's items and verbs, the auth fold, the history stacks and the pin; it hands
// this template one frozen props record (`ShellTitleBarProps`). The elements, keys, transitions and bound channels are what the shell
// built before the move, so Classic renders and behaves as it always did.
//
//   ONE 48-DIP engine TitleBar in merged mode:
//     [pane toggle][tabs island: back · forward · text-first tab strip][drag band][CENTRE: the omnibar field][drag band]
//     [trailing island: identity chip · bell · friends · pin][48 drag strip][caption-leading: theme toggle · search icon][min/max/close]
//
//   The drag regions and the three caption buttons are the engine control's: it reports the interactive islands as Client regions
//   (first match wins), the caption buttons as HTMIN / HTMAX / HTCLOSE and the whole bar as the Caption catch-all to the Win32 hit
//   test. The island builders below run INSIDE the bar's render (no hooks there; hook-owned behaviour lives in the child
//   components), and `ContentVersion` is the bar's memo key: every term an island reads is folded into it by the core, so an island
//   that changes size or shape re-pushes the regions in the same frame (dead drag space otherwise).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using ViewSlots = FluentGpu.Theming.Slots;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's merged title bar.</summary>
internal static class ClassicTitleBar
{
    /// <summary>The registered template of <see cref="ViewKeys.ShellTitleBar"/>.</summary>
    public static Element Template(ShellTitleBarProps props, in ViewContext ctx) => Embed.Comp(() =>
    {
        var bar = new TitleBar
        {
            IconGlyph = "", ShowBackButton = false, ShowCaptionButtons = true,
            ShowPaneToggle = true, OnPaneToggle = props.OnPaneToggle,
            PaneToggleEnabledSignal = props.PaneToggleEnabled, PaneToggleToolTip = props.PaneToggleTip,
            ShowRailBaseline = false,          // the seam below is the content region's own stroke
            Tabs = () => TabsIsland(props), TabsVersion = props.TabsVersion,
            TabsElasticLane = true,            // tabs absorb the overrun; the omnibar keeps its allocation
            Trailing = () => TrailingIsland(props), CaptionLeading = () => CaptionLeadingIsland(props),
            ContentVersion = props.ContentVersion,
        };
        bar.CenterContent = _ => CenterIsland(props, bar.CenterAvail);
        return bar;
    });

    // ── the strip instance ───────────────────────────────────────────────────────────────────────────────────────────

    private static readonly RootState<TabStrip?> s_stripState = new("Shell.TabStrip");
    /// <summary>The mounted tab strip (root-lifetime: the new root's strip is a new instance). The tabs island pokes its "+" slot
    /// from the allocation.</summary>
    private static TabStrip? s_strip { get => s_stripState.Value; set => s_stripState.Value = value; }

    // ── the metrics ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The island-button footprint: 40×44, the bar's own nav metric. A property: the default style captures theme brushes.</summary>
    private static IconButton.Style ChromeButtonStyle
        => IconButton.DefaultStyle with { Size = ClassicChromeLayout.ButtonSize, Height = ClassicChromeLayout.ButtonHeight };

    // ── the centre column ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The bar's flexible centre column: the field in Field mode, nothing in Icon mode (the icon sits in the
    /// caption-leading island). <paramref name="avail"/> is the bar's LIVE centre measurement.
    /// <para>This is the ONE mount/unmount decision for the field, and it reads the same search form the allocator used to decide
    /// the FORM (the form-vs-width split, #88), never a width. It is, however, only re-evaluated when the engine's
    /// <c>TitleBar</c> re-invokes the bar's centre content, which is gated on <c>ContentVersion</c>, not on every allocation publish;
    /// a mode flip can therefore be visible to the already-mounted field's own <c>Render()</c> a frame before it reaches here. That
    /// is why the field asks only for a FIELD width (<see cref="ShellSearchProps.FieldWidth"/>) and never for "whichever form is
    /// current": the still-mounted field can never be laid out at the icon's 44.</para></summary>
    private static Element CenterIsland(ShellTitleBarProps props, IReadSignal<float> avail)
        => props.Layout().Search == SearchForm.Field
            ? ViewSlots.View(ViewKeys.ShellSearch, props.Search with { Form = SearchForm.Field, Avail = avail }) with { Key = "chrome-search-host" }
            : new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };

    // ── the tabs island ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The tabs island takes a RESERVED, quantised width (issue #88): hugging the strip shoved the centred search by half of
    /// every title swing.</summary>
    private static Element TabsIsland(ShellTitleBarProps props)
    {
        var l = props.Layout();
        var kids = new List<Element>(3);
        if (l.ShowBack)
        {
            kids.Add(Embed.Comp(() => new NavHistoryButton(props, forward: false)) with { Key = "chrome-back" });
        }
        if (l.ShowForward)
        {
            kids.Add(Embed.Comp(() => new NavHistoryButton(props, forward: true)) with { Key = "chrome-forward" });
        }
        if (s_strip is { } strip) strip.IsAddTabButtonVisible = l.ShowNewTab;
        kids.Add(Embed.Comp(() => BuildTabStrip(props)) with { Key = "chrome-tab-strip" });
        return new BoxEl
        {
            Key = "chrome-tabs-lane", Animate = ClassicChromeLayout.TabLaneMotion,
            Direction = 0, AlignItems = FlexAlign.Center, Height = TitleBar.ExpandedHeight,
            Width = l.TabIslandWidth, Shrink = 0f, ClipToBounds = true, Children = kids.ToArray(),
        };
    }

    private static TabStrip BuildTabStrip(ShellTitleBarProps props)
    {
        var strip = new TabStrip
        {
            // TEXT-FIRST tabs: weight + opacity carry selection, one sliding 2-DIP accent underline marks it.
            Appearance = TabStripAppearance.Text,
            OverflowMode = TabStripOverflowMode.Scroll,
            TextFontSize = ClassicChromeLayout.TabTextSize,
            // The "+" is hover-only, but its 32-DIP slot is reserved at all times so the reported client rect never moves.
            IsAddTabButtonVisible = props.PeekLayout().ShowNewTab,
            AddButtonVisibility = TabStripAddButtonVisibility.OnStripPointerOver,
            OnAddTabButtonClick = () => { props.OnNewTab(); return null; },
            IndicatorFill = Prop.Of(static () => Tok.AccentDefault),
            MinTabWidth = ShellPolicy.Now.Chrome.Budget.TabMinW,
            MaxTabWidth = ShellPolicy.Now.Chrome.Budget.TabMaxW,
            ItemsSource = props.TabItems,
            ItemsVersion = props.TabItemsVersion,
            // CONTRACT: the strip WRITES this cell before raising OnSelectionChanged, so it is never evidence of a change —
            // ActivateTab asks the workspace, and the host re-asserts the cell from the model afterwards.
            SelectedIndex = props.TabSelection(),
            OnSelectionChanged = props.OnActivateTab,
            OnTabCloseRequested = props.OnCloseTab,
            ScrollMetricsChanged = m => props.OnTabExtent(m.ContentExtent),
        };
        s_strip = strip;   // the factory runs once per mount, so the newest instance IS the mounted one
        return strip;
    }

    // ── the caption-leading and trailing islands ─────────────────────────────────────────────────────────────────────

    private static Element ChromeButton(string glyph, Action onClick, string tooltip, string key)
        => ToolTip.Wrap(IconButton.Create(glyph, onClick, ChromeButtonStyle) with { Key = key, Margin = ClassicChromeLayout.ButtonMargin }, tooltip);

    private static Element CaptionLeadingIsland(ShellTitleBarProps props)
    {
        var l = props.Layout();
        bool dark = Theme.Dark;
        var theme = ToolTip.Wrap(
            IconButton.Create(dark ? Icons.Sun : Icons.Moon, props.ToggleTheme, ChromeButtonStyle)
                with { Key = "chrome-theme-toggle", Margin = ClassicChromeLayout.ButtonMargin },
            Loc.Get(dark ? Strings.Shell.LightTheme : Strings.Shell.DarkTheme));
        return new BoxEl
        {
            Direction = 0, Height = TitleBar.ExpandedHeight, Shrink = 0f, AlignItems = FlexAlign.Center,
            Children = l.Search == SearchForm.Icon ? [theme, SearchFlyoutButton(props)] : [theme],
        };
    }

    private static Element SearchFlyoutButton(ShellTitleBarProps props)
        => ViewSlots.View(ViewKeys.ShellSearch, props.Search with { Form = SearchForm.Icon }) with { Key = "chrome-search-button-host" };

    private static Element TrailingIsland(ShellTitleBarProps props)
    {
        var l = props.Layout();
        if (!l.ShowTrailing) return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };
        var kids = new List<Element>(5) { AuthChip(props, l.Chip) };
        // The ONE "actions in row" stage: bell, friends and pin enter together. Below it they fold into the profile menu
        // (pin simply drops — the tab menu still offers it).
        if (l.ShowActions)
        {
            kids.Add(Embed.Comp(() => new BellButton(props)) with { Key = "chrome-bell" });
            kids.Add(ChromeButton(Icons.Friends, props.ToggleFriends, Loc.Get(Strings.Shell.Friends), "chrome-friends"));
            kids.Add(PinButton(props));
        }
        return new BoxEl
        {
            Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center, Height = TitleBar.ExpandedHeight,
            Children = kids.ToArray(),
        };
    }

    /// <summary>The identity form from the AUTH FOLD, never raw session status: a silent resume behind the cache-first
    /// shell is Connecting, and an actionable "Sign in" there would race it (ch 18 W17).
    /// <para>The form comes from the RESOLVED row (<c>ChromeRowState.Chip</c>), not from the session directly, for the same
    /// reason <see cref="PinButton"/> reserves its 44 DIP unconditionally: the element tree must never be a form the
    /// budget did not price. The allocator reads the same fold, so the two can only ever agree. (#88)</para></summary>
    private static Element AuthChip(ShellTitleBarProps props, ChromeChipForm chip) => chip switch
    {
        ChromeChipForm.Profile => props.ProfileChip() with { Key = "chrome-profile" },
        ChromeChipForm.Connecting => new BoxEl
        {
            Key = "chrome-connecting", Height = ClassicChromeLayout.ChipHeight, AlignItems = FlexAlign.Center,
            Padding = ClassicChromeLayout.ConnectingChipPadding,
            Children = [Caption(Loc.Get(Strings.Shell.Connecting)).Secondary()],
        },
        // Offline = a credential is on disk but the resume failed: the verb is "try again", not "sign in".
        var form => Button.Accent(Loc.Get(form == ChromeChipForm.Reconnect ? Strings.Shell.Reconnect : Strings.Shell.SignIn),
            props.SignIn) with { Key = "chrome-sign-in" },
    };

    /// <summary>The direct Pin/Unpin for the destination on screen. Its 44 DIP are budgeted unconditionally (issue #88), so
    /// an unpinnable destination gets an invisible placeholder of the same width — the tree stays honest to the budget.</summary>
    private static Element PinButton(ShellTitleBarProps props)
    {
        if (props.Pin() is not { } pin)
            return new BoxEl
            {
                Key = "chrome-pin-placeholder", Width = ShellPolicy.Now.Chrome.Budget.NavButtonW, Height = TitleBar.ExpandedHeight,
                Shrink = 0f, HitTestVisible = false, Opacity = 0f,
            };
        return ChromeButton(pin.Glyph, pin.Invoke, pin.Label, "chrome-pin");
    }

    /// <summary>Back or Forward: the primary on click, the history flyout on right-click / touch-hold.</summary>
    private sealed class NavHistoryButton(ShellTitleBarProps props, bool forward) : Component
    {
        public override Element Render()
        {
            bool canDo = (forward ? props.CanForward : props.CanBack)();
            var anchor = UseRef<NodeHandle>(default);
            var handle = UseRef<OverlayHandle?>(null);
            var overlay = UseContext(Overlay.Service);

            void OpenHistory(ContextRequestEventArgs _)
            {
                if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
                var items = props.HistoryMenu(forward);   // read at OPEN, never at mount
                if (items.Count == 0) return;             // an empty stack opens nothing at all
                var h = overlay.Open(() => anchor.Value, () => ViewSlots.View(ViewKeys.ShellHistoryMenu, new HistoryMenuProps { Items = items, Close = () => handle.Value?.Close() }),
                    FlyoutPlacement.BottomEdgeAlignedLeft,
                    new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
                handle.Value = h;
                h.ClosedAction = () => handle.Value = null;
            }

            return IconButton.Create(forward ? Icons.Forward : Icons.Back, forward ? props.GoForward : props.GoBack,
                    ChromeButtonStyle, isEnabled: canDo)
                with { Margin = ClassicChromeLayout.ButtonMargin, OnRealized = h => anchor.Value = h, OnContextRequested = OpenHistory };
        }
    }

    /// <summary>The trailing island's bell: the unread pill over the button's own box (never its footprint).</summary>
    private sealed class BellButton(ShellTitleBarProps props) : Component
    {
        public override Element Render()
        {
            var overlay = UseContext(Overlay.Service);
            var anchor = UseRef<NodeHandle>(default);
            var handle = UseRef<OverlayHandle?>(null);
            // presentation mode (the Store shot): the account's unread count is account data, so no badge
            int unread = props.BellUnread();

            void Toggle() => props.ToggleNotifications(overlay, () => anchor.Value, handle);

            var style = ChromeButtonStyle;
            var button = IconButton.Create(Icons.Bell, Toggle, style) with { OnRealized = h => anchor.Value = h };
            float w = style.Size, hgt = style.Height ?? style.Size;
            BoxEl content = unread <= 0 ? button : new BoxEl
            {
                ZStack = true, Width = w, Height = hgt, Shrink = 0f,
                Children =
                [
                    button,
                    new BoxEl
                    {
                        Width = w, Height = hgt, Direction = 1, Justify = FlexJustify.Start, HitTestVisible = false,
                        Children = [new BoxEl { Direction = 0, Justify = FlexJustify.End, Children = [InfoBadge.Count(unread)] }],
                    },
                ],
            };
            return ToolTip.Wrap(content with { Margin = ClassicChromeLayout.ButtonMargin }, Loc.Get(Strings.Notifications.Title));
        }
    }
}
