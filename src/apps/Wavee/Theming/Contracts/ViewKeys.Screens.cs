// ── Theming/Contracts/ViewKeys.Screens.cs — the Screens family of published view keys (themes plan 6.0, T11b) ────────────────────
//
// T11b publishes the Settings page (`Pages.Settings`) and the three Settings surfaces the setup wizard shares with it: the theme picker
// (`Settings.ThemePicker`), the active theme's options (`Settings.ThemeOptions`, the expander on the page, the plate rows in the wizard) and the
// sidebar layout cards (`Settings.SidebarLayoutCards`).
//
// What stays core: the document (`SettingsDoc`: tabs, sections, rows, their kinds, titles, values, enablement and reasons), every verb
// (`SettingsVerbs`, the writers: persistence, live apply, DSP pushes, OS registrations, file I/O off the render path), the facts the rows ask
// (`SettingsFacts`), the page epoch and the Storage census, the pivot's root-lifetime cell and the deep link, the option model
// (`ThemeOptionsSection`, `SettingsThemeOptions`: grouping, visibility, the legacy rows a structure still binds) and the page context the verbs
// read (`SettingsContext`). A theme draws what the document and the props carry.

using FluentGpu.Theming;

namespace Wavee.Theming.Contracts;

public static partial class ViewKeys
{
    /// <summary>The Settings page: the masthead, the seven-tab pivot over a keyed scroll lane, and the active tab's sections and rows drawn from
    /// `SettingsDoc`. Mounted by `Settings.Page`. Props: <see cref="SettingsPageProps"/>.</summary>
    public static readonly ViewKey<SettingsPageProps> SettingsPage = new("Pages.Settings");

    /// <summary>The theme picker: one fixed-size card per installed theme (`ThemePickerFacts`), a pick is the `themePicker` row's writer. Mounted by the
    /// Settings Appearance tab and by the setup wizard's Theme step. Props: <see cref="SettingsThemePickerProps"/>.</summary>
    public static readonly ViewKey<SettingsThemePickerProps> SettingsThemePicker = new("Settings.ThemePicker");

    /// <summary>The active theme's options (`SettingsThemeOptions.Current`): the "‹Theme› options" expander on the Settings page, the plate rows
    /// under their group labels in the wizard. Props: <see cref="SettingsThemeOptionsProps"/>.</summary>
    public static readonly ViewKey<SettingsThemeOptionsProps> SettingsThemeOptions = new("Settings.ThemeOptions");

    /// <summary>The sidebar layout cards (Classic · Library), applied at once; the setup wizard's Layout step mounts them until the sidebar layout is
    /// a generated Cards option (M7a). Props: <see cref="SettingsSidebarLayoutProps"/>.</summary>
    public static readonly ViewKey<SettingsSidebarLayoutProps> SettingsSidebarLayoutCards = new("Settings.SidebarLayoutCards");
}
