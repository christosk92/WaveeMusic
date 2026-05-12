using System;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.Services;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace Wavee.UI.WinUI.Helpers.Playback;

/// <summary>
/// Builds the audio / video / subtitle track sub-menus from a live
/// <see cref="MediaPlaybackItem"/>. Shared by every video surface that
/// exposes a "tracks" gear / flyout — the cinematic <c>VideoPlayerPage</c>
/// gear button and the popout's <c>SpotifyVideoQualityFlyout</c> local-content
/// fallback. Keeping the menu population in one place avoids the two surfaces
/// drifting apart (different labels, missed track collections, etc.).
/// </summary>
internal static class MediaTracksMenuBuilder
{
    /// <summary>
    /// Resolves the active <see cref="LocalMediaPlayer.CurrentPlaybackItem"/>
    /// and populates <paramref name="flyout"/> with audio / video / subtitle
    /// sub-menus. Returns <c>true</c> when at least one entry was added;
    /// callers can use that to decide whether to show a "nothing to pick"
    /// fallback item.
    /// </summary>
    public static bool TryPopulateFromActivePlayback(MenuFlyout flyout)
    {
        var local = Ioc.Default.GetService<LocalMediaPlayer>();
        var item = local?.CurrentPlaybackItem;
        if (item is null)
            return false;

        return Populate(flyout, item);
    }

    /// <summary>
    /// Populate <paramref name="flyout"/> from the given playback item. Audio
    /// + video sub-menus appear only when the item exposes more than one
    /// track of that kind. Subtitles always render so the user has a path to
    /// toggle Off and discover the drag-drop hint.
    /// </summary>
    public static bool Populate(MenuFlyout flyout, MediaPlaybackItem item)
    {
        if (item is null) return false;
        var added = false;

        var audioCount = SafeCount(() => item.AudioTracks.Count);
        if (audioCount > 1)
        {
            var audio = new MenuFlyoutSubItem { Text = "Audio" };
            for (int i = 0; i < audioCount; i++)
            {
                var idx = i;
                var t = item.AudioTracks[i];
                var entry = new ToggleMenuFlyoutItem
                {
                    Text = TrackLabel(t.Label, t.Language, i),
                    IsChecked = item.AudioTracks.SelectedIndex == i,
                };
                entry.Click += (_, _) => item.AudioTracks.SelectedIndex = idx;
                audio.Items.Add(entry);
            }
            flyout.Items.Add(audio);
            added = true;
        }

        var videoCount = SafeCount(() => item.VideoTracks.Count);
        if (videoCount > 1)
        {
            var video = new MenuFlyoutSubItem { Text = "Video / Quality" };
            for (int i = 0; i < videoCount; i++)
            {
                var idx = i;
                var t = item.VideoTracks[i];
                var entry = new ToggleMenuFlyoutItem
                {
                    Text = TrackLabel(t.Label, t.Language, i),
                    IsChecked = item.VideoTracks.SelectedIndex == i,
                };
                entry.Click += (_, _) => item.VideoTracks.SelectedIndex = idx;
                video.Items.Add(entry);
            }
            flyout.Items.Add(video);
            added = true;
        }

        // Subtitle menu always renders for video content so the user can
        // toggle "Off" or pick from embedded + side-loaded tracks. Even with
        // zero entries beyond "Off" it advertises that drag-drop subtitle
        // support exists.
        var subs = new MenuFlyoutSubItem { Text = "Subtitles" };
        var off = new ToggleMenuFlyoutItem
        {
            Text = "Off",
            IsChecked = !AnySubtitleSelected(item),
        };
        off.Click += (_, _) =>
        {
            for (uint k = 0; k < item.TimedMetadataTracks.Count; k++)
                item.TimedMetadataTracks.SetPresentationMode(k, TimedMetadataTrackPresentationMode.Disabled);
        };
        subs.Items.Add(off);

        var subCount = SafeCount(() => item.TimedMetadataTracks.Count);
        for (int i = 0; i < subCount; i++)
        {
            var idx = (uint)i;
            var t = item.TimedMetadataTracks[i];
            var entry = new ToggleMenuFlyoutItem
            {
                Text = TrackLabel(t.Label, t.Language, i, fallbackPrefix: "Subtitle"),
                IsChecked = item.TimedMetadataTracks.GetPresentationMode(idx)
                    is TimedMetadataTrackPresentationMode.PlatformPresented
                    or TimedMetadataTrackPresentationMode.ApplicationPresented,
            };
            entry.Click += (_, _) =>
            {
                for (uint k = 0; k < item.TimedMetadataTracks.Count; k++)
                {
                    item.TimedMetadataTracks.SetPresentationMode(k,
                        k == idx
                            ? TimedMetadataTrackPresentationMode.PlatformPresented
                            : TimedMetadataTrackPresentationMode.Disabled);
                }
            };
            subs.Items.Add(entry);
        }

        subs.Items.Add(new MenuFlyoutSeparator());
        subs.Items.Add(new MenuFlyoutItem
        {
            Text = "Drop a .srt / .vtt / .ass file on the player to add a subtitle",
            IsEnabled = false,
        });
        flyout.Items.Add(subs);
        added = true;

        return added;
    }

    private static string TrackLabel(string? label, string? language, int index, string fallbackPrefix = "Track")
        => !string.IsNullOrWhiteSpace(label) ? label!
         : !string.IsNullOrWhiteSpace(language) ? language!
         : $"{fallbackPrefix} {index + 1}";

    private static bool AnySubtitleSelected(MediaPlaybackItem item)
    {
        for (uint i = 0; i < item.TimedMetadataTracks.Count; i++)
        {
            var mode = item.TimedMetadataTracks.GetPresentationMode(i);
            if (mode is TimedMetadataTrackPresentationMode.PlatformPresented
                or TimedMetadataTrackPresentationMode.ApplicationPresented)
                return true;
        }
        return false;
    }

    private static int SafeCount(Func<int> read)
    {
        try { return read(); } catch { return 0; }
    }
}
