using System;
using Wavee.Core;

namespace Wavee;

/// <summary>
/// The one place settings become a <see cref="NotificationPolicy"/> + per-topic <see cref="NotifyLevel"/>. Every
/// consumer (the centre's in-app filter, the live toast escalator, the scheduled-drop notifier, the settings tab)
/// reads through here, so "is this topic allowed to make a noise" has exactly one answer.
/// </summary>
static class NotificationPrefs
{
    /// <summary>Bumped on every write so mounted surfaces (the settings tab, the centre) re-read. The
    /// <c>AppearancePrefs</c> pattern.</summary>
    public static readonly FluentGpu.Signals.Signal<int> Epoch = new(0);

    public static void Bump() => Epoch.Value = Epoch.Peek() + 1;

    /// <summary>Every topic, in the order the settings page lists them (declaration order IS the UI order — one list, so
    /// a new topic cannot be added to the enum and forgotten in the UI).</summary>
    public static readonly NotifyTopic[] AllTopics =
    [
        NotifyTopic.NewAlbums,
        NotifyTopic.NewEpisodes,
        NotifyTopic.ReleaseDrops,
        NotifyTopic.Concerts,
        NotifyTopic.Followers,
        NotifyTopic.DaylistRefresh,
        NotifyTopic.AppUpdates,
        NotifyTopic.LibraryActivity,
    ];

    static SettingKey<int> KeyFor(NotifyTopic topic) => topic switch
    {
        NotifyTopic.NewAlbums => WaveeSettings.NotifyNewAlbums,
        NotifyTopic.NewEpisodes => WaveeSettings.NotifyNewEpisodes,
        NotifyTopic.ReleaseDrops => WaveeSettings.NotifyReleaseDrops,
        NotifyTopic.Concerts => WaveeSettings.NotifyConcerts,
        NotifyTopic.Followers => WaveeSettings.NotifyFollowers,
        NotifyTopic.DaylistRefresh => WaveeSettings.NotifyDaylist,
        NotifyTopic.AppUpdates => WaveeSettings.NotifyAppUpdates,
        _ => WaveeSettings.NotifyLibraryActivity,
    };

    /// <summary>The stored level for <paramref name="topic"/>, clamped to what the topic can actually reach.</summary>
    public static NotifyLevel Level(IAppSettings? settings, NotifyTopic topic)
    {
        if (settings is null) return NotificationPolicy.DefaultFor(topic);
        int raw = settings.Get(KeyFor(topic));
        var level = raw is >= 0 and <= 2 ? (NotifyLevel)raw : NotificationPolicy.DefaultFor(topic);
        return NotificationPolicy.Clamp(topic, level);
    }

    public static void SetLevel(IAppSettings? settings, NotifyTopic topic, NotifyLevel level)
    {
        if (settings is null) return;
        settings.Set(KeyFor(topic), (int)NotificationPolicy.Clamp(topic, level));
        Bump();
    }

    /// <summary>The global gates.</summary>
    public static NotificationPolicy Policy(IAppSettings? settings)
    {
        if (settings is null) return new NotificationPolicy(false, true, QuietHours.Off);
        return new NotificationPolicy(
            settings.Get(WaveeSettings.NotifyWindows),
            settings.Get(WaveeSettings.NotifySound),
            new QuietHours(
                settings.Get(WaveeSettings.NotifyQuietEnabled),
                settings.Get(WaveeSettings.NotifyQuietFromHour),
                settings.Get(WaveeSettings.NotifyQuietToHour)).Normalized());
    }

    /// <summary>Which notification categories the in-app centre may surface, derived from the per-topic dials. A category
    /// stays visible while ANY of its topics is above Off — the centre's pills are coarser than the dials, so hiding
    /// "Spotify" because followers were silenced would also hide the concerts the user still wants.</summary>
    public static bool ShowsCategory(IAppSettings? settings, NotificationCategory category) => category switch
    {
        NotificationCategory.NewRelease => Level(settings, NotifyTopic.NewAlbums) != NotifyLevel.Off
                                        || Level(settings, NotifyTopic.NewEpisodes) != NotifyLevel.Off,
        NotificationCategory.Social => Level(settings, NotifyTopic.Concerts) != NotifyLevel.Off
                                    || Level(settings, NotifyTopic.Followers) != NotifyLevel.Off,
        NotificationCategory.AppUpdate => Level(settings, NotifyTopic.AppUpdates) != NotifyLevel.Off,
        _ => Level(settings, NotifyTopic.LibraryActivity) != NotifyLevel.Off,
    };

    /// <summary>The topic a concrete centre row belongs to — the fine-grained answer the display category cannot give
    /// (its "New" pill covers albums AND episodes; its "Spotify" pill covers concerts AND followers).</summary>
    public static NotifyTopic TopicOf(WaveeNotification n) => n switch
    {
        NewReleaseNotification r => r.Kind == NewReleaseKind.Episode ? NotifyTopic.NewEpisodes : NotifyTopic.NewAlbums,
        SocialNotification s => SpotifyUpdates.IsConcert(s) ? NotifyTopic.Concerts : NotifyTopic.Followers,
        AppUpdateNotification => NotifyTopic.AppUpdates,
        _ => NotifyTopic.LibraryActivity,
    };
}
