using System;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.Logging;

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

    // TEMP [imgsuspend] diagnostics — homepage "images gone after back-nav" investigation.
    // Reveals whether the global image-load gate is stuck ON when cards stay blank. Remove after.
    private static ILogger? _log;
    private static ILogger? Log => _log ??= Ioc.Default.GetService<ILoggerFactory>()?.CreateLogger("Wavee.ImageSuspend");

    public static bool IsSuspended
    {
        get => _suspended;
        set
        {
            if (_suspended == value) return;
            _suspended = value;
            Log?.LogDebug("[imgsuspend] IsSuspended → {Value}", value);
            try { Changed?.Invoke(value); }
            catch { /* listeners must not bubble through transition code */ }
        }
    }

    public static event Action<bool>? Changed;
}