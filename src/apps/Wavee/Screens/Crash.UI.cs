// ── Screens/Crash.UI.cs ────────────────────────────────────────────────────────────────────────────────────────────
// The in-app crash & diagnostics UI (WP-E): the zero-size overlay chrome that decides prompt / silent-upload / toast
// for this launch (and arms the hang toast + the --crash-probe timer via Crash.Host.ArmProbe), the crash/hang consent
// dialog body (also Settings › Privacy & diagnostics › Saved reports' per-row "Send…", through `OpenSendPrompt`) and the
// outcome toasts. The tab's own rows — the crash-report mode, the saved-reports list, the §J right-to-erasure card —
// live in Screens/Settings.UI.Privacy.cs and read the internal doors below (`OpenSendPrompt`, `ComposePreview`,
// `PrivacyUrl`, `OutcomeToasts`).
// Everything here reads Crash.Host.* / Crash.Uploader.* / Crash.Scrubber.* (WP-A/B/C) and never writes a bundle
// itself — a report is only ever read, scrubbed for preview / copy, enqueued or deleted from here.
//
// Role: UI
// Owner: WP-E
// Spec: docs/plans/wavee/crash-diagnostics-implementation.md §D, §F "E · In-app UI", §I "WP-E", §J (erasure, added
//       2026-09-24); docs/plans/wavee/crash-production-readiness-implementation.md W3b (#165). Copy is verbatim from
//       the approved prototype artboards (Prompt.dc.html, Toast.dc.html, Settings.dc.html, Reports.dc.html).
//
// TRUTHFUL SEND STATES (#165): no toast claims "sent" before the service said so. Every upload this file starts goes
// through Uploader.Enqueue with an OutcomeToasts.Track callback, which the uploader calls on the UI thread with each
// real outcome (queued after a retryable attempt, then sent / failed); all of one report's cards share the dedupe key
// "crash:<id>" and a newer one replaces the older card.
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
                // The launch decision (deferral behind the wizard, the one-shot, silent upload / queued toast) is CrashConsent's.
                if (CrashConsent.ResolveLaunch() is { } bundle) OpenPrompt(overlay, bundle);
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

    /// <summary>Settings › Privacy &amp; diagnostics › Saved reports' per-row "Send…": the manual send (preview + dump
    /// checkbox) for any saved report, titled "Send this report?".</summary>
    internal static void OpenSendPrompt(IOverlayService? overlay, BundleInfo bundle) => OpenPrompt(overlay, bundle, fromList: true);

    /// <param name="fromList">The Saved reports list's per-row "Send…": the same manual send (preview + dump checkbox) for
    /// any saved report, titled "Send this report?" — not "Wavee closed unexpectedly", which is only true of this launch's.</param>
    static void OpenPrompt(IOverlayService? overlay, BundleInfo bundle, bool fromList = false)
    {
        if (overlay is null) return;
        var session = new PromptSession();
        var handle = ContentDialog.Show(overlay, d =>
        {
            d.Title = Loc.Get(fromList ? Strings.Crash.PromptListTitle
                : bundle.Summary.Kind == Kind.Hang ? Strings.Crash.PromptHangTitle : Strings.Crash.PromptTitle);
            d.DialogWidth = DialogChrome;
            d.Content = Embed.Comp(() => new CrashPromptBody { Bundle = bundle, Session = session, FromList = fromList })
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

    static string FormatMb(long bytes) => (bytes / 1_048_576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB";

    /// <summary>Pool thread: the scrubbed text a send would carry (<see cref="CrashConsent.ComposePreview"/>).</summary>
    internal static string ComposePreview(BundleInfo bundle, Feedback.RedactionRules rules) => CrashConsent.ComposePreview(bundle, rules);

    // ══ 2. THE OUTCOME TOASTS ══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The one door every upload-outcome card goes through (UI thread only). All of one report's cards share
    /// the dedupe key <c>crash:&lt;id&gt;</c>, and a newer outcome CLOSES the older card first: a coalesced toast keeps
    /// its first message, so a waiting "queued" card would otherwise swallow the later "sent". Internal for Settings ›
    /// Developer's "Send a test crash report" (#165), which shows the same real outcome.</summary>
    internal static class OutcomeToasts
    {
        static readonly Dictionary<string, ToastHandle> s_cards = new(StringComparer.Ordinal);

        /// <summary>The <c>settled</c> callback for <see cref="Uploader.Enqueue"/>: each real outcome picks its card via
        /// <see cref="UploadToasts.For"/>; "queued" is said once per report (<paramref name="queuedShown"/> = the caller
        /// already said it).</summary>
        public static Action<SendRecord> Track(BundleInfo bundle, bool queuedShown, bool test = false)
        {
            bool shown = queuedShown;
            return rec =>
            {
                var toast = UploadToasts.For(rec.State, shown);
                if (toast == UploadToasts.Toast.Queued) shown = true;
                Show(bundle, toast, test);
            };
        }

        /// <param name="test">A Settings › Developer test report: its "sent" line names the test, never "Wavee crashed".</param>
        public static void Show(BundleInfo bundle, UploadToasts.Toast toast, bool test = false)
        {
            if (toast == UploadToasts.Toast.None) return;
            string id = bundle.Summary.ReportId;
            string key = "crash:" + id;
            if (s_cards.Remove(id, out var previous)) previous.Close();
            s_cards[id] = toast switch
            {
                UploadToasts.Toast.Sent => Notify.Say(
                    test ? Strings.Crash.TestReportSentDetail(ShortId(id)) : Strings.Crash.SentToastDetail(ShortId(id)),
                    InfoBarSeverity.Success, title: Loc.Get(Strings.Crash.SentToast), dedupeKey: key),
                UploadToasts.Toast.Queued => Notify.Say(Loc.Get(Strings.Crash.QueuedToast), InfoBarSeverity.Warning,
                    dedupeKey: key, durationMs: 8000f),
                _ => Notify.Say(Loc.Get(Strings.Crash.SendFailed), InfoBarSeverity.Error, dedupeKey: key, durationMs: 8000f),
            };
        }
    }

    // ══ 3. THE PROMPT BODY ═════════════════════════════════════════════════════════════════════════════════════════

    const float DialogChrome = 548f, BodyWidth = 500f, PreviewHeight = 220f;

    /// <summary>The one handshake between <see cref="OpenPrompt"/>'s dialog chrome and the body: the card installs
    /// its submit + carries the handle it can close itself from (the "Report on GitHub instead" link).</summary>
    sealed class PromptSession
    {
        public Func<bool>? Submit;
        public OverlayHandle? Handle;
    }

    /// <summary>Width 500; every signal seeded once from <see cref="Bundle"/> at mount (the props freeze — the dialog is
    /// keyed by report id, and a re-render must not re-seed the user's in-progress choices). <see cref="FromList"/> is
    /// the Saved reports list's manual send: the bundle may be days old, so the header shows its date and there is no "last
    /// time" lead and no "always send" switch.</summary>
    sealed class CrashPromptBody : Component
    {
        public required BundleInfo Bundle;
        public required PromptSession Session;
        public bool FromList;

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
                    string text = ComposePreview(bundle, rules);
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
                    CrashConsent.Submit(Bundle, FromList, always.Peek(), includeDump.Peek());
                    return true;
                };
                return (Action?)(() => Session.Submit = null);
            }, DepKey.Empty);

            var children = new List<Element>(10) { HeaderInfoBar(Bundle, FromList) };
            if (!FromList) children.Add(Lead(Bundle.Summary.Kind));
            children.Add(WhatIsSentBox(showReport, preview.Value));
            if (Bundle.HasDump) children.Add(IncludeDumpRow(includeDump, Bundle.DumpBytes));
            if (!FromList) children.Add(CheckBox.Create(Loc.Get(Strings.Crash.PromptAlways), always));
            children.Add(new BoxEl
            {
                Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Width = BodyWidth,
                Children =
                [
                    HyperlinkButton.Create(Loc.Get(Strings.Crash.PromptPrivacy), PrivacyUrl),
                    new TextEl("·") { Color = Tok.TextTertiary },
                    HyperlinkButton.Create(Loc.Get(Strings.Crash.PromptGithub), () =>
                    {
                        Feedback.OpenCrashReport(Bundle);
                        Session.Handle?.Close();
                    }),
                ],
            });
            if (!Uploader.Configured)
                children.Add(new TextEl(Loc.Get(Strings.Crash.PromptNotConfigured)) { Size = 12f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, MaxWidth = BodyWidth });

            return new BoxEl { Direction = 1, Gap = Spacing.M, Width = BodyWidth, Children = children.ToArray() };
        }

        static Element HeaderInfoBar(BundleInfo bundle, bool fromList)
        {
            var s = bundle.Summary;
            string when = fromList
                ? bundle.StampLocal.ToString("g", CultureInfo.CurrentCulture)
                : Strings.Crash.PromptWhen(bundle.StampLocal.ToString("t", CultureInfo.CurrentCulture));
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

    internal const string PrivacyUrl = "https://github.com/christosk92/WaveeMusic/blob/main/PRIVACY.md";
}
