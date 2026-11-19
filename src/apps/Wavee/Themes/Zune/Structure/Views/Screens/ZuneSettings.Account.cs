// ── Themes/Zune/Structure/Views/Screens/ZuneSettings.Account.cs — Account & privacy's sections (themes plan 6.3, Z8b; settings rework phase 4) ─────────
//
// Boards Z-Settings-Sections ("account", "privacy"). The account card (the 48 avatar beside the name and the plan; the profile, the account page and log out are
// the group's plain actions, a row of command words), what leaves this PC (facts, so no controls), the crash-report mode (three radio lines: Automatic alone
// greys with its reason) with the memory-snapshot switch and what a report contains, the crash service's data (the shortened install id, copy, the confirmed
// right to erasure) and the saved reports (one line each with its own commands, then open folder and delete all).
//
// The decisions are `Settings.PrivacyRules` (engine-free), the verbs are the dials' writers. A part that must follow something the page does not bump (the reports
// folder, the send outbox, the crash service's data epoch) is its own component with its own subscriptions, so the page never re-renders for them. Every
// destructive action confirms through `Controls.Confirm`; every toast goes through `Notify.Say` (the verbs').

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using PrivacyRules = Wavee.Settings.PrivacyRules;
using PvFacts = Wavee.Strings.Settings.Privacy.Facts;
using PvReports = Wavee.Strings.Settings.Privacy.Reports;
using Z = Wavee.Themes.Zune.ZuneSettingsRules;

namespace Wavee.Themes.Zune;

internal static partial class ZuneSettings
{
    // ══ the account ══════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The account card (Z-Settings-Sections): the avatar beside the name (a skeleton until the profile answers) and the plan in the label voice, the
    /// plan the accent for Premium. Keyed as the identity dial's row; the plan dial's arrival lands on it too.</summary>
    static Element AccountCard(Paint d, SettingsBodyRow row)
    {
        string name = row.Value as string ?? "";
        int tier = SettingsSpec.Dial(PlanDial).Current is int t ? t : 0;
        Element picture = name.Length > 0
            ? PersonPicture.Create("", Z.AccountAvatar, displayName: name, imageSourcePath: SettingsFacts.AccountAvatarUrl())
            : ZuneOverlayParts.SkeletonCircle(Z.AccountAvatar);
        var lines = new List<Element>(2)
        {
            name.Length > 0
                ? R.Type.SheetTitle(name) with { Color = Tok.TextPrimary, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f }
                : ZuneOverlayParts.Skeleton(ZuneOverlayRules.HeaderNameBarWidth, ZuneOverlayRules.ChipNameBarHeight),
        };
        if (tier != 0)
            lines.Add(ZuneText.Cased(R.Type.DenseMeta(Loc.Get(tier == 2 ? Strings.Auth.PremiumBadge : Strings.Auth.FreeBadge)), LabelKind.Label)
                with { Color = tier == 2 ? Tok.AccentTextPrimary : Tok.TextSecondary, MaxLines = 1, MinWidth = 0f });
        return new BoxEl
        {
            Key = SettingsKeys.ZuneRow(row.Id), Direction = 0, AlignItems = FlexAlign.Center, Gap = Z.AccountGap, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
            MinHeight = Z.AccountAvatar,
            OnRealized = h => { s_rowNodes[row.Id] = h; s_rowNodes[PlanDial] = h; },
            Children = [picture, new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Children = lines.ToArray() }],
        };
    }

    const string PlanDial = "account.plan";

    // ══ privacy ══════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The collapsed answer is "no telemetry"; below it, the places anything leaves this PC and why. Facts, so no controls. Keyed as the dial's row, the AI
    /// models' line as its child's.</summary>
    static Element WhatLeaves(Paint d, SettingsBodyRow row)
    {
        var k = d.K;
        string aiKey = SettingsKeys.ZuneRow(WhatLeavesAiDial);
        return new BoxEl
        {
            Key = SettingsKeys.ZuneRow(row.Id), Direction = 1, Gap = Z.RowGap, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, OnRealized = NoteRow(row.Id),
            Children =
            [
                k.Row("zs:row:what-leaves", LabelOf(d.Focus, row.Id, ZuneText.Label(row.Label)), Loc.Get(PvFacts.WhatLeavesSub),
                    ZuneText.Label(Loc.Get(PvFacts.NoTelemetry)) with { Color = Tok.TextPrimary }),
                k.Row("zs:row:leaves-spotify", Loc.Get(PvFacts.Spotify), Loc.Get(PvFacts.SpotifySub), null),
                k.Row("zs:row:leaves-lyrics", Loc.Get(PvFacts.Lyrics), Loc.Get(PvFacts.LyricsSub), null),
                k.Row("zs:row:leaves-github", Loc.Get(PvFacts.Github), Loc.Get(PvFacts.GithubSub), null),
                k.Row(aiKey, LabelOf(d.Focus, WhatLeavesAiDial, ZuneText.Label(Loc.Get(PvFacts.AiModels))), Loc.Get(PvFacts.AiModelsSub), null)
                    with { OnRealized = NoteRow(WhatLeavesAiDial) },
                k.Row("zs:row:leaves-crash", Loc.Get(PvFacts.CrashService), Loc.Get(PvFacts.CrashServiceSub), null),
                ZuneText.Hint(Loc.Get(PvFacts.Footer)),
            ],
        };
    }

    const string WhatLeavesAiDial = "account.privacy.whatLeavesAi";

    sealed record CrashArgs(SettingsBodyRow Row, SettingsArrival Focus);

    /// <summary>Off / ask each time / automatic as three radio lines (a radio group has only a group-wide enabled flag, and Automatic alone must be disabled with its
    /// reason), then the memory-snapshot switch (live only while reporting is on) and what a report contains, each keyed as its dial's row. The answer is the
    /// EFFECTIVE mode: a stored Automatic on a build that cannot send behaves as, and shows as, ask each time.</summary>
    sealed class CrashModeRows : ZuneScreenComponent
    {
        public override Element Render()
        {
            var k = new ZuneKit(UseLayout());
            var args = UseProps<CrashArgs>();
            var row = args.Row;
            _ = Settings.PageEpoch.Value;
            var stored = row.Dial.Current is int storedMode ? (Crash.Reporting)storedMode : default;
            var effective = Crash.ConsentPolicy.Effective(stored, Crash.Uploader.Configured);
            bool reporting = effective != Crash.Reporting.Off;
            var facts = SettingsFacts.Live;
            var kids = new List<Element>(3)
            {
                k.Row(SettingsKeys.ZuneRow(row.Id), LabelOf(args.Focus, row.Id, ZuneText.Label(row.Label)), Loc.Get(PvFacts.CrashReportsSub) + SummarySeparator + Loc.Get(PrivacyRules.ModeKey(effective)),
                    new BoxEl
                    {
                        Direction = 1, Gap = Z.ControlGap, MinWidth = 0f,
                        Children =
                        [
                            Mode(row.Writer, Crash.Reporting.Off, effective, PvFacts.ModeOff, facts),
                            Mode(row.Writer, Crash.Reporting.Ask, effective, PvFacts.ModeAsk, facts),
                            Mode(row.Writer, Crash.Reporting.Auto, effective, PvFacts.ModeAuto, facts),
                        ],
                    }) with { OnRealized = NoteRow(row.Id) },
            };
            foreach (var child in row.Children)
            {
                string key = SettingsKeys.ZuneRow(child.Id);
                Element label = LabelOf(args.Focus, child.Id, ZuneText.Label(child.Label));
                var writer = child.Writer;
                kids.Add(child.Kind == SettingsRowKind.Toggle
                    ? k.Row(key, label, child.Description, k.Switch(child.Value is true, on => { if (writer.Toggle?.Invoke(on) == true) Settings.Bump(); }, reporting), reporting)
                        with { OnRealized = NoteRow(child.Id) }
                    : k.Row(key, label, child.Description, null) with { OnRealized = NoteRow(child.Id) });
            }
            return new BoxEl { Key = "zs:crash-mode:rows", Direction = 1, Gap = Z.RowGap, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, Children = kids.ToArray() };
        }

        /// <summary>One mode with its caption and enablement from the registry (Automatic needs a service, and says so). Compared against the STORED mode, so choosing
        /// ask over a stored-but-unusable Automatic really writes ask.</summary>
        static Element Mode(SettingsWriter writer, Crash.Reporting mode, Crash.Reporting current, string labelKey, ISettingsFacts facts)
        {
            bool enabled = SettingsSpec.CrashModeEnablement((int)mode, facts).Enabled;
            return new BoxEl
            {
                Key = "zs:crash-mode:" + (int)mode, Direction = 1, Gap = Z.LabelGap, MinWidth = 0f,
                Children =
                [
                    RadioButton.Create(Loc.Get(labelKey), current == mode, () =>
                    {
                        if (writer.Choose?.Invoke((int)mode) == true) Settings.Bump();
                    }, isEnabled: enabled),
                    ZuneText.Hint(Loc.Get(SettingsSpec.CrashModeCaptionKey((int)mode, facts)))
                        with { Color = enabled ? Tok.TextTertiary : Tok.TextDisabled, Margin = new Edges4(Z.RadioCaptionInset, 0f, 0f, 0f) },
                ],
            };
        }
    }

    /// <summary>"Your data on the crash service": the shortened install id, copy id (the child dial, keyed as its row) and the confirmed right to erasure (the dial).
    /// Its own component: the erase runs off the UI thread and, on success, the install id has ROTATED; the epoch below re-renders the row so the new id shows.</summary>
    sealed class CrashServiceRow : ZuneScreenComponent
    {
        public override Element Render()
        {
            var k = new ZuneKit(UseLayout());
            var args = UseProps<CrashArgs>();
            var row = args.Row;
            _ = SettingsFacts.CrashDataEpoch.Value;   // subscribed: a rotated install id re-renders
            string fullId = SettingsFacts.CrashInstallId();   // boot ensures it; a render never writes the store
            bool configured = Crash.Uploader.Configured;
            var controls = new List<Element>(2);
            foreach (var child in row.Children)
                if (child.Kind == SettingsRowKind.Action)
                    controls.Add(k.Cmd(Loc.Get(PvFacts.CopyId), RunOf(child), fullId.Length > 0) with { Key = SettingsKeys.ZuneRow(child.Id), OnRealized = NoteRow(child.Id) });
            var erase = row.Writer;
            controls.Add(k.Cmd(Loc.Get(PvFacts.DeleteMyData), () => ConfirmThen(Loc.Get(PvFacts.DeleteMyDataConfirmTitle), Loc.Get(PvFacts.DeleteMyDataConfirmBody),
                Loc.Get(PvFacts.DeleteMyDataConfirmButton), () => erase.Run?.Invoke()), configured));
            return k.Row(SettingsKeys.ZuneRow(row.Id), LabelOf(args.Focus, row.Id, ZuneText.Label(Loc.Get(PvFacts.YourData))), PvFacts.YourDataSub(PrivacyRules.ShortInstallId(fullId)),
                new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Z.ControlGap, Wrap = true, MinWidth = 0f, Children = controls.ToArray() })
                with { OnRealized = NoteRow(row.Id) };
        }
    }

    // ── saved reports ─────────────────────────────────────────────────────────────────────────────────────────────────────

    sealed record ReportsArgs(SettingsBodyRow Row);

    /// <summary>The saved reports: the count, the send-queue notice (only while the outbox is non-empty), a line per report with its own commands (view, copy, send,
    /// delete), then open folder and delete all; the queue's and the folder's verbs are the dial's children. Subscribes the two events that change what it shows, the
    /// reports folder and the outbox, so the page never re-renders for them.</summary>
    sealed class SavedReports : ZuneScreenComponent
    {
        public override Element Render()
        {
            var k = new ZuneKit(UseLayout());
            var row = UseProps<ReportsArgs>().Row;
            _ = Crash.Host.ReportsVersion.Value;       // subscribed
            _ = Crash.Uploader.OutboxVersion.Value;    // subscribed
            var bundles = Crash.Host.Bundles();        // at most Crash.Files.KeepBundles; event-driven, never per frame
            var tally = PrivacyRules.Tally(bundles, BundleBytes);
            int queued = Crash.Uploader.QueuedCount();
            bool configured = Crash.Uploader.Configured;
            SettingsDial? sendNow = null, discard = null, openFolder = null, deleteAll = null;
            foreach (var child in row.Children)
                switch (child.Id)
                {
                    case "account.reports.sendNow": sendNow = child.Dial; break;
                    case "account.reports.discard": discard = child.Dial; break;
                    case "account.reports.openFolder": openFolder = child.Dial; break;
                    case "account.reports.deleteAll": deleteAll = child.Dial; break;
                }

            var kids = new List<Element>(bundles.Count + 4);
            kids.Add(k.Row(SettingsKeys.ZuneRow(row.Id), row.Label,
                tally.Total == 0
                    ? Loc.Get(PvReports.SavedNone)
                    : PvReports.SavedSummary(tally.Sent.ToString(CultureInfo.InvariantCulture), tally.NotSent.ToString(CultureInfo.InvariantCulture),
                        tally.Closed.ToString(CultureInfo.InvariantCulture)),
                ZuneText.Meta(tally.Total == 1
                    ? PvReports.SavedCountOne(PrivacyRules.Mb(tally.Bytes))
                    : PvReports.SavedCount(tally.Total.ToString(CultureInfo.InvariantCulture), PrivacyRules.Mb(tally.Bytes)))) with { OnRealized = NoteRow(row.Id) });
            if (queued > 0)
                kids.Add(ZuneKit.Notice(InfoBarSeverity.Informational, "",
                    queued == 1 ? Loc.Get(PvReports.QueuedOne) : PvReports.Queued(queued.ToString(CultureInfo.InvariantCulture)),
                    new BoxEl
                    {
                        Direction = 0, Gap = Z.ControlGap, AlignItems = FlexAlign.Center,
                        Children =
                        [
                            Door(k, sendNow, Loc.Get(PvReports.SendNow), configured, accent: true),
                            Door(k, discard, Loc.Get(PvReports.Discard), true, accent: false),
                        ],
                    }) with { Key = "zs:reports:queued" });
            foreach (var b in bundles) kids.Add(ReportRow(k, b, configured) with { Key = "zs:crash:" + b.Summary.ReportId });
            kids.Add(k.Row("zs:row:reports-doors", Loc.Get(PvReports.OpenFolder), PvReports.Retention(Crash.Files.KeepBundles.ToString(CultureInfo.InvariantCulture)),
                new BoxEl
                {
                    Direction = 0, Gap = Z.ControlGap, AlignItems = FlexAlign.Center, Wrap = true, MinWidth = 0f,
                    Children =
                    [
                        Door(k, openFolder, Loc.Get(PvReports.OpenFolder), true, accent: false),
                        deleteAll is { } all
                            ? k.Cmd(Loc.Get(PvReports.DeleteAll), () => ConfirmThen(Loc.Get(PvReports.DeleteAllConfirmTitle), Loc.Get(PvReports.DeleteAllConfirmBody),
                                Loc.Get(PvReports.DeleteAll), () => all.Writer.Run?.Invoke()), tally.Total > 0) with { Key = SettingsKeys.ZuneRow(all.Id), OnRealized = NoteRow(all.Id) }
                            : s_none,
                    ],
                }));
            return new BoxEl { Key = "zs:reports:rows", Direction = 1, Gap = Z.RowGap, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, Children = kids.ToArray() };
        }

        /// <summary>A child dial's command, keyed as its row (nothing where the registry does not compose it).</summary>
        static Element Door(ZuneKit k, SettingsDial? dial, string word, bool enabled, bool accent)
            => dial is null ? s_none
                : k.Cmd(word, () => dial.Writer.Run?.Invoke(), enabled, accent) with { Key = SettingsKeys.ZuneRow(dial.Id), OnRealized = NoteRow(dial.Id) };

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
            string when = b.StampLocal.ToString("g", CultureInfo.CurrentCulture) + SummarySeparator + PvReports.ReportId(Crash.ShortId(id)) + SummarySeparator + StateText(b);
            return k.Row("zs:row:crash:" + id, Loc.Get(PrivacyRules.KindKey(s.Kind)) + SummarySeparator + WhatText(s), when,
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
             + (s.LastRoute.Length > 0 ? SummarySeparator + s.LastRoute : ""),
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
}
