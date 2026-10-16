// ── Themes/Classic/Structure/Views/Screens/ClassicSettings.Touch.cs — the Settings page's controls at the tier's hit target (themes plan 3.2, TC8) ──────
//
// The Compact rule is a floor, not a size: every hit target is at least 40 x 40 (the brief). The page asks for a control through the helpers below and gets the
// stock control at the size the tier's record (`ClassicSettingsLayout.Target`: 40 at Compact, 0 = the control's own size elsewhere) asks for: a Large button, a link
// with a 40 floor, a switch in a 48 x 40 cell, a segmented selector and a slider at 40. The one stock control that cannot be lifted is the ComboBox (its field is a
// 32-DIP constant of the engine), so Compact draws a picker over the same selection: a full-width 40-DIP field that opens the choices as an anchored menu, as TC10's
// Common blocks do (`Themes\Common\CommonTouch.cs`; this one also honours a combo's disabled items). A NumberBox's field is a 32-DIP constant too and has no seam to
// lift it: it stays (an engine gap, listed in the lane report). Nothing here decides anything; it draws what the record asks for.
//
// The record is read from `s_layout`: the page template sets it from the tier facts before it builds the first tab, and `SettingsComponent` sets it again before a
// component builds its control, from the facts it subscribed to, so a control is never built from a stale tier.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Themes.Common;

namespace Wavee.Themes.Classic;

internal static partial class ClassicSettings
{
    /// <summary>A stateful card of the page: it subscribes to the tier facts (so a crossing re-renders it) and refreshes <c>s_layout</c> before it builds a control.</summary>
    abstract class SettingsComponent : CommonTierComponent
    {
        TierFacts _tier;

        /// <summary>The tier facts this render was built from (set by <see cref="UseLayout"/>).</summary>
        protected TierFacts Tier => _tier;

        /// <summary>Call first in <c>Render</c>: the page's record for the tier on screen now.</summary>
        protected ClassicSettingsLayout UseLayout() => s_layout = ClassicSettingsTiers.Of(_tier = ReadTier());
    }

    /// <summary>The marker key of a toggle's cell: a toggle keeps the label | state | switch row where the tier stacks every other control under its label.</summary>
    const string ToggleKey = "settings:toggle";

    static bool IsToggle(Element? control) => control is BoxEl { Key: ToggleKey };

    // ── toggles ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The compact switch (36 tall) of a row, in a 48 x 40 cell where the tier asks for a hit target.</summary>
    static Element Toggle(Signal<bool> isOn, Action<bool>? onChange = null, bool isEnabled = true)
    {
        var layout = s_layout;
        var style = ToggleSwitch.DefaultStyle with { MinWidth = layout.ToggleMinWidth, MinHeight = layout.ToggleMinHeight };
        return new BoxEl
        {
            Key = ToggleKey, Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Children = [ToggleSwitch.Create(isOn, onChange, isEnabled: isEnabled, style: style)],
        };
    }

    // ── buttons and links ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A standard button; a Large (40) one where the tier asks for a hit target.</summary>
    static BoxEl StdButton(string label, Action onClick, bool isEnabled = true) => Btn(label, onClick, ButtonAppearance.Standard, isEnabled);

    /// <summary>An accent button.</summary>
    static BoxEl AccentButton(string label, Action onClick, bool isEnabled = true) => Btn(label, onClick, ButtonAppearance.Accent, isEnabled);

    /// <summary>A subtle button.</summary>
    static BoxEl SubtleButton(string label, Action onClick, bool isEnabled = true) => Btn(label, onClick, ButtonAppearance.Subtle, isEnabled);

    static BoxEl Btn(string label, Action onClick, ButtonAppearance appearance, bool isEnabled)
        => s_layout.Touch
            ? Button.Create(label, onClick, appearance, ControlSize.Large, isEnabled: isEnabled)
            : Button.Create(label, onClick, appearance, isEnabled: isEnabled);

    /// <summary>A link no smaller than the tier's hit target (40 at Compact).</summary>
    static BoxEl LinkButton(string text, Action onClick, bool isEnabled = true)
        => Lifted(HyperlinkButton.Create(text, onClick, isEnabled: isEnabled));

    /// <summary>A link that opens <paramref name="navigateUri"/>, no smaller than the tier's hit target.</summary>
    static BoxEl LinkButton(string text, string navigateUri) => Lifted(HyperlinkButton.Create(text, navigateUri));

    /// <summary>A control root lifted to the tier's hit target (a link, a clickable row's cell); the look is untouched.</summary>
    static BoxEl Lifted(BoxEl box)
        => s_layout.Touch ? box with { MinHeight = s_layout.Target, MinWidth = s_layout.Target } : box;

    // ── selectors, sliders, radios ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A segmented selector (the board's pill row); its items are the tier's hit target tall where it asks for one.</summary>
    static Element Selector(IReadOnlyList<string> items, Signal<int> selected, Action<int>? onChange = null)
        => s_layout.Touch
            ? SelectorBar.Create(items, selected, onChange, style: new SelectorBarStyle { ItemHeight = s_layout.Target })
            : SelectorBar.Create(items, selected, onChange);

    /// <summary>A slider whose cross size is the tier's hit target (the engine's own 32 elsewhere).</summary>
    static Element SliderControl(FloatSignal? value, Action<float>? onChange, Slider.SliderOptions? options, float length = 200f, bool isEnabled = true)
        => s_layout.Touch
            ? Slider.Create(value, onChange, options, length, thickness: s_layout.Target, isEnabled: isEnabled)
            : Slider.Create(value, onChange, options, length, isEnabled: isEnabled);

    /// <summary>A radio item at the tier's hit target.</summary>
    static BoxEl Radio(string label, bool isSelected, Action? onChange, bool isEnabled = true)
        => s_layout.Touch
            ? RadioButton.Create(label, isSelected, onChange, RadioButton.DefaultStyle with { MinHeight = s_layout.Target }, isEnabled)
            : RadioButton.Create(label, isSelected, onChange, isEnabled: isEnabled);

    // ── the combo ───────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A choice over <paramref name="selectedIndex"/>: the stock ComboBox, or at a hit target the full-width 40-DIP menu picker (see the file header).
    /// The arguments are the ComboBox's, so a call site changes by its name only.</summary>
    static Element Combo(IReadOnlyList<string> items, Signal<int> selectedIndex, float width = 220f, bool isEnabled = true, Action<int>? onChange = null,
                         IReadOnlyList<string>? itemDescriptions = null, IReadOnlyList<bool>? itemEnabled = null)
        => s_layout.Touch
            ? new BoxEl
            {
                Direction = 1, Grow = 1f, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
                Children = [Embed.Comp(new TouchComboProps(items, selectedIndex, onChange, itemEnabled, isEnabled, s_layout.Target), static () => new TouchCombo())],
            }
            : ComboBox.Create(items, selectedIndex, width: width, isEnabled: isEnabled, onChange: onChange, itemDescriptions: itemDescriptions, itemEnabled: itemEnabled);

    internal sealed record TouchComboProps(IReadOnlyList<string> Items, Signal<int> Selected, Action<int>? OnChange, IReadOnlyList<bool>? ItemEnabled, bool IsEnabled, float Target);

    /// <summary>The field that shows the selection and opens the choices as an anchored menu. The field's width is the row's: it fills the stacked content.</summary>
    sealed class TouchCombo : Component
    {
        IOverlayService? _overlay;
        OverlayHandle? _menu;
        NodeHandle _anchor = NodeHandle.Null;
        TouchComboProps? _props;

        public override Element Render()
        {
            var p = _props = UseProps<TouchComboProps>();
            _overlay = UseContext(Overlay.Service);
            int selected = p.Selected.Value;
            string label = (uint)selected < (uint)p.Items.Count ? p.Items[selected] : "";
            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = ClassicSettingsRules.TouchComboGap, AlignSelf = FlexAlign.Stretch, Grow = 1f, MinWidth = 0f,
                Height = p.Target, MinHeight = p.Target, Padding = new Edges4(Spacing.M, 0f, Spacing.M, 0f),
                Corners = Radii.ControlAll, Role = AutomationRole.ComboBox, Focusable = true, OnClick = Open,
                OnRealized = h => _anchor = h,
                Children =
                [
                    new TextEl(label) { Grow = 1f, MinWidth = 0f, Size = ClassicSettingsRules.TouchComboTextSize, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Color = Tok.TextPrimary },
                    Ui.Icon(Icons.ChevronDown, ClassicSettingsRules.TouchComboChevron, Tok.TextSecondary),
                ],
            }.Interactive(Interaction.Control, p.IsEnabled);
        }

        void Open()
        {
            if (_overlay is not { } overlay || _props is not { IsEnabled: true } p) return;
            if (_menu is { IsOpen: true } open) { open.Close(); return; }
            var items = new List<MenuFlyoutItem>(p.Items.Count);
            for (int i = 0; i < p.Items.Count; i++)
            {
                int index = i;
                bool enabled = p.ItemEnabled is null || (uint)index >= (uint)p.ItemEnabled.Count || p.ItemEnabled[index];
                items.Add(MenuFlyoutItem.RadioItem(p.Items[i], index == p.Selected.Peek(), () =>
                {
                    if (p.Selected.Peek() == index) return;
                    // The verb may refuse (the page's own writer): it decides, the mirror follows only what it accepted.
                    p.Selected.Value = index;
                    p.OnChange?.Invoke(index);
                }, enabled: enabled));
            }
            var handle = overlay.Open(() => _anchor, () => MenuFlyout.Create(items, () => _menu?.Close()),
                FlyoutPlacement.BottomEdgeAlignedLeft,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
            _menu = handle;
            handle.ClosedAction = () => { if (ReferenceEquals(_menu, handle)) _menu = null; };
        }
    }
}
