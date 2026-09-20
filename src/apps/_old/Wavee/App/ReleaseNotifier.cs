using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.WindowsApi.Notifications;
using Wavee.Core;
using Wavee.SpotifyLive;

namespace Wavee;

/// <summary>
/// Pre-save release drops (T1.6): when the user pre-saves an unreleased album, ask the OS to deliver an "out now" toast
/// at its release timestamp. This is the one notification that must work with Wavee CLOSED, which is exactly what a
/// SCHEDULED toast is for — the OS owns the timer, so no background process, service or tray resident is involved.
/// </summary>
/// <remarks>
/// <b>Opt-in.</b> Gated on <see cref="WaveeSettings.NotifyReleaseDrops"/> (default off). Turning it off unschedules
/// everything, so the setting is a real switch rather than a promise about future toasts.
///
/// <b>Reconcile every launch, don't trust the schedule.</b> A scheduled toast outlives the process, so the OS may hold
/// entries the user has since un-pre-saved, entries for albums that already dropped, and — the common case — entries
/// whose release date SLIPPED. Every launch therefore rebuilds the set from the live saved-set + a fresh
/// <see cref="IPreReleaseService"/> resolve rather than assuming what was scheduled last time is still right.
///
/// <b>Fail-soft.</b> A notifier that throws must never take playback or startup with it: every OS/WinRT call is wrapped,
/// and a machine where toasts are unavailable (elevated process, no AUMID) simply gets no drops.
/// </remarks>
static class ReleaseNotifier
{
    /// <summary>Toast group for every scheduled drop, so the whole set can be reasoned about (and cleared) as one.</summary>
    const string Group = "wavee.release-drops";

    /// <summary>A release is only worth scheduling if it is far enough out that the OS will actually hold the timer.
    /// Windows silently drops a scheduled toast whose delivery time is in the past or all but immediate.</summary>
    static readonly TimeSpan MinLead = TimeSpan.FromMinutes(1);

    static readonly object Gate = new();
    static readonly HashSet<string> Scheduled = new(StringComparer.Ordinal);   // prerelease uris we hold a toast for
    static IAppSettings? _settings;
    static LibraryBridge? _library;
    static IPreReleaseService? _preRelease;
    static bool _attached;

    /// <summary>Composition-root install. Idempotent. Safe to call before login — the first reconcile simply finds an
    /// empty saved-set and does nothing.</summary>
    public static void Attach(IAppSettings settings, LibraryBridge library, IPreReleaseService preRelease)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(preRelease);
        lock (Gate)
        {
            if (_attached) return;
            _attached = true;
            _settings = settings;
            _library = library;
            _preRelease = preRelease;
        }
        // Launch reconcile: the OS may be holding entries from a previous run whose date slipped, whose album already
        // dropped, or that the user has since un-pre-saved.
        RequestReconcile();
    }

    /// <summary>A single pre-save flipped (called from <see cref="LibraryBridge.SetSaved"/>, the one chokepoint every
    /// heart / menu / drop target goes through). Cheaper than a full reconcile and precise: only the affected album's
    /// toast is touched. Non-prerelease uris are ignored here.</summary>
    public static void OnSavedChanged(string uri, bool saved)
    {
        if (!PreReleaseUris.IsPreRelease(uri)) return;
        IAppSettings? settings;
        IPreReleaseService? svc;
        lock (Gate) { settings = _settings; svc = _preRelease; }
        if (settings is null || svc is null || !ToastNotifier.IsSupported) return;

        if (!saved)
        {
            lock (Gate) Scheduled.Remove(uri);
            TryUnschedule(TagFor(uri));
            return;
        }
        if (!Allowed(settings)) return;
        _ = ScheduleOneAsync(svc, uri);
    }

    /// <summary>Drops are allowed when the Windows channel is on AND the ReleaseDrops topic is dialled to Windows —
    /// the same gate the live escalator uses, read through the one accessor so the two can never disagree.</summary>
    static bool Allowed(IAppSettings settings) =>
        NotificationPrefs.Policy(settings).WindowsEnabled
        && NotificationPrefs.Level(settings, NotifyTopic.ReleaseDrops) == NotifyLevel.Windows;

    static async Task ScheduleOneAsync(IPreReleaseService svc, string uri)
    {
        PreReleaseLink? link;
        try { link = await svc.ResolveAsync(uri).ConfigureAwait(false); }
        catch (Exception) { return; }   // offline: the next launch reconciles
        if (link is null || !link.IsUpcoming || link.ReleaseAt is not { } due) return;
        if (due - DateTimeOffset.UtcNow < MinLead) return;
        TrySchedule(uri, link, due);
    }

    /// <summary>Re-derive the whole scheduled set from the live saved-set. Called on every saved-set change, on the
    /// notification setting being toggled, and (via the attach-time subscription) once at launch.</summary>
    public static void RequestReconcile()
    {
        IAppSettings? settings;
        LibraryBridge? library;
        lock (Gate) { settings = _settings; library = _library; }
        if (settings is null || library is null) return;
        if (!ToastNotifier.IsSupported) return;

        if (!Allowed(settings)) { UnscheduleAll(); return; }
        _ = ReconcileAsync(library.Saved.Peek());
    }

    /// <summary>Drop every scheduled release toast (the setting went off, or sign-out). Never throws.</summary>
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
        foreach (string uri in held) TryUnschedule(TagFor(uri));
    }

    static async Task ReconcileAsync(IReadOnlySet<string> saved)
    {
        IPreReleaseService? svc;
        lock (Gate) svc = _preRelease;
        if (svc is null) return;

        // Un-pre-saved (or already-dropped) entries first, so a slipped date is re-scheduled rather than duplicated.
        string[] stale;
        lock (Gate)
        {
            var drop = new List<string>();
            foreach (string uri in Scheduled) if (!saved.Contains(uri)) drop.Add(uri);
            stale = drop.ToArray();
            foreach (string uri in stale) Scheduled.Remove(uri);
        }
        foreach (string uri in stale) TryUnschedule(TagFor(uri));

        foreach (string uri in saved)
        {
            if (!PreReleaseUris.IsPreRelease(uri)) continue;
            PreReleaseLink? link;
            try { link = await svc.ResolveAsync(uri).ConfigureAwait(false); }
            catch (Exception) { continue; }        // offline / resolve failure: try again next launch
            if (link is null) continue;
            // IsUpcoming is the authority on "still worth announcing" (the kind-138 payload has a 30-day offline TTL, so
            // a cached link outlives its own release); a dated future release is the only schedulable shape.
            if (!link.IsUpcoming || link.ReleaseAt is not { } due) continue;
            if (due - DateTimeOffset.UtcNow < MinLead) continue;
            TrySchedule(uri, link, due);
        }
    }

    /// <summary>Build and hand the drop toast to the OS. Returns the instant the OS was actually asked to deliver at —
    /// which is NOT necessarily <paramref name="due"/>, because quiet hours shift it — or null when nothing was scheduled.
    /// Callers enforce the minimum lead time; this method deliberately does not, so a diagnostic can drive it directly.</summary>
    static DateTimeOffset? TrySchedule(string preReleaseUri, PreReleaseLink link, DateTimeOffset due)
    {
        string tag = TagFor(preReleaseUri);
        try
        {
            // Replace rather than skip-if-present: the whole point of reconciling is that the date may have moved.
            ToastNotifier.Default.Unschedule(tag, Group);

            string title = link.Name is { Length: > 0 } n ? n : "New release";
            string artist = link.Artist?.Name ?? "";
            // The album uri is what plays — the prerelease id and the album id are unrelated, so the toast must carry the
            // one the player can actually resolve once the record is out.
            string playCtx = link.AlbumUri is { Length: > 0 } album ? album : preReleaseUri;

            var toast = ToastBuilder.Create()
                .Title(title)
                .Body(artist.Length > 0 ? artist + " — out now" : "Out now")
                .Launch("wavee://open?route=album&arg=" + Uri.EscapeDataString(playCtx))
                .Button("Play", "wavee://play?ctx=" + Uri.EscapeDataString(playCtx))
                .DismissButton()
                .Tag(tag)
                .Group(Group);

            // A remote cover has to be localized to a file: the AUMID icon/hero path rejects http(s) for an unpackaged app.
            if (link.Cover?.Url is { Length: > 0 } cover)
            {
                try { toast.Hero(ToastImageCache.Default.Localize(cover)); }
                catch (Exception) { /* no hero is fine; the text toast still announces the drop */ }
            }

            // Quiet hours SHIFT a scheduled drop rather than dropping it: the album is still out, the user just hears
            // about it at a civilised hour instead of 03:00.
            IAppSettings? settings;
            lock (Gate) settings = _settings;
            DateTimeOffset deliver = settings is null
                ? due
                : NotificationPrefs.Policy(settings).Quiet.NextAudible(due.ToLocalTime());

            if (!ToastNotifier.Default.Schedule(toast, deliver, tag, Group)) return null;
            lock (Gate) Scheduled.Add(preReleaseUri);
            return deliver;
        }
        catch (Exception)
        {
            // Scheduling is best-effort: the next launch reconciles again.
            return null;
        }
    }

    /// <summary>Diagnostic seam for Settings ▸ Notifications ▸ Send event: schedule <paramref name="link"/> through the
    /// REAL path at <paramref name="due"/>, returning the instant the OS was asked for (quiet hours may have moved it).
    /// The caller owns the lead time — Windows accepts a near-immediate schedule and then silently never paints it, so
    /// <paramref name="due"/> must be comfortably in the future. Refuses when the dial would not deliver, so a simulate
    /// can never schedule something the user has switched off.</summary>
    internal static DateTimeOffset? SimulateSchedule(PreReleaseLink link, DateTimeOffset due)
    {
        IAppSettings? settings;
        lock (Gate) settings = _settings;
        if (settings is null || !Allowed(settings) || !ToastNotifier.IsSupported) return null;
        string uri = link.PreReleaseUri is { Length: > 0 } u ? u : "spotify:prerelease:simulated";
        return TrySchedule(uri, link, due);
    }

    static void TryUnschedule(string tag)
    {
        try { ToastNotifier.Default.Unschedule(tag, Group); } catch (Exception) { }
    }

    /// <summary>Stable per-album tag, so a reconcile REPLACES its own earlier entry instead of stacking duplicates.</summary>
    static string TagFor(string preReleaseUri) => "drop:" + preReleaseUri;
}
