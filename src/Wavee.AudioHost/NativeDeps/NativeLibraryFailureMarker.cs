using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wavee.AudioHost.NativeDeps;

/// <summary>
/// Shape of the failure-marker JSON file. Kept minimal and stable — any reader must
/// tolerate unknown fields.
/// </summary>
internal sealed class NativeLibraryFailurePayload
{
    [JsonPropertyName("displayName")]  public string DisplayName { get; set; } = "";
    [JsonPropertyName("libraryName")]  public string LibraryName { get; set; } = "";
    [JsonPropertyName("reason")]       public string Reason { get; set; } = "";
    [JsonPropertyName("url")]          public string? Url { get; set; }
    [JsonPropertyName("exception")]    public string? ExceptionType { get; set; }
    [JsonPropertyName("timestampUtc")] public string TimestampUtc { get; set; } = "";
}

/// <summary>
/// Source-generated JSON context so the marker file can be (de)serialized under trim/AOT.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(NativeLibraryFailurePayload))]
internal partial class NativeLibraryFailureJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Static helper that writes a small JSON failure marker to %LOCALAPPDATA%\Wavee\NativeDeps\
/// when <see cref="NativeLibraryProvisioner"/> fails. The UI-side AudioProcessManager detects
/// this marker on child-process exit code 3 and surfaces a specific error toast instead of
/// running the default exponential-backoff restart loop.
///
/// Contract: the marker lives alongside the NativeDeps cache folder (one level above
/// the per-platform subfolder) so it is visible across every provisioning target.
/// </summary>
internal static class NativeLibraryFailureMarker
{
    /// <summary>
    /// Returns the absolute path to the marker file for a given descriptor.
    /// </summary>
    public static string GetMarkerPath(NativeLibraryDescriptor descriptor)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Wavee", "NativeDeps");
        return Path.Combine(root, descriptor.FailureMarkerName);
    }

    /// <summary>
    /// Atomically writes a failure marker describing a provisioning failure.
    /// Swallows IO errors — a missing marker just means the UI will report a generic crash.
    /// </summary>
    public static void Write(NativeLibraryDescriptor descriptor, NativeLibraryProvisioningResult result)
    {
        try
        {
            var markerPath = GetMarkerPath(descriptor);
            var dir = Path.GetDirectoryName(markerPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var payload = new NativeLibraryFailurePayload
            {
                DisplayName = descriptor.DisplayName,
                LibraryName = descriptor.LibraryName,
                Reason = result.FailureReason ?? "Unknown",
                Url = descriptor.DownloadUrl.ToString(),
                ExceptionType = result.FailureException?.GetType().FullName,
                TimestampUtc = DateTime.UtcNow.ToString("o")
            };

            var json = JsonSerializer.Serialize(
                payload, NativeLibraryFailureJsonContext.Default.NativeLibraryFailurePayload);
            var tmpPath = markerPath + ".tmp";
            File.WriteAllText(tmpPath, json);
            File.Move(tmpPath, markerPath, overwrite: true);
        }
        catch
        {
            // Best-effort. If we can't even write a marker, surfacing a generic crash is acceptable.
        }
    }

    /// <summary>
    /// Deletes the marker file if present. Called by the UI after it has read the marker,
    /// so a subsequent successful run does not re-trigger the "provisioning failed" toast.
    /// </summary>
    public static void TryDelete(NativeLibraryDescriptor descriptor)
    {
        try { File.Delete(GetMarkerPath(descriptor)); }
        catch { /* best effort */ }
    }
}
