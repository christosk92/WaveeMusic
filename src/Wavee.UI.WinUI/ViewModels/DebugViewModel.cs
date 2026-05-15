using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Wavee.Core.Http;
using Wavee.Core.Session;
using Wavee.Protocol.Collection;
using Wavee.Protocol.ExtendedMetadata;
using Wavee.Protocol.Metadata;
using Wavee.UI.WinUI.ViewModels.DebugTools;

namespace Wavee.UI.WinUI.ViewModels;

public sealed partial class DebugViewModel : ObservableObject
{
    private readonly ISession _session;
    private readonly IExtendedMetadataClient _extendedMetadataClient;
    private readonly ILogger? _logger;
    private readonly string _resolvedBaseUrl;

    // ── Endpoint family ──
    public string[] EndpointFamilies { get; } = ["SpClient REST", "Extended Metadata", "Pathfinder GraphQL"];
    [ObservableProperty] private int _selectedEndpointFamily;

    // ── Base URL (SpClient family only) ──
    [ObservableProperty] private string _baseUrl = "";

    // ── URL bar (SpClient family) ──
    public string[] HttpMethods { get; } = ["GET", "POST", "PUT", "DELETE", "PATCH"];
    [ObservableProperty] private string _selectedMethod = "GET";
    [ObservableProperty] private string _path = "/";

    // ── SpClient presets ──
    public string[] Presets { get; } = ["Custom", "Collection Page", "Collection Delta", "Collection Write"];
    [ObservableProperty] private int _selectedPresetIndex;

    // ── SpClient preset fields ──
    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _set = "collection";
    [ObservableProperty] private int _limit = 300;
    [ObservableProperty] private string _paginationToken = "";
    [ObservableProperty] private string _lastSyncToken = "";
    [ObservableProperty] private string _itemUri = "spotify:track:";
    [ObservableProperty] private bool _isRemoved;

    // ── Custom body (SpClient) ──
    [ObservableProperty] private string _requestBody = "";
    [ObservableProperty] private string _contentType = "application/json";

    // ── Extended Metadata fields ──
    // No canonical PLAYLIST_V4 exists in the extended-metadata enum — full
    // playlist payloads come from Pathfinder's `fetchPlaylist` (use the
    // Pathfinder family for that). The PLAYLIST_* entries below are the
    // playlist-adjacent kinds the protocol does expose.
    public string[] ExtensionKindNames { get; } =
    [
        "TRACK_V4",
        "ALBUM_V4",
        "ARTIST_V4",
        "SHOW_V4",
        "SHOW_V5",
        "SHOW_V4_BASE",
        "EPISODE_V4",
        "EPISODE_V5",
        "AUDIO_FILES",
        "AUDIO_ATTRIBUTES",
        "EXTRACTED_COLOR",
        "PODCAST_TOPICS",
        "PODCAST_HTML_DESCRIPTION",
        "PODCAST_SEGMENTS",
        "EPISODE_TRANSCRIPTS",
        "USER_PROFILE",
        "STORYLINES",
        "CANVAZ",
        "CONTENT_WARNING",
        "CLIPS",
        "PLAYLIST_ATTRIBUTES_V2",
        "PLAYLISTABILITY",
        "PLAYLIST_DESCRIPTORS",
        "(custom — type below)"
    ];
    [ObservableProperty] private int _selectedExtensionKindIndex;
    [ObservableProperty] private string _customExtensionKind = "";
    [ObservableProperty] private string _entityUris = "";

    // ── Pathfinder fields ──
    public PathfinderCatalog.Operation[] PathfinderOperations { get; } = PathfinderCatalog.All;
    [ObservableProperty] private int _selectedPathfinderOperationIndex;
    [ObservableProperty] private string _pathfinderOperationName = "";
    [ObservableProperty] private string _pathfinderHash = "";
    [ObservableProperty] private string _pathfinderVariables = "{}";
    [ObservableProperty] private string _pathfinderCustomHash = "";
    [ObservableProperty] private string _pathfinderBaseUrl = "https://api-partner.spotify.com";

    // ── Response (shared across all families) ──
    [ObservableProperty] private string _responseBody = "";
    [ObservableProperty] private string _responseHeaders = "";
    [ObservableProperty] private int _selectedResponseTab;
    [ObservableProperty] private bool _isLoading;

    // ── Status bar ──
    [ObservableProperty] private string _statusCode = "";
    [ObservableProperty] private string _statusTime = "";
    [ObservableProperty] private string _statusSize = "";
    [ObservableProperty] private bool _isSuccess;

    public DebugViewModel(
        ISession session,
        IExtendedMetadataClient extendedMetadataClient,
        ILogger<DebugViewModel>? logger = null)
    {
        _session = session;
        _extendedMetadataClient = extendedMetadataClient;
        _logger = logger;

        _resolvedBaseUrl = session.SpClient?.BaseUrl ?? "https://spclient.wg.spotify.com";
        BaseUrl = _resolvedBaseUrl;

        var userData = session.GetUserData();
        if (userData != null)
            Username = userData.Username;

        // Seed Pathfinder fields from the first catalog entry so the UI shows a
        // valid starting point. Direct call (not property assignment) because
        // SelectedPathfinderOperationIndex defaults to 0 already and a no-op
        // assignment doesn't fire the source-generator's changed handler.
        if (PathfinderOperations.Length > 0)
            OnSelectedPathfinderOperationIndexChanged(0);
    }

    partial void OnSelectedPresetIndexChanged(int value)
    {
        switch (value)
        {
            case 1: // Collection Page
                SelectedMethod = "POST";
                Path = "/collection/v2/paging";
                break;
            case 2: // Collection Delta
                SelectedMethod = "POST";
                Path = "/collection/v2/delta";
                break;
            case 3: // Collection Write
                SelectedMethod = "POST";
                Path = "/collection/v2/write";
                break;
        }
    }

    partial void OnSelectedPathfinderOperationIndexChanged(int value)
    {
        if (value < 0 || value >= PathfinderOperations.Length) return;
        var op = PathfinderOperations[value];
        PathfinderOperationName = op.Name;
        PathfinderHash = op.Hash;
        PathfinderVariables = op.ExampleVariables;
        // Don't clobber a manual override the user is in the middle of typing.
        if (string.IsNullOrWhiteSpace(PathfinderCustomHash))
            PathfinderCustomHash = "";
    }

    [RelayCommand]
    private void ResetBaseUrl() => BaseUrl = _resolvedBaseUrl;

    [RelayCommand]
    private void FormatBody()
    {
        // Family-aware: format whichever JSON body is currently visible.
        switch (SelectedEndpointFamily)
        {
            case 2:
                if (!string.IsNullOrWhiteSpace(PathfinderVariables))
                {
                    try
                    {
                        var doc = JsonDocument.Parse(PathfinderVariables);
                        PathfinderVariables = PrettyPrintJson(doc);
                    }
                    catch (Exception ex) { _logger?.LogDebug(ex, "Pathfinder variables not valid JSON"); }
                }
                break;
            default:
                if (!string.IsNullOrWhiteSpace(RequestBody))
                {
                    try
                    {
                        var doc = JsonDocument.Parse(RequestBody);
                        RequestBody = PrettyPrintJson(doc);
                    }
                    catch (Exception ex) { _logger?.LogDebug(ex, "Request body not valid JSON"); }
                }
                break;
        }
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        ResponseBody = "";
        ResponseHeaders = "";
        StatusCode = "";
        StatusTime = "";
        StatusSize = "";

        var sw = Stopwatch.StartNew();
        try
        {
            switch (SelectedEndpointFamily)
            {
                case 1:
                    await SendExtendedMetadataAsync(sw);
                    break;
                case 2:
                    await SendPathfinderAsync(sw);
                    break;
                default:
                    await SendSpClientAsync(sw);
                    break;
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            StatusCode = "Error";
            StatusTime = $"{sw.ElapsedMilliseconds}ms";
            IsSuccess = false;
            ResponseBody = $"{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}";
            SelectedResponseTab = 0;
            _logger?.LogError(ex, "Debug request failed");
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ── SpClient REST family (existing behaviour) ──────────────────────────

    private async Task SendSpClientAsync(Stopwatch sw)
    {
        var accessToken = await _session.GetAccessTokenAsync();
        var url = $"{BaseUrl.TrimEnd('/')}{Path}";

        using var httpClient = new HttpClient();
        using var request = new HttpRequestMessage(new HttpMethod(SelectedMethod), url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
        request.Headers.UserAgent.ParseAdd("Spotify/8.9.0 Android/30");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

        if (SelectedMethod is "POST" or "PUT" or "PATCH")
        {
            if (SelectedPresetIndex > 0)
                AttachPresetBody(request);
            else if (!string.IsNullOrWhiteSpace(RequestBody))
            {
                request.Content = new StringContent(RequestBody, Encoding.UTF8);
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(ContentType);
            }
        }

        var response = await httpClient.SendAsync(request);
        sw.Stop();
        await ApplyHttpResponseAsync(response, sw);
    }

    private void AttachPresetBody(HttpRequestMessage request)
    {
        const string protoContentType = "application/vnd.collection-v2.spotify.proto";
        byte[] bytes;

        switch (SelectedPresetIndex)
        {
            case 1: // Page
                var pageReq = new PageRequest
                {
                    Username = Username,
                    Set = Set,
                    Limit = Limit
                };
                if (!string.IsNullOrWhiteSpace(PaginationToken))
                    pageReq.PaginationToken = PaginationToken;
                bytes = pageReq.ToByteArray();
                break;

            case 2: // Delta
                var deltaReq = new DeltaRequest
                {
                    Username = Username,
                    Set = Set,
                    LastSyncToken = string.IsNullOrWhiteSpace(LastSyncToken) ? " " : LastSyncToken
                };
                bytes = deltaReq.ToByteArray();
                break;

            case 3: // Write
                var writeReq = new WriteRequest
                {
                    Username = Username,
                    Set = Set,
                    ClientUpdateId = Guid.NewGuid().ToString("N")
                };
                writeReq.Items.Add(new CollectionItem
                {
                    Uri = ItemUri,
                    IsRemoved = IsRemoved
                });
                bytes = writeReq.ToByteArray();
                break;

            default:
                return;
        }

        request.Content = new ByteArrayContent(bytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(protoContentType);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(protoContentType));
    }

    // ── Extended Metadata family ────────────────────────────────────────────

    private async Task SendExtendedMetadataAsync(Stopwatch sw)
    {
        var uris = (EntityUris ?? "")
            .Split(new[] { ',', '\n', '\r', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct()
            .ToArray();

        if (uris.Length == 0)
        {
            sw.Stop();
            StatusCode = "No URIs";
            StatusTime = $"{sw.ElapsedMilliseconds}ms";
            IsSuccess = false;
            ResponseBody = "Enter at least one entity URI (e.g. spotify:track:4cOdK2wGLETKBW3PvgPWqT). Multiple URIs may be comma- or newline-separated.";
            SelectedResponseTab = 0;
            return;
        }

        if (!TryResolveExtensionKind(out var kind, out var kindError))
        {
            sw.Stop();
            StatusCode = "Bad kind";
            StatusTime = $"{sw.ElapsedMilliseconds}ms";
            IsSuccess = false;
            ResponseBody = kindError ?? "Invalid extension kind.";
            SelectedResponseTab = 0;
            return;
        }

        var requests = uris.Select(u => (u, (IEnumerable<ExtensionKind>)new[] { kind })).ToArray();

        var response = await _extendedMetadataClient.GetBatchedExtensionsAsync(requests);
        sw.Stop();

        // The typed client doesn't surface raw HTTP headers, so synthesize a
        // minimal status indicator from the response shape.
        var hits = uris.Count(u => response.GetExtensionData(u, kind) is not null);
        StatusCode = hits == uris.Length ? "200 OK"
            : hits > 0 ? $"200 OK (partial: {hits}/{uris.Length})"
            : "200 OK (empty)";
        StatusTime = $"{sw.ElapsedMilliseconds}ms";
        IsSuccess = hits > 0;

        ResponseBody = FormatExtendedMetadataResponse(response, uris, kind);
        ResponseHeaders =
            $"(typed client — raw headers not exposed)\n" +
            $"Endpoint: POST /extended-metadata/v0/extended-metadata\n" +
            $"Kind: {kind} ({(int)kind})\n" +
            $"Entities requested: {uris.Length}\n" +
            $"Entities returned with data: {hits}";
        StatusSize = FormatSize(Encoding.UTF8.GetByteCount(ResponseBody));
        SelectedResponseTab = 0;
    }

    private bool TryResolveExtensionKind(out ExtensionKind kind, out string? error)
    {
        var name = SelectedExtensionKindIndex >= 0 && SelectedExtensionKindIndex < ExtensionKindNames.Length
            ? ExtensionKindNames[SelectedExtensionKindIndex]
            : "";

        if (name.StartsWith("("))
            name = (CustomExtensionKind ?? "").Trim().ToUpperInvariant();

        if (string.IsNullOrEmpty(name))
        {
            kind = default;
            error = "Pick a kind (or enter a custom one).";
            return false;
        }

        // Map UPPER_SNAKE → enum PascalCase.
        var pascal = string.Concat(name.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(static p => char.ToUpperInvariant(p[0]) + p[1..].ToLowerInvariant()));

        if (Enum.TryParse<ExtensionKind>(pascal, ignoreCase: false, out kind))
        {
            error = null;
            return true;
        }

        error = $"Unknown extension kind: {name}. Try one of: TRACK_V4, ALBUM_V4, ARTIST_V4, SHOW_V5, EPISODE_V5, EXTRACTED_COLOR, ...";
        return false;
    }

    private string FormatExtendedMetadataResponse(BatchedExtensionResponse response, string[] uris, ExtensionKind kind)
    {
        var sb = new StringBuilder();
        sb.Append('[');
        bool first = true;
        foreach (var uri in uris)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.AppendLine();
            sb.Append("  {");
            sb.AppendLine();
            sb.Append("    \"uri\": ").Append(JsonString(uri)).Append(',').AppendLine();
            sb.Append("    \"kind\": ").Append(JsonString(kind.ToString())).Append(',').AppendLine();

            var ext = response.GetExtensionData(uri, kind);
            if (ext is null)
            {
                sb.Append("    \"data\": null").AppendLine();
            }
            else
            {
                var (decoded, asJson) = TryDecodeExtension(ext, kind);
                if (asJson is not null)
                {
                    sb.Append("    \"data\": ").Append(IndentJson(asJson, 4)).AppendLine();
                }
                else
                {
                    sb.Append("    \"size_bytes\": ").Append(decoded).Append(',').AppendLine();
                    sb.Append("    \"raw_base64\": ").Append(JsonString(Convert.ToBase64String(ext.ExtensionData?.Value?.ToByteArray() ?? Array.Empty<byte>()))).AppendLine();
                }
            }

            sb.Append("  }");
        }
        sb.AppendLine();
        sb.Append(']');
        return sb.ToString();
    }

    private (int sizeBytes, string? json) TryDecodeExtension(EntityExtensionData ext, ExtensionKind kind)
    {
        var bytes = ext.ExtensionData?.Value?.ToByteArray() ?? Array.Empty<byte>();
        try
        {
            IMessage? msg = kind switch
            {
                ExtensionKind.TrackV4 => Track.Parser.ParseFrom(bytes),
                ExtensionKind.AlbumV4 => Album.Parser.ParseFrom(bytes),
                ExtensionKind.ArtistV4 => Artist.Parser.ParseFrom(bytes),
                ExtensionKind.ShowV4 => Show.Parser.ParseFrom(bytes),
                ExtensionKind.ShowV5 => Show.Parser.ParseFrom(bytes),
                ExtensionKind.ShowV4Base => Show.Parser.ParseFrom(bytes),
                ExtensionKind.EpisodeV4 => Episode.Parser.ParseFrom(bytes),
                ExtensionKind.EpisodeV5 => Episode.Parser.ParseFrom(bytes),
                _ => null
            };
            if (msg is null) return (bytes.Length, null);
            return (bytes.Length, JsonFormatter.Default.Format(msg));
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to decode extension {Kind}", kind);
            return (bytes.Length, null);
        }
    }

    // ── Pathfinder GraphQL family ──────────────────────────────────────────

    private async Task SendPathfinderAsync(Stopwatch sw)
    {
        if (string.IsNullOrWhiteSpace(PathfinderOperationName))
        {
            sw.Stop();
            StatusCode = "No operation"; StatusTime = $"{sw.ElapsedMilliseconds}ms"; IsSuccess = false;
            ResponseBody = "Pick an operation from the picker (or set the operation name + hash manually).";
            SelectedResponseTab = 0;
            return;
        }

        var hash = string.IsNullOrWhiteSpace(PathfinderCustomHash) ? PathfinderHash : PathfinderCustomHash.Trim();
        if (string.IsNullOrWhiteSpace(hash))
        {
            sw.Stop();
            StatusCode = "No hash"; StatusTime = $"{sw.ElapsedMilliseconds}ms"; IsSuccess = false;
            ResponseBody = "Persisted-query hash is required.";
            SelectedResponseTab = 0;
            return;
        }

        // Sanity-check variables JSON before sending so the error is friendly.
        var variablesJson = string.IsNullOrWhiteSpace(PathfinderVariables) ? "{}" : PathfinderVariables;
        try { using var _ = JsonDocument.Parse(variablesJson); }
        catch (JsonException jex)
        {
            sw.Stop();
            StatusCode = "Bad JSON"; StatusTime = $"{sw.ElapsedMilliseconds}ms"; IsSuccess = false;
            ResponseBody = $"Variables JSON is invalid:\n{jex.Message}";
            SelectedResponseTab = 0;
            return;
        }

        var body = BuildPathfinderRequestBody(PathfinderOperationName, hash, variablesJson);

        var accessToken = await _session.GetAccessTokenAsync();
        var url = $"{(string.IsNullOrWhiteSpace(PathfinderBaseUrl) ? "https://api-partner.spotify.com" : PathfinderBaseUrl).TrimEnd('/')}/pathfinder/v2/query";

        using var httpClient = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await httpClient.SendAsync(request);
        sw.Stop();
        await ApplyHttpResponseAsync(response, sw);
    }

    private static string BuildPathfinderRequestBody(string operationName, string hash, string variablesJson)
    {
        using var ms = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();

            writer.WritePropertyName("variables");
            using (var varsDoc = JsonDocument.Parse(variablesJson))
                varsDoc.RootElement.WriteTo(writer);

            writer.WriteString("operationName", operationName);

            writer.WriteStartObject("extensions");
            writer.WriteStartObject("persistedQuery");
            writer.WriteNumber("version", 1);
            writer.WriteString("sha256Hash", hash);
            writer.WriteEndObject();
            writer.WriteEndObject();

            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    // ── Shared response handling ───────────────────────────────────────────

    private async Task ApplyHttpResponseAsync(HttpResponseMessage response, Stopwatch sw)
    {
        var statusInt = (int)response.StatusCode;
        StatusCode = $"{statusInt} {response.ReasonPhrase}";
        StatusTime = $"{sw.ElapsedMilliseconds}ms";
        IsSuccess = statusInt is >= 200 and < 300;

        var hdrSb = new StringBuilder();
        foreach (var h in response.Headers)
            hdrSb.AppendLine($"{h.Key}: {string.Join(", ", h.Value)}");
        foreach (var h in response.Content.Headers)
            hdrSb.AppendLine($"{h.Key}: {string.Join(", ", h.Value)}");
        ResponseHeaders = hdrSb.ToString();

        var ct = response.Content.Headers.ContentType?.MediaType ?? "";
        if (ct.Contains("json"))
        {
            var raw = await response.Content.ReadAsStringAsync();
            try
            {
                var doc = JsonDocument.Parse(raw);
                ResponseBody = PrettyPrintJson(doc);
            }
            catch (Exception ex) { _logger?.LogDebug(ex, "Failed to pretty-print JSON response"); ResponseBody = raw; }
        }
        else if (ct.Contains("text") || ct.Contains("html") || ct.Contains("xml"))
        {
            ResponseBody = await response.Content.ReadAsStringAsync();
        }
        else if (ct.Contains("protobuf") || ct.Contains("proto"))
        {
            var bytes = await response.Content.ReadAsByteArrayAsync();
            ResponseBody = FormatProtobufResponse(bytes);
        }
        else
        {
            var bytes = await response.Content.ReadAsByteArrayAsync();
            ResponseBody = bytes.Length == 0
                ? "(empty response)"
                : FormatHexDump(bytes);
        }

        StatusSize = FormatSize(Encoding.UTF8.GetByteCount(ResponseBody));
        SelectedResponseTab = 0;
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static string PrettyPrintJson(JsonDocument doc)
    {
        using var ms = new System.IO.MemoryStream();
        using var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true });
        doc.WriteTo(writer);
        writer.Flush();
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string JsonString(string s)
    {
        // Lightweight JSON string escaper for the Extended-Metadata response builder.
        using var ms = new System.IO.MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
            w.WriteStringValue(s);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string IndentJson(string json, int spaces)
    {
        // Re-indent a multi-line JSON block by `spaces` extra columns so it
        // reads naturally inside the surrounding hand-built JSON envelope.
        var prefix = new string(' ', spaces);
        var lines = json.Split('\n');
        var sb = new StringBuilder(json.Length + lines.Length * spaces);
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0) { sb.Append('\n').Append(prefix); }
            sb.Append(lines[i].TrimEnd('\r'));
        }
        return sb.ToString();
    }

    private string FormatProtobufResponse(byte[] bytes)
    {
        var sb = new StringBuilder();

        try
        {
            var page = PageResponse.Parser.ParseFrom(bytes);
            if (page.Items.Count > 0 || !string.IsNullOrEmpty(page.NextPageToken))
            {
                sb.AppendLine($"PageResponse ({page.Items.Count} items)");
                sb.AppendLine($"  NextPageToken: {(string.IsNullOrEmpty(page.NextPageToken) ? "(none)" : page.NextPageToken)}");
                sb.AppendLine($"  SyncToken: {(string.IsNullOrEmpty(page.SyncToken) ? "(none)" : page.SyncToken)}");
                sb.AppendLine();
                foreach (var item in page.Items)
                    sb.AppendLine($"  {item.Uri}  added={item.AddedAt}  removed={item.IsRemoved}");
                return sb.ToString();
            }
        }
        catch (Exception ex) { _logger?.LogDebug(ex, "Protobuf bytes are not a PageResponse"); }

        try
        {
            var delta = DeltaResponse.Parser.ParseFrom(bytes);
            if (delta.Items.Count > 0 || delta.DeltaUpdatePossible)
            {
                sb.AppendLine($"DeltaResponse ({delta.Items.Count} items)");
                sb.AppendLine($"  DeltaUpdatePossible: {delta.DeltaUpdatePossible}");
                sb.AppendLine($"  SyncToken: {(string.IsNullOrEmpty(delta.SyncToken) ? "(none)" : delta.SyncToken)}");
                sb.AppendLine();
                foreach (var item in delta.Items)
                    sb.AppendLine($"  {item.Uri}  added={item.AddedAt}  removed={item.IsRemoved}");
                return sb.ToString();
            }
        }
        catch (Exception ex) { _logger?.LogDebug(ex, "Protobuf bytes are not a DeltaResponse"); }

        sb.AppendLine($"Binary protobuf ({bytes.Length} bytes):");
        sb.AppendLine();
        sb.Append(FormatHexDump(bytes));
        return sb.ToString();
    }

    private static string FormatHexDump(byte[] bytes)
    {
        var sb = new StringBuilder();
        var limit = Math.Min(bytes.Length, 1024);
        for (int i = 0; i < limit; i++)
        {
            if (i % 16 == 0 && i > 0) sb.AppendLine();
            if (i % 16 == 0) sb.Append($"{i:X4}  ");
            sb.Append($"{bytes[i]:X2} ");
        }
        if (bytes.Length > 1024)
            sb.AppendLine($"\n... ({bytes.Length - 1024} more bytes)");
        return sb.ToString();
    }

    private static string FormatSize(int bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1048576 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / 1048576.0:F1} MB"
    };
}
