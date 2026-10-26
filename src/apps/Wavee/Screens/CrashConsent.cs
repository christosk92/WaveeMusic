// ── Screens/CrashConsent.cs ────────────────────────────────────────────────────────────────────────────────────────────
// the crash prompt's CONSENT model: what this launch's bundle does (prompt / silent upload / queued toast), the
// scrubbed preview a send would carry, and the send itself (consent write, enqueue)
//
// Role: CORE (engine-free: settings, the crash host and uploader, file reads; no element, component or overlay type)
// Spec: docs/themes/plan/technical-plan.md §5.1 C15 (X38); docs/plans/wavee/crash-diagnostics-implementation.md §D, §F
//
// Moved out of `Crash.UI.cs` unchanged: the launch decision of the chrome's effect A (`ResolveLaunch`), `ComposePreview`
// (pool-thread scrub of the saved report) and the prompt's submit body (`Submit`). The chrome, the dialog, the body, the
// outcome toasts and the overlay handle stay in the view file, which asks this model what to show and calls its verbs.

using FluentGpu.WindowsApi.Network;

namespace Wavee;

public static class CrashConsent
{
    /// <summary>The crash prompt waits behind the setup wizard: it must not stack on the plate, and stays armed until it is
    /// done.</summary>
    public static bool DeferredBehindWizard() => Setup.Gating.IsPending(Platform.Settings) || Setup.WizardOpen.Peek();

    /// <summary>The EFFECTIVE reporting mode: a stored Automatic on a build that can't send behaves as Ask each time.</summary>
    public static Crash.Reporting EffectiveMode()
        => Crash.ConsentPolicy.Effective((Crash.Reporting)Platform.Settings.Get(Platform.Keys.CrashReporting), Crash.Uploader.Configured);

    /// <summary>This launch's bundle, resolved. Null while there is nothing to do (no bundle, or deferred behind the wizard and
    /// left armed). Otherwise the bundle is consumed (one-shot, whichever arm fires), the after-update plate is told to wait
    /// for the next launch, and: Automatic queues the report (online the only toast is the real outcome; offline, say now that
    /// it waits), Ask returns the bundle for the caller to PROMPT with, and an unclean exit does nothing.</summary>
    public static Crash.BundleInfo? ResolveLaunch()
    {
        if (Crash.Host.ThisLaunch is not { } bundle) return null;
        if (DeferredBehindWizard()) return null;                    // deferred — still armed
        Crash.Host.ThisLaunch = null;                               // one-shot: consumed now, whichever arm fires below
        ReleaseNotes.CrashNoticeThisLaunch = true;                  // the after-update plate waits for the next launch

        var mode = EffectiveMode();
        bool online = NetworkStatus.IsOnline;
        var action = Crash.ConsentPolicy.Decide(mode, bundle.Summary.Kind, online);
        switch (action)
        {
            case Crash.ConsentPolicy.Action.UploadSilently:
            case Crash.ConsentPolicy.Action.Toast:
            {
                bool offline = action == Crash.ConsentPolicy.Action.Toast;
                bool dumpAllowed = Crash.ConsentPolicy.DumpAllowed(mode, bundle.Summary.Kind,
                    Platform.Settings.Get(Platform.Keys.CrashIncludeDump), manualSend: false);
                Crash.Uploader.Enqueue(bundle, dumpAllowed, Crash.OutcomeToasts.Track(bundle, queuedShown: offline));
                if (offline) Crash.OutcomeToasts.Show(bundle, Crash.UploadToasts.For(Crash.SendState.Queued, queuedAlreadyShown: false));
                return null;
            }
            case Crash.ConsentPolicy.Action.Prompt:
                return bundle;
            default:
                return null;
        }
    }

    /// <summary>Pool thread: the scrubbed text a send would carry — what the prompt previews and Saved reports' "Copy"
    /// puts on the clipboard. "" when the scrub itself failed (logged).</summary>
    public static string ComposePreview(Crash.BundleInfo bundle, Feedback.RedactionRules rules)
    {
        string reportTxt = "";
        try { reportTxt = File.ReadAllText(bundle.ReportTxt); } catch (Exception ex) { Log.Warn("crash", "preview.read.report.failed", ex); }
        string[] tail = Array.Empty<string>();
        try { if (File.Exists(bundle.TailTxt)) tail = File.ReadAllLines(bundle.TailTxt); } catch (Exception ex) { Log.Warn("crash", "preview.read.tail.failed", ex); }
        try { return Crash.Scrubber.Preview(Crash.Scrubber.Scrub(bundle.Summary, reportTxt, tail, rules)); }
        catch (Exception ex) { Log.Warn("crash", "preview.scrub.failed", ex); return ""; }
    }

    /// <summary>A launch prompt's consent record (a manual send from the Saved reports list records nothing): "Always send"
    /// switches the mode to Automatic, and consent is stamped asked either way.</summary>
    public static void RecordConsent(bool fromList, bool always)
    {
        if (fromList) return;
        if (always) Platform.Settings.Set(Platform.Keys.CrashReporting, (int)Crash.Reporting.Auto);
        Platform.Settings.Set(Platform.Keys.CrashConsentAsked, true);
    }

    /// <summary>The prompt's Send: a launch prompt (not <paramref name="fromList"/>) records consent — "Always send" switches
    /// the mode to Automatic — and stamps consent asked; then the report is queued. A manual send shows what it sends, so the
    /// dump checkbox alone decides whether the dump rides (it needs a dump to exist). The toast waits for the service's answer.</summary>
    public static void Submit(Crash.BundleInfo bundle, bool fromList, bool always, bool includeDump)
    {
        RecordConsent(fromList, always);
        Crash.Uploader.Enqueue(bundle, includeDump && bundle.HasDump, Crash.OutcomeToasts.Track(bundle, queuedShown: false));
    }
}
