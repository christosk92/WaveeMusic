// ── Themes/Classic/Structure/Views/Player/ClassicDevicePicker.cs — Classic's `Player.DevicePicker` (themes plan 6.1, T6b) ───────
//
// From `Shell/Shell.PlayerBar.UI.cs` (`BarDevicePickerMenu` and `DevicePickerMenuItems`): the picker's body. The bar's
// `ToggleDevicePicker` owns the overlay (placement, light dismiss, the picker-opened side effect); this is what goes inside it.
//
// It re-renders WHILE OPEN — a transfer landing swaps the checks in place (parity 79) — and re-keys its menu presenter on every
// change, because the presenter freezes its rows at mount. G-210: a dozen Connect devices must scroll well before the window
// edge, not just the engine's own fixed 468-DIP cap (a short or undocked window is smaller than that), so the scroller is capped
// to the window (`props.MaxHeight`). This computer's rows are plain commands, not radios: choosing one persists the output for the
// next session and pulls playback here, but the engine does not yet re-route a live session to it, so no row claims to be "the
// endpoint in use". A Connect device is a radio, checked when it is the active one. The states (this computer alone, a remote
// device, the empty roster's two disabled rows, the player's quality echo) are the core's entries.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's device picker menu.</summary>
internal static class ClassicDevicePicker
{
    /// <summary>The registered template of <see cref="ViewKeys.DevicePicker"/>.</summary>
    public static Element Template(DevicePickerProps props, in ViewContext ctx)
    {
        var view = props.Read();
        float windowH = ctx.Hooks.UseContextSignal(Viewport.Size).Value.Height;

        var entries = view.Entries;
        var items = new MenuFlyoutItem[entries.Count];
        for (int i = 0; i < items.Length; i++) items[i] = ItemOf(entries[i]);

        float maxH = props.MaxHeight(windowH);
        var parts = new TemplateParts();
        parts.Set<ScrollEl>(MenuFlyout.PartScrollViewer, s => s with { MaxHeight = maxH });

        int version = HashCode.Combine(view.Stamp, maxH);
        return new BoxEl
        {
            Direction = 1,
            Children = [MenuFlyout.Create(items, props.Close, parts: parts) with { Key = "devices:" + version }],
        };
    }

    /// <summary>One entry as a menu row.</summary>
    internal static MenuFlyoutItem ItemOf(in DevicePickerEntry e) => e.Kind switch
    {
        DevicePickerEntryKind.Separator => MenuFlyoutItem.Separator,
        // A disabled command row stands in as the section header (MenuFlyout has no header kind; neither does WinUI).
        // The quality echo rides the same disabled-row shape: it is informational, never a radio.
        DevicePickerEntryKind.Info => new MenuFlyoutItem(e.Label, default, false),
        DevicePickerEntryKind.Command => new MenuFlyoutItem(e.Label, e.Glyph, e.Enabled, e.Invoke)
            with { AcceleratorText = e.Accelerator },
        DevicePickerEntryKind.Radio => MenuFlyoutItem.RadioItem(e.Label, e.IsChecked, e.Invoke, e.Glyph),
        _ => new MenuFlyoutItem(e.Label, default, false),
    };
}
