// ── Entities/Concert.Picker.cs ─────────────────────────────────────────────────────────────────────────────────────
// the location picker: its controller (the live signals and the anchored open) and its core host (the debounced search, the locate flow, the save)
//
// Role: UI (core hosts)
// Owner: N (stream N-C)
// Spec: ch 17 §6 "Location picker", W10
//
// T10b3: the flyout's BODY (the 340×32 search field, "Use my location", the error line, the results and the one-row messages) is the structure's
// (`Concert.PlacePicker`, `ClassicConcertPicker`); this file keeps what decides.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Pal;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using Slots = FluentGpu.Theming.Slots;

namespace Wavee;

public readonly partial struct Concert
{
    /// <summary>The shared picker controller (0.2.9 <c>ConcertLocationController</c>): the live query / results / loading /
    /// error signals and the anchored open. One per page instance; every open RESETS the four signals first, so a prior
    /// session's error never greets the next. Results are PLACE SLOTS the host committed (Match rows).</summary>
    internal sealed class PlacePicker
    {
        public readonly Signal<string> Query = new("");
        public readonly Signal<int[]> Results = new([]);
        public readonly Signal<bool> Loading = new(false);
        public readonly Signal<string?> Error = new(null);
        /// <summary>Fired on the UI thread after a successful save, after the picker closed.</summary>
        public Action? Saved;
        OverlayHandle? _handle;

        public void Toggle(IOverlayService? overlay, Func<NodeHandle> anchor)
        {
            if (Controls.IsNullOverlay(overlay)) return;
            if (_handle is { IsOpen: true } open) { open.Close(); return; }
            Query.Value = "";
            Results.Value = [];
            Loading.Value = false;
            Error.Value = null;
            _handle = overlay.Open(anchor, () => Embed.Comp(() => new PlacePickerHost(this)),
                FlyoutPlacement.BottomEdgeAlignedLeft, FlyoutOptions);
            _handle.ClosedAction = () => _handle = null;
        }

        internal void Close() => _handle?.Close();
    }

    /// <summary>The picker's core host: the debounced search (220 ms; an empty query clears without a call), "Use my location" (it FILLS the results, it never
    /// saves) and the save. It mounts the structure's body (<see cref="ViewKeys.ConcertPlacePicker"/>) with the live signals and the two verbs.</summary>
    [CoreHost]
    sealed class PlacePickerHost(PlacePicker picker) : Component
    {
        int _ticket;
        Action? _useMine;
        Action<int>? _select;
        ConcertPlacePickerProps? _props;

        public override Element Render()
        {
            var post = UsePost();
            var debounced = UseDebouncedValue((IReadSignal<string>)picker.Query, 220f);
            UseSignalEffect(() =>
            {
                string q = debounced.Value;
                Reactive.Untrack(() => Search(q));
            });
            _useMine ??= () => UseMyLocation(post);
            _select ??= Select;
            _props ??= new ConcertPlacePickerProps
            {
                Query = () => picker.Query,
                Results = picker.Results,
                Loading = picker.Loading,
                Error = picker.Error,
                UseMyLocation = _useMine,
                Select = _select,
            };
            return Slots.View(ViewKeys.ConcertPlacePicker, _props);
        }

        void Search(string query)
        {
            int ticket = ++_ticket;
            if (string.IsNullOrWhiteSpace(query))
            {
                picker.Results.Value = [];
                picker.Loading.Value = false;
                return;
            }
            picker.Loading.Value = true;
            ConcertHost.Current.SearchPlaces(query.Trim(), answer =>
            {
                if (ticket != _ticket) return;                    // superseded by a newer keystroke
                picker.Loading.Value = false;
                if (answer.Ok) picker.Results.Value = answer.Places;
                else picker.Error.Value = Loc.Get(Strings.Concerts.Location.SearchFailed);
            });
        }

        /// <summary>Explicit user action only (the OS consent prompt starts here, on the UI thread). No geolocation PAL in
        /// this build ⇒ the honest "unavailable" sentence.</summary>
        async void UseMyLocation(Action<Action> post)
        {
            picker.Error.Value = null;
            if (ConcertHost.Current.Geolocation is not { } geo)
            {
                picker.Error.Value = LocationErrors.ForStatus(GeolocationStatus.Unavailable);
                return;
            }
            picker.Loading.Value = true;
            GeolocationResult fix;
            try { fix = await geo.RequestAsync(GeolocationRequest.Default).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { fix = GeolocationResult.Failed; }
            post(() =>
            {
                if (!fix.IsSuccess)
                {
                    picker.Loading.Value = false;
                    picker.Error.Value = LocationErrors.ForStatus(fix.Status);
                    return;
                }
                ConcertHost.Current.ReversePlaces(fix.Position.Latitude, fix.Position.Longitude, answer =>
                {
                    picker.Loading.Value = false;
                    picker.Results.Value = answer.Places;
                    picker.Error.Value = !answer.Ok ? Loc.Get(Strings.Concerts.Location.LookupFailed)
                        : answer.Places.Length == 0 ? Loc.Get(Strings.Concerts.Location.NoMatches) : null;
                });
            });
        }

        void Select(int slot)
        {
            if (ConcertPlaces.From(slot) is not { } place || string.IsNullOrWhiteSpace(place.Id))
            {
                picker.Error.Value = Loc.Get(Strings.Concerts.Location.Invalid);
                return;
            }
            picker.Error.Value = null;
            picker.Loading.Value = true;
            ConcertHost.Current.SavePlace(slot, ok =>
            {
                picker.Loading.Value = false;
                if (!ok) { picker.Error.Value = Loc.Get(Strings.Concerts.Location.SaveFailed); return; }
                picker.Close();
                picker.Saved?.Invoke();
            });
        }
    }
}
