using System.Runtime.InteropServices;

namespace Wavee.AudioHost.NativeDeps;

/// <summary>
/// Pinned descriptor for the Windows ARM64 PortAudio runtime.
///
/// <para>
/// <c>PortAudioSharp2</c> 1.0.6 (referenced at <c>Wavee.AudioHost.csproj</c> line 27) only ships
/// native <c>portaudio.dll</c> binaries for <c>win-x64</c>, <c>osx-arm64</c>, <c>osx-x64</c>,
/// <c>linux-x64</c>, and <c>linux-arm64</c>. There is no <c>win-arm64</c> payload, so on
/// Windows ARM64 the managed wrapper throws <c>DllNotFoundException</c> at
/// <c>PortAudioSharp.PortAudio.Initialize()</c>.
/// </para>
/// <para>
/// We fill the gap at runtime by downloading PlatinumLucario's
/// <c>org.portaudio.runtime.win-arm64</c> 19.7.5 NuGet package, extracting the embedded
/// <c>portaudio.dll</c>, and redirecting the P/Invoke lookup via
/// <see cref="System.Runtime.InteropServices.NativeLibrary.SetDllImportResolver(System.Reflection.Assembly,System.Runtime.InteropServices.DllImportResolver)"/>.
/// </para>
/// <para>
/// The SHA-256 constant below is pinned to the exact binary captured from the package at
/// the time of implementation. Any drift (republished package, tampered CDN response)
/// fails verification and blocks load. To bump: download the .nupkg, extract
/// <c>runtimes/win-arm64/native/portaudio.dll</c>, compute <c>sha256sum</c>, and paste the
/// new lowercase-hex value here.
/// </para>
/// </summary>
internal static class PortAudioWinArm64Descriptor
{
    /// <summary>
    /// org.portaudio.runtime.win-arm64 19.7.5 (author: PlatinumLucario, Apache-2.0).
    /// NuGet v3 flat-container URL is stable and CDN-backed.
    /// </summary>
    private const string PackageUrl =
        "https://api.nuget.org/v3-flatcontainer/org.portaudio.runtime.win-arm64/19.7.5/org.portaudio.runtime.win-arm64.19.7.5.nupkg";

    /// <summary>
    /// SHA-256 (lowercase hex) of the extracted <c>runtimes/win-arm64/native/portaudio.dll</c>
    /// from the pinned package. 150016-byte binary built 2024-02-26.
    /// Captured on 2026-04-09 against org.portaudio.runtime.win-arm64 19.7.5.
    /// </summary>
    private const string ExpectedSha256 =
        "16a9ac4bc20d14c31dcf82ea8b50d351018bc792976654a34d862328c19cf197";

    public static NativeLibraryDescriptor Create() => new()
    {
        LibraryName = "portaudio",
        DisplayName = "Windows ARM64 PortAudio",
        AppliesTo = static () =>
            OperatingSystem.IsWindows() &&
            RuntimeInformation.ProcessArchitecture == Architecture.Arm64,
        TargetAssembly = static () => typeof(global::PortAudioSharp.PortAudio).Assembly,
        DownloadUrl = new Uri(PackageUrl, UriKind.Absolute),
        ArchiveEntryPath = "runtimes/win-arm64/native/portaudio.dll",
        CacheSubfolder = Path.Combine("NativeDeps", "win-arm64"),
        CacheFileName = "portaudio.dll",
        ExpectedSha256 = ExpectedSha256,
        FailureMarkerName = "portaudio-win-arm64.failure.json",
    };
}
