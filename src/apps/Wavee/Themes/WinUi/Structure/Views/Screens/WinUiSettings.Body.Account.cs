// ── Themes/WinUi/Structure/Views/Screens/WinUiSettings.Body.Account.cs — Account & privacy's WinUI cards ────────────────────────────────────────────────────
//
// Spec: docs/plans/wavee/privacy-diagnostics-tab-implementation.md §3.1, §5.1, §5.3-§5.6 (the decisions are `Settings.PrivacyRules`, the verbs the dials'
// writers). Account: the plan as words. Privacy: what leaves this PC (collapsed; its answer is "No telemetry"), the crash-report mode (three radio items, the
// memory-snapshot switch, what a report holds), the crash service card (the shortened install id, Copy id, the confirmed right-to-erasure), the policy link.
// Crash reports: ONE Saved reports expander (the send-queue InfoBar, a row per report with a "…" menu, Open folder / Delete all). A group that must follow
// something the page does not bump — the reports folder, the send outbox, the crash data epoch — is its own component with its own subscriptions. Every
// expander starts COLLAPSED (the header answers); every destructive action confirms through `Controls.Confirm`.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using PrivacyRules = Wavee.Settings.PrivacyRules;
using PvFacts = Wavee.Strings.Settings.Privacy.Facts;
using PvReports = Wavee.Strings.Settings.Privacy.Reports;

namespace Wavee.Themes.WinUi;

internal static partial class WinUiSettings
{
    // ══ 1. ACCOUNT ════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The plan in words (Premium, Free), or null for none known.</summary>
    static string? PlanWords(object? value)
    {
        int tier = value is int t ? t : 0;
        return tier == 0 ? null : Loc.Get(tier == 2 ? Strings.Auth.PremiumBadge : Strings.Auth.FreeBadge);
    }

    /// <summary>The account: the signed-in name as the row's label (the label's own words while there is none) over the plan, as the old row drew it. The
    /// plan dial rides this row (`Draws`); on a results page it is its own row, the plan on its right.</summary>
    static Element IdentityCard(RowCtx x)
    {
        string name = x.Row.Value as string ?? "";
        return DialCard(BaseCard(x, null) with { Label = name.Length > 0 ? name : x.Row.Label, Description = PlanWords(Dial("account.plan").Current) });
    }

    /// <summary>The plan on its own (a search result): its words on the right of its row.</summary>
    static Element PlanCard(RowCtx x) => DialCard(BaseCard(x, PlanWords(x.Row.Value) is { } plan ? ValueTag(plan) : null));

    // ══ 2. PRIVACY ════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The collapsed answer is "No telemetry"; inside, the five places anything leaves this PC and why (facts: no control). The AI models' line is
    /// its child dial (hosted).</summary>
    static Element WhatLeavesCard(RowCtx x)
    {
        Element[] items =
        [
            PlainItem(Loc.Get(PvFacts.Spotify), Loc.Get(PvFacts.SpotifySub)),
            PlainItem(Loc.Get(PvFacts.Lyrics), Loc.Get(PvFacts.LyricsSub)),
            PlainItem(Loc.Get(PvFacts.Github), Loc.Get(PvFacts.GithubSub)),
            HostedItem(x, "account.privacy.whatLeavesAi", PlainItem(Loc.Get(PvFacts.AiModels), Loc.Get(PvFacts.AiModelsSub))),
            PlainItem(Loc.Get(PvFacts.CrashService), Loc.Get(PvFacts.CrashServiceSub)),
        ];
        var header = BaseCard(x, ValueTag(Loc.Get(PvFacts.NoTelemetry))) with { Label = Loc.Get(PvFacts.WhatLeaves), Description = Loc.Get(PvFacts.WhatLeavesSub) };
        return Expander(header, items, x.Expanded, itemsFooter: ExpanderFooter(FooterNote(Loc.Get(PvFacts.Footer)))) with { Key = "privacy.facts" };
    }

    /// <summary>Off / Ask each time / Automatic as three left-aligned radio items (D3: one RadioButtons group has only a group-wide enabled flag, and Automatic
    /// alone must be disabled with its reason), then the memory-snapshot switch (live only while reporting is on) and what a report contains (the two child
    /// dials, hosted). The header tag is the EFFECTIVE mode — a stored Automatic on a build that cannot send behaves as, and shows as, Ask each time.</summary>
    static Element CrashReportsCard(RowCtx x)
    {
        var dial = x.Row.Dial;
        var stored = x.Row.Value is int storedMode ? (Crash.Reporting)storedMode : default;
        bool configured = Crash.Uploader.Configured;
        var effective = Crash.ConsentPolicy.Effective(stored, configured);
        bool reporting = effective != Crash.Reporting.Off;
        var facts = SettingsFacts.Live;
        var items = new List<Element>(5)
        {
            ModeItemOf(dial, Crash.Reporting.Off, effective, PvFacts.ModeOff, facts),
            ModeItemOf(dial, Crash.Reporting.Ask, effective, PvFacts.ModeAsk, facts),
            ModeItemOf(dial, Crash.Reporting.Auto, effective, PvFacts.ModeAuto, facts),
        };
        if (Child(x, "account.privacy.crashDump") is { } dump)
            items.Add(ChildHost(x, dump, PlainItem(Loc.Get(PvFacts.IncludeDump), Loc.Get(PvFacts.IncludeDumpSub), DialToggle(dump.Dial, reporting), isEnabled: reporting)));
        if (Child(x, "account.privacy.crashContents") is { } contents)
            items.Add(ChildHost(x, contents, PlainItem(Loc.Get(PvFacts.Contents), Loc.Get(PvFacts.ContentsSub))));
        var header = BaseCard(x, ValueTag(Loc.Get(PrivacyRules.ModeKey(effective)))) with { Label = Loc.Get(PvFacts.CrashReports), Description = Loc.Get(PvFacts.CrashReportsSub) };
        return Expander(header, items, x.Expanded) with { Key = "privacy.crash-reports" };
    }

    /// <summary>One mode with its caption and enablement (Automatic needs a service, and says so): a radio and an indented caption, left-aligned. Written against
    /// the STORED mode, so choosing Ask over a stored-but-unusable Automatic really writes Ask.</summary>
    static Element ModeItemOf(SettingsDial dial, Crash.Reporting mode, Crash.Reporting current, string labelKey, ISettingsFacts facts)
    {
        bool enabled = SettingsSpec.CrashModeEnablement((int)mode, facts).Enabled;
        string sub = Loc.Get(SettingsSpec.CrashModeCaptionKey((int)mode, facts));
        return PlainItem("", null, new BoxEl
        {
            Direction = 1, Gap = Spacing.XXS, MinWidth = 0f,
            Children =
            [
                Radio(Loc.Get(labelKey), isSelected: current == mode, onChange: () =>
                {
                    if (dial.Writer.Choose is { } choose && choose((int)mode)) Settings.Bump();
                }, isEnabled: enabled),
                Caption(sub) with
                {
                    Color = enabled ? Tok.TextSecondary : Tok.TextDisabled,
                    Margin = new Edges4(WinUiSettingsRules.RadioCaptionInset, 0f, 0f, 0f), Wrap = TextWrap.Wrap, MaxLines = 2,
                },
            ],
        }, CardForm.Left, isEnabled: enabled);
    }

    /// <summary>"Your data on the crash service" (the crash service dial and its Copy child).</summary>
    static Element CrashServiceCardOf(RowCtx x) => Embed.Comp(new CrashServiceProps(x), static () => new CrashServiceCard()) with { Key = "privacy.crash-service" };

    sealed record CrashServiceProps(RowCtx X);

    /// <summary>The shortened install id, Copy id, and the confirmed right-to-erasure (§J). Its own component: the erase runs off the UI thread and, on success,
    /// the install id has ROTATED — the epoch re-renders the card so the new id shows.</summary>
    sealed class CrashServiceCard : SettingsComponent
    {
        public override Element Render()
        {
            UseLayout();
            var x = UseProps<CrashServiceProps>().X;
            var overlay = UseContext(Overlay.Service);
            _ = SettingsFacts.CrashDataEpoch.Value;   // subscribed: a rotated install id re-renders

            string fullId = SettingsFacts.CrashInstallId();   // boot ensures it; a render never writes the store
            bool configured = Crash.Uploader.Configured;
            var erase = x.Row.Dial;
            void OnDelete() => Controls.Confirm(overlay, Loc.Get(PvFacts.DeleteMyDataConfirmTitle), Loc.Get(PvFacts.DeleteMyDataConfirmBody),
                Loc.Get(PvFacts.DeleteMyDataConfirmButton), () => Press(erase));

            var copy = Child(x, "account.privacy.crashServiceCopy");
            Element copyButton = SubtleButton(Loc.Get(PvFacts.CopyId), () => { if (copy is not null) Press(copy.Dial); }, isEnabled: fullId.Length > 0);
            var card = BaseCard(x, HStack(Spacing.S, copy is null ? copyButton : ChildHost(x, copy, copyButton), StdButton(Loc.Get(PvFacts.DeleteMyData), OnDelete, isEnabled: configured)))
                with { Label = Loc.Get(PvFacts.YourData), Description = PvFacts.YourDataSub(PrivacyRules.ShortInstallId(fullId)), Enabled = true };
            return DialCard(card);
        }
    }

    // ══ 3. CRASH REPORTS › SAVED REPORTS ══════════════════════════════════════════════════════════════════════════

    /// <summary>The saved reports' expander (the composite dial; its verbs are its children's writers).</summary>
    static Element SavedReportsCard(RowCtx x) => Embed.Comp(new SavedReportsProps(x), static () => new SavedReportsGroup()) with { Key = "privacy.reports" };

    sealed record SavedReportsProps(RowCtx X);

    /// <summary>ONE expander over <see cref="Crash.Host.Bundles"/>: the send-queue InfoBar first (only while the outbox is non-empty), then a row per report with a
    /// single "…" menu (View · Copy · Send… · Delete), Open folder and Delete all in the footer. Subscribes the two events that change what it shows — the
    /// reports folder and the outbox — so the page never re-renders for them.</summary>
    sealed class SavedReportsGroup : SettingsComponent
    {
        readonly Dictionary<string, NodeHandle> _anchors = new(StringComparer.Ordinal);
        OverlayHandle? _menu;

        public override Element Render()
        {
            UseLayout();
            var x = UseProps<SavedReportsProps>().X;
            var overlay = UseContext(Overlay.Service);
            _ = Crash.Host.ReportsVersion.Value;       // subscribed
            _ = Crash.Uploader.OutboxVersion.Value;    // subscribed
            var bundles = Crash.Host.Bundles();        // at most Crash.Files.KeepBundles; event-driven, never per frame
            var tally = PrivacyRules.Tally(bundles, BundleBytes);
            int queued = Crash.Uploader.QueuedCount();
            bool configured = Crash.Uploader.Configured;
            var sendNow = Dial("account.reports.sendNow");
            var discard = Dial("account.reports.discard");
            var openFolder = Dial("account.reports.openFolder");
            var deleteAll = Dial("account.reports.deleteAll");

            var items = new List<Element>(bundles.Count + 1);
            if (queued > 0)
                items.Add(ExpanderPanel(InfoBar.Create(InfoBarSeverity.Informational, "",
                    queued == 1 ? Loc.Get(PvReports.QueuedOne) : PvReports.Queued(queued.ToString(CultureInfo.InvariantCulture)),
                    isClosable: false,
                    actionButton: HStack(Spacing.S,
                        HostedItem(x, "account.reports.sendNow", AccentButton(Loc.Get(PvReports.SendNow), () => Press(sendNow), isEnabled: configured)),
                        HostedItem(x, "account.reports.discard", StdButton(Loc.Get(PvReports.Discard), () => Press(discard)))))) with { Key = "privacy.reports.queued" });
            foreach (var b in bundles)
                items.Add(ReportRow(b, overlay, configured) with { Key = "crash:" + b.Summary.ReportId });
            PruneAnchors(bundles);

            var header = BaseCard(x, ValueTag(tally.Total == 1
                    ? PvReports.SavedCountOne(PrivacyRules.Mb(tally.Bytes))
                    : PvReports.SavedCount(tally.Total.ToString(CultureInfo.InvariantCulture), PrivacyRules.Mb(tally.Bytes))))
                with
                {
                    Label = Loc.Get(PvReports.Saved),
                    Description = tally.Total == 0
                        ? Loc.Get(PvReports.SavedNone)
                        : PvReports.SavedSummary(tally.Sent.ToString(CultureInfo.InvariantCulture),
                            tally.NotSent.ToString(CultureInfo.InvariantCulture), tally.Closed.ToString(CultureInfo.InvariantCulture)),
                };
            return Expander(header, items, x.Expanded, itemsFooter: ExpanderFooter(new BoxEl
            {
                Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, AlignSelf = FlexAlign.Stretch,
                Padding = new Edges4(WinUiSettingsRules.TextColumnInset, Spacing.S, Spacing.L, Spacing.S),
                Children =
                [
                    Caption(PvReports.Retention(Crash.Files.KeepBundles.ToString(CultureInfo.InvariantCulture)))
                        with { Color = Tok.TextTertiary, Grow = 1f, MinWidth = 0f, Wrap = TextWrap.Wrap },
                    HostedItem(x, "account.reports.openFolder", StdButton(Loc.Get(PvReports.OpenFolder), () => Press(openFolder))),
                    HostedItem(x, "account.reports.deleteAll", StdButton(Loc.Get(PvReports.DeleteAll), () => Controls.Confirm(overlay,
                        Loc.Get(PvReports.DeleteAllConfirmTitle), Loc.Get(PvReports.DeleteAllConfirmBody),
                        Loc.Get(PvReports.DeleteAll), () => Press(deleteAll)), isEnabled: tally.Total > 0)),
                ],
            }));
        }

        /// <summary>A deleted bundle's "…" handle goes; every other row keeps its (still realized) anchor — a realization callback fires once per node, so
        /// clearing them all would strand the survivors' menus.</summary>
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

        /// <summary>One report: kind dot · when + short id · kind · what · send state · "…". Not clickable as a whole — every action is in the menu.</summary>
        Element ReportRow(Crash.BundleInfo b, IOverlayService? overlay, bool configured)
        {
            var s = b.Summary;
            string id = s.ReportId;
            if (s_layout.Touch) return TouchReportRow(b, overlay, configured);
            return new BoxEl
            {
                Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, AlignSelf = FlexAlign.Stretch, MinHeight = WinUiSettingsRules.ReportRowHeight,
                Padding = new Edges4(WinUiSettingsRules.TextColumnInset, Spacing.S, Spacing.L, Spacing.S),
                Children =
                [
                    new BoxEl { Width = WinUiSettingsRules.ReportDotSize, Height = WinUiSettingsRules.ReportDotSize, Corners = Radii.FullAll, Fill = DotFor(s.Kind), Shrink = 0f },
                    new BoxEl
                    {
                        Direction = 1, Gap = WinUiSettingsRules.ReportStackGap, Width = WinUiSettingsRules.ReportStampWidth, Shrink = 0f,
                        Children =
                        [
                            new TextEl(b.StampLocal.ToString("g", CultureInfo.CurrentCulture)) { Size = WinUiSettingsRules.CaptionSize, Color = Tok.TextSecondary },
                            new TextEl(PvReports.ReportId(Crash.ShortId(id))) { Size = WinUiSettingsRules.ReportIdSize, Color = Tok.TextTertiary },
                        ],
                    },
                    new TextEl(Loc.Get(PrivacyRules.KindKey(s.Kind))) { Size = WinUiSettingsRules.ReportTextSize, Weight = 600, Width = WinUiSettingsRules.ReportKindWidth, Shrink = 0f },
                    new TextEl(WhatText(s)) { Size = WinUiSettingsRules.ReportTextSize, Color = Tok.TextSecondary, Grow = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    new TextEl(StateText(b)) { Size = WinUiSettingsRules.CaptionSize, Color = Tok.TextTertiary, Width = WinUiSettingsRules.ReportStateWidth, Shrink = 0f },
                    ToolTip.Wrap(IconButton.Create(Icons.More, () => OpenRowMenu(b, overlay, configured), size: ControlSize.Small)
                        with { Shrink = 0f, OnRealized = h => _anchors[id] = h }, Loc.Get(PvReports.RowMenu)),
                ],
            };
        }

        /// <summary>The report row where the tier asks for a hit target (Compact): the dot, ONE text column (when and id, the kind and what it was, the send
        /// state) and a 40-DIP "…" handle.</summary>
        Element TouchReportRow(Crash.BundleInfo b, IOverlayService? overlay, bool configured)
        {
            var s = b.Summary;
            string id = s.ReportId;
            return new BoxEl
            {
                Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, AlignSelf = FlexAlign.Stretch, MinHeight = WinUiSettingsRules.ReportRowHeight, MinWidth = 0f,
                Padding = new Edges4(Spacing.L, Spacing.XS, Spacing.XS, Spacing.XS),
                Children =
                [
                    new BoxEl { Width = WinUiSettingsRules.ReportDotSize, Height = WinUiSettingsRules.ReportDotSize, Corners = Radii.FullAll, Fill = DotFor(s.Kind), Shrink = 0f },
                    new BoxEl
                    {
                        Direction = 1, Gap = WinUiSettingsRules.ReportStackGap, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
                        Children =
                        [
                            new TextEl(Loc.Get(PrivacyRules.KindKey(s.Kind)) + " · " + WhatText(s))
                                { Size = WinUiSettingsRules.ReportTextSize, Weight = 600, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                            new TextEl(b.StampLocal.ToString("g", CultureInfo.CurrentCulture) + " · " + PvReports.ReportId(Crash.ShortId(id)))
                                { Size = WinUiSettingsRules.CaptionSize, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                            new TextEl(StateText(b)) { Size = WinUiSettingsRules.CaptionSize, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                        ],
                    },
                    ToolTip.Wrap(IconButton.Create(Icons.More, () => OpenRowMenu(b, overlay, configured), IconButton.DefaultStyle with { Size = s_layout.Target })
                        with { Shrink = 0f, OnRealized = h => _anchors[id] = h }, Loc.Get(PvReports.RowMenu)),
                ],
            };
        }

        /// <summary>The row's one menu, anchored at its "…". Built at open time, so Send… reflects the live send state. A second press closes it.</summary>
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
        if (key is null) return "—";
        return b.Send.State == Crash.SendState.Sent
            ? PvReports.StateSent(PrivacyRules.SentTimeLocal(b.Send.SentAtUtc, TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.UtcNow)))
            : Loc.Get(key);
    }
}
