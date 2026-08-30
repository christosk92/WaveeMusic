using System;
using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Features.Browse;

namespace Wavee;

/// <summary>THE one masthead ("Browse › Title") — mounted ONCE, as an overlay on ContentHost's KeepAlive page-swap
/// boundary, so a page swap never double-exposes it and crossing out of the family never changes KeepAlive height.
///
/// <para>Content predicate: <see cref="ShellMastheadRegistry"/> is the ONE family check (browse home / browse: /
/// browse-section: / home-section: / Concerts). Dynamics (live title, caption, tools) come from
/// <see cref="ShellMastheadStore.For"/> the active route. A page publishes with one deps-leg effect — no owner
/// tokens. Unknown families keep the last trail and fade opacity (they do not collapse to Height 0).</para></summary>
sealed class ShellMastheadBand : Component
{
    static readonly MotionTokenDef Hide = MotionTokenDef.Eased(
        PageNavMotion.FadeThroughExitMs, Easing.FluentAccelerate, ReducedMotionPolicy.KeepFade);
    static readonly MotionTokenDef Show = MotionTokenDef.Eased(
        PageNavMotion.FadeThroughExitMs, Easing.SmoothOut, ReducedMotionPolicy.KeepFade);

    readonly Signal<Route> _route;
    IReadOnlyList<DrillCrumb> _heldTrail = [];
    string? _heldTitle;
    bool _heldToolsVisible;
    bool _heldToolsLoading;
    Action? _heldToolsAction;

    public ShellMastheadBand(Signal<Route> route) => _route = route;

    public override Element Render()
    {
        var store = UseContext(ShellMasthead.Slot);
        var origins = UseContext(HistoryStore.OriginSlot);
        var go = UseContext(HistoryStore.NavCtx);
        var route = _route.Value;
        var published = store?.For(route.Name, route.Arg);
        var origin = origins?.For(route.Name, route.Arg);

        bool live = ShellMastheadRegistry.TryResolve(route.Name, route.Arg, published?.Title, origin, out var title, out var trail);
        if (live)
        {
            _heldTitle = title;
            _heldTrail = trail;
            _heldToolsVisible = published is { ToolsVisible: true };
            _heldToolsLoading = published?.ToolsLoading ?? false;
            _heldToolsAction = published?.ToolsAction;
        }

        if (_heldTitle is null)
            return new BoxEl { MinWidth = 0f, Opacity = 0f, HitTestVisible = false };

        Element? tools = _heldToolsVisible
            ? Button.Create(Loc.Get(Strings.Browse.ShowAll), _heldToolsAction ?? (static () => { }),
                ButtonAppearance.Subtle, ControlSize.Small, isEnabled: !_heldToolsLoading)
            : null;

        var masthead = TitleRow(_heldTrail, go, _heldTitle);
        return new BoxEl
        {
            Direction = 0, MinWidth = 0f, Gap = Spacing.M, AlignItems = FlexAlign.End,
            Padding = new Edges4(BrowseLayout.FrameX, BrowseLayout.FrameTop, BrowseLayout.FrameX, 0f),
            Opacity = live ? 1f : 0f,
            HitTestVisible = live,
            Transition = live ? Show : Hide,
            Children = [masthead, tools ?? new BoxEl()],
        };
    }

    /// <summary>The trail-as-title row: every crumb but the last renders dimmed + clickable, followed by a <c>›</c>
    /// separator, then the CURRENT title. Titles snap with the route — no Enter/Exit swap. The current title keeps a
    /// stable key so Browse-home → category does not remount it.
    /// <para>When there is no parent crumb the prefix child is omitted entirely. A zero-width first sibling still
    /// participates in the row and padded Browse-home a rung right of the body.</para></summary>
    static Element TitleRow(IReadOnlyList<DrillCrumb> trail, Action<string, string?> go, string title)
    {
        var segs = new List<Element>(Math.Max(0, trail.Count - 1) * 2);
        int last = trail.Count - 1;
        for (int i = 0; i < last; i++)
        {
            var crumb = trail[i];
            string? routeName = crumb.RouteName;
            string? routeArg = crumb.RouteArg;
            var label = WaveeType.SurfaceDisplay(crumb.Label) with
            {
                Color = Tok.TextTertiary, HoverColor = Tok.TextSecondary, PressedColor = Tok.TextTertiary,
                MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 0f,
            };
            segs.Add(new BoxEl
            {
                Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
                OnClick = routeName is { Length: > 0 } ? () => go(routeName, routeArg) : null,
                Children = [label],
            });
            segs.Add(WaveeType.SurfaceDisplay("›") with { Color = Tok.TextTertiary, Shrink = 0f });
        }
        bool hasPrefix = segs.Count > 0;
        var current = WaveeType.SurfaceDisplay(title) with
        {
            Key = "masthead-current",
            MaxLines = 2, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            Grow = 1f, Basis = 0f, Shrink = 1f,
        };
        if (!hasPrefix)
        {
            return new BoxEl
            {
                Direction = 0, Grow = 1f, Basis = 0f, MinWidth = 0f, AlignItems = FlexAlign.End,
                Children = [current],
            };
        }
        var prefix = new BoxEl
        {
            Key = "masthead-prefix:" + trail[0].Label,
            Direction = 0, AlignItems = FlexAlign.End, Gap = Spacing.S, Shrink = 0f,
            Children = segs.ToArray(),
        };
        return new BoxEl
        {
            Direction = 0, Grow = 1f, Basis = 0f, MinWidth = 0f, AlignItems = FlexAlign.End,
            Gap = Spacing.S,
            Children = [prefix, current],
        };
    }
}
