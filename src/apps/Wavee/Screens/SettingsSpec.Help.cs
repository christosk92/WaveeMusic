// ── Screens/SettingsSpec.Help.cs — Help & about: updates, feedback, shortcuts and diagnostics (the dials and their verbs) ─────────
//
// Role: CORE (settings rework phase 1). About Wavee (the update panel's buttons and switches, the receipts, What's new) · Feedback
// (report a problem, suggest, ask, what a report holds, copy diagnostics) · Keyboard shortcuts (the chords the shell defines, a
// READ-ONLY reference: nothing can be rebound) · Diagnostics (the log viewer and its detail levels, the tools, Connect's page) ·
// Developer · Links and licenses. Everything with LIVE state — the update snapshot, the receipts tick — is a child component of the
// view; these dials are what its buttons do.

using FluentGpu.Controls;
using FluentGpu.Localization;
using PvDev = Wavee.Strings.Settings.Privacy.Developer;
using PvLogs = Wavee.Strings.Settings.Privacy.Logs;
using PvTools = Wavee.Strings.Settings.Privacy.Tools;
using K = Wavee.SettingsRowKind;

namespace Wavee;

public static partial class SettingsSpec
{
    static SettingsCategory HelpCategory()
    {
        // The shortcuts are a read-only reference: Advanced, so a search ranks the setting ("Remember volume") before the chord that
        // shares its word ("Volume up").
        var shortcuts = new List<SettingsDial>(Settings.Shortcuts.All.Length);
        foreach (var shortcut in Settings.Shortcuts.All)
        {
            var s = shortcut;
            shortcuts.Add(Make("help.shortcuts." + s.Id, K.Fact, s.TitleKey, SettingsWriter.ReadOnly, legacy: [s.Id], tier: SettingsTier.Advanced,
                captionKey: s.CaptionKey, value: _ => s.Chord, visible: s.NeedsTabs ? TabsOffered : null));
        }

        return new("help", Strings.Settings.Categories.Help.Title, Strings.Settings.Categories.Help.Strip,
            Strings.Settings.Categories.Help.Subtitle, "Info",
        [
            Group("about", Strings.Settings.Groups.Help.About, null,
                // The hero's primary button: check · update now · retry, chosen from the live state at the press (AboutRules.Hero).
                Make("help.update.hero", K.Action, Strings.Update.Action.Check,
                    SettingsWriter.OfAction("about.hero", SettingsVerbs.RunHeroVerb), legacy: ["updateHero"]),
                Make("help.update.openReleasePage", K.Action, Strings.Update.Action.OpenReleasePage,
                    SettingsWriter.OfAction("about.openReleasePage", () => SettingsVerbs.RunUpdateVerb(UpdateRowAction.OpenReleasePage)),
                    legacy: ["updateOpenReleasePage"], parent: "help.update.hero"),
                Make("help.update.dismiss", K.Action, Strings.Update.Action.Dismiss,
                    SettingsWriter.OfAction("about.dismissUpdate", () => SettingsVerbs.RunUpdateVerb(UpdateRowAction.Dismiss)),
                    legacy: ["updateDismiss"], parent: "help.update.hero"),
                Make("help.update.store", K.Action, Strings.Update.Store.Title,
                    SettingsWriter.OfAction("about.openStore", () => SettingsVerbs.RunUpdateVerb(UpdateRowAction.UpdateNow)), legacy: ["storeOpen"],
                    captionKey: Strings.Update.Store.Hint),
                Make("help.update.channel", K.Action, Strings.Update.Channel.Title,
                    SettingsWriter.OfAction("about.betaChannel", SettingsVerbs.OpenBetaFeed), legacy: ["channel"],
                    captionKey: Strings.Update.Channel.Hint),
                Make("help.update.installOnQuit", K.Toggle, Strings.Update.InstallOnQuit.Title,
                    SettingsWriter.OfToggle("about.installOnQuit", WriteBool(Platform.Keys.UpdateInstallOnQuit)), legacy: ["installOnQuit"],
                    captionKey: Strings.Update.InstallOnQuit.Hint, value: BoolOf(Platform.Keys.UpdateInstallOnQuit)),
                Make("help.update.onMetered", K.Toggle, Strings.Update.Metered.Title,
                    SettingsWriter.OfToggle("about.updateOnMetered", WriteBool(Platform.Keys.UpdateOnMetered)), legacy: ["updateOnMetered"],
                    captionKey: Strings.Update.Metered.Hint, value: BoolOf(Platform.Keys.UpdateOnMetered)),
                Make("help.update.autoShow", K.Toggle, Strings.Update.AutoShow.Title,
                    SettingsWriter.OfToggle("about.autoShowNotes", WriteBool(Platform.Keys.ReleaseNotesAutoShow)), legacy: ["autoShow"],
                    captionKey: Strings.Update.AutoShow.Hint, value: BoolOf(Platform.Keys.ReleaseNotesAutoShow), contexts: [SettingsSurfaces.WhatsNew]),
                // The receipts tick on a 5 s interval on the view's child.
                Make("help.receipts", K.Fact, Strings.Settings.About.RightNow, SettingsWriter.ReadOnly, legacy: ["receipts"]),
                AboutLink("help.whatsNew", "whatsNew", Strings.Update.About.OpenWhatsNew, SettingsVerbs.OpenWhatsNew),
                Make("help.showSummaryAgain", K.Action, Strings.Update.About.ShowSummaryAgain,
                    SettingsWriter.OfAction("about.showSummaryAgain", SettingsVerbs.ShowSummaryAgain), legacy: ["showSummaryAgain"])),

            // "Support Wavee": its own group, never inside `links` (Classic's LinksCard would draw the door as a plain link; the door is
            // a link card of its own in every theme). A Store install sees the rate door and switch, a GitHub or source build the star
            // one; a Store install never reads "star" or the repo.
            Group("support", Strings.Settings.Groups.Help.Support, null,
                Make("help.links.rate", K.Action, Strings.RateNudge.Door.Rate,
                    SettingsWriter.OfAction("about.link:rate", RateNudge.DoorPrimary), captionKey: Strings.RateNudge.Door.RateSub,
                    visible: static _ => RateNudge.DoorAsk(RateNudge.Channel) == RateNudge.Ask.Rate),
                // A caption may be a key or resolved text (SettingsPageModel.Text passes non-keys through): the count is text.
                Make("help.links.star", K.Action, Strings.RateNudge.Door.Star,
                    SettingsWriter.OfAction("about.link:star", RateNudge.DoorPrimary),
                    caption: static _ => RateNudge.StarCount is { } n ? Strings.RateNudge.Door.StarSubCount(n) : Strings.RateNudge.Door.StarSub,
                    visible: static _ => RateNudge.DoorAsk(RateNudge.Channel) == RateNudge.Ask.Star),
                Make("help.rate.ask", K.Toggle, Strings.RateNudge.Setting.Rate,
                    SettingsWriter.OfToggle("rate.ask", RateNudge.SetEnabled), caption: static _ => RateNudge.CapHint(),
                    value: static _ => RateNudge.Current.Enabled,
                    visible: static _ => RateNudge.CardAsk(RateNudge.Channel) == RateNudge.Ask.Rate),
                Make("help.star.ask", K.Toggle, Strings.RateNudge.Setting.Star,
                    SettingsWriter.OfToggle("star.ask", RateNudge.SetEnabled), caption: static _ => RateNudge.CapHint(),
                    value: static _ => RateNudge.Current.Enabled,
                    visible: static _ => RateNudge.CardAsk(RateNudge.Channel) == RateNudge.Ask.Star)),

            // The Mix lesson (#48): "Build a transition together", a hands-on lesson in the Mix editor.
            Group("learn", Strings.Settings.Groups.Help.Learn, null,
                Make("help.learn.mix", K.Action, Strings.Mix.Lesson.Settings.Row, SettingsWriter.OfAction("learn.mix", static () => { MixLesson.Open(); return true; }),
                    captionKey: Strings.Mix.Lesson.Settings.Caption, glyph: "Equalizer")),

            Group("feedback", Strings.Settings.Groups.Help.Feedback, Strings.Settings.Feedback.Subtitle,
                Make("help.feedback.report", K.Action, Strings.Settings.Feedback.ReportProblem,
                    SettingsWriter.OfAction("feedback.report", SettingsVerbs.ReportProblem), legacy: ["reportProblem"],
                    captionKey: Strings.Settings.Feedback.ReportProblemSub),
                Make("help.feedback.suggest", K.Action, Strings.Settings.Feedback.Suggest,
                    SettingsWriter.OfAction("feedback.suggest", () => SettingsVerbs.OpenFeedback(Feedback.ReportKind.Feature)), legacy: ["suggestFeature"],
                    captionKey: Strings.Settings.Feedback.SuggestSub),
                Make("help.feedback.ask", K.Action, Strings.Settings.Feedback.Ask,
                    SettingsWriter.OfAction("feedback.ask", () => SettingsVerbs.OpenFeedback(Feedback.ReportKind.Question)), legacy: ["askQuestion"],
                    captionKey: Strings.Settings.Feedback.AskSub),
                // What the report dialog sends (a fact: the dialog shows the whole report before anything leaves this PC).
                Make("help.feedback.reportHolds", K.Fact, Strings.Settings.Feedback.HoldsTitle, SettingsWriter.ReadOnly, legacy: ["reportHolds"]),
                Make("help.feedback.copyDiagnostics", K.Action, Strings.Settings.About.CopyDiagnostics,
                    SettingsWriter.OfAction("feedback.copyDiagnostics", SettingsVerbs.CopyDiagnostics), legacy: ["copyDiagnostics"])),

            // One collapsed expander of facts, not a page of its own (owner decision 1).
            new SettingsGroup("shortcuts", Strings.Settings.Groups.Help.Shortcuts, Strings.Settings.Keyboard.Subtitle, shortcuts.ToArray()),

            Group("diagnostics", Strings.Settings.Groups.Help.Diagnostics, PvLogs.Subtitle,
                // The card's description counts the live session (a ticking sentence, not a key).
                Make("help.logs.viewer", K.Action, PvLogs.Viewer,
                    SettingsWriter.OfAction("logs.openViewer", SettingsVerbs.OpenLogViewer), legacy: ["logViewer"], glyph: "ViewList", tier: SettingsTier.Advanced),
                Make("help.logs.detailLevel", K.Composite, PvLogs.DetailLevel,
                    SettingsWriter.Owned("SettingsVerbs.SetViewerLogLevel/SetFileLogLevel"), legacy: ["detailLevel"], glyph: "Filter",
                    tier: SettingsTier.Advanced, captionKey: PvLogs.DetailLevelSub),
                Make("help.logs.viewerLevel", K.Choice, PvLogs.InViewer,
                    SettingsWriter.OfChoice("logs.viewerLevel", SettingsVerbs.SetViewerLogLevel), legacy: ["logLevelViewer"], parent: "help.logs.detailLevel",
                    tier: SettingsTier.Advanced, captionKey: PvLogs.InViewerSub, value: _ => (int)Log.MinLevel, contexts: [SettingsSurfaces.LogsMenu]),
                Make("help.logs.fileLevel", K.Choice, PvLogs.InFile,
                    SettingsWriter.OfChoice("logs.fileLevel", SettingsVerbs.SetFileLogLevel), legacy: ["logLevelFile"], parent: "help.logs.detailLevel",
                    tier: SettingsTier.Advanced, captionKey: PvLogs.InFileSub,
                    value: _ => (int)LogCapturePolicy.EffectiveFileLevel(Log.MinLevel, Log.FileMinLevel)),
                Make("help.logs.files", K.Action, PvLogs.Files,
                    SettingsWriter.OfAction("logs.openFolder", SettingsVerbs.OpenLogFolder), legacy: ["logFiles"], glyph: "Folder", tier: SettingsTier.Advanced),
                Make("help.tools.playbackRuntime", K.Action, PvTools.PlaybackRuntime,
                    SettingsWriter.OfAction("tools.playbackRuntime", () => SettingsVerbs.OpenDiagnostics(Shell.RouteKind.PlaybackDiagnostics)),
                    legacy: ["playbackRuntime"], glyph: "MusicNote", tier: SettingsTier.Advanced, captionKey: PvTools.PlaybackRuntimeSub),
                // The persisted key is `dealerArchive` verbatim (no legacy renumbering); it applies live through the capture host.
                Make("help.tools.capture", K.Toggle, PvTools.Capture,
                    SettingsWriter.OfToggle("tools.capture", SettingsVerbs.SetRealtimeCapture), legacy: ["realtimeCapture"], glyph: "RadioTower",
                    tier: SettingsTier.Advanced, captionKey: PvTools.CaptureSub, value: BoolOf(Platform.Keys.DealerArchiveEnabled)),
                Make("help.tools.captureViewer", K.Action, PvTools.OpenCaptureViewer,
                    SettingsWriter.OfAction("tools.captureViewer", () => SettingsVerbs.OpenDiagnostics(Shell.RouteKind.CaptureDiagnostics)),
                    legacy: ["captureViewer"], parent: "help.tools.capture", tier: SettingsTier.Advanced, captionKey: PvTools.OpenCaptureViewerSub),
                Make("help.tools.captureFolder", K.Action, PvTools.OpenCaptureFolder,
                    SettingsWriter.OfAction("tools.captureFolder", SettingsVerbs.OpenCaptureFolder), legacy: ["captureFolder"], parent: "help.tools.capture",
                    tier: SettingsTier.Advanced, captionKey: PvTools.OpenCaptureFolderSub),
                // The Connect diagnostics page: a tool, with the others.
                Make("help.connectDiagnostics", K.Action, PvTools.Connect,
                    SettingsWriter.OfAction("tools.connect", () => SettingsVerbs.OpenDiagnostics(Shell.RouteKind.ConnectDiagnostics)),
                    legacy: ["connectDiagnostics"], tier: SettingsTier.Advanced, captionKey: PvTools.ConnectSub)),

            // The three items are PRESENT and greyed while the switch is off (D2), never composed away.
            Group("developer", Strings.Settings.Groups.Help.Developer, PvDev.Subtitle,
                Make("help.developer.mode", K.Toggle, PvDev.Mode,
                    SettingsWriter.OfToggle("developer.mode", SettingsVerbs.SetDeveloperMode), legacy: ["developerMode"], glyph: "Settings",
                    tier: SettingsTier.Advanced, captionKey: PvDev.ModeSub, value: BoolOf(Platform.Keys.DeveloperMode)),
                Make("help.developer.fpsOverlay", K.Toggle, PvDev.FpsOverlay,
                    SettingsWriter.OfToggle("developer.fpsOverlay", SettingsVerbs.SetFpsOverlay), legacy: ["fpsOverlay"], parent: "help.developer.mode",
                    glyph: "Clock", tier: SettingsTier.Advanced,
                    captionKey: PvDev.FpsOverlaySub, value: BoolOf(Platform.Keys.FpsOverlay), enabled: DeveloperOnly, silent: true),
                Make("help.developer.simulateUpdate", K.Action, PvDev.SimulateUpdate,
                    SettingsWriter.OfAction("developer.simulateUpdate", SettingsVerbs.SimulateUpdate), legacy: ["simulateUpdate"], parent: "help.developer.mode",
                    glyph: "Refresh", tier: SettingsTier.Advanced, captionKey: PvDev.SimulateUpdateSub, enabled: DeveloperOnly, silent: true),
                Make("help.developer.sendTestCrashReport", K.Action, PvDev.SendTestCrashReport,
                    SettingsWriter.OfAction("developer.sendTestCrashReport", SettingsVerbs.SendTestCrashReport), legacy: ["sendTestCrashReport"],
                    parent: "help.developer.mode", glyph: "StatusWarning", tier: SettingsTier.Advanced,
                    caption: f => f.CrashServiceConfigured ? PvDev.SendTestCrashReportSub : PvDev.SendTestCrashReportUnavailable,
                    enabled: f => !f.DeveloperMode ? SettingsEnablement.Off(null)
                        : !f.CrashServiceConfigured ? SettingsEnablement.Off(PvDev.SendTestCrashReportUnavailable)
                        : f.TestCrashBusy ? SettingsEnablement.Off(null) : SettingsEnablement.On,
                    silent: true)),

            Group("links", Strings.Settings.Groups.Help.Links, null,
                AboutLink("help.links.allIssues", "allIssues", Strings.Report.AboutAllIssues, () => SettingsVerbs.OpenUri(Settings.IssuesUrl)),
                AboutLink("help.links.website", "website", Strings.Settings.About.Website, () => SettingsVerbs.OpenUri(Settings.WebsiteUrl)),
                // The repo, one row under the website that used to open it (new: no old row id to answer to).
                Make("help.links.source", K.Action, Strings.Settings.About.Source,
                    SettingsWriter.OfAction("about.link:source", () => SettingsVerbs.OpenUri(Settings.SourceUrl))),
                AboutLink("help.links.thirdPartyNotices", "thirdPartyNotices", Strings.Settings.About.ThirdPartyNotices, SettingsVerbs.OpenNotices),
                AboutLink("help.links.openDataFolder", "openDataFolder", Strings.Settings.About.OpenDataFolder, SettingsVerbs.OpenDataFolder),
                // The licenses are static text.
                Make("help.links.licenses", K.Fact, Strings.Settings.About.Licenses, SettingsWriter.ReadOnly, legacy: ["licenses"])),
        ]);
    }

    static readonly Func<ISettingsFacts, bool> TabsOffered = static _ => ShellCaps.Now.Tabs;

    static SettingsEnablement DeveloperOnly(ISettingsFacts f) => f.DeveloperMode ? SettingsEnablement.On : SettingsEnablement.Off(null);

    /// <summary>An About link: the verb keeps its old name (<c>about.link:&lt;rowId&gt;</c>).</summary>
    static SettingsDial AboutLink(string id, string rowId, string titleKey, Func<bool> run)
        => Make(id, K.Action, titleKey, SettingsWriter.OfAction("about.link:" + rowId, run), legacy: [rowId]);
}

public static partial class SettingsVerbs
{
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
    /// developer-only dials elsewhere are page-composed, so the page re-reads.</summary>
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
