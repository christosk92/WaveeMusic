// ── Screens/SettingsDoc.Rows.Areas.cs — the four tabs the Zune boards add: Account · Devices · Keyboard · Feedback ─────────
//
// Role: CORE. The boards list twelve sections; four had no home in the document. Each is built from what the app ALREADY has, never from a setting without a feature behind it:
//   · Account  the signed-in account's name and plan (read from the user row), the profile, Spotify's own account page, and the profile menu's "Log out" (the same confirm dialog);
//   · Devices  this PC's Spotify Connect name (a fact: the name the playback host registers) and the Connect diagnostics page (moved from Privacy › Tools);
//   · Keyboard the shortcuts the shell defines, as a READ-ONLY reference (`Settings.Shortcuts`): nothing here rebinds, because nothing can be rebound;
//   · Feedback report a problem / suggest something / ask a question (the existing report dialog), what a report holds (a fact), and the diagnostics that go with a
//     report (copy diagnostics moved from About). The log viewer, the detail level and the tools stay in Privacy & diagnostics' sections, drawn under it.

using FluentGpu.Localization;
using T = Wavee.Settings.Tab;
using K = Wavee.SettingsRowKind;

namespace Wavee;

public static partial class SettingsDoc
{
    // ══ ACCOUNT ═════════════════════════════════════════════════════════════════════════════════════════════════════

    static IReadOnlyList<SettingsSection> AccountSections() =>
    [
        Sect(T.Account, "Account", Strings.Settings.Areas.Account, Strings.Settings.Account.Subtitle,
            // The name ("" until the user row carries the Identity group) and the plan (0 unknown · 1 free · 2 premium): facts, read live.
            Make(T.Account, "Account", "identity", K.Fact, Strings.Settings.Account.SignedInAs, SettingsWriter.ReadOnly,
                value: _ => Shell.AccountSummary().Name ?? "", catalog: false),
            Make(T.Account, "Account", "plan", K.Fact, Strings.Settings.Account.SignedInAs, SettingsWriter.ReadOnly,
                value: _ => (int)Shell.AccountSummary().Tier, catalog: false),
            Make(T.Account, "Account", "profile", K.Action, Strings.Person.List.Menu.Profile,
                SettingsWriter.OfAction("account.profile", SettingsVerbs.OpenProfile), captionKey: Strings.Settings.Account.ProfileSub, catalog: false),
            Make(T.Account, "Account", "manageAccount", K.Action, Strings.Settings.Account.Manage,
                SettingsWriter.OfAction("account.manage", () => SettingsVerbs.OpenUri(Settings.SpotifyAccountUrl)),
                captionKey: Strings.Settings.Account.ManageSub, catalog: false),
            Make(T.Account, "Account", "signOut", K.Action, Strings.Auth.LogOut,
                SettingsWriter.OfAction("account.signOut", SettingsVerbs.SignOut), captionKey: Strings.Settings.Account.SignOutSub, catalog: false)),
    ];

    // ══ DEVICES & CONNECT ═══════════════════════════════════════════════════════════════════════════════════════════

    static IReadOnlyList<SettingsSection> DeviceSections() =>
    [
        Sect(T.Devices, "Connect", Strings.Settings.Devices.Title, Strings.Settings.Devices.Subtitle,
            Make(T.Devices, "Connect", "thisPc", K.Fact, Strings.Settings.Devices.ThisPc, SettingsWriter.ReadOnly,
                captionKey: Strings.Settings.Devices.ThisPcSub, value: _ => Playback.DeviceName, catalog: false),
            // Moved from Privacy › Tools: the Connect diagnostics page is Connect's own door.
            Make(T.Devices, "Connect", "connectDiagnostics", K.Action, Strings.Settings.Privacy.Tools.Connect,
                SettingsWriter.OfAction("tools.connect", () => SettingsVerbs.OpenDiagnostics(Shell.RouteKind.ConnectDiagnostics)),
                captionKey: Strings.Settings.Privacy.Tools.ConnectSub, catalog: false)),
    ];

    // ══ KEYBOARD ════════════════════════════════════════════════════════════════════════════════════════════════════

    static IReadOnlyList<SettingsSection> KeyboardSections()
    {
        var rows = new List<SettingsRow>(Settings.Shortcuts.All.Length);
        foreach (var shortcut in Settings.Shortcuts.All)
        {
            var s = shortcut;
            rows.Add(Make(T.Keyboard, "Shortcuts", s.Id, K.Fact, s.TitleKey, SettingsWriter.ReadOnly,
                captionKey: s.CaptionKey, value: _ => s.Chord,
                visible: s.NeedsTabs ? TabsOffered : null, catalog: false));
        }
        return [Sect(T.Keyboard, "Shortcuts", Strings.Settings.Keyboard.Title, Strings.Settings.Keyboard.Subtitle, rows.ToArray())];
    }

    static readonly Func<ISettingsFacts, bool> TabsOffered = static _ => ShellCaps.Now.Tabs;

    // ══ FEEDBACK & HELP ═════════════════════════════════════════════════════════════════════════════════════════════

    static IReadOnlyList<SettingsSection> FeedbackSections() =>
    [
        Sect(T.Feedback, "Help", Strings.Settings.Feedback.Title, Strings.Settings.Feedback.Subtitle,
            Make(T.Feedback, "Help", "reportProblem", K.Action, Strings.Settings.Feedback.ReportProblem,
                SettingsWriter.OfAction("feedback.report", SettingsVerbs.ReportProblem), captionKey: Strings.Settings.Feedback.ReportProblemSub, catalog: false),
            Make(T.Feedback, "Help", "suggestFeature", K.Action, Strings.Settings.Feedback.Suggest,
                SettingsWriter.OfAction("feedback.suggest", () => SettingsVerbs.OpenFeedback(Feedback.ReportKind.Feature)),
                captionKey: Strings.Settings.Feedback.SuggestSub, catalog: false),
            Make(T.Feedback, "Help", "askQuestion", K.Action, Strings.Settings.Feedback.Ask,
                SettingsWriter.OfAction("feedback.ask", () => SettingsVerbs.OpenFeedback(Feedback.ReportKind.Question)),
                captionKey: Strings.Settings.Feedback.AskSub, catalog: false),
            // What the report dialog sends (a fact: the dialog shows the whole report before anything leaves this PC).
            Make(T.Feedback, "Help", "reportHolds", K.Fact, Strings.Settings.Feedback.HoldsTitle, SettingsWriter.ReadOnly, catalog: false)),

        Sect(T.Feedback, "Diagnostics", Strings.Settings.Feedback.DiagnosticsTitle, null,
            // Moved from About › Links.
            Make(T.Feedback, "Diagnostics", "copyDiagnostics", K.Action, Strings.Settings.About.CopyDiagnostics,
                SettingsWriter.OfAction("feedback.copyDiagnostics", SettingsVerbs.CopyDiagnostics), catalog: false)),
    ];
}

public static partial class SettingsVerbs
{
    /// <summary>Open the signed-in account's profile page.</summary>
    public static bool OpenProfile()
    {
        Shell.GoTo(ProfileRoute.For(User.Me));
        return false;
    }

    /// <summary>The profile menu's "Log out": its confirm dialog first (Cancel the default), then the same sign-out.</summary>
    public static bool SignOut()
    {
        Shell.OpenLogoutConfirm(SettingsContext.Overlay);
        return false;
    }
}
