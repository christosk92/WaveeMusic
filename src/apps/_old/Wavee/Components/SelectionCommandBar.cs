using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// Contextual content for the detail-track CommandBar. TrackList owns the single persistent surface and swaps this
// projection in place of the browsing commands. Self-measures the command lane (ResponsiveBox pattern) so labels
// collapse to glyphs / essentials — AND reads SelectionModel.Version in the SAME Render that builds count + action
// labels (never via a frozen Responsive.Of closure, which previously left "1 selected" beside "Play 4 next").
sealed class SelectionCommandBar : Component
{
    readonly SelectionModel _sel;
    readonly Func<int, Track?> _trackAt;
    readonly Action _exit;
    readonly Func<PlaylistHost?>? _host;
    readonly bool _standalone;
    readonly int _minCount = 1;
    readonly float _bottomPadding;
    readonly Signal<float> _laneW = new(0f);

    public SelectionCommandBar(
        SelectionModel sel,
        Func<int, Track?> trackAt,
        Action exit,
        Func<PlaylistHost?>? host = null)
    {
        _sel = sel;
        _trackAt = trackAt;
        _exit = exit;
        _host = host;
    }

    // Compatibility projection for non-detail surfaces that still mount the batch bar as an overlay.
    public SelectionCommandBar(
        SelectionModel sel,
        Func<int, Track?> trackAt,
        float bottomPadding = Spacing.XL,
        Func<PlaylistHost?>? host = null)
    {
        _sel = sel;
        _trackAt = trackAt;
        _exit = sel.DeselectAll;
        _host = host;
        _standalone = true;
        // Overlay hosts have no explicit multi-select mode. A plain click is a 1-row highlight (ListView Extended);
        // the batch bar is for a real multi-selection — the same 2+ rule as the drawer's checkbox lane.
        _minCount = 2;
        _bottomPadding = bottomPadding;
    }

    public override Element Render()
    {
        var acts = UseContext(ActionServices.Slot);
        var overlay = UseContext(Overlay.Service);
        var menuAnchor = UseRef<NodeHandle>(default);
        var menuHandle = UseRef<OverlayHandle?>(null);
        var previousCount = UseRef(0);

        _ = _sel.Version.Value;                      // selection → rebuild count + action labels together
        float lane = _laneW.Value;                   // measured width → fit tier
        float effective = lane > 0.5f ? lane : 720f;
        int count = SelectedTrackCount();
        bool wasVisible = previousCount.Value > 0;
        previousCount.Value = count;
        if (count < _minCount) return new BoxEl();

        Element content = new BoxEl
        {
            Direction = 1,
            Grow = 1f,
            MinWidth = 0f,
            // Parent sizes us; report the real lane width (same contract as ResponsiveBox).
            OnBoundsChanged = r =>
            {
                if (r.W > 0f && MathF.Abs(r.W - _laneW.Peek()) > 0.5f)
                    _laneW.Value = r.W;
            },
            Children = [Build(acts, overlay, menuAnchor, menuHandle, count, FitFor(effective), wasVisible)],
        };
        if (!_standalone) return content;

        return new BoxEl
        {
            Direction = 1,
            Grow = 1f,
            HitTestPassThrough = true,
            AlignItems = FlexAlign.Center,
            Justify = FlexJustify.End,
            Padding = new Edges4(Spacing.L, 0f, Spacing.L, _bottomPadding),
            Children =
            [
                new BoxEl
                {
                    Direction = 1,
                    MinWidth = 0f,
                    Padding = new Edges4(8f, 6f, 8f, 6f),
                    Corners = CornerRadius4.All(Radii.Card),
                    Acrylic = Tok.AcrylicFlyout,
                    BorderWidth = 1f,
                    BorderColor = Tok.StrokeFlyoutDefault,
                    Shadow = Elevation.Flyout,
                    Children = [content],
                },
            ],
        };
    }

    // Actual command-lane width: 0 = labels, 1 = glyphs, 2 = essentials.
    static int FitFor(float width) => width >= 760f ? 0 : width >= 390f ? 1 : 2;

    ActionContext SelectionCtx(ActionServices acts) =>
        new(ActionTarget.ForTracks(SelectedTracks(), _host?.Invoke()), acts);

    Element Build(
        ActionServices? acts,
        IOverlayService overlay,
        Ref<NodeHandle> menuAnchor,
        Ref<OverlayHandle?> menuHandle,
        int count,
        int fit,
        bool wasVisible)
    {
        var children = new List<Element>(12);
        if (fit <= 1)
        {
            var thumbs = BuildThumbs();
            if (thumbs.Length > 0)
                children.Add(new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Children = thumbs });
        }

        children.Add(new BoxEl
        {
            Key = "selection-count:" + count,
            Animate = wasVisible ? MotionRecipes.TextSwap : MotionRecipes.TextSwap with { Enter = default },
            MinWidth = fit == 2 ? 66f : float.NaN,
            Children =
            [
                new TextEl(Strings.Detail.SelectedCount(count))
                {
                    Size = 12f,
                    Weight = 650,
                    Color = Tok.TextPrimary,
                    MaxLines = 1,
                    Trim = TextTrim.CharacterEllipsis,
                },
            ],
        });
        children.Add(Divider());

        if (acts is not null)
        {
            var ctx = SelectionCtx(acts);
            children.Add(ActionButton(TrackActions.Play, in ctx, fit));
            if (fit <= 1)
            {
                children.Add(ActionButton(TrackActions.PlayNext, in ctx, fit));
                children.Add(ActionButton(TrackActions.AddToQueue, in ctx, fit));
                children.Add(ActionButton(TrackActions.ToggleLike, in ctx, fit));
                children.Add(Divider());
                children.Add(Command(Icons.Accept, Loc.Get(Strings.Detail.SelectAll), fit, SelectAllTracks));
            }

            children.Add(ToolTip.Wrap(
                GlyphButton(
                    Icons.More,
                    () => ToggleMenu(acts, overlay, menuAnchor, menuHandle, fit),
                    realized: h => menuAnchor.Value = h),
                Loc.Get(Strings.Common.More)));
        }

        children.Add(new BoxEl { Grow = 1f, MinWidth = 0f });
        children.Add(ToolTip.Wrap(
            GlyphButton(Icons.Cancel, _exit),
            Loc.Get(Strings.Detail.ClearSelection)));

        return new BoxEl
        {
            Direction = 0,
            AlignItems = FlexAlign.Center,
            Gap = 3f,
            Grow = 1f,
            MinWidth = 0f,
            ClipToBounds = true,
            Children = children.ToArray(),
        };
    }

    Element ActionButton(AppAction action, in ActionContext ctx, int fit)
    {
        var c = ctx;
        bool enabled = action.EnabledFor(c);
        var icon = ActionIcons.Resolve(action.IconKey, action.IsChecked?.Invoke(c) ?? false);
        Action invoke = enabled
            ? () => { action.Execute(c); _exit(); }
            : static () => { };
        return Command(icon.Glyph ?? "", action.Label(c), fit, invoke, icon.Font, enabled);
    }

    void ToggleMenu(
        ActionServices acts,
        IOverlayService overlay,
        Ref<NodeHandle> menuAnchor,
        Ref<OverlayHandle?> menuHandle,
        int fit)
    {
        if (menuHandle.Value is { IsOpen: true } open)
        {
            open.Close();
            return;
        }

        var ctx = SelectionCtx(acts);
        var items = new List<MenuFlyoutItem>(14);
        if (fit >= 2)
        {
            items.Add(WithExit(TrackActions.PlayNext.ToMenuItem(ctx)));
            items.Add(WithExit(TrackActions.AddToQueue.ToMenuItem(ctx)));
            items.Add(WithExit(TrackActions.ToggleLike.ToMenuItem(ctx)));
            items.Add(MenuFlyoutItem.Separator);
        }

        foreach (var row in Menus.TrackRows(in ctx, showGoToAlbum: false))
            items.Add(WithExit(row));
        items.Add(MenuFlyoutItem.Separator);
        items.Add(new MenuFlyoutItem(
            Loc.Get(Strings.Detail.SelectAll),
            Icons.Accept,
            true,
            SelectAllTracks));

        menuHandle.Value = overlay.Open(
            () => menuAnchor.Value,
            () => MenuFlyout.Create(items, () => menuHandle.Value?.Close()),
            FlyoutPlacement.BottomEdgeAlignedRight,
            ToolFx.MenuPopup);
        menuHandle.Value.ClosedAction = () => menuHandle.Value = null;
    }

    MenuFlyoutItem WithExit(MenuFlyoutItem item)
    {
        if (item.Kind == MenuItemKind.Separator) return item;
        if (item.Kind == MenuItemKind.SubMenu && item.SubItems is { } nested)
        {
            var mapped = new MenuFlyoutItem[nested.Count];
            for (int i = 0; i < nested.Count; i++) mapped[i] = WithExit(nested[i]);
            return item with { SubItems = mapped };
        }
        if (item.Invoke is null) return item;
        var invoke = item.Invoke;
        return item with { Invoke = () => { invoke(); _exit(); } };
    }

    Element[] BuildThumbs()
    {
        var result = new List<Element>(3);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < _sel.ItemCount && result.Count < 3; i++)
        {
            if (!_sel.IsSelected(i) || _trackAt(i) is not { } track) continue;
            string key = track.Image?.Url is { Length: > 0 } url ? url : track.Id;
            if (!seen.Add(key)) continue;
            result.Add(Thumb(track, result.Count));
        }
        return result.ToArray();
    }

    int SelectedTrackCount()
    {
        int count = 0;
        for (int i = 0; i < _sel.ItemCount; i++)
            if (_sel.IsSelected(i) && _trackAt(i) is not null)
                count++;
        return count;
    }

    List<Track> SelectedTracks()
    {
        var tracks = new List<Track>();
        for (int i = 0; i < _sel.ItemCount; i++)
            if (_sel.IsSelected(i) && _trackAt(i) is { } track)
                tracks.Add(track);
        return tracks;
    }

    void SelectAllTracks()
    {
        int first = -1;
        int last = -1;
        for (int i = 0; i < _sel.ItemCount; i++)
        {
            if (_trackAt(i) is null) continue;
            if (first < 0) first = i;
            last = i;
        }
        if (first < 0) return;
        _sel.DeselectAll();
        _sel.SelectRange(first, last);
    }

    static Element Thumb(Track track, int index) => new BoxEl
    {
        Width = 28f,
        Height = 28f,
        Shrink = 0f,
        Corners = CornerRadius4.All(5f),
        ClipToBounds = true,
        Margin = new Edges4(index == 0 ? 0f : -11f, 0f, 0f, 0f),
        BorderWidth = 2f,
        BorderColor = Tok.FillCardSecondary,
        Children =
        [
            Surfaces.Artwork(track.Image, track.Id.GetHashCode() & 0x7fffffff, 28f, 28f, 5f),
        ],
    };

    static Element Divider() => new BoxEl
    {
        Width = 1f,
        Height = 20f,
        Fill = Tok.StrokeDividerDefault,
        Margin = new Edges4(4f, 0f, 4f, 0f),
    };

    static Element Command(
        string glyph,
        string label,
        int fit,
        Action invoke,
        string? font = null,
        bool enabled = true) =>
        fit == 0
            ? LabeledButton(glyph, label, invoke, font, enabled)
            : ToolTip.Wrap(GlyphButton(glyph, invoke, font: font, enabled: enabled), label);

    static Element LabeledButton(
        string glyph,
        string label,
        Action invoke,
        string? font,
        bool enabled) => new BoxEl
    {
        Direction = 0,
        Height = 32f,
        AlignItems = FlexAlign.Center,
        Gap = 6f,
        Padding = new Edges4(9f, 0f, 10f, 0f),
        Corners = CornerRadius4.All(Radii.Control),
        IsEnabled = enabled,
        Focusable = enabled,
        Role = AutomationRole.Button,
        OnClick = invoke,
        Children =
        [
            Icon(glyph, 14f, enabled ? Tok.TextSecondary : Tok.TextDisabled, family: font),
            new TextEl(label)
            {
                Size = 12f,
                Weight = 600,
                Color = enabled ? Tok.TextSecondary : Tok.TextDisabled,
            },
        ],
    }.Interactive(Interaction.Subtle);

    static Element GlyphButton(
        string glyph,
        Action invoke,
        Action<NodeHandle>? realized = null,
        string? font = null,
        bool enabled = true) => new BoxEl
    {
        Width = 32f,
        Height = 32f,
        AlignItems = FlexAlign.Center,
        Justify = FlexJustify.Center,
        Corners = CornerRadius4.All(Radii.Control),
        IsEnabled = enabled,
        Focusable = enabled,
        Role = AutomationRole.Button,
        OnClick = invoke,
        OnRealized = realized,
        Children =
        [
            Icon(glyph, 13f, enabled ? Tok.TextSecondary : Tok.TextDisabled, family: font),
        ],
    }.Interactive(Interaction.Subtle);
}
