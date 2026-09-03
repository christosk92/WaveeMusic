using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// ── The authenticated account chip + dropdown + logout confirm ────────────────────────────────────────────────────────
// WinUI-desktop parity: the avatar top-right opens a flyout (account / settings / log out). Reuses the shell's already-
// mounted Overlay.Service (no new host). "Log out" opens a modal confirm → Services.LogoutAsync, which flips the gate back
// to the takeover with NO process restart.
//
// Below ChromeActionsEnterW it is also the FOLD for the trailing island's action buttons: bell and friends, which live
// as direct buttons in the row above that width (ShellToolbar.NotificationBellButton, MergedChromeRow.Trailing),
// become menu rows here instead — never both — so neither affordance disappears, only moves. Settings is always a row
// here (it has no row form); pin has none here at all (it drops below the threshold — the tab/page context menu still
// offers it). The theme toggle is a permanent caption-leading button now, but keeps a menu row too for discoverability
// parity with the rest of the "me" cluster. (The Palette submenu that used to sit beside it is gone — Workstream B,
// "Settings regroup + removals": Wavee always renders the neutral palette now.)
sealed class ProfileMenu : Component
{
    static readonly ColorF Gold = ColorF.FromRgba(0xE6, 0xC2, 0x6C);
    const float MenuWidth = 304f;
    /// <summary>The chip's plain avatar diameter (the unread badge lives on the trailing bell now, not here).</summary>
    const float AvatarSize = 24f;
    /// <summary>Issue #88: <c>ShellResponsiveLayout.ChromeProfileNameW</c> (90) budgets the chip's 8-DIP gap +
    /// this caption + the named form's extra 6 DIP of right padding — but the caption itself carried no
    /// <c>MaxWidth</c>, so a long display name pushed the chip (and everything left of it) past what the budget had
    /// actually reserved, making the constant nominal rather than real. Capping the caption at the remainder
    /// (90 − 8 − 6) makes it a real reservation.</summary>
    const float NameCapW = ShellResponsiveLayout.ChromeProfileNameW - 8f - 6f;

    readonly PlaybackBridge _b;
    // The LADDER as a signal, not frozen bools: a ComponentEl never re-runs its factory, so a plain `bool showName`
    // ctor arg would freeze at mount. Reading it in Render subscribes THIS component to every stage change.
    readonly IReadSignal<MergedChromeLayout> _layout;
    // Reference-stable verbs owned by MergedChromeRow (a method group / a shell method), so freezing them at mount is
    // correct — each resolves its ambient service at INVOKE time.
    readonly Action _toggleTheme, _toggleFriends;

    public ProfileMenu(PlaybackBridge b, IReadSignal<MergedChromeLayout> layout, Action toggleTheme, Action toggleFriends)
    { _b = b; _layout = layout; _toggleTheme = toggleTheme; _toggleFriends = toggleFriends; }

    public override Element Render()
    {
        var services = UseContext(Services.Slot);
        var overlay = UseContext(Overlay.Service);
        var go = UseContext(HistoryStore.NavCtx);
        var actions = UseContext(ActionServices.Slot);   // the utility-command bag ("Play file…" needs Svc + Playback)
        var nc = UseContext(NotificationCenterBridge.Slot);
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var notifyHandle = UseRef<OverlayHandle?>(null);

        var l = _layout.Value;      // subscribe → the chip's name column and the menu-fold rows follow the ladder
        bool showName = l.ShowName;

        var user = _b.User.Value;   // subscribe → chip + menu header follow the session
        string name = string.IsNullOrWhiteSpace(user?.DisplayName) ? "—" : user!.DisplayName;
        bool premium = user?.IsPremium ?? false;
        string avatar = user?.AvatarUrl ?? "";
        string? email = user?.Email;
        var pic = PersonPicture.Create(avatar, AvatarSize, displayName: name);

        void Close() => handle.Value?.Close();

        // The bell's panel, re-anchored to the CHIP — NotificationPanelLauncher.Open is the exact mechanism
        // ShellToolbar.NotificationBellButton uses above ChromeActionsEnterW (same NotificationPanel, same
        // placement/chrome, same OnPanelOpened unread-seen mark); this is just a different anchor and its own handle
        // so it never fights the account flyout's own.
        void OpenNotifications()
        {
            if (nc is null) return;
            NotificationPanelLauncher.Open(overlay, nc, () => anchor.Value, notifyHandle);
        }

        void ConfirmLogout()
        {
            OverlayHandle? h = null;
            h = overlay.Open(
                () => NodeHandle.Null,
                () => ConfirmCard(
                    Loc.Get(Strings.Auth.LogoutConfirmTitle),
                    Loc.Get(Strings.Auth.LogoutConfirmBody),
                    Loc.Get(Strings.Auth.LogOut),
                    onConfirm: () => { h?.Close(); _ = services?.LogoutAsync(); },
                    onCancel: () => h?.Close()),
                FlyoutPlacement.BottomCenter,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.Modal, Chrome: PopupChrome.Modal));
        }

        // "Play ▸" — the GLOBAL play utilities (they belong to no track), so they live here rather than in the
        // per-track menu, and the whole submenu is absent (not disabled) when the backend cannot play anything of its
        // own: an offer you cannot take is worse than no offer, which is the rule "Play file…" already followed.
        //
        // The rows past the separator are contributed, not hard-coded: one per INSTALLED module that declares the
        // `match` capability, labelled and placeholdered by its own manifest. That is composition rule 2 — the menu is
        // built from ModuleHost.Installed, so a module the user installs later shows up here without a code change,
        // and a component still names no source type.
        MenuFlyoutItem[]? PlayItems()
        {
            if (!LocalFileActions.CanPlayFiles(actions)) return null;
            string genericPlaceholder = Loc.Get(Strings.Play.Placeholder);
            var items = new List<MenuFlyoutItem>(6)
            {
                new(Loc.Get(Strings.Play.File), Icons.Document,
                    Invoke: () => { Close(); LocalFileActions.PickAndPlay(actions); }),
                // No module pinned: the router decides who owns the text, so this row takes ANY link.
                new(Loc.Get(Strings.Play.Link), Icons.Link,
                    Invoke: () => { Close(); PlayLink.Open(overlay, actions, null, genericPlaceholder); }),
            };

            var installed = actions?.Svc?.Modules?.Installed;
            if (installed is not null)
            {
                bool separated = false;
                for (int i = 0; i < installed.Count; i++)
                {
                    var module = installed[i];
                    if (!PlayLinkActions.DeclaresMatch(module.Manifest)) continue;
                    if (!separated) { items.Add(MenuFlyoutItem.Separator); separated = true; }
                    // Captured by value: the invoke runs long after this loop, and a captured loop variable would
                    // otherwise pin whatever the last iteration left behind.
                    string id = module.Id;
                    string placeholder = PlayLinkActions.PlaceholderFor(module.Manifest, genericPlaceholder);
                    items.Add(new MenuFlyoutItem(PlayLinkActions.MenuLabel(module.Manifest), Icons.Globe,
                        Invoke: () => { Close(); PlayLink.Open(overlay, actions, id, placeholder); }));
                }
            }
            return items.ToArray();
        }

        void OpenMenu()
        {
            if (handle.Value is { IsOpen: true }) { Close(); return; }
            handle.Value = overlay.Open(
                () => anchor.Value,
                () => MenuContent(name, premium, avatar, email,
                    unread: nc?.UnreadCount.Peek() ?? 0,
                    showNotifications: nc is not null && _layout.Peek().ActionsInMenu,
                    showFriends: _layout.Peek().ActionsInMenu,
                    close: Close,
                    onAccount: () => { Close(); LoginView.OpenUrl("https://www.spotify.com/account"); },
                    onSettings: () => { Close(); go("settings", null); },
                    onNotifications: () => { Close(); OpenNotifications(); },
                    onFriends: () => { Close(); _toggleFriends(); },
                    onTheme: () => { Close(); _toggleTheme(); },
                    playItems: PlayItems(),
                    onLogout: () => { Close(); ConfirmLogout(); }),
                FlyoutPlacement.BottomEdgeAlignedRight,
                // MENU chrome, not FlyoutPresenter: the body IS a MenuFlyout (an account header over
                // MenuFlyout.Create rows + a Palette sub-menu), so it takes MenuPopupThemeTransition — the anchored
                // 250ms unfold with the content readable from the first frame, over a windowed DWM-acrylic popup.
                // PopupChrome.Popup would give it the ordinary-Flyout PopupThemeTransition instead: 83ms of nothing,
                // then an 83ms fade across a 367ms 50px slide (uxtheme TAS_SHOWPOPUP) — correct for arbitrary flyout
                // CONTENT, wrong for a menu, and visibly unlike every other menu in the shell.
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Flyout)
                {
                    ConstrainToRootBounds = false,
                });
            handle.Value.ClosedAction = () => handle.Value = null;
        }

        return new BoxEl
        {
            Direction = 0, Gap = 8f, AlignItems = FlexAlign.Center, Height = 32f,
            Padding = new Edges4(4f, 0f, showName ? 10f : 4f, 0f), Corners = CornerRadius4.All(Radii.Control),
            Role = AutomationRole.Button, Focusable = true,
            OnClick = OpenMenu, OnRealized = h => anchor.Value = h,
            Children = showName
                ? new Element[]
                {
                    pic,
                    Caption(name).Primary() with
                    {
                        MaxWidth = NameCapW, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                    },
                }
                : new Element[] { pic },
        }.Interactive(Interaction.Subtle);
    }

    // The dropdown: a compact account header over stock WinUI menu rows.
    static Element MenuContent(string name, bool premium, string avatar, string? email, int unread,
        bool showNotifications, bool showFriends,
        Action close, Action onAccount, Action onSettings, Action onNotifications, Action onFriends, Action onTheme,
        IReadOnlyList<MenuFlyoutItem>? playItems, Action onLogout)
    {
        var rows = new List<MenuFlyoutItem>(9)
        {
            new(Loc.Get(Strings.Auth.Account), Icons.Contact, Invoke: onAccount),
            new(Loc.Get(Strings.Auth.Settings), Icons.Settings, Invoke: onSettings),
        };
        if (playItems is { Count: > 0 })
            rows.Add(MenuFlyoutItem.SubMenu(Loc.Get(Strings.Play.Menu), playItems, Icons.MusicNote));

        // Notifications and Friends are the trailing island's own direct buttons above ChromeActionsEnterW
        // (ShellToolbar.NotificationBellButton, MergedChromeRow.Trailing) — they show HERE only when the ladder has
        // folded them out of the row, so the affordance is reachable at every width and duplicated at none.
        // Notifications carries the count in its label exactly as the bell's badge does in the row.
        if (showNotifications || showFriends) rows.Add(MenuFlyoutItem.Separator);
        if (showNotifications)
            rows.Add(new MenuFlyoutItem(
                unread > 0 ? Strings.Notifications.OverflowTitle(unread) : Loc.Get(Strings.Notifications.Title),
                Icons.Bell, Invoke: onNotifications));
        if (showFriends)
            rows.Add(new MenuFlyoutItem(Loc.Get(Strings.Shell.Friends), Icons.Friends, Invoke: onFriends));

        rows.Add(MenuFlyoutItem.Separator);
        // Labelled and glyphed with the TARGET theme — the register the retired "⋯" row and the old button's tooltip
        // both used ("Light theme" while dark).
        rows.Add(new MenuFlyoutItem(
            Theme.Dark ? Loc.Get(Strings.Shell.LightTheme) : Loc.Get(Strings.Shell.DarkTheme),
            Theme.Dark ? Icons.Sun : Icons.Moon, Invoke: onTheme));
        rows.Add(MenuFlyoutItem.Separator);
        rows.Add(new MenuFlyoutItem(Loc.Get(Strings.Auth.LogOut), Icons.SignOut, Invoke: onLogout));
        var items = rows.ToArray();

        return new BoxEl
        {
            Direction = 1,
            MinWidth = MenuWidth,
            MaxWidth = MenuWidth,
            // 6 + the menu presenter's own MenuFlyoutPresenterThemePadding (0,2,0,2, applied by FlyoutSurface's
            // Flyout branch) = the 8px inset this card has always had.
            Padding = new Edges4(0, 6, 0, 6),
            Children =
            [
                AccountHeader(name, premium, avatar, email),
                HeaderSeparator(),
                MenuFlyout.Create(items, close, MenuWidth),
            ],
        };
    }

    static Element HeaderSeparator() => new BoxEl
    {
        Height = 1f,
        Margin = new Edges4(8, 4, 8, 4),
        Fill = Tok.StrokeDividerDefault,
    };

    static Element AccountHeader(string name, bool premium, string avatar, string? email) => new BoxEl
    {
        Direction = 0,
        Gap = 12f,
        AlignItems = FlexAlign.Center,
        Padding = new Edges4(14, 10, 14, 10),
        Children =
        [
            PersonPicture.Create(avatar, 40f, displayName: name),
            new BoxEl
            {
                Direction = 1,
                Gap = 2f,
                Grow = 1f,
                Basis = 0f,
                ClipToBounds = true,
                Children =
                [
                    new TextEl(name)
                    {
                        Size = 14f,
                        Weight = 600,
                        Color = Tok.TextPrimary,
                        MaxLines = 1,
                        Trim = TextTrim.CharacterEllipsis,
                    },
                    TierLine(premium),
                    email is { Length: > 0 }
                        ? new TextEl(email)
                        {
                            Size = 12f,
                            Color = Tok.TextTertiary,
                            MaxLines = 1,
                            Trim = TextTrim.CharacterEllipsis,
                        }
                        : new BoxEl(),
                ],
            },
        ],
    };

    static Element TierBadge(bool premium)
    {
        if (!premium)
            return new TextEl(Loc.Get(Strings.Auth.FreeBadge)) { Size = 12f, Color = Tok.TextSecondary };
        ColorF goldInk = Theme.Dark ? Gold : ColorF.FromRgba(0x8A, 0x63, 0x12);
        return new TextEl(Loc.Get(Strings.Auth.PremiumBadge)) { Size = 12f, Color = goldInk };
    }

    static Element TierLine(bool premium)
    {
        ColorF fg = premium ? (Theme.Dark ? Gold : ColorF.FromRgba(0x8A, 0x63, 0x12)) : Tok.TextSecondary;
        return new BoxEl
        {
            Direction = 0,
            Gap = 5f,
            AlignItems = FlexAlign.Center,
            Children =
            [
                premium ? Icon(Icons.FavoriteStar, 10f, fg) : new BoxEl { Width = 10f },
                TierBadge(premium),
            ],
        };
    }

    // A focused modal confirm card (reuses the engine's dialog tokens + the Overlay.Service modal chrome).
    static Element ConfirmCard(string title, string message, string confirmLabel, Action onConfirm, Action onCancel) => new BoxEl
    {
        Direction = 1, Width = 380f, MinWidth = 320f, MaxWidth = 420f,
        Corners = Radii.OverlayAll, Fill = Tok.FillSolidBase, BorderColor = Tok.StrokeSurfaceDefault, BorderWidth = 1f,
        Shadow = Elevation.Dialog, Padding = Edges4.All(24f), Gap = Spacing.M,
        Children =
        [
            new TextEl(title) { Size = 20f, Weight = 600, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap },
            new TextEl(message) { Size = 14f, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap },
            new BoxEl
            {
                Direction = 0, Gap = Spacing.S, Justify = FlexJustify.End, Margin = new Edges4(0, Spacing.M, 0, 0),
                Children =
                [
                    Button.Standard(Loc.Get(Strings.Auth.Cancel), onCancel) with { MinWidth = 96f },
                    Button.Accent(confirmLabel, onConfirm) with { MinWidth = 96f },
                ],
            },
        ],
    };
}
