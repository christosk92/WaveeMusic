// ── Themes/Common/CommonTouch.cs — the Common blocks' controls at a tier's hit target (themes plan 3.2, TC10) ───────────────────
//
// The Compact rule is a floor, not a size: every hit target is at least 40 x 40 (the brief). A block asks for a control with the layout record's `Target` (0 = the control's own
// size, 40 at Compact) and gets the stock control at that size: a Large button, an icon button of the target's edge, a link and a segmented strip lifted to the target. The one
// stock control that cannot be lifted is the ComboBox (its field is a 32-DIP constant of the engine), so a Compact block draws a picker over the same selection instead: a
// 40-DIP button that opens the choices as a menu. Nothing here decides anything; it draws what the record asks for.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Theming;

namespace Wavee.Themes.Common;

/// <summary>Stock controls at a tier's hit target.</summary>
internal static class CommonTouch
{
    /// <summary>A standard or accent button; a Large (40) button when <paramref name="target"/> asks for it.</summary>
    public static BoxEl Button(string label, Action click, float target, bool accent = false, bool enabled = true, string? glyph = null)
        => target > 0f
            ? FluentGpu.Controls.Button.Create(label, click, accent ? ButtonAppearance.Accent : ButtonAppearance.Standard, ControlSize.Large, glyph, isEnabled: enabled)
            : FluentGpu.Controls.Button.Create(label, click, accent ? ButtonAppearance.Accent : ButtonAppearance.Standard, glyph: glyph, isEnabled: enabled);

    /// <summary>An icon button of the target's edge (the stock icon button's own size where <paramref name="target"/> is 0).</summary>
    public static BoxEl IconButton(string glyph, Action click, float target)
        => target > 0f
            ? FluentGpu.Controls.IconButton.Create(glyph, click, FluentGpu.Controls.IconButton.DefaultStyle with { Size = target })
            : FluentGpu.Controls.IconButton.Create(glyph, click);

    /// <summary>A link no smaller than <paramref name="target"/> tall and wide.</summary>
    public static BoxEl Link(string text, Action click, float target, ControlSize size = ControlSize.Medium)
    {
        BoxEl link = HyperlinkButton.Create(text, click, size: size);
        return target > 0f ? link with { MinHeight = target, MinWidth = target } : link;
    }

    /// <summary>A control root lifted to <paramref name="target"/> (a clickable row, a chip).</summary>
    public static BoxEl Lift(BoxEl box, float target)
        => target > 0f ? box with { MinHeight = target, MinWidth = target } : box;

    /// <summary>The segmented strip at the target's height.</summary>
    public static Element Segmented(IReadOnlyList<SegmentedItem> items, Signal<int> selected, float target, Action<int>? onChange = null)
    {
        if (target <= 0f) return FluentGpu.Controls.Segmented.Create(items, selected, onChange);
        // The strip's height includes the pill's padding top and bottom (an item is the strip less that padding): lift the strip so the ITEM is the target.
        var style = FluentGpu.Controls.Segmented.DefaultStyle;
        style = style with { Height = target + style.Padding.Top + style.Padding.Bottom };
        return FluentGpu.Controls.Segmented.Create(items, selected, onChange, new FluentGpu.Controls.Segmented.SegmentedOptions { Style = style });
    }

    /// <summary>A text field at the target's height (the stock field's own 32 where <paramref name="target"/> is 0).</summary>
    public static Element TextField(Signal<string> text, string header, float width, float target)
        => TextBox.Create(text, null, new TextBox.TextBoxOptions { Header = header, Width = width, Height = target });

    /// <summary>A check box at the target's height.</summary>
    public static Element Check(string label, Signal<bool> isChecked, float target)
        => target > 0f
            ? CheckBox.Create(label, isChecked, style: CheckBox.DefaultStyle with { MinHeight = target })
            : CheckBox.Create(label, isChecked);

    /// <summary>A choice over <paramref name="selected"/>: the stock ComboBox, or at a hit target the 40-DIP menu picker (see the file header); with
    /// <paramref name="glyph"/> the picker is a bare 40-DIP icon button (a head with no room for the selection's label).</summary>
    public static Element Picker(IReadOnlyList<string> items, Signal<int> selected, float width, float target, Action<int>? onChange = null, string? glyph = null)
        => target > 0f
            ? Embed.Comp(new PickerProps(items, selected, onChange, target, glyph), static () => new TouchPicker())
            : ComboBox.Create(items, selected, width: width, onChange: onChange);

    internal sealed record PickerProps(IReadOnlyList<string> Items, Signal<int> Selected, Action<int>? OnChange, float Target, string? Glyph);

    /// <summary>The button that shows the selection and opens the choices as an anchored menu.</summary>
    sealed class TouchPicker : Component
    {
        IOverlayService? _overlay;
        OverlayHandle? _menu;
        NodeHandle _anchor = NodeHandle.Null;
        PickerProps? _props;

        public override Element Render()
        {
            var p = _props = UseProps<PickerProps>();
            _overlay = UseContext(Overlay.Service);
            int selected = p.Selected.Value;
            string label = (uint)selected < (uint)p.Items.Count ? p.Items[selected] : "";
            BoxEl button = p.Glyph is { } glyph
                ? FluentGpu.Controls.IconButton.Create(glyph, Open, FluentGpu.Controls.IconButton.DefaultStyle with { Size = p.Target })
                : FluentGpu.Controls.Button.Create(label, Open, ButtonAppearance.Standard, ControlSize.Large, glyph: Icons.ChevronDown);
            return button with { OnRealized = h => _anchor = h };
        }

        void Open()
        {
            if (_overlay is not { } overlay || _props is not { } p) return;
            if (_menu is { IsOpen: true } open) { open.Close(); return; }
            var items = new List<MenuFlyoutItem>(p.Items.Count);
            for (int i = 0; i < p.Items.Count; i++)
            {
                int index = i;
                items.Add(MenuFlyoutItem.RadioItem(p.Items[i], index == p.Selected.Peek(), () =>
                {
                    if (p.Selected.Peek() == index) return;
                    p.Selected.Value = index;
                    p.OnChange?.Invoke(index);
                }));
            }
            var handle = overlay.Open(() => _anchor, () => MenuFlyout.Create(items, () => _menu?.Close()),
                FlyoutPlacement.BottomEdgeAlignedLeft,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
            _menu = handle;
            handle.ClosedAction = () => { if (ReferenceEquals(_menu, handle)) _menu = null; };
        }
    }
}
