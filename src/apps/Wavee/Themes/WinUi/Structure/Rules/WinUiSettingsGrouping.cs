// ── Themes/WinUi/Structure/Rules/WinUiSettingsGrouping.cs — WinUI's seven Settings tabs over the twelve areas (themes plan 6.2; P6 WK seam, W8b) ─────
//
// The boards (W-Settings, W-SettingsSections) draw SEVEN tabs: General · Appearance · Playback · Notifications · Storage & cache · Privacy & diagnostics · About. The
// core's page is twelve AREAS (`Settings.Area`, `Screens\Settings.Areas.cs`); the structure groups them here (owner Q3: "12 settings areas grouped into the board's 7
// tabs") and the core host resolves every deep link through the grouping:
//
//   General                 Account, Devices & connect and Keyboard, and the window rows the old General tab held (language, links, the notification-area icon,
//                           graphics), listed by the tab itself ("rows moved, not invented")
//   Appearance              Appearance (theme, its options, display, fullscreen, now playing, home) and Lyrics
//   Playback                Playback and Audio
//   Notifications           Notifications
//   Storage & cache         Storage
//   Privacy & diagnostics   Privacy and Feedback & help (the logs, the tools and the developer rows ride Feedback)
//   About                   About
//
// EVERY OLD SLUG LANDS (`WinUiScreensTests` proves each): an area's own slug lands on its area and shows on the tab that holds it (account, devices, keyboard on General;
// lyrics on Appearance; audio on Playback; feedback on Privacy & diagnostics), a tab's own slug (`general`, `storage-cache`, `privacy-diagnostics`) lands on the tab's
// first area, and every older alias lands where it always did (`logs`, `diagnostics`, `help`, `feedback-help` on Privacy & diagnostics; `downloads`, `downloads-storage`,
// `cache` on Storage & cache; `devices-connect`, `connect`, `shortcuts` on General). The old `general` alias used to open Appearance, where the window rows lived; the
// tab's own slug wins (accepted by the owner: General now holds them).
//
// Public, so `Wavee.Tests` pins it.

using System.Collections.Immutable;
using Wavee.Theming;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's grouping of the Settings areas.</summary>
public static class WinUiSettingsGrouping
{
    /// <summary>The seven tabs, in the boards' order.</summary>
    public static SettingsGrouping Seven { get; } = Build();

    private static SettingsGrouping Build()
    {
        static ImmutableArray<Settings.Area> A(params Settings.Area[] areas) => [.. areas];
        var general = new SettingsTab("general", Strings.Theme.Winui.Screens.TabGeneral, A(Settings.Area.Account, Settings.Area.Devices, Settings.Area.Keyboard))
        {
            Parts =
            [
                new(Settings.Tab.General, "Language & region"), new(Settings.Tab.General, "Links"), new(Settings.Tab.General, "Notification area"),
                new(Settings.Tab.General, "Graphics"), new(Settings.Tab.Account, "Account"), new(Settings.Tab.Devices, "Connect"), new(Settings.Tab.Keyboard, "Shortcuts"),
            ],
        };
        return new SettingsGrouping(
        [
            general,
            new SettingsTab("appearance", Strings.Theme.Winui.Screens.TabAppearance, A(Settings.Area.Appearance, Settings.Area.Lyrics)),
            new SettingsTab("playback", Strings.Theme.Winui.Screens.TabPlayback, A(Settings.Area.Playback, Settings.Area.Audio)),
            new SettingsTab("notifications", Strings.Theme.Winui.Screens.TabNotifications, A(Settings.Area.Notifications)),
            new SettingsTab("storage-cache", Strings.Theme.Winui.Screens.TabStorage, A(Settings.Area.Storage)),
            new SettingsTab("privacy-diagnostics", Strings.Theme.Winui.Screens.TabPrivacy, A(Settings.Area.Privacy, Settings.Area.Feedback)),
            new SettingsTab("about", Strings.Theme.Winui.Screens.TabAbout, A(Settings.Area.About)),
        ]);
    }
}
