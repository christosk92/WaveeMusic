// ── Themes/WinUi/Structure/Views/Screens/WinUiSettings.Touch.cs — the Settings page's controls at the tier's hit target (themes plan 3.2, TC8) ──────
//
// The Compact rule is a floor, not a size: every hit target is at least 40 x 40 (the brief). The page asks for a control through the helpers below and gets the
// stock control at the size the tier's record (`WinUiSettingsLayout.Target`: 40 at Compact, 0 = the control's own size elsewhere) asks for: a Large button, a link
// with a 40 floor, a switch in a 48 x 40 cell, a segmented selector and a slider at 40, the stock ComboBox at its Large (40-DIP) field filling the row with its
// touch-padded rows (C-SettingsSections-Compact), and a NumberBox whose field is the target tall. Nothing here decides anything; it draws what the record asks for.
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

namespace Wavee.Themes.WinUi;

internal static partial class WinUiSettings
{
    /// <summary>A stateful card of the page: it subscribes to the tier facts (so a crossing re-renders it) and refreshes <c>s_layout</c> before it builds a control.</summary>
    abstract class SettingsComponent : CommonTierComponent
    {
        TierFacts _tier;

        /// <summary>The tier facts this render was built from (set by <see cref="UseLayout"/>).</summary>
        protected TierFacts Tier => _tier;

        /// <summary>Call first in <c>Render</c>: the page's record for the tier on screen now.</summary>
        protected WinUiSettingsLayout UseLayout() => s_layout = WinUiSettingsTiers.Of(_tier = ReadTier());
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

    // ── the combo and the number box ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A choice over <paramref name="selectedIndex"/>: the stock ComboBox, at a hit target its Large (40-DIP) field filling the stacked row with the touch
    /// rows (the field's width is the row's: a column that grows in the card's content row). The arguments are the ComboBox's, so a call site changes by its name only.
    /// ONE tree at every tier: the combo's size, width and touch rows are live props of the mounted combo and its column is always there (it hugs the
    /// field where the tier asks for no target), so a crossing re-sizes the SAME combo (an open list, its focus) instead of remounting it.</summary>
    static Element Combo(IReadOnlyList<string> items, Signal<int> selectedIndex, float width = 220f, bool isEnabled = true, Action<int>? onChange = null,
                         IReadOnlyList<string>? itemDescriptions = null, IReadOnlyList<bool>? itemEnabled = null)
    {
        bool touch = s_layout.Touch;
        return new BoxEl
        {
            Direction = 1, MinWidth = 0f, Grow = touch ? 1f : 0f, AlignSelf = touch ? FlexAlign.Stretch : FlexAlign.Auto,
            Children = [ComboBox.Create(items, selectedIndex, width: touch ? float.NaN : width, isEnabled: isEnabled, onChange: onChange, touchInputMode: touch,
                itemDescriptions: itemDescriptions, itemEnabled: itemEnabled, size: touch ? ControlSize.Large : ControlSize.Medium)],
        };
    }

    /// <summary>A NumberBox whose field is the tier's hit target tall (the engine's own 32 elsewhere). ONE key at every tier: the style is a live prop of
    /// the mounted box, so a crossing re-sizes the SAME field (its text, caret and focus survive).</summary>
    static Element NumberField(Signal<double> value, Action<double> onChange, NumberBox.NumberBoxOptions options, string key)
        => NumberBox.Create(value, onChange, s_layout.Touch ? options with { Style = NumberBox.DefaultStyle with { FieldHeight = s_layout.Target } } : options with { Style = null })
            with { Key = key };
}
