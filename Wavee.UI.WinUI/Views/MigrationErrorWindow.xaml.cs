using System;
using Microsoft.UI.Xaml;
using Wavee.Core.Storage;
using WinUIEx;

namespace Wavee.UI.WinUI.Views;

/// <summary>
/// Blocking startup window shown when <see cref="MetadataDatabase"/> can't
/// open its SQLite file — schema migration failure, downgrade from a newer
/// Wavee build, or on-disk corruption. The main app is not allowed to
/// proceed until the user chooses Rebuild or Quit.
/// </summary>
public sealed partial class MigrationErrorWindow : WindowEx
{
    private readonly Action _onRebuild;
    private readonly Action _onQuit;
    private bool _dismissed;

    public MigrationErrorWindow(
        MetadataMigrationException error,
        Action onRebuild,
        Action onQuit)
    {
        InitializeComponent();
        _onRebuild = onRebuild;
        _onQuit = onQuit;

        ApplyErrorContent(error);

        // Treat window-close via X as a quit so the app doesn't silently keep
        // running headless in the background.
        Closed += (_, _) =>
        {
            if (!_dismissed) _onQuit();
        };
    }

    private void ApplyErrorContent(MetadataMigrationException error)
    {
        switch (error.Reason)
        {
            case MetadataMigrationFailureReason.Downgrade:
                HeadlineText.Text = "This library cache was written by a newer Wavee";
                ReasonText.Text = $"Database schema v{error.FromVersion} is newer than this build supports (v{error.ToVersion}).";
                ExplainText.Text =
                    "Wavee uses a local cache of your playlists, albums, and artist metadata to keep the app snappy offline. "
                    + "The cache on disk was created by a newer version of Wavee and may contain data this build doesn't know how to read.\n\n"
                    + "Update Wavee to the latest version, or rebuild the cache to downgrade.";
                RebuildButton.Content = "Rebuild library cache";
                break;

            case MetadataMigrationFailureReason.Corrupted:
                HeadlineText.Text = "Library cache is corrupted";
                ReasonText.Text = "The database file exists but can't be read.";
                ExplainText.Text =
                    "This usually means the cache file was partially written during a crash or external process (antivirus, backup tool) modified it. "
                    + "Rebuilding will delete the local cache and refetch your library from Spotify on next use.";
                break;

            case MetadataMigrationFailureReason.MigrationFailed:
            default:
                HeadlineText.Text = "Couldn't upgrade your library cache";
                ReasonText.Text = $"Migration from schema v{error.FromVersion} to v{error.ToVersion} failed.";
                ExplainText.Text =
                    "Wavee tried to update its local metadata cache to the new schema and ran into an error. "
                    + "Your cached playlists / albums / artists are still intact on disk, but the app can't use them until the upgrade completes. "
                    + "Rebuilding will delete the local cache and refetch your library from Spotify on next use.";
                break;
        }

        var inner = error.InnerException?.Message;
        DetailsText.Text = string.IsNullOrEmpty(inner)
            ? error.Message
            : $"{error.Message}\n\n{inner}";

        PreservedText.Text =
            "Your Spotify login, playback history, and app settings are not in this cache and will be preserved.";
    }

    private void OnRebuildClick(object sender, RoutedEventArgs e)
    {
        // Disable both to prevent re-entry while the host is torn down.
        RebuildButton.IsEnabled = false;
        QuitButton.IsEnabled = false;
        _dismissed = true;
        _onRebuild();
        Close();
    }

    private void OnQuitClick(object sender, RoutedEventArgs e)
    {
        RebuildButton.IsEnabled = false;
        QuitButton.IsEnabled = false;
        _dismissed = true;
        _onQuit();
        Close();
    }
}
