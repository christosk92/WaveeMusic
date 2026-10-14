// ── Screens/Setup.UI.Theme.cs ──────────────────────────────────────────────────────────────────────────────────────────
// the wizard's THEME page (themes plan 8.2, T1b): the picker cards and the active theme's options, on the plate. It is built
// here and mounted by nothing yet: today's Layout step stays the wizard's second page until M7a moves the sidebar layout into
// Classic's options (then the page switch is one line in `PageSlot`, and `Layout` is deleted). The page is the Layout page's
// twin on purpose: the same frame (`WizardPage.Layout`, so the hero, the step count and the Back arrow are unchanged), the same
// body vocabulary, so replacing one with the other moves nothing on the plate.
//
// A theme pick here is a theme switch under the wizard plate: the plate is core state (`WizardModel`), the picker is
// the structure's `Settings.ThemePicker` view, the options are the same rows Settings shows (the structure's `Settings.ThemeOptions` view over
// `SettingsThemeOptions`), so a theme gets ONE description of its options for both surfaces.
//
// Role: UI
// Spec: technical-plan §8.2 (`Setup.Theme`), board C-Setup

using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using Wavee.Theming.Contracts;

namespace Wavee;

public static partial class Setup
{
    /// <summary>The Theme page as a mountable element (the wizard's page slot, or a test). Not part of the wizard's page table
    /// yet.</summary>
    public static Element ThemeStep() => Embed.Comp(static () => new ThemePage()) with { Key = "setup:page:theme" };

    /// <summary>Pick a theme and set its options. Subscribes to the option cells and the sidebar through
    /// <c>SettingsThemeOptions.HasWizardOptions</c>, so a pick (or a change from the palette) shows at once.</summary>
    sealed class ThemePage : Component
    {
        public override Element Render()
        {
            var kids = new List<Element>(3)
            {
                SetupText.Body(Loc.Get(Strings.Setup.Theme.Body)),
                FluentGpu.Theming.Slots.View(ViewKeys.SettingsThemePicker, SettingsThemePickerProps.Instance),
            };
            if (SettingsThemeOptions.HasWizardOptions()) kids.Add(FluentGpu.Theming.Slots.View(ViewKeys.SettingsThemeOptions, SettingsThemeOptionsProps.WizardPlate));
            return WizardFrame(WizardPage.Layout, Loc.Get(Strings.Setup.Theme.Title), SetupText.Stack(kids.ToArray()));
        }
    }
}
