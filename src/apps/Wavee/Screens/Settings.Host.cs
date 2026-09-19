// ── Screens/Settings.Host.cs ───────────────────────────────────────────────────────────────────────────────────────
// the settings page's side effects: the spotify: scheme association, Explorer, the data-folder census, the clears, the
// audio-cache relocation (the two-dialog flow), the factory reset, the notices file, the diagnostics copy, and the
// cross-owner seams they need (the engine-free Storage/About decisions are Settings.cs §10)
//
// Role: SHELL
// Owner: R
// Wave: 6
// Budget: 700 lines
// Spec: ch 27 §9.3, §1.2 (`Settings.Host`: StorageCensus / MetadataCensus / DeleteOldLogs / ClearAudioBodies /
// ClearLicenseKeys / ClearMetadata / FactoryReset / RelocateCache / OpenFolder); ch 27 §7 (the toast inventory), W20, W27
//
// Every side effect lives here, off the UI thread where it touches the disk; the UI files only call in and post the
// answer back through the page's poster.

using System.Globalization;
using System.Runtime.InteropServices;
using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.WindowsApi.Activation;
using FluentGpu.WindowsApi.Dialogs;
using FluentGpu.WindowsApi.Packaging;
using Wavee.Sdk.Streams;

namespace Wavee;

public static partial class Settings
{
    // ══ 1. SHARED ═══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Apply the <c>spotify:</c> association AT THE TOGGLE, in both directions (0.2.9
    /// <c>DeepLink.SyncSpotifySchemeRegistration</c>). UNPACKAGED ONLY: a packaged build's manifest owns its protocols.
    /// Fail-soft (logged).</summary>
    static void SyncSpotifyScheme(bool on)
    {
        try
        {
            if (PackageIdentity.IsPackaged || Environment.ProcessPath is not { Length: > 0 } exe) return;
            if (on) ProtocolRegistrar.RegisterProtocol("spotify", exe, "Wavee");
            else ProtocolRegistrar.UnregisterProtocol("spotify");
        }
        catch (Exception ex)
        {
            Log.Warn("settings", "spotify: protocol registration failed", ex);
        }
    }

    /// <summary>Reveal a folder (or a file's folder) in Explorer. Best-effort: a missing path or Explorer never throws
    /// into the UI.</summary>
    static void OpenFolder(string path)
    {
        try
        {
            if (!Directory.Exists(path)) path = Path.GetDirectoryName(path) ?? path;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + path + "\"")
            {
                UseShellExecute = false,
            })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("settings", "could not open folder " + path, ex);
        }
    }

    // ══ 2. THE SEAMS (null = the chapter's honest absent state, never a throw) ══════════════════════════════════════

    /// <summary>The entities/store owner assigns (G3): the cache-tier census, called OFF the UI thread. Null (or a null
    /// answer) → the Metadata-cache row keeps its generic description.</summary>
    public static Func<MetadataCacheSnapshot?>? MetadataStats;

    /// <summary>Wired in <c>App.cs</c> to <c>Store.DropCatalog</c> (D1): drops the cache TIER of the store file — the
    /// catalog rows, palettes and non-library edges — while the library relations and the pending-intent journal are
    /// kept. Called OFF the UI thread; a no-op if the store never opened (--fake). Null would disable "Clear metadata
    /// cache", but after D1 it never is.</summary>
    public static Action? ClearMetadataCache;

    /// <summary>B4 assigns (G-220): how many license keys the audio path keeps on disk. Null → the generic sub.</summary>
    public static Func<int?>? LicenseKeyCount;

    /// <summary>B4 assigns (G-220): forget every saved license key. Called OFF the UI thread, before the on-disk
    /// <c>audiokeys.db</c> is deleted. Null → only the file is deleted.</summary>
    public static Action? ClearLicenseKeys;

    /// <summary>The "Wavee right now" block as text — the last receipts tick, for the diagnostics copy (0.2.9
    /// <c>WaveeNowReceipts.LastCopyText</c>). "" until the About tab has ticked once.</summary>
    public static string ReceiptsText { get; private set; } = "";

    // ══ 3. PATHS (every one 0.2.9's, rooted at the profile) ════════════════════════════════════════════════════════

    static string AudioCacheDirectory()
        => ChunkDiskCache.ResolveDirectory(Platform.Settings.Get(Platform.Keys.AudioBodyCacheBasePath), Spotify.Audio.DiskCache.DefaultDirectory());

    /// <summary>0.2.9 `LicenseKeyDiskCache.DefaultDbPath()`: the product cache folder, beside `audio\`.</summary>
    static string LicenseDbPath() => Path.Combine(Platform.LocalFolder, "Wavee", "Cache", "audiokeys.db");

    static string RuntimeDirectory() => Path.Combine(Platform.LocalFolder, "playplay");

    static string ImageCacheDirectory() => Path.Combine(Platform.LocalFolder, "cache", "images");

    static long FileBytes(string path)
    {
        try { var fi = new FileInfo(path); return fi.Exists ? fi.Length : 0; }
        catch { return 0; }
    }

    static long TreeBytes(string dir)
    {
        long total = 0;
        try
        {
            if (!Directory.Exists(dir)) return 0;
            foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) total += FileBytes(f);
        }
        catch { }
        return total;
    }

    // ══ 4. THE CENSUS (NotStarted → Loading → Ready | Failed) ══════════════════════════════════════════════════════

    /// <summary>Start (or queue) a census. A clear that lands while one is running re-runs it when it finishes, so the
    /// bar never keeps pre-clear numbers (parity 48b). The row cards render throughout; only their sizes wait.</summary>
    static void RefreshStorage()
    {
        if (s_storageLoad.Peek() == StorageLoadPhase.Loading) { s_recount = true; return; }
        s_storage = null;
        s_storageError = null;
        s_storageLoad.Value = StorageLoadPhase.Loading;
        var post = s_post;
        _ = Task.Run(() =>
        {
            StorageSnapshot? snap = null;
            AudioBodyCacheStatus? status = null;
            int? keys = null;
            MetadataCacheSnapshot? meta = null;
            string? error = null;
            try { snap = StorageCensus(out status); }
            catch (Exception ex) { error = ex.Message; Log.Warn("settings", "storage census failed", ex); }
            try { keys = LicenseKeyCount?.Invoke(); } catch (Exception ex) { Log.Warn("settings", "license key count failed", ex); }
            try { meta = MetadataStats?.Invoke(); } catch (Exception ex) { Log.Warn("settings", "metadata cache stats failed", ex); }
            post(() =>
            {
                s_storage = snap;
                s_storageError = error;
                s_audioStatus = status;
                s_keyCount = keys;
                s_metaStats = meta;
                s_storageLoad.Value = snap is null ? StorageLoadPhase.Failed : StorageLoadPhase.Ready;
                if (s_recount) { s_recount = false; RefreshStorage(); }
            });
        });
    }

    /// <summary>0.2.9 `ComputeStorage`, over the 0.3 profile paths. The audio bytes are the cache's own measurement when
    /// the cache exists (the same number the budget meter reads), else the directory size.</summary>
    static StorageSnapshot StorageCensus(out AudioBodyCacheStatus? status)
    {
        string root = Platform.LocalFolder;
        long library = 0, logs = 0;
        int logFiles = 0;
        // Every "library.*" file: the current schema-named set, a not-yet-reaped legacy library.db* set, and any
        // ".dead-*" leftovers a rename-first Delete could not clean up yet — all are bytes the cache occupies.
        try { foreach (string f in Directory.EnumerateFiles(root, "library.*")) library += FileBytes(f); }
        catch { }
        try
        {
            if (Directory.Exists(Platform.LogFolder))
                foreach (string f in Directory.EnumerateFiles(Platform.LogFolder)) { logs += FileBytes(f); logFiles++; }
        }
        catch { }
        status = null;
        try { status = Spotify.Audio.DiskCache.Shared?.Status(); }
        catch (Exception ex) { Log.Warn("settings", "audio cache status failed", ex); }

        long runtime = TreeBytes(RuntimeDirectory());
        long store = FileBytes(Platform.StorePath) + TreeBytes(Path.Combine(root, "WaveeMusic"));
        long audio = status?.Bytes ?? TreeBytes(AudioCacheDirectory());
        long keys = FileBytes(LicenseDbPath());
        long images = TreeBytes(ImageCacheDirectory());
        return new StorageSnapshot(library, runtime, logs, logFiles, store, audio, keys, images,
            library + runtime + logs + store + audio + keys + images);
    }

    /// <summary>After a budget write: trim to the new policy (the cache re-reads the settings on every operation) and
    /// re-read its status, off the UI thread — the meter and the reserve line follow without a full census.</summary>
    static void RefreshAudioStatus()
    {
        var post = s_post;
        _ = Task.Run(() =>
        {
            AudioBodyCacheStatus? status = null;
            try
            {
                if (Spotify.Audio.DiskCache.Shared is { } cache) { cache.Trim(); status = cache.Status(); }
            }
            catch (Exception ex) { Log.Warn("settings", "audio cache trim failed", ex); }
            post(() => { s_audioStatus = status; Bump(); });
        });
    }

    // ══ 5. THE CLEARS (N11: each is behind ConfirmThen; each toasts; the storage ones re-run the census) ════════════

    /// <summary>Deletes only the ROLLED <c>wavee-*.log</c> files — never the live <c>wavee.log</c> — and toasts even when
    /// there was nothing to delete (parity 48a).</summary>
    static void DeleteOldLogs()
    {
        var post = s_post;
        _ = Task.Run(() =>
        {
            int deleted = 0;
            try
            {
                if (Directory.Exists(Platform.LogFolder))
                    foreach (string f in Directory.EnumerateFiles(Platform.LogFolder, "wavee-*.log"))
                    {
                        try { File.Delete(f); deleted++; }
                        catch (Exception ex) { Log.Warn("settings", "could not delete " + f, ex); }
                    }
            }
            catch (Exception ex) { Log.Warn("settings", "old log sweep failed", ex); }
            post(() =>
            {
                Notify.Say(deleted > 0
                    ? Loc.Format("settings.storage.oldLogsDeleted", ("count", deleted))
                    : Loc.Get(Strings.Settings.Storage.NoOldLogsDeleted), InfoBarSeverity.Success);
                RefreshStorage();
            });
        });
    }

    static void ClearAudioBodies()
        => ClearThenRecount(static () => Spotify.Audio.DiskCache.Shared?.ClearAll(), Strings.Settings.Storage.AudioCacheCleared);

    static void ClearSavedKeys() => ClearThenRecount(static () =>
    {
        ClearLicenseKeys?.Invoke();
        string db = LicenseDbPath();
        if (File.Exists(db)) File.Delete(db);
    }, Strings.Settings.Storage.LicenseKeysCleared);

    static void ClearMetadata() => ClearThenRecount(static () => ClearMetadataCache?.Invoke(), Strings.Settings.Storage.MetadataCleared);

    static void ClearThenRecount(Action work, string toastKey)
    {
        var post = s_post;
        _ = Task.Run(() =>
        {
            try { work(); }
            catch (Exception ex) { Log.Warn("settings", "storage clear failed (" + toastKey + ")", ex); }
            post(() =>
            {
                Notify.Say(Loc.Get(toastKey), InfoBarSeverity.Success);
                RefreshStorage();
            });
        });
    }

    // ══ 6. THE CACHE RELOCATION (W27: dialog 1 → Move | Start empty → dialog 2) ═══════════════════════════════════

    static void PickCacheLocation()
    {
        string? picked;
        try { picked = FilePicker.PickFolder(FluentApp.WindowHandle, Loc.Get(Strings.Settings.Storage.ChooseCacheFolder)); }
        catch (Exception ex) { Log.Warn("settings", "cache folder picker failed", ex); return; }
        if (!string.IsNullOrWhiteSpace(picked)) OfferRelocation(picked);
    }

    /// <summary>Dialog 1. <paramref name="newBase"/> is the PARENT the cache owns a child under; "" = the default root.
    /// "Move existing" is the default button (a move loses nothing).</summary>
    static void OfferRelocation(string newBase)
    {
        if (s_overlay is not { } overlay || Spotify.Audio.DiskCache.Shared is null) return;
        ContentDialog.Show(overlay, d =>
        {
            d.Title = Loc.Get(Strings.Settings.Storage.MoveCacheTitle);
            d.Message = Loc.Get(Strings.Settings.Storage.MoveCacheBody);
            d.PrimaryText = Loc.Get(Strings.Settings.Storage.MoveExisting);
            d.SecondaryText = Loc.Get(Strings.Settings.Storage.StartEmpty);
            d.CloseText = Loc.Get(Strings.Auth.Cancel);
            d.DefaultButton = ContentDialog.DefaultBtn.Primary;
            d.PrimaryClick = () => BeginRelocation(newBase, AudioCacheRelocationMode.Move);
            // Posted: the second dialog opens after the first has finished closing.
            d.SecondaryClick = () => s_post(() => OfferStartEmpty(newBase));
        });
    }

    /// <summary>Dialog 2 — what happens to the old cache (0.2.9 leaves its default button unset: Primary).</summary>
    static void OfferStartEmpty(string newBase)
    {
        if (s_overlay is not { } overlay) return;
        ContentDialog.Show(overlay, d =>
        {
            d.Title = Loc.Get(Strings.Settings.Storage.OldCacheTitle);
            d.Message = Loc.Get(Strings.Settings.Storage.OldCacheBody);
            d.PrimaryText = Loc.Get(Strings.Settings.Storage.DeleteOldCache);
            d.SecondaryText = Loc.Get(Strings.Settings.Storage.LeaveOldCache);
            d.CloseText = Loc.Get(Strings.Auth.Cancel);
            d.PrimaryClick = () => BeginRelocation(newBase, AudioCacheRelocationMode.StartEmptyDeleteOld);
            d.SecondaryClick = () => BeginRelocation(newBase, AudioCacheRelocationMode.StartEmptyKeepOld);
        });
    }

    /// <summary>The cache prepares the new owned root FIRST; the base path is persisted only after it succeeds.</summary>
    static void BeginRelocation(string newBase, AudioCacheRelocationMode mode)
    {
        var post = s_post;
        _ = Task.Run(async () =>
        {
            bool ok = false;
            try
            {
                if (Spotify.Audio.DiskCache.Shared is { } cache)
                    ok = await cache.PrepareRelocationAsync(newBase, mode).ConfigureAwait(false);
            }
            catch (Exception ex) { Log.Warn("settings", "audio cache relocation failed", ex); }
            post(() =>
            {
                if (!ok)
                {
                    Notify.Say(Loc.Get(Strings.Settings.Storage.CacheLocationFailed), InfoBarSeverity.Error);
                    return;
                }
                Platform.Settings.Set(Platform.Keys.AudioBodyCacheBasePath, newBase);
                Notify.Say(Loc.Get(Strings.Settings.Storage.CacheLocationChanged), InfoBarSeverity.Success);
                RefreshStorage();
                Bump();
            });
        });
    }

    // ══ 7. THE FACTORY RESET (G-094 R half; the wipe itself is owner S's `Platform.HostApplyFactoryReset`) ════════

    /// <summary>After the confirm: arm the reset (a custom audio-cache root rides as an extra root — it can live outside
    /// the profile), spawn the restart broker and end this process. The credential blob lives in store.json inside the
    /// profile, so the next launch's wipe signs the user out.</summary>
    static void RequestFactoryReset()
    {
        try
        {
            string[]? extra = Platform.Settings.Get(Platform.Keys.AudioBodyCacheBasePath).Length > 0 ? [AudioCacheDirectory()] : null;
            Platform.RequestFactoryResetAndRelaunch(extra);
        }
        catch (Exception ex)
        {
            Log.Error("settings", "factory reset could not be armed", ex);
            Notify.Say(Loc.Get("settings.storage.factoryResetFailed"), InfoBarSeverity.Error);
        }
    }

    // ══ 8. ABOUT: THE NOTICES FILE, THE DIAGNOSTICS COPY, THE IDENTITY STRINGS ═══════════════════════════════════

    const string IssuesUrl = "https://github.com/christosk92/WaveeMusic/issues";
    const string WebsiteUrl = "https://github.com/christosk92/WaveeMusic";
    const string PrivacyUrl = "https://github.com/christosk92/WaveeMusic/blob/main/PRIVACY.md";

    /// <summary>Staged beside Wavee.exe by ops/build/generate-third-party-notices.ps1 at publish/pack time; a plain
    /// `dotnet run` has none, which is exactly what <c>settings.about.noticesMissing</c> says.</summary>
    const string NoticesFileName = "THIRD-PARTY-NOTICES.txt";

    static string NoticesPath => Path.Combine(AppContext.BaseDirectory, NoticesFileName);

    /// <summary>Wavee's OWN license. Everything third-party is enumerated by the generated notices file, never by a list
    /// in code that drifts the moment a PackageReference changes.</summary>
    const string WaveeLicense =
        "Copyright (c) 2026 Christos Karapasias\n\n" +
        "Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated " +
        "documentation files (the \"Software\"), to deal in the Software without restriction, including without limitation " +
        "the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and " +
        "to permit persons to whom the Software is furnished to do so, subject to the following conditions:\n\n" +
        "The above copyright notice and this permission notice shall be included in all copies or substantial portions of " +
        "the Software.\n\n" +
        "THE SOFTWARE IS PROVIDED \"AS IS\", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO " +
        "THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE " +
        "AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF " +
        "CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER " +
        "DEALINGS IN THE SOFTWARE.";

    // Read once per process: the file is stamped at publish time and cannot change under a running build, and a read per
    // render would be disk I/O on the UI thread.
    static string? s_notices;

    static string ReadNotices()
    {
        if (s_notices is not null) return s_notices;
        string text;
        try { text = File.Exists(NoticesPath) ? File.ReadAllText(NoticesPath) : Loc.Get(Strings.Settings.About.NoticesMissing); }
        catch (Exception ex)
        {
            Log.Warn("settings", "third-party notices unreadable", ex);
            text = Loc.Get(Strings.Settings.About.NoticesMissing);   // unreadable reads the same as absent
        }
        return s_notices = text;
    }

    /// <summary>Open the shipped notices with the shell; absent → an Informational toast, unopenable → a Warning.</summary>
    static void OpenNotices()
    {
        if (!File.Exists(NoticesPath))
        {
            Notify.Say(Loc.Get(Strings.Settings.About.NoticesMissing), InfoBarSeverity.Informational);
            return;
        }
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(NoticesPath) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex)
        {
            Log.Warn("settings", "could not open the third-party notices", ex);
            Notify.Say(Loc.Get(Strings.Settings.About.NoticesMissing), InfoBarSeverity.Warning);
        }
    }

    /// <summary>"Copy diagnostics info": owner S's full block (<see cref="Feedback.DiagnosticsText"/>, the same text a
    /// report carries) when assigned, else the lines this page can vouch for.</summary>
    static void CopyDiagnostics()
    {
        string text;
        try { text = Feedback.DiagnosticsText?.Invoke() ?? AboutDiagnosticsText(); }
        catch (Exception ex) { Log.Warn("settings", "diagnostics text failed", ex); text = AboutDiagnosticsText(); }
        try { (s_hooks?.Clipboard ?? InputHooks.Current.Default.Clipboard)?.SetText(text); }
        catch (Exception ex) { Log.Warn("settings", "clipboard write failed", ex); return; }
        Notify.Say(Loc.Get(Strings.Settings.About.DiagnosticsCopied), InfoBarSeverity.Success);
    }

    /// <summary>0.2.9 `DiagInfoText` minus the playback-runtime outcome (owner S's): identity, OS, engine, GPU, data folder,
    /// feed, and the last receipts tick.</summary>
    public static string AboutDiagnosticsText()
        => Platform.Version.OneLine(ArchToken) + "\nOS: " + OsDescription + "\nEngine: FluentGpu · .NET " + Environment.Version
           + "\nGPU: " + ReceiptsGpuLine() + "\nData folder: " + Platform.LocalFolder
           + "\nFeed: " + (Update.Host.FeedUrl is { Length: > 0 } feed ? feed : Receipts.Dash) + "\n" + ReceiptsText;

    /// <summary>The machine architecture as the feed names it ("arm64" / "x64").</summary>
    static string ArchToken => RuntimeInformation.OSArchitecture switch
    {
        Architecture.Arm64 => "arm64",
        Architecture.X64 => "x64",
        var other => other.ToString().ToLowerInvariant(),
    };

    static string OsDescription => RuntimeInformation.OSDescription + " (" + RuntimeInformation.OSArchitecture + ")";

    /// <summary>The beta channel is a SIDE-BY-SIDE package: the link hands the user to App Installer.</summary>
    static string BetaFeedUrl => Update.RepoUrl + "/releases/download/wavee-beta/Wavee.Beta." + ArchToken + ".appinstaller";

    static GpuTier ReceiptsTier(GpuPowerTier tier) => tier switch
    {
        GpuPowerTier.Weak => GpuTier.Weak,
        GpuPowerTier.Strong => GpuTier.Strong,
        _ => GpuTier.Unknown,
    };

    static string ReceiptsGpuLine() => Receipts.GpuLine(GpuProfile.AdapterName, ReceiptsTier(GpuProfile.Tier), GpuProfile.IsSoftwareAdapter);

    /// <summary>Every update verb goes through the updater's one door, with the snapshot the button was drawn from.</summary>
    static void RunUpdate(UpdateRowAction action) => Notify.UpdateCommand?.Invoke(action, Notify.Update.Peek());

    static void OpenWhatsNew() => Shell.GoTo(new Shell.Route(Shell.RouteKind.WhatsNew));
}
