using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.WindowsApi.Dialogs;
using Wavee.Backend.Audio;
using Wavee.SpotifyLive.Audio;
using static FluentGpu.Dsl.Ui;
using Wavee.Sdk.Streams;

namespace Wavee;

sealed partial class SettingsPage
{
    sealed record StorageSnapshot(long LibraryDb, long Runtime, long Logs, int LogFiles, long Store,
        long AudioBody, long LicenseDb, long ImageCache, long Total);

    enum StorageLoadPhase : byte { NotStarted, Loading, Ready, Failed }

    static readonly string[] s_bodyBudgetLabels = ["1 GB", "2 GB", "4 GB", "8 GB", "16 GB", "32 GB", "64 GB", "128 GB", "256 GB", "512 GB", "1 TB", "Custom"];
    static readonly long[] s_bodyBudgetBytes = [1L << 30, 2L << 30, 4L << 30, 8L << 30, 16L << 30, 32L << 30, 64L << 30, 128L << 30, 256L << 30, 512L << 30, 1L << 40];
    readonly Signal<int> _bodyBudgetMode = new((int)AudioCacheBudgetMode.DriveShare);
    readonly Signal<int> _bodyBudgetPreset = new(5);
    readonly Signal<double> _bodyBudgetGiB = new(32);
    readonly Signal<double> _bodyBudgetPercent = new(0);
    bool _bodyBudgetSeeded;

    // Distinct hues for the usage bar + matching row accents (accent tints read as identical blue on dark themes).
    static readonly ColorF StorageColorLibrary = ColorF.FromRgba(0x4A, 0x90, 0xD9);
    static readonly ColorF StorageColorRuntime = ColorF.FromRgba(0x9B, 0x59, 0xB6);
    static readonly ColorF StorageColorLogs = ColorF.FromRgba(0xF5, 0xA6, 0x23);
    static readonly ColorF StorageColorStore = ColorF.FromRgba(0x27, 0xAE, 0x60);
    static readonly ColorF StorageColorAudio = ColorF.FromRgba(0x1A, 0xBC, 0x9C);
    static readonly ColorF StorageColorKeys = ColorF.FromRgba(0x95, 0xA5, 0xA6);
    static readonly ColorF StorageColorImages = ColorF.FromRgba(0xE7, 0x4C, 0x3C);


    readonly Signal<StorageLoadPhase> _storageLoad = new(StorageLoadPhase.NotStarted);
    StorageSnapshot? _storage;
    string? _storageError;

    // ── metadata cache (library.db cache tier — design §C/§G) ────────────────────────────────────────────────────────
    static readonly long[] s_metaBudgetBytes = [32L << 20, 64L << 20, 128L << 20, 256L << 20];
    static readonly string[] s_metaBudgetLabels = ["32 MB", "64 MB", "128 MB", "256 MB"];
    readonly Signal<int> _metaBudget = new(1);
    readonly Signal<int> _metaTick = new(0);
    bool _metaBudgetSeeded;
    Wavee.Backend.Persistence.EntityCacheStats? _metaStats;

    // The census is a handful of indexed counts + three pragmas on the cold store's WRITER connection, so it rides a
    // background task exactly like ComputeStorage — never the UI thread.
    void RefreshMetadataStats(Services? svc, Action<Action> post)
    {
        if (svc?.RealCold is not { } cold) return;
        _ = Task.Run(() =>
        {
            Wavee.Backend.Persistence.EntityCacheStats? snap = null;
            try { snap = cold.GetCacheStats(); }
            catch { }
            post(() => { _metaStats = snap; _metaTick.Value = _metaTick.Peek() + 1; });
        });
    }

    string MetadataCacheDescription()
    {
        if (_metaStats is not { } s) return Loc.Get(Strings.Settings.Storage.MetadataCacheSub);
        // The budget governs the EVICTABLE slice only (Wave F / K1): `cache` also carries the extension cache and every
        // pinned row, none of which the GC may delete, so showing the budget against the gross number would promise a
        // reclaim that can never happen. Developer-facing suffix, deliberately un-localized (same as the entity census
        // in ResidentCacheDescription below).
        return Loc.Format("settings.storage.metadataCacheStats",
            ("db", FmtBytes(s.DbBytes)),
            ("cache", FmtBytes(s.CacheBytes)),
            ("pinned", FmtBytes(s.PinnedBytes)),
            ("rows", s.EntityRows),
            ("pinnedRows", s.PinnedRows),
            ("overviews", s.OverviewRows),
            ("extensions", s.ExtensionRows))
            + $" · evictable {FmtBytes(s.EvictableBytes)} of {FmtBytes(s.BudgetBytes)} budget";
    }

    Element MetadataBudgetControl(Services? svc, IAppSettings? settings)
    {
        if (!_metaBudgetSeeded && settings is not null)
        {
            _metaBudgetSeeded = true;
            long chosen = settings.Get(WaveeSettings.MetadataCacheBudgetBytes);
            int idx = Array.IndexOf(s_metaBudgetBytes, chosen);
            _metaBudget.Value = idx >= 0 ? idx : 1;
        }
        return ComboBox.Create(s_metaBudgetLabels, _metaBudget, width: 120f, isEnabled: settings is not null,
            onChange: i =>
            {
                if (settings is null || i < 0 || i >= s_metaBudgetBytes.Length) return;
                long bytes = s_metaBudgetBytes[i];
                settings.Set(WaveeSettings.MetadataCacheBudgetBytes, bytes);
                // The GC reads the DB meta row; setting BudgetBytes writes BOTH, so a pass already scheduled picks the
                // new ceiling up without a restart. With no GC instance (fake backend) the meta row still gets it.
                if (svc?.CacheGc is { } gc) gc.BudgetBytes = bytes;
                else svc?.RealCold?.SetCacheBudgetBytes(bytes);
                Bump();
            });
    }

    void ClearMetadataCache(Services? svc, Action<Action> post)
    {
        if (svc?.RealStore is not Wavee.Backend.Persistence.CachedStore store) return;
        _ = Task.Run(() =>
        {
            try { store.ClearMetadataCache(); }
            catch { }
            post(() =>
            {
                Toast.Show(Loc.Get(Strings.Settings.Storage.MetadataCleared), new ToastOptions { Severity = InfoBarSeverity.Success });
                _storage = null;
                RefreshStorage(post);
                RefreshMetadataStats(svc, post);
            });
        });
    }

    void RefreshStorage(Action<Action> post)
    {
        if (_storageLoad.Peek() == StorageLoadPhase.Loading) return;
        _storageLoad.Value = StorageLoadPhase.Loading;
        _storageError = null;
        _ = Task.Run(() =>
        {
            StorageSnapshot? snap = null;
            Exception? error = null;
            try { snap = ComputeStorage(); }
            catch (Exception ex) { error = ex; }
            post(() =>
            {
                if (snap is { } ready)
                {
                    _storage = ready;
                    _storageError = null;
                    _storageLoad.Value = StorageLoadPhase.Ready;
                }
                else
                {
                    _storage = null;
                    _storageError = error?.Message;
                    _storageLoad.Value = StorageLoadPhase.Failed;
                }
            });
        });
    }

    static StorageSnapshot ComputeStorage()
    {
        string root = SettingsShared.AppDataRoot;
        long library = 0, runtime = 0, logs = 0, store = 0;
        int logFiles = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(root, "library.db*")) library += FileSize(f);
        }
        catch { }
        runtime = DirSize(Path.Combine(root, "playplay"));
        try
        {
            string logDir = Path.Combine(root, "logs");
            if (Directory.Exists(logDir))
                foreach (var f in Directory.EnumerateFiles(logDir)) { logs += FileSize(f); logFiles++; }
        }
        catch { }
        store += FileSize(Path.Combine(root, "store.json"));
        store += DirSize(Path.Combine(root, "WaveeMusic"));
        var cacheSettings = AppDataSettings.ForUnpackaged("Wavee", "Wavee");
        long audioBody = DirSize(AudioBodyDiskCache.ResolveDirectory(cacheSettings.Get(WaveeSettings.AudioBodyCacheBasePath)));
        long licenseDb = FileSize(LicenseKeyDiskCache.DefaultDbPath());
        // Album art and other remote images, decoded once and kept on disk. Under the app root since the shipping build
        // sets AppOptions.ImageCacheDirectory — before that it lived in %TEMP% and was invisible to this page.
        long imageCache = DirSize(Path.Combine(root, "cache", "images"));
        return new StorageSnapshot(library, runtime, logs, logFiles, store, audioBody, licenseDb, imageCache,
            library + runtime + logs + store + audioBody + licenseDb + imageCache);
    }

    static long FileSize(string path)
    {
        try { var fi = new FileInfo(path); return fi.Exists ? fi.Length : 0; }
        catch { return 0; }
    }

    static long DirSize(string dir)
    {
        long total = 0;
        try
        {
            if (!Directory.Exists(dir)) return 0;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) total += FileSize(f);
        }
        catch { }
        return total;
    }

    static string FmtBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => (bytes / (1024.0 * 1024 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " GB",
        >= 1024L * 1024 => (bytes / (1024.0 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " MB",
        >= 1024L => (bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB",
        _ => bytes.ToString(CultureInfo.InvariantCulture) + " B",
    };

    static Element StorageActions(long? size, string folder, Element? extra = null)
    {
        var actions = new List<Element>();
        if (size is { } b) actions.Add(new TextEl(FmtBytes(b)) { Size = 13f, Color = Tok.TextSecondary, Shrink = 0f });
        actions.Add(HyperlinkButton.Create(Loc.Get(Strings.Settings.Storage.OpenFolder), () => SettingsShared.OpenFolder(folder)));
        if (extra is not null) actions.Add(extra);
        return new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Children = actions.ToArray() };
    }

    static Element StorageRowCard(string label, string? sub, long? size, string folder, ColorF accent,
                                  string? icon = null, Element? extra = null)
    {
        var row = SettingsRow(label, sub, StorageActions(size, folder, extra), icon);
        return new BoxEl
        {
            Direction = 0, AlignSelf = FlexAlign.Stretch,
            Children =
            [
                new BoxEl
                {
                    Width = 3f, AlignSelf = FlexAlign.Stretch, Fill = accent,
                    Corners = new CornerRadius4(2f, 0f, 0f, 2f),
                },
                new BoxEl { Grow = 1f, Basis = 0f, MinWidth = 0f, Children = [row] },
            ],
        };
    }

    static int BodyBudgetIndex(long bytes)
    {
        int best = 5;
        long bestDiff = long.MaxValue;
        for (int i = 0; i < s_bodyBudgetBytes.Length; i++)
        {
            long diff = Math.Abs(s_bodyBudgetBytes[i] - bytes);
            if (diff < bestDiff) { bestDiff = diff; best = i; }
        }
        return best;
    }

    void DeleteOldLogs(Action<Action> post)
    {
        _ = Task.Run(() =>
        {
            int deleted = 0;
            try
            {
                string logDir = Path.Combine(SettingsShared.AppDataRoot, "logs");
                if (Directory.Exists(logDir))
                    foreach (var f in Directory.EnumerateFiles(logDir, "wavee-*.log"))
                    { try { File.Delete(f); deleted++; } catch { } }
            }
            catch { }
            post(() =>
            {
                Toast.Show(deleted > 0
                    ? Loc.Format("settings.storage.oldLogsDeleted", ("count", deleted))
                    : Loc.Get(Strings.Settings.Storage.NoOldLogsDeleted),
                    new ToastOptions { Severity = InfoBarSeverity.Success });
                RefreshStorage(post);
            });
        });
    }

    static string ResidentCacheDescription(Wavee.Backend.Persistence.CachedStore? cold)
    {
        if (cold is null) return Loc.Get(Strings.Settings.Storage.ResidentCacheSub);
        string membership = Loc.Format("settings.storage.residentCacheStats",
            ("used", FmtBytes(cold.ResidentMembershipBytes)),
            ("cap", FmtBytes(cold.MaxResidentBytes)),
            ("count", cold.ResidentMembershipCount),
            ("max", cold.MaxResidentPlaylists));
        // Entity-store census (attribution for the ~92 MB resident-string floor — the residency plan). Compact + diagnostic,
        // appended on one line; deliberately developer-facing (raw counts), so not routed through the loc tables.
        var c = cold.EntityCounts;
        string entities = $" · entities t={c.Tracks} al={c.Albums} ar={c.Artists} pl={c.Playlists} sh={c.Shows} ep={c.Episodes} (~{FmtBytes(cold.EstimatedEntityBytes)})";
        return membership + entities;
    }

    Element StorageTab(Services? svc, Action<Action> post)
    {
        var storageLoad = _storageLoad.Value;
        _ = _metaTick.Value;   // subscribe: the metadata census lands asynchronously
        var s = _storage;
        string root = SettingsShared.AppDataRoot;
        string playplayDir = Path.Combine(root, "playplay");
        var settings = svc?.Settings;

        var cold = svc?.RealStore as Wavee.Backend.Persistence.CachedStore;
        string residentCacheDescription = ResidentCacheDescription(cold);
        string audioDir = svc?.AudioBodyCache?.CurrentDirectory
            ?? AudioBodyDiskCache.ResolveDirectory(settings?.Get(WaveeSettings.AudioBodyCacheBasePath));
        string licenseDb = LicenseKeyDiskCache.DefaultDbPath();
        var keyStats = svc?.AudioLicenseCache?.Stats();
        if (!_bodyBudgetSeeded && settings is not null)
        {
            _bodyBudgetSeeded = true;
            _bodyBudgetMode.Value = Math.Clamp(settings.Get(WaveeSettings.AudioBodyCacheBudgetMode), 0, 2);
            long fixedBytes = Math.Max(64L << 20, settings.Get(WaveeSettings.AudioBodyCacheBudgetBytes));
            _bodyBudgetGiB.Value = fixedBytes / (double)(1L << 30);
            int preset = Array.IndexOf(s_bodyBudgetBytes, fixedBytes);
            _bodyBudgetPreset.Value = preset >= 0 ? preset : s_bodyBudgetLabels.Length - 1;
            _bodyBudgetPercent.Value = Math.Clamp(settings.Get(WaveeSettings.AudioBodyCacheBudgetPercent), 0, 90);
        }
        string licenseSub = keyStats is { } ks
            ? (ks.Count == 1
                ? Loc.Get(Strings.Settings.Storage.LicenseKeysCountOne)
                : Loc.Format("settings.storage.licenseKeysCount", ("count", ks.Count)))
            : Loc.Get(Strings.Settings.Storage.LicenseKeysSub);

        Element Toggle(SettingKey<bool> key) => ToggleSwitch.Create(new Signal<bool>(settings?.Get(key) ?? true), onChange: _ =>
        {
            if (settings is null) return;
            settings.Set(key, !settings.Get(key));
            Bump();
        }, style: SettingsCard.CompactToggleStyle());

        Element top = storageLoad == StorageLoadPhase.Failed
            ? InfoBar.Create(InfoBarSeverity.Error,
                Loc.Get(Strings.Settings.Storage.ReadFailed),
                _storageError ?? Loc.Get(Strings.Common.ErrorTitle),
                isClosable: false,
                actionButton: Button.Standard(Loc.Get(Strings.Common.Retry), () => RefreshStorage(post)))
            : storageLoad == StorageLoadPhase.Loading || s is null
                ? new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M,
                    Padding = new Edges4(0, Spacing.L, 0, Spacing.L),
                    Children =
                    [
                        ProgressRing.Indeterminate(size: 20f),
                        new TextEl(Loc.Get(Strings.Settings.Storage.Reading)) { Size = 12f, Color = Tok.TextSecondary },
                    ],
                }
                : StorageUsageBar(s);

        return SettingsTabStack(
            SettingsSectionHeader(Loc.Get(Strings.Settings.Storage.OnThisPc), SettingsGlyphs.Section(SettingsTab.Storage, "On this PC")),
            top,
            StorageRowCard(Loc.Get(Strings.Settings.Storage.Library), Loc.Get(Strings.Settings.Storage.LibrarySub),
                s?.LibraryDb, root, StorageColorLibrary, SettingsGlyphs.Row(SettingsTab.Storage, "library")),
            StorageRowCard(Loc.Get(Strings.Settings.Storage.Runtime), Loc.Get(Strings.Settings.Storage.RuntimeSub),
                s?.Runtime, playplayDir, StorageColorRuntime, SettingsGlyphs.Row(SettingsTab.Storage, "runtime")),
            StorageRowCard(Loc.Get(Strings.Settings.Storage.Logs),
                s is { } sn
                    ? sn.LogFiles == 1
                        ? Loc.Get(Strings.Settings.Storage.LogsSubOne)
                        : Loc.Format("settings.storage.logsSub", ("count", sn.LogFiles))
                    : Loc.Get(Strings.Settings.Storage.LogsSubEmpty),
                s?.Logs, Path.Combine(root, "logs"), StorageColorLogs, SettingsGlyphs.Row(SettingsTab.Storage, "logs"),
                Button.Standard(Loc.Get(Strings.Settings.Storage.DeleteOldLogs), () =>
                    ConfirmThen(Loc.Get(Strings.Settings.Storage.DeleteOldLogs),
                        Loc.Get(Strings.Settings.Storage.DeleteOldLogsBody),
                        Loc.Get(Strings.Settings.Storage.DeleteOldLogs),
                        () => DeleteOldLogs(post)))),
            StorageRowCard(Loc.Get(Strings.Settings.Storage.LocalStore), Loc.Get(Strings.Settings.Storage.LocalStoreSub),
                s?.Store, root, StorageColorStore, SettingsGlyphs.Row(SettingsTab.Storage, "localStore")),
            StorageRowCard(Loc.Get(Strings.Settings.Storage.ImageCache), Loc.Get(Strings.Settings.Storage.ImageCacheSub),
                s?.ImageCache, Path.Combine(root, "cache", "images"), StorageColorImages, SettingsGlyphs.Row(SettingsTab.Storage, "imageCache")),
            SettingsSectionHeader(Loc.Get(Strings.Settings.Storage.PlaybackCache), SettingsGlyphs.Section(SettingsTab.Storage, "Playback cache")),
            SettingsRow(Loc.Get(Strings.Settings.Storage.CacheAudio), Loc.Get(Strings.Settings.Storage.CacheAudioSub),
                Toggle(WaveeSettings.AudioBodyCacheEnabled), SettingsGlyphs.Row(SettingsTab.Storage, "cacheAudio")),
            SettingsRow(Loc.Get(Strings.Settings.Storage.CacheKeys), Loc.Get(Strings.Settings.Storage.CacheKeysSub),
                Toggle(WaveeSettings.AudioKeyCacheEnabled), SettingsGlyphs.Row(SettingsTab.Storage, "cacheKeys")),
            SettingsRow(Loc.Get(Strings.Settings.Storage.BodyBudget), Loc.Get(Strings.Settings.Storage.BodyBudgetSub),
                BudgetControl(svc, settings, s), SettingsGlyphs.Row(SettingsTab.Storage, "budget")),
            SettingsRow(Loc.Get(Strings.Settings.Storage.CacheLocation), audioDir,
                CacheLocationActions(svc, settings, post, audioDir), SettingsGlyphs.Row(SettingsTab.Storage, "cacheLocation")),
            StorageRowCard(Loc.Get(Strings.Settings.Storage.AudioBodies), Loc.Get(Strings.Settings.Storage.AudioBodiesSub),
                s?.AudioBody, audioDir, StorageColorAudio, SettingsGlyphs.Row(SettingsTab.Storage, "audioBodies"),
                Button.Standard(Loc.Get(Strings.Settings.Storage.ClearAudio), () =>
                    ConfirmThen(Loc.Get(Strings.Settings.Storage.ClearAudio),
                        Loc.Get(Strings.Settings.Storage.ClearAudioBody),
                        Loc.Get(Strings.Settings.Storage.ClearAudio),
                        () => ClearAudioBodyCache(svc, post)))),
            StorageRowCard(Loc.Get(Strings.Settings.Storage.LicenseKeys), licenseSub,
                s?.LicenseDb, Path.GetDirectoryName(licenseDb) ?? audioDir, StorageColorKeys, SettingsGlyphs.Row(SettingsTab.Storage, "licenseKeys"),
                Button.Standard(Loc.Get(Strings.Settings.Storage.ClearKeys), () =>
                    ConfirmThen(Loc.Get(Strings.Settings.Storage.ClearKeys),
                        Loc.Get(Strings.Settings.Storage.ClearKeysBody),
                        Loc.Get(Strings.Settings.Storage.ClearKeys),
                        () => ClearLicenseKeys(svc, post)))),
            SettingsSectionHeader(Loc.Get(Strings.Settings.Storage.MetadataCache), SettingsGlyphs.Section(SettingsTab.Storage, "Metadata cache")),
            SettingsRow(Loc.Get(Strings.Settings.Storage.MetadataBudget), Loc.Get(Strings.Settings.Storage.MetadataBudgetSub),
                MetadataBudgetControl(svc, settings), SettingsGlyphs.Row(SettingsTab.Storage, "metadataBudget")),
            SettingsRow(Loc.Get(Strings.Settings.Storage.MetadataCache), MetadataCacheDescription(),
                Button.Standard(Loc.Get(Strings.Settings.Storage.ClearMetadata), () =>
                    ConfirmThen(Loc.Get(Strings.Settings.Storage.ClearMetadata),
                        Loc.Get(Strings.Settings.Storage.ClearMetadataBody),
                        Loc.Get(Strings.Settings.Storage.ClearMetadata),
                        () => ClearMetadataCache(svc, post))), SettingsGlyphs.Row(SettingsTab.Storage, "clearMetadata")),
            SettingsSectionHeader(Loc.Get(Strings.Settings.Storage.Memory), SettingsGlyphs.Section(SettingsTab.Storage, "Memory")),
            SettingsRow(Loc.Get(Strings.Settings.Storage.ResidentCache), residentCacheDescription,
                Button.Standard(Loc.Get(Strings.Settings.Storage.ReleaseNow), () =>
                {
                    svc?.LibraryStore.ShedDetails(keep: 16);
                    Toast.Show(Loc.Get(Strings.Settings.Storage.DetailsReleased), new ToastOptions { Severity = InfoBarSeverity.Success });
                    Bump();
                }), SettingsGlyphs.Row(SettingsTab.Storage, "residentCache")),
            SettingsSectionHeader(Loc.Get(Strings.Settings.Storage.FactoryReset), SettingsGlyphs.Section(SettingsTab.Storage, "Reset"),
                Loc.Get(Strings.Settings.Storage.FactoryResetSub)),
            SettingsRow(Loc.Get(Strings.Settings.Storage.FactoryReset), Loc.Get(Strings.Settings.Storage.FactoryResetRowSub),
                Button.Standard(Loc.Get(Strings.Settings.Storage.FactoryResetAction), () =>
                    ConfirmThen(Loc.Get(Strings.Settings.Storage.FactoryResetConfirmTitle),
                        Loc.Get(Strings.Settings.Storage.FactoryResetConfirmBody),
                        Loc.Get(Strings.Settings.Storage.FactoryResetAction),
                        () => RequestFactoryReset(svc))), SettingsGlyphs.Row(SettingsTab.Storage, "factoryReset")));
    }

    static void RequestFactoryReset(Services? svc)
    {
        try { Wavee.SpotifyLive.SpotifyLiveLogin.ClearStoredCredential(); }
        catch { }

        string? extra = null;
        try
        {
            string custom = svc?.Settings.Get(WaveeSettings.AudioBodyCacheBasePath) ?? "";
            if (custom.Length > 0) extra = AudioBodyDiskCache.ResolveDirectory(custom);
        }
        catch { }

        FactoryReset.RequestAndRelaunch(extra is null ? null : [extra]);
    }

    Element StorageUsageBar(StorageSnapshot s)
    {
        (string label, long bytes, ColorF color)[] segs =
        [
            (Loc.Get(Strings.Settings.Storage.Library), s.LibraryDb, StorageColorLibrary),
            (Loc.Get(Strings.Settings.Storage.Runtime), s.Runtime, StorageColorRuntime),
            (Loc.Get(Strings.Settings.Storage.Logs), s.Logs, StorageColorLogs),
            (Loc.Get(Strings.Settings.Storage.LocalStore), s.Store, StorageColorStore),
            (Loc.Get(Strings.Settings.Storage.AudioBodies), s.AudioBody, StorageColorAudio),
            (Loc.Get(Strings.Settings.Storage.LicenseKeys), s.LicenseDb, StorageColorKeys),
            (Loc.Get(Strings.Settings.Storage.ImageCache), s.ImageCache, StorageColorImages),
        ];

        var barKids = new List<Element>();
        var legend = new List<Element>();
        long total = Math.Max(1, s.Total);
        foreach (var (label, bytes, color) in segs)
        {
            if (bytes <= 0) continue;
            barKids.Add(new BoxEl { Grow = (float)bytes, Height = 10f, Fill = color, Corners = CornerRadius4.All(2f) });
            int pct = (int)Math.Round(bytes * 100.0 / total);
            legend.Add(new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = 8f, Grow = 1f, Basis = 0f, MinWidth = 200f,
                Children =
                [
                    new BoxEl { Width = 10f, Height = 10f, Corners = CornerRadius4.All(2f), Fill = color, Shrink = 0f },
                    new TextEl(label) { Size = 12f, Color = Tok.TextPrimary, Grow = 1f },
                    new TextEl(FmtBytes(bytes)) { Size = 12f, Color = Tok.TextSecondary, Shrink = 0f },
                    new TextEl(pct + "%") { Size = 11f, Color = Tok.TextTertiary, Width = 36f, Shrink = 0f },
                ],
            });
        }

        return new BoxEl
        {
            Direction = 1, Gap = Spacing.M,
            Padding = new Edges4(Spacing.L, Spacing.M, Spacing.L, Spacing.M),
            Corners = CornerRadius4.All(Radii.Card),
            Fill = Tok.FillCardSecondary, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S,
                    Children =
                    [
                        new TextEl(Loc.Get(Strings.Settings.Storage.Total)) { Size = 12f, Weight = 600, Color = Tok.TextSecondary },
                        new TextEl(FmtBytes(s.Total)) { Size = 18f, Weight = 700, Color = Tok.TextPrimary },
                    ],
                },
                barKids.Count > 0
                    ? new BoxEl
                    {
                        Direction = 0, Height = 10f, Gap = 2f, ClipToBounds = true,
                        Corners = CornerRadius4.All(3f), Fill = Tok.StrokeDividerDefault,
                        Children = barKids.ToArray(),
                    }
                    : new BoxEl(),
                new BoxEl { Direction = 0, Gap = Spacing.S, Wrap = true, Children = legend.ToArray() },
            ],
        };
    }

    Element BudgetControl(Services? svc, IAppSettings? settings, StorageSnapshot? s)
    {
        var status = svc?.AudioBodyCache?.Status();
        int mode = Math.Clamp(_bodyBudgetMode.Value, 0, 2);
        long? budget = status?.BudgetBytes;
        long used = s?.AudioBody ?? 0;
        float frac = budget is > 0 ? Math.Clamp((float)(used / (double)budget.Value), 0f, 1f) : 0f;
        bool over = budget is { } b && used > b;

        void SetMode(int next)
        {
            if (settings is null) return;
            next = Math.Clamp(next, 0, 2);
            settings.Set(WaveeSettings.AudioBodyCacheBudgetMode, next);
            svc?.AudioBodyCache?.Trim();
            Bump();
        }

        Element editor = mode switch
        {
            (int)AudioCacheBudgetMode.FixedBytes => FixedBudgetEditor(svc, settings),
            (int)AudioCacheBudgetMode.DriveShare => NumberBox.Create(value: _bodyBudgetPercent,
                options: new NumberBox.NumberBoxOptions { Minimum = 0, Maximum = 90, SmallChange = 1, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Width = 150f, Formatter = v => v <= 0 ? Loc.Get(Strings.Settings.Storage.AutoTenPercent) : v.ToString("0", CultureInfo.InvariantCulture) + "%", IsEnabled = settings is not null },
                onChange: v =>
                {
                    if (settings is null) return;
                    int pct = Math.Clamp((int)Math.Round(v), 0, 90);
                    _bodyBudgetPercent.Value = pct;
                    settings.Set(WaveeSettings.AudioBodyCacheBudgetPercent, pct);
                    svc?.AudioBodyCache?.Trim();
                    Bump();
                }),
            _ => new TextEl(Loc.Get(Strings.Settings.Storage.UnlimitedReserve)) { Size = 12f, Color = Tok.TextSecondary },
        };

        string budgetLabel = budget is { } limit ? FmtBytes(limit) : Loc.Get(Strings.Settings.Storage.Unlimited);

        return new BoxEl
        {
            Direction = 1, Gap = 6f,
            Children =
            [
                SelectorBar.Create([
                    Loc.Get(Strings.Settings.Storage.FixedSize),
                    Loc.Get(Strings.Settings.Storage.DriveShare),
                    Loc.Get(Strings.Settings.Storage.Unlimited)], _bodyBudgetMode, onChange: SetMode),
                editor,
                ProgressBar.Determinate(frac, width: 300f, state: over ? ProgressBarState.Error : ProgressBarState.Normal),
                new TextEl(Strings.Settings.Storage.UsedOfBudget(FmtBytes(used), budgetLabel))
                    { Size = 11.5f, Color = Tok.TextSecondary },
                status is { Available: false }
                    ? new TextEl(Loc.Get(Strings.Settings.Storage.LocationUnavailable)) { Size = 11.5f, Color = Tok.SystemFillCritical }
                    : new TextEl(Loc.Format("settings.storage.freeReserve", ("reserve", FmtBytes(status?.ReserveBytes ?? 0))))
                        { Size = 11.5f, Color = Tok.TextTertiary },
            ],
        };
    }

    Element FixedBudgetEditor(Services? svc, IAppSettings? settings)
    {
        void CommitGiB(double gib)
        {
            if (settings is null || double.IsNaN(gib) || double.IsInfinity(gib)) return;
            gib = Math.Clamp(gib, 0.0625, long.MaxValue / (double)(1L << 30));
            long bytes = checked((long)Math.Round(gib * (1L << 30)));
            _bodyBudgetGiB.Value = gib;
            settings.Set(WaveeSettings.AudioBodyCacheBudgetBytes, bytes);
            svc?.AudioBodyCache?.SetBudget(bytes);
            Bump();
        }

        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Wrap = true,
            Children =
            [
                ComboBox.Create(s_bodyBudgetLabels, _bodyBudgetPreset, width: 120f, isEnabled: settings is not null,
                    onChange: i =>
                    {
                        if (i < 0 || i >= s_bodyBudgetBytes.Length) return;
                        _bodyBudgetGiB.Value = s_bodyBudgetBytes[i] / (double)(1L << 30);
                        CommitGiB(_bodyBudgetGiB.Value);
                    }),
                NumberBox.Create(value: _bodyBudgetGiB,
                    options: new NumberBox.NumberBoxOptions
                    {
                        Minimum = 0.0625, Maximum = long.MaxValue / (double)(1L << 30), SmallChange = 1,
                        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Width = 150f,
                        Formatter = v => v.ToString("0.###", CultureInfo.InvariantCulture) + " GB",
                        IsEnabled = settings is not null,
                    },
                    onChange: v =>
                    {
                        _bodyBudgetPreset.Value = s_bodyBudgetLabels.Length - 1;
                        CommitGiB(v);
                    }),
            ],
        };
    }

    Element CacheLocationActions(Services? svc, IAppSettings? settings, Action<Action> post, string audioDir) =>
        new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, Wrap = true,
            Children =
            [
                Button.Standard(Loc.Get(Strings.Settings.Storage.ChooseLocation), () => PickCacheLocation(svc, settings, post)),
                Button.Standard(Loc.Get(Strings.Settings.Storage.UseDefaultLocation), () => OfferCacheRelocation(svc, settings, post, "")),
                HyperlinkButton.Create(Loc.Get(Strings.Settings.Storage.OpenFolder), () => SettingsShared.OpenFolder(audioDir)),
            ],
        };

    void PickCacheLocation(Services? svc, IAppSettings? settings, Action<Action> post)
    {
        if (settings is null || svc?.AudioBodyCache is null) return;
        string? selected = FilePicker.PickFolder(FluentApp.WindowHandle, Loc.Get(Strings.Settings.Storage.ChooseCacheFolder));
        if (string.IsNullOrWhiteSpace(selected)) return;
        OfferCacheRelocation(svc, settings, post, selected);
    }

    void OfferCacheRelocation(Services? svc, IAppSettings? settings, Action<Action> post, string newBase)
    {
        if (_overlay is null || settings is null || svc?.AudioBodyCache is null) return;
        ContentDialog.Show(_overlay, d =>
        {
            d.Title = Loc.Get(Strings.Settings.Storage.MoveCacheTitle);
            d.Message = Loc.Get(Strings.Settings.Storage.MoveCacheBody);
            d.PrimaryText = Loc.Get(Strings.Settings.Storage.MoveExisting);
            d.SecondaryText = Loc.Get(Strings.Settings.Storage.StartEmpty);
            d.CloseText = Loc.Get(Strings.Auth.Cancel);
            d.DefaultButton = ContentDialog.DefaultBtn.Primary;
            d.PrimaryClick = () => BeginCacheRelocation(svc, settings, post, newBase, AudioCacheRelocationMode.Move);
            d.SecondaryClick = () => post(() => OfferStartEmptyChoice(svc, settings, post, newBase));
        });
    }

    void OfferStartEmptyChoice(Services svc, IAppSettings settings, Action<Action> post, string newBase)
    {
        if (_overlay is null) return;
        ContentDialog.Show(_overlay, d =>
        {
            d.Title = Loc.Get(Strings.Settings.Storage.OldCacheTitle);
            d.Message = Loc.Get(Strings.Settings.Storage.OldCacheBody);
            d.PrimaryText = Loc.Get(Strings.Settings.Storage.DeleteOldCache);
            d.SecondaryText = Loc.Get(Strings.Settings.Storage.LeaveOldCache);
            d.CloseText = Loc.Get(Strings.Auth.Cancel);
            d.PrimaryClick = () => BeginCacheRelocation(svc, settings, post, newBase, AudioCacheRelocationMode.StartEmptyDeleteOld);
            d.SecondaryClick = () => BeginCacheRelocation(svc, settings, post, newBase, AudioCacheRelocationMode.StartEmptyKeepOld);
        });
    }

    void BeginCacheRelocation(Services svc, IAppSettings settings, Action<Action> post, string newBase, AudioCacheRelocationMode mode)
    {
        _ = Task.Run(async () =>
        {
            bool ok = await svc.AudioBodyCache!.PrepareRelocationAsync(newBase, mode).ConfigureAwait(false);
            post(() =>
            {
                if (ok)
                {
                    settings.Set(WaveeSettings.AudioBodyCacheBasePath, newBase);
                    Toast.Show(Loc.Get(Strings.Settings.Storage.CacheLocationChanged), new ToastOptions { Severity = InfoBarSeverity.Success });
                    _storage = null;
                    RefreshStorage(post);
                    Bump();
                }
                else Toast.Show(Loc.Get(Strings.Settings.Storage.CacheLocationFailed), new ToastOptions { Severity = InfoBarSeverity.Error });
            });
        });
    }

    void ClearAudioBodyCache(Services? svc, Action<Action> post)
    {
        _ = Task.Run(() =>
        {
            try { svc?.AudioBodyCache?.ClearAll(); }
            catch
            {
                try
                {
                    string dir = svc?.AudioBodyCache?.CurrentDirectory ?? AudioBodyDiskCache.DefaultDirectory();
                    if (Directory.Exists(dir))
                        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                            if (f.EndsWith(".enc", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".map", StringComparison.OrdinalIgnoreCase)) File.Delete(f);
                }
                catch { }
            }
            post(() =>
            {
                Toast.Show(Loc.Get(Strings.Settings.Storage.AudioCacheCleared), new ToastOptions { Severity = InfoBarSeverity.Success });
                _storage = null;
                RefreshStorage(post);
            });
        });
    }

    void ClearLicenseKeys(Services? svc, Action<Action> post)
    {
        _ = Task.Run(() =>
        {
            try { svc?.AudioLicenseCache?.ClearAll(); }
            catch { }
            post(() =>
            {
                Toast.Show(Loc.Get(Strings.Settings.Storage.LicenseKeysCleared), new ToastOptions { Severity = InfoBarSeverity.Success });
                _storage = null;
                RefreshStorage(post);
            });
        });
    }
}
