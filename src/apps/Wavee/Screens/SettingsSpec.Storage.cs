// ── Screens/SettingsSpec.Storage.cs — Storage: what Wavee keeps on this PC (the dials and their verbs) ─────────────────────────
//
// Role: CORE (settings rework phase 1). Music on this PC (#240 S3: the music folders, their three buttons, watching and keeping missing
// songs; the words every theme shows are `SettingsMusic`'s) · On this PC (the census categories, each with its Open-folder door and, for two, a confirmed
// clear or remove) · Playback cache (the two switches, the three-mode budget, the cache location, the two stores) · Metadata cache ·
// Reset (factory reset).
//
// WHERE THE WORK IS. The census, the clears (off the UI thread, toast, recount), the two-dialog cache relocation and the factory reset
// live in `Settings.Host.cs` (every side effect, off the UI thread where it touches the disk); these dials' writers are the verbs that
// call them, so a view never touches the disk or a cache itself. The census's results are the page's state; the dials only carry what
// to do and when it is allowed.

using FluentGpu.Controls;
using FluentGpu.Localization;
using Wavee.Sdk.Streams;
using K = Wavee.SettingsRowKind;

namespace Wavee;

public static partial class SettingsSpec
{
    static SettingsCategory StorageCategory() => new("storage", Strings.Settings.Categories.Storage.Title,
        Strings.Settings.Categories.Storage.Strip, Strings.Settings.Categories.Storage.Subtitle, "Folder",
    [
        // #240 S3: the music folders Wavee plays (the local library). The folder list with its per-folder verbs (show in Explorer, rescan
        // this folder, remove) is one composite drawn by each theme from `SettingsMusic`; its three buttons are child dials. "More options"
        // is a read-only parent whose children are the two library settings (the views draw it as an expander; Zune draws its rows).
        Group("music", Strings.Settings.Groups.Storage.Music, null,
            Make("storage.music.folders", K.Composite, Strings.Settings.Music.Folders,
                SettingsWriter.Owned("SettingsVerbs.RemoveMusicFolder/RevealMusicFolder/RescanMusicFolder"), glyph: "Folder",
                captionKey: Strings.Settings.Music.FoldersSub, aliases: ["local files", "mp3", "flac", "ogg", "m4a", "nas"]),
            Make("storage.music.add", K.Action, Strings.Settings.Music.Add,
                SettingsWriter.OfAction("music.addFolder", SettingsVerbs.AddMusicFolder), parent: "storage.music.folders", glyph: "FolderOpen"),
            Make("storage.music.addDefault", K.Action, Strings.Settings.Music.AddDefault,
                SettingsWriter.OfAction("music.addDefault", SettingsVerbs.AddDefaultMusicFolder), parent: "storage.music.folders", glyph: "MusicNote",
                visible: static f => f.DefaultMusicFolderOffered),
            Make("storage.music.rescan", K.Action, Strings.Settings.Music.Rescan,
                SettingsWriter.OfAction("music.rescan", SettingsVerbs.RescanMusic), parent: "storage.music.folders", glyph: "Refresh",
                enabled: static f => f.MusicFolderCount > 0 ? SettingsEnablement.On : SettingsEnablement.Off(null), silent: true),
            Make("storage.music.options", K.Fact, Strings.Settings.Music.Options, SettingsWriter.ReadOnly, glyph: "Equalizer",
                captionKey: Strings.Settings.Music.OptionsSub),
            Make("storage.music.watch", K.Toggle, Strings.Settings.Music.Watch,
                SettingsWriter.OfToggle("music.watch", SettingsVerbs.SetMusicWatch), parent: "storage.music.options", glyph: "Bell",
                captionKey: Strings.Settings.Music.WatchSub, value: BoolOf(Platform.Keys.MusicWatch)),
            Make("storage.music.keepMissing", K.Choice, Strings.Settings.Music.KeepMissing,
                SettingsWriter.OfChoice("music.keepMissing", SettingsVerbs.SetMusicMissingDays), parent: "storage.music.options", glyph: "Clock",
                captionKey: Strings.Settings.Music.KeepMissingSub, value: static s => SettingsMusic.MissingDaysIndex(s.Get(Platform.Keys.MusicMissingDays)),
                choices: [Strings.Settings.Music.Days30, Strings.Settings.Music.Days60, Strings.Settings.Music.Days90])),

        Group("onThisPc", Strings.Settings.Groups.Storage.OnThisPc, null,
            // Each category's card: the size (the census, once it lands) + "Open folder"; the dial's writer is the folder door.
            FolderDial("storage.library", "library", Strings.Settings.Storage.Library, Strings.Settings.Storage.LibrarySub, "Album"),
            FolderDial("storage.runtime", "runtime", Strings.Settings.Storage.Runtime, Strings.Settings.Storage.RuntimeSub, "Code"),
            FolderDial("storage.logs", "logs", Strings.Settings.Storage.Logs, null, "Document"),
            Make("storage.logs.clear", K.Action, Strings.Settings.Storage.DeleteOldLogs,
                SettingsWriter.OfAction("storage.deleteOldLogs", SettingsVerbs.DeleteOldLogs), legacy: ["logsClear"], parent: "storage.logs",
                confirm: new SettingsConfirm(Strings.Settings.Storage.DeleteOldLogs, Strings.Settings.Storage.DeleteOldLogsBody,
                    Strings.Settings.Storage.DeleteOldLogs)),
            FolderDial("storage.localStore", "localStore", Strings.Settings.Storage.LocalStore, Strings.Settings.Storage.LocalStoreSub, "Folder"),
            FolderDial("storage.imageCache", "imageCache", Strings.Settings.Storage.ImageCache, Strings.Settings.Storage.ImageCacheSub, "Picture"),
            // The on-device AI lyrics models: the door is the card's own (AiLyrics.OpenFolder), the remove the card's own verb.
            FolderDial("storage.aiModels", "aiModels", Strings.Settings.Storage.AiModels, Strings.Settings.Storage.AiModelsSub, "RefineSparkle"),
            Make("storage.aiModels.remove", K.Action, Strings.Settings.Storage.RemoveAiModels,
                SettingsWriter.OfAction("storage.removeAiModels", SettingsVerbs.RemoveAiModels), legacy: ["aiModelsRemove"], parent: "storage.aiModels",
                enabled: f => f.AiModelsPresent ? SettingsEnablement.On : SettingsEnablement.Off(null), silent: true,
                // The dialog's button is the card's own "Remove": "Remove AI models" ellipsizes in the dialog's button lane.
                confirm: new SettingsConfirm(Strings.Settings.Storage.RemoveAiModels, Strings.Settings.Storage.RemoveAiModelsBody,
                    Strings.Settings.Lyrics.Ai.RemoveConfirm.Action)),
            // "FolderOpen", not "ThisPc": the old section header owned ThisPc.
            FolderDial("storage.otherData", "otherData", Strings.Settings.Storage.OtherData, Strings.Settings.Storage.OtherDataSub, "FolderOpen")),

        Group("playbackCache", Strings.Settings.Groups.Storage.PlaybackCache, null,
            Make("storage.cache.audio", K.Toggle, Strings.Settings.Storage.CacheAudio,
                SettingsWriter.OfToggle("cache.audio", WriteBool(Platform.Keys.AudioBodyCacheEnabled)), legacy: ["cacheAudio"], glyph: "Audio",
                captionKey: Strings.Settings.Storage.CacheAudioSub, value: BoolOf(Platform.Keys.AudioBodyCacheEnabled)),
            // "Document", not "Tag": License keys keeps Tag; the two would collide within this one group.
            Make("storage.cache.keys", K.Toggle, Strings.Settings.Storage.CacheKeys,
                SettingsWriter.OfToggle("cache.keys", WriteBool(Platform.Keys.AudioKeyCacheEnabled)), legacy: ["cacheKeys"], glyph: "Document",
                captionKey: Strings.Settings.Storage.CacheKeysSub, value: BoolOf(Platform.Keys.AudioKeyCacheEnabled)),
            // The audio-body budget (W17): the mode selector is the dial; the fixed-size editor (a preset and a typed size) and the
            // drive-share percent are its children. Every commit trims to the new policy and re-reads the cache status.
            Make("storage.cache.budget", K.Segmented, Strings.Settings.Storage.BodyBudget,
                SettingsWriter.OfChoice("cache.budgetMode", SettingsVerbs.SetBudgetMode), legacy: ["budget"], glyph: "RowSize",
                captionKey: Strings.Settings.Storage.BodyBudgetSub, value: s => Math.Clamp(s.Get(Platform.Keys.AudioBodyCacheBudgetMode), 0, 2),
                choices: [Strings.Settings.Storage.FixedSize, Strings.Settings.Storage.DriveShare, Strings.Settings.Storage.Unlimited]),
            Make("storage.cache.budget.preset", K.Choice, Strings.Settings.Storage.FixedSize,
                SettingsWriter.OfChoice("cache.budgetPreset", SettingsVerbs.SetBudgetPreset), legacy: ["budgetPreset"], parent: "storage.cache.budget",
                value: s => Settings.StorageFormat.BodyBudgetExactIndex(Math.Max(ChunkDiskCache.MinBudgetBytes, s.Get(Platform.Keys.AudioBodyCacheBudgetBytes)))),
            Make("storage.cache.budget.size", K.Slider, Strings.Settings.Storage.FixedSize,
                SettingsWriter.OfSlider("cache.budgetSize", v => SettingsVerbs.SetBudgetGiB(v)), legacy: ["budgetSize"], parent: "storage.cache.budget"),
            Make("storage.cache.budget.percent", K.Slider, Strings.Settings.Storage.DriveShare,
                SettingsWriter.OfSlider("cache.budgetPercent", v => SettingsVerbs.SetBudgetPercent(v)), legacy: ["budgetPercent"], parent: "storage.cache.budget",
                value: s => Math.Clamp(s.Get(Platform.Keys.AudioBodyCacheBudgetPercent), 0, 90)),
            // The PATH is the description (W16, the live cache directory); the relocation is a two-dialog flow.
            Make("storage.cache.location", K.Action, Strings.Settings.Storage.ChooseLocation,
                SettingsWriter.OfAction("cache.chooseLocation", SettingsVerbs.ChooseCacheLocation), legacy: ["cacheLocation"], glyph: "FolderOpen",
                enabled: NeedsAudioCache, silent: true),
            Make("storage.cache.location.default", K.Action, Strings.Settings.Storage.UseDefaultLocation,
                SettingsWriter.OfAction("cache.defaultLocation", SettingsVerbs.UseDefaultCacheLocation), legacy: ["cacheLocationDefault"],
                parent: "storage.cache.location", enabled: NeedsAudioCache, silent: true),
            FolderDial("storage.cache.audioBodies", "audioBodies", Strings.Settings.Storage.AudioBodies, Strings.Settings.Storage.AudioBodiesSub, "MusicNote"),
            Make("storage.cache.audioBodies.clear", K.Action, Strings.Settings.Storage.ClearAudio,
                SettingsWriter.OfAction("storage.clearAudio", SettingsVerbs.ClearAudioBodies), legacy: ["audioBodiesClear"], parent: "storage.cache.audioBodies",
                enabled: NeedsAudioCache, silent: true,
                confirm: new SettingsConfirm(Strings.Settings.Storage.ClearAudio, Strings.Settings.Storage.ClearAudioBody,
                    Strings.Settings.Storage.ClearAudio)),
            FolderDial("storage.cache.licenseKeys", "licenseKeys", Strings.Settings.Storage.LicenseKeys, null, "Tag"),
            Make("storage.cache.licenseKeys.clear", K.Action, Strings.Settings.Storage.ClearKeys,
                SettingsWriter.OfAction("storage.clearKeys", SettingsVerbs.ClearSavedKeys), legacy: ["licenseKeysClear"], parent: "storage.cache.licenseKeys",
                confirm: new SettingsConfirm(Strings.Settings.Storage.ClearKeys, Strings.Settings.Storage.ClearKeysBody,
                    Strings.Settings.Storage.ClearKeys))),

        Group("metadataCache", Strings.Settings.Groups.Storage.MetadataCache, null,
            Make("storage.metadata.budget", K.Choice, Strings.Settings.Storage.MetadataBudget,
                SettingsWriter.OfChoice("metadata.budget", SettingsVerbs.SetMetadataBudget), legacy: ["metadataBudget"], glyph: "RowSize",
                captionKey: Strings.Settings.Storage.MetadataBudgetSub,
                value: s => Settings.StorageFormat.MetaBudgetIndex(s.Get(Platform.Keys.MetadataCacheBudgetBytes))),
            // The description is the census sentence (a live string, not a key).
            Make("storage.metadata.clear", K.Action, Strings.Settings.Storage.MetadataCache,
                SettingsWriter.OfAction("storage.clearMetadata", SettingsVerbs.ClearMetadata), legacy: ["clearMetadata"], glyph: "Delete",
                enabled: f => f.MetadataClearAvailable ? SettingsEnablement.On : SettingsEnablement.Off(null), silent: true,
                confirm: new SettingsConfirm(Strings.Settings.Storage.ClearMetadata, Strings.Settings.Storage.ClearMetadataBody,
                    Strings.Settings.Storage.ClearMetadata))),

        Group("reset", Strings.Settings.Groups.Storage.Reset, Strings.Settings.Storage.FactoryResetSub,
            // "Attention", not "Delete": the old section owned Delete.
            Make("storage.factoryReset", K.Action, Strings.Settings.Storage.FactoryReset,
                SettingsWriter.OfAction("storage.factoryReset", SettingsVerbs.FactoryReset), legacy: ["factoryReset"], glyph: "Attention",
                captionKey: Strings.Settings.Storage.FactoryResetRowSub,
                confirm: new SettingsConfirm(Strings.Settings.Storage.FactoryResetConfirmTitle,
                    Strings.Settings.Storage.FactoryResetConfirmBody, Strings.Settings.Storage.FactoryResetAction))),
    ]);

    /// <summary>A storage category: its folder door is the writer (the verb keys the folder by the old row id).</summary>
    static SettingsDial FolderDial(string id, string rowId, string titleKey, string? captionKey, string glyph)
        => Make(id, K.Action, titleKey, SettingsWriter.OfAction("storage.openFolder:" + rowId, () => SettingsVerbs.OpenStorageFolder(rowId)),
            legacy: [rowId], glyph: glyph, captionKey: captionKey);

    /// <summary>The audio-body cache does not exist under --fake: its verbs grey (no reason of their own).</summary>
    static SettingsEnablement NeedsAudioCache(ISettingsFacts f)
        => f.AudioCacheAvailable ? SettingsEnablement.On : SettingsEnablement.Off(null);
}

public static partial class SettingsVerbs
{
    /// <summary>Reveal a category's folder in Explorer (best-effort; a missing path or Explorer never throws into the UI).</summary>
    public static bool OpenStorageFolder(string rowId)
    {
        if (rowId == "aiModels") { AiLyrics.OpenFolder(); return false; }   // the card's door: it creates the folder first
        string folder = rowId switch
        {
            "runtime" => Settings.RuntimeDirectory(),
            "logs" => Platform.LogFolder,
            "imageCache" => Settings.ImageCacheDirectory(),
            "audioBodies" or "cacheLocation" => Settings.AudioCacheFolder(),
            "licenseKeys" => System.IO.Path.GetDirectoryName(Spotify.Audio.KeyStore.DefaultPath()) ?? Settings.AudioCacheFolder(),
            _ => Platform.LocalFolder,   // library · localStore · otherData
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

    public static bool RemoveAiModels()
    {
        Settings.RemoveAiModels();
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

    // ── Music on this PC (#240 S3) ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>"Add a folder…": the house folder picker (off the UI thread); the pick is added when it answers.</summary>
    public static bool AddMusicFolder()
    {
        Pickers.Pick(PickerRequest.Folder(Loc.Get(Strings.Settings.Music.PickTitle)),
            done: picked => { if (!string.IsNullOrWhiteSpace(picked)) AddMusicFolderAt(picked); },
            failed: ex => Log.Warn("settings", "music folder picker failed", ex));
        return false;
    }

    /// <summary>"Add my Music folder" (offered only while the user's Music folder is not covered).</summary>
    public static bool AddDefaultMusicFolder()
    {
        if (LocalLibrary.DefaultMusicFolder is { } music) AddMusicFolderAt(music);
        return false;
    }

    /// <summary>Add <paramref name="path"/>: the row shows at once as "Checking…" while the library asks off the UI thread whether it is
    /// there (F4). A refusal says why in a toast (S11), at once or when the check answers, and so does a folder that replaced the ones
    /// inside it; a network folder that is away is kept and its row says so.</summary>
    internal static void AddMusicFolderAt(string path)
    {
        string? parent = LocalLibrary.ContainerOf(path);
        if (!LocalLibrary.AddFolder(path, out string reason, landed: verdict => { Say(verdict, path, parent); Settings.Bump(); }))
            Say(reason, path, parent);
        Settings.Bump();

        static void Say(string reason, string path, string? parent)
        {
            switch (reason)
            {
                case "combined": Notify.Say(Strings.Settings.Music.Combined(LocalLibrary.Normalize(path))); break;
                case "inside": Notify.Say(Strings.Settings.Music.Inside(parent ?? ""), InfoBarSeverity.Warning); break;
                case "duplicate": Notify.Say(Loc.Get(Strings.Settings.Music.Duplicate), InfoBarSeverity.Warning); break;
                case "missing": Notify.Say(Loc.Get(Strings.Settings.Music.NotFound), InfoBarSeverity.Warning); break;
            }
        }
    }

    /// <summary>"Rescan now": a warm walk of every folder.</summary>
    public static bool RescanMusic()
    {
        LocalLibrary.Rescan();
        return false;
    }

    /// <summary>A folder row's remove: no confirm (nothing leaves the disk); the toast offers Undo, which adds the folder back.</summary>
    public static void RemoveMusicFolder(string folder)
    {
        if (!LocalLibrary.RemoveFolder(folder)) return;
        Notify.Say(Strings.Settings.Music.Removed(folder), actionLabel: Loc.Get(Strings.Settings.Music.Undo), onAction: () => AddMusicFolderAt(folder),
            dedupeKey: "music.remove");
        Settings.Bump();
    }

    /// <summary>A folder row's "Show in Explorer".</summary>
    public static void RevealMusicFolder(string folder) => LocalLibrary.Reveal(folder);

    /// <summary>A folder row's "Rescan this folder".</summary>
    public static void RescanMusicFolder(string folder) => LocalLibrary.Rescan(folder);

    /// <summary>"Watch folders for changes": persists and starts or stops the watchers.</summary>
    public static bool SetMusicWatch(bool on)
    {
        LocalLibrary.SetWatching(on);
        return true;
    }

    /// <summary>"Keep missing songs for" 30 · 60 · 90 days.</summary>
    public static bool SetMusicMissingDays(int index)
    {
        if ((uint)index >= (uint)SettingsMusic.MissingDays.Length) return false;
        Platform.Settings.Set(Platform.Keys.MusicMissingDays, SettingsMusic.MissingDays[index]);
        return true;
    }

    /// <summary>After the confirm: arm the reset, spawn the restart broker and end this process.</summary>
    public static bool FactoryReset()
    {
        Settings.RequestFactoryReset();
        return false;
    }
}
