// ── Themes/WinUi/Structure/Views/TitleBar/WinUiTitleBar.cs — WinUI's `Shell.TitleBar` (themes plan 6.2; P6 W2a) ─────────────────────────
//
// Boards Shell-Top, W-Tiers ("Title bar: tabs stay on in every tier"), W-Shell-Compact / Small / Huge, W-Narrow. The engine's two-row shell bar
// (EP16, `TitleBar.Rows`): the core owns the tab workspace, the history stacks, the auth fold, the pin and every verb (`ShellTitleBarProps`);
// this template draws, per window tier, what `WinUiTiers.TitleBar(tier)` says:
//
//   TABS ON (88)   row 1 (40, Mica Alt): the TabView strip (Chrome tabs, the tier's form: Ladder 240 / 200 / 160 -> 110 then scroll, or
//                  Compact's 140 selected + 36 icon tabs) and "+", the drag band, the caption buttons (row-tall)
//                  row 2 (48): ≡ · Back · Forward | the search (centred on the window at 520 / 640; filling the middle at Small; the
//                  magnifier at Compact) | pin · Friends · bell (badge) · avatar (+ the name at Huge); Small folds pin and Friends into ⋯
//   TABS OFF (48)  the toolbar alone, the captions at its end; after Forward the Wavee mark (not at Compact). The strip is hidden, not the
//                  tabs: the workspace is kept (`ShellCapabilities.HiddenTabStrip`) and comes back in place.
//
// Every interactive child is a keyed `TitleBarItem` (its own Client region; the gaps stay drag space, re-reported in the frame they move), the
// mark is non-interactive drag space. The bar's row count follows `ShowsTabStrip` live; the frame's slot eases its height (88 <-> 48), so the
// toolbar row never jumps. Self-updating content (the history buttons, the bell's badge, the pin, the chip) is its own component.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using ViewSlots = FluentGpu.Theming.Slots;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's two-row title bar.</summary>
internal static class WinUiTitleBar
{
    /// <summary>The registered template of <see cref="ViewKeys.ShellTitleBar"/>.</summary>
    public static Element Template(ShellTitleBarProps props, in ViewContext ctx)
    {
        var tiers = ctx.TierReader();
        var facts = TierContext.DrawnIn(ctx.Hooks.UseContext(ThemeFactsContext.Signal));
        return Embed.Comp(() => Bar(props, tiers, facts));
    }


    private static TitleBar Bar(ShellTitleBarProps props, Func<TierFacts> tiers, IReadSignal<ThemeFacts> facts)
    {
        WinUiTitleBarLayout Tier() => WinUiTiers.TitleBarOf(tiers());
        bool Strip() => facts.Value.Capabilities.ShowsTabStrip;

        var parts = new TemplateParts();
        // Row 1 is the Mica Alt strip (Shell-Top `strip`); the toolbar row lies on the window's Mica.
        parts[TitleBar.PartTopRow] = static b => b with { Key = WinUiFrameRules.TabRowKey, Fill = Tok.FillSolidBaseAlt };
        parts[TitleBar.PartToolbarRow] = static b => b with { Key = WinUiFrameRules.ToolbarKey };
        parts[TitleBar.PartPaneToggle] = static b => b with { Key = "winui.titlebar.pane" };

        var bar = new TitleBar
        {
            IconGlyph = "", ShowBackButton = false, ShowCaptionButtons = true, ShowRailBaseline = false,
            // The hamburger is the bar's own pane toggle (its tooltip and enabled state are the core's).
            ShowPaneToggle = facts.Peek().Capabilities.NavTree, OnPaneToggle = props.OnPaneToggle,
            PaneToggleEnabledSignal = props.PaneToggleEnabled, PaneToggleToolTip = props.PaneToggleTip,
            Rows = Prop.Of(() => Strip() ? 2 : 1),
            TopRowHeight = WinUiTiers.TabStripH,
            BarHeight = WinUiTiers.ToolbarH,
            // The strip's host decides, per render, which strip stands (the bar hands its tabs island a child it never re-pushes).
            Tabs = () => Embed.Comp(() => new StripHost(props, tiers)),
            TabsVersion = props.TabsVersion,
            ContentVersion = props.ContentVersion,
            Lead = () => LeadItems(props, Tier()),
            Wordmark = () => !Strip() && Tier().Mark ? Mark() : new BoxEl { Width = 0f, Height = 0f },
            TrailingItems = () => TrailItems(props, Tier()),
            CenterAlign = Prop.Of(() => Tier().AlignWith(Strip())),
            RowSpacing = () => new TitleBarSpacing(WinUiFrameRules.LeadPad, Tier().DragStrip),
            ToolbarSpacing = static () => new TitleBarToolbarSpacing(WinUiFrameRules.ToolGap, WinUiFrameRules.LeadInset, WinUiFrameRules.TrailInset),
            Parts = parts,
        };
        // The centre: the search FIELD at the tier's width (the magnifier is a trailing item at Compact). Its width reads the bar's measured
        // centre live, so a resize narrows a filling field without re-building the bar.
        var search = props.Search with { Form = SearchForm.Field, FieldWidth = () => FieldWidth(Tier(), bar.CenterAvail.Value) };
        bar.CenterContent = _ => Tier().SearchField
            ? ViewSlots.View(ViewKeys.ShellSearch, search) with { Key = "winui.titlebar.search-host" }
            : new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };
        return bar;
    }

    /// <summary>The field's width: the tier's (520 / 640, Small's widest), never wider than the centre the bar measured (the clusters always win:
    /// a crowded row narrows the field, it never overlaps them). Before the first measure (NaN or infinite), the tier's.</summary>
    private static float FieldWidth(WinUiTitleBarLayout tier, float avail)
        => float.IsFinite(avail) ? Math.Clamp(avail, 0f, tier.SearchW) : tier.SearchW;

    // ── the strip ────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static TabStrip BuildStrip(ShellTitleBarProps props, Func<TierFacts> tiers, bool touch) => new()
    {
        Parts = touch ? s_touchParts : s_ladderParts,
        // WinUI's TabView: Chrome tabs (the selected tab joins the toolbar's Mica), the tier's form against the bar's tab lane.
        Appearance = TabStripAppearance.Chrome,
        Form = () => WinUiTiers.TitleBarOf(tiers()).TabForm,
        TabHeight = touch ? WinUiFrameRules.CompactTabH : WinUiFrameRules.TabH,
        TextFontSize = WinUiFrameRules.MarkTextSize,
        SelectedFill = Prop.Of(static () => Tok.FillSolidBase),
        // Shell-Top: only the selected tab carries its close button at rest (the others show it under the pointer).
        CloseButtonOverlayMode = TabViewCloseButtonOverlayMode.OnPointerOver,
        IsAddTabButtonVisible = true,
        OnAddTabButtonClick = () => { props.OnNewTab(); return null; },
        ItemsSource = props.TabItems,
        ItemsVersion = props.TabItemsVersion,
        // The strip writes this cell before it raises OnSelectionChanged; the host re-asserts it from the model afterwards.
        SelectedIndex = props.TabSelection(),
        OnSelectionChanged = props.OnActivateTab,
        OnTabCloseRequested = props.OnCloseTab,
        ScrollMetricsChanged = m => props.OnTabExtent(m.ContentExtent),
    };

    /// <summary>The tabs island's child: nothing while the strip is hidden (a one-row bar would seat the island in the toolbar), the Compact
    /// touch strip (its tabs and "+" fill the 40 row: a crossing of 641 re-mounts it), or the ladder strip.</summary>
    private sealed class StripHost(ShellTitleBarProps props, Func<TierFacts> tiers) : Component
    {
        public override Element Render()
        {
            var facts = TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal)).Value;
            if (!facts.Capabilities.ShowsTabStrip) return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };
            // A keyed child of a box (a component's root is matched by type, never by key): the swap re-mounts the strip.
            Element strip = WinUiTiers.TitleBarOf(facts.Tiers).TouchStrip
                ? Embed.Comp(() => BuildStrip(props, tiers, touch: true)) with { Key = WinUiFrameRules.StripKey + ":touch" }
                : Embed.Comp(() => BuildStrip(props, tiers, touch: false)) with { Key = WinUiFrameRules.StripKey };
            return new BoxEl { Direction = 0, AlignItems = FlexAlign.Stretch, Shrink = 1f, MinWidth = 0f, Children = [strip] };
        }
    }

    /// <summary>The Compact strip's "+" and the selected tab's close button: the 40 x 40 touch target (the engine's close plate is 32 x 24, and the
    /// selected tab rides 1 DIP up its row, so a smaller box's slop was cut by the strip's clip to 40 x 39).</summary>
    private static readonly TemplateParts s_touchParts = TouchParts();

    /// <summary>The ladder strip's "+": a 32 square, the hit floor above Compact.</summary>
    private static readonly TemplateParts s_ladderParts = LadderParts();

    private static TemplateParts TouchParts()
    {
        var p = new TemplateParts();
        p[TabStrip.PartAddButton] = static b => b with { Width = WinUiFrameRules.CompactTabH, Height = WinUiFrameRules.CompactTabH };
        p[TabStrip.PartTabCloseButton] = static b => b with { Width = WinUiFrameRules.CompactTabH, Height = WinUiFrameRules.CompactTabH, Margin = default };
        return p;
    }

    private static TemplateParts LadderParts()
    {
        var p = new TemplateParts();
        p[TabStrip.PartAddButton] = static b => b with { Width = WinUiFrameRules.AddTarget, Height = WinUiFrameRules.AddTarget };
        return p;
    }

    // ── the toolbar's clusters ───────────────────────────────────────────────────────────────────────────────────────────

    private static IReadOnlyList<TitleBarItem> LeadItems(ShellTitleBarProps props, WinUiTitleBarLayout tier)
    {
        var back = new TitleBarItem("back", () => Embed.Comp(() => new HistoryButton(props, forward: false)) with { Key = WinUiFrameRules.BackKey });
        if (!tier.Forward) return [back];
        return [back, new TitleBarItem("forward", () => Embed.Comp(() => new HistoryButton(props, forward: true)) with { Key = WinUiFrameRules.ForwardKey })];
    }

    private static IReadOnlyList<TitleBarItem> TrailItems(ShellTitleBarProps props, WinUiTitleBarLayout tier)
    {
        var items = new List<TitleBarItem>(5);
        if (!tier.SearchField)
            items.Add(new TitleBarItem("search", () => ViewSlots.View(ViewKeys.ShellSearch, props.Search with { Form = SearchForm.Icon })));
        if (tier.PinAndFriends)
        {
            items.Add(new TitleBarItem("pin", () => Embed.Comp(() => new PinButton(props)) with { Key = WinUiFrameRules.PinKey }));
            items.Add(new TitleBarItem("friends", () => Embed.Comp(() => new FriendsButton(props)) with { Key = WinUiFrameRules.FriendsKey }));
        }
        // The bell's item carries Small's ⋯ after it (one keyed child): the ⋯ slot eases its width in and out, so the bell glides 44 at the 1008
        // edge instead of jumping (a keyed item of its own would come and go with its slot in one frame).
        items.Add(new TitleBarItem("bell", () => Embed.Comp(() => new BellCluster(props))));
        // The name joins the avatar at Huge (the core's chip, `ShowName`); the identity eases its width, so the cluster glides.
        items.Add(new TitleBarItem("avatar", () => Embed.Comp(() => new Identity(props)) with { Key = WinUiFrameRules.AvatarKey }));
        return items;
    }


    /// <summary>The Wavee mark (tabs off, Shell-Top `appMark`): a 16 accent square and the app's name in Caption, 8 after Forward.</summary>
    private static Element Mark() => new BoxEl
    {
        Key = WinUiFrameRules.MarkKey, Direction = 0, AlignItems = FlexAlign.Center, Gap = WinUiFrameRules.MarkGap,
        Padding = new Edges4(WinUiFrameRules.MarkLeadPad, 0f, WinUiFrameRules.MarkLeadPad, 0f), HitTestVisible = false,
        Children =
        [
            new BoxEl
            {
                Width = WinUiFrameRules.MarkSize, Height = WinUiFrameRules.MarkSize, Shrink = 0f,
                Corners = CornerRadius4.All(WinUiFrameRules.MarkRadius), Fill = Prop.Of(static () => Tok.AccentDefault),
            },
            R.Type.Wordmark(Loc.Get(Strings.Theme.Winui.Frame.AppName)),
        ],
    };

    // ── the self-updating children ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Back or Forward: the primary on click, the history flyout on right-click / touch-hold.</summary>
    private sealed class HistoryButton(ShellTitleBarProps props, bool forward) : Component
    {
        public override Element Render()
        {
            bool canDo = (forward ? props.CanForward : props.CanBack)();
            var anchor = UseRef<NodeHandle>(default);
            var handle = UseRef<OverlayHandle?>(null);
            var overlay = UseContext(Overlay.Service);
            var style = WinUiSearch.ToolStyleFor(TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal)).Value.Tiers);

            void OpenHistory(ContextRequestEventArgs _)
            {
                if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
                var items = props.HistoryMenu(forward);   // read at OPEN, never at mount
                if (items.Count == 0) return;
                var h = overlay.Open(() => anchor.Value,
                    () => ViewSlots.View(ViewKeys.ShellHistoryMenu, new HistoryMenuProps { Items = items, Close = () => handle.Value?.Close() }),
                    FlyoutPlacement.BottomEdgeAlignedLeft,
                    new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
                handle.Value = h;
                h.ClosedAction = () => handle.Value = null;
            }

            var button = IconButton.Create(forward ? Icons.Forward : Icons.Back, forward ? props.GoForward : props.GoBack, style, isEnabled: canDo)
                with { OnRealized = h => anchor.Value = h, OnContextRequested = OpenHistory };
            return ToolTip.Wrap(button, Loc.Get(forward ? Strings.Theme.Winui.Frame.Forward : Strings.Theme.Winui.Frame.Back));
        }
    }

    /// <summary>The bell (Shell-Top `toolRel`): the unread count as an accent badge over the button's top right, never its footprint.</summary>
    private sealed class BellButton(ShellTitleBarProps props) : Component
    {
        public override Element Render()
        {
            var overlay = UseContext(Overlay.Service);
            var anchor = UseRef<NodeHandle>(default);
            var handle = UseRef<OverlayHandle?>(null);
            int unread = props.BellUnread();

            void Toggle() => props.ToggleNotifications(overlay, () => anchor.Value, handle);

            var style = WinUiSearch.ToolStyleFor(TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal)).Value.Tiers);
            var button = IconButton.Create(Icons.Bell, Toggle, style) with { OnRealized = h => anchor.Value = h };
            float w = style.Size, h = style.Height ?? style.Size;
            BoxEl content = new()
            {
                ZStack = true, Width = w, Height = h, Shrink = 0f,
                Children =
                [
                    button,
                    new BoxEl
                    {
                        Width = w, Height = h, Direction = 1, Justify = FlexJustify.Start, HitTestVisible = false,
                        Children = unread <= 0 ? [] :
                        [
                            new BoxEl
                            {
                                Direction = 0, Justify = FlexJustify.End, Padding = WinUiFrameRules.BadgeMargin,
                                Children = [InfoBadge.Count(unread)],
                            },
                        ],
                    },
                ],
            };
            return ToolTip.Wrap(content, Loc.Get(Strings.Notifications.Title));
        }
    }

    /// <summary>The direct Pin / Unpin for the destination on screen; an unpinnable destination keeps the button's place, disabled.</summary>
    private sealed class PinButton(ShellTitleBarProps props) : Component
    {
        public override Element Render()
        {
            var pin = props.Pin();
            var style = WinUiSearch.ToolStyleFor(TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal)).Value.Tiers);
            if (pin is null) return IconButton.Create(Icons.Pin, static () => { }, style, isEnabled: false);
            return ToolTip.Wrap(IconButton.Create(pin.Glyph, pin.Invoke, style), pin.Label);
        }
    }

    /// <summary>Friends: toggles the friends pane.</summary>
    private sealed class FriendsButton(ShellTitleBarProps props) : Component
    {
        public override Element Render()
        {
            var style = WinUiSearch.ToolStyleFor(TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal)).Value.Tiers);
            return ToolTip.Wrap(IconButton.Create(Icons.Friends, props.ToggleFriends, style), Loc.Get(Strings.Shell.Friends));
        }
    }

    /// <summary>The bell and, at Small, the ⋯ after it (`WinUiFrameRules.MoreSlotMotion`): the ⋯ mounts in a slot that eases its width
    /// 0 <-> 44 (the cluster gap and the button) and fades, so the bell and everything left of it glide across the 1008 edge.</summary>
    private sealed class BellCluster(ShellTitleBarProps props) : Component
    {
        public override Element Render()
        {
            bool more = WinUiTiers.TitleBarOf(TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal)).Value.Tiers).More;
            Element bell = Embed.Comp(() => new BellButton(props)) with { Key = WinUiFrameRules.BellKey };
            if (!more) return new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f, Children = [bell] };
            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f,
                Children =
                [
                    bell,
                    new BoxEl
                    {
                        Key = WinUiFrameRules.MoreSlotKey, Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f,
                        Padding = new Edges4(WinUiFrameRules.ToolGap, 0f, 0f, 0f),
                        Animate = Design.Reduced ? null : WinUiFrameRules.MoreSlotMotion,
                        Children = [Embed.Comp(() => new MoreButton(props)) with { Key = WinUiFrameRules.MoreKey }],
                    },
                ],
            };
        }
    }

    /// <summary>Small's ⋯ (W-Tiers: "Pin and Friends move into ⋯"): a menu of the destination's pin and Friends, read at open.</summary>
    private sealed class MoreButton(ShellTitleBarProps props) : Component
    {
        public override Element Render()
        {
            var overlay = UseContext(Overlay.Service);
            var anchor = UseRef<NodeHandle>(default);
            var handle = UseRef<OverlayHandle?>(null);
            var style = WinUiSearch.ToolStyleFor(TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal)).Value.Tiers);

            void Open()
            {
                if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
                var rows = new List<MenuFlyoutItem>(2);
                if (props.Pin() is { } pin) rows.Add(new MenuFlyoutItem(pin.Label, pin.Glyph, true, pin.Invoke));
                rows.Add(new MenuFlyoutItem(Loc.Get(Strings.Shell.Friends), Icons.Friends, true, props.ToggleFriends));
                var h = overlay.Open(() => anchor.Value,
                    () => ViewSlots.View(ViewKeys.ShellHistoryMenu, new HistoryMenuProps { Items = rows, Close = () => handle.Value?.Close() }),
                    FlyoutPlacement.BottomEdgeAlignedRight,
                    new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
                handle.Value = h;
                h.ClosedAction = () => handle.Value = null;
            }

            return ToolTip.Wrap(IconButton.Create(Icons.More, Open, style) with { OnRealized = h => anchor.Value = h },
                Loc.Get(Strings.Theme.Winui.Frame.More));
        }
    }

    /// <summary>The identity from the auth fold (never raw session status): the avatar chip (the core host; the name beside it at Huge), a
    /// "Connecting" caption, or the accent Sign in / Reconnect.</summary>
    private sealed class Identity(ShellTitleBarProps props) : Component
    {
        public override Element Render() => new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f,
            Animate = Design.Reduced ? null : WinUiFrameRules.IdentityWidthMotion,
            Children = [Chip()],
        };

        Element Chip() => props.Layout().Chip switch
        {
            ChromeChipForm.Profile => props.ProfileChip(),
            ChromeChipForm.Connecting => new BoxEl
            {
                Height = WinUiFrameRules.AvatarSize, AlignItems = FlexAlign.Center,
                Children = [Caption(Loc.Get(Strings.Shell.Connecting)).Secondary()],
            },
            var form => Button.Accent(Loc.Get(form == ChromeChipForm.Reconnect ? Strings.Shell.Reconnect : Strings.Shell.SignIn), props.SignIn),
        };
    }
}
