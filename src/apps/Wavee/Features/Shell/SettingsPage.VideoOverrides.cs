using System;
using System.Collections.Generic;
using System.IO;
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
    bool _voWatchWired;

    // ── the "Manage" flyout: anchor + handle + the deep-link's deferred open ─────────────────────────────────────────
    OverlayHandle? _voHandle;
    NodeHandle _voAnchor;
    bool _voOpenPending;
    Services? _voSvc;
    VideoOverrideService? _voCuration;

    /// <summary>Watch the store's roster sentinel so an attach/remove made anywhere else (the track context menu, an
    /// undo toast) refreshes this list live. Returns the effect's cleanup, so navigating away disposes it.</summary>
    Action? WatchVideoOverrides(Services? svc, Action<Action> post)
    {
        if (_voWatchWired || svc?.RealStore is not { } store) return UnmountVideoOverrides;
        _voWatchWired = true;
        var sub = store.Changes.Subscribe(c => post(() =>
        {
            if (c.IsBulk || string.Equals(c.Uri, VideoOverride.ChangeKey, StringComparison.Ordinal))
                RefreshVideoOverrides(svc, post, force: true);
        }));
        return () => { _voWatchWired = false; sub.Dispose(); UnmountVideoOverrides(); };
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
            _voRows = Array.Empty<VideoOverrideRow>();
            _voLoad.Value = VideoOverrideLoadPhase.Ready;
            return;
        }
        if (_voLoad.Peek() == VideoOverrideLoadPhase.Loading) return;
        if (!force && _voLoad.Peek() == VideoOverrideLoadPhase.Ready) return;
        _voLoad.Value = VideoOverrideLoadPhase.Loading;
        _ = Task.Run(() =>
        {
            IReadOnlyList<VideoOverrideRow> rows;
            try { rows = VideoOverrideUx.BuildRoster(curation, Directory.Exists, svc.RealStore is { } s ? s.GetTrack : null); }
            catch { rows = Array.Empty<VideoOverrideRow>(); }
            post(() =>
            {
                _voRows = rows;
                _voLoad.Value = VideoOverrideLoadPhase.Ready;
                _voVersion.Value = _voVersion.Peek() + 1;
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
                Loc.Get(Strings.VideoOverride.SettingsSub), null, Icons.Movie, isEnabled: false);
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
            Loc.Get(Strings.VideoOverride.SettingsSub), control, Icons.Movie);
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
        actions.Add(Button.Standard(Loc.Get(Strings.VideoOverride.Remove), () =>
        {
            if (!curation.Remove(uri)) return;
            svc?.Log.Event(WaveeLogLevel.Info, "ui", "override.settings.remove", "detached the attached video",
                fields: [WaveeLogField.Of("uri", uri), WaveeLogField.Of("path", path)]);
            Toast.Show(Loc.Get(Strings.VideoOverride.Removed), new ToastOptions
            {
                Severity = InfoBarSeverity.Success,
                ActionLabel = Loc.Get(Strings.VideoOverride.Undo),
                OnAction = () =>
                {
                    try { curation.Attach(uri, path); }
                    catch (Exception ex) { Toast.Show(ex.Message, new ToastOptions { Severity = InfoBarSeverity.Error }); }
                },
            });
        }));

        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinWidth = 0f, Wrap = true,
            Children = actions.ToArray(),
        };
    }

    void PickForRow(Services? svc, VideoOverrideService curation, string uri, string title, string? start)
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

        try { curation.Attach(uri, picked); }
        catch (Exception ex) { Toast.Show(ex.Message, new ToastOptions { Severity = InfoBarSeverity.Error }); return; }
        svc?.Log.Event(WaveeLogLevel.Info, "ui", "override.settings.replace", "replaced the attached video",
            fields: [WaveeLogField.Of("uri", uri), WaveeLogField.Of("path", picked)]);
        Toast.Show(Loc.Get(Strings.VideoOverride.Replaced), new ToastOptions { Severity = InfoBarSeverity.Success });
    }

    void ClearAllVideoOverrides(Services? svc, VideoOverrideService curation)
    {
        var all = curation.All();
        int removed = 0;
        for (int i = 0; i < all.Count; i++)
            if (curation.Remove(all[i].Uri)) removed++;
        svc?.Log.Event(WaveeLogLevel.Info, "ui", "override.settings.clear_all", "detached every attached video",
            fields: [WaveeLogField.Of("count", removed)]);
        Toast.Show(Loc.Get(Strings.VideoOverride.ClearedAll), new ToastOptions { Severity = InfoBarSeverity.Success });
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
