// ── Themes/Common/CommonMixMenu.cs — the Mix editor's choice menus, shared by every theme (#48 M6) ──────────────────────────────────────
//
// A button that names what is chosen and opens the choices as a radio menu: a Customize ingredient's styles, the thirteen presets under
// "More". The theme picks the button's appearance; the menu is the stock MenuFlyout (each theme restyles it through its own keys).

using System.Collections.Immutable;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Common;

public sealed record MixMenuProps(string Label, ImmutableArray<MixChoice> Choices, Action<int> Pick, ButtonAppearance Appearance, bool Enabled = true,
    ControlSize Size = ControlSize.Medium)
{
    /// <summary>The button's least height (a theme's touch target); 0 = the control's own.</summary>
    public float Height { get; init; }
}

public static class MixChoiceMenu
{
    /// <summary>A menu button keyed <paramref name="key"/> (its label live: the props are re-pushed).</summary>
    public static Element Create(string key, MixMenuProps props) => Embed.Comp(props, static () => new MixMenuHost()) with { Key = key };
}

internal sealed class MixMenuHost : Component
{
    MixMenuProps _p = null!;
    IOverlayService? _overlay;
    NodeHandle _anchor;
    OverlayHandle? _menu;
    Action? _toggle;
    Action<NodeHandle>? _capture;

    public override Element Render()
    {
        _p = UseProps<MixMenuProps>();
        _overlay = UseContext(Overlay.Service);
        _toggle ??= Toggle;
        _capture ??= h => _anchor = h;
        return Button.Create(_p.Label, _toggle, _p.Appearance, _p.Size, glyph: Icons.ChevronDown, isEnabled: _p.Enabled) with
        {
            OnRealized = _capture, MinWidth = 0f, Shrink = 1f, MinHeight = _p.Height,
        };
    }

    void Toggle()
    {
        if (_menu is { IsOpen: true } shown) { shown.Close(); return; }
        if (Controls.IsNullOverlay(_overlay)) return;
        var items = new List<MenuFlyoutItem>(_p.Choices.Length);
        var pick = _p.Pick;
        foreach (var c in _p.Choices)
        {
            int id = c.Id;
            items.Add(MenuFlyoutItem.RadioItem(c.Label, c.On, () => pick(id)));
        }
        OverlayHandle? opened = null;
        opened = _overlay!.Open(() => _anchor, () => MenuFlyout.Create(items, () => opened?.Close(), size: OverlayFit.MenuSize),
            FlyoutPlacement.BottomEdgeAlignedLeft, new PopupOptions(FocusTrap: true) { ConstrainToRootBounds = false });
        _menu = opened;
    }
}
