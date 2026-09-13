// ── Platform/Notify.Host.cs ────────────────────────────────────────────────────────────────────────────────────────
// WinRT toasts, the Action Center, both schedulers (DaylistNotifier, ReleaseNotifier), AUMID + the activator, the
// activation → deep-link hop; ToastEscalator's WinRT half (A11)
//
// Role: SHELL
// Owner: I
// Wave: 4
// Budget: 715 lines
// Spec: ch 14 §9 (715) — ch 19 §9.4 states in terms that it adds nothing to this file and it "must not re-count it"
//
// THE SHELL RULE, applied: nothing here DECIDES anything. Every rule — the watermark, the cap, the sentinel fold, the
// quiet-hours shift, the per-topic dial, the update transition table — is `Notify.cs`'s, and this file posts values to
// the OS and reads them back. Where 0.2.9 had a decision inline in a WinRT-bound static (`ToastEscalator`'s whole
// escalation policy, which had NO test), the decision moved out and this file calls `Notify.Escalation.Decide`.
//
// FAIL-SOFT EVERYWHERE. A notifier that throws must never take playback or startup with it: every OS/WinRT call is
// wrapped, and a machine where toasts are unavailable (an elevated process, no AUMID) simply gets no banners. A
// failure is LOGGED, never swallowed silently — a silent catch here is how a lost toast went unnoticed for a release.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

using FluentGpu.Localization;
using FluentGpu.WindowsApi.Notifications;

namespace Wavee;

public static partial class Notify
{
    // ══ 1. AUMID + THE ACTIVATOR ════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Wavee's toast-activator CLSID. Defined ONCE: the same value goes here and in the MSIX manifest's
    /// <c>ToastActivatorCLSID</c> + <c>com:ExeServer</c> class id. A mismatch means the Shell relaunches a class it
    /// cannot bind and the click reaches nothing.</summary>
    public static readonly Guid ActivatorClsid = new("6C3E7A31-5E5E-4C0C-9D2E-8A9F6B2C41D7");

    /// <summary>The display name the Action Center shows for an UNPACKAGED build (a packaged one takes it from the
    /// manifest).</summary>
    const string ActionCenterName = "Wavee";

    static bool s_registered;
    static Action<string>? s_deepLink;
    static Action<Action>? s_post;

    /// <summary>Install the toast platform. Called once, from <c>Shell.Run</c>, AFTER the window exists (the
    /// activation dispatcher posts onto the UI thread) and BEFORE anything can rebuild the feed.
    /// <para><paramref name="deepLink"/> is the ACTIVATION → DEEP-LINK HOP: a click on a banner hands us the toast's
    /// <c>launch=</c> string, which is a <c>wavee://</c> verb, and the shell routes it through exactly the same intake
    /// an OS protocol activation takes. ONE intake, so a toast cannot reach a destination a link cannot.</para></summary>
    public static void HostInstall(Action<Action> post, Action<string> deepLink, string? iconPath = null)
    {
        s_post = post;
        s_deepLink = deepLink;
        if (s_registered || !ToastNotifier.IsSupported) return;
        try
        {
            // The dispatcher is re-assigned even on a second call, so a re-entry updates the hop without
            // re-registering — collapsing this into `if (registered) return;` leaves a STALE dispatcher pointing at a
            // dead post delegate.
            ToastNotifier.Default.ActivationDispatcher = a => post(a);
            ToastNotifier.Default.Activated -= OnActivated;
            ToastNotifier.Default.Activated += OnActivated;
            ToastNotifier.Default.Register(ActivatorClsid, ActionCenterName, iconPath);
            s_registered = true;
            // The jump list is keyed BY AUMID: a mismatch writes the list for an identity the taskbar button does not
            // have and it silently never appears. `Playback.Os` owns the list; we own the identity.
            Playback.Os.JumpList.Aumid = ToastNotifier.Default.Aumid;
            Log.Event(WaveeLogLevel.Info, "notify", "toast.registered", "toast platform registered", null, -1, null,
                WaveeLogField.Of("aumid", ToastNotifier.Default.Aumid));
        }
        catch (Exception ex)
        {
            Log.Warn("notify", "toast registration failed; no Windows banners this run", ex);
        }
    }

    static void OnActivated(ToastActivatedArgs args)
    {
        if (args.Argument.Length == 0) return;
        try { s_deepLink?.Invoke(args.Argument); }
        catch (Exception ex) { Log.Warn("notify", "toast activation could not be routed", ex); }
    }

    /// <summary>Sign-out teardown (ch 14 DATA GAP 8, VERIFIED: NOTHING in 0.2.9 was wired to this, so a signed-out
    /// Wavee kept the previous account's scheduled drop toasts armed in the OS and fired them with the new user signed
    /// in). <c>Playback.Os.SignedOut</c> does the jump-list half beside this.</summary>
    public static void SignedOut()
    {
        ReleaseDrops.UnscheduleAll();
        Daylist.Unschedule();
        try { ToastNotifier.Default.RemoveGroup(LiveGroup); } catch (Exception) { /* fail-soft */ }
        try { s_toastImages.Clear(); } catch (Exception) { /* fail-soft */ }
    }

    /// <summary>Wavee's OWN toast-image cache (G-091): the engine's <see cref="ToastImageCache.Default"/> is rooted at
    /// <c>%LOCALAPPDATA%\FluentGpu\toastimg</c> — a generic engine folder, not this app's — and is otherwise unbounded
    /// (cleared only by <see cref="SignedOut"/>, which never ran before this wave). This instance roots the cache at
    /// <c>%LOCALAPPDATA%\Wavee\toastimg</c> instead; <see cref="TrimToastImageCache"/> gives it the 50 MB ceiling the
    /// engine type does not have on its own.</summary>
    static readonly ToastImageCache s_toastImages = new("Wavee");

    /// <summary>The toast-image cache's soft cap. Enforced opportunistically (after a localize that actually wrote a
    /// file) rather than on a timer — the folder only grows while toasts with remote art are being raised, which is
    /// already a human-rate event.</summary>
    const long ToastImageCacheCapBytes = 50L * 1024 * 1024;

    /// <summary>Evict the OLDEST (by last-write) cached images until the folder is back under
    /// <see cref="ToastImageCacheCapBytes"/>. Fail-soft throughout: the cache is a courtesy (it only saves a re-download
    /// of art already fetched once), never a correctness requirement.</summary>
    static void TrimToastImageCache()
    {
        try
        {
            string dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wavee", "toastimg");
            if (!System.IO.Directory.Exists(dir)) return;

            var files = new System.IO.DirectoryInfo(dir).GetFiles();
            long total = 0;
            for (int i = 0; i < files.Length; i++) total += files[i].Length;
            if (total <= ToastImageCacheCapBytes) return;

            Array.Sort(files, static (a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
            for (int i = 0; i < files.Length && total > ToastImageCacheCapBytes; i++)
            {
                total -= files[i].Length;
                try { files[i].Delete(); } catch (Exception) { /* another process may have raced us; fine either way */ }
            }
        }
        catch (Exception) { /* fail-soft: see the class remark */ }
    }

    /// <summary>Process exit: revoke the activator so the next launch registers cleanly.</summary>
    public static void HostShutdown()
    {
        if (!s_registered) return;
        s_registered = false;
        try { ToastNotifier.Default.Activated -= OnActivated; } catch (Exception) { }
        try { ToastNotifier.Default.Unregister(); } catch (Exception) { }
    }

    // ══ 2. THE LIVE ESCALATOR'S WinRT HALF (A11) ════════════════════════════════════════════════════════════════════

    const string LiveGroup = "wavee.live";

    /// <summary>The (state, target) of the app update last raised as a banner. PROCESS-LIFETIME, because that is the
    /// lifetime of the condition it describes: making it an instance field on something that can be rebuilt re-raises
    /// the banner on every rebuild.</summary>
    static string s_lastUpdateRaised = "";

    /// <summary>The last download percentage pushed into the live toast, or -1 when no download toast is up.</summary>
    static int s_lastProgressPushed = -1;

    /// <summary>The CORE's seam: every rebuild lands here. Decides nothing — <see cref="Escalation.Decide"/> does —
    /// and then posts the result to the OS.</summary>
    static partial void HostConsider(in Feed feed)
    {
        if (!ToastNotifier.IsSupported || feed.Items.Count == 0) return;

        var policy = Prefs.Policy();
        long watermark = Platform.Settings.Get(Platform.Keys.NotifyLastToastedMs);
        var plan = Escalation.Decide(feed.Items, watermark, in policy, static t => Prefs.Level(t),
            DateTimeOffset.Now, s_lastUpdateRaised);

        // The progress push happens BEFORE the raise loop: after it, a 5 %-per-tick stream becomes twenty identical
        // banners.
        if (plan.ProgressPercent >= 0) UpdateProgressToast(feed, plan.ProgressPercent);
        else s_lastProgressPushed = -1;   // out of Downloading: arm the latch so a LATER download starts from its own raise

        int raised = 0;
        for (int i = 0; i < plan.Raise.Length; i++)
        {
            var n = feed.Items[plan.Raise[i]];
            if (n.Category == NotifyCategory.AppUpdate) s_lastUpdateRaised = Escalation.UpdateIdentity(in n);
            if (TryRaise(in n, in policy)) raised++;
        }
        if (plan.Suppressed > 0) RaiseSummary(plan.Suppressed, in policy);

        if (plan.Watermark > watermark) Platform.Settings.Set(Platform.Keys.NotifyLastToastedMs, plan.Watermark);
        if (raised > 0)
            Log.Event(WaveeLogLevel.Debug, "notify", "toast.raised", "banners raised", null, -1, null,
                WaveeLogField.Of("count", raised),
                WaveeLogField.Of("suppressed", plan.Suppressed));
    }

    static bool TryRaise(in Notification n, in NotificationPolicy policy)
    {
        try
        {
            var (title, body, launch, image, circle) = Present(in n);
            if (title.Length == 0) return false;

            var toast = ToastBuilder.Create().Title(title);
            if (body.Length > 0) toast.Body(body);
            bool isDownload = n is { Category: NotifyCategory.AppUpdate, Update.State: AppUpdateState.Downloading };
            if (isDownload)
            {
                // Data-bound so the bar can be driven in place; Show carries no initial data, so the first values are
                // pushed immediately after the raise.
                toast.Progress(dataBound: true);
                s_lastProgressPushed = Math.Clamp(n.Update!.ProgressPercent, 0, 100);
            }
            if (launch is { Length: > 0 }) toast.Launch(launch);
            if (!policy.Sound) toast.Silent();
            toast.Tag(TagFor(in n)).Group(LiveGroup);
            if (image is { Length: > 0 })
            {
                // Remote art must become a LOCAL FILE: the unpackaged AUMID image path silently drops http(s). A
                // PACKAGED process skips localization entirely (the Shell fetches the url itself), which is why art
                // behaves differently packaged vs unpackaged — expected, not a bug.
                try { toast.AppLogo(s_toastImages.Localize(image), circle); TrimToastImageCache(); }
                catch (Exception) { /* art is optional; the text still says what happened */ }
            }
            bool shown = ToastNotifier.Default.Show(toast);
            if (shown && isDownload) PushProgress(in n, s_lastProgressPushed);
            return shown;
        }
        catch (Exception ex)
        {
            Log.Warn("notify", "a banner could not be raised", ex);
            return false;   // a banner that fails is never worth failing a feed refresh over
        }
    }

    static void RaiseSummary(int more, in NotificationPolicy policy)
    {
        try
        {
            var toast = ToastBuilder.Create()
                .Title(Strings.Toast.MoreUpdates(more))
                .Body(Loc.Get(Strings.Toast.OpenToSee))
                .Launch("wavee://open?route=home")
                .Tag("live-summary").Group(LiveGroup);
            if (!policy.Sound) toast.Silent();
            ToastNotifier.Default.Show(toast);
        }
        catch (Exception ex) { Log.Warn("notify", "the summary banner could not be raised", ex); }
    }

    static void UpdateProgressToast(in Feed feed, int percent)
    {
        if (!Escalation.ShouldPushProgress(s_lastProgressPushed, percent)) return;
        for (int i = 0; i < feed.Items.Count; i++)
        {
            var n = feed.Items[i];
            if (n is not { Category: NotifyCategory.AppUpdate, Update.State: AppUpdateState.Downloading }) continue;
            s_lastProgressPushed = percent;
            PushProgress(in n, percent);
            return;
        }
    }

    static void PushProgress(in Notification n, int percent)
    {
        try
        {
            // An expired or dismissed toast answers NotificationNotFound — not an error, and not worth re-raising.
            ToastNotifier.Default.Update(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["progressValue"] = (percent / 100.0).ToString("0.###", CultureInfo.InvariantCulture),
                    ["progressStatus"] = percent >= 100
                        ? Loc.Get(Strings.Update.State.Installing)
                        : Strings.Update.Toast.Downloading(AppUpdateToasts.ReleaseName(n.Update)),
                },
                TagFor(in n), LiveGroup);
        }
        catch (Exception) { /* a live update that fails is never worth failing a feed refresh over */ }
    }

    /// <summary>Presentation for a BANNER. Deliberately NOT shared with the centre's row rendering: a toast has one
    /// line of title and one of body with no layout, so it needs its own (shorter) phrasing, and the centre must stay
    /// free to render rich rows.</summary>
    static (string Title, string Body, string? Launch, string? Image, bool Circle) Present(in Notification n)
    {
        switch (n.Category)
        {
            case NotifyCategory.NewRelease:
                return (n.Title,
                    n.ReleaseKind == NewReleaseKind.Episode
                        ? Strings.Toast.NewEpisode(n.Creator ?? "")
                        : Strings.Toast.NewRelease(n.Creator ?? ""),
                    n.Subject.IsValid ? "wavee://play?ctx=" + Uri.EscapeDataString(n.Subject.Text) : null,
                    n.ImageUrl, false);

            case NotifyCategory.Social:
            {
                // The feed's title is already a finished, server-localized sentence — so it IS the title, cleaned of
                // its leading decorative glyph exactly as Home's timeline cleans it (ch 14 DATA GAP 11: 0.2.9 passed
                // it raw here and stripped it there, so one event read two ways).
                string title = SpotifyUpdates.CleanTitle(n.Title);
                // A concert row's ActionUri is a `spotify:concert:<id>` or an https concert page — NOT a route key.
                // 0.2.9 escaped it straight into `route=`, which `IsKnown` then refused: the banner's click landed
                // NOWHERE. 0.3 routes it through the SAME route composer the in-app panel row uses. A deliberate
                // divergence from 0.2.9 (plan §9.6 Q2) — it needs its own issue + CHANGELOG bullet, and parity item 29
                // is restated as "differs from 0.2.9, deliberately" so a reviewer does not file it as a regression.
                string? launch = LaunchForSocial(in n);
                return (title, n.ActName ?? "", launch, n.ImageUrl, Prefs.TopicOf(in n) == NotifyTopic.Followers);
            }

            case NotifyCategory.AppUpdate:
            {
                // `Available` deliberately raises NO OS toast: the in-app strip already offers it, and an unprompted
                // Action Center banner for something the user has not started is the noisiest possible version of this
                // feature. Downloading and Installing narrate work already in flight; Completed is the one the user
                // genuinely missed (Windows restarted the app), and it deep-links straight at the notes.
                var s = n.Update;
                return s?.State switch
                {
                    AppUpdateState.Downloading => (Loc.Get(Strings.Update.Os.Downloading), "", null, null, false),
                    AppUpdateState.Installing => (Loc.Get(Strings.Update.State.Installing), "", null, null, false),
                    AppUpdateState.Completed => (
                        Strings.Update.Os.Updated(AppUpdateToasts.ReleaseName(s)),
                        Loc.Get(Strings.Update.Os.SeeWhatsNew),
                        "wavee://open?route=whatsnew" + WhatsNewArg(s),
                        null, false),
                    _ => ("", "", null, null, false),
                };
            }

            default:
                return ("", "", null, null, false);   // library activity never becomes a banner
        }
    }

    /// <summary>The <c>wavee://</c> a social banner's click follows, composed through the ROUTE TABLE rather than by
    /// escaping the raw action uri into <c>route=</c>.</summary>
    static string? LaunchForSocial(in Notification n)
    {
        if (n.ActionType != SocialActionType.Navigate || n.ActionUri is not { Length: > 0 } uri) return null;
        var route = Shell.For(EntityUri.Parse(uri));
        if (route.Kind == Shell.RouteKind.NotFound) return null;
        return "wavee://open?route=" + Uri.EscapeDataString(Shell.NameOf(route));
    }

    static string TagFor(in Notification n) => TagIds.Clamp("live:", n.Id);

    /// <summary>The <c>&amp;arg=</c> the "Updated" toast deep-links with — the semver whose notes to open. Omitted
    /// entirely when we do not know it, so the route lands on the newest release rather than on nothing.</summary>
    static string WhatsNewArg(AppUpdateSnapshot? s)
    {
        if (s is null) return "";
        string semver = s.TargetSemVer is { Length: > 0 } v ? v : AppUpdateVersion.ReleaseTagVersion(s.TargetQuad ?? "");
        return semver.Length > 0 ? "&arg=" + Uri.EscapeDataString(semver) : "";
    }

    // ══ 3. THE DAYLIST SCHEDULER ════════════════════════════════════════════════════════════════════════════════════

    /// <summary>"Your daylist refreshed", scheduled with the OS. The rollover moment is KNOWN IN ADVANCE (the daylist
    /// card carries the end of its own window), so this needs no polling and no background task — the same trick as a
    /// pre-save release drop.
    /// <para>Fed from whatever resolves the home feed, i.e. a DATA path, never a render. The tag is keyed on the
    /// WINDOW, so learning the same window twice is idempotent while a genuinely new window REPLACES the entry rather
    /// than stacking a second banner.</para></summary>
    public static class Daylist
    {
        const string Group = "wavee.daylist";
        const string Tag = "daylist-roll";

        /// <summary>Windows silently DROPS a scheduled toast that is due immediately, and a rollover we learn about a
        /// second before it happens is not worth announcing anyway.</summary>
        public static readonly TimeSpan MinLead = TimeSpan.FromMinutes(2);

        static readonly object Gate = new();
        static long s_scheduledFor;      // the window end we currently hold a toast for (0 = none)

        /// <summary>The daylist's current window ends at <paramref name="expiresAtUnixMs"/>. Called whenever the feed
        /// hydrates a daylist card; cheap and idempotent for a window already scheduled.</summary>
        public static void Note(EntityUri context, long expiresAtUnixMs, string? title)
        {
            if (expiresAtUnixMs <= 0) return;
            lock (Gate) { if (s_scheduledFor == expiresAtUnixMs) return; }   // unchanged window: nothing to do
            if (!ToastNotifier.IsSupported) return;

            var policy = Prefs.Policy();
            var level = Prefs.Level(NotifyTopic.DaylistRefresh);
            var due = DateTimeOffset.FromUnixTimeMilliseconds(expiresAtUnixMs).ToLocalTime();

            // Not allowed (master off / dialled below Windows): give back anything we hold and remember NOTHING — so
            // turning the dial back up re-schedules from the next feed resolve.
            if (policy.ScheduleAt(level, due) is not { } deliver)
            {
                Unschedule();
                return;
            }
            if (deliver - DateTimeOffset.Now < MinLead) return;

            try
            {
                ToastNotifier.Default.Unschedule(Tag, Group);   // replace: a new window supersedes the old entry
                var toast = ToastBuilder.Create()
                    .Title(Loc.Get(Strings.Toast.DaylistTitle))
                    // The NEXT window's name is not knowable at schedule time (it is minted when it rolls), so the
                    // body names the window that just ENDED rather than inventing one that might be wrong.
                    .Body(title is { Length: > 0 } t ? Strings.Toast.DaylistMovedOn(t) : Loc.Get(Strings.Toast.DaylistGeneric))
                    .Launch(context.IsValid
                        ? "wavee://open?route=pl&arg=" + Uri.EscapeDataString(context.Text)
                        : "wavee://open?route=home")
                    .Tag(Tag).Group(Group);
                if (!policy.Sound) toast.Silent();

                if (ToastNotifier.Default.Schedule(toast, deliver, Tag, Group))
                    lock (Gate) s_scheduledFor = expiresAtUnixMs;
            }
            catch (Exception ex)
            {
                Log.Warn("notify", "the daylist toast could not be scheduled; the next feed resolve retries", ex);
            }
        }

        /// <summary>Diagnostic seam (Settings ▸ Notifications ▸ Send event): pretend the current window ends at
        /// <paramref name="due"/> and schedule through the REAL <see cref="Note"/> path. Returns the delivery instant
        /// the OS was asked for, or null when the dial would not deliver — deliberately checked HERE so a simulate
        /// never reaches <see cref="Note"/>'s disallowed branch, which REVOKES a genuinely pending real toast.</summary>
        public static DateTimeOffset? SimulateSchedule(DateTimeOffset due, EntityUri context, string? title)
        {
            if (!ToastNotifier.IsSupported) return null;
            var policy = Prefs.Policy();
            if (policy.ScheduleAt(Prefs.Level(NotifyTopic.DaylistRefresh), due) is not { } deliver) return null;

            long ms = due.ToUnixTimeMilliseconds();
            lock (Gate) { if (s_scheduledFor == ms) ms++; }   // an identical window is a no-op in Note; nudge so a repeat press works
            Note(context, ms, title);
            lock (Gate) return s_scheduledFor == ms ? deliver : null;
        }

        /// <summary>Re-check the held entry against the current dials (a notification setting changed). Can only ever
        /// REVOKE here: re-scheduling needs a window end, which arrives with the next feed resolve — so turning the
        /// dial back up quietly re-arms itself rather than guessing at a stale expiry.</summary>
        public static void RequestReconcile()
        {
            if (Prefs.Policy().ScheduleAt(Prefs.Level(NotifyTopic.DaylistRefresh), DateTimeOffset.Now) is null)
                Unschedule();
        }

        /// <summary>Drop the held entry (the dial went down, or sign-out). Never throws.</summary>
        public static void Unschedule()
        {
            lock (Gate)
            {
                if (s_scheduledFor == 0) return;
                s_scheduledFor = 0;
            }
            try { ToastNotifier.Default.Unschedule(Tag, Group); } catch (Exception) { }
        }
    }

    // ══ 4. THE PRE-SAVE RELEASE-DROP SCHEDULER ══════════════════════════════════════════════════════════════════════

    /// <summary>One schedulable drop, as the resolver hands it over. A plain value, so this file does not depend on
    /// owner M's prerelease surface (Wave 5) to compile.</summary>
    /// <param name="PreRelease">The prerelease uri the user pre-saved — the schedule's identity.</param>
    /// <param name="Play">The uri that PLAYS once the record is out. The prerelease id and the album id are unrelated,
    /// so the toast must carry the one the player can actually resolve.</param>
    /// <param name="Name">The record's name; "" falls back to the generic title.</param>
    /// <param name="Artist">The billed artist; "" falls back to the generic body.</param>
    /// <param name="CoverUrl">Remote cover art, or null.</param>
    /// <param name="ReleaseAt">When it drops. Null ⇒ undated, and an undated release is not schedulable.</param>
    /// <param name="IsUpcoming">The authority on "still worth announcing" — a cached link outlives its own release.</param>
    public readonly record struct DropLink(
        EntityUri PreRelease, EntityUri Play, string Name, string Artist, string? CoverUrl,
        DateTimeOffset? ReleaseAt, bool IsUpcoming);

    /// <summary>Pre-save release drops: when the user pre-saves an unreleased album, ask the OS to deliver an "out
    /// now" toast at its release timestamp. This is the ONE notification that must work with Wavee CLOSED, which is
    /// exactly what a SCHEDULED toast is for — the OS owns the timer, so no background process is involved.
    /// <para><b>Reconcile every launch; do not trust the schedule.</b> A scheduled toast outlives the process, so the
    /// OS may hold entries the user has since un-pre-saved, entries for albums that already dropped, and — the common
    /// case — entries whose release date SLIPPED.</para>
    /// <para><b>Known residual (ch 14 DATA GAP 10):</b> the held set is process-lifetime, so a pre-save revoked while
    /// Wavee was CLOSED is never unscheduled — at launch the set is empty and the stale sweep finds nothing to drop.
    /// Fixing it needs the held tags on disk or a <c>RemoveGroup</c> before the re-derive; it is recorded, not
    /// fixed here.</para></summary>
    public static class ReleaseDrops
    {
        /// <summary>Toast group for every scheduled drop, so the whole set can be reasoned about (and cleared) as
        /// one.</summary>
        const string Group = "wavee.release-drops";

        /// <summary>A release is only worth scheduling if it is far enough out that the OS will actually hold the
        /// timer. Windows silently drops a scheduled toast whose delivery time is in the past or all but
        /// immediate.</summary>
        public static readonly TimeSpan MinLead = TimeSpan.FromMinutes(1);

        static readonly object Gate = new();
        static readonly HashSet<string> Scheduled = new(StringComparer.Ordinal);

        /// <summary>The saved pre-releases, resolved. LATE-BOUND on purpose: the library does not exist when
        /// <see cref="HostInstall"/> runs, and a Wave-5 owner attaches this without this file knowing their types.</summary>
        public static Func<IReadOnlyList<DropLink>>? Resolve { get; set; }

        /// <summary>Attach the resolver and reconcile once. Idempotent; safe before login — the first reconcile simply
        /// finds an empty set and does nothing.</summary>
        public static void Attach(Func<IReadOnlyList<DropLink>> resolve)
        {
            Resolve = resolve;
            RequestReconcile();
        }

        /// <summary>Drops are allowed when the Windows channel is on AND the ReleaseDrops topic is dialled to Windows
        /// — the same gate the live escalator uses, read through the ONE accessor so the two can never disagree.</summary>
        static bool Allowed()
            => Prefs.Policy().WindowsEnabled && Prefs.Level(NotifyTopic.ReleaseDrops) == NotifyLevel.Windows;

        /// <summary>A single pre-save flipped. Cheaper than a full reconcile and precise: only the affected album's
        /// toast is touched.</summary>
        public static void OnSavedChanged(in DropLink link, bool saved)
        {
            if (!ToastNotifier.IsSupported || !link.PreRelease.IsValid) return;
            string id = link.PreRelease.Text;
            if (!saved)
            {
                lock (Gate) Scheduled.Remove(id);
                TryUnschedule(TagFor(id));
                return;
            }
            if (!Allowed()) return;
            if (!link.IsUpcoming || link.ReleaseAt is not { } due) return;
            if (due - DateTimeOffset.UtcNow < MinLead) return;
            TrySchedule(in link, due);
        }

        /// <summary>Re-derive the whole scheduled set from the live saved set. Called on every saved-set change, on the
        /// dial being toggled, and once at launch.</summary>
        public static void RequestReconcile()
        {
            if (!ToastNotifier.IsSupported) return;
            if (!Allowed()) { UnscheduleAll(); return; }
            if (Resolve is not { } resolve) return;

            IReadOnlyList<DropLink> links;
            try { links = resolve(); }
            catch (Exception ex) { Log.Warn("notify", "the pre-save set could not be resolved", ex); return; }

            // Un-pre-saved (or already-dropped) entries FIRST, so a slipped date is re-scheduled rather than
            // duplicated.
            var live = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < links.Count; i++)
                if (links[i].PreRelease.IsValid) live.Add(links[i].PreRelease.Text);

            string[] stale;
            lock (Gate)
            {
                var drop = new List<string>();
                foreach (string id in Scheduled) if (!live.Contains(id)) drop.Add(id);
                stale = drop.ToArray();
                foreach (string id in stale) Scheduled.Remove(id);
            }
            foreach (string id in stale) TryUnschedule(TagFor(id));

            for (int i = 0; i < links.Count; i++)
            {
                var link = links[i];
                if (!link.PreRelease.IsValid || !link.IsUpcoming || link.ReleaseAt is not { } due) continue;
                if (due - DateTimeOffset.UtcNow < MinLead) continue;
                TrySchedule(in link, due);
            }
        }

        /// <summary>Drop every scheduled release toast (the dial went off, or sign-out). Never throws.</summary>
        public static void UnscheduleAll()
        {
            string[] held;
            lock (Gate)
            {
                if (Scheduled.Count == 0) return;
                held = new string[Scheduled.Count];
                Scheduled.CopyTo(held);
                Scheduled.Clear();
            }
            foreach (string id in held) TryUnschedule(TagFor(id));
        }

        /// <summary>Build and hand the drop toast to the OS. Returns the instant the OS was actually asked to deliver
        /// at — which is NOT necessarily <paramref name="due"/>, because quiet hours SHIFT it — or null when nothing
        /// was scheduled. Callers enforce the minimum lead time; this deliberately does not, so a diagnostic can drive
        /// it directly.</summary>
        static DateTimeOffset? TrySchedule(in DropLink link, DateTimeOffset due)
        {
            string tag = TagFor(link.PreRelease.Text);
            try
            {
                // REPLACE rather than skip-if-present: the whole point of reconciling is that the date may have moved.
                ToastNotifier.Default.Unschedule(tag, Group);

                string title = link.Name.Length > 0 ? link.Name : Loc.Get(Strings.Toast.NewReleaseTitle);
                string play = (link.Play.IsValid ? link.Play : link.PreRelease).Text;

                var policy = Prefs.Policy();
                var toast = ToastBuilder.Create()
                    .Title(title)
                    .Body(link.Artist.Length > 0 ? Strings.Toast.OutNow(link.Artist) : Loc.Get(Strings.Toast.OutNowGeneric))
                    .Launch("wavee://open?route=album&arg=" + Uri.EscapeDataString(play))
                    .Button(Loc.Get(Strings.Toast.Play), "wavee://play?ctx=" + Uri.EscapeDataString(play))
                    .DismissButton()
                    .Tag(tag)
                    .Group(Group);

                // 0.2.9 never called Silent() here, so with "Play a sound" OFF a release drop still chimed while the
                // other two schedulers honoured the dial. FIXED (plan §9.6 Q3) — a deliberate divergence, so parity
                // item 31 is restated rather than left as a straight port.
                if (!policy.Sound) toast.Silent();

                if (link.CoverUrl is { Length: > 0 } cover)
                {
                    try { toast.Hero(s_toastImages.Localize(cover)); TrimToastImageCache(); }
                    catch (Exception) { /* no hero is fine; the text toast still announces the drop */ }
                }

                // Quiet hours SHIFT a scheduled drop rather than dropping it: the album is still out, the user just
                // hears about it at a civilised hour instead of 03:00.
                var deliver = policy.Quiet.NextAudible(due.ToLocalTime());

                if (!ToastNotifier.Default.Schedule(toast, deliver, tag, Group)) return null;
                lock (Gate) Scheduled.Add(link.PreRelease.Text);
                return deliver;
            }
            catch (Exception ex)
            {
                Log.Warn("notify", "a release drop could not be scheduled; the next launch reconciles", ex);
                return null;
            }
        }

        /// <summary>Diagnostic seam (Settings ▸ Notifications ▸ Send event). The caller owns the lead time — Windows
        /// ACCEPTS a near-immediate schedule and then silently never paints it — so <paramref name="due"/> must be
        /// comfortably in the future. Refuses when the dial would not deliver, so a simulate can never schedule
        /// something the user has switched off.</summary>
        public static DateTimeOffset? SimulateSchedule(in DropLink link, DateTimeOffset due)
        {
            if (!ToastNotifier.IsSupported || !Allowed()) return null;
            return TrySchedule(in link, due);
        }

        static void TryUnschedule(string tag)
        {
            try { ToastNotifier.Default.Unschedule(tag, Group); } catch (Exception) { }
        }

        /// <summary>Stable per-album tag, so a reconcile REPLACES its own earlier entry instead of stacking
        /// duplicates.</summary>
        static string TagFor(string preReleaseUri) => TagIds.Clamp("drop:", preReleaseUri);
    }

    // ══ 5. THE ACTION CENTRE'S HOUSEKEEPING ════════════════════════════════════════════════════════════════════════

    /// <summary>Remove a single live banner (the user dismissed its in-app row). Fail-soft.</summary>
    public static void Dismiss(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        try { ToastNotifier.Default.RemoveByTag("live:" + id, LiveGroup); } catch (Exception) { }
    }

    /// <summary>Clear every live banner (Mark all read). The SCHEDULED groups are untouched — a drop the user has not
    /// received yet is not something they have read.</summary>
    public static void ClearLive()
    {
        try { ToastNotifier.Default.RemoveGroup(LiveGroup); } catch (Exception) { }
    }

    /// <summary>Is the OS willing to show our banners at all? False for an elevated process, and false while the user
    /// has turned Wavee's notifications off in Windows Settings. Only Settings ▸ Notifications surfaces this — there
    /// is deliberately no fallback and no toast about toasts.</summary>
    public static bool OsAllowsToasts()
    {
        if (!ToastNotifier.IsSupported || !s_registered) return false;
        try { return ToastNotifier.Default.Setting == ToastDeliverySetting.Enabled; }
        catch (Exception) { return false; }
    }

    /// <summary>The UI-thread marshal the schedulers use when a resolve completes off-thread. Null before
    /// <see cref="HostInstall"/>; a caller that has no marshal runs inline, which is correct in a test.</summary>
    internal static void ToUi(Action a)
    {
        if (s_post is { } post) post(a);
        else a();
    }

    static int s_reconcilePending;

    /// <summary>Coalesce a reconcile request from any writer (a saved-set change, a dial change) onto one pass. A
    /// heart pressed ten times in a second must not run ten resolves.</summary>
    public static void RequestReconcile()
    {
        if (Interlocked.Exchange(ref s_reconcilePending, 1) != 0) return;
        ToUi(() =>
        {
            Interlocked.Exchange(ref s_reconcilePending, 0);
            ReleaseDrops.RequestReconcile();
            Daylist.RequestReconcile();
        });
    }
}
