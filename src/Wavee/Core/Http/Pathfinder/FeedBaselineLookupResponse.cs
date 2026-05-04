using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wavee.Core.Http.Pathfinder;

public sealed class FeedBaselineLookupResponse
{
    [JsonPropertyName("data")]
    public FeedBaselineLookupData? Data { get; set; }
}

public sealed class FeedBaselineLookupData
{
    [JsonPropertyName("lookup")]
    public List<FeedBaselineLookupEntry>? Lookup { get; set; }
}

public sealed class FeedBaselineLookupEntry
{
    [JsonPropertyName("__typename")]
    public string? TypeName { get; set; }

    [JsonPropertyName("_uri")]
    public string? Uri { get; set; }

    [JsonPropertyName("data")]
    public JsonElement? Data { get; set; }
}

public static class FeedBaselineLookupEntryExtensions
{
    public static FeedBaselinePlaylistData? GetPlaylistData(this FeedBaselineLookupEntry entry)
    {
        if (entry.Data is not { } el || el.ValueKind == JsonValueKind.Null) return null;
        return JsonSerializer.Deserialize(el, FeedBaselineLookupJsonContext.Default.FeedBaselinePlaylistData);
    }

    public static FeedBaselineAlbumData? GetAlbumData(this FeedBaselineLookupEntry entry)
    {
        if (entry.Data is not { } el || el.ValueKind == JsonValueKind.Null) return null;
        return JsonSerializer.Deserialize(el, FeedBaselineLookupJsonContext.Default.FeedBaselineAlbumData);
    }
}

public sealed class FeedBaselinePlaylistData
{
    [JsonPropertyName("__typename")]
    public string? TypeName { get; set; }

    [JsonPropertyName("uri")]
    public string? Uri { get; set; }

    [JsonPropertyName("previewItems")]
    public FeedBaselinePreviewItemsPage? PreviewItems { get; set; }
}

public sealed class FeedBaselineAlbumData
{
    [JsonPropertyName("__typename")]
    public string? TypeName { get; set; }

    [JsonPropertyName("uri")]
    public string? Uri { get; set; }

    [JsonPropertyName("previewItems")]
    public FeedBaselinePreviewItemsPage? PreviewItems { get; set; }
}

public sealed class FeedBaselinePreviewItemsPage
{
    [JsonPropertyName("items")]
    public List<FeedBaselineTrackWrapper>? Items { get; set; }
}

public sealed class FeedBaselineTrackWrapper
{
    [JsonPropertyName("__typename")]
    public string? TypeName { get; set; }

    [JsonPropertyName("data")]
    public FeedBaselineTrackData? Data { get; set; }
}

public sealed class FeedBaselineTrackData
{
    [JsonPropertyName("__typename")]
    public string? TypeName { get; set; }

    [JsonPropertyName("uri")]
    public string? Uri { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("albumOfTrack")]
    public FeedBaselineAlbumOfTrack? AlbumOfTrack { get; set; }

    [JsonPropertyName("canvas")]
    public FeedBaselineCanvas? Canvas { get; set; }

    [JsonPropertyName("previews")]
    public FeedBaselinePreviews? Previews { get; set; }
}

public sealed class FeedBaselineAlbumOfTrack
{
    [JsonPropertyName("coverArt")]
    public HomeImageContainer? CoverArt { get; set; }
}

public sealed class FeedBaselineCanvas
{
    [JsonPropertyName("fileId")]
    public string? FileId { get; set; }

    [JsonPropertyName("thumbnail")]
    public FeedBaselineCanvasThumbnail? Thumbnail { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("uri")]
    public string? Uri { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

public sealed class FeedBaselineCanvasThumbnail
{
    [JsonPropertyName("sources")]
    public List<FeedBaselineCanvasThumbnailSource>? Sources { get; set; }
}

public sealed class FeedBaselineCanvasThumbnailSource
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

public sealed class FeedBaselinePreviews
{
    [JsonPropertyName("audioPreviews")]
    public FeedBaselineAudioPreviews? AudioPreviews { get; set; }
}

public sealed class FeedBaselineAudioPreviews
{
    [JsonPropertyName("items")]
    public List<FeedBaselineAudioPreviewItem>? Items { get; set; }
}

public sealed class FeedBaselineAudioPreviewItem
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

public sealed class FeedBaselineLookupVariables
{
    [JsonPropertyName("uris")]
    public List<string> Uris { get; set; } = [];
}

[JsonSerializable(typeof(FeedBaselineLookupResponse))]
[JsonSerializable(typeof(FeedBaselinePlaylistData))]
[JsonSerializable(typeof(FeedBaselineAlbumData))]
[JsonSerializable(typeof(HomeImageContainer))]
[JsonSerializable(typeof(HomeImageSource))]
[JsonSerializable(typeof(HomeExtractedColors))]
[JsonSerializable(typeof(HomeColorValue))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
public partial class FeedBaselineLookupJsonContext : JsonSerializerContext;

[JsonSerializable(typeof(FeedBaselineLookupVariables))]
public partial class FeedBaselineLookupVariablesJsonContext : JsonSerializerContext;
