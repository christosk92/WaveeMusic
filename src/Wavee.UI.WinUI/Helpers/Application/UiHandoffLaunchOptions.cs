using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wavee.UI.WinUI.Helpers.Application;

internal sealed record UiHandoffLaunchOptions(
    int AudioHostProcessId,
    string PipeName,
    string SessionId,
    string LaunchToken,
    string FilePath)
{
    public const string CommandLineSwitch = "--wavee-ui-handoff";

    public static UiHandoffLaunchOptions? TryReadFromCommandLine(string[] args)
    {
        if (args.Length == 0)
            return null;

        for (var i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], CommandLineSwitch, StringComparison.OrdinalIgnoreCase))
                continue;
            if (i + 1 >= args.Length)
                return null;

            return TryReadFromFile(args[i + 1]);
        }

        return null;
    }

    public void TryDeleteFile()
    {
        try
        {
            if (File.Exists(FilePath))
                File.Delete(FilePath);
        }
        catch
        {
            // Best effort cleanup only.
        }
    }

    private static UiHandoffLaunchOptions? TryReadFromFile(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return null;

            var json = File.ReadAllText(path);
            var payload = JsonSerializer.Deserialize(json, UiHandoffJsonContext.Default.UiHandoffFilePayload);
            if (payload is null
                || payload.AudioHostProcessId <= 0
                || string.IsNullOrWhiteSpace(payload.PipeName)
                || string.IsNullOrWhiteSpace(payload.SessionId)
                || string.IsNullOrWhiteSpace(payload.LaunchToken))
            {
                return null;
            }

            var age = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - payload.CreatedAtUnixMs;
            if (age < 0 || age > TimeSpan.FromMinutes(5).TotalMilliseconds)
                return null;

            return new UiHandoffLaunchOptions(
                payload.AudioHostProcessId,
                payload.PipeName,
                payload.SessionId,
                payload.LaunchToken,
                path);
        }
        catch
        {
            return null;
        }
    }
}

internal sealed class UiHandoffFilePayload
{
    [JsonPropertyName("audioHostProcessId")]
    public int AudioHostProcessId { get; init; }

    [JsonPropertyName("pipeName")]
    public required string PipeName { get; init; }

    [JsonPropertyName("sessionId")]
    public required string SessionId { get; init; }

    [JsonPropertyName("launchToken")]
    public required string LaunchToken { get; init; }

    [JsonPropertyName("createdAtUnixMs")]
    public long CreatedAtUnixMs { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(UiHandoffFilePayload))]
internal partial class UiHandoffJsonContext : JsonSerializerContext;
