using FluentGpu.Signals;

namespace Wavee;

/// <summary>Cross-surface appearance preference epoch. Settings writes bump this once so mounted and KeepAlive-cached
/// player/detail/artist surfaces re-read their persisted appearance flags immediately.</summary>
static class AppearancePrefs
{
    public static readonly Signal<int> Epoch = new(0);
    public static void Bump() => Epoch.Value = Epoch.Peek() + 1;

    /// <summary>Reactive read of the app-wide track-cell artwork policy. The setting store is not observable, so every
    /// mounted consumer takes its update edge from <see cref="Epoch"/> and then re-reads the persisted source of truth.</summary>
    public static bool TrackArtworkHidden(IAppSettings? settings)
    {
        _ = Epoch.Value;
        return settings?.Get(WaveeSettings.HideTrackArtwork) ?? WaveeSettings.HideTrackArtwork.Default;
    }

    /// <summary>Reactive read of the Liked Songs cover treatment (the <see cref="TrackArtworkHidden"/> pattern: Epoch is
    /// the update edge, the settings store is the truth). The persisted int is clamped through
    /// <see cref="LikedCoverRules.FromSetting"/>, so a hand-edited registry value or a downgrade from a build that
    /// shipped more treatments reads as Stock rather than as nothing at all.</summary>
    public static LikedCoverStyle LikedCover(IAppSettings? settings)
    {
        _ = Epoch.Value;
        return LikedCoverRules.FromSetting(
            settings?.Get(WaveeSettings.LikedCoverStyle) ?? WaveeSettings.LikedCoverStyle.Default);
    }
}
