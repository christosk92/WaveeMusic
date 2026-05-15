using System;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Process-wide gate for cached-image loading. Pages flip this on during
/// navigation transitions so realized cards skip the network/cache fetch
/// while a heavy animation is in flight, then flip it off to trigger a
/// reload pass.
///
/// <para>
/// Both the new <c>CompositionImage</c> control and any other consumer
/// that wants to coordinate with this gate subscribe to
/// <see cref="Changed"/>.
/// </para>
/// </summary>
public static class ImageLoadingSuspension
{
    private static bool _suspended;

    public static bool IsSuspended
    {
        get => _suspended;
        set
        {
            if (_suspended == value) return;
            _suspended = value;
            try { Changed?.Invoke(value); }
            catch { /* listeners must not bubble through transition code */ }
        }
    }

    public static event Action<bool>? Changed;
}
