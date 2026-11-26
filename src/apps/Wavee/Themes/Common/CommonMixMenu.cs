// ── Themes/Common/CommonMixMenu.cs — the Mix editor's choice menus, shared by every theme (#48 M6) ──────────────────────────────────────
//
// A button that names what is chosen and opens the choices as a radio menu: a Customize ingredient's styles, the thirteen presets under
// "More". The theme picks the button's appearance and where its chevron sits (WinUI: trailing at the right edge, as its ComboBox and
// DropDownButton draw it; elsewhere a leading glyph); the menu is the stock MenuFlyout (each theme restyles it through its own keys).

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
    /// <summary>The chevron trails the label at the button's right edge (WinUI's DropDownButton: a 12-DIP box 8 DIP after the label, the small
    /// chevron in the secondary foreground) instead of leading it as a glyph.</summary>
    public bool TrailingChevron { get; init; }
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
        if (!_p.TrailingChevron)
            return Button.Create(_p.Label, _toggle, _p.Appearance, _p.Size, glyph: Icons.ChevronDown, isEnabled: _p.Enabled) with
            {
                OnRealized = _capture, MinWidth = 0f, Shrink = 1f, MinHeight = _p.Height,
            };
        var button = Button.Create(_p.Label, _toggle, _p.Appearance, _p.Size, isEnabled: _p.Enabled);
        var label = (TextEl)button.Children[0];
        // The chevron keeps the label's ramp on an accent button (its foreground is the accent's), the secondary one otherwise.
        bool accent = _p.Appearance == ButtonAppearance.Accent;
        var chevron = new BoxEl
        {
            Width = TrailingBox, Height = TrailingBox, Shrink = 0f, Margin = new Edges4(Spacing.S, 0f, 0f, 0f),
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Children =
            [
                new TextEl(Icons.ChevronDownSmall)
                {
                    Size = TrailingGlyph, FontFamily = Tok.Definition.Fonts.Icon,
                    Color = accent ? label.Color : Tok.TextSecondary, HoverColor = accent ? label.HoverColor : Tok.TextTertiary,
                    PressedColor = accent ? label.PressedColor : Tok.TextTertiary, DisabledColor = Tok.TextDisabled,
                },
            ],
        };
        return button with
        {
            OnRealized = _capture, MinWidth = 0f, Shrink = 1f, MinHeight = _p.Height,
            Children = [label with { Grow = 1f, Shrink = 1f }, chevron],
        };
    }

    // WinUI's DropDownButton chevron: the AnimatedIcon's 12 × 12 box over its FontIcon fallback at 8.
    const float TrailingBox = 12f, TrailingGlyph = 8f;

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
