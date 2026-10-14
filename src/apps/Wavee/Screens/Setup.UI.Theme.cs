// ── Screens/Setup.UI.Theme.cs ──────────────────────────────────────────────────────────────────────────────────────────
// the wizard's THEME page host (themes plan 8.2, T1b; the picture is the `Setup.Theme` view, T11c)
//
// Role: CORE host (the view is `Themes\Classic\Structure\Views\Screens\ClassicSetup.Pages.cs`)
// Spec: technical-plan §8.2 (`Setup.Theme`), board C-Setup
//
// The picker cards and the active theme's options, on the plate: the wizard's second page (M7a: the sidebar layout became Classic's first option, so the Layout page is gone). The page keeps the
// Layout page's slot and frame (it was `WizardPage.Layout`, now `WizardPage.Theme`: the hero, the step count and the Back arrow are unchanged).
//
// A theme pick here is a theme switch under the wizard plate: the plate is core state (`WizardModel`), the picker is the structure's `Settings.ThemePicker` view, the options are the same
// rows Settings shows (the `Settings.ThemeOptions` view over `SettingsThemeOptions`), so a theme gets ONE description of its options for both surfaces.

using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee;

public static partial class Setup
{
    /// <summary>The Theme page's props: one stable object for the plate (and for a test that mounts the page alone).</summary>
    public static SetupThemeProps ThemeProps { get; } = new()
    {
        // The structure's Settings blocks (T11b) — the same picker and options Settings shows, read inside the template's render so a pick shows at once.
        ThemePicker = static () => Slots.View(ViewKeys.SettingsThemePicker, SettingsThemePickerProps.Instance),
        ThemeOptions = static () => SettingsThemeOptions.HasWizardOptions()
            ? Slots.View(ViewKeys.SettingsThemeOptions, SettingsThemeOptionsProps.WizardPlate) : null,
    };
}
