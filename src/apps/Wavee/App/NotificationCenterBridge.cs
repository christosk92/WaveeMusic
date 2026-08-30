using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;

namespace Wavee;

/// <summary>
/// The notification-center bridge: merges the four category snapshots (app-update, gander social, what's-new, local
/// activity) into one reactive <see cref="Items"/> list + the bell's <see cref="UnreadCount"/> + the active
/// <see cref="Filter"/>. Subscribes each source's Changed stream and does a full <see cref="Rebuild"/> on the UI thread
/// (≤ ~290 items — a full rebuild is cheap). Read-state: activity uses its per-entry read flag; the remote feeds gate
/// their server "new" flag against the persisted last-seen keys, advanced on panel open, PLUS the per-item
/// <see cref="MarkRead"/> set for rows marked seen one at a time on other surfaces. Provided once at the app root
/// via <see cref="Slot"/>; follows the <see cref="LibraryBridge"/>/<see cref="FriendsBridge"/> pattern end to end.
/// </summary>
public sealed class NotificationCenterBridge
{
    public static readonly Context<NotificationCenterBridge?> Slot = new(null);

    readonly ActivityLog _log;
    readonly ISpotifyNotificationsService _social;
    readonly IWhatsNewService _whatsNew;
    readonly IAppUpdateService _update;
    readonly IAppSettings _settings;
    readonly ActivityUndoExecutor _undo;
    readonly List<IDisposable> _subs = [];
    Action<Action>? _post;
    bool _active;

    /// <summary>Every notification across the four categories, newest-first. The panel filters this by <see cref="Filter"/>.</summary>
    public Signal<IReadOnlyList<WaveeNotification>> Items { get; } = new(Array.Empty<WaveeNotification>());
    /// <summary>Badge-eligible unread count. Local Activity entries remain in the panel but do not increment the bell.</summary>
    public Signal<int> UnreadCount { get; } = new(0);
    /// <summary>The active filter pill (null = All).</summary>
    public Signal<NotificationCategory?> Filter { get; } = new(null);
    /// <summary>Bumped by the panel's 30-second tick while open so relative times recompute per render.</summary>
    public Signal<long> NowTick { get; } = new(0);
    /// <summary>The gander feed's coarse state — lets the panel tell "failed/offline/loading" apart from genuinely empty.</summary>
    public Signal<NotificationFeedState> SocialState { get; } = new(NotificationFeedState.Idle);
    /// <summary>The what's-new feed's coarse state (same purpose as <see cref="SocialState"/>).</summary>
    public Signal<NotificationFeedState> WhatsNewState { get; } = new(NotificationFeedState.Idle);

    /// <summary>How an update toast's "What's new" action navigates: <c>(route, arg)</c>. Set once by the shell (which
    /// owns the history store); null before the shell mounts, and a null navigator simply offers no such action.</summary>
    public Action<string, string?>? Navigate { get; set; }

    /// <summary>0..1 download progress for the sticky update toast's bar. A <see cref="FloatSignal"/> rather than a
    /// re-render: the toast card is mounted once and its indicator width binds to this, so twenty progress ticks cost
    /// twenty float writes instead of twenty reconciles.</summary>
    public FloatSignal UpdateProgress { get; } = new(0f);

    public NotificationCenterBridge(ActivityLog log, ISpotifyNotificationsService social, IWhatsNewService whatsNew,
        IAppUpdateService update, IAppSettings settings, ActivityUndoExecutor undo)
    {
        _log = log;
        _social = social;
        _whatsNew = whatsNew;
        _update = update;
        _settings = settings;
        _undo = undo;
    }

    /// <summary>Subscribe the four Changed streams → a full rebuild on the UI thread. Idempotent; call once from a mount
    /// effect with <c>UsePost()</c>.</summary>
    public void Activate(Action<Action> post)
    {
        if (_active) return;
        _active = true;
        _post = post;
        _subs.Add(_log.Changed.Subscribe(_ => post(Rebuild)));
        _subs.Add(_social.Changed.Subscribe(_ => post(Rebuild)));
        _subs.Add(_whatsNew.Changed.Subscribe(_ => post(Rebuild)));
        _subs.Add(_update.Changed.Subscribe(_ => post(() => { ConsiderUpdateToast(_update.Current); Rebuild(); })));
        Rebuild();
    }

    /// <summary>Run <paramref name="action"/> on the UI thread through the same post delegate the feeds use. The
    /// developer-mode update simulator drives the bridge from a timer thread and needs exactly this and nothing more.
    /// Before <see cref="Activate"/> it runs inline (a headless/CLI process has no UI thread to hop to).</summary>
    public void Post(Action action)
    {
        if (action is null) return;
        if (_post is { } post) post(action);
        else action();
    }

    /// <summary>Panel opened: refetch the remote feeds if stale + advance the last-seen keys (clears remote unread).</summary>
    public void OnPanelOpened()
    {
        _social.EnsureFresh();
        _whatsNew.EnsureFresh();
        AdvanceRemoteLastSeen();
        Rebuild();
    }

    public void SetFilter(NotificationCategory? category) => Filter.Value = category;

    /// <summary>Mark everything read — the activity read flag + advance the remote last-seen keys. (Update clears via its own action.)</summary>
    public void MarkAllRead()
    {
        _log.MarkAllRead();
        // Simulated rows go too. A real app-update row is state-driven and outlives a read (it is dismissed by
        // Acknowledge, not by reading it) — but a SIMULATED one has no state behind it, so without this it would sit in
        // the centre for the rest of the process with no way for the user to clear it.
        _injectedSocial.Clear();
        _injectedNew.Clear();
        _injectedUpdate = null;
        AdvanceRemoteLastSeen();
        Rebuild();
    }

    /// <summary>Mark ONE remote notification read, durably. This is the whole per-item read seam: every surface that
    /// shows a notification row outside this panel (today: Home's what's-new timeline) calls it, and the state lands in
    /// the SAME store the panel and the bell badge read back through <see cref="Rebuild"/> — there is no second
    /// read-state anywhere. A no-op for ids already covered by a watermark or by a previous call, so a repeat click
    /// costs nothing and never republishes.</summary>
    public void MarkRead(string? id)
    {
        if (id is not { Length: > 0 }) return;
        string current = _settings.Get(WaveeSettings.NotificationsReadIds);
        string next = NotificationReadIds.Add(current, id);
        if (string.Equals(next, current, StringComparison.Ordinal)) return;
        _settings.Set(WaveeSettings.NotificationsReadIds, next);
        Rebuild();
    }

    /// <summary>Clear the local activity history (only the Activity category).</summary>
    public void ClearActivity()
    {
        _log.Clear();
        Rebuild();
    }

    /// <summary>Undo an invertible activity entry; a failed undo surfaces a caution toast.</summary>
    public async Task UndoAsync(ActivityEntry entry)
    {
        bool ok = await _undo.UndoAsync(entry).ConfigureAwait(false);
        if (!ok && _post is { } post)
            post(() => Toast.Show(Loc.Get(Strings.Notifications.UndoFailed), new ToastOptions { Severity = InfoBarSeverity.Warning }));
    }

    /// <summary>Undo by ACTIVITY ID — the seam a CONFIRMATION TOAST uses. Undo already existed, but only as a button in
    /// the notification panel, which is the wrong place to look for it: a filing mistake ("wrong playlist", "wrong row")
    /// is noticed immediately, while the user is still on the page they filed from, and the escape has to be in the
    /// confirmation itself. An unknown, aged-out or already-undone id is a NO-OP rather than an error — the panel may have
    /// undone it first, and a toast that has been sitting on screen must not fail loudly for being late.</summary>
    public Task UndoByIdAsync(long id)
    {
        if (id < 0) return Task.CompletedTask;
        var entries = _log.Snapshot;
        for (int i = 0; i < entries.Count; i++)
            if (entries[i].Id == id)
                return entries[i].IsUndoable ? UndoAsync(entries[i]) : Task.CompletedTask;
        return Task.CompletedTask;
    }

    void AdvanceRemoteLastSeen()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _settings.Set(WaveeSettings.NotificationsGanderLastSeenMs, now);
        _settings.Set(WaveeSettings.NotificationsWhatsNewLastSeenMs, now);
        // A watermark advance subsumes every individually-marked id, so the per-item set is dropped rather than kept
        // growing beside a watermark that already covers it. This is what bounds it in practice.
        if (_settings.Get(WaveeSettings.NotificationsReadIds).Length > 0)
            _settings.Set(WaveeSettings.NotificationsReadIds, "");
    }

    // Full merge of the four snapshots → Items + UnreadCount, both on the UI thread. The merge itself is the pure,
    // engine-free NotificationMerge.Build (unit-tested); this method just supplies the live snapshots + settings.
    void Rebuild()
    {
        long ganderSeen = _settings.Get(WaveeSettings.NotificationsGanderLastSeenMs);
        long whatsNewSeen = _settings.Get(WaveeSettings.NotificationsWhatsNewLastSeenMs);

        // App update pinned at the top (state-driven, unread until acknowledged). A SIMULATED update stands in when the
        // real service reports None — there is no other way to exercise that topic (the service is a get-only Null impl).
        AppUpdateNotification? update = _update.Current.State != AppUpdateState.None
            ? new AppUpdateNotification(long.MaxValue, IsUnread: true, _update.Current)
            : _injectedUpdate;

        var (items, unread) = NotificationMerge.Build(
            update, Merged(_social.Snapshot, _injectedSocial), ganderSeen,
            Merged(_whatsNew.Snapshot, _injectedNew), whatsNewSeen, _log.Snapshot,
            _settings.Get(WaveeSettings.NotificationsReadIds));

        // The per-topic dials (Settings → Notifications) decide what the centre may surface at all. Filtered HERE rather
        // than inside NotificationMerge so the merge stays a pure feed fold, and the unread badge is recounted over the
        // visible set — a silenced topic must not keep the bell lit for rows the user cannot see.
        (items, unread) = ApplyTopicVisibility(items, unread);

        Items.Value = items;
        UnreadCount.Value = unread;
        // One escalation point for every LIVE topic: whatever just arrived, is unread, and is dialled to Windows becomes a
        // banner here (watermarked, so a rebuild or relaunch never re-toasts the feed).
        _lastEscalated = ToastEscalator.Consider(_settings, items);
        SocialState.Value = _social.State;
        WhatsNewState.Value = _whatsNew.State;
    }

    // ── the in-app update toast ──────────────────────────────────────────────────────────────────────────────────────
    // ONE card, keyed "update", refreshed in place for the whole lifecycle: available → downloading (sticky, with a
    // live bar) → restarting → updated / failed. The DECISION is AppUpdateToasts.Plan (pure, unit-tested); this method
    // only owns the side effects — the progress signal, the dedupe key and which action the single button carries.

    AppUpdateSnapshot _lastUpdateSnapshot = AppUpdateSnapshot.Idle;
    string _updateProgressTitle = "";

    void ConsiderUpdateToast(AppUpdateSnapshot next)
    {
        if (next is null) return;
        var previous = _lastUpdateSnapshot;
        _lastUpdateSnapshot = next;

        // The bar binds to the signal, so progress must land even when the transition plans no new card.
        UpdateProgress.Value = Math.Clamp(next.ProgressPercent / 100f, 0f, 1f);

        if (AppUpdateToasts.Plan(previous, next) is not { } plan) return;
        // A silenced App-updates dial means the centre never shows the row; the toast must not shout past it either.
        if (NotificationPrefs.Level(_settings, NotifyTopic.AppUpdates) == NotifyLevel.Off) return;

        var action = plan.Actions.Length > 0 ? plan.Actions[0] : (ToastActionKind?)null;
        bool downloading = next.State == AppUpdateState.Downloading;
        if (downloading) _updateProgressTitle = plan.Title;
        Toast.Show(plan.Body, new ToastOptions
        {
            Title = plan.Title.Length > 0 ? plan.Title : null,
            Severity = plan.Severity,
            DurationMs = plan.Sticky ? 0f : 5000f,
            DedupeKey = "update",
            ActionLabel = action is { } a ? AppUpdateToasts.Label(a) : null,
            OnAction = action is { } k ? () => Invoke(k, next) : null,
            CustomContent = downloading ? UpdateProgressCard : null,
        });
    }

    /// <summary>The sticky download card: the planned line plus a live bar. Mounted once per toast; the bar tracks
    /// <see cref="UpdateProgress"/> without a re-render.</summary>
    Element UpdateProgressCard() => new BoxEl
    {
        Direction = 1,
        Gap = 8f,
        Padding = new Edges4(16f, 14f, 16f, 14f),
        Children =
        [
            new TextEl(_updateProgressTitle) { Size = 13f, Weight = 600 },
            ProgressBar.Create(UpdateProgress, 240f),
        ],
    };

    void Invoke(ToastActionKind kind, AppUpdateSnapshot snapshot)
    {
        // The SIMULATOR when one is walking, else the live updater. A toast raised BY a simulation whose button then
        // asked the real (Idle) service to apply an update did nothing at all, which is the one outcome a simulation
        // must never produce. AppUpdateSurface is the single owner of that choice; the panel row and About share it.
        var update = AppUpdateSurface.Resolve(_update);
        if (update is null) return;
        switch (kind)
        {
            case ToastActionKind.UpdateNow:
                _ = update.ApplyAsync(CancellationToken.None);
                break;
            case ToastActionKind.Retry:
                // Retry re-does what FAILED: a check failure has no target yet (nothing to apply), an apply failure
                // has one. Re-applying after a failed check used to be a no-op with a fresh error.
                if (snapshot.TargetQuad is null) _ = update.CheckAsync(UpdateCheckOrigin.User, CancellationToken.None);
                else _ = update.ApplyAsync(CancellationToken.None);
                break;
            case ToastActionKind.Later:
                update.Snooze();
                break;
            case ToastActionKind.WhatsNew:
                Navigate?.Invoke("whatsnew", snapshot.TargetSemVer);
                break;
            case ToastActionKind.OpenReleasePage:
                // One owner for the tag URL: ReleaseNotesText. This method used to carry its own copy.
                ShellOpen.OpenUrl(ReleaseNotesText.ReleasePageUrl(snapshot));
                break;
        }
    }

    // ── simulated events (Settings → Notifications ▸ Send event) ─────────────────────────────────────────────────────
    // Injected rows are merged into the SAME NotificationMerge.Build inputs the feeds fill, so a simulated event is
    // indistinguishable from a real one downstream: identical unread gating, identical topic filtering, identical
    // escalation. That is the entire point — a test affordance that took a shortcut past this method would prove nothing
    // about the pipeline it claims to exercise. Injected at the BRIDGE rather than at a feed service because the live
    // services replace their snapshot wholesale on every fetch (an injected item would vanish on the next panel open)
    // and because a SetInner decorator is discarded by the next login or logout.

    /// <summary>Cap: a simulated row behaves exactly like a real one, including persisting for the session, so repeated
    /// presses must not grow without bound. Oldest is dropped first.</summary>
    const int MaxInjected = 10;

    readonly List<SocialNotification> _injectedSocial = new();
    readonly List<NewReleaseNotification> _injectedNew = new();
    AppUpdateNotification? _injectedUpdate;
    int _lastEscalated;

    /// <summary>Banners raised by the most recent <see cref="Rebuild"/>. Lets the simulate affordance report what
    /// actually happened rather than re-deriving what it believes should have happened.</summary>
    public int LastEscalatedCount => _lastEscalated;

    /// <summary>Push a synthetic notification through the real pipeline and return how many Windows banners it raised
    /// (0 when the topic is dialled below Windows, quiet hours are active, or the row simply never banners).</summary>
    public int Simulate(WaveeNotification notification)
    {
        switch (notification)
        {
            case SocialNotification s: Add(_injectedSocial, s); break;
            case NewReleaseNotification r: Add(_injectedNew, r); break;
            // A simulated update must reach the toast planner too — the strip is half of what the simulation exists to
            // exercise, and the real path plans its card from the service's Changed stream, which a simulation never fires.
            case AppUpdateNotification u: _injectedUpdate = u; ConsiderUpdateToast(u.Snapshot); break;
            default: return 0;      // ActivityNotification arrives via ActivityLog.Record, its own real path
        }
        _lastEscalated = 0;
        Rebuild();
        return _lastEscalated;
    }

    /// <summary>Forget every simulated row (they are session-scoped by nature — nothing persists them).</summary>
    public void ClearSimulated()
    {
        if (_injectedSocial.Count == 0 && _injectedNew.Count == 0 && _injectedUpdate is null) return;
        _injectedSocial.Clear();
        _injectedNew.Clear();
        _injectedUpdate = null;
        Rebuild();
    }

    static void Add<T>(List<T> list, T item)
    {
        if (list.Count >= MaxInjected) list.RemoveAt(0);
        list.Add(item);
    }

    /// <summary>The live snapshot plus any injected rows. Returns the snapshot itself when nothing is injected, so the
    /// default path allocates nothing extra.</summary>
    static IReadOnlyList<T> Merged<T>(IReadOnlyList<T> snapshot, List<T> injected)
    {
        if (injected.Count == 0) return snapshot;
        var all = new List<T>(snapshot.Count + injected.Count);
        all.AddRange(snapshot);
        all.AddRange(injected);
        return all;
    }

    /// <summary>Drop the rows whose TOPIC is dialled Off and recount unread. Returns the input untouched in the common
    /// case (nothing silenced), so the default configuration allocates nothing extra.</summary>
    (IReadOnlyList<WaveeNotification> Items, int Unread) ApplyTopicVisibility(
        IReadOnlyList<WaveeNotification> items, int unread)
    {
        bool anySilenced = false;
        for (int i = 0; i < items.Count; i++)
            if (NotificationPrefs.Level(_settings, NotificationPrefs.TopicOf(items[i])) == NotifyLevel.Off)
            { anySilenced = true; break; }
        if (!anySilenced) return (items, unread);

        var kept = new List<WaveeNotification>(items.Count);
        int keptUnread = 0;
        for (int i = 0; i < items.Count; i++)
        {
            var n = items[i];
            if (NotificationPrefs.Level(_settings, NotificationPrefs.TopicOf(n)) == NotifyLevel.Off) continue;
            kept.Add(n);
            if (n.IsUnread) keptUnread++;
        }
        return (kept, keptUnread);
    }
}
