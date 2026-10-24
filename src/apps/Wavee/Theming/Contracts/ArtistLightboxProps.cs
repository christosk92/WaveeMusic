// ── Theming/Contracts/ArtistLightboxProps.cs — the props of the artist gallery's lightbox (themes plan 6.0, T10b2) ────────────────
//
// `Artist.GalleryOpen` (Entities\Artist.UI.cs) keeps what is not a picture: the modal overlay (focus-trapped, no light dismiss), the OPEN state and the
// selected INDEX (one signal per open), the close, the Escape veto's wiring (the overlay asks the view whether Escape was consumed, so a zoomed photo
// unzooms instead of closing) and the export (the Save As picker, the download or copy, the notice). The structure's view draws the pager, the pan/zoom
// mode, the auto-hiding chrome and the filmstrip, and owns their motion.

using FluentGpu.Signals;

namespace Wavee.Theming.Contracts;

/// <summary>The props of <see cref="ViewKeys.ArtistLightbox"/>: one record per open, mount-stable by construction (the overlay mounts one view per open).</summary>
public sealed record ArtistLightboxProps
{
    /// <summary>The photo urls, in gallery order (never empty).</summary>
    public required string[] Photos { get; init; }

    /// <summary>The selected photo (the core's, clamped into the list on open).</summary>
    public required IReadSignal<int> Selected { get; init; }

    /// <summary>Select a photo (the pager's swipe, the arrows, a filmstrip thumb); an unchanged index writes nothing.</summary>
    public required Action<int> Select { get; init; }

    /// <summary>An export is running (the Export button reads "Exporting" and disables).</summary>
    public required IReadSignal<bool> Saving { get; init; }

    /// <summary>Export the photo at an index: the Save As picker, then the original bytes. The view hands its post (the UI-thread marshal) for the
    /// answer.</summary>
    public required Action<int, Action<Action>> Export { get; init; }

    /// <summary>Close the lightbox.</summary>
    public required Action Close { get; init; }

    /// <summary>Called once by the view on mount: the overlay's Escape asks <c>consumeEscape</c> (true vetoes the close: a zoomed photo unzooms) and
    /// the overlay's close runs <c>closed</c> (the view's timers stop).</summary>
    public required Action<Func<bool>, Action> Attach { get; init; }

    /// <summary>Equal only to itself (one record per open).</summary>
    public bool Equals(ArtistLightboxProps? other) => ReferenceEquals(this, other);

    /// <inheritdoc />
    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
}
