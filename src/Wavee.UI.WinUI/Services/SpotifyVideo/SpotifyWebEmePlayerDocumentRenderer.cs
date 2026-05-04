using System;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core.Video;
using Windows.Storage;

namespace Wavee.UI.WinUI.Services.SpotifyVideo;

internal sealed class SpotifyWebEmePlayerDocumentRenderer
{
    private const string ConfigPlaceholder = "__WAVEE_PLAYER_CONFIG__";
    private const string StartSecondsPlaceholder = "__WAVEE_START_SECONDS__";
    private const string AutoPlayPlaceholder = "__WAVEE_AUTO_PLAY__";
    private static readonly Uri TemplateUri = new("ms-appx:///Services/SpotifyVideo/WebEmePlayer.html");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private string? _template;

    public async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_template is not null)
            return;

        cancellationToken.ThrowIfCancellationRequested();
        var file = await StorageFile.GetFileFromApplicationUriAsync(TemplateUri);
        var template = await FileIO.ReadTextAsync(file);
        cancellationToken.ThrowIfCancellationRequested();

        if (!template.Contains(ConfigPlaceholder, StringComparison.Ordinal)
            || !template.Contains(StartSecondsPlaceholder, StringComparison.Ordinal)
            || !template.Contains(AutoPlayPlaceholder, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Spotify Web EME player template is missing required placeholders.");
        }

        _template = template;
    }

    public string Render(SpotifyWebEmeVideoManifest config, double startPositionMs, bool autoPlay)
    {
        if (_template is null)
            throw new InvalidOperationException("Spotify Web EME player template has not been loaded.");

        var configJson = JsonSerializer.Serialize(config, JsonOptions);
        var startSeconds = (Math.Max(0, startPositionMs) / 1000d)
            .ToString(CultureInfo.InvariantCulture);

        return _template
            .Replace(ConfigPlaceholder, configJson, StringComparison.Ordinal)
            .Replace(StartSecondsPlaceholder, startSeconds, StringComparison.Ordinal)
            .Replace(AutoPlayPlaceholder, autoPlay ? "true" : "false", StringComparison.Ordinal);
    }
}
