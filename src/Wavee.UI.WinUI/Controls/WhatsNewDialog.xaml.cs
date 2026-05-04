using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.WinUI.Animations;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Wavee.UI.WinUI.Data;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Services;

namespace Wavee.UI.WinUI.Controls;

public sealed partial class WhatsNewDialog : ContentDialog
{
    public IReadOnlyList<ChangelogFeature> Features { get; }
    public string ReleaseTitle { get; }
    public string VersionDisplay { get; }
    public string? Announcement { get; }
    public Visibility HasAnnouncement { get; }
    public string? ReleaseUrl { get; }
    public Visibility HasReleaseUrl { get; }

    public WhatsNewDialog()
    {
        var latestRelease = ChangelogData.Releases.FirstOrDefault();
        Features = latestRelease?.Features ?? [];
        ReleaseTitle = latestRelease?.ReleaseTitle ?? "What's new in Wavee";
        VersionDisplay = latestRelease?.Version ?? "0.0.0";
        Announcement = latestRelease?.Announcement;
        HasAnnouncement = string.IsNullOrEmpty(Announcement) ? Visibility.Collapsed : Visibility.Visible;
        ReleaseUrl = latestRelease?.ReleaseUrl;
        HasReleaseUrl = string.IsNullOrEmpty(ReleaseUrl) ? Visibility.Collapsed : Visibility.Visible;

        InitializeComponent();

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Features.Count > 0)
            FeatureList.SelectedIndex = 0;
    }

    private async void FeatureList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FeatureList.SelectedItem is ChangelogFeature feature)
            await UpdateDetailPanelAsync(feature);
    }

    private async Task UpdateDetailPanelAsync(ChangelogFeature feature)
    {
        // Fade + slide out current content
        await AnimationBuilder.Create()
            .Opacity(to: 0, duration: TimeSpan.FromMilliseconds(120))
            .Translation(Axis.Y, to: 8, duration: TimeSpan.FromMilliseconds(120))
            .StartAsync(DetailPanel);

        // Swap content
        DetailTitle.Text = feature.DetailTitle;
        DetailDescription.Text = feature.DetailDescription;

        if (!string.IsNullOrEmpty(feature.ImageAssetPath))
        {
            // Downsample feature art — packaged assets can be 1000+ px; the
            // dialog renders at ~400 px, so decoding full-res would hold several
            // megabytes of unused bitmap for the dialog's lifetime.
            DetailImage.Source = new BitmapImage(new Uri(feature.ImageAssetPath))
            {
                DecodePixelWidth = 400
            };
            DetailImageBorder.Visibility = Visibility.Visible;
        }
        else
        {
            DetailImageBorder.Visibility = Visibility.Collapsed;
        }

        // Fade + slide in new content
        await AnimationBuilder.Create()
            .Opacity(from: 0, to: 1, duration: TimeSpan.FromMilliseconds(250))
            .Translation(Axis.Y, from: -12, to: 0, duration: TimeSpan.FromMilliseconds(250))
            .StartAsync(DetailPanel);
    }

    private void GotIt_Click(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    private async void ReleaseLink_Click(object sender, RoutedEventArgs e)
    {
        if (ReleaseUrl is not null)
            await Windows.System.Launcher.LaunchUriAsync(new Uri(ReleaseUrl));
    }

    /// <summary>
    /// Shows the dialog if the user hasn't seen the current version's changelog yet.
    /// Also posts a notification to the activity bell.
    /// </summary>
    public static async Task ShowIfNeededAsync(
        XamlRoot xamlRoot,
        ISettingsService settingsService,
        IUpdateService updateService)
    {
        var currentVersion = updateService.CurrentVersion;
        var lastSeen = settingsService.Settings.LastSeenChangelogVersion;

        // Don't show on first install (new user has nothing to compare)
        if (!settingsService.Settings.PlayBehaviorConfigured && lastSeen is null)
        {
            settingsService.Update(s => s.LastSeenChangelogVersion = currentVersion);
            return;
        }

        // Already seen this version
        if (string.Equals(lastSeen, currentVersion, StringComparison.OrdinalIgnoreCase))
            return;

        // Post to activity bell so user can find it later
        var activityService = Ioc.Default.GetService<IActivityService>();
        activityService?.Post(
            category: "app",
            title: $"Wavee updated to v{currentVersion}",
            iconGlyph: "\uE789",
            status: ActivityStatus.Info,
            message: "Tap to see what's new");

        // Show the dialog
        try
        {
            var dialog = new WhatsNewDialog { XamlRoot = xamlRoot };
            await dialog.ShowAsync();
        }
        catch (COMException)
        {
            // Another ContentDialog is already open — skip silently
        }

        settingsService.Update(s => s.LastSeenChangelogVersion = currentVersion);
    }
}
