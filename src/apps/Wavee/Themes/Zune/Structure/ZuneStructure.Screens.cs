// ── Themes/Zune/Structure/ZuneStructure.Screens.cs — the Screens family's registrar (themes plan 6.3, P5 Z8b) ──────────────────────────────────
//
// Z8b: the screens. Settings (the page, the theme picker, the active theme's options), the setup wizard and its sign-in and runtime surfaces, the What's-new page with its
// viewer and its after-update plate, and the bodies of the report and crash dialogs. Zune's registrations win over Classic's stand-in key by key. The Common blocks (logs,
// diagnostics, history) are registered by `ZuneStructure.RegisterOwn` and are not touched here.
//
// Keys: Pages.Settings, Settings.ThemePicker, Settings.ThemeOptions, Setup.Wizard / Terms / SignIn / Theme / LocalPlayback / SignInDoor / RuntimeCard, Pages.WhatsNew,
// ReleaseNotes.Viewer, ReleaseNotes.AfterUpdatePlate, Dialog.Feedback, Dialog.Crash.

using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Zune;

public sealed partial class ZuneStructure
{
    static partial void RegisterScreens(ViewRegistryBuilder views)
    {
        ZuneSettings.Register(views);
        ZuneSetup.Register(views);
        ZuneReleaseNotes.Register(views);
        views.Add(ViewKeys.DialogFeedback, ZuneFeedback.Template, "zune.dialog.feedback");
        views.Add(ViewKeys.DialogCrash, ZuneCrash.Template, "zune.dialog.crash");
    }
}
