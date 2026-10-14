// ── Screens/ReleaseNotesModel.cs ───────────────────────────────────────────────────────────────────────────────────────
// the What's-new surfaces' MODEL: the last-seen marker, the developer/deep-link read, the after-update gate, latch and
// load, and the auto-show write
//
// Role: CORE (engine-free: settings, a latch, a background load; no element, component or overlay type)
// Spec: docs/themes/plan/technical-plan.md §5.1 C15 (X37)
//
// Moved out of `ReleaseNotes.UI.cs` unchanged: the page's seen-on-open read and write (ch 28 §0.11: `lastSeen` is read
// BEFORE the write and carried into the load), the deep-link verb read behind the viewer's "Try it", the after-update
// plate's gate (`CurrentGate`), its once-per-launch latch, the document load that runs before the plate opens, the
// resolve step (re-check the gates on the UI thread, consume the one-shot only when the plate is about to open), and the
// "Don't show again" write. The plate, the page and the viewer stay in the view file and call these verbs.
//
// This is NOT a partial of `ReleaseNotes`: `Wavee.ReleaseTool` compiles `ReleaseNotes.cs` + `ReleaseNotes.Model.cs` by name
// and must not pick up app types (the settings, the wizard, the shell).

using System.Threading.Tasks;

namespace Wavee;

public static class ReleaseNotesModel
{
    // 0 idle · 1 a load is in flight, or the automatic plate already resolved this launch (opened, or found nothing).
    static int s_afterUpdateLatch;

    // ── the running build and the last-seen marker ──────────────────────────────────────────────────────────────────

    /// <summary>The running build as the loaders take it.</summary>
    public static ReleaseNotes.RunningBuild Running()
    {
        var me = Platform.Version;
        return new ReleaseNotes.RunningBuild(me.Core, me.Channel, me.IsStore);
    }

    /// <summary>The release the reader last opened the notes for ("" before the first open). Read BEFORE
    /// <see cref="MarkSeen"/> and carried into the load: advancing first hands the range (me, me) and the banner and every
    /// dot die.</summary>
    public static string LastSeen() => Platform.Settings.Get(Platform.Keys.ReleaseNotesLastSeen);

    /// <summary>Reading the page is what marks the notes seen: the last-seen marker becomes the running core version.</summary>
    public static void MarkSeen() => Platform.Settings.Set(Platform.Keys.ReleaseNotesLastSeen, Platform.Version.Core);

    /// <summary>"Open What's new" carries an unread dot while the running release's notes have never been opened.</summary>
    public static bool RunningNotesUnread() => ReleaseNotes.NotesUnread(LastSeen(), Platform.Version.Core);

    // ── the developer / deep-link read ──────────────────────────────────────────────────────────────────────────────

    /// <summary>The in-app route a highlight's deep link opens, or null when the link is not an "open this route" verb
    /// (the developer-mode setting decides which developer-only links count).</summary>
    public static Shell.Route? OpenRouteFor(string? deepLink)
    {
        if (deepLink is not { Length: > 0 }) return null;
        var verb = Shell.DeepLink(deepLink, Platform.Settings.Get(Platform.Keys.DeveloperMode));
        return verb.Kind == Shell.DeepLinkKind.Open ? verb.Route : null;
    }

    // ── the after-update plate: gate, latch, load, resolve ──────────────────────────────────────────────────────────

    /// <summary>The quad of the previous run an armed after-update plate summarizes from ("" = not armed).</summary>
    public static string PendingFrom() => Platform.Settings.Get(Platform.Keys.ReleaseNotesPendingFrom);

    /// <summary>The "Show the update summary again" source: the DURABLE previous version (the running quad on an install that
    /// never updated).</summary>
    public static string SummaryFromQuad()
    {
        string from = Platform.Settings.Get(Platform.Keys.ReleaseNotesPreviousVersion);
        return from.Length > 0 ? from : Platform.Version.Quad;
    }

    /// <summary>Should the automatic plate open now? Armed, auto-show on, the wizard neither pending nor open, and no crash
    /// notice this launch.</summary>
    public static ReleaseNotes.AfterUpdateVerdict Gate() => ReleaseNotes.AfterUpdateGate.Decide(
        PendingFrom(),
        Platform.Settings.Get(Platform.Keys.ReleaseNotesAutoShow),
        Setup.Gating.IsPending(Platform.Settings) || Setup.WizardOpen.Peek(),
        ReleaseNotes.CrashNoticeThisLaunch);

    /// <summary>The automatic path's entry: the gate says Open AND this launch's one load is claimed (one load at a time, one
    /// plate per launch). Every deferral leaves the key armed and the latch free.</summary>
    public static bool TryBeginAutomaticLoad()
        => Gate() == ReleaseNotes.AfterUpdateVerdict.Open && Interlocked.Exchange(ref s_afterUpdateLatch, 1) == 0;

    /// <summary>Load the summary document and its cards OFF the UI thread (the plate opens at its final height only once this
    /// has landed). Null on any failure: the absorbed throw is logged and the caller decides what "nothing" means.</summary>
    public static async Task<(ReleaseNotes.ReleaseNotesDocument Doc, ReleaseNotes.HighlightItem[] Cards)?> LoadSummaryAsync()
    {
        try
        {
            var store = ReleaseNotes.Notes;
            var me = Running();
            return await Task.Run(() => ReleaseNotes.LoadSummaryAsync(store, me, CancellationToken.None)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn("whatsnew", "dialog notes load failed", ex);
            return null;
        }
    }

    /// <summary>On the UI thread, after the load: should the plate open? The automatic path re-checks the gate (the wizard may
    /// have opened meanwhile; false frees the latch for the next marker bump) and consumes the one-shot only when the load
    /// resolved to something (a load that found nothing opens nothing and leaves pendingFrom ARMED for the next launch). The
    /// manual path always opens and consumes.</summary>
    public static bool Resolve(bool automatic, bool loaded)
    {
        if (automatic)
        {
            if (Gate() != ReleaseNotes.AfterUpdateVerdict.Open) { Interlocked.Exchange(ref s_afterUpdateLatch, 0); return false; }
            if (!ReleaseNotes.AfterUpdateGate.Consume(Platform.Settings, opening: loaded))
            {
                Log.Info("whatsnew", "after-update notes unavailable; the plate stays armed for the next launch");
                return false;
            }
            return true;
        }
        ReleaseNotes.AfterUpdateGate.Consume(Platform.Settings, opening: true);
        return true;
    }

    /// <summary>Forget the latch (tests; a harness that boots more than once).</summary>
    public static void ResetLatchForTests() => Interlocked.Exchange(ref s_afterUpdateLatch, 0);

    // ── the plate's checkbox ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>"Don't show this again" (written IMMEDIATELY, not on close): turning it on switches auto-show off.</summary>
    public static void SetDontShowAgain(bool dontShow) => Platform.Settings.Set(Platform.Keys.ReleaseNotesAutoShow, !dontShow);
}
