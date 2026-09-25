// ── Screens/Crash.UI.cs ────────────────────────────────────────────────────────────────────────────────────────────
// The in-app crash & diagnostics UI (WP-E): the zero-size overlay chrome that decides prompt / silent-upload / toast
// for this launch (and arms the hang toast + the --crash-probe timer via Crash.Host.ArmProbe), the crash/hang consent
// dialog body, Settings › Privacy & diagnostics' rows (incl. the §J right-to-erasure row) and the Logs › Reports list.
// Everything here reads Crash.Host.* / Crash.Uploader.* / Crash.Scrubber.* (WP-A/B/C) and never writes a bundle
// itself — a report is only ever read, scrubbed for preview, enqueued or deleted from here.
//
// Role: UI
// Owner: WP-E
// Spec: docs/plans/wavee/crash-diagnostics-implementation.md §D, §F "E · In-app UI", §I "WP-E", §J (erasure, added
//       2026-09-24). Copy is verbatim from the approved prototype artboards (Prompt.dc.html, Toast.dc.html,
//       Settings.dc.html, Reports.dc.html).
//
// MOUNT ORDER (mirrors Feedback.UI.cs's old crash arm, ch 28 §9.3.8): the overlay layer mounts Setup.WizardChrome
// FIRST, Feedback.Chrome SECOND, Crash.Chrome THIRD, ReleaseNotes.AfterUpdateChrome FOURTH — Crash.Chrome sets
// ReleaseNotes.CrashNoticeThisLaunch before the after-update plate's effect reads it, same load-bearing reason as
// before: a launch that surfaced a crash defers the plate to the next launch.

using System.Globalization;
using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.WindowsApi.Network;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public static partial class Crash
{
    // ══ 1. THE CHROME (mounted in the shell overlay layer, right after Feedback.Chrome) ═══════════════════════════

    /// <summary>Zero-size chrome, mounted THIRD in the overlay layer (see the header).</summary>
    // MOUNT POINT (owner E contract)
    public static Element Chrome() => Embed.Comp(static () => new ChromeCore());

    sealed class ChromeCore : Component
    {
        // A static baseline, not UseRef(-1) — see Feedback.UI.cs's ChromeCore for why (ch 28 §9.1 #10): a UseRef
        // treats "whatever value is current when I first mount" as already served and swallows the first hang report
        // after an OverlayHost subtree remount.
        static int s_lastHangSeen = -1;

        public override Element Render()
        {
            var overlay = UseContext(Overlay.Service);
            var post = UsePost();

            // ── effect A: this launch's bundle (prompt / silent upload / toast), deferred behind the setup wizard ──
            int wizardEpoch = Setup.WizardMarkerEpoch.Value;   // re-evaluate the deferral on every wizard marker bump
            UseEffect(() =>
            {
                if (Host.ThisLaunch is not { } bundle) return;
                if (Setup.Gating.IsPending(Platform.Settings) || Setup.WizardOpen.Peek()) return;   // deferred — still armed
                Host.ThisLaunch = null;                     // one-shot: consumed now, whichever arm fires below
                ReleaseNotes.CrashNoticeThisLaunch = true;   // the after-update plate waits for the next launch

                var mode = (Reporting)Platform.Settings.Get(Platform.Keys.CrashReporting);
                bool online = NetworkStatus.IsOnline;
                switch (ConsentPolicy.Decide(mode, bundle.Summary.Kind, online))
                {
                    case ConsentPolicy.Action.UploadSilently:
                        bool dumpAllowed = ConsentPolicy.DumpAllowed(mode, bundle.Summary.Kind,
                            Platform.Settings.Get(Platform.Keys.CrashIncludeDump), manualSend: false);
                        Uploader.Enqueue(bundle, dumpAllowed);
                        Notify.Say(Strings.Crash.SentToastDetail(ShortId(bundle.Summary.ReportId)), InfoBarSeverity.Success,
                            title: Loc.Get(Strings.Crash.SentToast), dedupeKey: "crash-sent:" + bundle.Summary.ReportId);
                        break;
                    case ConsentPolicy.Action.Toast:
                        Notify.Say(Loc.Get(Strings.Crash.QueuedToast), InfoBarSeverity.Warning,
                            Loc.Get(Strings.Crash.View), () => OpenPrompt(overlay, bundle),
                            "crash-toast:" + bundle.Summary.ReportId, durationMs: 0f);
                        break;
                    case ConsentPolicy.Action.Prompt:
                        OpenPrompt(overlay, bundle);
                        break;
                }
            }, wizardEpoch);

            // ── effect B: a hang the child reported for THIS process while it kept running ──
            int hangVersion = Host.HangReported.Value;
            UseEffect(() =>
            {
                if (hangVersion <= 0 || hangVersion == s_lastHangSeen) return;
                s_lastHangSeen = hangVersion;
                BundleInfo? hang = null;
                foreach (var b in Host.Bundles())
                    if (b.Summary.Kind == Kind.Hang) { hang = b; break; }   // newest first — the one just written
                string detail = hang?.Summary.ExceptionMessage is { Length: > 0 } m ? m : Loc.Get(Strings.Crash.KindHang);
                string dedupe = "crash-hang:" + hangVersion.ToString(CultureInfo.InvariantCulture);
                if (hang is { } hangBundle)
                    Notify.Say(Strings.Crash.HangToast(detail), InfoBarSeverity.Warning,
                        Loc.Get(Strings.Crash.View), () => OpenPrompt(overlay, hangBundle), dedupe);
                else
                    Notify.Say(Strings.Crash.HangToast(detail), InfoBarSeverity.Warning, dedupeKey: dedupe);
            }, hangVersion);

            // ── effect C: --crash-probe, moved from Feedback.UI.cs's ChromeCore verbatim (§B.0/§G) ──
            UseEffect(() =>
            {
                if (Host.ProbeMode() is not { Length: > 0 } mode) return;
                Host.ArmProbe(mode, post);
            }, DepKey.Empty);

            return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false, Shrink = 0f };
        }
    }

    static void OpenPrompt(IOverlayService? overlay, BundleInfo bundle)
    {
        if (overlay is null) return;
        var session = new PromptSession();
        var handle = ContentDialog.Show(overlay, d =>
        {
            d.Title = Loc.Get(bundle.Summary.Kind == Kind.Hang ? Strings.Crash.PromptHangTitle : Strings.Crash.PromptTitle);
            d.DialogWidth = DialogChrome;
            d.Content = Embed.Comp(() => new CrashPromptBody { Bundle = bundle, Session = session })
                with { Key = "crash-prompt:" + bundle.Summary.ReportId };
            d.PrimaryText = Loc.Get(Strings.Crash.Send);
            d.CloseText = Loc.Get(Strings.Crash.NotNow);
            d.DefaultButton = ContentDialog.DefaultBtn.Primary;
            d.IsPrimaryButtonEnabled = Uploader.Configured;
            // VETOABLE: the body has not finished composing the scrubbed preview yet.
            d.PrimaryButtonClick = args => { if (!(session.Submit?.Invoke() ?? false)) args.Cancel = true; };
        });
        session.Handle = handle;
    }

    static string ShortId(string reportId) => reportId.Length >= 8 ? reportId[..4] + "-" + reportId[4..8] : reportId;

    static string ShortInstallId(string id) => id.Length > 10 ? id[..4] + "…" + id[^4..] : id;

    static string FormatMb(long bytes) => (bytes / 1_048_576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB";

    // ══ 2. THE PROMPT BODY ═════════════════════════════════════════════════════════════════════════════════════════

    const float DialogChrome = 548f, BodyWidth = 500f, PreviewHeight = 220f;

    /// <summary>The one handshake between <see cref="OpenPrompt"/>'s dialog chrome and the body: the card installs
    /// its submit + carries the handle it can close itself from (the "Report on GitHub instead" link).</summary>
    sealed class PromptSession
    {
        public Func<bool>? Submit;
        public OverlayHandle? Handle;
    }

    /// <summary>Width 500; every signal seeded once from <see cref="Bundle"/> at mount (the prop freezes — a second
    /// bundle for the same launch never happens, but a re-render must not re-seed the user's in-progress choices).</summary>
    sealed class CrashPromptBody : Component
    {
        public required BundleInfo Bundle;
        public required PromptSession Session;

        public override Element Render()
        {
            var showReport = UseSignal(false);
            var includeDump = UseSignal(Bundle.HasDump && Platform.Settings.Get(Platform.Keys.CrashIncludeDump));
            var always = UseSignal(false);
            var preview = UseSignal<string?>(null);
            var post = UsePost();

            // Off-thread compose: the scrub touches file I/O + the redaction pass, so it never runs on the UI
            // thread (ReportComposer's pattern, Feedback.UI.cs). Cancelled on unmount.
            UseEffect(() =>
            {
                var cts = new CancellationTokenSource();
                var rules = Scrubber.RulesNow();   // UI thread: reads the live account/device seams, never blocks
                var bundle = Bundle;
                Task.Run(() =>
                {
                    string reportTxt = "";
                    try { reportTxt = File.ReadAllText(bundle.ReportTxt); } catch (Exception ex) { Log.Warn("crash", "prompt.read.report.failed", ex); }
                    string[] tail = Array.Empty<string>();
                    try { tail = File.ReadAllLines(bundle.TailTxt); } catch (Exception ex) { Log.Warn("crash", "prompt.read.tail.failed", ex); }
                    string text;
                    try { text = Scrubber.Preview(Scrubber.Scrub(bundle.Summary, reportTxt, tail, rules)); }
                    catch (Exception ex) { Log.Warn("crash", "prompt.scrub.failed", ex); text = ""; }
                    if (!cts.IsCancellationRequested) post(() => { if (!cts.IsCancellationRequested) preview.Value = text; });
                });
                return (Action?)(() => cts.Cancel());
            }, DepKey.Empty);

            UseEffect(() =>
            {
                Session.Submit = () =>
                {
                    if (preview.Peek() is null)
                    {
                        Notify.Say(Loc.Get(Strings.Report.Preparing), InfoBarSeverity.Informational);
                        return false;
                    }
                    if (always.Peek()) Platform.Settings.Set(Platform.Keys.CrashReporting, (int)Reporting.Auto);
                    Platform.Settings.Set(Platform.Keys.CrashConsentAsked, true);
                    Uploader.Enqueue(Bundle, includeDump.Peek() && Bundle.HasDump);
                    Notify.Say(Strings.Crash.SentToastDetail(ShortId(Bundle.Summary.ReportId)), InfoBarSeverity.Success,
                        title: Loc.Get(Strings.Crash.SentToast), dedupeKey: "crash-sent:" + Bundle.Summary.ReportId);
                    return true;
                };
                return (Action?)(() => Session.Submit = null);
            }, DepKey.Empty);

            var children = new List<Element>(10) { HeaderInfoBar(Bundle), Lead(Bundle.Summary.Kind) };
            children.Add(WhatIsSentBox(showReport, preview.Value));
            if (Bundle.HasDump) children.Add(IncludeDumpRow(includeDump, Bundle.DumpBytes));
            children.Add(CheckBox.Create(Loc.Get(Strings.Crash.PromptAlways), always));
            children.Add(new BoxEl
            {
                Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Width = BodyWidth,
                Children =
                [
                    HyperlinkButton.Create(Loc.Get(Strings.Crash.PromptPrivacy), PrivacyUrl),
                    new TextEl("·") { Color = Tok.TextTertiary },
                    HyperlinkButton.Create(Loc.Get(Strings.Crash.PromptGithub), () =>
                    {
                        Feedback.OpenCrashReport(Bundle.ReportTxt);
                        Session.Handle?.Close();
                    }),
                ],
            });
            if (!Uploader.Configured)
                children.Add(new TextEl(Loc.Get(Strings.Crash.PromptNotConfigured)) { Size = 12f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, MaxWidth = BodyWidth });

            return new BoxEl { Direction = 1, Gap = Spacing.M, Width = BodyWidth, Children = children.ToArray() };
        }

        static Element HeaderInfoBar(BundleInfo bundle)
        {
            var s = bundle.Summary;
            string when = Strings.Crash.PromptWhen(bundle.StampLocal.ToString("t", CultureInfo.CurrentCulture));
            string message = s.ExceptionType.Length > 0
                ? when + " · " + s.ExceptionType + (s.ExceptionMessage.Length > 0 ? ": " + s.ExceptionMessage : "")
                : when;
            return InfoBar.Create(InfoBarSeverity.Warning, "", message, isClosable: false, availableWidth: BodyWidth);
        }

        static Element Lead(Kind kind) => new TextEl(Loc.Get(kind == Kind.Hang ? Strings.Crash.PromptHangLead : Strings.Crash.PromptLead))
        {
            Size = 14f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxWidth = BodyWidth,
        };

        static Element WhatIsSentBox(Signal<bool> showReport, string? preview)
        {
            var kids = new List<Element>(8)
            {
                new TextEl(Loc.Get(Strings.Crash.PromptWhatIsSent)) { Size = 12f, Weight = 600, Color = Tok.TextSecondary },
                Bullet(Loc.Get(Strings.Crash.PromptBullet1), positive: true),
                Bullet(Loc.Get(Strings.Crash.PromptBullet2), positive: true),
                Bullet(Loc.Get(Strings.Crash.PromptBullet3), positive: true),
                Bullet(Loc.Get(Strings.Crash.PromptBullet4), positive: false),
                HyperlinkButton.Create(Loc.Get(showReport.Value ? Strings.Crash.PromptHideFull : Strings.Crash.PromptReadFull),
                    () => showReport.Value = !showReport.Peek()),
            };
            if (showReport.Value)
                kids.Add(new BoxEl
                {
                    Width = BodyWidth - 32f, Height = PreviewHeight, Shrink = 0f, ClipToBounds = true, Fill = Tok.FillSolidBase,
                    BorderWidth = 1f, BorderColor = Tok.StrokeDividerDefault, Corners = Radii.ControlAll,
                    Children =
                    [
                        new ScrollEl
                        {
                            Height = PreviewHeight, ScrollKey = "crash-prompt-preview", EdgeCues = ScrollEdgeCues.None,
                            Content = new BoxEl
                            {
                                Padding = Edges4.All(10f),
                                Children = [Design.Type.MicroMeta(preview ?? Loc.Get(Strings.Report.Preparing))
                                    with { FontFamily = "Cascadia Code", Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxWidth = BodyWidth - 52f }],
                            },
                        },
                    ],
                });
            return new BoxEl
            {
                Direction = 1, Gap = Spacing.S, Width = BodyWidth, Shrink = 0f, Padding = Edges4.All(Spacing.M),
                Fill = Tok.FillSolidSecondary, BorderWidth = 1f, BorderColor = Tok.StrokeDividerDefault, Corners = Radii.CardAll,
                Children = kids.ToArray(),
            };
        }

        static Element Bullet(string text, bool positive) => new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Start, Width = BodyWidth - 32f,
            Children =
            [
                Icon(positive ? Icons.Check : Icons.Cancel, 14f, positive ? Tok.SystemFillSuccess : Tok.SystemFillCritical)
                    with { Margin = new Edges4(2f, 0f, 0f, 0f) },
                new TextEl(text) { Size = 13f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxWidth = BodyWidth - 56f },
            ],
        };

        static Element IncludeDumpRow(Signal<bool> includeDump, long dumpBytes) => new BoxEl
        {
            Direction = 1, Gap = 2f, Width = BodyWidth,
            Children =
            [
                CheckBox.Create(Strings.Crash.PromptIncludeDump(FormatMb(dumpBytes)), includeDump),
                new TextEl(Loc.Get(Strings.Crash.PromptIncludeDumpSub))
                    { Size = 12f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, MaxWidth = BodyWidth - 28f, Margin = new Edges4(28f, 0f, 0f, 0f) },
            ],
        };
    }

    const string PrivacyUrl = "https://github.com/christosk92/WaveeMusic/blob/main/PRIVACY.md";

    // ══ 3. SETTINGS › GENERAL › PRIVACY & DIAGNOSTICS ═════════════════════════════════════════════════════════════

    const float RowGap = 4f;

    /// <summary>The five rows of §D's wireframe plus §J's erasure row, mounted by <c>Settings.UI.cs</c>'s
    /// <c>GeneralTab</c> right after its own section header (the header's glyph/title live in
    /// <c>Settings.Catalog</c>, a table this component does not touch). Every row is its own house
    /// <see cref="SettingsCard"/>, matching the shape <c>Settings.UI.cs</c>'s private <c>Row</c> helper builds — that
    /// helper is private to <c>Settings</c>, so this reimplements its shape rather than reaching across the class
    /// boundary (WP-E owns this file only).</summary>
    public sealed class PrivacyRows : Component
    {
        public override Element Render()
        {
            var overlay = UseContext(Overlay.Service);
            var hooks = UseContext(InputHooks.Current);
            var post = UsePost();

            var mode = UseSignal(Platform.Settings.Get(Platform.Keys.CrashReporting));
            var dump = UseSignal(Platform.Settings.Get(Platform.Keys.CrashIncludeDump));
            var eraseEpoch = UseSignal(0);
            _ = eraseEpoch.Value;   // subscribed so a rotated install id (after a successful erase) re-renders

            _ = Uploader.OutboxVersion.Value;   // subscribed
            _ = Host.ReportsVersion.Value;      // subscribed
            int queuedCount = Uploader.QueuedCount();
            var bundles = Host.Bundles();
            long totalBytes = 0;
            foreach (var b in bundles) totalBytes += TotalBytes(b);

            return new BoxEl
            {
                Direction = 1, Gap = RowGap, AlignSelf = FlexAlign.Stretch,
                Children =
                [
                    ModeRow(mode),
                    DumpRow(dump, mode.Value),
                    QueueRow(queuedCount),
                    SavedRow(bundles.Count, totalBytes, overlay),
                    PrivacyRow(),
                    EraseRow(overlay, hooks, post, eraseEpoch),
                ],
            };
        }

        static long TotalBytes(BundleInfo b)
        {
            long n = 0;
            try
            {
                n += new FileInfo(b.ReportTxt).Length;
                if (File.Exists(b.TailTxt)) n += new FileInfo(b.TailTxt).Length;
                if (b.DumpPath is { } dp && File.Exists(dp)) n += b.DumpBytes;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn("crash", "privacyRows.size.failed", ex); }
            return n;
        }

        static Element Row(string label, string? sub, Element control, string icon, bool isEnabled = true) => SettingsCard.Create(new SettingsCard.Options
        {
            Header = label, Description = sub, HeaderIcon = icon, Content = control, IsEnabled = isEnabled,
        });

        static Element ModeRow(Signal<int> mode) => Row(Loc.Get(Strings.Crash.Mode), Loc.Get(Strings.Crash.ModeSub),
            ComboBox.Create([Loc.Get(Strings.Crash.ModeOff), Loc.Get(Strings.Crash.ModeAsk), Loc.Get(Strings.Crash.ModeAuto)],
                mode, width: 200f, onChange: i => Platform.Settings.Set(Platform.Keys.CrashReporting, i)),
            Icons.StatusWarning);

        static Element DumpRow(Signal<bool> dump, int mode) => Row(Loc.Get(Strings.Crash.IncludeDump), Loc.Get(Strings.Crash.IncludeDumpSub),
            ToggleSwitch.Create(dump, onChange: v => Platform.Settings.Set(Platform.Keys.CrashIncludeDump, v),
                isEnabled: mode != (int)Reporting.Off),
            Icons.Camera, isEnabled: mode != (int)Reporting.Off);

        static Element QueueRow(int queued) => Row(Loc.Get(Strings.Crash.Queued), Strings.Crash.QueuedCount(queued.ToString(CultureInfo.InvariantCulture)),
            HStack(Spacing.S,
                Button.Standard(Loc.Get(Strings.Crash.SendNow), static () => Uploader.Drain(), isEnabled: queued > 0),
                Button.Standard(Loc.Get(Strings.Crash.Discard), static () => Uploader.DiscardQueue(), isEnabled: queued > 0)),
            Icons.Forward);

        static Element SavedRow(int count, long totalBytes, IOverlayService? overlay) => Row(Loc.Get(Strings.Crash.Saved),
            Strings.Crash.SavedCount(count.ToString(CultureInfo.InvariantCulture), (totalBytes / 1_048_576.0).ToString("0", CultureInfo.InvariantCulture)),
            HStack(Spacing.S,
                Button.Standard(Loc.Get(Strings.Crash.OpenFolder), static () => Diagnostics.OpenFolder(Files.Root(Host.LogFolder))),
                Button.Standard(Loc.Get(Strings.Crash.DeleteAll), () => Controls.Confirm(overlay,
                    Loc.Get(Strings.Crash.DeleteAllConfirmTitle), Loc.Get(Strings.Crash.DeleteAllConfirmBody),
                    Loc.Get(Strings.Crash.DeleteAll), static () => Host.DeleteAll()), isEnabled: count > 0)),
            Icons.Folder);

        static Element PrivacyRow() => Row(Loc.Get(Strings.Crash.Privacy), null,
            HyperlinkButton.Create(Loc.Get(Strings.Crash.Privacy), PrivacyUrl), Icons.OpenInNewWindow);

        static Element EraseRow(IOverlayService? overlay, InputHooks hooks, Action<Action> post, Signal<int> eraseEpoch)
        {
            string fullId = InstallId.Ensure(Platform.Settings);
            string sub = Loc.Get(Strings.Crash.EraseSub) + " " + Strings.Crash.EraseInstallId(ShortInstallId(fullId));
            bool enabled = Uploader.Configured;

            void OnDelete() => Controls.Confirm(overlay, Loc.Get(Strings.Crash.EraseConfirmTitle), Loc.Get(Strings.Crash.EraseConfirmBody),
                Loc.Get(Strings.Crash.EraseButton), () => _ = Task.Run(async () =>
                {
                    var result = await Uploader.DeleteRemote(CancellationToken.None).ConfigureAwait(false);
                    post(() =>
                    {
                        if (result.Ok) Notify.Say(Strings.Crash.EraseDone(result.Deleted.ToString(CultureInfo.InvariantCulture)), InfoBarSeverity.Success);
                        else Notify.Say(Loc.Get(Strings.Crash.EraseFailed), InfoBarSeverity.Error);
                        eraseEpoch.Value = eraseEpoch.Peek() + 1;
                    });
                }));

            return Row(Loc.Get(Strings.Crash.EraseRow), sub,
                HStack(Spacing.S,
                    Button.Subtle(Loc.Get(Strings.Crash.EraseCopy), () => hooks.Clipboard?.SetText(fullId)),
                    Button.Standard(Loc.Get(Strings.Crash.EraseButton), OnDelete, isEnabled: enabled)),
                Icons.Delete);
        }
    }

    // ══ 4. LOGS › REPORTS ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Rows from <see cref="Host.Bundles"/>, remounted on <see cref="Host.ReportsVersion"/> — mounted under
    /// the Logs panel body (<c>Settings.UI.cs</c>'s <c>LogsTab</c> → <c>Diagnostics.LogsPanel</c>).</summary>
    public sealed class ReportsList : Component
    {
        public override Element Render()
        {
            var overlay = UseContext(Overlay.Service);
            _ = Host.ReportsVersion.Value;   // subscribed
            var bundles = Host.Bundles();

            var rows = new Element[bundles.Count];
            for (int i = 0; i < bundles.Count; i++)
                rows[i] = ReportRow(bundles[i]) with { Key = "crash:" + bundles[i].Summary.ReportId };

            var kids = new List<Element>(2)
            {
                new BoxEl
                {
                    Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, AlignSelf = FlexAlign.Stretch,
                    Children =
                    [
                        new TextEl(Loc.Get(Strings.Crash.Reports)) { Size = 18f, Weight = 600, Grow = 1f },
                        Button.Standard(Loc.Get(Strings.Crash.OpenFolder), static () => Diagnostics.OpenFolder(Files.Root(Host.LogFolder))),
                        Button.Standard(Loc.Get(Strings.Crash.DeleteAll), () => Controls.Confirm(overlay,
                            Loc.Get(Strings.Crash.DeleteAllConfirmTitle), Loc.Get(Strings.Crash.DeleteAllConfirmBody),
                            Loc.Get(Strings.Crash.DeleteAll), static () => Host.DeleteAll()), isEnabled: bundles.Count > 0),
                    ],
                },
            };
            kids.Add(bundles.Count == 0
                ? new TextEl(Loc.Get(Strings.Crash.ReportsEmpty)) { Size = 12f, Color = Tok.TextTertiary }
                : new BoxEl { Direction = 1, AlignSelf = FlexAlign.Stretch, Children = rows });

            return new BoxEl { Direction = 1, Gap = Spacing.M, AlignSelf = FlexAlign.Stretch, Children = kids.ToArray() };
        }

        static Element ReportRow(BundleInfo b)
        {
            var s = b.Summary;
            ColorF dot = s.Kind switch
            {
                Kind.Hang => Tok.SystemFillCaution,
                Kind.UncleanExit => Tok.SystemFillSolidNeutral,
                _ => Tok.SystemFillCritical,
            };
            string kindLabel = Loc.Get(s.Kind switch
            {
                Kind.Hang => Strings.Crash.KindHang,
                Kind.ExitCode => Strings.Crash.KindExit,
                Kind.UncleanExit => Strings.Crash.KindClosed,
                _ => Strings.Crash.KindCrash,
            });
            string what = WhatText(s);
            var send = b.Send;
            string state = s.Kind == Kind.UncleanExit ? "—" : send.State switch
            {
                SendState.Sent => Strings.Crash.StateSent(FormatSentTime(send.SentAtUtc)),
                SendState.Queued => Loc.Get(Strings.Crash.StateQueued),
                SendState.Failed => Loc.Get(Strings.Crash.StateFailed),
                _ => Loc.Get(Strings.Crash.StateNotSent),
            };
            bool canSend = Uploader.Configured && s.Kind != Kind.UncleanExit && send.State is SendState.NotSent or SendState.Failed;

            return new BoxEl
            {
                Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, AlignSelf = FlexAlign.Stretch,
                Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.XS),
                BorderWidth = 1f, BorderColor = Tok.StrokeDividerDefault, Corners = Radii.ControlAll,
                Children =
                [
                    new BoxEl { Width = 8f, Height = 8f, Corners = Radii.FullAll, Fill = dot, Shrink = 0f },
                    new TextEl(b.StampLocal.ToString("g", CultureInfo.CurrentCulture)) { Size = 12f, Color = Tok.TextSecondary, Width = 130f, Shrink = 0f },
                    new TextEl(kindLabel) { Size = 13f, Weight = 600, Width = 56f, Shrink = 0f },
                    new TextEl(what) { Size = 13f, Color = Tok.TextSecondary, Grow = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    new TextEl(state) { Size = 12f, Color = Tok.TextTertiary, Width = 110f, Shrink = 0f },
                    Button.Standard(Loc.Get(Strings.Crash.View), () => ViewReport(b.ReportTxt)),
                    canSend ? Button.Accent(Loc.Get(Strings.Crash.SendButton), () => SendOne(b)) : new BoxEl { Width = 0f },
                ],
            };
        }

        static string WhatText(Summary s) => s.Kind switch
        {
            Kind.Hang => HangWhat(s.ExceptionMessage),
            Kind.UncleanExit => Loc.Get(Strings.Crash.ClosedSub),
            _ => (s.ExceptionType.Length > 0 ? s.ExceptionType : "0x" + s.ExitCode.ToString("X", CultureInfo.InvariantCulture))
                 + (s.LastRoute.Length > 0 ? " · " + s.LastRoute : ""),
        };

        /// <summary>Pulls the seconds back out of <c>Crash.Handler.WriteHangBundle</c>'s "no UI heartbeat for N s"
        /// wording; any other shape (a future handler change, a synthetic fixture) falls back to the raw text rather
        /// than guessing.</summary>
        static string HangWhat(string exceptionMessage)
        {
            int forIdx = exceptionMessage.LastIndexOf("for ", StringComparison.Ordinal);
            if (forIdx >= 0)
            {
                int start = forIdx + 4, end = exceptionMessage.IndexOf(' ', start);
                if (end > start && int.TryParse(exceptionMessage.AsSpan(start, end - start), NumberStyles.None, CultureInfo.InvariantCulture, out int seconds))
                    return Strings.Crash.HangWhat(seconds.ToString(CultureInfo.InvariantCulture));
            }
            return exceptionMessage.Length > 0 ? exceptionMessage : Loc.Get(Strings.Crash.KindHang);
        }

        /// <summary>The local short time <c>send.json</c>'s ISO <see cref="SendRecord.SentAtUtc"/> renders as; an
        /// unparsable or missing stamp (never expected from <c>Crash.Uploader</c>'s own writer) degrades to the raw
        /// text rather than throwing into a settings-card render.</summary>
        static string FormatSentTime(string? sentAtUtc)
        {
            if (sentAtUtc is { Length: > 0 } t && DateTime.TryParse(t, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var sentUtc))
                return sentUtc.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
            return sentAtUtc ?? "";
        }

        static void ViewReport(string path)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", "\"" + path + "\"") { UseShellExecute = false })?.Dispose(); }
            catch (Exception ex) { Log.Warn("crash", "reports.view.failed", ex); }
        }

        static void SendOne(BundleInfo b)
        {
            bool dumpAllowed = ConsentPolicy.DumpAllowed((Reporting)Platform.Settings.Get(Platform.Keys.CrashReporting), b.Summary.Kind,
                Platform.Settings.Get(Platform.Keys.CrashIncludeDump), manualSend: false);
            Uploader.Enqueue(b, dumpAllowed);
        }
    }
}
