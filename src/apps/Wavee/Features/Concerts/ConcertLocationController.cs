using System;
using System.Collections.Generic;
using System.Threading;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Pal;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Core;
using Wavee.Features.Concerts;

namespace Wavee;

/// <summary>The shared concert location-picker controller (artist schedule + hub). Owns the live query/results/
/// loading/error signals plus the debounced search, explicit-click geolocation, and save-selection handlers; the
/// presentational <see cref="ConcertLocationPickerPanel"/> stays unchanged and mounts fresh per open. A page owns ONE
/// instance as a field (stable across renders) and re-<see cref="Attach"/>es the current Services/overlay/post/saved
/// callback each render. OS location is requested ONLY from the explicit "Use my location" click.</summary>
sealed class ConcertLocationController
{
    public readonly Signal<string> Query = new("");
    public readonly Signal<IReadOnlyList<ConcertPlace>> Results = new(Array.Empty<ConcertPlace>());
    public readonly Signal<bool> Loading = new(false);
    public readonly Signal<string?> Error = new(null);

    Services? _svc;
    IOverlayService? _overlay;
    Action<Action> _post = static run => run();
    Action<ConcertPlace>? _onSaved;
    CancellationTokenSource? _searchCts;
    OverlayHandle? _handle;

    /// <summary>Re-bind the render-scoped collaborators (called each render; the signal identities never change).
    /// <paramref name="onSaved"/> fires on the UI thread after a successful save, AFTER the picker closes.</summary>
    public void Attach(Services svc, IOverlayService overlay, Action<Action> post, Action<ConcertPlace> onSaved)
    {
        _svc = svc;
        _overlay = overlay;
        _post = post;
        _onSaved = onSaved;
    }

    /// <summary>Call inside <c>UseSignalEffect</c>: reads <see cref="Query"/> (subscribing the effect), debounces, and
    /// cancels the superseded run — the as-you-type city search. Empty query clears results without a network call.</summary>
    public void TrackQuery()
    {
        string q = Query.Value;   // subscribe
        var svc = _svc;
        if (svc is null) return;
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        if (string.IsNullOrWhiteSpace(q))
        {
            _searchCts = null;
            Results.Value = Array.Empty<ConcertPlace>();
            Loading.Value = false;
            return;
        }
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        Loading.Value = true;
        RunSearch(svc, q, cts.Token);
        Reactive.OnCleanup(() => { try { cts.Cancel(); } catch { /* already disposed */ } });
    }

    /// <summary>Open (or close, when already open) the anchored picker. Fresh state each open so a prior session's
    /// query/results/error never bleed into the new one.</summary>
    public void TogglePicker(Func<NodeHandle> anchor)
    {
        if (_svc is null || _overlay is not { } overlay) return;
        if (_handle is { IsOpen: true } open) { open.Close(); return; }

        Query.Value = "";
        Results.Value = Array.Empty<ConcertPlace>();
        Loading.Value = false;
        Error.Value = null;

        _handle = overlay.Open(
            anchor,
            () => Embed.Comp(() => new ConcertLocationPickerPanel
            {
                Query = Query,
                Results = Results,
                Loading = Loading,
                Error = Error,
                Select = place => SelectLocation(place),
                UseMyLocation = UseMyLocation,
            }),
            FlyoutPlacement.BottomEdgeAlignedLeft,
            new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup)
            {
                ConstrainToRootBounds = true,
            });
        _handle.ClosedAction = () => _handle = null;
    }

    async void RunSearch(Services svc, string query, CancellationToken ct)
    {
        var post = _post;
        try
        {
            await System.Threading.Tasks.Task.Delay(220, ct).ConfigureAwait(false);   // debounce keystrokes
            var matches = await svc.Concerts.SearchLocationsAsync(query, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested) return;
            post(() =>
            {
                if (ct.IsCancellationRequested) return;
                Results.Value = matches;
                Loading.Value = false;
            });
        }
        catch (OperationCanceledException) { /* superseded by a newer keystroke / picker closed */ }
        catch
        {
            if (ct.IsCancellationRequested) return;
            post(() =>
            {
                if (ct.IsCancellationRequested) return;
                Loading.Value = false;
                Error.Value = Loc.Get(Strings.Concerts.Location.SearchFailed);
            });
        }
    }

    // Explicit user action only. Runs on the UI thread so the OS consent prompt starts on the foreground thread (the
    // WindowsGeolocationProvider requirement); results marshal back through post.
    async void UseMyLocation()
    {
        var svc = _svc;
        if (svc is null) return;
        var post = _post;

        Error.Value = null;
        Loading.Value = true;   // pre-await: still on the UI thread

        GeolocationResult result;
        try { result = await svc.Geolocation.RequestAsync(GeolocationRequest.Default).ConfigureAwait(false); }
        catch { result = GeolocationResult.Failed; }

        if (!result.IsSuccess)
        {
            post(() =>
            {
                Loading.Value = false;
                Error.Value = LocationErrors.ForStatus(result.Status);
            });
            return;
        }

        var coordinates = new GeoCoordinates(result.Position.Latitude, result.Position.Longitude);
        try
        {
            var matches = await svc.Concerts.ReverseLocationAsync(coordinates).ConfigureAwait(false);
            post(() =>
            {
                Loading.Value = false;
                Results.Value = matches;
                Error.Value = matches.Count == 0 ? Loc.Get(Strings.Concerts.Location.NoMatches) : null;
            });
        }
        catch
        {
            post(() =>
            {
                Loading.Value = false;
                Error.Value = Loc.Get(Strings.Concerts.Location.LookupFailed);
            });
        }
    }

    async void SelectLocation(ConcertPlace place)
    {
        var svc = _svc;
        if (svc is null) return;
        var post = _post;

        if (string.IsNullOrWhiteSpace(place.Id))
        {
            Error.Value = Loc.Get(Strings.Concerts.Location.Invalid);
            return;
        }

        Error.Value = null;
        Loading.Value = true;

        bool ok;
        try { ok = await svc.Concerts.SaveLocationAsync(place.Id).ConfigureAwait(false); }
        catch { ok = false; }

        post(() =>
        {
            Loading.Value = false;
            if (ok)
            {
                _handle?.Close();
                _onSaved?.Invoke(place);
            }
            else
            {
                Error.Value = Loc.Get(Strings.Concerts.Location.SaveFailed);
            }
        });
    }
}
