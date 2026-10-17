// ── Themes/Zune/Structure/Views/Screens/ZuneSettings.Privacy.cs — the Privacy & diagnostics tab (themes plan 6.3, Z8b) ────────────────────────────
//
// Boards Z-Settings-Sections ("privacy"), Z-Feedback ("feedback & help": the report verbs, what a report holds, the log detail and the doors to this PC). Privacy (what
// leaves this PC, the crash-report mode with its memory-snapshot switch, the crash-service data, the policy link) · Crash reports (the saved reports, one line each with its
// own commands in place of the classic "..." menu) · Logs (the viewer with live counts, the two detail levels, the log-file rule, report a problem) · Tools (playback runtime,
// Spotify Connect, realtime capture) · Developer (developer mode and the three items it unlocks).
//
// The decisions are `Settings.PrivacyRules` (engine-free and tested), the verbs are the rows' writers. A group that must follow something the page does not bump (the reports
// folder, the send outbox, the log ring, the logger's levels, the developer signals) is its own component with its own subscriptions, so the page never re-renders for them.
// Every destructive action confirms through `Controls.Confirm`; every toast goes through `Notify.Say` (the verbs').

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming;
using static FluentGpu.Dsl.Ui;
using PrivacyRules = Wavee.Settings.PrivacyRules;
using PvDev = Wavee.Strings.Settings.Privacy.Developer;
using PvFacts = Wavee.Strings.Settings.Privacy.Facts;
using PvLogs = Wavee.Strings.Settings.Privacy.Logs;
using PvReports = Wavee.Strings.Settings.Privacy.Reports;
using PvTools = Wavee.Strings.Settings.Privacy.Tools;
using Tab = Wavee.Settings.Tab;
using Z = Wavee.Themes.Zune.ZuneSettingsRules;

namespace Wavee.Themes.Zune;

internal static partial class ZuneSettings
{
    static Element[] PrivacyTab(ZuneKit k) =>
    [
        Group(k, Tab.PrivacyDiagnostics, "Privacy",
            WhatLeaves(k), Embed.Comp(static () => new CrashModeRows()) with { Key = "zs:crash-mode" },
            Embed.Comp(static () => new CrashServiceRow()) with { Key = "zs:crash-service" },
            LinkRow(k, "privacyPolicy")),

        Group(k, Tab.PrivacyDiagnostics, "Crash reports", Embed.Comp(static () => new SavedReports()) with { Key = "zs:reports" }),

        Group(k, Tab.PrivacyDiagnostics, "Logs",
            Embed.Comp(static () => new LogViewerRow()) with { Key = "zs:log-viewer" },
            Embed.Comp(static () => new DetailLevelRows()) with { Key = "zs:detail-level" },
            k.Row("zs:row:log-files", Loc.Get(PvLogs.Files),
                PvLogs.FilesSub(PrivacyRules.WholeMb(Log.MaxFileBytes), Log.RetentionDays.ToString(CultureInfo.InvariantCulture), PrivacyRules.WholeMb(Log.RetentionBytes)),
                k.Cmd(Loc.Get(PvLogs.OpenFolder), static () => D(Tab.PrivacyDiagnostics, "logFiles").Writer.Run!())),
            LinkRow(k, "reportProblem", Loc.Get(PvLogs.Report), Loc.Get(PvLogs.ReportSub))),

        Group(k, Tab.PrivacyDiagnostics, "Tools",
            LinkRow(k, "playbackRuntime"), LinkRow(k, "connectDiagnostics"), CaptureRow(k)),

        Group(k, Tab.PrivacyDiagnostics, "Developer", Embed.Comp(static () => new DeveloperRows()) with { Key = "zs:developer" }),
    ];

    /// <summary>A row that is one command: the row's words, the command opening the page its writer opens.</summary>
    static Element LinkRow(ZuneKit k, string id, string? title = null, string? sub = null)
    {
        var row = D(Tab.PrivacyDiagnostics, id);
        return k.DocRow(row, k.Cmd(title ?? Loc.Get(row.TitleKey), () => row.Writer.Run!()), sub, row.Enabled(SettingsFacts.Live).Enabled);
    }

    /// <summary>The collapsed answer is "no telemetry"; below it, the five places anything leaves this PC and why. Facts, so no controls.</summary>
    static Element WhatLeaves(ZuneKit k) => new BoxEl
    {
        Key = "zs:what-leaves", Direction = 1, Gap = Z.RowGap, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
        Children =
        [
            k.Row("zs:row:what-leaves", Loc.Get(PvFacts.WhatLeaves), Loc.Get(PvFacts.WhatLeavesSub), ZuneText.Label(Loc.Get(PvFacts.NoTelemetry)) with { Color = Tok.TextPrimary }),
            k.Row("zs:row:leaves-spotify", Loc.Get(PvFacts.Spotify), Loc.Get(PvFacts.SpotifySub), null),
            k.Row("zs:row:leaves-lyrics", Loc.Get(PvFacts.Lyrics), Loc.Get(PvFacts.LyricsSub), null),
            k.Row("zs:row:leaves-github", Loc.Get(PvFacts.Github), Loc.Get(PvFacts.GithubSub), null),
            k.Row("zs:row:leaves-ai", Loc.Get(PvFacts.AiModels), Loc.Get(PvFacts.AiModelsSub), null),
            k.Row("zs:row:leaves-crash", Loc.Get(PvFacts.CrashService), Loc.Get(PvFacts.CrashServiceSub), null),
            ZuneText.Hint(Loc.Get(PvFacts.Footer)),
        ],
    };

    // ── the crash-report mode ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Off / ask each time / automatic as three radio lines (a radio group has only a group-wide enabled flag, and Automatic alone must be disabled with its
    /// reason), then the memory-snapshot switch (live only while reporting is on) and what a report contains. The answer is the EFFECTIVE mode: a stored Automatic on a
    /// build that cannot send behaves as, and shows as, ask each time.</summary>
    sealed class CrashModeRows : ZuneScreenComponent
    {
        public override Element Render()
        {
            var k = new ZuneKit(UseLayout());
            _ = Settings.PageEpoch.Value;
            var stored = D(Tab.PrivacyDiagnostics, "crashReports").Current is int storedMode ? (Crash.Reporting)storedMode : default;
            var effective = Crash.ConsentPolicy.Effective(stored, Crash.Uploader.Configured);
            bool reporting = effective != Crash.Reporting.Off;
            var facts = SettingsFacts.Live;
            var dump = D(Tab.PrivacyDiagnostics, "crashDump");
            var contents = D(Tab.PrivacyDiagnostics, "crashContents");
            return new BoxEl
            {
                Key = "zs:crash-mode:rows", Direction = 1, Gap = Z.RowGap, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
                Children =
                [
                    k.Row("zs:row:crash-mode", Loc.Get(PvFacts.CrashReports), Loc.Get(PvFacts.CrashReportsSub) + "  ·  " + Loc.Get(PrivacyRules.ModeKey(effective)),
                        new BoxEl
                        {
                            Direction = 1, Gap = Z.ControlGap, MinWidth = 0f,
                            Children =
                            [
                                Mode(Crash.Reporting.Off, effective, PvFacts.ModeOff, facts),
                                Mode(Crash.Reporting.Ask, effective, PvFacts.ModeAsk, facts),
                                Mode(Crash.Reporting.Auto, effective, PvFacts.ModeAuto, facts),
                            ],
                        }),
                    k.Row(ZuneKit.RowKey(dump), Loc.Get(PvFacts.IncludeDump), Loc.Get(PvFacts.IncludeDumpSub), k.Toggle(dump, reporting), reporting),
                    k.Row(ZuneKit.RowKey(contents), Loc.Get(PvFacts.Contents), Loc.Get(PvFacts.ContentsSub), null),
                ],
            };
        }

        /// <summary>One mode with its caption and enablement from the document (Automatic needs a service, and says so). Compared against the STORED mode, so choosing
        /// ask over a stored-but-unusable Automatic really writes ask.</summary>
        static Element Mode(Crash.Reporting mode, Crash.Reporting current, string labelKey, ISettingsFacts facts)
        {
            bool enabled = SettingsDoc.CrashModeEnablement((int)mode, facts).Enabled;
            return new BoxEl
            {
                Key = "zs:crash-mode:" + (int)mode, Direction = 1, Gap = Z.LabelGap, MinWidth = 0f,
                Children =
                [
                    RadioButton.Create(Loc.Get(labelKey), current == mode, () =>
                    {
                        if (D(Tab.PrivacyDiagnostics, "crashReports").Writer.Choose!((int)mode)) Settings.Bump();
                    }, isEnabled: enabled),
                    ZuneText.Hint(Loc.Get(SettingsDoc.CrashModeCaptionKey((int)mode, facts)))
                        with { Color = enabled ? Tok.TextTertiary : Tok.TextDisabled, Margin = new Edges4(Z.RadioCaptionInset, 0f, 0f, 0f) },
                ],
            };
        }
    }

    /// <summary>"Your data on the crash service": the shortened install id, copy id, and the confirmed right-to-erasure. Its own component: the erase runs off the UI
    /// thread and, on success, the install id has ROTATED; the epoch below re-renders the row so the new id shows.</summary>
    sealed class CrashServiceRow : ZuneScreenComponent
    {
        public override Element Render()
        {
            var k = new ZuneKit(UseLayout());
            _ = SettingsFacts.CrashDataEpoch.Value;   // subscribed: a rotated install id re-renders
            string fullId = SettingsFacts.CrashInstallId();   // boot ensures it; a render never writes the store
            bool configured = Crash.Uploader.Configured;
            return k.Row("zs:row:crash-service", Loc.Get(PvFacts.YourData), PvFacts.YourDataSub(PrivacyRules.ShortInstallId(fullId)),
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = Z.ControlGap, Wrap = true, MinWidth = 0f,
                    Children =
                    [
                        k.Cmd(Loc.Get(PvFacts.CopyId), static () => D(Tab.PrivacyDiagnostics, "crashServiceCopy").Writer.Run!(), fullId.Length > 0),
                        k.Cmd(Loc.Get(PvFacts.DeleteMyData), () => ConfirmThen(Loc.Get(PvFacts.DeleteMyDataConfirmTitle), Loc.Get(PvFacts.DeleteMyDataConfirmBody),
                            Loc.Get(PvFacts.DeleteMyDataConfirmButton), static () => D(Tab.PrivacyDiagnostics, "crashService").Writer.Run!()), configured),
                    ],
                });
        }
    }

    // ── saved reports ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The saved reports: the send-queue notice first (only while the outbox is non-empty), a line per report with its own commands (view, copy, send, delete),
    /// then open folder and delete all. Subscribes the two events that change what it shows, the reports folder and the outbox, so the page never re-renders for them.</summary>
    sealed class SavedReports : ZuneScreenComponent
    {
        public override Element Render()
        {
            var k = new ZuneKit(UseLayout());
            _ = Crash.Host.ReportsVersion.Value;       // subscribed
            _ = Crash.Uploader.OutboxVersion.Value;    // subscribed
            var bundles = Crash.Host.Bundles();        // at most Crash.Files.KeepBundles; event-driven, never per frame
            var tally = PrivacyRules.Tally(bundles, BundleBytes);
            int queued = Crash.Uploader.QueuedCount();
            bool configured = Crash.Uploader.Configured;

            var kids = new List<Element>(bundles.Count + 4);
            kids.Add(k.Row("zs:row:reports-saved", Loc.Get(PvReports.Saved),
                tally.Total == 0
                    ? Loc.Get(PvReports.SavedNone)
                    : PvReports.SavedSummary(tally.Sent.ToString(CultureInfo.InvariantCulture), tally.NotSent.ToString(CultureInfo.InvariantCulture),
                        tally.Closed.ToString(CultureInfo.InvariantCulture)),
                ZuneText.Meta(tally.Total == 1
                    ? PvReports.SavedCountOne(PrivacyRules.Mb(tally.Bytes))
                    : PvReports.SavedCount(tally.Total.ToString(CultureInfo.InvariantCulture), PrivacyRules.Mb(tally.Bytes)))));
            if (queued > 0)
                kids.Add(ZuneKit.Notice(InfoBarSeverity.Informational, "",
                    queued == 1 ? Loc.Get(PvReports.QueuedOne) : PvReports.Queued(queued.ToString(CultureInfo.InvariantCulture)),
                    new BoxEl
                    {
                        Direction = 0, Gap = Z.ControlGap, AlignItems = FlexAlign.Center,
                        Children =
                        [
                            k.Cmd(Loc.Get(PvReports.SendNow), static () => D(Tab.PrivacyDiagnostics, "reportsSendNow").Writer.Run!(), configured, accent: true),
                            k.Cmd(Loc.Get(PvReports.Discard), static () => D(Tab.PrivacyDiagnostics, "reportsDiscard").Writer.Run!()),
                        ],
                    }) with { Key = "zs:reports:queued" });
            foreach (var b in bundles) kids.Add(ReportRow(k, b, configured) with { Key = "zs:crash:" + b.Summary.ReportId });
            kids.Add(k.Row("zs:row:reports-doors", Loc.Get(PvReports.OpenFolder), PvReports.Retention(Crash.Files.KeepBundles.ToString(CultureInfo.InvariantCulture)),
                new BoxEl
                {
                    Direction = 0, Gap = Z.ControlGap, AlignItems = FlexAlign.Center, Wrap = true, MinWidth = 0f,
                    Children =
                    [
                        k.Cmd(Loc.Get(PvReports.OpenFolder), static () => D(Tab.PrivacyDiagnostics, "reportsOpenFolder").Writer.Run!()),
                        k.Cmd(Loc.Get(PvReports.DeleteAll), () => ConfirmThen(Loc.Get(PvReports.DeleteAllConfirmTitle), Loc.Get(PvReports.DeleteAllConfirmBody),
                            Loc.Get(PvReports.DeleteAll), static () => D(Tab.PrivacyDiagnostics, "reportsDeleteAll").Writer.Run!()), tally.Total > 0),
                    ],
                }));
            return new BoxEl { Key = "zs:reports:rows", Direction = 1, Gap = Z.RowGap, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, Children = kids.ToArray() };
        }

        /// <summary>One report: the kind dot, what it was and when, its send state, and its commands.</summary>
        static Element ReportRow(ZuneKit k, Crash.BundleInfo b, bool configured)
        {
            var s = b.Summary;
            string id = s.ReportId;
            var commands = new List<Element>(4)
            {
                k.Cmd(Loc.Get(PvReports.View), () => SettingsVerbs.ViewCrashReport(b.ReportTxt)),
                k.Cmd(Loc.Get(PvReports.Copy), () => SettingsVerbs.CopyCrashReport(b)),
            };
            if (PrivacyRules.CanSend(s.Kind, b.Send.State, configured))
                commands.Add(k.Cmd(Loc.Get(PvReports.Send), () =>
                {
                    if (SettingsContext.Overlay is { } overlay) Crash.OpenSendPrompt(overlay, b);
                }, accent: true));
            commands.Add(k.Cmd(Loc.Get(PvReports.Delete), () => ConfirmThen(Loc.Get(PvReports.DeleteConfirmTitle), Loc.Get(PvReports.DeleteConfirmBody),
                Loc.Get(PvReports.Delete), () => Crash.Host.Delete(b.Dir))));
            string when = b.StampLocal.ToString("g", CultureInfo.CurrentCulture) + " · " + PvReports.ReportId(Crash.ShortId(id)) + " · " + StateText(b);
            return k.Row("zs:row:crash:" + id, Loc.Get(PrivacyRules.KindKey(s.Kind)) + " · " + WhatText(s), when,
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = Z.ControlGap, Wrap = true, MinWidth = 0f,
                    Children = [new BoxEl { Width = Z.HueSize, Height = Z.HueSize, Shrink = 0f, Corners = CornerRadius4.All(ZuneControls.Round), Fill = DotFor(s.Kind) }, .. commands],
                });
        }
    }

    /// <summary>What a bundle holds on disk (the report, the log tail, the dump). An unreadable file counts as nothing.</summary>
    static long BundleBytes(Crash.BundleInfo b)
    {
        long n = 0;
        try
        {
            n += new FileInfo(b.ReportTxt).Length;
            if (File.Exists(b.TailTxt)) n += new FileInfo(b.TailTxt).Length;
            if (b.DumpPath is { } dump && File.Exists(dump)) n += b.DumpBytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn("crash", "reports.size.failed", ex); }
        return n;
    }

    static ColorF DotFor(Crash.Kind kind) => kind switch
    {
        Crash.Kind.Hang => Tok.SystemFillCaution,
        Crash.Kind.UncleanExit => Tok.SystemFillSolidNeutral,
        _ => Tok.SystemFillCritical,
    };

    /// <summary>The row's "what": the hang's seconds, why a "closed" report exists, or the exception type (the exit code when there is none) with the last route.</summary>
    static string WhatText(Crash.Summary s) => s.Kind switch
    {
        Crash.Kind.Hang => PrivacyRules.HangSeconds(s.ExceptionMessage) is { } seconds
            ? PvReports.HangWhat(seconds.ToString(CultureInfo.InvariantCulture))
            : s.ExceptionMessage.Length > 0 ? s.ExceptionMessage : Loc.Get(PvReports.KindHang),
        Crash.Kind.UncleanExit => Loc.Get(PvReports.ClosedWhat),
        _ => (s.ExceptionType.Length > 0 ? s.ExceptionType : "0x" + s.ExitCode.ToString("X", CultureInfo.InvariantCulture))
             + (s.LastRoute.Length > 0 ? " · " + s.LastRoute : ""),
    };

    /// <summary>"Sent 14:32", "Waiting to send", "Not sent", "Couldn't send", or a dash for a closed-unexpectedly report.</summary>
    static string StateText(Crash.BundleInfo b)
    {
        string? key = PrivacyRules.StateKey(b.Summary.Kind, b.Send.State);
        if (key is null) return Z.Dash;
        return b.Send.State == Crash.SendState.Sent
            ? PvReports.StateSent(PrivacyRules.SentTimeLocal(b.Send.SentAtUtc, TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.UtcNow)))
            : Loc.Get(key);
    }

    // ── logs ──────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The log viewer row: opens the viewer page, and its words count the live session. A 750 ms interval on THIS row writes only when the log or the minute
    /// moved, so one line of text re-renders and nothing runs per frame.</summary>
    sealed class LogViewerRow : ZuneScreenComponent
    {
        readonly Signal<string> _line = new(Compose());
        long _seenVersion = -1, _seenMinute = -1;

        public override Element Render()
        {
            var k = new ZuneKit(UseLayout());
            UseInterval(() =>
            {
                long version = Log.Version, minute = Log.SinceStartMs / Z.MillisPerMinute;
                if (version == _seenVersion && minute == _seenMinute) return;
                _seenVersion = version;
                _seenMinute = minute;
                _line.Value = Compose();
            }, Z.LogTickMs);
            var row = D(Tab.PrivacyDiagnostics, "logViewer");
            return k.Row(ZuneKit.RowKey(row), Loc.Get(PvLogs.Viewer), _line.Value, k.Cmd(Loc.Get(PvLogs.Viewer), () => row.Writer.Run!()));
        }

        static string Compose()
        {
            Log.Tally(out int total, out int warnings, out int errors);
            return PvLogs.ViewerSub(Thousands(total), Thousands(warnings), Thousands(errors), Diagnostics.LogView.Uptime(TimeSpan.FromMilliseconds(Log.SinceStartMs)));
        }

        static string Thousands(int n) => n.ToString("N0", CultureInfo.InvariantCulture);
    }

    /// <summary>The two detail levels: what the viewer captures and what reaches the log file. Both are persisted settings, so the group subscribes
    /// <see cref="Platform.SettingsChanged"/> (the viewer's Verbose toggle writes the same keys). A combo's items and selection freeze at mount, so each is keyed by the
    /// level VALUE it shows.</summary>
    sealed class DetailLevelRows : ZuneScreenComponent
    {
        public override Element Render()
        {
            var k = new ZuneKit(UseLayout());
            _ = Platform.SettingsChanged.Value;   // subscribed
            var viewer = Log.MinLevel;
            var file = LogCapturePolicy.EffectiveFileLevel(viewer, Log.FileMinLevel);
            int last = Diagnostics.LogView.LevelNames.Length - 1;
            int viewerIndex = Math.Clamp((int)viewer, 0, last);
            int fileIndex = Math.Clamp((int)file, 0, last);
            return new BoxEl
            {
                Key = "zs:detail-level:rows", Direction = 1, Gap = Z.RowGap, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
                Children =
                [
                    k.Row("zs:row:level-viewer", Loc.Get(PvLogs.InViewer), Loc.Get(PvLogs.InViewerSub),
                        k.Choice("logLevelViewer:" + viewerIndex, new ZuneChoice(Diagnostics.LogView.LevelNames, () => viewerIndex,
                            static i => D(Tab.PrivacyDiagnostics, "logLevelViewer").Writer.Choose!(i), Chips: false, Width: Z.LevelComboWidth))),
                    k.Row("zs:row:level-file", Loc.Get(PvLogs.InFile), Loc.Get(PvLogs.InFileSub),
                        k.Choice("logLevelFile:" + fileIndex, new ZuneChoice(Diagnostics.LogView.LevelNames, () => fileIndex,
                            static i => D(Tab.PrivacyDiagnostics, "logLevelFile").Writer.Choose!(i), Chips: false, Width: Z.LevelComboWidth))),
                ],
            };
        }
    }

    // ── tools ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The persisted key and subtitle are `dealerArchive` verbatim; the toggle applies live through `RealtimeCaptureHost.OnSettingsChanged`, and the two doors
    /// sit beside it.</summary>
    static Element CaptureRow(ZuneKit k)
    {
        var capture = D(Tab.PrivacyDiagnostics, "realtimeCapture");
        return k.DocRow(capture, new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Z.ControlGap, Wrap = true, MinWidth = 0f,
            Children =
            [
                k.Toggle(capture),
                k.Cmd(Loc.Get(PvTools.OpenCaptureViewer), static () => D(Tab.PrivacyDiagnostics, "captureViewer").Writer.Run!()),
                k.Cmd(Loc.Get(PvTools.OpenCaptureFolder), static () => D(Tab.PrivacyDiagnostics, "captureFolder").Writer.Run!()),
            ],
        });
    }

    // ── developer ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Developer mode and the three items it unlocks. The items are PRESENT and greyed while the switch is off, never composed away. The switch and the FPS
    /// overlay go through <see cref="Platform.Developer"/> (the one persist-then-publish setter) so they apply at once; the group reads the same signals, so it follows a
    /// flip made anywhere.</summary>
    sealed class DeveloperRows : ZuneScreenComponent
    {
        public override Element Render()
        {
            var k = new ZuneKit(UseLayout());
            var facts = SettingsFacts.Live;
            bool dev = facts.DeveloperMode;
            bool fps = Platform.Developer.FpsOverlay.Value;
            var fpsDoc = D(Tab.PrivacyDiagnostics, "fpsOverlay");
            var simulateDoc = D(Tab.PrivacyDiagnostics, "simulateUpdate");
            var testDoc = D(Tab.PrivacyDiagnostics, "sendTestCrashReport");
            bool fpsOn = fpsDoc.Enabled(facts).Enabled, simulateOn = simulateDoc.Enabled(facts).Enabled, testOn = testDoc.Enabled(facts).Enabled;
            return new BoxEl
            {
                Key = "zs:developer:rows", Direction = 1, Gap = Z.RowGap, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
                Children =
                [
                    k.Row("zs:row:developer-mode", Loc.Get(PvDev.Mode), Loc.Get(PvDev.ModeSub), k.Switch(dev, static on =>
                    {
                        // The Appearance and Notifications developer-only rows are page-composed, so the writer asks the page to re-read.
                        if (D(Tab.PrivacyDiagnostics, "developerMode").Writer.Toggle!(on)) Settings.Bump();
                    })),
                    k.Row("zs:row:developer-fps", Loc.Get(PvDev.FpsOverlay), Loc.Get(PvDev.FpsOverlaySub),
                        k.Switch(fps, static on => D(Tab.PrivacyDiagnostics, "fpsOverlay").Writer.Toggle!(on), fpsOn), fpsOn),
                    k.Row("zs:row:developer-simulate", Loc.Get(PvDev.SimulateUpdate), Loc.Get(PvDev.SimulateUpdateSub),
                        k.Cmd(Loc.Get(PvDev.SimulateUpdateButton), static () => D(Tab.PrivacyDiagnostics, "simulateUpdate").Writer.Run!(), simulateOn), simulateOn),
                    k.Row("zs:row:developer-test-report", Loc.Get(PvDev.SendTestCrashReport), Loc.Get(testDoc.Caption(facts)!),
                        k.Cmd(Loc.Get(PvDev.SendTestCrashReportButton), static () => D(Tab.PrivacyDiagnostics, "sendTestCrashReport").Writer.Run!(), testOn), dev && facts.CrashServiceConfigured),
                ],
            };
        }
    }
}
