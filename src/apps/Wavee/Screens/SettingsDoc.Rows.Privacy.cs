// ── Screens/SettingsDoc.Rows.Privacy.cs — Privacy & diagnostics and About as document rows + their verbs ──────────
//
// Role: CORE (PR C13; extraction X36). Privacy (what leaves this PC, the crash-report mode with its memory-snapshot switch,
// the crash-service card, the policy link) · Crash reports (the saved-reports group's verbs) · Logs (the viewer, the two
// detail levels, the folder, Report a problem) · Tools · Developer; and the About tab (the update panel, the links, the
// receipts, the licenses).
//
// The decisions the tab makes are `PrivacyRules` (`Settings.Privacy.cs`, engine-free and tested). What a row DOES — a store
// write, a crash-service call, a Notepad launch, a clipboard copy of a scrubbed report, a test bundle — is a `SettingsVerbs`
// member here, with the file I/O and the network off the UI thread and the answer posted back through `SettingsContext.Post`.

using FluentGpu.Controls;
using FluentGpu.Localization;
using PvDev = Wavee.Strings.Settings.Privacy.Developer;
using PvFacts = Wavee.Strings.Settings.Privacy.Facts;
using PvLogs = Wavee.Strings.Settings.Privacy.Logs;
using PvReports = Wavee.Strings.Settings.Privacy.Reports;
using PvTools = Wavee.Strings.Settings.Privacy.Tools;
using T = Wavee.Settings.Tab;
using K = Wavee.SettingsRowKind;

namespace Wavee;

public static partial class SettingsDoc
{
    // ══ PRIVACY & DIAGNOSTICS ═══════════════════════════════════════════════════════════════════════════════════════

    static IReadOnlyList<SettingsSection> PrivacySections() =>
    [
        Sect(T.PrivacyDiagnostics, "Privacy", PvFacts.Title, PvFacts.Subtitle,
            // The collapsed answer is "No telemetry"; inside, the five places anything leaves this PC and why: facts, no control.
            Make(T.PrivacyDiagnostics, "Privacy", "whatLeaves", K.Fact, PvFacts.WhatLeaves, SettingsWriter.ReadOnly, captionKey: PvFacts.WhatLeavesSub),
            Make(T.PrivacyDiagnostics, "Privacy", "whatLeavesAi", K.Fact, PvFacts.AiModels, SettingsWriter.ReadOnly, captionKey: PvFacts.AiModelsSub),
            // Off / Ask each time / Automatic as three left-aligned radio items. Per-item enablement and captions are
            // `CrashModeEnablement` / `CrashModeCaptionKey`: Automatic alone is disabled (no service in this build), with its reason.
            Make(T.PrivacyDiagnostics, "Privacy", "crashReports", K.Segmented, PvFacts.CrashReports,
                SettingsWriter.OfChoice("crash.mode", SettingsVerbs.SetCrashMode),
                captionKey: PvFacts.CrashReportsSub, value: IntOf(Platform.Keys.CrashReporting)),
            // The memory-snapshot switch is live only while reporting is on (greyed, no reason of its own).
            Make(T.PrivacyDiagnostics, "Privacy", "crashDump", K.Toggle, PvFacts.IncludeDump,
                SettingsWriter.OfToggle("crash.includeDump", WriteBool(Platform.Keys.CrashIncludeDump)),
                captionKey: PvFacts.IncludeDumpSub, value: BoolOf(Platform.Keys.CrashIncludeDump),
                enabled: f => f.CrashReportingOn ? SettingsEnablement.On : SettingsEnablement.Off(null), silent: true),
            Make(T.PrivacyDiagnostics, "Privacy", "crashContents", K.Fact, PvFacts.Contents, SettingsWriter.ReadOnly, captionKey: PvFacts.ContentsSub),
            // "Your data on the crash service": the confirmed right-to-erasure (§J), off the UI thread.
            Make(T.PrivacyDiagnostics, "Privacy", "crashService", K.Action, PvFacts.DeleteMyData,
                SettingsWriter.OfAction("crash.deleteMyData", SettingsVerbs.DeleteCrashServiceData),
                enabled: f => f.CrashServiceConfigured ? SettingsEnablement.On : SettingsEnablement.Off(null), silent: true,
                confirm: new SettingsConfirm(PvFacts.DeleteMyDataConfirmTitle, PvFacts.DeleteMyDataConfirmBody, PvFacts.DeleteMyDataConfirmButton)),
            Make(T.PrivacyDiagnostics, "Privacy", "crashServiceCopy", K.Action, PvFacts.CopyId,
                SettingsWriter.OfAction("crash.copyInstallId", SettingsVerbs.CopyCrashInstallId), catalog: false),
            Make(T.PrivacyDiagnostics, "Privacy", "privacyPolicy", K.Action, PvFacts.Policy,
                SettingsWriter.OfAction("privacy.openPolicy", SettingsVerbs.OpenPrivacyPolicy), captionKey: PvFacts.PolicySub)),

        Sect(T.PrivacyDiagnostics, "Crash reports", PvReports.Title, PvReports.Subtitle,
            // ONE expander over the saved bundles: a row per report with a "…" menu (View · Copy · Send… · Delete) — the
            // per-report verbs are `SettingsVerbs.ViewCrashReport` / `CopyCrashReport` (and the crash module's send prompt).
            Make(T.PrivacyDiagnostics, "Crash reports", "savedReports", K.Composite, PvReports.Saved,
                SettingsWriter.Owned("SettingsVerbs.ViewCrashReport/CopyCrashReport")),
            Make(T.PrivacyDiagnostics, "Crash reports", "reportsSendNow", K.Action, PvReports.SendNow,
                SettingsWriter.OfAction("reports.sendQueued", SettingsVerbs.SendQueuedReports),
                enabled: f => f.CrashServiceConfigured ? SettingsEnablement.On : SettingsEnablement.Off(null), silent: true, catalog: false),
            Make(T.PrivacyDiagnostics, "Crash reports", "reportsDiscard", K.Action, PvReports.Discard,
                SettingsWriter.OfAction("reports.discardQueue", SettingsVerbs.DiscardQueuedReports), catalog: false),
            Make(T.PrivacyDiagnostics, "Crash reports", "reportsOpenFolder", K.Action, PvReports.OpenFolder,
                SettingsWriter.OfAction("reports.openFolder", SettingsVerbs.OpenReportsFolder), catalog: false),
            Make(T.PrivacyDiagnostics, "Crash reports", "reportsDeleteAll", K.Action, PvReports.DeleteAll,
                SettingsWriter.OfAction("reports.deleteAll", SettingsVerbs.DeleteAllReports),
                confirm: new SettingsConfirm(PvReports.DeleteAllConfirmTitle, PvReports.DeleteAllConfirmBody, PvReports.DeleteAll), catalog: false)),

        Sect(T.PrivacyDiagnostics, "Logs", PvLogs.Title, PvLogs.Subtitle,
            // The card's description counts the live session (a ticking sentence, not a key).
            Make(T.PrivacyDiagnostics, "Logs", "logViewer", K.Action, PvLogs.Viewer,
                SettingsWriter.OfAction("logs.openViewer", SettingsVerbs.OpenLogViewer)),
            Make(T.PrivacyDiagnostics, "Logs", "detailLevel", K.Composite, PvLogs.DetailLevel,
                SettingsWriter.Owned("SettingsVerbs.SetViewerLogLevel/SetFileLogLevel"), captionKey: PvLogs.DetailLevelSub),
            Make(T.PrivacyDiagnostics, "Logs", "logLevelViewer", K.Choice, PvLogs.InViewer,
                SettingsWriter.OfChoice("logs.viewerLevel", SettingsVerbs.SetViewerLogLevel),
                captionKey: PvLogs.InViewerSub, value: _ => (int)Log.MinLevel, catalog: false),
            Make(T.PrivacyDiagnostics, "Logs", "logLevelFile", K.Choice, PvLogs.InFile,
                SettingsWriter.OfChoice("logs.fileLevel", SettingsVerbs.SetFileLogLevel),
                captionKey: PvLogs.InFileSub, value: _ => (int)LogCapturePolicy.EffectiveFileLevel(Log.MinLevel, Log.FileMinLevel), catalog: false),
            // "Report a problem" lives in Feedback & help (Tab.Feedback, "Help"): one home for the action.
            Make(T.PrivacyDiagnostics, "Logs", "logFiles", K.Action, PvLogs.Files,
                SettingsWriter.OfAction("logs.openFolder", SettingsVerbs.OpenLogFolder))),

        Sect(T.PrivacyDiagnostics, "Tools", PvTools.Title, PvTools.Subtitle,
            Make(T.PrivacyDiagnostics, "Tools", "playbackRuntime", K.Action, PvTools.PlaybackRuntime,
                SettingsWriter.OfAction("tools.playbackRuntime", () => SettingsVerbs.OpenDiagnostics(Shell.RouteKind.PlaybackDiagnostics)),
                captionKey: PvTools.PlaybackRuntimeSub),
            // The Connect diagnostics page is a door of Devices & Connect (Tab.Devices, "Connect").
            // The persisted key is `dealerArchive` verbatim (no legacy renumbering); it applies live through the capture host.
            Make(T.PrivacyDiagnostics, "Tools", "realtimeCapture", K.Toggle, PvTools.Capture,
                SettingsWriter.OfToggle("tools.capture", SettingsVerbs.SetRealtimeCapture),
                captionKey: PvTools.CaptureSub, value: BoolOf(Platform.Keys.DealerArchiveEnabled)),
            Make(T.PrivacyDiagnostics, "Tools", "captureViewer", K.Action, PvTools.OpenCaptureViewer,
                SettingsWriter.OfAction("tools.captureViewer", () => SettingsVerbs.OpenDiagnostics(Shell.RouteKind.CaptureDiagnostics)),
                captionKey: PvTools.OpenCaptureViewerSub, catalog: false),
            Make(T.PrivacyDiagnostics, "Tools", "captureFolder", K.Action, PvTools.OpenCaptureFolder,
                SettingsWriter.OfAction("tools.captureFolder", SettingsVerbs.OpenCaptureFolder),
                captionKey: PvTools.OpenCaptureFolderSub, catalog: false)),

        // The three items are PRESENT and greyed while the switch is off (D2), never composed away.
        Sect(T.PrivacyDiagnostics, "Developer", PvDev.Title, PvDev.Subtitle,
            Make(T.PrivacyDiagnostics, "Developer", "developerMode", K.Toggle, PvDev.Mode,
                SettingsWriter.OfToggle("developer.mode", SettingsVerbs.SetDeveloperMode),
                captionKey: PvDev.ModeSub, value: BoolOf(Platform.Keys.DeveloperMode)),
            Make(T.PrivacyDiagnostics, "Developer", "fpsOverlay", K.Toggle, PvDev.FpsOverlay,
                SettingsWriter.OfToggle("developer.fpsOverlay", SettingsVerbs.SetFpsOverlay),
                captionKey: PvDev.FpsOverlaySub, value: BoolOf(Platform.Keys.FpsOverlay), enabled: DeveloperOnly, silent: true),
            Make(T.PrivacyDiagnostics, "Developer", "simulateUpdate", K.Action, PvDev.SimulateUpdate,
                SettingsWriter.OfAction("developer.simulateUpdate", SettingsVerbs.SimulateUpdate),
                captionKey: PvDev.SimulateUpdateSub, enabled: DeveloperOnly, silent: true),
            Make(T.PrivacyDiagnostics, "Developer", "sendTestCrashReport", K.Action, PvDev.SendTestCrashReport,
                SettingsWriter.OfAction("developer.sendTestCrashReport", SettingsVerbs.SendTestCrashReport),
                caption: f => f.CrashServiceConfigured ? PvDev.SendTestCrashReportSub : PvDev.SendTestCrashReportUnavailable,
                enabled: f => !f.DeveloperMode ? SettingsEnablement.Off(null)
                    : !f.CrashServiceConfigured ? SettingsEnablement.Off(PvDev.SendTestCrashReportUnavailable)
                    : f.TestCrashBusy ? SettingsEnablement.Off(null) : SettingsEnablement.On,
                silent: true)),
    ];

    static SettingsEnablement DeveloperOnly(ISettingsFacts f) => f.DeveloperMode ? SettingsEnablement.On : SettingsEnablement.Off(null);

    /// <summary>Crash-report mode <paramref name="mode"/> (0 Off · 1 Ask each time · 2 Automatic): Automatic needs a service to
    /// send to, so without one its radio is disabled and its caption says why.</summary>
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

    // ══ ABOUT ═══════════════════════════════════════════════════════════════════════════════════════════════════════
    //
    // Not in the catalog (About paints its own glyphs), so no GlyphRole. Everything with LIVE state — the update snapshot, the
    // receipts tick — is a child component of the view; these rows are what its buttons do.

    static IReadOnlyList<SettingsSection> AboutSections() =>
    [
        Sect(T.About, "Update", Strings.Update.Status.Title, null,
            // The hero's primary button: check · update now · retry, chosen from the live state at the press (AboutRules.Hero).
            Make(T.About, "Update", "updateHero", K.Action, Strings.Update.Action.Check,
                SettingsWriter.OfAction("about.hero", SettingsVerbs.RunHeroVerb), catalog: false),
            Make(T.About, "Update", "updateOpenReleasePage", K.Action, Strings.Update.Action.OpenReleasePage,
                SettingsWriter.OfAction("about.openReleasePage", () => SettingsVerbs.RunUpdateVerb(UpdateRowAction.OpenReleasePage)), catalog: false),
            Make(T.About, "Update", "updateDismiss", K.Action, Strings.Update.Action.Dismiss,
                SettingsWriter.OfAction("about.dismissUpdate", () => SettingsVerbs.RunUpdateVerb(UpdateRowAction.Dismiss)), catalog: false),
            Make(T.About, "Update", "storeOpen", K.Action, Strings.Update.Store.Title,
                SettingsWriter.OfAction("about.openStore", () => SettingsVerbs.RunUpdateVerb(UpdateRowAction.UpdateNow)),
                captionKey: Strings.Update.Store.Hint, catalog: false),
            Make(T.About, "Update", "channel", K.Action, Strings.Update.Channel.Title,
                SettingsWriter.OfAction("about.betaChannel", SettingsVerbs.OpenBetaFeed),
                captionKey: Strings.Update.Channel.Hint, catalog: false),
            Make(T.About, "Update", "installOnQuit", K.Toggle, Strings.Update.InstallOnQuit.Title,
                SettingsWriter.OfToggle("about.installOnQuit", WriteBool(Platform.Keys.UpdateInstallOnQuit)),
                captionKey: Strings.Update.InstallOnQuit.Hint, value: BoolOf(Platform.Keys.UpdateInstallOnQuit), catalog: false),
            Make(T.About, "Update", "updateOnMetered", K.Toggle, Strings.Update.Metered.Title,
                SettingsWriter.OfToggle("about.updateOnMetered", WriteBool(Platform.Keys.UpdateOnMetered)),
                captionKey: Strings.Update.Metered.Hint, value: BoolOf(Platform.Keys.UpdateOnMetered), catalog: false),
            Make(T.About, "Update", "autoShow", K.Toggle, Strings.Update.AutoShow.Title,
                SettingsWriter.OfToggle("about.autoShowNotes", WriteBool(Platform.Keys.ReleaseNotesAutoShow)),
                captionKey: Strings.Update.AutoShow.Hint, value: BoolOf(Platform.Keys.ReleaseNotesAutoShow), catalog: false)),

        Sect(T.About, "Links", Strings.Settings.About.Website, null,
            // "Report a problem", "Suggest a feature" and "Copy diagnostics" moved to Feedback & help (Tab.Feedback).
            AboutLink("whatsNew", Strings.Update.About.OpenWhatsNew, SettingsVerbs.OpenWhatsNew),
            AboutLink("allIssues", Strings.Report.AboutAllIssues, () => SettingsVerbs.OpenUri(Settings.IssuesUrl)),
            AboutLink("website", Strings.Settings.About.Website, () => SettingsVerbs.OpenUri(Settings.WebsiteUrl)),
            AboutLink("privacyPolicy", Strings.Settings.About.PrivacyPolicy, () => SettingsVerbs.OpenUri(Settings.PrivacyUrl)),
            AboutLink("thirdPartyNotices", Strings.Settings.About.ThirdPartyNotices, SettingsVerbs.OpenNotices),
            AboutLink("openDataFolder", Strings.Settings.About.OpenDataFolder, SettingsVerbs.OpenDataFolder),
            Make(T.About, "Links", "showSummaryAgain", K.Action, Strings.Update.About.ShowSummaryAgain,
                SettingsWriter.OfAction("about.showSummaryAgain", SettingsVerbs.ShowSummaryAgain), catalog: false)),

        // The receipts tick on a 5 s interval on the view's child; the licenses are static text.
        Sect(T.About, "Right now", Strings.Settings.About.RightNow, null,
            Make(T.About, "Right now", "receipts", K.Fact, Strings.Settings.About.RightNow, SettingsWriter.ReadOnly, catalog: false)),
        Sect(T.About, "Licenses", Strings.Settings.About.Licenses, null,
            Make(T.About, "Licenses", "licenses", K.Fact, Strings.Settings.About.Licenses, SettingsWriter.ReadOnly, catalog: false)),
    ];

    static SettingsRow AboutLink(string id, string titleKey, Func<bool> run)
        => Make(T.About, "Links", id, K.Action, titleKey, SettingsWriter.OfAction("about.link:" + id, run), catalog: false);
}

public static partial class SettingsVerbs
{
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

    // ── Logs and tools ───────────────────────────────────────────────────────────────────────────────────────────────

    public static bool OpenLogViewer()
    {
        Shell.GoTo(new Shell.Route(Shell.RouteKind.Logs));
        return false;
    }

    /// <summary>The write is posted so the click finishes before the group remounts on the new level.</summary>
    public static bool SetViewerLogLevel(int level)
    {
        SettingsContext.Post(() => LogCapturePolicy.SetMinLevel(Platform.Settings, (WaveeLogLevel)Math.Clamp(level, 0, 4)));
        return false;
    }

    public static bool SetFileLogLevel(int level)
    {
        SettingsContext.Post(() => LogCapturePolicy.SetFileLevel(Platform.Settings, (WaveeLogLevel)Math.Clamp(level, 0, 4)));
        return false;
    }

    public static bool OpenLogFolder()
    {
        Diagnostics.OpenFolder(Platform.LogFolder);
        return false;
    }

    public static bool ReportProblem() => OpenFeedback(Feedback.ReportKind.Bug);

    public static bool OpenFeedback(Feedback.ReportKind kind)
    {
        Feedback.Open(kind);
        return false;
    }

    /// <summary>A clickable card that opens one diagnostics page (Playback keeps its own door to the same pages).</summary>
    public static bool OpenDiagnostics(Shell.RouteKind route)
    {
        Shell.GoTo(new Shell.Route(route));
        return false;
    }

    /// <summary>Applies live through <c>RealtimeCaptureHost.OnSettingsChanged</c>.</summary>
    public static bool SetRealtimeCapture(bool on)
    {
        Platform.Settings.Set(Platform.Keys.DealerArchiveEnabled, on);
        RealtimeCaptureHost.OnSettingsChanged();
        return true;
    }

    public static bool OpenCaptureFolder()
    {
        Diagnostics.OpenFolder(System.IO.Path.Combine(Platform.LogFolder, "capture"));
        return false;
    }

    // ── Developer ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Through <see cref="Platform.Developer"/> — the one persist-then-publish setter — so it applies at once; the
    /// Appearance / Notifications developer-only rows are page-composed, so the page re-reads.</summary>
    public static bool SetDeveloperMode(bool on)
    {
        Platform.Developer.Set(on);
        return true;
    }

    public static bool SetFpsOverlay(bool on)
    {
        Platform.Developer.SetFpsOverlay(on);
        return false;
    }

    public static bool SimulateUpdate()
    {
        Update.Host.SimulateUpdate();
        return false;
    }

    /// <summary>Developer › "Send a test crash report" (#165): <see cref="Crash.Host.WriteTestReport"/> off the UI thread (it
    /// blocks on the crash handler's dump), then — back on it — the Saved reports bump and the NORMAL send:
    /// <see cref="Crash.Uploader.Enqueue"/> with the dump, whatever the reporting mode (this click is the manual send), and the
    /// real outcome card carrying the report's short id. <see cref="SettingsFacts.TestCrashBusy"/> guards the GENERATION (one live
    /// dump request at a time from here), not the upload.</summary>
    public static bool SendTestCrashReport()
    {
        if (SettingsFacts.TestCrashBusy.Peek() || !Crash.Uploader.Configured) return false;
        SettingsFacts.TestCrashBusy.Value = true;
        var post = SettingsContext.Post;
        var rules = Crash.Scrubber.RulesNow();   // UI thread: the live account/device tables
        var writing = Notify.Say(Loc.Get(Strings.Crash.TestReportWriting), InfoBarSeverity.Informational,
            dedupeKey: "crash-test-report", durationMs: 15000f);
        _ = Task.Run(() =>
        {
            Crash.BundleInfo? bundle = Crash.Host.WriteTestReport(rules);
            post(() =>
            {
                SettingsFacts.TestCrashBusy.Value = false;
                if (bundle is null)
                {
                    writing.Close();
                    Notify.Say(Loc.Get(Strings.Crash.TestReportFailed), InfoBarSeverity.Error, durationMs: 8000f);
                    return;
                }
                Crash.Host.ReportsVersion.Value++;   // Saved reports lists it now
                var outcome = Crash.OutcomeToasts.Track(bundle, queuedShown: false, test: true);
                Crash.Uploader.Enqueue(bundle, includeDump: true, settled: rec =>
                {
                    writing.Close();
                    outcome(rec);
                });
            });
        });
        return false;
    }

    // ── About ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Every update verb goes through the updater's one door, with the snapshot the button was drawn from.</summary>
    public static bool RunUpdateVerb(UpdateRowAction action)
    {
        Notify.UpdateCommand?.Invoke(action, Notify.Update.Peek());
        return false;
    }

    /// <summary>The hero's primary button, chosen from the live state (W20): a user feed check, an update verb, or nothing.</summary>
    public static bool RunHeroVerb()
    {
        var me = Platform.Version;
        var shape = Settings.AboutRules.Hero(Notify.Update.Peek().State, me.IsDev && !Update.Host.IsSimulating, me.IsStore);
        switch (shape.Verb)
        {
            case Settings.AboutRules.HeroVerb.Check: Update.Host.CheckNow(); break;
            case Settings.AboutRules.HeroVerb.UpdateNow: RunUpdateVerb(UpdateRowAction.UpdateNow); break;
            case Settings.AboutRules.HeroVerb.Retry: RunUpdateVerb(UpdateRowAction.Retry); break;
        }
        return false;
    }

    /// <summary>The beta channel is a SIDE-BY-SIDE package: the link hands the user to App Installer.</summary>
    public static bool OpenBetaFeed()
    {
        OpenUri(Settings.BetaFeedUrl);
        return false;
    }

    public static bool OpenWhatsNew()
    {
        Shell.GoTo(new Shell.Route(Shell.RouteKind.WhatsNew));
        return false;
    }

    public static bool OpenNotices()
    {
        Settings.OpenNotices();
        return false;
    }

    public static bool CopyDiagnostics()
    {
        Settings.CopyDiagnostics();
        return false;
    }

    public static bool OpenDataFolder()
    {
        Settings.OpenFolder(Platform.LocalFolder);
        return false;
    }

    /// <summary>The after-update plate on demand; a dev build has no update story to summarize.</summary>
    public static bool ShowSummaryAgain()
    {
        if (SettingsContext.Overlay is { } overlay && ReleaseNotes.CanShowSummaryAgain)
            ReleaseNotes.ShowSummaryAgain(overlay, SettingsContext.Post);
        return false;
    }
}
