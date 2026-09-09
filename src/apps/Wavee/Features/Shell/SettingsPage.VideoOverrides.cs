using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.WindowsApi.Dialogs;
using Wavee.Backend;
using Wavee.Core.Catalog;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// ── Settings → Playback → "Video overrides" — the curation roster ─────────────────────────────────────────────────────
// The answer to "what have I attached, and is it still there?" plus the repair verbs. An async load phase (every row
// stats the disk, and one row can live on an offline share) feeds the PURE VideoOverrideUx.BuildRoster; per-row actions
// go straight through VideoOverrideService — the same mutations the context menu uses, so the surfaces cannot drift.
//
// The settings CARD is a summary (count · Manage · Remove all). The roster itself lives in the anchored
// VideoOverrideManagerFlyout that "Manage" opens — recently-added + search at the root, the full list one drill in.
// This file still owns the data, the status chip and every mutation; the flyout is a presentational shell.
sealed partial class SettingsPage
{
    enum VideoOverrideLoadPhase : byte { NotStarted, Loading, Ready }

    readonly Signal<VideoOverrideLoadPhase> _voLoad = new(VideoOverrideLoadPhase.NotStarted);
    readonly Signal<int> _voVersion = new(0);
    IReadOnlyList<VideoOverrideRow> _voRows = Array.Empty<VideoOverrideRow>();
    bool _voWatchWired, _voActive = true, _voRefreshAgain, _voMetadataPosted;
    long _voGeneration;
    readonly Dictionary<string, QuerySignalBinding<EntityCardSnapshot>> _voQueries = new(StringComparer.Ordinal);

    // ── the "Manage" flyout: anchor + handle + the deep-link's deferred open ─────────────────────────────────────────
    OverlayHandle? _voHandle;
    NodeHandle _voAnchor;
    bool _voOpenPending;
    Services? _voSvc;
    VideoOverrideService? _voCuration;

    /// <summary>Observe durable curation changes; catalog labels are passive typed query projections.</summary>
    Action? WatchVideoOverrides(Services? svc, Action<Action> post)
    {
        if (_voWatchWired || svc?.VideoOverrides is not { } curation) return UnmountVideoOverrides;
        _voWatchWired = true;
        long generation = ++_voGeneration;
        var sub = curation.Changes.Subscribe(_ => post(() =>
        {
            if (!_voWatchWired || generation != _voGeneration) return;
            if (_voActive && _tab.Peek() == TabPlayback) RefreshVideoOverrides(svc, post, force: true);
            else _voRefreshAgain = true;
        }));
        RefreshVideoOverrides(svc, post, force: true);
        return () =>
        {
            _voWatchWired = false; _voGeneration++; _voMetadataPosted = false; _voRefreshAgain = false;
            sub.Dispose();
            foreach (var query in _voQueries.Values) query.Dispose();
            _voQueries.Clear(); _voRows = []; _voLoad.Value = VideoOverrideLoadPhase.NotStarted;
            UnmountVideoOverrides();
        };
    }

    void SetVideoRosterActive(bool active, Services? svc, Action<Action> post)
    {
        _voActive = active;
        foreach (var query in _voQueries.Values) query.SetActive(active && _tab.Peek() == TabPlayback);
        if (active && _tab.Peek() == TabPlayback && _voWatchWired) RefreshVideoOverrides(svc, post, force: _voRefreshAgain);
        if (!active) CloseVideoManager();
    }

    void SyncVideoRosterQueries(Services svc, VideoOverrideService curation, Action<Action> post)
    {
        var wanted = curation.All().Select(row => row.Uri).ToHashSet(StringComparer.Ordinal);
        foreach (string uri in _voQueries.Keys.Where(uri => !wanted.Contains(uri)).ToArray())
        { _voQueries[uri].Dispose(); _voQueries.Remove(uri); }
        foreach (string uri in wanted)
        {
            if (_voQueries.ContainsKey(uri)) continue;
            var binding = new QuerySignalBinding<EntityCardSnapshot>(
                svc.Queries.Acquire(new EntityCardQuery(svc.CatalogScope, uri)), post, _ =>
                {
                    if (_voMetadataPosted || !_voWatchWired) return;
                    _voMetadataPosted = true;
                    long generation = _voGeneration;
                    post(() =>
                    {
                        if (generation != _voGeneration) return;
                        _voMetadataPosted = false;
                        if (!_voActive || !_voWatchWired) return;
                        ApplyVideoRosterLabels();
                    });
                });
            _voQueries.Add(uri, binding);
            // Curation survives account changes. Labels may use the current account's cold cache, but opening
            // Settings never fans out provider requests for the device-wide attachment roster.
            binding.SetDemand(QueryDemand.None);
            binding.SetActive(_voActive && _tab.Peek() == TabPlayback);
        }
    }

    void ApplyVideoRosterLabels()
    {
        _voRows = VideoOverrideUx.WithCatalogLabels(_voRows, uri =>
            _voQueries.TryGetValue(uri, out var binding) && binding.Snapshot.Peek() is { Status.HasPrimaryData: true } snapshot
                ? snapshot.Value : null);
        _voVersion.Value = _voVersion.Peek() + 1;
    }

    /// <summary>Navigating away from Settings must not leave the roster flyout floating over the next page, and must
    /// not leave a deep-link request armed for a page that no longer exists.</summary>
    void UnmountVideoOverrides()
    {
        _voOpenPending = false;
        CloseVideoManager();
    }

    /// <summary>Tab-leave teardown: close the surface and drop the (about-to-be-destroyed) anchor node, but KEEP any
    /// armed deep-link request — the request is what flipped us back to the Playback tab in the first place.</summary>
    void CloseVideoManager()
    {
        _voAnchor = NodeHandle.Null;
        if (_voHandle is { IsOpen: true } open) open.Close();
        _voHandle = null;
    }

    /// <summary>Rebuild the roster off the UI thread (each row probes the filesystem — an unplugged drive can block for
    /// seconds, and a settings tab must not freeze on it).</summary>
    void RefreshVideoOverrides(Services? svc, Action<Action> post, bool force = false)
    {
        if (svc?.VideoOverrides is not { } curation)
        {
            _voRows = [];
            _voLoad.Value = VideoOverrideLoadPhase.Ready;
            return;
        }
        if (!_voWatchWired || !_voActive || _tab.Peek() != TabPlayback) { _voRefreshAgain |= force; return; }
        SyncVideoRosterQueries(svc, curation, post);
        if (_voLoad.Peek() == VideoOverrideLoadPhase.Loading) { _voRefreshAgain |= force; return; }
        if (!force && _voLoad.Peek() == VideoOverrideLoadPhase.Ready) return;
        _voRefreshAgain = false;
        _voLoad.Value = VideoOverrideLoadPhase.Loading;
        long generation = _voGeneration;
        _ = Task.Run(() =>
        {
            IReadOnlyList<VideoOverrideRow>? rows = null;
            try { rows = VideoOverrideUx.BuildRoster(curation, Directory.Exists); }
            catch (Exception error) { svc.Log.Event(WaveeLogLevel.Warning, "ui", "override.roster.failed", error.Message); }
            post(() =>
            {
                if (!_voWatchWired || generation != _voGeneration) return;
                if (rows is not null) _voRows = rows;
                _voLoad.Value = VideoOverrideLoadPhase.Ready;
                if (_voActive) ApplyVideoRosterLabels();
                if (_voRefreshAgain) RefreshVideoOverrides(svc, post, force: true);
            });
        });
    }

    /// <summary>The settings card is now a SUMMARY: count + "Manage" (which opens the anchored roster flyout) + the
    /// bulk detach. The roster itself moved into <see cref="VideoOverrideManagerFlyout"/> — an inline list of every
    /// attachment made the Playback tab scroll for something the user visits rarely, and it had nowhere to put a
    /// search. The row-building / status-chip / mutation logic stays here and is handed to the flyout as delegates,
    /// so the flyout, the settings card and the track context menu cannot drift.</summary>
    Element VideoOverridesGroup(Services? svc)
    {
        _ = _voVersion.Value;                 // re-render when the async load lands / the sentinel fires
        var phase = _voLoad.Value;
        var curation = svc?.VideoOverrides;
        var rows = _voRows;
        _voSvc = svc;
        _voCuration = curation;

        // No curation service (fake backend) → the whole feature is unreachable; say so plainly rather than showing an
        // empty list that never fills.
        if (curation is null)
        {
            _voAnchor = NodeHandle.Null;
            return SettingsRow(Loc.Get(Strings.VideoOverride.SettingsHeader),
                Loc.Get(Strings.VideoOverride.SettingsSub), null,
                SettingsGlyphs.Row(SettingsTab.Playback, "videoOverrides"), isEnabled: false);
        }

        Element control;
        // Spinner ONLY on the cold load. A live re-load (the sentinel fired because the user just removed a row from
        // inside the open flyout) keeps the last roster and therefore keeps the Manage button mounted — swapping it for
        // a spinner would destroy the anchor node the open flyout is hanging off.
        if (phase != VideoOverrideLoadPhase.Ready && rows.Count == 0)
        {
            // No Manage button yet → drop the anchor, so a deep-link that arrives mid-load waits for the real one.
            _voAnchor = NodeHandle.Null;
            control = ProgressRing.Indeterminate(size: 18f);
        }
        else
        {
            var kids = new List<Element>(3)
            {
                new TextEl(rows.Count > 0
                    ? Strings.VideoOverride.SettingsCount(rows.Count)
                    : Loc.Get(Strings.VideoOverride.SettingsEmpty)) { Size = 12f, Color = Tok.TextSecondary },
                // The anchor lives on a wrapper rather than on the button itself: Button owns its own root props.
                new BoxEl
                {
                    Direction = 0, Shrink = 0f,
                    OnRealized = h =>
                    {
                        _voAnchor = h;
                        if (_voOpenPending) _voPost?.Invoke(TryOpenPendingVideoManager);
                    },
                    Children = [Button.Standard(Loc.Get(Strings.VideoOverride.Manage), ToggleVideoOverrideManager)],
                },
            };
            if (rows.Count > 0)
            {
                // Bulk detach is the ONE place a confirm earns its keep (N links at once, no per-row undo).
                kids.Add(Button.Standard(Loc.Get(Strings.VideoOverride.ClearAll), () =>
                    ConfirmThen(Loc.Get(Strings.VideoOverride.ClearAll),
                        Loc.Get(Strings.VideoOverride.ClearAllBody),
                        Loc.Get(Strings.VideoOverride.ClearAll),
                        () => ClearAllVideoOverrides(svc, curation))));
            }
            control = new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Children = kids.ToArray(),
            };
        }

        // The trust disclosure lives in the description, not in a dismissible tip: device-wide, and linked-not-copied.
        return SettingsRow(Loc.Get(Strings.VideoOverride.SettingsHeader),
            Loc.Get(Strings.VideoOverride.SettingsSub), control,
            SettingsGlyphs.Row(SettingsTab.Playback, "videoOverrides"));
    }

    // ── the Manage flyout (the ConcertFilterBar anchored-overlay mechanics) ───────────────────────────────────────────

    /// <summary>Open the roster flyout, or close it when the same button re-opens it (the toggle contract every other
    /// anchored surface in the app uses).</summary>
    void ToggleVideoOverrideManager()
    {
        if (_overlay is not { } overlay || _voCuration is not { } curation) return;
        if (_voHandle is { IsOpen: true } open) { open.Close(); return; }
        var svc = _voSvc;
        _voHandle = overlay.Open(
            () => _voAnchor,
            () => Embed.Comp(() => new VideoOverrideManagerFlyout
            {
                Rows = () => _voRows,
                Version = _voVersion,
                RowActions = row => VideoOverrideRowActions(svc, curation, row),
                StatusChip = VideoStatusChip,
            }),
            FlyoutPlacement.BottomEdgeAlignedLeft,
            new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup)
            {
                ConstrainToRootBounds = true,
            });
        _voHandle.ClosedAction = () => _voHandle = null;
    }

    /// <summary>The "Manage" deep-link (a missing/unplayable toast's action, routed through
    /// <c>PlaybackBridge.OpenVideoOverrides</c>): land on the Playback tab AND open the flyout. The open is deferred
    /// because the Manage button is not realized yet on the frame the tab flips — whichever of the posted retry or the
    /// button's <c>OnRealized</c> gets a live anchor first wins, and the flag makes it happen exactly once.</summary>
    void RequestVideoOverrideManager(Action<Action> post)
    {
        _voOpenPending = true;
        post(TryOpenPendingVideoManager);
    }

    void TryOpenPendingVideoManager()
    {
        if (!_voOpenPending || _voAnchor.IsNull || _overlay is null || _voCuration is null) return;
        _voOpenPending = false;
        if (_voHandle is { IsOpen: true }) return;   // already showing — the request is satisfied
        ToggleVideoOverrideManager();
    }

    /// <summary>The per-row repair verbs, shared by the flyout's search results and its browse-all leaf. Every one of
    /// them goes straight through <see cref="VideoOverrideService"/> — the same mutations the context menu uses.</summary>
    Element VideoOverrideRowActions(Services? svc, VideoOverrideService curation, VideoOverrideRow row)
    {
        string uri = row.Uri;
        string path = row.Path;
        var actions = new List<Element>(4);

        actions.Add(HyperlinkButton.Create(Loc.Get(Strings.VideoOverride.Replace),
            () => PickForRow(svc, curation, uri, Loc.Get(Strings.VideoOverride.PickTitle), start: null)));
        if (row.CanLocate)
            actions.Add(HyperlinkButton.Create(Loc.Get(Strings.VideoOverride.Locate),
                () => PickForRow(svc, curation, uri, Loc.Get(Strings.VideoOverride.LocateTitle),
                    VideoOverrideUx.NearestExistingAncestor(path, Directory.Exists))));
        if (row.CanReveal)
            actions.Add(HyperlinkButton.Create(Loc.Get(Strings.VideoOverride.ShowInExplorer),
                () => ShellOpen.RevealInExplorer(path)));
        actions.Add(Button.Standard(Loc.Get(Strings.VideoOverride.Remove), async () =>
        {
            try { if (!await curation.RemoveAsync(uri).ConfigureAwait(false)) return; }
            catch (Exception ex) { _voPost?.Invoke(() => Toast.Show(ex.Message, new ToastOptions { Severity = InfoBarSeverity.Error })); return; }
            svc?.Log.Event(WaveeLogLevel.Info, "ui", "override.settings.remove", "detached the attached video",
                fields: [WaveeLogField.Of("uri", uri), WaveeLogField.Of("path", path)]);
            _voPost?.Invoke(() => Toast.Show(Loc.Get(Strings.VideoOverride.Removed), new ToastOptions
            {
                Severity = InfoBarSeverity.Success,
                ActionLabel = Loc.Get(Strings.VideoOverride.Undo),
                OnAction = async () =>
                {
                    try { await curation.AttachAsync(uri, path).ConfigureAwait(false); }
                    catch (Exception ex) { _voPost?.Invoke(() => Toast.Show(ex.Message, new ToastOptions { Severity = InfoBarSeverity.Error })); }
                },
            }));
        }));

        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinWidth = 0f, Wrap = true,
            Children = actions.ToArray(),
        };
    }

    async void PickForRow(Services? svc, VideoOverrideService curation, string uri, string title, string? start)
    {
        string? picked;
        try
        {
            picked = FilePicker.OpenFile(FluentApp.WindowHandle, start is null ? title : title + " — " + start,
                new[] { VideoOverrideUx.PickerFilter(Loc.Get(Strings.VideoOverride.Filter)) });
        }
        catch (Exception ex) { Toast.Show(ex.Message, new ToastOptions { Severity = InfoBarSeverity.Error }); return; }
        if (picked is null) return;

        var rejection = VideoOverrideUx.Validate(picked, File.Exists);
        if (rejection != VideoAttachRejection.None)
        {
            // Settings has room for an inline explanation, but the row list is virtual-free and long — a toast keeps the
            // failure attached to the action the user just took (the Storage tab's InfoBar is a TAB-level state).
            Toast.Show(Loc.Get(rejection == VideoAttachRejection.NotMp4
                ? Strings.VideoOverride.RejectedNotMp4
                : Strings.VideoOverride.RejectedNotFound), new ToastOptions { Severity = InfoBarSeverity.Error });
            svc?.Log.Event(WaveeLogLevel.Warning, "ui", "override.attach.rejected", "the picked file was refused",
                fields: [WaveeLogField.Of("path", picked), WaveeLogField.Of("reason", rejection.ToString())]);
            return;
        }

        try { await curation.AttachAsync(uri, picked).ConfigureAwait(false); }
        catch (Exception ex) { _voPost?.Invoke(() => Toast.Show(ex.Message, new ToastOptions { Severity = InfoBarSeverity.Error })); return; }
        svc?.Log.Event(WaveeLogLevel.Info, "ui", "override.settings.replace", "replaced the attached video",
            fields: [WaveeLogField.Of("uri", uri), WaveeLogField.Of("path", picked)]);
        _voPost?.Invoke(() => Toast.Show(Loc.Get(Strings.VideoOverride.Replaced), new ToastOptions { Severity = InfoBarSeverity.Success }));
    }

    async void ClearAllVideoOverrides(Services? svc, VideoOverrideService curation)
    {
        var all = curation.All();
        int removed = 0;
        try
        {
            for (int i = 0; i < all.Count; i++)
                if (await curation.RemoveAsync(all[i].Uri).ConfigureAwait(false)) removed++;
        }
        catch (Exception ex) { _voPost?.Invoke(() => Toast.Show(ex.Message, new ToastOptions { Severity = InfoBarSeverity.Error })); return; }
        svc?.Log.Event(WaveeLogLevel.Info, "ui", "override.settings.clear_all", "detached every attached video",
            fields: [WaveeLogField.Of("count", removed)]);
        _voPost?.Invoke(() => Toast.Show(Loc.Get(Strings.VideoOverride.ClearedAll), new ToastOptions { Severity = InfoBarSeverity.Success }));
    }

    /// <summary>The status chip. Ok is deliberately QUIET (a healthy roster should read as calm); the two repairable
    /// states are caution, and only a file that actually failed to play is critical.</summary>
    static Element VideoStatusChip(VideoOverrideStatus status)
    {
        (string text, ColorF fg, ColorF bg) = status switch
        {
            VideoOverrideStatus.Missing => (Loc.Get(Strings.VideoOverride.StatusMissing), Tok.SystemFillCaution, Tok.SystemFillCautionBackground),
            VideoOverrideStatus.DriveOffline => (Loc.Get(Strings.VideoOverride.StatusDriveOffline), Tok.SystemFillCaution, Tok.SystemFillCautionBackground),
            VideoOverrideStatus.Unplayable => (Loc.Get(Strings.VideoOverride.StatusUnplayable), Tok.SystemFillCritical, Tok.SystemFillCriticalBackground),
            _ => (Loc.Get(Strings.VideoOverride.StatusOk), Tok.TextSecondary, Tok.FillSubtleSecondary),
        };
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center,
            Padding = new Edges4(8f, 3f, 8f, 3f), Corners = CornerRadius4.All(Radii.Full), Fill = bg,
            Children = [new TextEl(text) { Size = 12f, Weight = 600, Color = fg }],
        };
    }
}
