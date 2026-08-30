# Wavee auto-update + "What's new" — implementation plan (local/manual releases)

## Context

`docs/plans/wavee/auto-update-whatsnew-plan.md` + the research dossier + the Mica prototype
(`auto-update-whatsnew-mica.html`) define the target. Two corrections from the user drive this plan:

1. **No CI-created releases.** Releases are cut by scripts run by hand on this arm64 PC: build both arches
   (x64 via NativeAOT cross-compile — MSVC `HostArm64\x64` and `runtime.win-x64.microsoft.dotnet.ilcompiler` are
   present), sign locally with Azure Trusted Signing (`pack-wavee-msix.ps1 -TrustedSigning` already works: dlib, `az`,
   `metadata.json` present), and the script uploads with `gh`. Builds are **PlayPlay-inclusive** (junction required,
   `-PublicOnly` opt-in). The MSIX 4th part is a **committed counter** `<WaveeBuild>`.
2. **Everything ships in one pass**: tooling + update engine + notes pipeline + UI.

Facts that force the redesign (dossier, all verified): the current `ms-appinstaller:` hand-off is disabled by default
on consumer Windows (dead button that then reports `Downloaded`); `releases/latest/download/…` is repo-global and
belongs to the gallery's `v0.1.2` (the Wavee feed 404s); `ForceUpdateFromAnyVersion` **does** allow downgrades (runbook
§5 is wrong); a `-beta` tag cannot build today. Rules: no legacy paths, no env switches, no source-text tests, AOT-clean
(STJ source-gen, hand vtables, no `ComWrappers`), TerraFX only inside `FluentGpu.Windows`/`WindowsApi`.

Naming: Wavee already uses `WhatsNew*` for Spotify's new-releases feed (`NotificationCenterBridge.WhatsNewState`,
`WaveeSettings.NotificationsWhatsNewLastSeenMs`). Our types are therefore **`ReleaseNotes*`**; the user-facing route,
strings and asset file names stay "What's new" / `whatsnew.json`.

---

## 1. System map

```
 ┌── developer, by hand ───────────────────────────────────────────────────────────────────────┐
 │ src/apps/Wavee/Wavee.Version.props   <WaveeVersion>0.2.0 <WaveeCodename>Breaker <WaveeBuild>N│
 │ CHANGELOG.md  "## [0.2.0] - unreleased" + Added/Changed/Fixed/Removed/Known limitations       │
 │ ops/release/wavee/0.2.0/whatsnew.json + media/*.webp|*.mp4  (tagline, hero highlights, notices)│
 └───────────────┬─────────────────────────────────────────────────────────────────────────────┘
                 │  powershell -File ops\release\wavee-release.ps1  [-DryRun]
                 ▼
 ┌── ops/release/wavee-release.ps1 (phases; release-state.json ledger) ─────────────────────────┐
 │ 0 preflight ─ 1a bump+date ─ 2 Wavee.ReleaseTool validate ─ 1b commit+tag(local)             │
 │ 3 pack arm64 ─ 4 pack x64 ─ 5 sign(one signtool call) ─ 6 .appinstaller×2 ─ 7 stage+MANIFEST │
 │ 8 push main+tag ─ 9 gh release (draft→upload→publish) ─ 10 feed refresh (LAST) ─ 11 verify    │
 │   uses: ops/build/Wavee.Build.psm1 + ops/build/pack-wavee-msix.ps1 (one arch) + Wavee.Release.psm1│
 └───────────────┬──────────────────────────────────────────────┬───────────────────────────────┘
                 ▼                                              ▼
 GitHub release wavee-v0.2.0                        GitHub rolling release wavee-stable (anchor tag)
   Wavee_0.2.0.N_{arm64,x64}.msix  whatsnew.json      Wavee.arm64.appinstaller  Wavee.x64.appinstaller
   media/*  THIRD-PARTY-NOTICES.txt  MANIFEST.txt      whatsnew-index.json
   body = RELEASE_BODY.md                              (--clobber on every release)
                 │                                              │
                 │ 302 → release-assets.githubusercontent.com   │ GET (root Version) / App Installer OS poll
                 ▼                                              ▼
 ┌── Windows ─────────────────┐   ┌── Wavee.exe (packaged) ───────────────────────────────────────┐
 │ App Installer service      │   │ App/AppUpdateScheduler ─► App/AppInstallerUpdateService        │
 │  OnLaunch check + bg task; │◄──│   ReadFeedVersionAsync (GET feed)                              │
 │  silent apply next launch  │   │   IPackageUpdater.CheckUpdateAvailabilityAsync / GetAppInstallerInfo│
 │  (desktop apps: NO prompt) │   │   ApplyAsync ─► FluentGpu.WindowsApi/Packaging/PackageUpdater  │
 └────────────────────────────┘   │       RegisterApplicationRestart → ModuleHost.Dispose()        │
                                  │       IPackageManager6.AddPackageByAppInstallerFileAsync       │
 ┌── api.github.com (no token)┐   │       (ForceTargetAppShutdown) → progress → Windows relaunches │
 │ GET /issues/{n}  ≤20/open  │◄──│ App/ReleaseNotesStore  embedded → cache → release asset        │
 └────────────────────────────┘   │ UI: ReleaseNotesPage · AfterUpdateDialog · Settings›About ·    │
                                  │     Toast/NotificationPanel · ToastEscalator (OS toast)         │
                                  └───────────────────────────────────────────────────────────────┘
```

---

## 2. Versioning ("Trains") — code

### `src/apps/Wavee/Wavee.Version.props` (new)
```xml
<Project>
  <PropertyGroup>
    <!-- Hand-edited before a release: semver M.m.p or M.m.p-beta.N, and the per-MINOR codename (sea/wave series:
         Abyss 0.1, Breaker 0.2, Crest 0.3, Drift 0.4, Ebb, Fetch, Groundswell, Harbor, Inlet, Jetty, Kelp, Lagoon ...). -->
    <WaveeVersion>0.2.0</WaveeVersion>
    <WaveeCodename>Breaker</WaveeCodename>
    <!-- NEVER hand-edited. Monotonic release counter (max 65535) bumped + committed by ops/release/wavee-release.ps1.
         MSIX Identity/@Version = M.m.p.WaveeBuild - the ONLY thing Windows and AppUpdateVersion.IsNewer compare. -->
    <WaveeBuild>0</WaveeBuild>
  </PropertyGroup>
</Project>
```

### `src/apps/Wavee/Wavee.csproj` — replaces lines 15-19
```xml
<Import Project="Wavee.Version.props" />
<PropertyGroup>
  <Version>$(WaveeVersion)</Version>
  <WaveeChannel Condition="'$(WaveeChannel)' == ''">dev</WaveeChannel>
  <!-- A local `dotnet run` is "0.2.0-dev"; the pack script stamps "<semver>+build.<N>.sha.<sha7>". -->
  <InformationalVersion Condition="'$(InformationalVersion)' == ''">$(WaveeVersion)-dev</InformationalVersion>
</PropertyGroup>
<ItemGroup>
  <AssemblyMetadata Include="Codename"       Value="$(WaveeCodename)" />
  <AssemblyMetadata Include="Channel"        Value="$(WaveeChannel)" />
  <AssemblyMetadata Include="PackageVersion" Value="$(WaveePackageVersion)" />
  <AssemblyMetadata Include="Commit"         Value="$(WaveeCommit)" />
  <AssemblyMetadata Include="BuildDate"      Value="$(WaveeBuildDate)" />
</ItemGroup>
```

### `src/apps/Wavee.Core/Versioning/WaveeVersionInfo.cs` (new, pure, unit-tested)
```csharp
namespace Wavee.Core;

/// <summary>Everything the app knows about its own build, parsed once from assembly metadata.</summary>
public sealed record WaveeVersionInfo(
    string SemVer,            // "0.2.0" | "0.4.0-beta.2" | "0.2.0-dev"
    string Core,              // "0.2.0"
    int? Beta,                // 2 for -beta.2, else null
    string Quad,              // "0.2.0.17"  ("" for a dev build)
    string Codename,          // "Breaker"   ("" if unstamped)
    string Channel,           // "stable" | "beta" | "dev"
    string Commit,            // "d4227b3" or ""
    string BuildDate)         // ISO-8601 UTC or ""
{
    public bool IsDev => Channel == "dev" || Quad.Length == 0;
    public string Display => IsDev ? $"Wavee {SemVer}" : Beta is int b
        ? $"Wavee {Core} \u201C{Codename}\u201D \u00B7 Beta {b}" : $"Wavee {Core} \u201C{Codename}\u201D";
    /// <summary>RFC 9110 product token — ThirdParty/GitHub client only, never the Spotify-facing UA.</summary>
    public string UserAgent(string os, string arch) => $"Wavee/{Core} (build {Quad}; {Channel}; {os}; {arch})";
    public string OneLine(string os, string arch) => $"{Display} \u00B7 build {Quad} \u00B7 {Commit} \u00B7 {BuildDate} \u00B7 {arch}";
    /// <summary>The value written to app.lastRunVersion (the quad; a dev build writes its semver so it never "updates").</summary>
    public string LastRunKey => IsDev ? SemVer : Quad;

    public static WaveeVersionInfo Parse(string? informational, IReadOnlyDictionary<string, string> metadata)
    {
        string inf = string.IsNullOrWhiteSpace(informational) ? "dev" : informational.Trim();
        int plus = inf.IndexOf('+'); string semver = plus > 0 ? inf[..plus] : inf;
        int dash = semver.IndexOf('-'); string core = dash > 0 ? semver[..dash] : semver;
        int? beta = null;
        if (dash > 0 && semver.AsSpan(dash + 1).StartsWith("beta.") && int.TryParse(semver.AsSpan(dash + 6), out int b)) beta = b;
        string Get(string k) => metadata.TryGetValue(k, out var v) ? v : "";
        string channel = Get("Channel"); if (channel.Length == 0) channel = "dev";
        return new(semver, core, beta, Get("PackageVersion"), Get("Codename"), channel, Get("Commit"), Get("BuildDate"));
    }
}
```
`App/AppVersion.cs` becomes: `public static WaveeVersionInfo Info { get; } = WaveeVersionInfo.Parse(inf, ReadMetadata())`
(`AssemblyMetadataAttribute` scan on `typeof(AppVersion).Assembly`), `Current => Info.SemVer`, `IsDev => Info.IsDev`.
`Services.HostVersion` is deleted; About/crash header/diagnostics read `AppVersion.Info`.

---

## 3. Update engine — code

### 3.1 Contract `src/apps/Wavee.Core/Notifications/AppUpdate.cs` (rewrite)
```csharp
namespace Wavee.Core;

public enum AppUpdateState { None, Checking, Available, Snoozed, Downloading, Installing, Completed, Failed }
public enum AppUpdateFailureKind { Network, Metered, PackagesInUse, VersionConflict, SideloadPolicy, AppInstallerOutdated, NotAssociated, Unknown }
public sealed record AppUpdateFailure(AppUpdateFailureKind Kind, int HResult, string Message);

/// <summary>One immutable observation. Published whole so the UI never reads a torn state.</summary>
public sealed record AppUpdateSnapshot(
    AppUpdateState State,
    string? TargetQuad,                // feed root Version when Available/Snoozed/Downloading/Installing; the running quad when Completed
    string? TargetSemVer,              // from whatsnew-index when known ("0.3.0")
    string? TargetCodename,            // "Crest" when known
    int ProgressPercent,               // 0..100 while Downloading/Installing
    AppUpdateFailure? Failure,
    bool AutoUpdateAssociated,         // GetAppInstallerInfo() != null (packaged only)
    long LastCheckedMs)
{
    public static readonly AppUpdateSnapshot Idle = new(AppUpdateState.None, null, null, null, 0, null, false, 0);
}

public interface IAppUpdateService
{
    AppUpdateSnapshot Current { get; }
    IObservable<int> Changed { get; }         // revision ticks; readers re-read Current
    string FeedUrl { get; }
    Task CheckAsync(CancellationToken ct);
    Task ApplyAsync(CancellationToken ct);    // "Update now": download+stage+restart (packaged) / open release page (unpackaged)
    void Snooze();                            // "Later": Available → Snoozed for this TargetQuad
    void Acknowledge();                       // clears Completed / Failed
}

public sealed class NullAppUpdateService : IAppUpdateService { /* Current = Idle, every call inert */ }
```
`AppUpdateNotification` (`NotificationModels.cs`) becomes `(long Timestamp, bool IsUnread, AppUpdateSnapshot Snapshot)`;
`NotificationMerge` is untouched (it only pins the record); `NotificationSimulator` injects snapshots.

### 3.2 `src/apps/Wavee/App/AppInstallerUpdateService.cs` (rewrite; ctor shape pinned for `Services.cs` L348)
```csharp
sealed class AppInstallerUpdateService : IAppUpdateService
{
    const string Repo = "https://github.com/christosk92/WaveeMusic";
    readonly SimpleEvent<int> _changed = new();
    readonly IAppSettings _settings; readonly HttpClient _http; readonly IPackageUpdater _updater; readonly IWaveeLog _log;
    readonly WaveeVersionInfo _me; readonly string _arch;
    readonly object _gate = new(); int _rev;
    public static AppInstallerUpdateService? Instance { get; private set; }
    public AppUpdateSnapshot Current { get; private set; } = AppUpdateSnapshot.Idle;
    public IObservable<int> Changed => _changed;
    public string FeedUrl { get; }

    public AppInstallerUpdateService(IAppSettings settings, HttpClient github, WaveeVersionInfo me, string arch, IPackageUpdater updater, IWaveeLog log)
    {
        (_settings, _http, _me, _arch, _updater, _log) = (settings, github, me, arch, updater, log);
        string feed = me.Channel == "beta" ? "wavee-beta" : "wavee-stable";
        string asset = me.Channel == "beta" ? "Wavee.Beta." : "Wavee.";
        FeedUrl = $"{Repo}/releases/download/{feed}/{asset}{arch}.appinstaller";

        string lastRun = settings.Get(WaveeSettings.LastRunVersion);
        if (AppUpdateVersion.IsFirstRunAfterUpdate(lastRun, me.LastRunKey) && !me.IsDev)
        {
            Current = AppUpdateSnapshot.Idle with { State = AppUpdateState.Completed, TargetQuad = me.Quad, TargetSemVer = me.Core, TargetCodename = me.Codename };
            settings.Set(WaveeSettings.ReleaseNotesPendingFrom, lastRun);     // AfterUpdateDialog reads + clears this
            log.Info("update", "updated: " + lastRun + " -> " + me.Quad);
        }
        settings.Set(WaveeSettings.LastRunVersion, me.LastRunKey);
        Instance = this;
    }

    public async Task CheckAsync(CancellationToken ct)
    {
        Publish(Current with { State = AppUpdateState.Checking, Failure = null });
        try
        {
            string? remote = await ReadFeedVersionAsync(ct).ConfigureAwait(false);          // unchanged XmlReader, DTD prohibited
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _settings.Set(WaveeSettings.UpdateLastCheckedMs, now);
            bool assoc = _updater.IsSupported && _updater.GetAppInstallerInfo() is not null;
            if (remote is null) { Fail(AppUpdateFailureKind.Network, 0, "The update feed did not carry a version.", assoc, now); return; }

            if (AppUpdateVersion.IsNewer(remote, _me.Quad) && !_me.IsDev)
            {
                bool snoozed = _settings.Get(WaveeSettings.UpdateSnoozedVersion) == remote;
                var index = await ReleaseNotesStore.Instance?.PeekIndexAsync(ct);          // null if not prefetched; no network here
                var entry = index?.Find(remote);
                Publish(new(snoozed ? AppUpdateState.Snoozed : AppUpdateState.Available, remote, entry?.Version, entry?.Name, 0, null, assoc, now));
                if (!NetworkPolicy.IsMetered) _ = ReleaseNotesStore.Instance?.PrefetchAsync(remote, ct);   // index + next notes, best effort
            }
            else if (Current.State != AppUpdateState.Completed)
                Publish(AppUpdateSnapshot.Idle with { AutoUpdateAssociated = assoc, LastCheckedMs = now });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Fail(AppUpdateFailureKind.Network, ex.HResult, ex.Message, Current.AutoUpdateAssociated, Current.LastCheckedMs); _log.Warn("update", "check failed", ex); }
    }

    public async Task ApplyAsync(CancellationToken ct)
    {
        if (!_updater.IsSupported) { LoginView.OpenUrl($"{Repo}/releases/tag/wavee-v{Current.TargetSemVer ?? AppUpdateVersion.ReleaseTagVersion(Current.TargetQuad)}"); return; }
        if (NetworkPolicy.IsMetered && !_settings.Get(WaveeSettings.UpdateOnMetered)) { Fail(AppUpdateFailureKind.Metered, 0, "", Current.AutoUpdateAssociated, Current.LastCheckedMs); return; }
        if (Current.State is not (AppUpdateState.Available or AppUpdateState.Snoozed or AppUpdateState.Failed)) return;
        Publish(Current with { State = AppUpdateState.Downloading, ProgressPercent = 0, Failure = null });
        try
        {
            ModuleHost.Current?.Dispose();                        // package-identity children must be gone before ForceTargetAppShutdown
            var result = await _updater.ApplyFromAppInstallerAsync(new Uri(FeedUrl),
                pct => Publish(Current with { State = AppUpdateState.Downloading, ProgressPercent = pct }), ct).ConfigureAwait(false);
            if (result.IsRegistered || result.HResult == 0)
            {
                _settings.Set(WaveeSettings.UpdateSnoozedVersion, "");
                Publish(Current with { State = AppUpdateState.Installing, ProgressPercent = 100 });   // Windows terminates us next
            }
            else Fail(PackageUpdater.Classify(result.HResult), result.HResult, result.ErrorText, Current.AutoUpdateAssociated, Current.LastCheckedMs);
        }
        catch (OperationCanceledException) { Publish(Current with { State = AppUpdateState.Available, ProgressPercent = 0 }); }
        catch (Exception ex) { Fail(AppUpdateFailureKind.Unknown, ex.HResult, ex.Message, Current.AutoUpdateAssociated, Current.LastCheckedMs); }
    }

    public void Snooze() { if (Current.TargetQuad is { } q) { _settings.Set(WaveeSettings.UpdateSnoozedVersion, q); Publish(Current with { State = AppUpdateState.Snoozed }); } }
    public void Acknowledge() => Publish(AppUpdateSnapshot.Idle with { AutoUpdateAssociated = Current.AutoUpdateAssociated, LastCheckedMs = Current.LastCheckedMs });

    void Fail(AppUpdateFailureKind k, int hr, string msg, bool assoc, long last)
        => Publish(new(AppUpdateState.Failed, Current.TargetQuad, Current.TargetSemVer, Current.TargetCodename, 0, new(k, hr, msg), assoc, last));
    void Publish(AppUpdateSnapshot s) { lock (_gate) { Current = s; } _changed.OnNext(Interlocked.Increment(ref _rev)); }
}
```
`PackageUpdater.Classify(int hr)`: `0x80073D02`→PackagesInUse, `0x80073D06|0x80073CFB`→VersionConflict, `0x80073CFF`→SideloadPolicy,
`0x80072F76|0x80072EE7|0x80072EFD`→Network, `0x80070057`→AppInstallerOutdated, else Unknown. `AppUpdateScheduler` is unchanged.

### 3.3 `src/FluentGpu.WindowsApi/Packaging/` — interop

TerraFX projects `IPackageStatics`, `IPackage2…9` (`IPackage6.CheckUpdateAvailabilityAsync`, `IPackage8.GetAppInstallerInfo`),
`IAppInstallerInfo`, `IPackageUpdateAvailabilityResult`, `IAsyncInfo`, `IAsyncOperation<T>` — reuse them exactly like
`WindowsGeolocationProvider` (`RoActivateInstance` + `QueryInterface` + `IAsyncInfo` status polling). It does **not**
project `Windows.Management.Deployment`; those are hand vtables in the `IStringMap` style:

```csharp
// PackageManagerInterop.cs — call-OUT vtables. Slot numbers: IInspectable 0-5, then the interface's own methods in
// declaration order from windows.management.deployment.h (SDK 10.0.26100). IIDs verified in the dossier §2.3.
[SupportedOSPlatform("windows10.0.19041")]
internal static class DeploymentIids
{
    public static readonly Guid IPackageManager  = new("9a7d4b65-5e8f-4fc7-a2e5-7f6925cb8b53");
    public static readonly Guid IPackageManager3 = new("daad9948-36f1-41a7-9188-bc263e0dcb72");
    public static readonly Guid IPackageManager6 = new("0847e909-53cd-4e4f-832e-57d180f6e447");
    public static readonly Guid IDeploymentResult  = new("2563b9ae-b77d-4c1f-8a7b-20e6ad515ef3");
    public static readonly Guid IDeploymentResult2 = new("fc0e715c-5a01-4bd7-bcf1-381c8c82e04a");
    public static readonly Guid IAsyncOpDeployment = new("5a97aab7-b6ea-55ac-a5dc-d5b164d94e94"); // IAsyncOperationWithProgress<DeploymentResult,DeploymentProgress>
    public const string RuntimeClass = "Windows.Management.Deployment.PackageManager";
    public const uint DeploymentOptions_ForceTargetAppShutdown = 0x40;
}

internal readonly unsafe struct IPackageManagerVtbl      // slot 6 = AddPackageAsync, ... slot 12 = FindPackageByUserSecurityIdPackageFullName
{
    readonly void** _lpVtbl;
    static IPackageManagerVtbl* Self(in IPackageManagerVtbl s) => (IPackageManagerVtbl*)Unsafe.AsPointer(ref Unsafe.AsRef(in s));
    internal int QueryInterface(Guid* iid, void** ppv) => ((delegate* unmanaged<IPackageManagerVtbl*, Guid*, void**, int>)_lpVtbl[0])(Self(in this), iid, ppv);
    internal uint Release() => ((delegate* unmanaged<IPackageManagerVtbl*, uint>)_lpVtbl[2])(Self(in this));
    /// <summary>HRESULT FindPackageByUserSecurityIdPackageFullName(HSTRING userSid, HSTRING packageFullName, IPackage** package) — slot 12.</summary>
    internal int FindPackageForUser(HSTRING sid, HSTRING fullName, IPackage** pkg)
        => ((delegate* unmanaged<IPackageManagerVtbl*, HSTRING, HSTRING, IPackage**, int>)_lpVtbl[12])(Self(in this), sid, fullName, pkg);
}
internal readonly unsafe struct IPackageManager3Vtbl     // slot 6 = GetDefaultPackageVolume
{
    readonly void** _lpVtbl;
    internal int GetDefaultPackageVolume(void** volume) => ((delegate* unmanaged<IPackageManager3Vtbl*, void**, int>)_lpVtbl[6])((IPackageManager3Vtbl*)Unsafe.AsPointer(ref Unsafe.AsRef(in this)), volume);
    internal uint Release() => ((delegate* unmanaged<IPackageManager3Vtbl*, uint>)_lpVtbl[2])((IPackageManager3Vtbl*)Unsafe.AsPointer(ref Unsafe.AsRef(in this)));
}
internal readonly unsafe struct IPackageManager6Vtbl     // slot 10 = AddPackageByAppInstallerFileAsync (after ProvisionPackageForAllUsersAsync(6), AddPackageByUriAsync? no — verify slot order in the header before use)
{
    readonly void** _lpVtbl;
    /// <summary>HRESULT AddPackageByAppInstallerFileAsync(IUriRuntimeClass* appInstallerFileUri, AddPackageByAppInstallerOptions options, IPackageVolume* targetVolume, IAsyncOperationWithProgress** op)</summary>
    internal int AddPackageByAppInstallerFileAsync(void* uri, uint options, void* volume, void** op)
        => ((delegate* unmanaged<IPackageManager6Vtbl*, void*, uint, void*, void**, int>)_lpVtbl[SLOT_AddByAppInstaller])((IPackageManager6Vtbl*)Unsafe.AsPointer(ref Unsafe.AsRef(in this)), uri, options, volume, op);
    internal uint Release() => ((delegate* unmanaged<IPackageManager6Vtbl*, uint>)_lpVtbl[2])((IPackageManager6Vtbl*)Unsafe.AsPointer(ref Unsafe.AsRef(in this)));
    // SLOT_AddByAppInstaller: IPackageManager6 = [6] ProvisionPackageForAllUsersAsync, [7] AddPackageByUriAsync(uri, deps, options), [8] StagePackageByUriAsync,
    //                         [9] RegisterPackageByFullNameAsync, [10] AddPackageByAppInstallerFileAsync ... — A2 confirms against the header and pins the constant.
}
internal readonly unsafe struct IAsyncOpDeploymentVtbl   // IAsyncOperationWithProgress<DeploymentResult,DeploymentProgress>: [6] put_Progress [7] get_Progress [8] put_Completed [9] get_Completed [10] GetResults
{
    readonly void** _lpVtbl;
    internal int GetResults(void** result) => ((delegate* unmanaged<IAsyncOpDeploymentVtbl*, void**, int>)_lpVtbl[10])((IAsyncOpDeploymentVtbl*)Unsafe.AsPointer(ref Unsafe.AsRef(in this)), result);
}
internal readonly unsafe struct IDeploymentResultVtbl    // [6] get_ErrorText(HSTRING*) [7] get_ActivityId(GUID*) [8] get_ExtendedErrorCode(HRESULT*)
{
    readonly void** _lpVtbl;
    internal int get_ErrorText(HSTRING* s) => ((delegate* unmanaged<IDeploymentResultVtbl*, HSTRING*, int>)_lpVtbl[6])((IDeploymentResultVtbl*)Unsafe.AsPointer(ref Unsafe.AsRef(in this)), s);
    internal int get_ExtendedErrorCode(int* hr) => ((delegate* unmanaged<IDeploymentResultVtbl*, int*, int>)_lpVtbl[8])((IDeploymentResultVtbl*)Unsafe.AsPointer(ref Unsafe.AsRef(in this)), hr);
}
```
Progress: no CCW. `WinRtAsync.WaitAsync(nint op, IProgress<int>? progress, TimeSpan poll, CancellationToken)` polls
`IAsyncInfo.get_Status` every 100 ms (Geolocation's `WaitForAsync` lifted into a shared helper) and, for the deployment
op, reads `DeploymentProgress` via `IAsyncOperationWithProgress.get_Progress` (slot 7 → struct `{uint state; uint percentage}`).
`Windows.Foundation.Uri` is created with `RoGetActivationFactory("Windows.Foundation.Uri", IUriRuntimeClassFactory)` →
`CreateUri(HSTRING)` (TerraFX projects `IUriRuntimeClassFactory`).

```csharp
// IPackageUpdater.cs (public surface; the app codes against this)
public readonly record struct AppInstallerInfo(Uri? Uri, DateTimeOffset LastChecked, DateTimeOffset PausedUntil, bool OnLaunch, bool AutomaticBackgroundTask);
public enum PackageUpdateAvailability { Unknown, NoUpdates, Available, Required, Error }
public readonly record struct PackageDeploymentResult(bool IsRegistered, int HResult, string ErrorText);
public interface IPackageUpdater
{
    bool IsSupported { get; }                                   // PackageIdentity.IsPackaged && OS >= 19041
    AppInstallerInfo? GetAppInstallerInfo();                   // IPackageStatics.get_Current → IPackage8.GetAppInstallerInfo (null = no association)
    Task<PackageUpdateAvailability> CheckUpdateAvailabilityAsync(CancellationToken ct);   // FindPackageForUser("", PackageFullName) → IPackage6 (Package.Current → Access denied)
    Task<PackageDeploymentResult> ApplyFromAppInstallerAsync(Uri feed, Action<int> progress, CancellationToken ct);
}
// PackageUpdater.cs — all WinRT calls on one MTA worker thread (Thread { IsBackground = true }, SetApartmentState(MTA)); a
// TaskCompletionSource per call. ApplyFromAppInstallerAsync: RegisterApplicationRestart(null, 0) (kernel32) →
// RoActivateInstance(PackageManager) → QI IPackageManager6 + IPackageManager3 → GetDefaultPackageVolume →
// AddPackageByAppInstallerFileAsync(uri, ForceTargetAppShutdown, volume, &op) → WinRtAsync.WaitAsync(op, progress) →
// GetResults → IDeploymentResult(.2): ExtendedErrorCode / ErrorText / IsRegistered → release everything.
```

### 3.4 Sequence — "Update now"
```
 UI (About/toast)     AppInstallerUpdateService        PackageUpdater (MTA thread)             Windows
 ─────────────        ─────────────────────────        ───────────────────────────             ───────
 ApplyAsync ────────► Publish(Downloading 0)
                      ModuleHost.Current.Dispose()
                      updater.Apply(feed) ───────────► RegisterApplicationRestart
                                                        AddPackageByAppInstallerFileAsync ───► GET feed → GET msix (302 → signed URL)
                                                        poll IAsyncInfo.Status / get_Progress ◄─ {Processing, 37}
 toast bar 37% ◄───── Publish(Downloading 37) ◄──────── progress(37)
                                                        Completed → GetResults ◄────────────── DeploymentResult
 "Restarting…" ◄───── Publish(Installing 100) ◄──────── {IsRegistered=true}
                      settings flush                                                            ForceTargetAppShutdown → exit
                                                                                                relaunch (restart registration)
 next process: ctor → LastRunVersion ≠ quad → Completed → ReleaseNotesPendingFrom set → AfterUpdateDialog + OS "Updated" toast
```

---

## 4. Release notes — model, parsers, store — code

### 4.1 `src/apps/Wavee.Core/ReleaseNotes/ReleaseNotesDocument.cs` (new; STJ source-gen; shared with the tool)
```csharp
namespace Wavee.Core.ReleaseNotes;

public sealed class ReleaseNotesDocument
{
    public int Schema { get; set; } = 1;
    public string Product { get; set; } = "wavee";
    public string Version { get; set; } = "";          // "0.3.0"
    public string PackageVersion { get; set; } = "";   // "0.3.0.17"
    public string Name { get; set; } = "";             // codename
    public string Tagline { get; set; } = "";
    public string Date { get; set; } = "";             // yyyy-MM-dd
    public string Channel { get; set; } = "stable";
    public string Lang { get; set; } = "en";
    public string MinOs { get; set; } = "10.0.19041.0";
    public string[] Arch { get; set; } = [];
    public ReleaseLinks Links { get; set; } = new();
    public ReleaseHighlight[] Highlights { get; set; } = [];
    public ReleaseSection[] Sections { get; set; } = [];
    public ReleaseNotice[] Notices { get; set; } = [];
    public ReleaseContributor[] Contributors { get; set; } = [];
    public string GeneratedAt { get; set; } = "";
    public ReleaseMedia[] Media { get; set; } = [];
}
public sealed class ReleaseLinks { public string Release { get; set; } = ""; public string Changelog { get; set; } = ""; public string Compare { get; set; } = ""; }
public sealed class ReleaseHighlight { public string Id = ""; public string Title = ""; public string Body = ""; public string Kind = "new"; /* new|improved|rebuilt */ public ReleaseMediaRef? Media; public string? DeepLink; public int[] Issues = []; }
public sealed class ReleaseMediaRef { public string Kind = "image"; /* image|video */ public string Src = ""; public string? Poster; public string Alt = ""; public int Width; public int Height; public long Bytes; }
public sealed class ReleaseSection { public string Kind = "added"; /* added|changed|fixed|removed|deprecated|security|known */ public ReleaseItem[] Items = []; }
public sealed class ReleaseItem { public string Id = ""; public string? Scope; public string Text = ""; public ReleaseIssue[] Issues = []; public ReleasePr[] Prs = []; public ReleaseContributor[] Contributors = []; }
public sealed class ReleaseIssue { public string Repo = ""; public int Number; public string Title = ""; public string State = "open"; public string? StateReason; public bool IsPullRequest; }
public sealed class ReleasePr { public string Repo = ""; public int Number; public string Title = ""; public bool Merged; }
public sealed class ReleaseContributor { public string Login = ""; public bool FirstTime; }
public sealed class ReleaseNotice { public string Kind = "info"; /* breaking|warning|info */ public string Text = ""; }
public sealed class ReleaseMedia { public string Src = ""; public long Bytes; public string Sha256 = ""; }
public sealed class ReleaseNotesIndex { public int Schema = 1; public string Product = "wavee"; public ReleaseNotesIndexEntry[] Releases = []; public ReleaseNotesIndexEntry? Find(string quadOrSemver) => …; }
public sealed class ReleaseNotesIndexEntry { public string Version = ""; public string PackageVersion = ""; public string Name = ""; public string Date = ""; public string Channel = "stable"; }

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ReleaseNotesDocument))] [JsonSerializable(typeof(ReleaseNotesIndex))] [JsonSerializable(typeof(IssueStateCache))]
public partial class ReleaseNotesJsonContext : JsonSerializerContext { }
```
(Fields are shown compactly; the real classes use `{ get; set; }` properties — STJ source-gen needs properties.)

### 4.2 `MarkdownLite.cs` — inline tokenizer (pure)
```csharp
public enum InlineKind { Text, Bold, Code, Link, Issue, Pr, Mention, Url }
public readonly record struct InlineToken(InlineKind Kind, string Text, string? Target = null, int Number = 0, string? Repo = null);

public static class MarkdownLite
{
    /// <summary>**bold**, *em* (rendered as weight), `code`, [text](url), bare http(s) URLs, #123, owner/repo#123, !123, @handle,
    /// backslash escapes. No headings/images/HTML. Never throws; unknown syntax falls through as text.</summary>
    public static InlineToken[] Tokenize(string s)
    {
        var outp = new List<InlineToken>(8); var buf = new StringBuilder();
        void Flush() { if (buf.Length > 0) { outp.Add(new(InlineKind.Text, buf.ToString())); buf.Clear(); } }
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\\' && i + 1 < s.Length) { buf.Append(s[++i]); continue; }
            if (c == '`') { int e = s.IndexOf('`', i + 1); if (e > i) { Flush(); outp.Add(new(InlineKind.Code, s[(i + 1)..e])); i = e; continue; } }
            if (c == '*' && i + 1 < s.Length && s[i + 1] == '*') { int e = s.IndexOf("**", i + 2, StringComparison.Ordinal); if (e > i) { Flush(); outp.Add(new(InlineKind.Bold, s[(i + 2)..e])); i = e + 1; continue; } }
            if (c == '*') { int e = s.IndexOf('*', i + 1); if (e > i + 1) { Flush(); outp.Add(new(InlineKind.Bold, s[(i + 1)..e])); i = e; continue; } }
            if (c == '[') { int close = s.IndexOf("](", i, StringComparison.Ordinal); int end = close > 0 ? s.IndexOf(')', close) : -1;
                            if (end > close) { Flush(); outp.Add(new(InlineKind.Link, s[(i + 1)..close], s[(close + 2)..end])); i = end; continue; } }
            if ((c == '#' || c == '!') && TryRef(s, i, out int n, out string? repo, out int len)) { Flush(); outp.Add(new(c == '#' ? InlineKind.Issue : InlineKind.Pr, s.Substring(i, len), null, n, repo)); i += len - 1; continue; }
            if (c == '@' && (i == 0 || !char.IsLetterOrDigit(s[i - 1])) && TryHandle(s, i, out string h)) { Flush(); outp.Add(new(InlineKind.Mention, h)); i += h.Length; continue; }
            if (c == 'h' && (s.AsSpan(i).StartsWith("https://") || s.AsSpan(i).StartsWith("http://"))) { int e = i; while (e < s.Length && !char.IsWhiteSpace(s[e]) && s[e] != ')' ) e++; Flush(); outp.Add(new(InlineKind.Url, s[i..e], s[i..e])); i = e - 1; continue; }
            buf.Append(c);
        }
        Flush(); return outp.ToArray();
    }
    // owner/repo#123 is recognised when the token is preceded by [A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+ immediately before '#'.
    static bool TryRef(string s, int i, out int number, out string? repo, out int len) { … }
    static bool TryHandle(string s, int i, out string handle) { … }   // @[A-Za-z0-9-]{1,39}
}
```
`ReleaseNotesText.ToSpans(InlineToken[] tokens, ReleaseItem item, Action<string> openUrl) → TextSpan[]` (app side) maps
Text→`RichTextBlock.Run`, Bold→`RichTextBlock.Bold`, Code→`new TextSpan(t, FontFamily: "Cascadia Code", Size: 12f)`,
Link/Url→`RichTextBlock.Hyperlink(text, () => openUrl(target))`, Issue/Pr/Mention→`Hyperlink` to GitHub. Issue **chips**
are separate elements after the paragraph (see §5), so issue tokens inside the text render as plain links.

### 4.3 `ChangelogParser.cs` (pure) — Keep a Changelog 1.1 + `Known limitations`
```csharp
public sealed record ChangelogRelease(string Version, string? Date, ReleaseSection[] Sections);
public static class ChangelogParser
{
    static readonly Regex Heading = new(@"^## \[(?<v>[^\]]+)\](?: - (?<d>\d{4}-\d{2}-\d{2}|unreleased))?\s*$", RegexOptions.Compiled);
    static readonly Regex Section = new(@"^### (?<k>Added|Changed|Deprecated|Removed|Fixed|Security|Known limitations)\s*$", RegexOptions.Compiled);
    static readonly Regex Bullet  = new(@"^- (?:\*\*)?(?:(?<scope>[A-Z][A-Za-z ]{1,16}):\s)?(?<text>.+?)(?:\s\((?<refs>(?:[#!]\d+(?:,\s*)?)+)\))?\s*$", RegexOptions.Compiled);
    public static IReadOnlyList<ChangelogRelease> Parse(string markdown) { … }      // continuation lines (indented) join the bullet; unknown headings are skipped
    public static ChangelogRelease? Find(string markdown, string version) => Parse(markdown).FirstOrDefault(r => r.Version == version);
}
```
Kinds map: `Known limitations` → `"known"`. `refs` become `ReleaseIssue{Number}`/`ReleasePr{Number}` with `Repo` defaulted
to the product repo; the tool fills `Title/State/StateReason/IsPullRequest` from the API.

### 4.4 `src/apps/Wavee/App/ReleaseNotesStore.cs`
```csharp
sealed class ReleaseNotesStore
{
    public static ReleaseNotesStore? Instance { get; private set; }
    readonly HttpClient _github; readonly string _cacheRoot; readonly string _embeddedRoot; readonly IWaveeLog _log; readonly string _feedRelease;
    readonly IssueStateBudget _budget;                                 // pure: ≤20 per page-open, 24 h per issue, stops on 403 / x-ratelimit-remaining: 0
    public Signal<ReleaseNotesIndex?> Index { get; } = new(null);
    public ReleaseNotesStore(HttpClient github, string appDataRoot, string feedRelease, IWaveeLog log)
    { _cacheRoot = Path.Combine(appDataRoot, "cache", "whatsnew"); _embeddedRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "whatsnew"); … Instance = this; }

    /// <summary>embedded (current version only) → cache → releases/download/wavee-v{semver}/whatsnew.json. Never throws; null = nothing anywhere.</summary>
    public async Task<ReleaseNotesDocument?> GetAsync(string semver, CancellationToken ct)
    {
        if (TryReadEmbedded(semver) is { } e) return e;
        string path = Path.Combine(_cacheRoot, semver, "whatsnew.json");
        if (File.Exists(path) && TryRead(path) is { } c) return c;
        try { var bytes = await _github.GetByteArrayAsync($"{Repo}/releases/download/wavee-v{semver}/whatsnew.json", ct);   // asset GET: not REST, no quota
              Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllBytesAsync(path, bytes, ct); return JsonSerializer.Deserialize(bytes, ReleaseNotesJsonContext.Default.ReleaseNotesDocument); }
        catch (Exception ex) { _log.Warn("whatsnew", "fetch failed " + semver, ex); return null; }
    }
    public Task<ReleaseNotesIndex?> PeekIndexAsync(CancellationToken ct) => Task.FromResult(Index.Peek() ?? TryReadIndexCache());
    public async Task PrefetchAsync(string? targetQuad, CancellationToken ct) { await RefreshIndexAsync(ct); if (Index.Peek()?.Find(targetQuad ?? "") is { } e) _ = await GetAsync(e.Version, ct); }
    public async Task RefreshIndexAsync(CancellationToken ct) { /* GET releases/download/{feed}/whatsnew-index.json → cache/index.json → Index.Value */ }
    /// <summary>Live issue states while the page is open. Returns the (issueKey → state) map merged over the snapshot; budgeted.</summary>
    public async Task<IssueStateCache> RefreshIssueStatesAsync(ReleaseNotesDocument doc, CancellationToken ct) { /* GET https://api.github.com/repos/{repo}/issues/{n}, Accept: application/vnd.github+json, UA = AppVersion.Info.UserAgent */ }
    public string MediaPath(ReleaseNotesDocument doc, string src) => embedded ? Path.Combine(_embeddedRoot, src) : Path.Combine(_cacheRoot, doc.Version, src);   // ImageEl.Source takes a file path/URL
}
```
`ReleaseNotesRange.Between(string lastSeenSemver, string currentSemver, ReleaseNotesIndex index, string channel) → ReleaseNotesIndexEntry[]`
(pure) selects `(lastSeen, current]` newest-first for stacking. `HttpPools` gains `HttpPool.GitHub` (15 s, HTTP/1.1,
`User-Agent: AppVersion.Info.UserAgent`, `Accept: application/vnd.github+json`).

### 4.5 Settings keys (`Platform/AppSettings.cs`)
```csharp
public static readonly SettingKey<string> UpdateSnoozedVersion     = new("app.update.snoozedVersion", "");
public static readonly SettingKey<int>    UpdatePolicy             = new("app.update.policy", 0);        // 0 background/next launch (OS), 1 install on quit, 2 notify only
public static readonly SettingKey<bool>   UpdateOnMetered          = new("app.update.onMetered", false);
public static readonly SettingKey<bool>   ReleaseNotesAutoShow     = new("app.whatsnew.autoShow", true);
public static readonly SettingKey<string> ReleaseNotesLastSeen     = new("app.whatsnew.lastSeenVersion", "");   // semver
public static readonly SettingKey<string> ReleaseNotesPendingFrom  = new("app.whatsnew.pendingFrom", "");       // set by the updater ctor on Completed; the dialog clears it
```

---

## 5. UI — component tree and code

### 5.1 Component tree
```
WaveeShell
 ├─ ContentHost.PageFor("whatsnew") → BoxEl{Key="page:whatsnew"} → Embed.Comp(() => new ReleaseNotesPage(r.Arg))
 │    ReleaseNotesPage : Component  (UseContext(Overlay.Service), UseContext(HistoryStore.NavCtx), UseState<ReleaseNotesView>)
 │     ├─ SinceBanner            (shown when ReleaseNotesRange returns >1)                      [Only the latest]
 │     ├─ ScrollView(BoxEl Direction=1 Gap=L)
 │     │   ├─ ReleaseNotesHero   (pills Latest/Stable|Beta/quad · "Wavee 0.3 «Crest»" · tagline · meta · Open on GitHub / Copy link)
 │     │   ├─ HighlightStrip     → HighlightCard ×≤3  (ImageEl poster | MediaPlayerElement when !ReducedMotion && cached · title · body · deep link)
 │     │   ├─ ChangelogSection ×N (kind icon+colour · title · count · Show all)  → ChangelogItem ×M
 │     │   │     ChangelogItem = BoxEl row: [ScopeChip] RichTextBlock.Paragraph(spans) [IssueChip…] [PrChip…] [Avatars]
 │     │   ├─ NoticesBar        (InfoBar per notice: breaking=Warning, info=Informational)
 │     │   └─ Footer            ("Chips show the issue's current GitHub state · as of <generatedAt>")
 │     └─ ReleaseRail            (Flow.For over index entries: dot · version · codename · date · YOU / unread dot / Beta pill)
 ├─ AfterUpdateDialog            (overlay.Open(... PopupChrome.Modal); mounted once from WaveeShell when ReleaseNotesPendingFrom != "" && ReleaseNotesAutoShow)
 │     ├─ DialogHero (Updated pill · "0.2.1 → 0.3.0.17" · Welcome to Wavee 0.3 «Crest» · tagline)
 │     ├─ HighlightCard ×≤3 (poster only)
 │     └─ Footer: CheckBox "Don't show this after updates" · [Full release notes] · [Got it]
 ├─ SettingsPage.AboutTab
 │     ├─ AboutHero        (mark · Display · quad pill · channel pill · state pill · sha/arch · installed/last-checked · [primary] · What's new →)
 │     ├─ UpdateStatusCard (SettingsCard: state sentence · ProgressBar while Downloading · [Restart now] when Installing)
 │     ├─ ReleaseChannelCard (link to beta .appinstaller in v1; ComboBox in phase 2)
 │     ├─ UpdatePolicyCard (RadioButtons 3 options) · MeteredCard (ToggleSwitch) · AutoShowCard (ToggleSwitch)
 │     └─ LinksCard        (Open What's new (+InfoBadge.Dot when unread) · Send feedback · Copy diagnostics · …)
 ├─ NotificationPanel → AppUpdateRow(snapshot)  (actions per state; progress row while Downloading)
 ├─ Toast strip (Toast.Show, DedupeKey "update")  ← AppUpdateToasts.Consider(prev, next) in NotificationCenterBridge
 └─ ToastEscalator → OS toast (ToastBuilder.Progress data-bound; ToastNotifier.Update for progress; launch wavee://open?route=whatsnew&arg=<semver>)
```

### 5.2 `Features/ReleaseNotes/ReleaseNotesPage.cs`
```csharp
sealed class ReleaseNotesPage(string? versionArg) : Component
{
    readonly Signal<bool> _onlyLatest = new(false);
    readonly Signal<ReleaseNotesView?> _view = new(null);      // loaded off-thread; assigned on the UI thread via HostDispatch

    public override Element Render()
    {
        var overlay = UseContext(Overlay.Service);
        var go = UseContext(HistoryStore.NavCtx);
        var svc = UseContext(Services.Slot);
        var view = _view.Value; bool onlyLatest = _onlyLatest.Value;      // subscribe

        UseEffect(() =>
        {
            var cts = new CancellationTokenSource();
            _ = LoadAsync(svc, versionArg, cts.Token);                  // embedded → cache → remote; then RefreshIssueStatesAsync (budgeted) → _view.Value = enriched
            svc?.Settings.Set(WaveeSettings.ReleaseNotesLastSeen, AppVersion.Info.Core);
            return () => cts.Cancel();
        }, DepKey.Empty);

        if (view is null) return new BoxEl { Grow = 1f, Direction = 1, Children = [ SettingsShared.Loading() ] };

        var releases = onlyLatest ? [view.Releases[0]] : view.Releases;
        var main = new List<Element>(8);
        if (view.Releases.Length > 1 && !onlyLatest) main.Add(SinceBanner(view, () => _onlyLatest.Value = true));
        main.Add(ReleaseNotesHero.Create(view.Releases[0], svc));
        main.Add(HighlightStrip.Create(view.MergedHighlights, svc, go));
        foreach (var r in releases)
        {
            if (releases.Length > 1) main.Add(ReleaseDivider(r));         // "0.2.1 Breaker · 14 Aug 2026" when stacked
            foreach (var s in r.Doc.Sections) main.Add(ChangelogSection.Create(s, r.IssueStates, svc));
            foreach (var n in r.Doc.Notices) main.Add(InfoBar.Create(n.Kind == "breaking" ? InfoBarSeverity.Warning : InfoBarSeverity.Informational, n.Text, "", isClosable: false));
        }
        main.Add(new TextEl(Strings.WhatsNew.AsOf(view.Releases[0].Doc.GeneratedAt)) { Size = 12f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap });

        return new BoxEl
        {
            Grow = 1f, Direction = 0, Gap = Spacing.L, Padding = new Edges4(Spacing.PageWide, Spacing.M, Spacing.PageWide, 0f),
            Children =
            [
                ScrollView(new BoxEl { Direction = 1, Gap = Spacing.L, Children = main.ToArray() }) with { Grow = 1f, Shrink = 1f, MinWidth = 0f },
                ReleaseRail.Create(view, versionArg, v => go?.Invoke("whatsnew", v)),
            ],
        };
    }
}
sealed record ReleaseNotesView(ReleaseEntry[] Releases, ReleaseHighlight[] MergedHighlights);   // newest first; MergedHighlights ≤3
sealed record ReleaseEntry(ReleaseNotesDocument Doc, IssueStateCache IssueStates, bool IsYou, bool IsUnread);
```
Wireframe = prototype scene 1 (`auto-update-whatsnew-mica.html#whatsnew`):
```
┌ ✦ Since you last looked: 2 releases — 0.2.1 Breaker → 0.3.0 Crest        [Only the latest] ┐ ┌ RELEASES ─┐
│ ┌ HERO ─────────────────────────────────────────────────────────────────┐                 │ │ ● 0.3.0    │
│ │ [Latest][Stable][0.3.0.17]                       [Open on GitHub ↗]  │                 │ │   Crest    │
│ │ Wavee 0.3 «Crest»                                 [Copy link]        │                 │ │ ○ 0.2.1 YOU│
│ │ tagline · Released · Size · Requires · Tag                            │                 │ │ ○ 0.2.0    │
│ └───────────────────────────────────────────────────────────────────────┘                 │ │ ○ 0.1.x    │
│ HIGHLIGHTS  [media][New] Docked video …  [Rebuilt] Queue …  [Improved] Lyrics …            │ └───────────┘
│ [+] Added 5   ┌ PLAYER **Docked video** — …  (● #412 closed)(■ !430)  ◉◉ ┐                │
│               │ QUEUE  Drag to reorder …       (● #388 closed)         ◉  │                │
│ [~] Changed 3 … [✓] Fixed 14 [Show all] … [−] Removed 1 … [!] Known limitations 3         │
└────────────────────────────────────────────────────────────────────────────────────────────┘
```

### 5.3 `ChangelogItem` + `IssueChip`
```csharp
static class ChangelogItem
{
    public static Element Create(ReleaseItem item, IssueStateCache states, Action<string> openUrl)
    {
        var spans = ReleaseNotesText.ToSpans(MarkdownLite.Tokenize(item.Text), openUrl);
        var trailing = new List<Element>(item.Issues.Length + item.Prs.Length + 1);
        foreach (var i in item.Issues) trailing.Add(IssueChip.Create(i, states.Lookup(i), openUrl));
        foreach (var p in item.Prs)    trailing.Add(IssueChip.Pr(p, openUrl));
        if (item.Contributors.Length > 0) trailing.Add(Avatars.Create(item.Contributors));
        return new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Start, Padding = new Edges4(Spacing.M, 9f, Spacing.M, 9f),
            BorderWidth = 0f, Children =
            [
                new BoxEl { Direction = 0, Gap = 6f, Wrap = true, Grow = 1f, Shrink = 1f, MinWidth = 0f, Children =
                    [ item.Scope is { } sc ? ScopeChip(sc) : Spacer(0f), RichTextBlock.Paragraph(spans) with { Size = 13f }, ..trailing ] },
            ],
        };
    }
}
static class IssueChip
{
    // neutral (no data) → open / closed-completed / not-planned / duplicate / pr-merged. Color dot + "#412" mono + state word.
    public static BoxEl Create(ReleaseIssue issue, IssueState? live, Action<string> openUrl)
    {
        string state = live?.State ?? issue.State; string? reason = live?.StateReason ?? issue.StateReason;
        ColorF dot = state == "open" ? Tok.SystemSuccess : reason == "not_planned" ? Tok.TextTertiary : Tok.AccentDefault;
        string word = Loc.Get(state == "open" ? Strings.WhatsNew.Issue.Open : reason == "not_planned" ? Strings.WhatsNew.Issue.NotPlanned : Strings.WhatsNew.Issue.Closed);
        string url = $"https://github.com/{issue.Repo}/issues/{issue.Number}";
        return ToolTip.Wrap(new BoxEl
        {
            Direction = 0, Gap = 4f, AlignItems = FlexAlign.Center, Padding = new Edges4(5f, 0f, 7f, 0f), Height = 18f,
            Corners = CornerRadius4.All(9f), Fill = Tok.FillControlDefault, BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault,
            Cursor = CursorKind.Hand, Role = AccessibilityRole.Link, OnClick = () => openUrl(url),
            Children = [ new BoxEl { Width = 8f, Height = 8f, Corners = CornerRadius4.All(4f), Fill = dot },
                         new TextEl("#" + issue.Number) { Size = 11f, Weight = 600, FontFamily = "Cascadia Code" },
                         new TextEl(word) { Size = 11f, Color = Tok.TextTertiary } ],
        }.Interactive(Interaction.Subtle), issue.Title);
    }
}
```

### 5.4 `AfterUpdateDialog.cs` — opened from `WaveeShell` (next to the crash-notice effect, `WaveeShell.cs` ~L618)
```csharp
// WaveeShell: UseEffect(() => { if (_settings.Get(WaveeSettings.ReleaseNotesPendingFrom) is { Length: > 0 } from && _settings.Get(WaveeSettings.ReleaseNotesAutoShow)
//     && !SetupGating.IsPending(_settings) && _settings.Get(WaveeSettings.PendingCrashReport).Length == 0)
//     AfterUpdateDialog.Open(_overlay, _settings, from, AppVersion.Info, ReleaseNotesStore.Instance, key => GoNav(key, null)); }, DepKey.Empty);
static class AfterUpdateDialog
{
    public static OverlayHandle Open(IOverlayService overlay, IAppSettings settings, string fromQuad, WaveeVersionInfo me, ReleaseNotesStore? store, Action<string> nav)
    {
        settings.Set(WaveeSettings.ReleaseNotesPendingFrom, "");                       // one shot, whatever happens next
        var handle = overlay.Open(static () => NodeHandle.Null, () => Embed.Comp(() => new Plate(fromQuad, me, store, nav)), FlyoutPlacement.BottomCenter,
            new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.Modal, Chrome: PopupChrome.Modal) { ScrimVisual = true });
        Plate.Close = () => handle.Close();
        return handle;
    }
    sealed class Plate(string fromQuad, WaveeVersionInfo me, ReleaseNotesStore? store, Action<string> nav) : Component
    {
        public static Action? Close;
        readonly Signal<ReleaseNotesDocument?> _doc = new(null);
        readonly Signal<bool> _dontShow = new(false);
        public override Element Render()
        {
            var settings = UseContext(Services.Slot)?.Settings;
            var doc = _doc.Value;
            UseEffect(() => { _ = store?.GetAsync(me.Core, CancellationToken.None).ContinueWith(t => HostDispatch.Current?.Post(() => _doc.Value = t.Result)); return null; }, DepKey.Empty);
            var highlights = doc?.Highlights ?? [];
            return new BoxEl
            {
                Width = 720f, Direction = 1, Corners = CornerRadius4.All(Radii.Overlay), Fill = Tok.BackgroundSolidBase, ClipToBounds = true,
                Children =
                [
                    new BoxEl { Direction = 1, Gap = Spacing.S, Padding = new Edges4(26f, 22f, 26f, 18f), Children =
                        [ HStack(8f, Pill(Loc.Get(Strings.WhatsNew.Dialog.Updated), accent: true), Pill($"{AppUpdateVersion.ReleaseTagVersion(fromQuad)} \u2192 {me.Quad}", mono: true)),
                          new TextEl(Strings.WhatsNew.Dialog.Welcome(me.Core, me.Codename)) { Size = 26f, Weight = 600 },
                          new TextEl(doc?.Tagline ?? "") { Size = 14f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap } ] },
                    new BoxEl { Direction = 0, Gap = Spacing.M, Padding = new Edges4(26f, 6f, 26f, 14f), Children = highlights.Take(3).Select(h => HighlightCard.Compact(h, store!)).ToArray() },
                    new BoxEl { Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Padding = new Edges4(26f, 14f, 26f, 14f), Fill = Tok.LayerOnAcrylicFillDefault, Children =
                        [ CheckBox.Create(Loc.Get(Strings.WhatsNew.Dialog.DontShow), _dontShow, v => settings?.Set(WaveeSettings.ReleaseNotesAutoShow, !v)),
                          Spacer(),
                          Button.Create(Loc.Get(Strings.WhatsNew.Dialog.Full), () => { Close?.Invoke(); nav("whatsnew"); }),
                          Button.Accent(Loc.Get(Strings.WhatsNew.Dialog.GotIt), () => Close?.Invoke()) ] },
                ],
            };
        }
    }
}
```

### 5.5 Settings › About — `AboutHero` + `UpdateStatusCard` (replaces `CheckForUpdates` toast plumbing)
```csharp
Element AboutTab(Services? svc, InputHooks hooks)
{
    var me = AppVersion.Info; var upd = svc?.AppUpdate ?? new NullAppUpdateService();
    int rev = UseObservable(upd.Changed);            // existing helper pattern: subscribe → re-render on every publish
    var s = upd.Current;
    return SettingsTabStack(
        AboutHero(me, s, upd, svc),
        UpdateStatusCard(s, upd),
        SettingsCard.Create(new() { Header = Loc.Get(Strings.Update.Channel.Title), Description = Loc.Get(Strings.Update.Channel.Hint), HeaderIcon = Icons.Info,
            Content = HyperlinkButton.Create(Loc.Get(Strings.Update.Channel.Beta), BetaFeedUrl) }),
        SettingsCard.Create(new() { Header = Loc.Get(Strings.Update.Policy.Title), Alignment = SettingsCard.ContentAlignment.Vertical,
            Content = RadioButtons.Create([ Loc.Get(Strings.Update.Policy.Background), Loc.Get(Strings.Update.Policy.OnQuit), Loc.Get(Strings.Update.Policy.Notify) ],
                UseSettingSignal(WaveeSettings.UpdatePolicy), v => svc?.Settings.Set(WaveeSettings.UpdatePolicy, v)) }),
        SettingsCard.Create(new() { Header = Loc.Get(Strings.Update.Metered.Title), Description = Loc.Get(Strings.Update.Metered.Hint),
            Content = ToggleSwitch.Create(UseSettingSignal(WaveeSettings.UpdateOnMetered), v => svc?.Settings.Set(WaveeSettings.UpdateOnMetered, v)) }),
        SettingsCard.Create(new() { Header = Loc.Get(Strings.Update.AutoShow.Title), Description = Loc.Get(Strings.Update.AutoShow.Hint),
            Content = ToggleSwitch.Create(UseSettingSignal(WaveeSettings.ReleaseNotesAutoShow), v => svc?.Settings.Set(WaveeSettings.ReleaseNotesAutoShow, v)) }),
        SettingsSectionHeader("Wavee right now", Icons.Info), Embed.Comp(() => new WaveeNowReceipts()),
        AboutLinksCard(svc, hooks, DiagInfo, os),           // gains "Open What's new" + InfoBadge.Dot when ReleaseNotesLastSeen != me.Core
        SettingsSectionHeader(Loc.Get(Strings.Settings.About.Licenses), Icons.Document), ..LicenseExpanders());
}
static Element UpdateStatusCard(AppUpdateSnapshot s, IAppUpdateService upd) => SettingsCard.Create(new()
{
    Header = Loc.Get(Strings.Update.Status.Title), HeaderIcon = Icons.Sync,
    Description = s.State switch
    {
        AppUpdateState.None        => Loc.Get(Strings.Update.State.UpToDate),
        AppUpdateState.Checking    => Loc.Get(Strings.Update.State.Checking),
        AppUpdateState.Available   => Strings.Update.State.Available(s.TargetQuad ?? "", s.TargetCodename ?? ""),
        AppUpdateState.Snoozed     => Strings.Update.State.Snoozed(s.TargetQuad ?? ""),
        AppUpdateState.Downloading => Strings.Update.State.Downloading(s.TargetCodename ?? ""),
        AppUpdateState.Installing  => Loc.Get(Strings.Update.State.Installing),
        AppUpdateState.Completed   => Strings.Update.State.JustUpdated(AppVersion.Info.Codename),
        AppUpdateState.Failed      => Strings.Update.Failure.For(s.Failure!),
        _ => "",
    },
    Content = s.State == AppUpdateState.Downloading
        ? HStack(8f, ProgressBar.Create(UseProgressSignal(s.ProgressPercent), 180f), new TextEl(s.ProgressPercent + "%") { Size = 12f, FontFamily = "Cascadia Code" })
        : PrimaryUpdateButton(s, upd),
});
static Element PrimaryUpdateButton(AppUpdateSnapshot s, IAppUpdateService upd) => s.State switch
{
    AppUpdateState.Available or AppUpdateState.Snoozed => Button.Accent(Loc.Get(Strings.Update.Action.UpdateNow), () => _ = upd.ApplyAsync(CancellationToken.None)),
    AppUpdateState.Failed  => HStack(8f, Button.Accent(Loc.Get(Strings.Update.Action.Retry), () => _ = upd.ApplyAsync(CancellationToken.None)), Button.Create(Loc.Get(Strings.Update.Action.OpenReleasePage), OpenReleasePage)),
    AppUpdateState.Checking or AppUpdateState.Installing => Button.Create(Loc.Get(Strings.Update.State.Checking), () => { }, isEnabled: false),
    _ => Button.Create(Loc.Get(Strings.Update.Action.Check), () => _ = upd.CheckAsync(CancellationToken.None)),
};
```
About wireframe = prototype scene 2 (`#about`). `UseObservable`/`UseSettingSignal`/`UseProgressSignal` are tiny hooks in
`Features/Shell/SettingsShared.cs` (a `FloatSignal` re-set from the snapshot so the bar binds without re-render).

### 5.6 Toasts, notification centre, OS toast — one artefact per state
`App/AppUpdateToasts.cs` (pure decision table, unit-tested) maps `(previous, next)` snapshot → `ToastPlan?`:

| next.State | in-app `Toast.Show` (DedupeKey `"update"`) | NotificationPanel row actions | OS toast (ToastEscalator; tag `live:update`) |
|---|---|---|---|
| Available | `Strings.Update.Toast.Available(name)` + first highlight title · [Update now][What's new][Later] | Update now / What's new / Later | — |
| Downloading | sticky, `CustomContent` = ProgressBar bound to `ProgressPercent` | progress row | `ToastBuilder.Progress(dataBound)` shown once; `ToastNotifier.Update({progressValue, progressStatus})` per 5% — only when the window is not foreground |
| Installing | "Restarting…" | — | progress 100% "Restarting…" |
| Completed | Success · `Strings.Update.Toast.Updated(name)` · [What's new] | What's new / Dismiss | "Wavee updated to 0.3 Crest" [See what's new] → `wavee://open?route=whatsnew&arg=<semver>` |
| Failed | Error · mapped reason · [Retry][Open release page] | Retry / Dismiss | — |

`ToastEscalator` L168-173: the three hard-coded titles move to `Strings.Update.Os.*`; launch arg → `wavee://open?route=whatsnew&arg=<semver>`.
`NotificationPanel` L195-215: `Download`→`ApplyAsync`, `Downloaded` arm deleted, `SeeWhatsNew` → `go("whatsnew", semver)` instead of `LoginView.OpenUrl`.
Route: `ShellRoutes.s_exact` += `"whatsnew"`; `ContentHost.PageFor` arm `r.Name == "whatsnew"` → `Embed.Comp(() => new ReleaseNotesPage(r.Arg))`, `Key = "page:whatsnew:" + (r.Arg ?? "")`; `WaveeShell.GoDeepLinkOpen` already passes unknown-route checks via `ShellRoutes.IsKnown`.

### 5.7 Loc keys (`assets/loc/en-US.json`) — `whatsnew.*`, `update.*`
```json
"whatsnew": { "title": "What's new", "since": "Since you last looked: {count} releases — {from} → {to}. Showing everything new.",
  "onlyLatest": "Only the latest", "highlights": "Highlights", "showAll": "Show all {count}", "asOf": "Issue states as of {date}",
  "section": { "added": "Added", "changed": "Changed", "fixed": "Fixed", "removed": "Removed", "deprecated": "Deprecated", "security": "Security", "known": "Known limitations" },
  "issue": { "open": "open", "closed": "closed", "notPlanned": "not planned", "merged": "merged" },
  "dialog": { "updated": "Updated", "welcome": "Welcome to Wavee {version} \u201C{name}\u201D", "dontShow": "Don't show this after updates", "full": "Full release notes", "gotIt": "Got it" } },
"update": { "status": { "title": "Update status" },
  "state": { "upToDate": "You're on the latest release.", "checking": "Checking…", "available": "Wavee {version} {name} is available.", "snoozed": "{version} is waiting — it installs the next time Wavee starts.",
             "downloading": "Downloading {name} in the background. Playback continues.", "installing": "Restarting to finish updating…", "justUpdated": "Updated to {name} just now." },
  "action": { "check": "Check for updates", "updateNow": "Update now", "later": "Later", "retry": "Retry", "openReleasePage": "Open release page", "restartNow": "Restart now", "whatsNew": "What's new", "repair": "Repair auto-update" },
  "policy": { "title": "How updates install", "background": "Download in the background, install on next launch", "onQuit": "Install when I quit Wavee", "notify": "Only notify me" },
  "metered": { "title": "Download on metered connections", "hint": "Off: waits for an unmetered network." },
  "autoShow": { "title": "Show \u201CWhat's new\u201D after an update", "hint": "A short summary the first time the new version opens." },
  "channel": { "title": "Release channel", "hint": "Beta installs side by side with Wavee and gets features a few weeks early.", "beta": "Get Wavee Beta" },
  "failure": { "packagesInUse": "Close other Wavee windows and try again.", "versionConflict": "This version can't be installed over the current one.", "sideloadPolicy": "Sideloading is turned off on this PC.",
               "network": "Couldn't reach GitHub. Try again later.", "appInstallerOutdated": "Windows App Installer needs an update from the Microsoft Store.", "metered": "Waiting for an unmetered network.", "unknown": "Update failed ({code})." },
  "toast": { "available": "Wavee {name} is available", "updated": "Updated to Wavee {name}", "downloading": "Downloading {name}…" },
  "os": { "downloading": "Updating Wavee", "ready": "Wavee {name} is ready", "updated": "Wavee updated to {name}", "seeWhatsNew": "See what's new" } }
```

---

## 6. Release tool — `src/apps/Wavee.ReleaseTool/Program.cs` (console, AOT, refs `Wavee.Core`)
```
wavee-release validate --semver 0.2.0 --quad 0.2.0.17 --codename Breaker --channel stable --changelog CHANGELOG.md
                       --notes ops/release/wavee/0.2.0 --out artifacts/release/0.2.0/notes --repo christosk92/WaveeMusic
                       [--previous-index <file>] [--github-token <tok>]
```
```csharp
static int Validate(Args a)
{
    var errors = new List<string>();
    var rel = ChangelogParser.Find(File.ReadAllText(a.Changelog), a.Semver) ?? Fail("CHANGELOG has no ## [" + a.Semver + "] entry");
    if (rel.Date is null or "unreleased") errors.Add("CHANGELOG entry is not dated");
    var doc = JsonSerializer.Deserialize(File.ReadAllBytes(Path.Combine(a.Notes, "whatsnew.json")), ReleaseNotesJsonContext.Default.ReleaseNotesDocument)!;
    if (doc.Version != a.Semver) errors.Add("whatsnew.json version != " + a.Semver);
    if (doc.Name != a.Codename)  errors.Add("whatsnew.json name != " + a.Codename);
    doc.PackageVersion = a.Quad; doc.Channel = a.Channel; doc.Date = rel.Date!; doc.Sections = rel.Sections;
    long total = 0; var names = new HashSet<string>();
    foreach (var h in doc.Highlights) foreach (var src in new[] { h.Media?.Src, h.Media?.Poster }.Where(s => s is not null))
    {   var p = Path.Combine(a.Notes, src!); if (!File.Exists(p)) { errors.Add("missing media " + src); continue; }
        if (Path.GetExtension(p).Equals(".gif", StringComparison.OrdinalIgnoreCase)) errors.Add("GIF not allowed: " + src);
        long len = new FileInfo(p).Length; total += len; if (!names.Add(Path.GetFileName(p))) errors.Add("duplicate media basename " + src);
        if (len > (p.EndsWith(".mp4") ? 600_000 : 150_000)) errors.Add("media too large " + src); }
    if (total > 1_500_000) errors.Add("media total > 1.5 MB");
    foreach (var h in doc.Highlights) if (h.DeepLink is { } dl && !dl.StartsWith("wavee://open?route=")) errors.Add("deep link must be wavee://open?route=…: " + dl);
    using var gh = GitHubClient(a);                                              // Authorization: Bearer <token> when given; UA "Wavee.ReleaseTool/<semver>"
    foreach (var item in doc.Sections.SelectMany(s => s.Items)) { foreach (var i in item.Issues) Snapshot(gh, i, errors); foreach (var p in item.Prs) SnapshotPr(gh, p, errors); }
    doc.GeneratedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"); doc.Links = new() { Release = $"https://github.com/{a.Repo}/releases/tag/wavee-v{a.Semver}", … };
    doc.Media = MediaHashes(a.Notes, doc);
    if (errors.Count > 0) { foreach (var e in errors) Console.Error.WriteLine("error: " + e); return 2; }
    Directory.CreateDirectory(a.Out);
    File.WriteAllBytes(Path.Combine(a.Out, "whatsnew.json"), JsonSerializer.SerializeToUtf8Bytes(doc, ReleaseNotesJsonContext.Default.ReleaseNotesDocument));
    WriteIndex(a, doc); WriteBody(a, doc, rel); WriteStoreListing(a, doc); CopyMedia(a.Notes, a.Out);
    return 0;
}
```
`RELEASE_BODY.md`: `# Wavee {semver} — {codename}` · tagline · `## Highlights` (title — body, media as image links to the
release assets) · `## Added/Changed/Fixed/…` (bullets with `#n` autolinks) · `<details><summary>Commits & contributors</summary>`
(the `POST /releases/generate-notes` output when a token is available, else omitted). `whatsnew-index.json` = previous index
(if given) with this release prepended, capped at 12.

---

## 7. Local release tooling — scripts (PowerShell 5.1, ASCII literals, UTF-8 no BOM)

### 7.1 `ops/build/Wavee.Build.psm1` (new; imported by both pack scripts)
```powershell
function Get-WindowsSdkTools { <# highest 10.* kit with x64\makeappx.exe -> @{Version;ToolDir;MakeAppx;MakePri;SignTool} #> }
function Add-VsInstallerToPath { <# vswhere dir onto PATH once #> }
function Test-X64CrossToolchain { <# vswhere -all -prerelease -property installationPath -> VC\Tools\MSVC\*\bin\Host[Aa]rm64\x64\link.exe ; NuGet cache runtime.win-x64.microsoft.dotnet.ilcompiler -> @{Ok;LinkExe;IlcPack;Reason} #> }
function Get-WaveeVersionProps([string]$Path) {
  $t = [IO.File]::ReadAllText($Path)
  $m = [regex]::Match($t, '<WaveeVersion>([^<]+)</WaveeVersion>'); $c = [regex]::Match($t, '<WaveeCodename>([^<]+)</WaveeCodename>'); $b = [regex]::Match($t, '<WaveeBuild>(\d+)</WaveeBuild>')
  if (-not ($m.Success -and $c.Success -and $b.Success)) { throw "Wavee.Version.props is missing WaveeVersion/WaveeCodename/WaveeBuild" }
  [pscustomobject]@{ Version = $m.Groups[1].Value.Trim(); Codename = $c.Groups[1].Value.Trim(); Build = [int]$b.Groups[1].Value; Path = $Path }
}
function Set-WaveeBuild([string]$Path, [int]$Build) {
  if ($Build -lt 0 -or $Build -gt 65535) { throw "WaveeBuild out of range: $Build" }
  $t = [IO.File]::ReadAllText($Path); $rx = [regex]'<WaveeBuild>\d+</WaveeBuild>'
  if ($rx.Matches($t).Count -ne 1) { throw "expected exactly one <WaveeBuild> in $Path" }
  [IO.File]::WriteAllText($Path, $rx.Replace($t, "<WaveeBuild>$Build</WaveeBuild>"), (New-Object System.Text.UTF8Encoding $false))
}
function Invoke-Native([string]$FilePath, [string[]]$ArgumentList, [switch]$AllowFailure) {
  $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
  try { $out = & $FilePath @ArgumentList 2>&1; $code = $LASTEXITCODE } finally { $ErrorActionPreference = $prev }
  if ($code -ne 0 -and -not $AllowFailure) { throw "$FilePath exited $code`n$($out -join "`n")" }
  [pscustomobject]@{ ExitCode = $code; Output = ($out | ForEach-Object { "$_" }) }
}
function Invoke-TrustedSigning([string[]]$Path, [string]$Metadata, [string]$Subscription = 'Azure subscription 1', [string]$SignTool) {
  if (-not (Test-Path $Metadata)) { throw "Trusted Signing metadata not found: $Metadata (copy ops/build/signing/metadata.template.json -> metadata.json)" }
  $dlib = @("$env:LOCALAPPDATA\Microsoft\MicrosoftArtifactSigningClientTools\Azure.CodeSigning.Dlib.dll",
            'C:\Program Files (x86)\Microsoft\ArtifactSigningClientTools\bin\Azure.CodeSigning.Dlib.dll',
            'C:\Program Files\Microsoft\ArtifactSigningClientTools\bin\Azure.CodeSigning.Dlib.dll',
            'C:\Program Files (x86)\Microsoft\TrustedSigningClientTools\bin\Azure.CodeSigning.Dlib.dll') | Where-Object { Test-Path $_ } | Select-Object -First 1
  if (-not $dlib) { throw "Azure.CodeSigning.Dlib.dll not found. winget install -e --id Microsoft.Azure.ArtifactSigningClientTools" }
  if (-not ($env:AZURE_CLIENT_ID -and $env:AZURE_TENANT_ID -and $env:AZURE_CLIENT_SECRET)) { Invoke-Native 'az' @('account','set','--subscription',$Subscription) | Out-Null }
  Invoke-Native $SignTool (@('sign','/v','/fd','SHA256','/tr','http://timestamp.acs.microsoft.com','/td','SHA256','/dlib',$dlib,'/dmdf',$Metadata) + $Path) | Out-Null
  foreach ($p in $Path) { if (-not (Test-MsixSignature $p $SignTool)) { throw "signature did not verify: $p" } }
}
function Invoke-DevCertSigning([string[]]$Path, [string]$Publisher, [string]$FriendlyName, [string]$SignTool) { <# existing dev-cert block, returns the X509Certificate2 #> }
function Test-MsixSignature([string]$Path, [string]$SignTool) { (Invoke-Native $SignTool @('verify','/pa','/q',$Path) -AllowFailure).ExitCode -eq 0 }
function Get-MsixIdentity([string]$Path) {
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  $zip = [IO.Compression.ZipFile]::OpenRead($Path); try { $e = $zip.GetEntry('AppxManifest.xml'); $sr = New-Object IO.StreamReader($e.Open()); [xml]$x = $sr.ReadToEnd(); $sr.Dispose() } finally { $zip.Dispose() }
  $id = $x.Package.Identity; [pscustomobject]@{ Name = $id.Name; Publisher = $id.Publisher; Version = $id.Version; ProcessorArchitecture = $id.ProcessorArchitecture }
}
function Test-PeMachine([string]$Path, [ValidateSet('arm64','x64')][string]$Arch) {
  $fs = [IO.File]::OpenRead($Path); try { $br = New-Object IO.BinaryReader($fs); $fs.Position = 0x3C; $pe = $br.ReadInt32(); $fs.Position = $pe + 4; $m = $br.ReadUInt16() } finally { $fs.Dispose() }
  if ($Arch -eq 'x64') { $m -eq 0x8664 } else { $m -eq 0xAA64 }
}
Export-ModuleMember -Function *
```

### 7.2 `ops/build/pack-wavee-msix.ps1` — parameter block (replaces L14-33) and the new steps
```powershell
param(
  [ValidateSet('arm64','x64')][string]$Arch = <existing default>,
  [string]$Quad = '',            # M.m.p.N ; default: props Version + '.' + props Build (dev pack)
  [string]$Semver = '',          # default: props Version
  [string]$Channel = 'dev',      # stable|beta|dev
  [string]$Codename = '',        # default: props Codename
  [string]$IdentityName = 'cproducts.Wavee', [string]$DisplayName = 'Wavee', [string]$Protocol = 'wavee',
  [string]$Commit = '', [string]$BuildDate = '',
  [string]$NotesDir = '',        # copied to layout\Assets\whatsnew\ when given
  [switch]$PublicOnly,           # -p:WaveeSkipPrivateSources=true
  [string]$Configuration = 'Release', [string]$Publisher = 'CN=cproducts, O=cproducts, L=Utrecht, S=Utrecht, C=NL',
  [string]$OutputDir = 'artifacts', [switch]$NoAot, [switch]$NoSign, [switch]$Install, [switch]$TrustedSigning,
  [string]$Metadata, [string]$Subscription = 'Azure subscription 1'
)
Import-Module (Join-Path $PSScriptRoot 'Wavee.Build.psm1') -Force -DisableNameChecking
$props = Get-WaveeVersionProps (Join-Path $root 'src\apps\Wavee\Wavee.Version.props')
if (-not $Semver) { $Semver = $props.Version }; if (-not $Codename) { $Codename = $props.Codename }
if (-not $Quad) { $Quad = ($Semver -replace '-.*$','') + '.' + $props.Build }
if ($Quad -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw "Quad must be 4 numeric parts: $Quad" }
if (-not $Commit) { $Commit = (git -C $root rev-parse --short=7 HEAD).Trim() }
if (-not $BuildDate) { $BuildDate = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ') }
$outRoot = if ([IO.Path]::IsPathRooted($OutputDir)) { $OutputDir } else { Join-Path $root $OutputDir }     # absolute -OutputDir fix
$pubArgs = @($csproj,'-c',$Configuration,'-r',$rid,'-o',$pubDir,'--nologo','-v','m','/p:NuGetAudit=false',
  "/p:InformationalVersion=$Semver+build.$($Quad.Split('.')[3]).sha.$Commit", "/p:WaveeChannel=$Channel", "/p:WaveePackageVersion=$Quad", "/p:WaveeCommit=$Commit", "/p:WaveeBuildDate=$BuildDate")
if ($PublicOnly) { $pubArgs += '-p:WaveeSkipPrivateSources=true' }
# ... existing publish / modules / notices / layout copy ...
if ($NotesDir) { $dst = Join-Path $layout 'Assets\whatsnew'; New-Item -ItemType Directory -Force $dst | Out-Null; Copy-Item (Join-Path $NotesDir '*') $dst -Recurse -Force }
$mf = (Get-Content $manifestTemplate -Raw).Replace('__PUBLISHER__',$Publisher).Replace('__VERSION__',$Quad).Replace('__ARCH__',$Arch).Replace('__IDENTITY__',$IdentityName).Replace('__DISPLAY__',$DisplayName).Replace('__PROTOCOL__',$Protocol)
# ... makepri / makeappx ...
Get-ChildItem $layout -Recurse -Include *.exe,*.dll | ForEach-Object { if (-not (Test-PeMachine $_.FullName $Arch)) { throw "wrong machine type for $Arch : $($_.FullName)" } }
$id = Get-MsixIdentity $outMsix; if ($id.Version -ne $Quad -or $id.ProcessorArchitecture -ne $Arch -or $id.Name -ne $IdentityName) { throw "package identity mismatch: $($id | ConvertTo-Json -Compress)" }
Copy-Item (Join-Path $pubDir 'THIRD-PARTY-NOTICES.txt') $outRoot -Force
if (-not $NoSign) { if ($TrustedSigning) { Invoke-TrustedSigning -Path @($outMsix) -Metadata $Metadata -Subscription $Subscription -SignTool $tools.SignTool } else { Invoke-DevCertSigning ... } }
```
`ops/build/Wavee.AppxManifest.xml`: `Identity Name="__IDENTITY__"`, `DisplayName>__DISPLAY__`, protocol `Name="__PROTOCOL__"`,
`MinVersion="10.0.19041.0"`, capabilities `<rescap:Capability Name="runFullTrust"/><rescap:Capability Name="packageManagement"/>`.
`ops/build/Wavee.AppInstaller.template.xml`: `Name="__IDENTITY__"`; comment L9-10 replaced with the verified facts.
`pack-msix.ps1` (gallery): its L114-133 block → `Invoke-TrustedSigning`/`Invoke-DevCertSigning` from the module.

### 7.3 `ops/release/Wavee.Release.psm1` (new) — the pure helpers
```powershell
function Test-WaveeSemver([string]$Semver) {
  $m = [regex]::Match($Semver, '^(?<M>\d+)\.(?<m>\d+)\.(?<p>\d+)(?:-beta\.(?<b>[1-9]\d*))?$'); if (-not $m.Success) { throw "bad semver: $Semver" }
  [pscustomobject]@{ Major=[int]$m.Groups['M'].Value; Minor=[int]$m.Groups['m'].Value; Patch=[int]$m.Groups['p'].Value
    Beta = $(if ($m.Groups['b'].Success) { [int]$m.Groups['b'].Value } else { $null }); Channel = $(if ($m.Groups['b'].Success) { 'beta' } else { 'stable' }); Core = "$($m.Groups['M'].Value).$($m.Groups['m'].Value).$($m.Groups['p'].Value)" }
}
function ConvertTo-WaveeQuad([string]$Semver, [int]$Build) { $s = Test-WaveeSemver $Semver; foreach ($p in @($s.Major,$s.Minor,$s.Patch,$Build)) { if ($p -gt 65535) { throw "version part > 65535" } }; "$($s.Core).$Build" }
function Get-WaveeFeedVersion([string]$Repo, [string]$FeedRelease, [string]$Arch, [string]$AssetPrefix = 'Wavee') {
  [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
  $url = "https://github.com/$Repo/releases/download/$FeedRelease/$AssetPrefix.$Arch.appinstaller"
  try { $r = Invoke-WebRequest -UseBasicParsing -Uri $url -MaximumRedirection 5 } catch { if ($_.Exception.Response -and [int]$_.Exception.Response.StatusCode -eq 404) { return $null }; throw }
  [xml]$x = $r.Content; [version]$x.AppInstaller.Version
}
function Test-FeedMonotonic([string]$Repo, [string[]]$FeedRelease, [string]$Quad, [string]$Semver, [string[]]$Arch, [string]$AssetPrefix = 'Wavee') {
  $new = [version]$Quad; $core = [version](Test-WaveeSemver $Semver).Core; $bad = @(); $rows = @()
  foreach ($f in $FeedRelease) { foreach ($a in $Arch) { $cur = Get-WaveeFeedVersion $Repo $f $a $AssetPrefix
      $rows += [pscustomobject]@{ Feed=$f; Arch=$a; Current=$cur; New=$new }
      if ($cur -ne $null) { if ($new -le $cur) { $bad += "$f/$a : $new is not > $cur" }
        $curCore = [version]"$($cur.Major).$($cur.Minor).$($cur.Build)"; if ($core -lt $curCore) { $bad += "$f/$a : semver $core < feed $curCore" } } } }
  if ($bad.Count) { throw ("feed monotonic gate failed:`n  " + ($bad -join "`n  ")) }; $rows
}
function New-WaveeAppInstaller([string]$Template, [string]$OutFile, [string]$Arch, [string]$Quad, [string]$Publisher, [string]$IdentityName, [string]$FeedUri, [string]$MsixUri) {
  $t = [IO.File]::ReadAllText($Template).Replace('__VERSION__',$Quad).Replace('__ARCH__',$Arch).Replace('__PUBLISHER__',$Publisher).Replace('__IDENTITY__',$IdentityName).Replace('__APPINSTALLER_URI__',$FeedUri).Replace('__MSIX_URI__',$MsixUri)
  [IO.File]::WriteAllText($OutFile, $t, (New-Object System.Text.UTF8Encoding $false))
  [xml]$x = [IO.File]::ReadAllText($OutFile); if ($x.AppInstaller.Version -ne $Quad -or $x.AppInstaller.MainPackage.Uri -ne $MsixUri) { throw "appinstaller substitution failed: $OutFile" }
}
function Write-ReleaseManifest([string]$Dir, [string[]]$Files, [string]$OutFile) { $lines = $Files | Sort-Object | ForEach-Object { $h = (Get-FileHash (Join-Path $Dir $_) -Algorithm SHA256).Hash.ToLower(); "$h  $_" }; [IO.File]::WriteAllText($OutFile, ($lines -join "`n") + "`n", (New-Object System.Text.UTF8Encoding $false)) }
function Test-ReleaseManifest([string]$Dir, [string]$ManifestFile) { foreach ($l in Get-Content $ManifestFile) { $h,$n = $l -split '  ',2; $p = Join-Path $Dir $n; if (-not (Test-Path $p) -or (Get-FileHash $p -Algorithm SHA256).Hash.ToLower() -ne $h) { return $false } }; $true }
function Invoke-Gh([string[]]$Arguments, [switch]$AllowFailure) { $r = Invoke-Native 'gh' $Arguments -AllowFailure:$AllowFailure; if ($r.ExitCode -ne 0 -and -not $AllowFailure) { throw "gh $($Arguments -join ' ') failed:`n$($r.Output -join "`n")" }; ($r.Output -join "`n") }
function Get-GhRelease([string]$Repo, [string]$Tag) { $r = Invoke-Native 'gh' @('release','view',$Tag,'--repo',$Repo,'--json','isDraft,isPrerelease,assets') -AllowFailure; if ($r.ExitCode -ne 0) { return $null }; ($r.Output -join "`n") | ConvertFrom-Json }
function Publish-WaveeRelease([string]$Repo, [string]$Tag, [string]$Title, [string]$BodyFile, [string[]]$Assets, [bool]$Prerelease, [bool]$Latest) {
  if (-not (Get-GhRelease $Repo $Tag)) { Invoke-Gh @('release','create',$Tag,'--repo',$Repo,'--draft','--verify-tag','--title',$Title,'--notes-file',$BodyFile) | Out-Null }
  Invoke-Gh (@('release','upload',$Tag,'--repo',$Repo,'--clobber') + $Assets) | Out-Null
  $edit = @('release','edit',$Tag,'--repo',$Repo,'--draft=false'); if ($Prerelease) { $edit += @('--prerelease','--latest=false') } elseif ($Latest) { $edit += '--latest' }
  Invoke-Gh $edit | Out-Null
}
function Update-WaveeFeed([string]$Repo, [string]$FeedRelease, [string]$FeedBodyFile, [string[]]$Assets) {
  if (-not (Get-GhRelease $Repo $FeedRelease)) { Invoke-Gh @('release','create',$FeedRelease,'--repo',$Repo,'--target','main','--title',"Wavee update feed ($FeedRelease)",'--notes-file',$FeedBodyFile,'--latest=false') | Out-Null }
  Invoke-Gh (@('release','upload',$FeedRelease,'--repo',$Repo,'--clobber') + $Assets) | Out-Null
}
function Test-WaveeFeedLive([string]$Repo, [string]$FeedRelease, [string]$Arch, [string]$AssetPrefix, [string]$ExpectedQuad, [string]$ExpectedMsixUri, [int]$Retries = 6, [int]$DelaySeconds = 10) {
  for ($i = 0; $i -lt $Retries; $i++) { $v = Get-WaveeFeedVersion $Repo $FeedRelease $Arch $AssetPrefix; if ("$v" -eq $ExpectedQuad) { return $true }; Start-Sleep -Seconds $DelaySeconds }; $false
}
function Get-ReleaseState([string]$Path) { if (Test-Path $Path) { Get-Content $Path -Raw | ConvertFrom-Json } else { $null } }
function Set-ReleaseState([string]$Path, [hashtable]$State) { [IO.File]::WriteAllText($Path, ($State | ConvertTo-Json -Depth 5), (New-Object System.Text.UTF8Encoding $false)) }
Export-ModuleMember -Function *
```

### 7.4 `ops/release/wavee-release.ps1` — orchestrator skeleton
```powershell
#requires -Version 5.1
[CmdletBinding()] param(
  [ValidateSet('stable','beta')][string]$Channel, [string[]]$Arch = @('arm64','x64'), [ValidateSet('arm64','x64')][string]$SkipArch, [string]$X64Msix,
  [switch]$PublicOnly, [switch]$DryRun, [switch]$NoUpload, [switch]$Resume, [switch]$Abort, [string]$RepointFeed, [switch]$AllowDowngrade,
  [switch]$SkipTests, [switch]$NoSign, [switch]$NoNotes, [switch]$InstallFromFeed, [switch]$Force,
  [string]$Repo = 'christosk92/WaveeMusic', [string]$FeedRelease = 'wavee-stable', [string]$TagPrefix = 'wavee-v',
  [string]$Publisher = 'CN=cproducts, O=cproducts, L=Utrecht, S=Utrecht, C=NL', [string]$Subscription = 'Azure subscription 1',
  [string]$Metadata, [string]$Configuration = 'Release', [string]$OutputDir = 'artifacts/release')
$ErrorActionPreference = 'Stop'; $root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Import-Module (Join-Path $root 'ops\build\Wavee.Build.psm1') -Force -DisableNameChecking; Import-Module (Join-Path $PSScriptRoot 'Wavee.Release.psm1') -Force -DisableNameChecking
if (-not $Metadata) { $Metadata = Join-Path $root 'ops\build\signing\metadata.json' }
if ($DryRun -and ($NoUpload -or $Resume -or $Abort)) { throw '-DryRun excludes -NoUpload/-Resume/-Abort' }
if ($NoSign -and -not ($DryRun -or $NoUpload)) { throw '-NoSign requires -DryRun or -NoUpload' }
if ($X64Msix -and $SkipArch -eq 'x64') { throw '-X64Msix excludes -SkipArch x64' }
$arches = @($Arch | Where-Object { $_ -ne $SkipArch })

# ── 0 preflight ─────────────────────────────────────────────────────────────
function Step([string]$m) { Write-Host "`n== $m" -ForegroundColor Cyan }
Step 'Preflight'
$props = Get-WaveeVersionProps (Join-Path $root 'src\apps\Wavee\Wavee.Version.props'); $sv = Test-WaveeSemver $props.Version
if ($Channel -and $Channel -ne $sv.Channel) { throw "-Channel $Channel disagrees with semver $($props.Version)" }; $Channel = $sv.Channel
if ($Channel -eq 'beta') { throw 'beta channel is phase 2 (separate package identity); tag a stable semver' }
$tag = "$TagPrefix$($props.Version)"; $stage = Join-Path $root ($OutputDir.Replace('/','\')) ; $stage = Join-Path $stage ($props.Version + $(if ($DryRun) { '-dryrun' } else { '' })); $statePath = Join-Path $stage 'release-state.json'
if ($Abort) { Invoke-Abort; return }; if ($RepointFeed) { Invoke-Repoint; return }
$state = if ($Resume) { Get-ReleaseState $statePath } else { $null }; if ($Resume -and -not $state) { throw "nothing to resume in $stage" }
if (-not $Resume) {
  if ((git -C $root status --porcelain) -and -not $DryRun) { throw 'working tree is not clean' }
  if (-not $Force) { git -C $root fetch origin main | Out-Null; if ((git -C $root rev-parse HEAD) -ne (git -C $root rev-parse origin/main)) { throw 'HEAD != origin/main (use -Force)' }; if ((git -C $root branch --show-current) -ne 'main') { throw 'not on main (use -Force)' } }
  if (-not $PublicOnly -and -not (Test-Path (Join-Path $root 'src\apps\Wavee.PlayPlay\Client\InProcessPlayPlayKeyDeriver.cs'))) { throw 'Wavee.PlayPlay junction missing; a release build is PlayPlay-inclusive (or pass -PublicOnly)' }
  if (-not $DryRun) { if (git -C $root tag -l $tag) { throw "tag $tag exists locally" }; if (git -C $root ls-remote --tags origin "refs/tags/$tag") { throw "tag $tag exists on origin" }; if (Get-GhRelease $Repo $tag) { throw "release $tag exists" } }
  $cl = Get-Content (Join-Path $root 'CHANGELOG.md') -Raw; if ($cl -notmatch "(?m)^## \[$([regex]::Escape($props.Version))\] - (\d{4}-\d{2}-\d{2}|unreleased)\s*$") { throw "CHANGELOG.md has no '## [$($props.Version)] - <date|unreleased>' entry" }
  $notesSrc = Join-Path $root "ops\release\wavee\$($props.Version)"; if (-not $NoNotes -and -not (Test-Path (Join-Path $notesSrc 'whatsnew.json'))) { throw "missing $notesSrc\whatsnew.json" }; if ($NoNotes -and -not $Force) { throw '-NoNotes requires -Force' }
  $tools = Get-WindowsSdkTools; Add-VsInstallerToPath; if ($arches -contains 'x64' -and -not $X64Msix) { $x = Test-X64CrossToolchain; if (-not $x.Ok) { throw "x64 cross toolchain: $($x.Reason) (use -SkipArch x64 or -X64Msix)" } }
  if (-not $NoSign) { Invoke-Native 'az' @('account','set','--subscription',$Subscription) | Out-Null; Invoke-Native 'az' @('account','get-access-token','--scope','https://codesigning.azure.net/.default','--query','expiresOn','-o','tsv') | Out-Null }
  if (-not ($DryRun -or $NoUpload)) { Invoke-Gh @('auth','status') | Out-Null; Invoke-Gh @('repo','view',$Repo,'--json','nameWithOwner') | Out-Null }
  $build = $props.Build + 1; $quad = ConvertTo-WaveeQuad $props.Version $build
  $feeds = @($FeedRelease); if ($Channel -eq 'stable' -and (Get-GhRelease $Repo 'wavee-beta')) { $feeds += 'wavee-beta' }
  Test-FeedMonotonic $Repo $feeds $quad $props.Version $arches | Format-Table | Out-String | Write-Host
  if (-not $SkipTests) { Step 'Gates'; Invoke-Native 'dotnet' @('build',"$root\src\FluentGpu.slnx",'-c','Debug','--nologo','-v','q') | Out-Null; Invoke-Native 'dotnet' @('test',"$root\src\apps\Wavee.Tests\Wavee.Tests.csproj",'--nologo','-v','q') | Out-Null
    $vs = Invoke-Native 'dotnet' @('run','--project',"$root\src\FluentGpu.VerticalSlice",'-c','Release'); if (($vs.Output -join "`n") -notmatch 'ALL CHECKS PASSED') { throw 'VerticalSlice failed' } }
  if ((Test-Path $stage) -and -not $Force) { throw "$stage exists (use -Force or -Resume)" }; Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue; New-Item -ItemType Directory -Force $stage | Out-Null
}

# ── 1a bump + date  (skipped for -DryRun / -Resume) ─────────────────────────
# Set-WaveeBuild; CHANGELOG 'unreleased' -> today (UTC); assert git diff --name-only == those two files
# ── 2 validate notes → $stage\notes  (dotnet run Wavee.ReleaseTool; GITHUB_TOKEN = gh auth token; --previous-index from feed if present) ─
#    on failure: git checkout -- CHANGELOG.md src/apps/Wavee/Wavee.Version.props ; throw
# ── 1b commit + tag (local): git add <2 files>; git commit -m "release: Wavee <semver> <codename> (build <quad>)"; git tag -a <tag> -m "..."; state.bump=done; state.pushed=$false
# ── 3/4 pack per arch: powershell -File ops\build\pack-wavee-msix.ps1 -Arch <a> -Quad $quad -Semver <semver> -Channel stable -Codename <name> -Commit <sha7> -BuildDate <iso> -NotesDir "$stage\notes" -Publisher $Publisher -OutputDir $stage -NoSign [-PublicOnly]
#    -X64Msix: Copy-Item -> "$stage\Wavee_${quad}_x64.msix"; Get-MsixIdentity must match; re-sign only if Test-MsixSignature fails
# ── 5 sign: Invoke-TrustedSigning -Path @(all msix) -Metadata $Metadata -Subscription $Subscription -SignTool $tools.SignTool   (state.sign=done)
# ── 6 appinstaller: foreach arch New-WaveeAppInstaller -Template ops\build\Wavee.AppInstaller.template.xml -OutFile "$stage\Wavee.$a.appinstaller" -Arch $a -Quad $quad -Publisher $Publisher -IdentityName cproducts.Wavee
#       -FeedUri "https://github.com/$Repo/releases/download/$FeedRelease/Wavee.$a.appinstaller" -MsixUri "https://github.com/$Repo/releases/download/$tag/Wavee_${quad}_$a.msix"
# ── 7 stage: copy notes\whatsnew.json, notes\whatsnew-index.json, notes\media\* (basenames), notes\RELEASE_BODY.md; Write-ReleaseManifest over every asset; state.stage=done
#    if ($DryRun -or $NoUpload) { print folder + the gh commands; return }
# ── 8 push: git fetch; assert origin/main == HEAD~1; git push origin main; git push origin "refs/tags/$tag"; state.pushed=$true
# ── 9 release: Publish-WaveeRelease $Repo $tag "Wavee <semver> $([char]0x2014) <codename>" "$stage\RELEASE_BODY.md" <release assets> -Prerelease:($Channel -eq 'beta') -Latest:($Channel -eq 'stable')
# ── 10 feed (LAST): foreach feed in $feeds: Update-WaveeFeed $Repo $feed "$PSScriptRoot\feed-release-body.md" @("$stage\Wavee.arm64.appinstaller","$stage\Wavee.x64.appinstaller","$stage\whatsnew-index.json")
# ── 11 verify: Get-GhRelease assets == staged; Test-WaveeFeedLive per arch; HEAD msix url Content-Length == local; optional Add-AppxPackage -AppInstallerFile <feed url>; state.verify=done; summary
```
Each phase is a function guarded by `if ($state.phases.<name> -ne 'done')` so `-Resume` skips completed work; `-Resume`
first runs `Test-ReleaseManifest` (if `stage=done`) and refuses on a hash mismatch. `-Abort` requires `state.pushed -eq $false`,
checks the tag points at HEAD and HEAD's message matches, then `git tag -d`, `git reset --hard HEAD~1`, deletes `$stage`.
`-RepointFeed <semver>`: locates release `$TagPrefix<semver>`, reads the quad from its msix asset names, regenerates both
`.appinstaller` files pointing at that release, runs the monotonic gate (inverted only with `-AllowDowngrade`), `Update-WaveeFeed`.

Workflows: delete `.github/workflows/wavee-msix.yml` (a CI checkout has no junction → public-only variant); add `make_latest: false`
to the gallery `msix.yml` release step (second fence).

---

## 8. Waves (Opus subagents; disjoint files; agents NEVER build/test/run/tag/push/stash; orchestrator verifies once)

Step 0 (orchestrator): fold §§2–7 of this plan into `docs/plans/wavee/auto-update-whatsnew-plan.md` (replace §2.5/§4.4/§7-CI/§9/§10;
`WaveeBuildOffset`→`WaveeBuild`; `WhatsNew*` types → `ReleaseNotes*`). Every agent reads that doc + dossier §2–§5 + its row.

| Agent | Owns exclusively | Deliverable / pinned |
|---|---|---|
| A1 Core | `Wavee.Core/Notifications/{AppUpdate,AppUpdateVersion,NotificationModels}.cs` (record change only); new `Wavee.Core/Versioning/WaveeVersionInfo.cs`; new `Wavee.Core/ReleaseNotes/{ReleaseNotesDocument,ReleaseNotesJsonContext,MarkdownLite,ChangelogParser,ReleaseNotesRange,IssueStateBudget,IssueStateCache}.cs`; tests `Wavee.Tests/ReleaseNotes/*`, `Wavee.Tests/Versioning/*` | §3.1, §4.1–4.3 verbatim; STJ properties; tokenizer/parser fixtures incl. edge cases |
| A2 WindowsApi | new `FluentGpu.WindowsApi/Packaging/{IPackageUpdater,PackageUpdater,PackageManagerInterop,WinRtAsync,PackageDeploymentResult}.cs`; `PackageIdentity.cs` (add OS-version probe only) | §3.3; confirm every vtable slot against `windows.management.deployment.h` (SDK 10.0.26100) and pin as `const int`; TerraFX `IPackageStatics/IPackage6/IPackage8/IAppInstallerInfo/IAsyncInfo/IUriRuntimeClassFactory`; MTA worker; `RegisterApplicationRestart` P/Invoke; `Classify(int)` |
| A3 Release tool + notes | new `src/apps/Wavee.ReleaseTool/{Wavee.ReleaseTool.csproj,Program.cs,GitHubApi.cs,BodyWriter.cs}` + `src/FluentGpu.slnx` entry; `CHANGELOG.md` (date `[0.2.0]` = release day placeholder `unreleased` stays until the script dates it; add update/what's-new/removed items); new `ops/release/wavee/0.2.0/whatsnew.json` (3 highlights, no binary media — `media` refs omitted); tests `Wavee.Tests/ReleaseTool/ValidateTests.cs` (fixture dirs) | §6; exit codes 0/2; `--previous-index`; writes `whatsnew.json`, `whatsnew-index.json`, `RELEASE_BODY.md`, `store-listing.txt` |
| A4 App services | `App/{AppInstallerUpdateService,AppUpdateScheduler,AppVersion,Services,NotificationCenterBridge,NotificationSimulator,ToastEscalator}.cs`; new `App/{ReleaseNotesStore,AppUpdateToasts,FakeAppUpdateService}.cs`; `Platform/AppSettings.cs` (§4.5 keys); `Backend/Spotify/HttpPools.cs` (`HttpPool.GitHub`); tests `Wavee.Tests/{AppInstallerUpdateServiceTests,ReleaseNotesStoreTests,AppUpdateToastsTests}.cs` | §3.2, §4.4, §5.6 table; `Services.AppUpdate` stays `IAppUpdateService`; `Services.ReleaseNotes` new; `FakeAppUpdateService` walks all states on a 2 s timer (developer mode → Settings › Developer "Simulate update") |
| A5 UI | new `Features/ReleaseNotes/{ReleaseNotesPage,ReleaseNotesHero,HighlightStrip,HighlightCard,ChangelogSection,ChangelogItem,IssueChip,Avatars,ReleaseRail,ReleaseNotesText,AfterUpdateDialog}.cs`; `Features/Shell/{SettingsPage.About,SettingsShared,ShellRoutes,ContentHost,NotificationPanel,WaveeShell}.cs` (route arm, dialog mount, About); `Features/Diagnostics/PlaybackRuntimeDiagnosticsPage.cs` (Updates section); `assets/loc/en-US.json` (§5.7) | §5; every string via `Strings.*`; reduced motion = value (`Motion.ReducedMotion`) never a hook branch; no `ms-appinstaller`, no `LoginView.OpenUrl(ReleaseNotesUrl)` |
| I1 Build side | new `ops/build/Wavee.Build.psm1`; `ops/build/{pack-wavee-msix,pack-msix,publish-wavee-aot}.ps1`; `ops/build/Wavee.AppInstaller.template.xml`; `ops/build/Wavee.AppxManifest.xml`; new `src/apps/Wavee/Wavee.Version.props`; `src/apps/Wavee/Wavee.csproj` (L15-19 only) | §2, §7.1–7.2; PS 5.1, ASCII, no BOM; dev pack still works with zero flags |
| I2 Release side | new `ops/release/{wavee-release.ps1,Wavee.Release.psm1,feed-release-body.md,README.md,tests/Wavee.Release.Tests.ps1}` | §7.3–7.4; Pester 3.4 tests for the pure helpers (semver, quad, gate with a stubbed `Get-WaveeFeedVersion`, appinstaller substitution, manifest round-trip, `Set-WaveeBuild`) |

Wave 2 (after the orchestrator's first green build): **B1 docs** — `docs/guide/releasing-wavee.md` rewrite (hand edits → `-DryRun` →
real run → verify → failure/`-Resume`/`-Abort` table → corrected rollback with `-RepointFeed` → scratch-feed test recipe),
`.claude/skills/releasing/SKILL.md`, `ops/build/README.md`, `docs/guide/README.md` index, delete `wavee-msix.yml`, `msix.yml`
`make_latest: false`, `PRIVACY.md`/`docs/guide/playback-modules.md` feed wording; **B2 review** (read-only report): no
`ms-appinstaller`/`Downloaded`/`releases/latest`/`HostVersion` remnants, no env-var gate, no source-text test, Controls +
VerticalSlice closure TerraFX-free, all cross-agent signatures (§3.1, §3.3 `IPackageUpdater`, §4.1, §4.4, §4.5) identical, every
new string localized.

## 9. End-to-end test (real feed untouched)

Two additions the E2E needs, folded into the waves above: (a) the feed name is **build-time metadata** —
`AssemblyMetadata FeedRelease` stamped by `pack-wavee-msix.ps1 -FeedRelease` (default `wavee-stable`), read by
`WaveeVersionInfo.FeedRelease`, so a test package polls `wavee-stable-test` (no env var, no runtime switch); (b)
`wavee-release.ps1 -Branch <name>` lets the bump commit + push target a throwaway branch (`-Force` still needed for the
"HEAD == origin/main" check).

```
 machine: this PC (arm64)            GitHub (test namespace)                     what you observe
 ────────────────────────            ───────────────────────                     ────────────────
 A) git checkout -b release-test
    props: 0.2.0 / Breaker
    wavee-release.ps1 -FeedRelease wavee-stable-test -TagPrefix wavee-test-v -Branch release-test -Force -SkipTests -InstallFromFeed
        └─► release wavee-test-v0.2.0 (quad 0.2.0.N+1) + feed wavee-stable-test ──► Add-AppxPackage -AppInstallerFile <feed url>
                                                                                   → App A installed WITH the App Installer association
    launch A  → About: Wavee 0.2.0 "Breaker" · 0.2.0.N+1 · sha · [Up to date]; What's new page from the embedded doc;
                Get-AppxPackageAutoUpdateSettings shows the feed; diagnostics › Updates shows association=true
 B) edit props → 0.2.1 (still Breaker); CHANGELOG "## [0.2.1] - unreleased"; copy ops/release/wavee/0.2.1/whatsnew.json
    wavee-release.ps1 (same flags, no -InstallFromFeed)
        └─► release wavee-test-v0.2.1 (quad N+2); feed root Version moves to 0.2.1.N+2 (Test-WaveeFeedLive green)
 C) in-app path: launch A (or wait ≤30 s) → toast "Wavee Breaker is available" → Update now → progress toast/About bar →
    "Restarting…" → Windows relaunches → B: AfterUpdateDialog "Welcome to Wavee 0.2.1", OS toast "Wavee updated", About 0.2.1.N+2,
    notification-centre card, LastRunVersion advanced. Failure drills: kill the network mid-download (Failed › Network + Retry);
    press Later (Snoozed, no re-toast; Settings shows "waiting"); metered toggle off + metered network (Failed › Metered).
 D) OS silent path: Remove-AppxPackage; install A's .msix directly (no association) → diagnostics shows association=false +
    "Repair auto-update" → click → association restored; close app; launch → App Installer applied B silently on that launch
    (this is the only observation of the OS path; also the place to see 0x80073D02 if a module child survived).
 E) resume/abort drills: run B again with a fresh semver and kill the script during phase 9 → `-Resume` finishes (draft never public);
    run once more → fails at "tag exists"; `-Abort` on an un-pushed run restores the tree.
 F) cleanup: gh release delete wavee-test-v0.2.0 / wavee-test-v0.2.1 / wavee-stable-test --cleanup-tag --yes; git tag -d …;
    git push origin --delete release-test; Remove-AppxPackage cproducts.Wavee; delete artifacts/release/*.
```
Automated layers underneath: `Wavee.Tests` (tokenizer, changelog parser, range stacking, issue budget, toast decision
table, `AppInstallerUpdateService` over `ScriptedHttpHandler` + a fake `IPackageUpdater` returning `IsRegistered`/each
HRESULT), Pester over the release helpers (semver/quad/gate/appinstaller/manifest/`Set-WaveeBuild`), ReleaseTool fixture
folders, VerticalSlice, the developer-mode simulator (every UI state without a network), and once per minor a clean-VM
install from the **real** feed (`releases/download/wavee-stable/Wavee.x64.appinstaller`) — the only x64 observation.

## 10. Verification (orchestrator, merged tree)
1. `dotnet build src/FluentGpu.slnx` Debug + Release (clean); `dotnet test src/apps/Wavee.Tests/Wavee.Tests.csproj`; `dotnet run --project src/FluentGpu.VerticalSlice` → "ALL CHECKS PASSED".
2. `Invoke-Pester ops/release/tests`; `dotnet run --project src/apps/Wavee.ReleaseTool -- validate --semver 0.2.0 --quad 0.2.0.1 --codename Breaker --channel stable --changelog CHANGELOG.md --notes ops/release/wavee/0.2.0 --out artifacts/notes-probe` (expected: fails only on the undated changelog until the script dates it → run with a dated copy).
3. x64 cross probe: `powershell -File ops\build\pack-wavee-msix.ps1 -Arch x64 -NoSign -OutputDir artifacts\x64-probe` → PE sweep + identity `x64`.
4. `powershell -File ops\release\wavee-release.ps1 -DryRun -SkipTests` → `artifacts/release/0.2.0-dryrun/` with two Trusted-Signed packages, `.appinstaller` root Version `0.2.0.1`, MANIFEST, `git status` clean. Install the arm64 msix; check About hero (`Wavee 0.2.0 "Breaker"`, quad, sha), What's new page from the embedded doc, developer-mode simulator through every state (toast + panel + About + OS toast), relaunch → AfterUpdateDialog (LastRunVersion differs) and "Updated" OS toast.
5. Scratch end-to-end on a throwaway branch: `-FeedRelease wavee-stable-test -TagPrefix wavee-test-v -SkipTests -Force` → phases 8–11 green (`Test-WaveeFeedLive`), second run fails at "tag exists", kill mid-upload then `-Resume` completes; cleanup releases/tags/branch.
6. The first real `wavee-v0.2.0` release is the user's call.

---

## As built (2026-08-29)

Everything above is the plan as approved. This section records where the landed code **differs** from it; the body is
left as written. Where the two disagree, this section wins.

### a. Packaging interop (`FluentGpu.WindowsApi/Packaging/`)

- `GetAppInstallerInfo` lives on **`IPackage6`**, not `IPackage8` as §3.3 says; the scalar properties of the returned
  info (`Uri`, `LastChecked`, `PausedUntil`, `OnLaunch`, `AutomaticBackgroundTask`) are on **`IAppInstallerInfo2`**.
- **`IAsyncOperationWithProgress.get_Progress` returns the progress *handler*, not the progress value** — §3.3's
  "read `DeploymentProgress` via `get_Progress` (slot 7)" is wrong and cannot work. Progress instead arrives through a
  hand-rolled **call-IN CCW**, `DeploymentProgressSink` (a fabricated vtable + a pinned instance, no `ComWrappers`),
  handed to `put_Progress`; the callback writes into a native block that the MTA worker polls alongside
  `IAsyncInfo.get_Status`.
- Verified vtable slots (SDK 10.0.26100 headers), replacing the guesses in §3.3:
  `IPackageManager.FindPackageByUserSecurityIdPackageFullName` = **21** (not 12);
  `IPackageManager3.GetDefaultPackageVolume` = **12** (not 6);
  `IPackageManager6.AddPackageByAppInstallerFileAsync` = **7** (not 10);
  `IDeploymentResult.get_ErrorText` = **6**, `get_ExtendedErrorCode` = **8**;
  `IDeploymentResult2.get_IsRegistered` = **6**.
- The options flag is **`AddPackageByAppInstallerOptions.ForceTargetAppShutdown = 0x40`** (an
  `AddPackageByAppInstallerOptions` value, not `DeploymentOptions`).
- The HRESULT classifier is **`PackageUpdateErrors.Classify`**, not `PackageUpdater.Classify` (§3.2) — it is a pure
  static in its own file so it is unit-testable without the interop.

### b. Update service + notes store

- `AppInstallerUpdateService`'s constructor gained two optional trailing parameters —
  `Func<bool>? isMetered, Action<string>? openUrl` — so the service is testable without `NetworkPolicy` /
  `LoginView.OpenUrl` statics. Production wiring passes null and gets the real ones.
- **`CheckAsync` captures the entering snapshot *before* publishing `Checking`.** The plan's ordering overwrote
  `Current` with `Checking` first and then compared against it, which ate the first-run-after-update `Completed`
  notice; the landed code keeps the entering state and restores it when the check finds nothing.
- `ReleaseNotesStore`'s constructor gained an optional `embeddedRoot` (tests point it at a fixture folder instead of
  `AppContext.BaseDirectory`).
- `IssueStateBudget` exists in `Wavee.Core`, but the store still implements the ≤20-per-open / 24 h / stop-on-403
  budget **inline** — the type is currently dead code. To reconcile later: either route the store through it or
  delete it.

### c. UI

- The localization section is **`whatsNew`** (the loc-keys generator PascalCases only the first letter, so
  `whats-new` / `whatsnew` would not have produced the expected `Strings` shape).
- The after-update dialog is mounted from **`AfterUpdateChrome`, inside the `OverlayHost` subtree** — not from
  `WaveeShell` as §5.1 draws it. The shell's own render sees a null overlay service, so a shell-level mount could
  never open the dialog.
- The developer-mode **"Simulate update"** entry point lives in **Settings › Diagnostics** (`DiagnosticsPanel.cs`),
  not Settings › Developer.
- Video highlights are **poster-only**: the card shows the `poster` still and never instantiates a file-backed
  player. `kind: "video"` still validates and still ships the `.mp4` as an asset.
- Token / API names actually used: `Tok.SystemFillSuccess`, `Tok.FillSolidBase`, `Tok.FillLayerAlt`,
  `AutomationRole`, `CursorId`. `HighlightCard.Create` / `HighlightCard.Compact` take an optional
  `ReleaseNotesDocument` (for media path resolution).

### d. Release tooling

- **`-DryRun` never touches the working tree.** It validates against a **dated copy** of `CHANGELOG.md` written into
  the staging folder (`<stage>\CHANGELOG.md`) and points the release tool at that, so `git status` stays clean and
  `<WaveeBuild>` is not bumped. §7.4's "1a bump + date (skipped for -DryRun)" understated this.
- `wavee-release.ps1` gained `-Branch` (the throwaway-branch E2E), `-IdentityName` and `-AssetPrefix` (a future beta
  identity / asset naming) on top of the parameter block in §7.4.
- `Test-FeedMonotonic` returns **`,$rows`** — the unary comma is load-bearing: without it PowerShell unrolls a
  single-row result and the caller's `.Count` breaks.
- `ops/release/tests/Wavee.Release.Tests.ps1` imports **`Wavee.Build.psm1` last**; imported first, its `Invoke-Native`
  is shadowed by the release module's re-export and the signing helpers resolve to the wrong copy.
- `.gitignore` gained **`!ops/release/`**: the pre-existing `[Rr]elease/` build-output pattern would otherwise
  swallow the entire release-tooling tree.

### e. ReleaseTool

- The pure validation core is **`Wavee.Core.ReleaseNotes.ReleaseNotesValidation`** (engine-free, unit-tested);
  `Program.cs` is only the CLI shell over it.
- Exit codes: **0** ok, **1** usage or I/O, **2** the release is not shippable (every problem listed). On a failure
  **nothing is written**, so a rejected run cannot half-publish.
- The CLI gained **`--previous-tag`**, which fills `links.compare` and the generated notes' `previous_tag_name`.
- Media caps as enforced: **150 KB** per still, **600 KB** per motion file, **1.5 MB** total.

### f. Workflows

`.github/workflows/wavee-msix.yml` is **deleted** (a CI checkout has no PlayPlay junction, so it would silently
publish the public-only variant). The gallery's `msix.yml` is UNCHANGED: `releases/latest` is the gallery's own feed
(`releases/latest/download/FluentGpu.<arch>.appinstaller`), so it is **Wavee** that must never claim it —
`Publish-WaveeRelease` always passes `--latest=false` (the plan's `make_latest: false` idea was dropped).
