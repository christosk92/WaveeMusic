using System.Reflection;

namespace Wavee.AudioHost.NativeDeps;

/// <summary>
/// Immutable description of one native library that must be provisioned at runtime
/// when the NuGet-supplied copy does not cover the current platform (e.g. win-arm64).
/// Descriptor instances are data-only — behaviour lives in <see cref="NativeLibraryProvisioner"/>.
/// </summary>
internal sealed class NativeLibraryDescriptor
{
    /// <summary>
    /// The exact P/Invoke name the managed wrapper imports. On Windows the runtime appends
    /// ".dll"; on Linux it prepends "lib" and appends ".so". Use the bare name (e.g. "portaudio").
    /// </summary>
    public required string LibraryName { get; init; }

    /// <summary>
    /// Returns true if the current process requires this native library to be provisioned.
    /// Typical shape: <c>() =&gt; OperatingSystem.IsWindows() &amp;&amp; RuntimeInformation.ProcessArchitecture == Architecture.Arm64</c>.
    /// </summary>
    public required Func<bool> AppliesTo { get; init; }

    /// <summary>
    /// The managed assembly whose <c>[DllImport]</c> calls should be intercepted by the
    /// provisioner's <see cref="System.Runtime.InteropServices.NativeLibrary.SetDllImportResolver"/>.
    /// Resolved lazily so the descriptor can be constructed without forcing assembly load.
    /// </summary>
    public required Func<Assembly> TargetAssembly { get; init; }

    /// <summary>
    /// HTTPS URL to a .nupkg archive that contains the required native binary.
    /// NuGet flat-container URLs are preferred (stable, CDN-backed).
    /// </summary>
    public required Uri DownloadUrl { get; init; }

    /// <summary>
    /// Forward-slash relative path of the native binary inside the archive,
    /// e.g. "runtimes/win-arm64/native/portaudio.dll".
    /// </summary>
    public required string ArchiveEntryPath { get; init; }

    /// <summary>
    /// Subfolder under %LOCALAPPDATA%\Wavee\ where the cached binary is stored.
    /// Example: "NativeDeps\\win-arm64".
    /// </summary>
    public required string CacheSubfolder { get; init; }

    /// <summary>
    /// File name of the cached binary, e.g. "portaudio.dll".
    /// </summary>
    public required string CacheFileName { get; init; }

    /// <summary>
    /// Lowercase-hex SHA-256 of the extracted native binary.
    /// Pinned by the maintainer; any drift fails verification and blocks load.
    /// </summary>
    public required string ExpectedSha256 { get; init; }

    /// <summary>
    /// Short human-readable name used in log messages and the UI failure toast.
    /// </summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// File name (no path) of the failure-marker file dropped into
    /// %LOCALAPPDATA%\Wavee\NativeDeps\ when provisioning fails. Used by the UI's
    /// AudioProcessManager to detect a deterministic failure and skip the auto-restart loop.
    /// </summary>
    public required string FailureMarkerName { get; init; }
}
