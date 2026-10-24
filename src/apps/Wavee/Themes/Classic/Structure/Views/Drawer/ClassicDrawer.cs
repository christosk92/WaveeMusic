// ── Themes/Classic/Structure/Views/Drawer/ClassicDrawer.cs — Classic's `Shell.NarrowDrawer` (themes plan 6.1, T4c) ──────────
//
// From `Shell.UI.cs`: `NarrowDrawer`'s picture, `DrawerScrim` and `DrawerPane`. In the Narrow and Tiny bands the sidebar is an
// OVERLAY over the page. The core host (`Shell.NarrowDrawer`, a [CoreHost]) decides: the band's overlay and pinned answers, the
// session's open flag, the single `KeyPreview` slot it chains while open (Escape closes the overlay; while editing Escape belongs to
// the pane first), the leaf-navigation close, and the mount decision. This template draws and animates.
//
//   not mounted   a zero-size placeholder. BUG E1: on a desktop-width window the overlay is unreachable, so mount NOTHING (no scrim and
//                 no second `Sidebar.DrawerPane()`, a whole second pane planning the sidebar on every invalidation). The decision is
//                 the band ALONE (`NarrowDrawerMount.ShouldMount`), never the open flag: the pane must stay mounted for the ENTIRE time
//                 the shell is narrow, closed included, or the first open would have nothing already-mounted for the slide to animate from.
//   mounted       a ZStack: the SCRIM (a dim that fades with the open state; a click closes; a pinned drawer (editing in a forced band)
//                 keeps a lighter, non-dismissing scrim that lets the page behind stay usable) under the PANE (a stock overlay
//                 pane: in-app acrylic + flyout elevation, sliding in on the pane spline). Open/close is compositor-only.
//
// THE ACCOUNT ROW (TC2, C-Shell-Compact / C-Sidebar account="true"): where the window tier's title bar carries no identity chip (Classic
// Compact), the pane leads with a 56-DIP account row: the account chip in its row form (the avatar, the name over "Account and more"; a
// press opens the account menu), the bell and the friends door at 40 x 40, a divider under it.
//
// The scrim and the pane are Components: each runs its own opacity/translate transition on its own root, seeded from the closed
// resting value on mount so the first open has something to slide from, and from where it is drawn on a reversal (a quick re-open
// mid-close continues, never jumps).
//
// THE PANE FILLS THE WINDOW'S HEIGHT. It mounts inside a component anchor, and an anchor is a default COLUMN whatever row it sits in,
// so a row's cross-axis stretch reaches the anchor only: the pane measured its content (the account row, and a nav list whose
// scroller has no minimum height, so 0) and the drawer was a 100-tall card with no navigation. The pane is a column child that GROWS
// instead, inside a column wrapper (the anchor mirrors the grow), so the account row sits on top and the nav pane takes the rest with
// its own scroll (C-Shell-Compact, C-Frame-Narrow).
//
// MODAL (open and not pinned): the scrim and the pane keep the wheel off the page behind (`BlocksBackgroundScroll`: the page is a
// sibling under the drawer, not an ancestor, so the router's containing-scroller scan would find it), and focus moves into the pane
// with Tab trapped there (`ClassicModalScope`); the core host's key preview closes it on Escape.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's narrow-window drawer.</summary>
internal static class ClassicDrawer
{
    /// <summary>The registered template of <see cref="ViewKeys.ShellNarrowDrawer"/>.</summary>
    public static Element Template(NarrowDrawerProps props, in ViewContext ctx)
    {
        if (!props.Mounted)
            return new BoxEl { Width = 0f, Height = 0f, Shrink = 0f, HitTestVisible = false };

        var parts = props.Parts;
        return new BoxEl
        {
            Grow = 1f, ZStack = true, HitTestVisible = props.Open,
            Children =
            [
                Embed.Comp(parts, static () => new ClassicDrawerScrim()),
                new BoxEl
                {
                    Grow = 1f, Direction = 1, AlignItems = FlexAlign.Start, HitTestPassThrough = true,
                    Children = [Embed.Comp(props, static () => new ClassicDrawerPane())],
                },
            ],
        };
    }
}

/// <summary>The drawer's dim.</summary>
internal sealed class ClassicDrawerScrim : Component
{
    bool _mounted;

    public override Element Render()
    {
        var parts = UseProps<NarrowDrawerParts>();
        // Pinned (editing in a forced band) keeps the pane open behind a non-dismissing 0.2 scrim that lets the page
        // behind it stay usable (Q16): no hit test, no click-to-close.
        bool pinned = parts.IsPinned();
        bool open = parts.IsOpen();
        float ms = Design.Reduced ? 0f : Design.Motion.Fast;
        float target = parts.RestingOpacity(open);
        UseTransition(AnimChannel.Opacity, _mounted ? ClassicOverlayMotion.From(Context, AnimChannel.Opacity, 1f - target) : target, target, ms, Easing.Linear, DepKey.From(open));
        _mounted = true;
        bool modal = open && !pinned;
        return new BoxEl
        {
            Grow = 1f,
            Fill = pinned ? ClassicFrameBitsRules.DrawerPinnedScrim : ClassicFrameBitsRules.DrawerScrim,
            Opacity = pinned ? 1f : target,
            HitTestVisible = modal, OnClick = pinned ? null : parts.Close, TabStop = false,
            // The page is a sibling under the drawer, never an ancestor: without this the wheel over the dim found it.
            BlocksBackgroundScroll = modal,
        };
    }
}

/// <summary>The drawer's pane.</summary>
internal sealed class ClassicDrawerPane : Component
{
    bool _mounted;
    readonly ClassicModalScope _scope = new();

    public override Element Render()
    {
        var props = UseProps<NarrowDrawerProps>();
        var parts = props.Parts;
        bool open = parts.IsOpen();
        bool modal = open && !parts.IsPinned();
        var hooks = UseContext(InputHooks.Current);
        var vp = UseContextSignal(Viewport.Size);
        float width = parts.WidthNow(vp.Peek().Width);
        float ms = Design.Reduced ? 0f : (open ? MotionTok.PaneOpen.DurationMs : MotionTok.PaneClose.DurationMs);
        float target = parts.RestingTranslateX(open, width);
        UseTransition(AnimChannel.TranslateX, _mounted ? ClassicOverlayMotion.From(Context, AnimChannel.TranslateX, open ? -width : 0f) : target, target, ms,
            Easing.FluentPane, DepKey.From(open));
        _mounted = true;
        // Focus into the open pane, Tab trapped there; back where it was on close (a pinned pane leaves the page usable: no trap).
        UseLayoutEffect(() => _scope.Update(hooks, Context.Scene, Context.HostNode, modal), DepKey.From(modal));
        UseEffect(() => (Action?)(() => _scope.Lower(Context.Scene)), DepKey.Empty);

        return new BoxEl
        {
            // A column child that GROWS to the window's height (see the file header): the anchor mirrors the grow, a stretch would not reach.
            Key = "drawer-pane", Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinHeight = 0f, ClipToBounds = true,
            // The resting position stays coupled to the live width: a closed pane can grow while resizing inside the
            // band, and a stale static translation would expose the added strip.
            Transform = Prop.Of(() => Affine2D.Translation(parts.RestingTranslateX(parts.IsOpen(), parts.Width(vp.Value.Width)), 0f)),
            Width = Prop.Of(() => parts.Width(vp.Value.Width)),
            // A stock OVERLAY PANE (NavigationView minimal mode): in-app acrylic + flyout elevation, not a dialog slab.
            Fill = ColorF.Transparent, Acrylic = Tok.AcrylicFlyout,
            BorderWidth = 1f, BorderColor = Prop.Of(static () => Tok.StrokeCardDefault),
            Corners = ClassicFrameBitsRules.DrawerPaneCorners,
            Shadow = Elevation.Flyout, HitTestVisible = open,
            // Over the page, not inside it: the wheel over the pane's own rows scrolls the nav list (an ancestor), never the page.
            BlocksBackgroundScroll = true,
            Children = props.ShowAccount && props.Account is { } account ? [AccountRow(account), parts.PaneBody()] : [parts.PaneBody()],
        };
    }

    /// <summary>The account row over the pane (Compact): the chip's row form (or "Sign in"), the bell and the friends door, a divider under it.</summary>
    static Element AccountRow(DrawerAccountProps account) => new BoxEl
    {
        Key = "drawer-account", Direction = 1, Shrink = 0f, Margin = new Edges4(0f, 0f, 0f, ClassicFrameBitsRules.AccountRowBelow),
        Children =
        [
            new BoxEl
            {
                Direction = 0, Height = ClassicFrameBitsRules.AccountRowH, Shrink = 0f, AlignItems = FlexAlign.Center,
                Gap = ClassicFrameBitsRules.AccountRowGap, Padding = ClassicFrameBitsRules.AccountRowPadding,
                Children =
                [
                    Embed.Comp(account, static () => new ClassicDrawerIdentity()),
                    Embed.Comp(account, static () => new ClassicDrawerBell()),
                    ToolTip.Wrap(ClassicDrawerButtons.Glyph(Icons.Friends, account.ToggleFriends) with { Key = "drawer-friends" }, Loc.Get(Strings.Shell.Friends)),
                ],
            },
            new BoxEl { Height = 1f, Shrink = 0f, Fill = Prop.Of(static () => Tok.StrokeDividerDefault), HitTestVisible = false },
        ],
    };
}

/// <summary>The account row's identity: the chip's drawer form while signed in, else "Sign in" / "Reconnect".</summary>
internal sealed class ClassicDrawerIdentity : Component
{
    public override Element Render()
    {
        var account = UseProps<DrawerAccountProps>();
        return account.SignedIn()
            ? new BoxEl { Grow = 1f, Shrink = 1f, MinWidth = 0f, Direction = 0, Children = [account.Chip()] }
            : new BoxEl
            {
                Grow = 1f, Shrink = 1f, MinWidth = 0f, Direction = 0, AlignItems = FlexAlign.Center,
                Children = [Button.Accent(Loc.Get(Strings.Shell.SignIn), account.SignIn) with { Key = "drawer-sign-in" }],
            };
    }
}

/// <summary>The account row's bell: the unread pill over the button's own box.</summary>
internal sealed class ClassicDrawerBell : Component
{
    public override Element Render()
    {
        var account = UseProps<DrawerAccountProps>();
        var overlay = UseContext(Overlay.Service);
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        int unread = account.BellUnread();
        var button = ClassicDrawerButtons.Glyph(Icons.Bell, () => account.ToggleNotifications(overlay, () => anchor.Value, handle)) with
        {
            OnRealized = h => anchor.Value = h,
        };
        float box = ClassicFrameBitsRules.AccountButton;
        BoxEl content = unread <= 0 ? button : new BoxEl
        {
            ZStack = true, Width = box, Height = box, Shrink = 0f,
            Children =
            [
                button,
                new BoxEl
                {
                    Width = box, Height = box, Direction = 1, Justify = FlexJustify.Start, HitTestVisible = false,
                    Children = [new BoxEl { Direction = 0, Justify = FlexJustify.End, Children = [InfoBadge.Count(unread)] }],
                },
            ],
        };
        return ToolTip.Wrap(content with { Key = "drawer-bell" }, Loc.Get(Strings.Notifications.Title));
    }
}

/// <summary>The account row's 40 x 40 glyph buttons.</summary>
internal static class ClassicDrawerButtons
{
    public static BoxEl Glyph(string glyph, Action onClick) => new BoxEl
    {
        Width = ClassicFrameBitsRules.AccountButton, Height = ClassicFrameBitsRules.AccountButton, Shrink = 0f,
        Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.ControlAll,
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand, OnClick = onClick,
        Children = [new TextEl(glyph) { Size = ClassicFrameBitsRules.AccountGlyph, FontFamily = Tok.Definition.Fonts.Icon, Color = Tok.TextPrimary }],
    }.Interactive(Interaction.Subtle);
}
