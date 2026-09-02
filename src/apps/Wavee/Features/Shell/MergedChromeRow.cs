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

/// <summary>Builders for Wavee's single 48-DIP TitleBar row. The builders run inside TitleBar.Render and therefore
/// contain no hooks; hook-owned behavior is delegated to child components.</summary>
sealed class MergedChromeRow
{
    readonly Signal<bool> _canBack, _canForward;
    readonly Action<string, string?> _go;
    readonly Action _back, _forward, _toggleTheme;
    readonly Signal<string> _searchText;
    readonly List<Route> _backHistory, _forwardHistory;
    readonly IReadSignal<MergedChromeLayout> _layout;
    readonly IReadSignal<int> _searchFocusRequest;
    readonly Signal<bool> _searchFocused, _searchFlyoutOpen;
    readonly Func<Element> _tabStrip;
    readonly Func<int> _tabsEpoch;
    // The omnibar's suggestion state, owned HERE rather than by the omnibar component: the field-mode field and the
    // icon-mode flyout are two mounts of FluentRichOmnibar, and a search-mode switch must not restart the rows, the
    // pending request or the keyboard cursor from nothing.
    readonly OmnibarSuggestStore _suggest = new();

    internal PlaybackBridge? Bridge;
    internal ShellUi? Ui;
    internal ActionServices? Acts;

    public MergedChromeRow(
        Signal<bool> canBack, Signal<bool> canForward,
        Action<string, string?> go, Action back, Action forward,
        Signal<string> searchText, Action toggleTheme,
        List<Route> backHistory, List<Route> forwardHistory,
        IReadSignal<MergedChromeLayout> layout, IReadSignal<int> searchFocusRequest,
        Signal<bool> searchFocused, Signal<bool> searchFlyoutOpen,
        Func<Element> tabStrip, Func<int> tabsEpoch)
    {
        _canBack = canBack; _canForward = canForward;
        _go = go; _back = back; _forward = forward;
        _searchText = searchText; _toggleTheme = toggleTheme;
        _backHistory = backHistory; _forwardHistory = forwardHistory;
        _layout = layout; _searchFocusRequest = searchFocusRequest;
        _searchFocused = searchFocused; _searchFlyoutOpen = searchFlyoutOpen;
        _tabStrip = tabStrip; _tabsEpoch = tabsEpoch;
    }

    public int ContentVersion()
    {
        var l = _layout.Value;
        int epoch = _tabsEpoch();
        // ShellAuthState, NOT the raw AuthStatus: the chip below renders off the SHELL state (a silent resume
        // running behind the cache-first shell is Connecting, and AuthStatus stays LoggedOut for its whole
        // duration), so the memo key has to move on the same value the chip does.
        int auth = (int)(Bridge?.AuthState.Value ?? ShellAuthState.SignInRequired);
        int flags = (l.ShowName ? 1 : 0) | (l.ShowActions ? 2 : 0) | (l.ShowForward ? 4 : 0)
                  | (l.SearchMode == MergedSearchMode.Icon ? 8 : 0) | (l.ShowBack ? 16 : 0)
                  | (l.ShowNewTab ? 32 : 0) | (l.ShowTrailing ? 64 : 0);
        return HashCode.Combine(flags, (int)l.SearchWidth, epoch, auth);
    }

    public Element Tabs()
    {
        var l = _layout.Value;
        var kids = new List<Element>(3);
        if (l.ShowBack)
            kids.Add(Embed.Comp(() => new NavHistoryButton(
                Icons.Back, _back, _canBack, _backHistory, _go, ShellToolbar.BarNavStyle)));
        if (l.ShowForward)
            kids.Add(Embed.Comp(() => new NavHistoryButton(
                Icons.Forward, _forward, _canForward, _forwardHistory, _go, ShellToolbar.BarNavStyle)));
        kids.Add(_tabStrip());
        return new BoxEl
        {
            // HUGS (the TitleBar island contract — its rect is reported wholesale as Client, so slack in here is dead
            // drag space). Shrink=1 + MinWidth=0 is what lets the strip's scroll lane give way instead.
            Direction = 0, AlignItems = FlexAlign.Center, Height = TitleBar.ExpandedHeight,
            Shrink = 1f, MinWidth = 0f, Children = kids.ToArray(),
        };
    }

    public Element Center(IReadSignal<float> avail)
    {
        var l = _layout.Value;
        return l.SearchMode == MergedSearchMode.Field
            ? Embed.Comp(() => new MergedSearchField(
                _searchText, _go, _suggest, _searchFocusRequest, _searchFocused, _layout, avail))
            : new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };
    }

    public Element CaptionLeading()
    {
        var l = _layout.Value;
        var kids = new List<Element>(2) { ThemeToggle() };
        if (l.SearchMode == MergedSearchMode.Icon)
            kids.Add(Embed.Comp(() => new MergedSearchFlyoutButton(
                _searchText, _go, _suggest, _searchFocusRequest, _searchFlyoutOpen)));
        return new BoxEl
        {
            Direction = 0,
            Height = TitleBar.ExpandedHeight,
            Shrink = 0f,
            AlignItems = FlexAlign.Center,
            Children = kids.ToArray(),
        };
    }

    // A real island button now (ShellToolbar.BarNavStyle + BarNavMargin), not the old hand-rolled 32-DIP box — the
    // same 40x44 geometry as every other caption-leading/trailing affordance (ChromeThemeToggleW tracks the change).
    Element ThemeToggle() => ToolTip.Wrap(
        IconButton.Create(Theme.Dark ? Icons.Sun : Icons.Moon, _toggleTheme, ShellToolbar.BarNavStyle)
            with { Key = "chrome-theme-toggle", Margin = ShellToolbar.BarNavMargin },
        Theme.Dark ? Loc.Get(Strings.Shell.LightTheme) : Loc.Get(Strings.Shell.DarkTheme));

    public Element Trailing()
    {
        var l = _layout.Value;
        if (!l.ShowTrailing) return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };
        var kids = new List<Element>(5) { ProfileChip() };
        // The ONE "actions in row" stage (MergedChromeLayout.ActionsInRow): bell, friends, pin and settings enter
        // together, no "…" any more. Below it they fold into the profile menu instead of vanishing (ProfileMenu's
        // Notifications/Friends rows) — pin alone simply drops (the tab/page context menu still offers it).
        if (l.ActionsInRow)
        {
            kids.Add(Embed.Comp(() => new NotificationBellButton()));
            kids.Add(NavButton(Icons.Friends, ToggleFriends, Loc.Get(Strings.Shell.Friends)));
            if (PinButton() is { } pin) kids.Add(pin);
            kids.Add(NavButton(Icons.Settings, () => _go("settings", null), Loc.Get(Strings.Auth.Settings)));
        }
        return new BoxEl
        {
            Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center,
            Height = TitleBar.ExpandedHeight, Children = kids.ToArray(),
        };
    }

    // The shared 40x44 island-button shape (ShellToolbar.BarNavStyle + BarNavMargin) every trailing action button
    // uses, tooltipped like the theme toggle and the search icon.
    static Element NavButton(string glyph, Action onClick, string tooltip) => ToolTip.Wrap(
        IconButton.Create(glyph, onClick, ShellToolbar.BarNavStyle) with { Margin = ShellToolbar.BarNavMargin },
        tooltip);

    // The direct pin/unpin button for whatever destination is on screen — same row PinActions.RowForDestination
    // already hands the tab context menu (WaveeShell.TabMenu) and the retired "…" overflow, so the toast/undo
    // behaviour is identical; only the affordance moved. Null when the destination isn't pinnable or there is no
    // sidebar pin store (ActionServices.Sidebar absent).
    Element? PinButton()
    {
        if (Acts is not { } acts || acts.CurrentDestination?.Invoke() is not { } destination) return null;
        if (PinActions.RowForDestination(acts, in destination) is not { Invoke: { } invoke } row) return null;
        return NavButton(row.Icon.Glyph ?? Icons.Pin, invoke, row.Label);
    }

    internal void ToggleFriends() => Ui?.Toggle(RailMode.Friends);

    // The chip reads ShellAuthState, not the raw AuthStatus. Under the cache-first shell (WaveeApp.needsSignIn) a
    // returning user's shell mounts while the SILENT resume runs behind it — and that resume drives LiveSessionHost
    // directly, never ISpotifySession.ConnectAsync, so AuthStatus sits on LoggedOut for its whole duration. Reading it
    // here put an actionable "Sign in" button on screen during every launch, and pressing it called ConnectAsync on the
    // SwitchableSession's PRE-go-live inner — the FakeSpotifySession — signing the user in as "Wavee Listener" over a
    // real Spotify resume that was already in flight. ShellAuthState is the fold that knows the difference
    // (PlaybackBridge.ProjectAuthState), and Bridge.SignIn is the ONE verb that starts a real one.
    Element ProfileChip()
    {
        var b = Bridge;
        var auth = b?.AuthState.Value ?? ShellAuthState.SignInRequired;
        if (auth == ShellAuthState.Live)
            return Embed.Comp(() => new ProfileMenu(b!, _layout, _toggleTheme, ToggleFriends));
        if (auth == ShellAuthState.Connecting)
            return new BoxEl
            {
                Height = 32f, AlignItems = FlexAlign.Center, Padding = new Edges4(8f, 0f, 8f, 0f),
                Children = [Caption(Loc.Get(Strings.Shell.Connecting)).Secondary()],
            };
        // Offline = a credential is still on disk but the resume failed (a network drop, not a rejection), so the verb
        // is "try again", not "sign in" — the account is not in question. SignInRequired is unreachable from the shell
        // on the real backend (the wizard is the whole window there), but the fake/demo backend does reach it.
        return Button.Accent(
            Loc.Get(auth == ShellAuthState.Offline ? Strings.Shell.Reconnect : Strings.Shell.SignIn),
            () => { if (b?.SignIn is { } signIn) signIn(); else _ = b?.Session.ConnectAsync(); });
    }
}

sealed class MergedSearchField : Component
{
    readonly Signal<string> _text;
    readonly Action<string, string?> _go;
    readonly OmnibarSuggestStore _suggest;
    readonly IReadSignal<int> _focusRequest;
    readonly Signal<bool> _focused;
    readonly IReadSignal<MergedChromeLayout> _layout;
    readonly IReadSignal<float> _avail;

    public MergedSearchField(Signal<string> text, Action<string, string?> go, OmnibarSuggestStore suggest,
        IReadSignal<int> focusRequest, Signal<bool> focused, IReadSignal<MergedChromeLayout> layout, IReadSignal<float> avail)
    {
        _text = text; _go = go; _suggest = suggest; _focusRequest = focusRequest; _focused = focused;
        _layout = layout; _avail = avail;
    }

    public override Element Render()
    {
        var hooks = UseContext(InputHooks.Current);
        var field = UseRef<NodeHandle>(default);
        var parts = UseMemo(() =>
        {
            var p = new TemplateParts();
            p[AutoSuggestBox.PartRoot] = b => b with
            {
                OnRealized = h => field.Value = h,
                OnFocusChanged = f => _focused.SetIfChanged(f),
            };
            return p;
        }, DepKey.Empty);

        int request = _focusRequest.Value;
        UseLayoutEffect(() =>
        {
            // PartRoot is the ComboBox chrome, not the editor. OnChar/OnKey walk ancestors only, so focusing
            // the chrome paints a ring that cannot type. FirstFocusableIn lands on the chromeless EditableText;
            // OnFocusChanged on PartRoot still fires because GotFocus bubbles (InputDispatcher.SetFocus).
            if (request <= 0 || field.Value.IsNull) return;
            var chrome = field.Value;
            var editor = hooks.FirstFocusableIn?.Invoke(chrome) ?? NodeHandle.Null;
            if (!editor.IsNull) hooks.FocusNode?.Invoke(editor, true);
        }, DepKey.From(request));

        float width = _layout.Value.SearchWidth;
        float available = _avail.Value;
        if (float.IsFinite(available) && available > 0f) width = MathF.Min(width, available);
        return new BoxEl
        {
            Key = "chrome-search-field",
            Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center,
            Width = width,
            Children = [Embed.Comp(() => new FluentRichOmnibar(_text, _go, _suggest, parts, maxWidth: width))],
        };
    }
}

sealed class MergedSearchFlyoutButton : Component
{
    readonly Signal<string> _text;
    readonly Action<string, string?> _go;
    readonly OmnibarSuggestStore _suggest;
    readonly IReadSignal<int> _focusRequest;
    readonly Signal<bool> _openState;

    public MergedSearchFlyoutButton(Signal<string> text, Action<string, string?> go, OmnibarSuggestStore suggest,
        IReadSignal<int> focusRequest, Signal<bool> openState)
    { _text = text; _go = go; _suggest = suggest; _focusRequest = focusRequest; _openState = openState; }

    public override Element Render()
    {
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var overlay = UseContext(Overlay.Service);
        var viewport = UseContext(Viewport.Size);
        float flyoutWidth = MathF.Max(ShellResponsiveLayout.ChromeSearchIconW,
            MathF.Min(ShellResponsiveLayout.ChromeSearchMaxW, viewport.Width - 2f * Spacing.M));

        void Close()
        {
            handle.Value?.Close();
            handle.Value = null;
            _openState.SetIfChanged(false);
        }

        void Open()
        {
            if (handle.Value is { IsOpen: true }) return;
            void GoAndClose(string route, string? arg) { Close(); _go(route, arg); }
            handle.Value = overlay.Open(
                () => anchor.Value,
                () => new BoxEl
                {
                    Direction = 1, Width = flyoutWidth, MinWidth = flyoutWidth,
                    Children =
                    [
                        Embed.Comp(() => new FluentRichOmnibar(
                            _text, GoAndClose, _suggest, maxWidth: flyoutWidth,
                            suggestionPresentation: AutoSuggestBoxSuggestionPresentation.Inline,
                            allowNarrowSuggestions: true)),
                    ],
                },
                FlyoutPlacement.BottomEdgeAlignedRight,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup)
                { ConstrainToRootBounds = true });
            _openState.SetIfChanged(true);
            handle.Value.ClosedAction = () => { handle.Value = null; _openState.SetIfChanged(false); };
        }

        int request = _focusRequest.Value;
        UseLayoutEffect(() => { if (request > 0) Open(); }, DepKey.From(request));
        UseEffect(() => (Action)(() => Close()), DepKey.Empty);

        void Toggle() { if (handle.Value is { IsOpen: true }) Close(); else Open(); }
        // Same ShellToolbar.BarNavStyle + BarNavMargin geometry as the theme toggle beside it (ChromeSearchIconW
        // tracks the change), not the old hand-rolled 32-DIP box.
        return ToolTip.Wrap(
            IconButton.Create(Icons.Search, Toggle, ShellToolbar.BarNavStyle)
                with { Key = "chrome-search-button", Margin = ShellToolbar.BarNavMargin, OnRealized = h => anchor.Value = h },
            Loc.Get(Strings.Nav.Search));
    }
}
