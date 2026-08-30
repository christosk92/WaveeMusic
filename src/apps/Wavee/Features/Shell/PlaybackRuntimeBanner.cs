using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using Wavee.Backend.Audio;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>Reactive epoch for the banner-dismissed setting: <c>IAppSettings</c> writes aren't signals, so anyone who
/// flips <c>PlaybackRuntimeSetupDismissed</c> (banner ✕, dialog "Not now", Remove) bumps this to make the floating
/// chrome re-evaluate immediately.</summary>
static class PlaybackRuntimeBannerState
{
    public static readonly FluentGpu.Signals.Signal<int> Epoch = new(0);
    public static void Bump() => Epoch.Value++;
}

/// <summary>Persistent caution banner when local PlayPlay runtime is missing or invalid.</summary>
public static class PlaybackRuntimeBanner
{
    public static Element Build(PlaybackRuntimeStatus status, Action onSetUp, Action onDismiss)
    {
        string msg = status.Outcome switch
        {
            ProvisioningOutcome.ArchUnsupported => Loc.Get(Strings.Playback.Runtime.WrongArch),
            ProvisioningOutcome.NoSupportedPack => Loc.Get(Strings.Playback.Runtime.NoPack),
            ProvisioningOutcome.HashMismatch or ProvisioningOutcome.SignatureInvalid
                => Loc.Get(Strings.Playback.Runtime.Unsupported),
            _ => Loc.Get(Strings.Playback.Runtime.Missing),
        };
        // Floats over content (the shell's runtimeBannerLayer): an opaque base under the InfoBar's translucent severity
        // tint (it overlays artwork, not chrome), plus the overlay elevation InfoBar doesn't carry itself.
        return new BoxEl
        {
            Shrink = 0f,
            Fill = Tok.FillSolidBase,
            Corners = CornerRadius4.All(Radii.Control),
            Shadow = Elevation.Flyout,
            ClipToBounds = true,
            Children =
            [
                InfoBar.Create(
                    InfoBarSeverity.Warning,
                    Loc.Get(Strings.Playback.Runtime.Title),
                    msg,
                    onClose: onDismiss,
                    isClosable: true,
                    actionButton: Button.Accent(Loc.Get(Strings.Playback.Runtime.SetUp), onSetUp)),
            ],
        };
    }
}

/// <summary>Mounts the runtime banner + listens for setup-modal open requests.</summary>
sealed class PlaybackRuntimeChrome : Component
{
    readonly IAppSettings _settings;
    public PlaybackRuntimeChrome(IAppSettings settings) => _settings = settings;

    public override Element Render()
    {
        var bridge = UseContext(PlaybackBridge.Slot);
        var services = UseContext(Services.Slot);
        var overlay = UseContext(Overlay.Service);
        var status = bridge?.RuntimeStatus.Value ?? PlaybackRuntimeStatus.NotApplicable;
        _ = PlaybackRuntimeBannerState.Epoch.Value;   // subscribe: re-evaluate when the dismissed setting flips
        bool dismissed = _settings.Get(WaveeSettings.PlaybackRuntimeSetupDismissed);
        bool showBanner = bridge is not null && status.ShowBanner && !dismissed;

        var post = UsePost();
        var setupReq = bridge?.OpenPlaybackRuntimeSetup.Value ?? 0;
        var lastReq = UseRef(setupReq);
        var modal = UseRef<OverlayHandle?>(null);

        void OpenSetup()
        {
            if (bridge is null || services is null) return;
            if (modal.Value is { IsOpen: true }) return;
            services.Log.Event(WaveeLogLevel.Info, "ui", "runtime.banner.open_setup", "Playback runtime setup requested",
                fields:
                [
                    WaveeLogField.Of("status", status.Outcome.ToString()),
                    WaveeLogField.Of("dismissed", dismissed),
                    WaveeLogField.Of("request", setupReq),
                ]);
            var h = PlaybackRuntimeSetupCard.Open(overlay, post, services, _settings, bridge);
            modal.Value = h;
            h.ClosedAction = () => modal.Value = null;
        }

        UseEffect(() =>
        {
            if (bridge is null || setupReq == lastReq.Value) return;
            lastReq.Value = setupReq;
            services?.Log.Event(WaveeLogLevel.Debug, "ui", "runtime.banner.request", "Playback runtime setup request signal changed",
                fields: [WaveeLogField.Of("request", setupReq)]);
            OpenSetup();
        }, setupReq);

        if (!showBanner) return new BoxEl { Shrink = 0f };

        return PlaybackRuntimeBanner.Build(status,
            onSetUp: OpenSetup,
            onDismiss: () =>
            {
                services?.Log.Event(WaveeLogLevel.Info, "ui", "runtime.banner.dismiss", "Playback runtime banner dismissed",
                    fields: [WaveeLogField.Of("status", status.Outcome.ToString())]);
                _settings.Set(WaveeSettings.PlaybackRuntimeSetupDismissed, true);
                PlaybackRuntimeBannerState.Bump();
            });
    }
}
