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
        int auth = (int)(Bridge?.Auth.Value ?? AuthStatus.LoggedOut);
        int flags = (l.ShowName ? 1 : 0) | (l.ShowFriends ? 2 : 0) | (l.ShowForward ? 4 : 0)
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
                _searchText, _go, _searchFocusRequest, _searchFocused, _layout, avail))
            : new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };
    }

    public Element CaptionLeading()
    {
        var l = _layout.Value;
        var kids = new List<Element>(2) { ThemeToggle() };
        if (l.SearchMode == MergedSearchMode.Icon)
            kids.Add(Embed.Comp(() => new MergedSearchFlyoutButton(
                _searchText, _go, _searchFocusRequest, _searchFlyoutOpen)));
        return new BoxEl
        {
            Direction = 0,
            Height = TitleBar.ExpandedHeight,
            Shrink = 0f,
            AlignItems = FlexAlign.Center,
            Children = kids.ToArray(),
        };
    }

    Element ThemeToggle() => ToolTip.Wrap(new BoxEl
    {
        Key = "chrome-theme-toggle",
        Width = ShellResponsiveLayout.ChromeThemeToggleW,
        Height = Spacing.XXXL,
        Shrink = 0f,
        AlignItems = FlexAlign.Center,
        Justify = FlexJustify.Center,
        Corners = Radii.ControlAll,
        Role = AutomationRole.Button,
        Cursor = CursorId.Hand,
        Focusable = true,
        OnClick = _toggleTheme,
        Children = [Icon(Theme.Dark ? Icons.Sun : Icons.Moon, Spacing.L, Tok.TextSecondary)],
    }.Interactive(Interaction.Subtle),
        Theme.Dark ? Loc.Get(Strings.Shell.LightTheme) : Loc.Get(Strings.Shell.DarkTheme));

    public Element Trailing()
    {
        var l = _layout.Value;
        if (!l.ShowTrailing) return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };
        var kids = new List<Element>(3) { ProfileChip() };
        if (l.FriendsInRow)
            kids.Add(IconButton.Create(Icons.Friends, ToggleFriends, ShellToolbar.BarNavStyle)
                with { Margin = ShellToolbar.BarNavMargin });
        if (OverflowItems(l).Count > 0)
            kids.Add(Embed.Comp(() => new OverflowMenu(this, _layout)));
        return new BoxEl
        {
            Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center,
            Height = TitleBar.ExpandedHeight, Children = kids.ToArray(),
        };
    }

    internal void ToggleFriends() => Ui?.Toggle(RailMode.Friends);

    Element ProfileChip()
    {
        var b = Bridge;
        var auth = b?.Auth.Value ?? AuthStatus.LoggedOut;
        if (auth == AuthStatus.Authenticated)
            return Embed.Comp(() => new ProfileMenu(b!, _layout, _toggleTheme, ToggleFriends));
        if (auth == AuthStatus.Authenticating)
            return new BoxEl
            {
                Height = 32f, AlignItems = FlexAlign.Center, Padding = new Edges4(8f, 0f, 8f, 0f),
                Children = [Caption(Loc.Get(Strings.Shell.Connecting)).Secondary()],
            };
        return Button.Accent(Loc.Get(Strings.Shell.SignIn), () => { _ = b?.Session.ConnectAsync(); });
    }

    internal List<MenuFlyoutItem> OverflowItems(MergedChromeLayout l)
    {
        var items = new List<MenuFlyoutItem>(2);
        if (!l.ShowForward)
            items.Add(new MenuFlyoutItem(Loc.Get(Strings.Nav.Forward), Icons.Forward,
                Enabled: _canForward.Value, Invoke: _forward));
        if (Acts is { } acts && acts.CurrentDestination?.Invoke() is { } destination
            && PinActions.RowForDestination(acts, in destination) is { } pagePin)
        {
            if (items.Count > 0 && !items[^1].IsSeparator) items.Add(MenuFlyoutItem.Separator);
            items.Add(pagePin);
        }
        return items;
    }
}

sealed class MergedSearchField : Component
{
    readonly Signal<string> _text;
    readonly Action<string, string?> _go;
    readonly IReadSignal<int> _focusRequest;
    readonly Signal<bool> _focused;
    readonly IReadSignal<MergedChromeLayout> _layout;
    readonly IReadSignal<float> _avail;

    public MergedSearchField(Signal<string> text, Action<string, string?> go, IReadSignal<int> focusRequest,
        Signal<bool> focused, IReadSignal<MergedChromeLayout> layout, IReadSignal<float> avail)
    { _text = text; _go = go; _focusRequest = focusRequest; _focused = focused; _layout = layout; _avail = avail; }

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
            Children = [Embed.Comp(() => new FluentRichOmnibar(_text, _go, parts, maxWidth: width))],
        };
    }
}

sealed class MergedSearchFlyoutButton : Component
{
    readonly Signal<string> _text;
    readonly Action<string, string?> _go;
    readonly IReadSignal<int> _focusRequest;
    readonly Signal<bool> _openState;

    public MergedSearchFlyoutButton(Signal<string> text, Action<string, string?> go,
        IReadSignal<int> focusRequest, Signal<bool> openState)
    { _text = text; _go = go; _focusRequest = focusRequest; _openState = openState; }

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
                            _text, GoAndClose, maxWidth: flyoutWidth,
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
        return ToolTip.Wrap(new BoxEl
        {
            Key = "chrome-search-button",
            Width = ShellResponsiveLayout.ChromeSearchIconW, Height = 32f, Shrink = 0f,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = Radii.ControlAll,
            Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true,
            OnRealized = h => anchor.Value = h,
            OnClick = Toggle,
            Children = [Icon(Icons.Search, 16f, Tok.TextSecondary)],
        }.Interactive(Interaction.Subtle), Loc.Get(Strings.Nav.Search));
    }
}
