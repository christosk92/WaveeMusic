// ── Themes/Zune/Structure/Views/TitleBar/ZuneTitleBar.cs — Zune's `Shell.TitleBar` (themes plan 6.3; P5 Z2a) ──────────────────────────
//
// Board Z-Part-TitleBar (and the bars Z-Frame-Home, Z-Frame-Panel and Z-Frame-Narrow draw). ONE engine `TitleBar` in merged mode, 48 tall
// (56 at Huge 4K, `ZuneFrameLayout.TitleBarH`):
//
//     [wordmark · Back · the trail]  [drag band]  [the search · the account · the bell]  [drag strip]  [min/max/close]
//
//   · LEAD (the bar's tabs island, the row's elastic lane): the wordmark in the secondary ink, Back (40 x 40; its history menu opens on
//     right-click or touch-hold where the capabilities offer it), and the trail from the page's chrome (`ZuneFrameContext.PageChrome`): the
//     crumbs in the tertiary ink with the current page in the secondary one, never at Compact. The lead starts on the page's gutter
//     (16 / 24 / 48 / 48 / 64). When the row is short the trail gives way first, then the wordmark; Back never does.
//   · TRAILING: the search (the pill from Standard up, 220 / 320 / 400, growing while focused; the magnifier below), the account (the core's
//     chip host; "Connecting" as a caption; "Sign in" / "Reconnect" as the accent button) and, on the pill tiers, the bell with the accent dot.
//   · NOT HERE, by Zune's capabilities (`ThemeFactsContext`): no pane toggle (NavTree false; the engine part is keyed so a test can hold it
//     to that), no tabs and no tab count (Tabs false), no Forward (ForwardButton false). The friends toggle and the pin are not drawn either:
//     friends is a pivot of the panel, the pins are the band's.
//
// The bar re-renders on a window-tier crossing (the tier is folded into its content version) and on a navigation (the trail is folded into
// its tabs version), so the non-client regions are re-pushed in the same frame either way. The engine control owns the drag regions and
// the three caption buttons.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using ViewSlots = FluentGpu.Theming.Slots;

namespace Wavee.Themes.Zune;

/// <summary>Zune's title bar.</summary>
internal static class ZuneTitleBar
{
    /// <summary>The registered template of <see cref="ViewKeys.ShellTitleBar"/>.</summary>
    public static Element Template(ShellTitleBarProps props, in ViewContext ctx)
    {
        var tiers = ctx.TierReader();
        var chrome = ctx.Hooks.UseContext(ZuneFrameContext.PageChrome);
        var facts = TierContext.DrawnIn(ctx.Hooks.UseContext(ThemeFactsContext.Signal));
        return Embed.Comp(() => Bar(props, tiers, chrome, facts));
    }

    private static TitleBar Bar(ShellTitleBarProps props, Func<TierFacts> tiers, IReadSignal<PageChrome?>? chrome, IReadSignal<ThemeFacts> facts)
    {
        var caps = facts.Peek().Capabilities;
        var parts = new TemplateParts();
        // Never mounted under Zune (NavTree is false); keyed so a regression that brings it back is visible to the tests.
        parts[TitleBar.PartPaneToggle] = static b => b with { Key = ZuneFrameRules.PaneToggleKey };
        return new TitleBar
        {
            IconGlyph = "", ShowBackButton = false, ShowCaptionButtons = true, ShowRailBaseline = false,
            // ZK hand-off: the pane toggle follows the capabilities, never a hard-coded true.
            ShowPaneToggle = caps.NavTree, OnPaneToggle = props.OnPaneToggle,
            PaneToggleEnabledSignal = props.PaneToggleEnabled, PaneToggleToolTip = props.PaneToggleTip,
            Tabs = () => LeadIsland(props, tiers, chrome, facts),
            // The trail is part of the lead island's shape: a navigation re-renders the bar and re-pushes its regions.
            TabsVersion = () => HashCode.Combine(props.TabsVersion(), chrome?.Value),
            TabsElasticLane = true,
            Trailing = () => TrailingIsland(props, tiers),
            // The tier is part of every island's shape (pill or magnifier, the bell, the gaps): a crossing re-renders the bar.
            ContentVersion = () => HashCode.Combine(props.ContentVersion(), (int)tiers().Window),
            RowSpacing = () => ZuneFrameRules.Spacing(tiers().Window),
            BarHeight = Prop.Of(() => ZuneTiers.FrameOf(tiers()).TitleBarH),
            Parts = parts,
        };
    }

    /// <summary>A title-bar button: 40 x 40, square, the chrome wash on hover, still under the pointer (Metro answers with the wash).</summary>
    internal static IconButton.Style ButtonStyle(ColorF ink) => IconButton.DefaultStyle with
    {
        Size = ZuneFrameRules.Button, Height = ZuneFrameRules.Button, CornerRadius = R.Shape.Control,
        Foreground = ink, HoverForeground = Tok.TextPrimary, PressedForeground = Tok.TextSecondary,
        Fill = ColorF.Transparent, HoverFill = R.Layer.ChromeHover, PressedFill = R.Layer.ChromePressed,
        IconHoverScale = R.Motion.ScaleSubtle.HoverTarget, IconPressScale = R.Motion.ScaleSubtle.PressTarget,
    };

    // ── the lead island ──────────────────────────────────────────────────────────────────────────────────────────────────

    private static Element LeadIsland(ShellTitleBarProps props, Func<TierFacts> tiers, IReadSignal<PageChrome?>? chrome, IReadSignal<ThemeFacts> facts)
    {
        var t = tiers();
        var rec = ZuneFrameRules.TitleBarOf(t);
        var kids = new List<Element>(3)
        {
            R.Type.Wordmark(Loc.Get(Strings.Theme.Zune.Frame.Wordmark)) with
            {
                Key = ZuneFrameRules.WordmarkKey, Color = Tok.TextSecondary, Case = R.Case.For(LabelKind.Label),
                MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
            },
            Embed.Comp(() => new BackButton(props, facts)) with { Key = ZuneFrameRules.BackKey },
        };
        // The tabs version subscribed the bar to the chrome: a peek here.
        if (rec.Crumbs && chrome?.Peek() is { } page && page.Crumbs.Count >= ZuneFrameRules.CrumbMinDepth)
            kids.Add(Trail(page.Crumbs, rec));
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = rec.Gap, Shrink = 1f, MinWidth = 0f, ClipToBounds = true,
            Height = ZuneTiers.FrameOf(t).TitleBarH,
            Children = kids.ToArray(),
        };
    }

    /// <summary>The trail: every crumb, a tertiary <c>›</c> between two; the ancestors are links in the tertiary ink, the current page (the
    /// last crumb) in the secondary one. Names keep their own casing. The trail gives way (ellipsised, last crumb first) before Back.</summary>
    private static Element Trail(IReadOnlyList<PageCrumb> crumbs, ZuneTitleBarLayout rec)
    {
        var kids = new Element[crumbs.Count * 2 - 1];
        for (int i = 0; i < crumbs.Count; i++)
        {
            var crumb = crumbs[i];
            bool current = i == crumbs.Count - 1;
            if (i > 0) kids[i * 2 - 1] = Words(rec, "›") with { Color = Tok.TextTertiary, Shrink = 0f };
            var words = Words(rec, crumb.Label) with
            {
                Color = current ? Tok.TextSecondary : Tok.TextTertiary, HoverColor = current ? Tok.TextSecondary : Tok.TextPrimary,
                MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
            };
            kids[i * 2] = current || crumb.Go is not { } go
                ? words
                : new BoxEl
                {
                    Key = "zune-crumb:" + i, Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = go,
                    MinWidth = 0f, Shrink = 1f, Children = [words],
                };
        }
        return new BoxEl
        {
            Key = ZuneFrameRules.CrumbsKey, Direction = 0, AlignItems = FlexAlign.Center, Gap = ZuneFrameRules.CrumbGap,
            Shrink = 1f, MinWidth = 0f, ClipToBounds = true, Children = kids,
            // The trail comes and goes at the 640 crossing (never at Compact): it fades, never pops (ZT1).
            Enter = ZuneMotion.SwapFade, Exit = ZuneMotion.SwapFade,
        };
    }

    private static TextEl Words(ZuneTitleBarLayout rec, string text) => rec.LargeCrumbs ? R.Type.TrackTitle(text) : R.Type.DenseMeta(text);

    // ── the trailing island ──────────────────────────────────────────────────────────────────────────────────────────────

    private static Element TrailingIsland(ShellTitleBarProps props, Func<TierFacts> tiers)
    {
        var t = tiers();
        var rec = ZuneFrameRules.TitleBarOf(t);
        var l = props.Layout();
        var kids = new List<Element>(3)
        {
            // The pill and the magnifier are two mounts of one omnibar over one model: a crossing swaps them with the shared cross-fade.
            rec.Pill
                ? new BoxEl
                {
                    Key = "zune-search-pill-host", Direction = 0, AlignItems = FlexAlign.Center,
                    Enter = ZuneMotion.SwapFade, Exit = ZuneMotion.SwapFade,
                    Children = [ViewSlots.View(ViewKeys.ShellSearch, props.Search with { Form = SearchForm.Field })],
                }
                : new BoxEl
                {
                    Key = "zune-search-icon-host", Direction = 0, AlignItems = FlexAlign.Center,
                    Enter = ZuneMotion.SwapFade, Exit = ZuneMotion.SwapFade,
                    Children = [ViewSlots.View(ViewKeys.ShellSearch, props.Search with { Form = SearchForm.Icon })],
                },
            Account(props, l.Chip, rec),
        };
        // The bell comes and goes at the 1008 crossing (the pill tiers): its keyed host fades, never pops (ZT1).
        if (rec.BellInRow)
            kids.Add(new BoxEl
            {
                Key = ZuneFrameRules.BellKey, Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f,
                Enter = ZuneMotion.SwapFade, Exit = ZuneMotion.SwapFade,
                Children = [Embed.Comp(() => new Bell(props))],
            });
        return new BoxEl
        {
            Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = rec.Gap, Height = ZuneTiers.FrameOf(t).TitleBarH,
            // A crossing changes what the island holds (the magnifier <-> the pill, the bell, the gap): its WIDTH eases (a reflow, a size
            // channel, its right edge riding), so the identity and the bell ride the re-solve. A position slide of theirs would be snapped by
            // the engine's reflow-shove rule whenever the page reflows on the same crossing (ZT1).
            Animate = Design.Reduced ? null : ZuneFrameTiers.TrailingIslandMotion,
            Children = kids.ToArray(),
        };
    }

    /// <summary>The identity, from the AUTH FOLD the allocator resolved (a silent resume is Connecting, never an actionable "Sign in"): the
    /// core's chip host when signed in, a caption while a resume is in flight, the accent button otherwise (at least 40 x 40).</summary>
    private static Element Account(ShellTitleBarProps props, ChromeChipForm chip, ZuneTitleBarLayout rec)
    {
        Element el = chip switch
        {
            ChromeChipForm.Profile => props.ProfileChip() with { Key = ZuneFrameRules.ProfileKey },
            ChromeChipForm.Connecting => new BoxEl
            {
                Key = ZuneFrameRules.ConnectingKey, Height = ZuneFrameRules.Button, AlignItems = FlexAlign.Center,
                Padding = ZuneFrameRules.ConnectingPadding,
                Children = [R.Type.DenseMeta(Loc.Get(Strings.Shell.Connecting)) with { Color = Tok.TextSecondary, Case = R.Case.For(LabelKind.Label) }],
            },
            var form => Button.Accent(Loc.Get(form == ChromeChipForm.Reconnect ? Strings.Shell.Reconnect : Strings.Shell.SignIn), props.SignIn) with
            {
                Key = ZuneFrameRules.SignInKey, MinWidth = ZuneFrameRules.Button, MinHeight = ZuneFrameRules.Button,
            },
        };
        // The board's extra 8 after the pill; the magnifier tiers sit the account at the row's own gap. ONE keyed host at every tier (ZT1), so
        // the 1008 crossing keeps the identity chip's node (a bare chip at Small and a wrapped one at Standard would remount it).
        return new BoxEl
        {
            Key = AccountHostKey, Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f,
            Margin = rec.Pill ? ZuneFrameRules.AccountMargin : default,
            // The identity's own box changes height with the chip's form per tier (32 <-> 40 at 640): a size reflow, never a snap. Its place
            // rides the island's width reflow above.
            Animate = Design.Reduced ? null : ZuneFrameTiers.AccountSizeMotion,
            Children = [el],
        };
    }

    /// <summary>The identity's host in the trailing island (kept at every tier).</summary>
    internal const string AccountHostKey = "zune-account-host";

    // ── the components ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Back: the primary on click, the history menu on right-click / touch-hold when the capabilities offer it. The menu's rows
    /// are read at OPEN, never at mount; an empty stack opens nothing.</summary>
    private sealed class BackButton(ShellTitleBarProps props, IReadSignal<ThemeFacts> facts) : Component
    {
        public override Element Render()
        {
            bool canBack = props.CanBack();
            bool menu = facts.Value.Capabilities.HistoryMenu;
            var anchor = UseRef<NodeHandle>(default);
            var handle = UseRef<OverlayHandle?>(null);
            var overlay = UseContext(Overlay.Service);

            void OpenHistory(ContextRequestEventArgs _)
            {
                if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
                var items = props.HistoryMenu(false);   // read at OPEN
                if (items.Count == 0) return;
                var h = overlay.Open(() => anchor.Value,
                    () => ViewSlots.View(ViewKeys.ShellHistoryMenu, new HistoryMenuProps { Items = items, Close = () => handle.Value?.Close() }),
                    FlyoutPlacement.BottomEdgeAlignedLeft,
                    new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
                handle.Value = h;
                h.ClosedAction = () => handle.Value = null;
            }

            var button = IconButton.Create(Icons.Back, props.GoBack, ButtonStyle(Tok.TextPrimary), isEnabled: canBack) with
            {
                OnRealized = h => anchor.Value = h,
                OnContextRequested = menu ? new Action<ContextRequestEventArgs>(OpenHistory) : null,
            };
            return ToolTip.Wrap(button, Loc.Get(Strings.Theme.Zune.Frame.Back));
        }
    }

    /// <summary>The bell: a 40 x 40 glyph with the accent dot over its top right while anything is unread. The dot is always laid out (its
    /// opacity says it), so an unread edge moves nothing.</summary>
    private sealed class Bell(ShellTitleBarProps props) : Component
    {
        public override Element Render()
        {
            var overlay = UseContext(Overlay.Service);
            var anchor = UseRef<NodeHandle>(default);
            var handle = UseRef<OverlayHandle?>(null);
            int unread = props.BellUnread();

            void Toggle() => props.ToggleNotifications(overlay, () => anchor.Value, handle);

            var button = IconButton.Create(Icons.Bell, Toggle, ButtonStyle(Tok.TextSecondary)) with { OnRealized = h => anchor.Value = h };
            var content = new BoxEl
            {
                ZStack = true, Width = ZuneFrameRules.Button, Height = ZuneFrameRules.Button, Shrink = 0f,
                Children =
                [
                    button,
                    new BoxEl
                    {
                        Width = ZuneFrameRules.Button, Height = ZuneFrameRules.Button, Direction = 1, Justify = FlexJustify.Start, HitTestVisible = false,
                        Children =
                        [
                            new BoxEl
                            {
                                Direction = 0, Justify = FlexJustify.End,
                                Children =
                                [
                                    new BoxEl
                                    {
                                        Key = ZuneFrameRules.BellDotKey, Width = ZuneFrameRules.DotSize, Height = ZuneFrameRules.DotSize,
                                        Margin = ZuneFrameRules.DotMargin, Corners = R.Shape.All(ShapeRole.CurrentMark), Fill = R.Ink.AccentDecor,
                                        Opacity = unread > 0 ? 1f : 0f,
                                    },
                                ],
                            },
                        ],
                    },
                ],
            };
            return ToolTip.Wrap(content, Loc.Get(Strings.Notifications.Title));
        }
    }
}
