// ── Screens/SettingsSpec.Account.cs — Account & privacy: your Spotify account and what leaves this PC (the dials and their verbs) ──
//
// Role: CORE (settings rework phase 1). Account (the signed-in name and plan, the profile, Spotify's own account page, "Log out" with the
// profile menu's confirm) · Privacy (what leaves this PC, the crash-report mode with its memory-snapshot switch, the
// crash-service card, the policy link) · Crash reports (the saved reports and their verbs).
//
// The decisions the privacy dials make are `PrivacyRules` (`Settings.Privacy.cs`, engine-free). What a dial DOES — a store write, a
// crash-service call, a Notepad launch, a clipboard copy of a scrubbed report — is a `SettingsVerbs` member here, with the file I/O and
// the network off the UI thread and the answer posted back through `SettingsContext.Post`.

using FluentGpu.Controls;
using FluentGpu.Localization;
using PvFacts = Wavee.Strings.Settings.Privacy.Facts;
using PvReports = Wavee.Strings.Settings.Privacy.Reports;
using K = Wavee.SettingsRowKind;

namespace Wavee;

public static partial class SettingsSpec
{
    static SettingsCategory AccountCategory() => new("account", Strings.Settings.Categories.Account.Title,
        Strings.Settings.Categories.Account.Strip, Strings.Settings.Categories.Account.Subtitle, "Contact",
    [
        Group("account", Strings.Settings.Groups.Account.SignedIn, Strings.Settings.Account.Subtitle,
            // The name ("" until the user row carries the Identity group) and the plan (0 unknown · 1 free · 2 premium): facts, read live.
            Make("account.identity", K.Fact, Strings.Settings.Account.SignedInAs, SettingsWriter.ReadOnly, legacy: ["identity"],
                value: _ => Shell.AccountSummary().Name ?? ""),
            Make("account.plan", K.Fact, Strings.Settings.Account.SignedInAs, SettingsWriter.ReadOnly, legacy: ["plan"],
                value: _ => (int)Shell.AccountSummary().Tier),
            Make("account.profile", K.Action, Strings.Person.List.Menu.Profile,
                SettingsWriter.OfAction("account.profile", SettingsVerbs.OpenProfile), legacy: ["profile"], captionKey: Strings.Settings.Account.ProfileSub),
            Make("account.manage", K.Action, Strings.Settings.Account.Manage,
                SettingsWriter.OfAction("account.manage", () => SettingsVerbs.OpenUri(Settings.SpotifyAccountUrl)), legacy: ["manageAccount"],
                captionKey: Strings.Settings.Account.ManageSub),
            Make("account.signOut", K.Action, Strings.Auth.LogOut,
                SettingsWriter.OfAction("account.signOut", SettingsVerbs.SignOut), legacy: ["signOut"], captionKey: Strings.Settings.Account.SignOutSub)),

        Group("privacy", Strings.Settings.Groups.Account.Privacy, PvFacts.Subtitle,
            // The collapsed answer is "No telemetry"; inside, the five places anything leaves this PC and why: facts, no control.
            Make("account.privacy.whatLeaves", K.Fact, PvFacts.WhatLeaves, SettingsWriter.ReadOnly, legacy: ["whatLeaves"], glyph: "Globe",
                captionKey: PvFacts.WhatLeavesSub),
            Make("account.privacy.whatLeavesAi", K.Fact, PvFacts.AiModels, SettingsWriter.ReadOnly, legacy: ["whatLeavesAi"],
                parent: "account.privacy.whatLeaves", glyph: "RefineSparkle", captionKey: PvFacts.AiModelsSub),
            // Off / Ask each time / Automatic as three left-aligned radio items. Per-item enablement and captions are
            // `CrashModeEnablement` / `CrashModeCaptionKey`: Automatic alone is disabled (no service in this build), with its reason.
            Make("account.privacy.crashReports", K.Segmented, PvFacts.CrashReports,
                SettingsWriter.OfChoice("crash.mode", SettingsVerbs.SetCrashMode), legacy: ["crashReports"], glyph: "StatusWarning",
                captionKey: PvFacts.CrashReportsSub, value: IntOf(Platform.Keys.CrashReporting), contexts: [SettingsSurfaces.CrashPrompt]),
            // The memory-snapshot switch is live only while reporting is on (greyed, no reason of its own).
            Make("account.privacy.crashDump", K.Toggle, PvFacts.IncludeDump,
                SettingsWriter.OfToggle("crash.includeDump", WriteBool(Platform.Keys.CrashIncludeDump)), legacy: ["crashDump"],
                parent: "account.privacy.crashReports", glyph: "Camera",
                captionKey: PvFacts.IncludeDumpSub, value: BoolOf(Platform.Keys.CrashIncludeDump),
                enabled: f => f.CrashReportingOn ? SettingsEnablement.On : SettingsEnablement.Off(null), silent: true),
            Make("account.privacy.crashContents", K.Fact, PvFacts.Contents, SettingsWriter.ReadOnly, legacy: ["crashContents"],
                parent: "account.privacy.crashReports", glyph: "Info", captionKey: PvFacts.ContentsSub),
            // "Your data on the crash service": the confirmed right-to-erasure (§J), off the UI thread.
            Make("account.privacy.crashService", K.Action, PvFacts.DeleteMyData,
                SettingsWriter.OfAction("crash.deleteMyData", SettingsVerbs.DeleteCrashServiceData), legacy: ["crashService"], glyph: "Delete",
                enabled: f => f.CrashServiceConfigured ? SettingsEnablement.On : SettingsEnablement.Off(null), silent: true,
                confirm: new SettingsConfirm(PvFacts.DeleteMyDataConfirmTitle, PvFacts.DeleteMyDataConfirmBody, PvFacts.DeleteMyDataConfirmButton)),
            Make("account.privacy.crashServiceCopy", K.Action, PvFacts.CopyId,
                SettingsWriter.OfAction("crash.copyInstallId", SettingsVerbs.CopyCrashInstallId), legacy: ["crashServiceCopy"],
                parent: "account.privacy.crashService"),
            Make("account.privacy.policy", K.Action, PvFacts.Policy,
                SettingsWriter.OfAction("privacy.openPolicy", SettingsVerbs.OpenPrivacyPolicy), legacy: ["privacyPolicy"], glyph: "OpenInNewWindow",
                captionKey: PvFacts.PolicySub)),

        Group("crashReports", Strings.Settings.Groups.Account.CrashReports, PvReports.Subtitle,
            // ONE expander over the saved bundles: a row per report with a "…" menu (View · Copy · Send… · Delete) — the per-report verbs
            // are `SettingsVerbs.ViewCrashReport` / `CopyCrashReport` (and the crash module's send prompt).
            Make("account.reports.saved", K.Composite, PvReports.Saved,
                SettingsWriter.Owned("SettingsVerbs.ViewCrashReport/CopyCrashReport"), legacy: ["savedReports"], glyph: "Folder"),
            Make("account.reports.sendNow", K.Action, PvReports.SendNow,
                SettingsWriter.OfAction("reports.sendQueued", SettingsVerbs.SendQueuedReports), legacy: ["reportsSendNow"], parent: "account.reports.saved",
                enabled: f => f.CrashServiceConfigured ? SettingsEnablement.On : SettingsEnablement.Off(null), silent: true),
            Make("account.reports.discard", K.Action, PvReports.Discard,
                SettingsWriter.OfAction("reports.discardQueue", SettingsVerbs.DiscardQueuedReports), legacy: ["reportsDiscard"], parent: "account.reports.saved"),
            Make("account.reports.openFolder", K.Action, PvReports.OpenFolder,
                SettingsWriter.OfAction("reports.openFolder", SettingsVerbs.OpenReportsFolder), legacy: ["reportsOpenFolder"], parent: "account.reports.saved"),
            Make("account.reports.deleteAll", K.Action, PvReports.DeleteAll,
                SettingsWriter.OfAction("reports.deleteAll", SettingsVerbs.DeleteAllReports), legacy: ["reportsDeleteAll"], parent: "account.reports.saved",
                confirm: new SettingsConfirm(PvReports.DeleteAllConfirmTitle, PvReports.DeleteAllConfirmBody, PvReports.DeleteAll))),
    ]);

    /// <summary>Crash-report mode <paramref name="mode"/> (0 Off · 1 Ask each time · 2 Automatic): Automatic needs a service to send to,
    /// so without one its radio is disabled and its caption says why.</summary>
    public static SettingsEnablement CrashModeEnablement(int mode, ISettingsFacts facts)
        => mode == (int)Crash.Reporting.Auto && !facts.CrashServiceConfigured
            ? SettingsEnablement.Off(PvFacts.ModeAutoNeedsService)
            : SettingsEnablement.On;

    /// <summary>The caption under crash-report mode <paramref name="mode"/> (a loc key).</summary>
    public static string CrashModeCaptionKey(int mode, ISettingsFacts facts) => mode switch
    {
        (int)Crash.Reporting.Off => PvFacts.ModeOffSub,
        (int)Crash.Reporting.Ask => PvFacts.ModeAskSub,
        _ => facts.CrashServiceConfigured ? PvFacts.ModeAutoSub : PvFacts.ModeAutoNeedsService,
    };
}

public static partial class SettingsVerbs
{
    // ── Account ──────────────────────────────────────────────────────────────────────────────────────────────────────

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

    // ── Privacy ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Off · Ask each time · Automatic. Compared against the STORED mode (not the effective one), so choosing Ask over a
    /// stored-but-unusable Automatic really writes Ask.</summary>
    public static bool SetCrashMode(int mode)
    {
        if (mode is < 0 or > 2) return false;
        if (Platform.Settings.Get(Platform.Keys.CrashReporting) == mode) return false;
        Platform.Settings.Set(Platform.Keys.CrashReporting, mode);
        return true;
    }

    /// <summary>"Delete my data" (confirmed first): the erasure runs off the UI thread; the answer is a toast and an epoch bump so the card shows the rotated install id.</summary>
    public static bool DeleteCrashServiceData()
    {
        var post = SettingsContext.Post;
        _ = Task.Run(async () =>
        {
            var result = await Crash.Uploader.DeleteRemote(CancellationToken.None).ConfigureAwait(false);
            post(() =>
            {
                if (result.Ok) Notify.Say(PvFacts.DeleteMyDataDone(result.Deleted.ToString(System.Globalization.CultureInfo.InvariantCulture)), InfoBarSeverity.Success);
                else Notify.Say(Loc.Get(PvFacts.DeleteMyDataFailed), InfoBarSeverity.Error);
                SettingsFacts.CrashDataEpoch.Value = SettingsFacts.CrashDataEpoch.Peek() + 1;
            });
        });
        return false;
    }

    public static bool CopyCrashInstallId()
    {
        string fullId = Crash.InstallId.Peek(Platform.Settings);   // boot ensures it; a render never writes the store
        if (fullId.Length > 0) SettingsContext.Hooks?.Clipboard?.SetText(fullId);
        return false;
    }

    public static bool OpenPrivacyPolicy()
    {
        OpenUri(Crash.PrivacyUrl);
        return false;
    }

    /// <summary>Open a URI through the host (ms-settings:, https:), fail-soft.</summary>
    public static bool OpenUri(string uri)
    {
        try { (SettingsContext.Hooks?.OpenUri ?? FluentGpu.Hooks.InputHooks.Current.Default.OpenUri)?.Invoke(uri); }
        catch (Exception ex) { Log.Warn("settings", "could not open " + uri, ex); }
        return false;
    }

    // ── Crash reports ────────────────────────────────────────────────────────────────────────────────────────────────

    public static bool SendQueuedReports()
    {
        Crash.Uploader.Drain();
        return false;
    }

    public static bool DiscardQueuedReports()
    {
        Crash.Uploader.DiscardQueue();
        return false;
    }

    public static bool OpenReportsFolder()
    {
        Diagnostics.OpenFolder(Crash.Files.Root(Crash.Host.LogFolder));
        return false;
    }

    public static bool DeleteAllReports()
    {
        Crash.Host.DeleteAll();
        return false;
    }

    /// <summary>"View": the raw local report in Notepad.</summary>
    public static void ViewCrashReport(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", "\"" + path + "\"") { UseShellExecute = false })?.Dispose(); }
        catch (Exception ex) { Log.Warn("crash", "reports.view.failed", ex); }
    }

    /// <summary>"Copy": exactly what a send would carry — the scrub rules are read on the UI thread, the scrub and layout run off
    /// it, and the clipboard and the toast come back on it. A failed scrub copies nothing (never an unscrubbed report).</summary>
    public static void CopyCrashReport(Crash.BundleInfo b)
    {
        var rules = Crash.Scrubber.RulesNow();   // UI thread: the live account/device seams
        var hooks = SettingsContext.Hooks;
        var post = SettingsContext.Post;
        _ = Task.Run(() =>
        {
            string text = Crash.ComposePreview(b, rules);
            if (text.Length == 0) return;   // the scrub failed (logged)
            post(() =>
            {
                hooks?.Clipboard?.SetText(text);
                Notify.Say(Loc.Get(PvReports.Copied), InfoBarSeverity.Success, dedupeKey: "crash-copied:" + b.Summary.ReportId);
            });
        });
    }
}
