// ── Themes/WinUi/Structure/WinUiStructure.Screens.cs — the Screens family's registrar (themes plan 6.2, P6 W8b) ──────────────────────────────
//
// W8b: the screens. Settings (the seven-tab page over the twelve areas, the theme picker, the active theme's six options), the setup wizard and its
// sign-in (browser plus QR) and runtime surfaces, the What's-new page with its viewer and its after-update plate, and the bodies of the report and
// crash dialogs. WinUI's registrations win over the stand-in key by key. The Common blocks (logs, diagnostics, history, module page) are registered
// by `WinUiStructure.RegisterOwn` and are not touched here.
//
// Keys: Pages.Settings, Settings.ThemePicker, Settings.ThemeOptions, Setup.Wizard / Terms / SignIn / Theme / LocalPlayback / SignInDoor / RuntimeCard,
// Pages.WhatsNew, ReleaseNotes.Viewer, ReleaseNotes.AfterUpdatePlate, Dialog.Feedback, Dialog.Crash.

using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

public sealed partial class WinUiStructure
{
    static partial void RegisterScreens(ViewRegistryBuilder views)
    {
        WinUiSettings.Register(views);
        WinUiSetup.Register(views);
        WinUiReleaseNotes.Register(views);
        views.Add(ViewKeys.DialogFeedback, WinUiFeedback.Template, "winui.dialog.feedback");
        views.Add(ViewKeys.DialogCrash, WinUiCrash.Template, "winui.dialog.crash");
    }
}
