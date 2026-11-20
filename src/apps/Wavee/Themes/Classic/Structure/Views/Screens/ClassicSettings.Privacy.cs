// ── Themes/Classic/Structure/Views/Screens/ClassicSettings.Privacy.cs — Account & privacy's and Help's diagnostics cards (settings rework 7.1) ──────
//
// From `Screens\Settings.UI.Privacy.cs`. Account & privacy › Privacy (what leaves this PC, the crash-report mode with its memory-snapshot switch, the crash-service
// card, the policy link) and › Crash reports (ONE Saved reports expander: the send-queue InfoBar, a row per report with a "…" menu, Open folder / Delete all);
// Help & about › Diagnostics (the log viewer card with live counts, the two detail levels, the log-file rule, Realtime capture) and › Developer (developer mode
// and the three items it unlocks). Each card is drawn where its dial lives; the child dials it holds are keyed as their rows (`FrameOf`).
//
// Spec: docs/plans/wavee/privacy-diagnostics-tab-implementation.md §3.1, §5.1, §5.3-§5.6 — the decisions are `Settings.PrivacyRules` (engine-free), the verbs are
// the dials' writers.
//
// THE SHAPE: a card that must follow something the page does not bump — the reports folder, the send outbox, the log ring, the logger's levels, the developer
// signals — is its own component with its own subscriptions, so the page never re-renders for them. Every expander starts COLLAPSED (the header answers);
// every destructive action confirms through `Controls.Confirm`; every toast goes through `Notify.Say`.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;
using PrivacyRules = Wavee.Settings.PrivacyRules;
using PvDev = Wavee.Strings.Settings.Privacy.Developer;
using PvFacts = Wavee.Strings.Settings.Privacy.Facts;
using PvLogs = Wavee.Strings.Settings.Privacy.Logs;
using PvReports = Wavee.Strings.Settings.Privacy.Reports;
using PvTools = Wavee.Strings.Settings.Privacy.Tools;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

internal static partial class ClassicSettings
{
    // ══ 2. PRIVACY ════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The collapsed answer is "No telemetry"; inside, the five places anything leaves this PC and why. Items
    /// carry no control — they are facts.</summary>
    static Element WhatLeavesExpander(SettingsBodyRow row) => SettingsExpander.Create(new SettingsExpander.Options
    {
        Header = Loc.Get(PvFacts.WhatLeaves),
        Description = Loc.Get(PvFacts.WhatLeavesSub),
        HeaderIcon = GlyphOf(row.Dial),
        Content = ValueTag(Loc.Get(PvFacts.NoTelemetry)),
        IsExpanded = ExpandCell(row.Id, false),
        Items =
        [
            Item(Loc.Get(PvFacts.Spotify), Loc.Get(PvFacts.SpotifySub)),
            Item(Loc.Get(PvFacts.Lyrics), Loc.Get(PvFacts.LyricsSub)),
            Item(Loc.Get(PvFacts.Github), Loc.Get(PvFacts.GithubSub)),
            FrameOf("account.privacy.whatLeavesAi", Item(Loc.Get(PvFacts.AiModels), Loc.Get(PvFacts.AiModelsSub), icon: GlyphOf(Dial("account.privacy.whatLeavesAi")))),
            Item(Loc.Get(PvFacts.CrashService), Loc.Get(PvFacts.CrashServiceSub)),
        ],
        ItemsFooter = ExpanderFooter(FooterNote(Loc.Get(PvFacts.Footer))),
    }) with { Key = "privacy.facts" };

    /// <summary>Off / Ask each time / Automatic as three left-aligned radio items (D3: one <c>RadioButtons</c> group has
    /// only a group-wide enabled flag, and Automatic alone must be disabled with its reason), then the memory-snapshot
    /// switch (live only while reporting is on) and what a report contains. The header tag is the EFFECTIVE mode — a
    /// stored Automatic on a build that cannot send behaves as, and shows as, Ask each time.</summary>
    static Element CrashReportsExpander(SettingsBodyRow row)
    {
        var stored = row.Value is int storedMode ? (Crash.Reporting)storedMode : default;
        bool configured = Crash.Uploader.Configured;
        var effective = Crash.ConsentPolicy.Effective(stored, configured);
        bool reporting = effective != Crash.Reporting.Off;
        var facts = SettingsFacts.Live;
        return SettingsExpander.Create(new SettingsExpander.Options
        {
            Header = Loc.Get(PvFacts.CrashReports),
            Description = Loc.Get(PvFacts.CrashReportsSub),
            HeaderIcon = GlyphOf(row.Dial),
            Content = ValueTag(Loc.Get(PrivacyRules.ModeKey(effective))),
            IsExpanded = ExpandCell(row.Id, false),
            Items =
            [
                ModeItemOf(Crash.Reporting.Off, effective, PvFacts.ModeOff, facts),
                ModeItemOf(Crash.Reporting.Ask, effective, PvFacts.ModeAsk, facts),
                ModeItemOf(Crash.Reporting.Auto, effective, PvFacts.ModeAuto, facts),
                FrameOf("account.privacy.crashDump", Item(Loc.Get(PvFacts.IncludeDump), Loc.Get(PvFacts.IncludeDumpSub),
                    RowToggle(Dial("account.privacy.crashDump"), reporting),
                    isEnabled: reporting, icon: GlyphOf(Dial("account.privacy.crashDump")))),
                FrameOf("account.privacy.crashContents",
                    Item(Loc.Get(PvFacts.Contents), Loc.Get(PvFacts.ContentsSub), icon: GlyphOf(Dial("account.privacy.crashContents")))),
            ],
        }) with { Key = "privacy.crash-reports" };
    }

    /// <summary>One mode with its caption and enablement from the document (Automatic needs a service, and says so).</summary>
    static Element ModeItemOf(Crash.Reporting mode, Crash.Reporting current, string labelKey, ISettingsFacts facts)
        => ModeItem(mode, current, Loc.Get(labelKey), Loc.Get(SettingsSpec.CrashModeCaptionKey((int)mode, facts)),
            SettingsSpec.CrashModeEnablement((int)mode, facts).Enabled);

    /// <summary>One mode: a radio and an indented caption, left-aligned so the card renders ONLY the content
    /// (<c>SettingsCard.ContentAlignment.Left</c>). Disabled, the caption says why.</summary>
    static Element ModeItem(Crash.Reporting mode, Crash.Reporting current, string label, string sub, bool enabled)
        => Item("", null, new BoxEl
        {
            Direction = 1, Gap = Spacing.XXS, MinWidth = 0f,
            Children =
            [
                Radio(label, isSelected: current == mode, onChange: () => SetCrashMode(mode), isEnabled: enabled),
                Caption(sub) with
                {
                    Color = enabled ? Tok.TextSecondary : Tok.TextDisabled,
                    Margin = new Edges4(ClassicSettingsRules.RadioCaptionInset, 0f, 0f, 0f), Wrap = TextWrap.Wrap, MaxLines = 2,
                },
            ],
        }, SettingsCard.ContentAlignment.Left, isEnabled: enabled);

    /// <summary>Compared against the STORED mode (not the effective one), so choosing Ask over a stored-but-unusable
    /// Automatic really writes Ask.</summary>
    static void SetCrashMode(Crash.Reporting mode)
    {
        if (Dial("account.privacy.crashReports").Writer.Choose!((int)mode)) Settings.Bump();
    }

    /// <summary>"Your data on the crash service": the shortened install id, Copy id, and the confirmed right-to-erasure
    /// (§J). Its own component: the erase runs off the UI thread and, on success, the install id has ROTATED — the epoch
    /// below re-renders the card so the new id shows.</summary>
    sealed class CrashServiceCard : SettingsComponent
    {
        public override Element Render()
        {
            UseLayout();
            var overlay = UseContext(Overlay.Service);
            _ = SettingsFacts.CrashDataEpoch.Value;   // subscribed: a rotated install id re-renders

            string fullId = SettingsFacts.CrashInstallId();   // boot ensures it; a render never writes the store
            bool configured = Crash.Uploader.Configured;

            // The confirmed erasure is the row's writer (off the UI thread; the answer bumps the epoch above).
            void OnDelete() => Controls.Confirm(overlay, Loc.Get(PvFacts.DeleteMyDataConfirmTitle), Loc.Get(PvFacts.DeleteMyDataConfirmBody),
                Loc.Get(PvFacts.DeleteMyDataConfirmButton), () => Dial("account.privacy.crashService").Writer.Run!());

            return SettingsCard.Create(new SettingsCard.Options
            {
                Header = Loc.Get(PvFacts.YourData),
                Description = PvFacts.YourDataSub(PrivacyRules.ShortInstallId(fullId)),
                HeaderIcon = GlyphOf(Dial("account.privacy.crashService")),
                Content = HStack(Spacing.S,
                    FrameOf("account.privacy.crashServiceCopy",
                        SubtleButton(Loc.Get(PvFacts.CopyId), () => Dial("account.privacy.crashServiceCopy").Writer.Run!(), isEnabled: fullId.Length > 0)),
                    StdButton(Loc.Get(PvFacts.DeleteMyData), OnDelete, isEnabled: configured)),
            });
        }
    }

    static Element PrivacyPolicyCard(SettingsBodyRow row) => CardRow(row, isClickEnabled: true,
        onClick: static () => Dial("account.privacy.policy").Writer.Run!(), actionIcon: Icons.OpenInNewWindow);

    // ══ 3. CRASH REPORTS › SAVED REPORTS ══════════════════════════════════════════════════════════════════════════════

    /// <summary>ONE expander over <see cref="Crash.Host.Bundles"/>: the send-queue InfoBar first (only while the outbox
    /// is non-empty), then a row per report with a single "…" menu (View · Copy · Send… · Delete), Open folder and Delete
    /// all in the footer. Subscribes the two events that change what it shows — the reports folder and the outbox — so the
    /// page never re-renders for them.</summary>
    sealed class SavedReportsGroup : SettingsComponent
    {
        readonly Dictionary<string, NodeHandle> _anchors = new(StringComparer.Ordinal);
        OverlayHandle? _menu;

        public override Element Render()
        {
            UseLayout();
            var overlay = UseContext(Overlay.Service);
            _ = Crash.Host.ReportsVersion.Value;       // subscribed
            _ = Crash.Uploader.OutboxVersion.Value;    // subscribed
            var bundles = Crash.Host.Bundles();        // at most Crash.Files.KeepBundles; event-driven, never per frame
            var tally = PrivacyRules.Tally(bundles, BundleBytes);
            int queued = Crash.Uploader.QueuedCount();
            bool configured = Crash.Uploader.Configured;

            var items = new List<Element>(bundles.Count + 1);
            if (queued > 0)
                items.Add(ExpanderPanel(InfoBar.Create(InfoBarSeverity.Informational, "",
                    queued == 1 ? Loc.Get(PvReports.QueuedOne) : PvReports.Queued(queued.ToString(CultureInfo.InvariantCulture)),
                    isClosable: false,
                    actionButton: HStack(Spacing.S,
                        FrameOf("account.reports.sendNow",
                            AccentButton(Loc.Get(PvReports.SendNow), static () => Dial("account.reports.sendNow").Writer.Run!(), isEnabled: configured)),
                        FrameOf("account.reports.discard",
                            StdButton(Loc.Get(PvReports.Discard), static () => Dial("account.reports.discard").Writer.Run!()))))) with { Key = "privacy.reports.queued" });
            foreach (var b in bundles)
                items.Add(ReportRow(b, overlay, configured) with { Key = "crash:" + b.Summary.ReportId });
            PruneAnchors(bundles);

            return SettingsExpander.Create(new SettingsExpander.Options
            {
                Header = Loc.Get(PvReports.Saved),
                Description = tally.Total == 0
                    ? Loc.Get(PvReports.SavedNone)
                    : PvReports.SavedSummary(tally.Sent.ToString(CultureInfo.InvariantCulture),
                        tally.NotSent.ToString(CultureInfo.InvariantCulture), tally.Closed.ToString(CultureInfo.InvariantCulture)),
                HeaderIcon = GlyphOf(Dial("account.reports.saved")),
                IsExpanded = ExpandCell("account.reports.saved", false),
                Content = ValueTag(tally.Total == 1
                    ? PvReports.SavedCountOne(PrivacyRules.Mb(tally.Bytes))
                    : PvReports.SavedCount(tally.Total.ToString(CultureInfo.InvariantCulture), PrivacyRules.Mb(tally.Bytes))),
                Items = items,
                ItemsFooter = ExpanderFooter(new BoxEl
                {
                    Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, AlignSelf = FlexAlign.Stretch,
                    Padding = new Edges4(ClassicSettingsRules.TextColumnInset, Spacing.S, Spacing.L, Spacing.S),
                    Children =
                    [
                        Caption(PvReports.Retention(Crash.Files.KeepBundles.ToString(CultureInfo.InvariantCulture)))
                            with { Color = Tok.TextTertiary, Grow = 1f, MinWidth = 0f, Wrap = TextWrap.Wrap },
                        FrameOf("account.reports.openFolder",
                            StdButton(Loc.Get(PvReports.OpenFolder), static () => Dial("account.reports.openFolder").Writer.Run!())),
                        FrameOf("account.reports.deleteAll", StdButton(Loc.Get(PvReports.DeleteAll), () => Controls.Confirm(overlay,
                            Loc.Get(PvReports.DeleteAllConfirmTitle), Loc.Get(PvReports.DeleteAllConfirmBody),
                            Loc.Get(PvReports.DeleteAll), static () => Dial("account.reports.deleteAll").Writer.Run!()), isEnabled: tally.Total > 0)),
                    ],
                }),
            });
        }

        /// <summary>A deleted bundle's "…" handle goes; every other row keeps its (still realized) anchor — a realization
        /// callback fires once per node, so clearing them all would strand the survivors' menus.</summary>
        void PruneAnchors(IReadOnlyList<Crash.BundleInfo> bundles)
        {
            if (_anchors.Count <= bundles.Count) return;
            List<string>? stale = null;
            foreach (var id in _anchors.Keys)
            {
                bool live = false;
                for (int i = 0; i < bundles.Count && !live; i++)
                    live = string.Equals(bundles[i].Summary.ReportId, id, StringComparison.Ordinal);
                if (!live) (stale ??= []).Add(id);
            }
            if (stale is null) return;
            foreach (var id in stale) _anchors.Remove(id);
        }

        /// <summary>One report: kind dot · when + short id · kind · what · send state · "…". Not clickable as a whole —
        /// every action is in the menu.</summary>
        Element ReportRow(Crash.BundleInfo b, IOverlayService? overlay, bool configured)
        {
            var s = b.Summary;
            string id = s.ReportId;
            if (s_layout.Touch) return TouchReportRow(b, overlay, configured);
            return new BoxEl
            {
                Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, AlignSelf = FlexAlign.Stretch, MinHeight = ClassicSettingsRules.ReportRowHeight,
                Padding = new Edges4(ClassicSettingsRules.TextColumnInset, Spacing.S, Spacing.L, Spacing.S),
                Children =
                [
                    new BoxEl { Width = ClassicSettingsRules.ReportDotSize, Height = ClassicSettingsRules.ReportDotSize, Corners = Radii.FullAll, Fill = DotFor(s.Kind), Shrink = 0f },
                    new BoxEl
                    {
                        Direction = 1, Gap = ClassicSettingsRules.ReportStackGap, Width = ClassicSettingsRules.ReportStampWidth, Shrink = 0f,
                        Children =
                        [
                            new TextEl(b.StampLocal.ToString("g", CultureInfo.CurrentCulture)) { Size = ClassicSettingsRules.CaptionSize, Color = Tok.TextSecondary },
                            new TextEl(PvReports.ReportId(Crash.ShortId(id))) { Size = ClassicSettingsRules.ReportIdSize, Color = Tok.TextTertiary },
                        ],
                    },
                    new TextEl(Loc.Get(PrivacyRules.KindKey(s.Kind))) { Size = ClassicSettingsRules.ReportTextSize, Weight = 600, Width = ClassicSettingsRules.ReportKindWidth, Shrink = 0f },
                    new TextEl(WhatText(s)) { Size = ClassicSettingsRules.ReportTextSize, Color = Tok.TextSecondary, Grow = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    new TextEl(StateText(b)) { Size = ClassicSettingsRules.CaptionSize, Color = Tok.TextTertiary, Width = ClassicSettingsRules.ReportStateWidth, Shrink = 0f },
                    ToolTip.Wrap(IconButton.Create(Icons.More, () => OpenRowMenu(b, overlay, configured), size: ControlSize.Small)
                        with { Shrink = 0f, OnRealized = h => _anchors[id] = h }, Loc.Get(PvReports.RowMenu)),
                ],
            };
        }

        /// <summary>The report row where the tier asks for a hit target (Compact): the fixed lanes (stamp 150, kind 56, state 110) cannot sit in a 300-wide card, so the
        /// row is the dot, ONE text column (when and id, the kind and what it was, the send state) and a 40-DIP "…" handle.</summary>
        Element TouchReportRow(Crash.BundleInfo b, IOverlayService? overlay, bool configured)
        {
            var s = b.Summary;
            string id = s.ReportId;
            return new BoxEl
            {
                Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, AlignSelf = FlexAlign.Stretch, MinHeight = ClassicSettingsRules.ReportRowHeight, MinWidth = 0f,
                Padding = new Edges4(Spacing.L, Spacing.XS, Spacing.XS, Spacing.XS),
                Children =
                [
                    new BoxEl { Width = ClassicSettingsRules.ReportDotSize, Height = ClassicSettingsRules.ReportDotSize, Corners = Radii.FullAll, Fill = DotFor(s.Kind), Shrink = 0f },
                    new BoxEl
                    {
                        Direction = 1, Gap = ClassicSettingsRules.ReportStackGap, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
                        Children =
                        [
                            new TextEl(Loc.Get(PrivacyRules.KindKey(s.Kind)) + " · " + WhatText(s))
                                { Size = ClassicSettingsRules.ReportTextSize, Weight = 600, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                            new TextEl(b.StampLocal.ToString("g", CultureInfo.CurrentCulture) + " · " + PvReports.ReportId(Crash.ShortId(id)))
                                { Size = ClassicSettingsRules.CaptionSize, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                            new TextEl(StateText(b)) { Size = ClassicSettingsRules.CaptionSize, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                        ],
                    },
                    ToolTip.Wrap(IconButton.Create(Icons.More, () => OpenRowMenu(b, overlay, configured), IconButton.DefaultStyle with { Size = s_layout.Target })
                        with { Shrink = 0f, OnRealized = h => _anchors[id] = h }, Loc.Get(PvReports.RowMenu)),
                ],
            };
        }

        /// <summary>The row's one menu, anchored at its "…" (the same anchored-flyout shape as the history menu and the
        /// video-override manager). Built at open time, so Send… reflects the live send state. A second press closes it.</summary>
        void OpenRowMenu(Crash.BundleInfo b, IOverlayService? overlay, bool configured)
        {
            if (overlay is null) return;
            if (_menu is { IsOpen: true } open) { open.Close(); return; }
            string id = b.Summary.ReportId;
            var items = new List<MenuFlyoutItem>(5)
            {
                new(Loc.Get(PvReports.View), Icons.OpenInNewWindow, true, () => SettingsVerbs.ViewCrashReport(b.ReportTxt)),
                new(Loc.Get(PvReports.Copy), Icons.Copy, true, () => SettingsVerbs.CopyCrashReport(b)),
            };
            if (PrivacyRules.CanSend(b.Summary.Kind, b.Send.State, configured))
                items.Add(new(Loc.Get(PvReports.Send), Icons.Forward, true, () => Crash.OpenSendPrompt(overlay, b)));
            items.Add(MenuFlyoutItem.Separator);
            items.Add(new(Loc.Get(PvReports.Delete), Icons.Delete, true, () => Controls.Confirm(overlay,
                Loc.Get(PvReports.DeleteConfirmTitle), Loc.Get(PvReports.DeleteConfirmBody),
                Loc.Get(PvReports.Delete), () => Crash.Host.Delete(b.Dir))));
            var handle = overlay.Open(() => _anchors.TryGetValue(id, out var a) ? a : NodeHandle.Null,
                () => MenuFlyout.Create(items, () => _menu?.Close(), size: OverlayFit.MenuSize),
                FlyoutPlacement.BottomEdgeAlignedRight,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
            _menu = handle;
            handle.ClosedAction = () => { if (ReferenceEquals(_menu, handle)) _menu = null; };
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

    /// <summary>The row's "what": the hang's seconds, why a "closed" report exists, or the exception type (the exit code
    /// when there is none) with the last route.</summary>
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
        if (key is null) return "—";
        return b.Send.State == Crash.SendState.Sent
            ? PvReports.StateSent(PrivacyRules.SentTimeLocal(b.Send.SentAtUtc, TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.UtcNow)))
            : Loc.Get(key);
    }

    // ══ 4. LOGS ═══════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The Log viewer card: opens the viewer page, and its description counts the live session. The counts and
    /// the uptime are bound into the card's description text node through the <see cref="SettingsCard.PartDescription"/>
    /// template part, fed by a 750 ms interval that writes only when the log or the minute moved — so ONE text node
    /// re-renders, the card never remounts, and nothing runs per frame (N12). The component renders once: its render
    /// reads no signal, and the signal is seeded at construction (a render never writes one).</summary>
    sealed class LogViewerCard : SettingsComponent
    {
        readonly Signal<string> _line = new(Compose());
        readonly TemplateParts _parts = new();
        long _seenVersion = -1, _seenMinute = -1;

        public LogViewerCard() => _parts.Set<TextEl>(SettingsCard.PartDescription, t => t with { Text = _line });

        public override Element Render()
        {
            UseLayout();
            UseInterval(() =>
            {
                long version = Log.Version, minute = Log.SinceStartMs / 60_000;
                if (version == _seenVersion && minute == _seenMinute) return;
                _seenVersion = version;
                _seenMinute = minute;
                _line.Value = Compose();
            }, ClassicSettingsRules.LogTickMs);

            return SettingsCard.Create(new SettingsCard.Options
            {
                Header = Loc.Get(PvLogs.Viewer),
                Description = _line.Peek(),   // non-empty, so the card builds the description node the part binds
                HeaderIcon = GlyphOf(Dial("help.logs.viewer")),
                IsClickEnabled = true,
                IsActionIconVisible = true,
                OnClick = static () => Dial("help.logs.viewer").Writer.Run!(),
                Parts = _parts,
            });
        }

        static string Compose()
        {
            Log.Tally(out int total, out int warnings, out int errors);
            return PvLogs.ViewerSub(Thousands(total), Thousands(warnings), Thousands(errors),
                Diagnostics.LogView.Uptime(TimeSpan.FromMilliseconds(Log.SinceStartMs)));
        }

        static string Thousands(int n) => n.ToString("N0", CultureInfo.InvariantCulture);
    }

    /// <summary>The two detail levels as one expander: what the viewer captures and what reaches the log file. Both are
    /// persisted settings, so the group subscribes <see cref="Platform.SettingsChanged"/> — the viewer's Verbose toggle
    /// writes the same keys and the header tag follows. A combo's items and selection freeze at mount, so each is keyed by
    /// the level VALUE it shows, and the write is posted so the click finishes before the remount.</summary>
    sealed class DetailLevelGroup : SettingsComponent
    {
        public override Element Render()
        {
            UseLayout();
            _ = Platform.SettingsChanged.Value;   // subscribed
            var viewer = Log.MinLevel;
            var file = LogCapturePolicy.EffectiveFileLevel(viewer, Log.FileMinLevel);
            int viewerIndex = Math.Clamp((int)viewer, 0, Diagnostics.LogView.LevelNames.Length - 1);
            int fileIndex = Math.Clamp((int)file, 0, Diagnostics.LogView.LevelNames.Length - 1);

            return SettingsExpander.Create(new SettingsExpander.Options
            {
                Header = Loc.Get(PvLogs.DetailLevel),
                Description = Loc.Get(PvLogs.DetailLevelSub),
                HeaderIcon = GlyphOf(Dial("help.logs.detailLevel")),
                Content = ValueTag(PvLogs.DetailLevelValue(Diagnostics.LogView.LevelNames[viewerIndex], Diagnostics.LogView.LevelNames[fileIndex])),
                IsExpanded = ExpandCell("help.logs.detailLevel", false),
                Items =
                [
                    FrameOf("help.logs.viewerLevel", Item(Loc.Get(PvLogs.InViewer), Loc.Get(PvLogs.InViewerSub), new BoxEl
                    {
                        Direction = 1, MinWidth = 0f,
                        Children =
                        [
                            Combo(Diagnostics.LogView.LevelNames, new Signal<int>(viewerIndex), width: ClassicSettingsRules.LevelComboWidth,
                                onChange: static i => Dial("help.logs.viewerLevel").Writer.Choose!(i))
                                with { Key = "privacy.level.viewer:" + viewerIndex.ToString(CultureInfo.InvariantCulture) },
                        ],
                    })),
                    FrameOf("help.logs.fileLevel", Item(Loc.Get(PvLogs.InFile), Loc.Get(PvLogs.InFileSub), new BoxEl
                    {
                        Direction = 1, MinWidth = 0f,
                        Children =
                        [
                            Combo(Diagnostics.LogView.LevelNames, new Signal<int>(fileIndex), width: ClassicSettingsRules.LevelComboWidth,
                                onChange: static i => Dial("help.logs.fileLevel").Writer.Choose!(i))
                                with { Key = "privacy.level.file:" + fileIndex.ToString(CultureInfo.InvariantCulture) },
                        ],
                    })),
                ],
            });
        }
    }

    /// <summary>The writer's rule in one sentence, fed by the writer's own numbers (never hard-coded here), and a door to
    /// the folder.</summary>
    static Element LogFilesCard(SettingsBodyRow row) => CardRow(row,
        StdButton(Loc.Get(PvLogs.OpenFolder), static () => Dial("help.logs.files").Writer.Run!()),
        sub: PvLogs.FilesSub(PrivacyRules.WholeMb(Log.MaxFileBytes), Log.RetentionDays.ToString(CultureInfo.InvariantCulture),
            PrivacyRules.WholeMb(Log.RetentionBytes)));

    // ══ 5. TOOLS ══════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The persisted key/subtitle are `dealerArchive` verbatim (CLAUDE.md: no legacy renumbering); the toggle
    /// applies live through <c>RealtimeCaptureHost.OnSettingsChanged</c>, and the two doors sit inside.</summary>
    static Element RealtimeCaptureExpander(SettingsBodyRow row) => SettingsExpander.Create(new SettingsExpander.Options
    {
        Header = Loc.Get(PvTools.Capture),
        Description = Loc.Get(PvTools.CaptureSub),
        HeaderIcon = GlyphOf(row.Dial),
        Content = RowToggle(row.Dial),
        Style = ToggleHeaderStyle(RowToggle(row.Dial)),
        IsExpanded = ExpandCell(row.Id, false),
        Items =
        [
            FrameOf("help.tools.captureViewer", Item(Loc.Get(PvTools.OpenCaptureViewer), Loc.Get(PvTools.OpenCaptureViewerSub), isClickEnabled: true,
                onClick: static () => Dial("help.tools.captureViewer").Writer.Run!())),
            FrameOf("help.tools.captureFolder", Item(Loc.Get(PvTools.OpenCaptureFolder), Loc.Get(PvTools.OpenCaptureFolderSub),
                StdButton(Loc.Get(PvTools.OpenFolder), static () => Dial("help.tools.captureFolder").Writer.Run!()))),
        ],
    }) with { Key = "privacy.capture" };

    // ══ 6. DEVELOPER ══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Developer mode and the three items it unlocks. The items are PRESENT and greyed while the switch is off
    /// (D2), never composed away. The switch and the FPS overlay go through <see cref="Platform.Developer"/> — the one
    /// persist-then-publish setter — so they apply at once instead of at the next launch; the group reads the same
    /// signals, so it follows a flip made anywhere.</summary>
    sealed class DeveloperGroup : SettingsComponent
    {
        public override Element Render()
        {
            UseLayout();
            var facts = SettingsFacts.Live;
            bool dev = facts.DeveloperMode;
            bool fps = Platform.Developer.FpsOverlay.Value;
            bool canSend = facts.CrashServiceConfigured;
            var fpsDoc = Dial("help.developer.fpsOverlay");
            var simulateDoc = Dial("help.developer.simulateUpdate");
            var testDoc = Dial("help.developer.sendTestCrashReport");

            return SettingsExpander.Create(new SettingsExpander.Options
            {
                Header = Loc.Get(PvDev.Mode),
                Description = Loc.Get(PvDev.ModeSub),
                HeaderIcon = GlyphOf(Dial("help.developer.mode")),
                Content = Toggle(new Signal<bool>(dev), onChange: static on =>
                {
                    // The developer-only dials are page-composed, so the writer asks the page to re-read.
                    if (Dial("help.developer.mode").Writer.Toggle!(on)) Settings.Bump();
                }),
                Style = ToggleHeaderStyle(Toggle(new Signal<bool>(dev))),
                IsExpanded = ExpandCell("help.developer.mode", false),
                Items =
                [
                    FrameOf("help.developer.fpsOverlay", Item(Loc.Get(PvDev.FpsOverlay), Loc.Get(PvDev.FpsOverlaySub),
                        Toggle(new Signal<bool>(fps), onChange: static on => Dial("help.developer.fpsOverlay").Writer.Toggle!(on),
                            isEnabled: fpsDoc.Enabled(facts).Enabled),
                        isEnabled: fpsDoc.Enabled(facts).Enabled, icon: GlyphOf(fpsDoc))),
                    FrameOf("help.developer.simulateUpdate", Item(Loc.Get(PvDev.SimulateUpdate), Loc.Get(PvDev.SimulateUpdateSub),
                        StdButton(Loc.Get(PvDev.SimulateUpdateButton), static () => Dial("help.developer.simulateUpdate").Writer.Run!(),
                            isEnabled: simulateDoc.Enabled(facts).Enabled),
                        isEnabled: simulateDoc.Enabled(facts).Enabled, icon: GlyphOf(simulateDoc))),
                    FrameOf("help.developer.sendTestCrashReport", Item(Loc.Get(PvDev.SendTestCrashReport), Loc.Get(testDoc.Caption(facts)!),
                        StdButton(Loc.Get(PvDev.SendTestCrashReportButton), static () => Dial("help.developer.sendTestCrashReport").Writer.Run!(),
                            isEnabled: testDoc.Enabled(facts).Enabled),
                        isEnabled: dev && canSend, icon: GlyphOf(testDoc))),
                ],
            });
        }
    }

    // ══ 7. SHARED CHROME OF THESE EXPANDERS ══════════════════════════════════════════════════════════════════════

    /// <summary>A footer under an expander's items: a hairline above, then the content (the engine puts dividers between
    /// items but none before a footer).</summary>
    static Element ExpanderFooter(Element content) => new BoxEl
    {
        Direction = 1, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
        Children = [new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault }, content],
    };

    /// <summary>A tertiary caption aligned with the items' text column (58 DIP from the left edge).</summary>
    static Element FooterNote(string text) => new BoxEl
    {
        Direction = 0, AlignSelf = FlexAlign.Stretch,
        Padding = new Edges4(ClassicSettingsRules.TextColumnInset, Spacing.M, Spacing.L, Spacing.M),
        Children = [Caption(text) with { Color = Tok.TextTertiary, Grow = 1f, MinWidth = 0f, Wrap = TextWrap.Wrap }],
    };
}
