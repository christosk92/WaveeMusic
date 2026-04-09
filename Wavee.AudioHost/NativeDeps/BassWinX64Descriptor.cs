using System.Runtime.InteropServices;

namespace Wavee.AudioHost.NativeDeps;

/// <summary>
/// Pinned descriptor for the Windows x64 BASS runtime.
///
/// <para>
/// <c>ManagedBass</c> 4.0.2 (referenced at <c>Wavee.AudioHost.csproj</c>) is a managed-only
/// wrapper — it does <b>not</b> ship the native <c>bass.dll</c>. Without provisioning, the
/// first P/Invoke into <c>Bass.Init()</c> throws <c>DllNotFoundException</c>.
/// </para>
/// <para>
/// We fill the gap at runtime by downloading the community
/// <c>BASS.Native</c> 2.4.13.10 NuGet package, extracting the embedded
/// <c>bass.dll</c>, and redirecting the P/Invoke lookup via
/// <see cref="NativeLibrary.SetDllImportResolver"/>.
/// </para>
/// <para>
/// The SHA-256 constant below is pinned to the exact binary captured from the package at
/// the time of implementation. Any drift (republished package, tampered CDN response)
/// fails verification and blocks load. To bump: download the .nupkg, extract
/// <c>build/native/x64/bass.dll</c>, compute <c>sha256sum</c>, and paste the
/// new lowercase-hex value here.
/// </para>
/// </summary>
internal static class BassWinX64Descriptor
{
    /// <summary>
    /// BASS.Native 2.4.13.10 (author: radio42, BASS license).
    /// NuGet v3 flat-container URL is stable and CDN-backed.
    /// </summary>
    private const string PackageUrl =
        "https://api.nuget.org/v3-flatcontainer/bass.native/2.4.13.10/bass.native.2.4.13.10.nupkg";

    /// <summary>
    /// SHA-256 (lowercase hex) of the extracted <c>build/native/x64/bass.dll</c>
    /// from the pinned package. 256000-byte binary.
    /// Captured on 2026-04-10 against BASS.Native 2.4.13.10.
    /// </summary>
    private const string ExpectedSha256 =
        "2fbaae03b30b08afccefdcff383add6436fc5c89968697ba996f0f8de3ea48af";

    public static NativeLibraryDescriptor Create() => new()
    {
        LibraryName = "bass",
        DisplayName = "Windows x64 BASS",
        AppliesTo = static () =>
            OperatingSystem.IsWindows() &&
            RuntimeInformation.ProcessArchitecture == Architecture.X64,
        TargetAssembly = static () => typeof(global::ManagedBass.Bass).Assembly,
        DownloadUrl = new Uri(PackageUrl, UriKind.Absolute),
        ArchiveEntryPath = "build/native/x64/bass.dll",
        CacheSubfolder = Path.Combine("NativeDeps", "win-x64"),
        CacheFileName = "bass.dll",
        ExpectedSha256 = ExpectedSha256,
        FailureMarkerName = "bass-win-x64.failure.json",
    };
}
