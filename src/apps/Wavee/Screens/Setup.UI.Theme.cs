// ── Screens/Setup.UI.Theme.cs ──────────────────────────────────────────────────────────────────────────────────────────
// the wizard's THEME page host (themes plan 8.2, T1b; the picture is the `Setup.Theme` view, T11c)
//
// Role: CORE host (the view is `Themes\Classic\Structure\Views\Screens\ClassicSetup.Pages.cs`)
// Spec: technical-plan §8.2 (`Setup.Theme`), board C-Setup
//
// The picker cards and the active theme's options, on the plate. It is mounted by nothing yet: today's Layout step stays the wizard's second page until M7a moves the sidebar layout into
// Classic's options (then the page switch is one line in the plate's page table, and the Layout page is deleted). The page is the Layout page's twin on purpose: the same frame
// (`WizardPage.Layout`, so the hero, the step count and the Back arrow are unchanged), the same body vocabulary, so replacing one with the other moves nothing on the plate.
//
// A theme pick here is a theme switch under the wizard plate: the plate is core state (`WizardModel`), the picker is `Settings.ThemePickerView`, the options are the same rows Settings
// shows (`Settings.WizardThemeOptions`), so a theme gets ONE description of its options for both surfaces.

using FluentGpu.Dsl;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee;

public static partial class Setup
{
    static readonly SetupThemeProps s_themeProps = new() { ThemePicker = Settings.ThemePickerView, ThemeOptions = Settings.WizardThemeOptions };

    /// <summary>The Theme page as a mountable element (the wizard's page slot, or a test). Not part of the wizard's page table
    /// yet.</summary>
    public static Element ThemeStep() => Slots.View(ViewKeys.SetupTheme, s_themeProps) with { Key = "setup:page:theme" };
}
