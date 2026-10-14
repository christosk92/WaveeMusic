// ── Screens/SettingsFacts.cs — the facts a settings row is enabled, worded and shown by ─────────────────────────────
//
// Role: CORE (themes plan §5.1, PR C13; extraction X34-X36). The Settings views read these inline: `PackageIdentity` for
// the spotify: row, the tray mode for four rows' enablement, `ToastNotifier` for the OS-block banner, a cold DXGI walk for
// the GPU picker, `File.Exists` and a notices read on the render path. They are `ISettingsFacts` now — one narrow
// interface the `SettingsDoc` rows ask (`Enabled`, `Caption`, `Visible`), with the live producers here and a fake in the
// tests — plus the producers that touch the OS or the disk, kept off the render path:
//   · `RefreshGpus()` enumerates the adapters (a cold walk) when the GPU card mounts, never during a render;
//   · `ToastDelivery()` is the one OS query behind the Notifications banner;
//   · `WarmNotices()` reads the shipped third-party notices on a pool thread when the page mounts, so the About tab's
//     first render finds the text (a render that gets there first still reads it itself, exactly once).

using FluentGpu.Rhi.D3D12;
using FluentGpu.WindowsApi.Notifications;
using FluentGpu.WindowsApi.Packaging;

namespace Wavee;

/// <summary>What the settings rows ask about the app. Every member is a cheap read; none writes.</summary>
public interface ISettingsFacts
{
    /// <summary>Developer mode (reactive when read inside a tab body, like the Appearance tab always did).</summary>
    bool DeveloperMode { get; }

    /// <summary>A packaged (MSIX) build: its manifest owns the protocols and the startup task.</summary>
    bool Packaged { get; }

    /// <summary>Where the notification-area icon shows.</summary>
    Tray.IconMode TrayMode { get; }

    /// <summary>Wavee starts when the user signs in to Windows.</summary>
    bool StartOnLogin { get; }

    /// <summary>The Windows-toast delivery gate of the notification policy.</summary>
    bool WindowsNotifications { get; }

    /// <summary>Quiet hours are on.</summary>
    bool QuietHours { get; }

    /// <summary>What Windows says about toast delivery for this app (the OS query).</summary>
    ToastDeliverySetting ToastDelivery { get; }

    /// <summary>A key deriver is installed, so Lossless can play (decision D7).</summary>
    bool CanDerive { get; }

    bool EqualizerEnabled { get; }
    bool CrossfadeEnabled { get; }

    /// <summary>The attached-videos roster store exists.</summary>
    bool VideoOverridesPresent { get; }

    /// <summary>The track page layout (<see cref="Prefs.DetailHero"/>'s Automatic / Hero).</summary>
    int PageLayout { get; }

    /// <summary>"Keep left-rail same size" is on.</summary>
    bool RailUniform { get; }

    /// <summary>Some per-scope rail size differs from its authored default.</summary>
    bool RailCustomized { get; }

    /// <summary>The sidebar is in Edit mode (its layout verbs refuse and say so).</summary>
    bool SidebarEditing { get; }

    /// <summary>The sidebar layout differs from its authored one (Reset is meaningful).</summary>
    bool SidebarModified { get; }

    /// <summary>The audio-body disk cache exists (it does not under --fake).</summary>
    bool AudioCacheAvailable { get; }

    /// <summary>The metadata-cache clear is wired.</summary>
    bool MetadataClearAvailable { get; }

    /// <summary>This build can send crash reports to a service.</summary>
    bool CrashServiceConfigured { get; }

    /// <summary>Crash reporting is effectively on (Ask or Auto).</summary>
    bool CrashReportingOn { get; }

    /// <summary>A test crash report is being written.</summary>
    bool TestCrashBusy { get; }
}

/// <summary>The live <see cref="ISettingsFacts"/> and the producers that touch the OS or the disk.</summary>
public sealed class LiveSettingsFacts : ISettingsFacts
{
    public bool DeveloperMode => Platform.Developer.Enabled.Value;
    public bool Packaged => SettingsFacts.IsPackaged();
    public Tray.IconMode TrayMode => Tray.ModeFrom(Platform.Settings.Get(Platform.Keys.TrayIconMode));
    public bool StartOnLogin => Platform.Settings.Get(Platform.Keys.StartOnLogin);
    public bool WindowsNotifications => Notify.Prefs.Policy().WindowsEnabled;
    public bool QuietHours => Notify.Prefs.Policy().Quiet.Enabled;
    public ToastDeliverySetting ToastDelivery => SettingsFacts.ToastDelivery();
    public bool CanDerive => Spotify.Audio.CanDerive;
    public bool EqualizerEnabled => Platform.Settings.Get(Platform.Keys.EqualizerEnabled);
    public bool CrossfadeEnabled => Platform.Settings.Get(Platform.Keys.CrossfadeEnabled);
    public bool VideoOverridesPresent => Video.Overrides.Present;
    public int PageLayout => Prefs.Clamp(Platform.Settings.Get(Platform.Keys.DetailPageLayout), 2);
    public bool RailUniform => Platform.Settings.Get(Platform.Keys.DetailRailUniform);
    public bool RailCustomized => Settings.HasCustomizedRail();
    public bool SidebarEditing => Sidebar.Editing.Value;
    public bool SidebarModified => SidebarLayoutRules.IsModified(Sidebar.State.Of(Sidebar.Layout.Value));
    public bool AudioCacheAvailable => Spotify.Audio.DiskCache.Shared is not null;
    public bool MetadataClearAvailable => Settings.ClearMetadataCache is not null;
    public bool CrashServiceConfigured => Crash.Uploader.Configured;

    public bool CrashReportingOn
        => Crash.ConsentPolicy.Effective((Crash.Reporting)Platform.Settings.Get(Platform.Keys.CrashReporting), Crash.Uploader.Configured)
           != Crash.Reporting.Off;

    public bool TestCrashBusy => SettingsFacts.TestCrashBusy.Value;
}

/// <summary>What the settings page hands its verbs from the component context (the overlay host, the input hooks, the UI
/// poster): published once per render by the page, read by the verbs. These were page statics of the view. ROOT-LIFETIME state
/// (<see cref="RootScope"/>): they belong to the mounted root, so a remounted root starts unbound (the verbs fall back to the
/// inline poster and the engine defaults) until its own Settings page renders.</summary>
public static class SettingsContext
{
    static readonly RootState<FluentGpu.Controls.IOverlayService?> s_overlay = new("Settings.Context.Overlay");
    static readonly RootState<FluentGpu.Hooks.InputHooks?> s_hooks = new("Settings.Context.Hooks");
    static readonly RootState<Action<Action>> s_post = new("Settings.Context.Post", static () => static a => a());

    public static FluentGpu.Controls.IOverlayService? Overlay => s_overlay.Value;
    public static FluentGpu.Hooks.InputHooks? Hooks => s_hooks.Value;
    public static Action<Action> Post => s_post.Value;

    public static void Bind(FluentGpu.Controls.IOverlayService overlay, FluentGpu.Hooks.InputHooks hooks, Action<Action> post)
    {
        s_overlay.Value = overlay;
        s_hooks.Value = hooks;
        s_post.Value = post;
    }
}

/// <summary>The settings page's fact producers.</summary>
public static class SettingsFacts
{
    /// <summary>The Auto zoom the Zoom row last offered ("Auto (N%)"): published by the row's picker each render, applied by
    /// the head item's pick.</summary>
    public static float AutoZoom { get; private set; } = 1f;

    public static void PublishAutoZoom(float suggestion) => AutoZoom = suggestion;

    /// <summary>True from the click until the test crash bundle is written and handed to the uploader (UI thread only). A
    /// signal, so the Developer group's button follows it.</summary>
    public static readonly FluentGpu.Signals.Signal<bool> TestCrashBusy = new(false);

    /// <summary>Bumped when the crash service answers an erasure request: the "Your data" card re-renders (the install id rotated).</summary>
    public static readonly FluentGpu.Signals.Signal<int> CrashDataEpoch = new(0);

    /// <summary>The process-wide live facts.</summary>
    public static ISettingsFacts Live { get; } = new LiveSettingsFacts();

    // ── what a view reads that no row carries (a theme never reaches the store, WVTH002) ───────────────────────────────

    /// <summary>The stored equalizer curve (the comma-joined gains the Sound group parses).</summary>
    public static string EqualizerGainsRaw() => Platform.Settings.Get(Platform.Keys.EqualizerGains);

    /// <summary>The crossfade length in seconds (the Sound group's slider and number box seed from it).</summary>
    public static double CrossfadeSeconds() => Settings.Quality.CrossfadeSeconds(Platform.Settings.Get(Platform.Keys.CrossfadeMs));

    /// <summary>When the update feed was last checked (unix ms), 0 = never: the About hero's provenance line when the snapshot has no stamp.</summary>
    public static long UpdateLastCheckedMs() => Platform.Settings.Get(Platform.Keys.UpdateLastCheckedMs);

    /// <summary>The on-device AI lyrics preferences' epoch: a switch flipped anywhere (the lyrics header, the card) re-reads the card's rows.</summary>
    public static FluentGpu.Signals.IReadSignal<int> AiLyricsEpoch => Prefs.AiLyrics.Epoch;

    /// <summary>The crash service's install id (boot ensures it exists; a render never writes the store).</summary>
    public static string CrashInstallId() => Crash.InstallId.Peek(Platform.Settings);

    /// <summary>The fixed audio-body budget in bytes, never below the cache's minimum.</summary>
    public static long AudioBudgetBytes()
        => Math.Max(Wavee.Sdk.Streams.ChunkDiskCache.MinBudgetBytes, Platform.Settings.Get(Platform.Keys.AudioBodyCacheBudgetBytes));

    /// <summary>The ids the sidebar's last layout migration dropped, comma-joined; "" when it dropped nothing.</summary>
    public static string SidebarMigrationDropped() => Platform.Settings.Get(Platform.Keys.SidebarMigrationDropped);

    /// <summary>A video-override row's track title: the catalog's when the playable is a known track, else null (the roster shows the uri).</summary>
    public static string? VideoOverrideTitle(string uri)
        => Entities.Current.Tracks.TryGetSlot(uri.AsSpan(), out int slot) && new Track(slot).Knows(TrackFields.Title) ? new Track(slot).Title : null;

    /// <summary>A video-override row's artist line, or null when the catalog does not know the track's artists.</summary>
    public static string? VideoOverrideArtistLine(string uri)
        => Entities.Current.Tracks.TryGetSlot(uri.AsSpan(), out int slot) && new Track(slot).Knows(TrackFields.Artists)
            ? Entities.Strings.Resolve(new Track(slot).ArtistLineId) : null;

    /// <summary>A packaged build; an unreadable identity reads as unpackaged.</summary>
    public static bool IsPackaged()
    {
        try { return PackageIdentity.IsPackaged; }
        catch (Exception) { return false; }
    }

    /// <summary>The one OS query behind the Notifications banner; an unreadable notifier is <c>Unknown</c> (no banner).</summary>
    public static ToastDeliverySetting ToastDelivery()
    {
        try { return ToastNotifier.Default.Setting; }
        catch (Exception) { return ToastDeliverySetting.Unknown; }
    }

    static IReadOnlyList<GpuAdapterDesc> s_gpus = [];

    /// <summary>The adapters the GPU card last enumerated (what the Graphics row's writer indexes into).</summary>
    public static IReadOnlyList<GpuAdapterDesc> Gpus => s_gpus;

    /// <summary>Enumerate the render adapters (a cold DXGI walk) and remember them for the row's writer. Called when the GPU
    /// card MOUNTS, never from a render; a failed walk is an empty list (the card then offers Automatic only).</summary>
    public static IReadOnlyList<GpuAdapterDesc> RefreshGpus()
    {
        try { s_gpus = GpuAdapterInfo.EnumerateAdapters(); }
        catch (Exception ex)
        {
            Log.Warn("settings", "GPU adapter enumeration failed", ex);
            s_gpus = [];
        }
        return s_gpus;
    }

    /// <summary>The combo index of the persisted GPU choice within <paramref name="adapters"/> (0 = Automatic): the LUID wins,
    /// else a name match (the name survives a reboot that renumbers LUIDs).</summary>
    public static int GpuIndexOf(IReadOnlyList<GpuAdapterDesc> adapters, long luid, string name)
    {
        int idx = 0;
        if (luid != 0L || name.Length > 0)
            for (int k = 0; k < adapters.Count; k++)
            {
                if (luid != 0L && adapters[k].Luid == luid) { idx = k + 1; break; }
                if (idx == 0 && name.Length > 0 && string.Equals(adapters[k].Name, name, StringComparison.Ordinal)) idx = k + 1;
            }
        return idx;
    }

    static Task<string>? s_noticesLoad;

    /// <summary>Start reading the shipped third-party notices on a pool thread (once per process). The About tab's first
    /// render finds the text; the file is stamped at publish time and cannot change under a running build.</summary>
    public static void WarmNotices(Func<string> read) => s_noticesLoad ??= Task.Run(read);

    /// <summary>The notices text if the warm read finished, else null (the caller reads it itself).</summary>
    public static string? WarmedNotices => s_noticesLoad is { IsCompletedSuccessfully: true } t ? t.Result : null;
}
