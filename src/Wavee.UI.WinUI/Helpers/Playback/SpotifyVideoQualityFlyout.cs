using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.Services;

namespace Wavee.UI.WinUI.Helpers.Playback;

internal static class SpotifyVideoQualityFlyout
{
    public static void ShowAt(FrameworkElement target)
    {
        var details = Ioc.Default.GetService<ISpotifyVideoPlaybackDetails>();
        var flyout = new MenuFlyout();
        if (details is null || details.AvailableQualities.Count == 0)
        {
            flyout.Items.Add(new MenuFlyoutItem
            {
                Text = "Stream details unavailable",
                IsEnabled = false
            });
            flyout.ShowAt(target);
            return;
        }

        var currentProfileId = details.CurrentQuality?.ProfileId;
        foreach (var option in details.AvailableQualities)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = FormatQualityMenuText(option),
                IsChecked = option.ProfileId == currentProfileId,
                IsEnabled = option.ProfileId != currentProfileId
            };
            item.Click += async (_, _) =>
            {
                try
                {
                    await details.SelectQualityAsync(option.ProfileId);
                }
                catch
                {
                    // The provider logs failures; the flyout should not crash the control surface.
                }
            };
            flyout.Items.Add(item);
        }

        if (details.PlaybackMetadata is { } metadata)
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(new MenuFlyoutItem
            {
                Text = $"{metadata.DrmSystem} - {metadata.Container}",
                IsEnabled = false
            });
            flyout.Items.Add(new MenuFlyoutItem
            {
                Text = $"{metadata.SegmentCount:N0} segments, {metadata.SegmentLengthSeconds}s chunks",
                IsEnabled = false
            });
            flyout.Items.Add(new MenuFlyoutItem
            {
                Text = string.IsNullOrWhiteSpace(metadata.LicenseServerEndpoint)
                    ? "License endpoint unavailable"
                    : "License endpoint provided",
                IsEnabled = false
            });
        }

        flyout.ShowAt(target);
    }

    private static string FormatQualityMenuText(SpotifyVideoQualityOption option)
    {
        var size = option.Width > 0 && option.Height > 0
            ? $"{option.Width}x{option.Height}"
            : "unknown size";
        return $"{option.Label} - {size} - profile {option.ProfileId}";
    }
}
