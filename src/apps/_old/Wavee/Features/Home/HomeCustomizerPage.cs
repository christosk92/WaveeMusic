using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;
using Wavee.Core.Home;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>The Home customizer destination. Route key <see cref="Route"/> — ContentHost / WaveeShell must register
/// the page (this file only exposes the factory). v1 is visibility toggles + drag order over the fixed landing
/// modules; dynamic deck ids are on the document for later.</summary>
sealed class HomeCustomizerPage : Component
{
    public const string Route = "home-customize";
    public const float HeaderHeight = 64f;
    public const float ColumnMaxWidth = 720f;
    public const float RowExtent = 48f;

    readonly Reorderable _reorder = new("wavee.home-layout")
    {
        ItemExtent = RowExtent,
        Spacing = 0f,
        DragStyle = new DragVisualStyle { Lift = DragLift.Stationary, Opacity = Drag.SourceDimOpacity },
        RequireDropOnList = true,
    };

    HomePreferences? _prefs;
    Action<string, string?>? _go;
    Action? _back;
    HistoryStore? _history;
    readonly Signal<int> _bannerEpoch = new(0);
    bool _corruptDismissed;

    /// <summary>The page factory ContentHost / WaveeShell should mount for <see cref="Route"/>.</summary>
    public static HomeCustomizerPage Create() => new();

    public override Element Render()
    {
        _prefs = UseContext(HomePreferences.Slot);
        _go = UseContext(HistoryStore.NavCtx);
        _back = UseContext(HistoryStore.BackCtx);
        _history = UseContext(HistoryStore.Slot);
        int layoutVersion = _prefs?.LayoutVersion.Value ?? 0;
        _ = layoutVersion;
        _ = _bannerEpoch.Value;

        var prefs = _prefs;
        var modules = prefs?.Layout.Modules ?? HomeLayoutDoc.Default.Modules;

        _reorder.Scene = Context.Scene;
        _reorder.RequestRender = Context.RequestRerender;
        _reorder.ItemCount = modules.Count;
        _reorder.ItemOf = slot => (uint)slot < (uint)modules.Count ? modules[slot].Kind : null;
        _reorder.OnReorder = (from, to) => prefs?.Dispatch(new MoveHomeModule(from, to));

        var rows = new List<Element>(modules.Count);
        for (int i = 0; i < modules.Count; i++)
        {
            int slot = _reorder.ItemAt(i);
            var spec = (uint)slot < (uint)modules.Count ? modules[slot] : modules[i];
            string key = HomeLayoutModules.KindName(spec.Kind);
            var kind = spec.Kind;
            rows.Add(_reorder.Item(slot,
                Embed.Comp(() => new HomeCustomizeRow(kind)) with { Key = "mod:" + key },
                key: key));
        }

        return new BoxEl
        {
            Key = "home-customizer", Grow = 1f, Shrink = 1f, Direction = 1, MinWidth = 0f, MinHeight = 0f,
            ClipToBounds = true,
            Children = [HeaderBar(), Divider(), Banners(), Body(_reorder.List(new BoxEl
            {
                Direction = 1, MinWidth = 0f,
                Children = [.. rows],
            }))],
        };
    }

    Element HeaderBar()
    {
        var prefs = _prefs;
        return new BoxEl
        {
            Key = "cmdbar", Direction = 0, Height = HeaderHeight, Shrink = 0f, Gap = Spacing.S,
            AlignItems = FlexAlign.Center,
            Padding = new Edges4(Spacing.S, 0f, Spacing.L, 0f),
            Children =
            [
                new BoxEl
                {
                    Shrink = 0f,
                    Children =
                    [
                        ToolTip.Wrap(
                            IconButton.Create(Icons.Back, GoBack, size: ControlSize.Small) with { Shrink = 0f },
                            Loc.Get(Strings.Home.Customizer.Back)),
                    ],
                },
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, Shrink = 1f, MinWidth = 0f, Justify = FlexJustify.Center,
                    Children =
                    [
                        WaveeType.Eyebrow(Loc.Get(Strings.Home.Customizer.Eyebrow)) with
                        {
                            Color = WaveeAccent.Decor, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                        },
                        new TextEl(Loc.Get(Strings.Home.Customizer.Title))
                        {
                            Size = 16f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1,
                            Trim = TextTrim.CharacterEllipsis,
                        },
                    ],
                },
                new BoxEl
                {
                    Direction = 0, Shrink = 0f, Gap = Spacing.XS, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        Button.Create(Loc.Get(Strings.Home.Customizer.Reset), () => prefs?.Dispatch(new ResetHomeLayout()),
                            ButtonAppearance.Subtle, ControlSize.Small) with { Shrink = 0f },
                        Button.Create(Loc.Get(Strings.Home.Customizer.Done), GoBack,
                            ButtonAppearance.Accent, ControlSize.Small) with { Shrink = 0f },
                    ],
                },
            ],
        };
    }

    Element Banners()
    {
        var prefs = _prefs;
        if (prefs is not { Fault: not HomeLayoutLoadFault.None } || _corruptDismissed)
            return new BoxEl { Key = "banners", Height = 0f, Shrink = 0f };

        return new BoxEl
        {
            Key = "banners", Direction = 1, Shrink = 0f,
            Padding = new Edges4(Spacing.L, Spacing.S, Spacing.L, 0f),
            Children =
            [
                InfoBar.Create(
                    InfoBarSeverity.Warning,
                    Loc.Get(Strings.Home.Customizer.Corrupt),
                    Loc.Get(Strings.Home.Customizer.CorruptSub),
                    onClose: () => { _corruptDismissed = true; _bannerEpoch.Value = _bannerEpoch.Peek() + 1; },
                    actionButton: Button.Create(Loc.Get(Strings.Home.Customizer.FaultDiscard),
                        () => prefs.DiscardCorrupt(), ButtonAppearance.Standard, ControlSize.Small)),
            ],
        };
    }

    Element Body(Element list) => ScrollView(new BoxEl
    {
        Direction = 1, Gap = Spacing.L, MaxWidth = ColumnMaxWidth, MinWidth = 0f,
        Padding = new Edges4(Spacing.L, Spacing.M, Spacing.L, Spacing.XL),
        Children =
        [
            new TextEl(Loc.Get(Strings.Home.Customizer.HiddenHint))
            {
                Size = 12f, Color = Tok.TextTertiary, MaxLines = 3,
            },
            list,
        ],
    }) with
    {
        Key = "home-customizer-column", Grow = 1f, Shrink = 1f, MinHeight = 0f, MinWidth = 0f,
        AutoEdgeFade = true, ScrollKey = "home.customizer",
    };

    void GoBack()
    {
        _prefs?.WaitForWrites(200);
        if (_back is { } back) { back(); return; }
        if (_history?.Entries is { Count: > 0 } log)
        {
            for (int i = log.Count - 1; i >= 0; i--)
            {
                var route = log[i].Route;
                if (string.Equals(route.Name, Route, StringComparison.Ordinal)) continue;
                _go?.Invoke(route.Name, route.Arg);
                return;
            }
        }
        _go?.Invoke("home", null);
    }
}

/// <summary>Header affordance on Home — a quiet overflow that navigates to <see cref="HomeCustomizerPage.Route"/>.
/// Not a FAB: it sits in the greeting/chips chrome row.</summary>
static class HomeCustomizeAffordance
{
    public static Element Button(Action<string, string?> go)
        => Embed.Comp(() => new HomeCustomizeButton(go)) with { Key = "home-customize-entry" };
}

sealed class HomeCustomizeButton : Component
{
    readonly Action<string, string?> _go;
    public HomeCustomizeButton(Action<string, string?> go) => _go = go;

    public override Element Render()
    {
        var overlay = UseContext(Overlay.Service);
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);

        void Toggle()
        {
            if (overlay is null)
            {
                _go(HomeCustomizerPage.Route, Loc.Get(Strings.Home.Customizer.Title));
                return;
            }
            if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
            var items = new[]
            {
                new MenuFlyoutItem(Loc.Get(Strings.Home.Customizer.Title), Icons.Edit, true,
                    () => _go(HomeCustomizerPage.Route, Loc.Get(Strings.Home.Customizer.Title))),
            };
            handle.Value = overlay.Open(
                () => anchor.Value,
                () => MenuFlyout.Create(items, () => handle.Value?.Close(), minWidth: 200f),
                FlyoutPlacement.BottomEdgeAlignedRight,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup)
                { ConstrainToRootBounds = false });
            handle.Value.ClosedAction = () => handle.Value = null;
        }

        return ToolTip.Wrap(new BoxEl
        {
            Width = 28f, Height = 28f, Shrink = 0f,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = Radii.ControlAll,
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
            OnRealized = h => anchor.Value = h,
            OnClick = Toggle,
            Children = [Icon(Icons.More, 14f, Tok.TextSecondary)],
        }.Interactive(Interaction.Subtle), Loc.Get(Strings.Home.Customize));
    }
}

sealed class HomeCustomizeRow : Component
{
    readonly HomeGroupKind _kind;
    readonly Signal<bool> _visible = new(true);

    public HomeCustomizeRow(HomeGroupKind kind) => _kind = kind;

    public override Element Render()
    {
        var prefs = UseContext(HomePreferences.Slot);
        int epoch = prefs?.LayoutVersion.Value ?? 0;
        bool hidden = prefs?.Layout.IsHidden(_kind) ?? false;
        UseLayoutEffect(() => _visible.SetIfChanged(!hidden), DepKey.From(hidden ? 1 : 0, epoch));

        return new BoxEl
        {
            Direction = 0, Height = HomeCustomizerPage.RowExtent, AlignItems = FlexAlign.Center,
            Gap = Spacing.S, MinWidth = 0f, Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f),
            Opacity = hidden ? 0.55f : 1f,
            Children =
            [
                new BoxEl
                {
                    Width = 12f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    HitTestVisible = false,
                    Children = [Icon(Icons.GripperBar, 12f, Tok.TextTertiary)],
                },
                new TextEl(HomeCustomizeLabels.Of(_kind))
                {
                    Size = 14f, Color = Tok.TextPrimary, Grow = 1f, Basis = 0f, Shrink = 1f, MinWidth = 0f,
                    MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                },
                ToggleSwitch.Create(_visible, v => prefs?.Dispatch(new SetHomeModuleHidden(_kind, !v))),
            ],
        };
    }
}

static class HomeCustomizeLabels
{
    public static string Of(HomeGroupKind kind)
    {
        var t = HomeModuleCopy.Titles;
        return kind switch
        {
            HomeGroupKind.Hero => Loc.Get(Strings.Home.Customizer.Hero),
            HomeGroupKind.WeeklyPair => Loc.Get(Strings.Home.Customizer.WeeklyPair),
            HomeGroupKind.QuickGrid => t.JumpBackIn,
            HomeGroupKind.Recents => t.Recents,
            HomeGroupKind.MixBand => t.MadeForYou,
            HomeGroupKind.ChipCards => t.TopMixes,
            HomeGroupKind.RadioDial => t.Radio,
            HomeGroupKind.QueueList => t.UpNext,
            HomeGroupKind.RatedShelf => t.Audiobooks,
            HomeGroupKind.PodcastShelf => t.Podcasts,
            HomeGroupKind.Featured => t.EditorsPicks,
            HomeGroupKind.DiscoverFeed => t.BecauseYouListened,
            _ => HomeLayoutModules.KindName(kind),
        };
    }
}
