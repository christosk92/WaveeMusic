// ── Screens/SettingsDoc.Rows.Storage.cs — the Storage tab as document rows + their verbs ──────────────────────────
//
// Role: CORE (PR C13; extraction X36). On this PC (seven categories, each with its Open-folder door and, for three, a confirmed
// clear) · Playback cache (the two switches, the three-mode budget, the cache location) · Metadata cache · Factory reset.
//
// WHERE THE WORK IS. The census, the clears (off the UI thread, toast, recount), the two-dialog cache relocation and the
// factory reset live in `Settings.Host.cs` (every side effect, off the UI thread where it touches the disk); these rows'
// writers are the verbs that call them, so a view never touches the disk or a cache itself. The census's results are the
// page's state; the rows only carry what to do and when it is allowed.

using FluentGpu.Controls;
using FluentGpu.Localization;
using Wavee.Sdk.Streams;
using T = Wavee.Settings.Tab;
using K = Wavee.SettingsRowKind;

namespace Wavee;

public static partial class SettingsDoc
{
    static IReadOnlyList<SettingsSection> StorageSections() =>
    [
        Sect(T.Storage, "On this PC", Strings.Settings.Storage.OnThisPc, null,
            // Each category's card: the size (the census, once it lands) + "Open folder"; the row's writer is the folder door.
            FolderRow("On this PC", "library", Strings.Settings.Storage.Library, Strings.Settings.Storage.LibrarySub),
            FolderRow("On this PC", "runtime", Strings.Settings.Storage.Runtime, Strings.Settings.Storage.RuntimeSub),
            FolderRow("On this PC", "logs", Strings.Settings.Storage.Logs, null),
            Make(T.Storage, "On this PC", "logsClear", K.Action, Strings.Settings.Storage.DeleteOldLogs,
                SettingsWriter.OfAction("storage.deleteOldLogs", SettingsVerbs.DeleteOldLogs),
                confirm: new SettingsConfirm(Strings.Settings.Storage.DeleteOldLogs, Strings.Settings.Storage.DeleteOldLogsBody,
                    Strings.Settings.Storage.DeleteOldLogs), catalog: false),
            FolderRow("On this PC", "localStore", Strings.Settings.Storage.LocalStore, Strings.Settings.Storage.LocalStoreSub),
            FolderRow("On this PC", "imageCache", Strings.Settings.Storage.ImageCache, Strings.Settings.Storage.ImageCacheSub)),

        Sect(T.Storage, "Playback cache", Strings.Settings.Storage.PlaybackCache, null,
            Make(T.Storage, "Playback cache", "cacheAudio", K.Toggle, Strings.Settings.Storage.CacheAudio,
                SettingsWriter.OfToggle("cache.audio", WriteBool(Platform.Keys.AudioBodyCacheEnabled)),
                captionKey: Strings.Settings.Storage.CacheAudioSub, value: BoolOf(Platform.Keys.AudioBodyCacheEnabled)),
            Make(T.Storage, "Playback cache", "cacheKeys", K.Toggle, Strings.Settings.Storage.CacheKeys,
                SettingsWriter.OfToggle("cache.keys", WriteBool(Platform.Keys.AudioKeyCacheEnabled)),
                captionKey: Strings.Settings.Storage.CacheKeysSub, value: BoolOf(Platform.Keys.AudioKeyCacheEnabled)),
            // The audio-body budget (W17): the mode selector is the row; the fixed-size editor (a preset and a typed size) and
            // the drive-share percent are its sub-rows. Every commit trims to the new policy and re-reads the cache status.
            Make(T.Storage, "Playback cache", "budget", K.Segmented, Strings.Settings.Storage.BodyBudget,
                SettingsWriter.OfChoice("cache.budgetMode", SettingsVerbs.SetBudgetMode),
                captionKey: Strings.Settings.Storage.BodyBudgetSub, value: s => Math.Clamp(s.Get(Platform.Keys.AudioBodyCacheBudgetMode), 0, 2)),
            Make(T.Storage, "Playback cache", "budgetPreset", K.Choice, Strings.Settings.Storage.FixedSize,
                SettingsWriter.OfChoice("cache.budgetPreset", SettingsVerbs.SetBudgetPreset),
                value: s => Settings.StorageFormat.BodyBudgetExactIndex(Math.Max(ChunkDiskCache.MinBudgetBytes, s.Get(Platform.Keys.AudioBodyCacheBudgetBytes))),
                visible: AlwaysVisible, catalog: false),
            Make(T.Storage, "Playback cache", "budgetSize", K.Slider, Strings.Settings.Storage.FixedSize,
                SettingsWriter.OfSlider("cache.budgetSize", v => SettingsVerbs.SetBudgetGiB(v)), catalog: false),
            Make(T.Storage, "Playback cache", "budgetPercent", K.Slider, Strings.Settings.Storage.DriveShare,
                SettingsWriter.OfSlider("cache.budgetPercent", v => SettingsVerbs.SetBudgetPercent(v)),
                value: s => Math.Clamp(s.Get(Platform.Keys.AudioBodyCacheBudgetPercent), 0, 90), catalog: false),
            // The PATH is the description (W16, the live cache directory); the relocation is a two-dialog flow.
            Make(T.Storage, "Playback cache", "cacheLocation", K.Action, Strings.Settings.Storage.ChooseLocation,
                SettingsWriter.OfAction("cache.chooseLocation", SettingsVerbs.ChooseCacheLocation),
                enabled: NeedsAudioCache, silent: true),
            Make(T.Storage, "Playback cache", "cacheLocationDefault", K.Action, Strings.Settings.Storage.UseDefaultLocation,
                SettingsWriter.OfAction("cache.defaultLocation", SettingsVerbs.UseDefaultCacheLocation),
                enabled: NeedsAudioCache, silent: true, catalog: false),
            FolderRow("Playback cache", "audioBodies", Strings.Settings.Storage.AudioBodies, Strings.Settings.Storage.AudioBodiesSub),
            Make(T.Storage, "Playback cache", "audioBodiesClear", K.Action, Strings.Settings.Storage.ClearAudio,
                SettingsWriter.OfAction("storage.clearAudio", SettingsVerbs.ClearAudioBodies),
                enabled: NeedsAudioCache, silent: true,
                confirm: new SettingsConfirm(Strings.Settings.Storage.ClearAudio, Strings.Settings.Storage.ClearAudioBody,
                    Strings.Settings.Storage.ClearAudio), catalog: false),
            FolderRow("Playback cache", "licenseKeys", Strings.Settings.Storage.LicenseKeys, null),
            Make(T.Storage, "Playback cache", "licenseKeysClear", K.Action, Strings.Settings.Storage.ClearKeys,
                SettingsWriter.OfAction("storage.clearKeys", SettingsVerbs.ClearSavedKeys),
                confirm: new SettingsConfirm(Strings.Settings.Storage.ClearKeys, Strings.Settings.Storage.ClearKeysBody,
                    Strings.Settings.Storage.ClearKeys), catalog: false)),

        Sect(T.Storage, "Metadata cache", Strings.Settings.Storage.MetadataCache, null,
            Make(T.Storage, "Metadata cache", "metadataBudget", K.Choice, Strings.Settings.Storage.MetadataBudget,
                SettingsWriter.OfChoice("metadata.budget", SettingsVerbs.SetMetadataBudget),
                captionKey: Strings.Settings.Storage.MetadataBudgetSub,
                value: s => Settings.StorageFormat.MetaBudgetIndex(s.Get(Platform.Keys.MetadataCacheBudgetBytes))),
            // The description is the census sentence (a live string, not a key).
            Make(T.Storage, "Metadata cache", "clearMetadata", K.Action, Strings.Settings.Storage.MetadataCache,
                SettingsWriter.OfAction("storage.clearMetadata", SettingsVerbs.ClearMetadata),
                enabled: f => f.MetadataClearAvailable ? SettingsEnablement.On : SettingsEnablement.Off(null), silent: true,
                confirm: new SettingsConfirm(Strings.Settings.Storage.ClearMetadata, Strings.Settings.Storage.ClearMetadataBody,
                    Strings.Settings.Storage.ClearMetadata))),

        Sect(T.Storage, "Reset", Strings.Settings.Storage.FactoryReset, Strings.Settings.Storage.FactoryResetSub,
            Make(T.Storage, "Reset", "factoryReset", K.Action, Strings.Settings.Storage.FactoryReset,
                SettingsWriter.OfAction("storage.factoryReset", SettingsVerbs.FactoryReset),
                captionKey: Strings.Settings.Storage.FactoryResetRowSub,
                confirm: new SettingsConfirm(Strings.Settings.Storage.FactoryResetConfirmTitle,
                    Strings.Settings.Storage.FactoryResetConfirmBody, Strings.Settings.Storage.FactoryResetAction))),
    ];

    /// <summary>A storage category: its folder door is the writer.</summary>
    static SettingsRow FolderRow(string section, string id, string titleKey, string? captionKey)
        => Make(T.Storage, section, id, K.Action, titleKey, SettingsWriter.OfAction("storage.openFolder:" + id, () => SettingsVerbs.OpenStorageFolder(id)),
            captionKey: captionKey);

    /// <summary>The audio-body cache does not exist under --fake: its verbs grey (no reason of their own).</summary>
    static SettingsEnablement NeedsAudioCache(ISettingsFacts f)
        => f.AudioCacheAvailable ? SettingsEnablement.On : SettingsEnablement.Off(null);
}

public static partial class SettingsVerbs
{
    /// <summary>Reveal a category's folder in Explorer (best-effort; a missing path or Explorer never throws into the UI).</summary>
    public static bool OpenStorageFolder(string rowId)
    {
        string folder = rowId switch
        {
            "runtime" => Settings.RuntimeDirectory(),
            "logs" => Platform.LogFolder,
            "imageCache" => Settings.ImageCacheDirectory(),
            "audioBodies" or "cacheLocation" => Settings.AudioCacheFolder(),
            "licenseKeys" => System.IO.Path.GetDirectoryName(Spotify.Audio.KeyStore.DefaultPath()) ?? Settings.AudioCacheFolder(),
            _ => Platform.LocalFolder,   // library · localStore
        };
        Settings.OpenFolder(folder);
        return false;
    }

    // ── the audio-body budget ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Fixed size · Drive share · Unlimited.</summary>
    public static bool SetBudgetMode(int mode)
    {
        Platform.Settings.Set(Platform.Keys.AudioBodyCacheBudgetMode, Math.Clamp(mode, 0, 2));
        Settings.RefreshAudioStatus();
        return true;
    }

    /// <summary>Persist a fixed byte budget and re-read the cache status (the cache trims to the new policy).</summary>
    public static bool CommitBudgetBytes(long bytes)
    {
        Platform.Settings.Set(Platform.Keys.AudioBodyCacheBudgetBytes, bytes);
        Settings.RefreshAudioStatus();
        return true;
    }

    /// <summary>A preset rung of the fixed-size combo.</summary>
    public static bool SetBudgetPreset(int index)
    {
        if ((uint)index >= (uint)Settings.StorageFormat.BodyBudgetBytes.Length) return false;
        return CommitBudgetBytes(Settings.StorageFormat.BodyBudgetBytes[index]);
    }

    /// <summary>A typed size in GiB (floored at the box's minimum); a non-number writes nothing.</summary>
    public static bool SetBudgetGiB(double gib)
    {
        if (!double.IsFinite(gib)) return false;
        return CommitBudgetBytes(Settings.StorageFormat.GiBToBytes(gib));
    }

    /// <summary>The drive-share percent (0 = automatic 10 %, capped at 90).</summary>
    public static bool SetBudgetPercent(double percent)
    {
        int pct = Math.Clamp((int)Math.Round(double.IsFinite(percent) ? percent : 0), 0, 90);
        Platform.Settings.Set(Platform.Keys.AudioBodyCacheBudgetPercent, pct);
        Settings.RefreshAudioStatus();
        return true;
    }

    /// <summary>The percent a typed value commits as (what the box shows after the write).</summary>
    public static int ClampBudgetPercent(double percent) => Math.Clamp((int)Math.Round(double.IsFinite(percent) ? percent : 0), 0, 90);

    // ── the cache location (the two-dialog flow) and the clears ──────────────────────────────────────────────────────

    public static bool ChooseCacheLocation()
    {
        Settings.PickCacheLocation();
        return false;
    }

    public static bool UseDefaultCacheLocation()
    {
        Settings.OfferRelocation("");
        return false;
    }

    public static bool DeleteOldLogs()
    {
        Settings.DeleteOldLogs();
        return false;
    }

    public static bool ClearAudioBodies()
    {
        Settings.ClearAudioBodies();
        return false;
    }

    public static bool ClearSavedKeys()
    {
        Settings.ClearSavedKeys();
        return false;
    }

    public static bool ClearMetadata()
    {
        Settings.ClearMetadata();
        return false;
    }

    /// <summary>The store sweep reads <see cref="Store.Policy"/> on its next pass; the tick asks for that pass now, so a lowered
    /// ceiling trims without a restart.</summary>
    public static bool SetMetadataBudget(int index)
    {
        if ((uint)index >= (uint)Settings.StorageFormat.MetaBudgetBytes.Length) return false;
        long bytes = Settings.StorageFormat.MetaBudgetBytes[index];
        Platform.Settings.Set(Platform.Keys.MetadataCacheBudgetBytes, bytes);
        Store.Policy = Store.Policy with { ByteBudget = bytes };
        Store.Tick();
        return true;
    }

    /// <summary>After the confirm: arm the reset, spawn the restart broker and end this process.</summary>
    public static bool FactoryReset()
    {
        Settings.RequestFactoryReset();
        return false;
    }

}
