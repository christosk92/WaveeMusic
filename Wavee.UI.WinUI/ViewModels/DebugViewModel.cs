using System;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Wavee.Core.Session;
using Wavee.Protocol.Collection;

namespace Wavee.UI.WinUI.ViewModels;

public sealed partial class DebugViewModel : ObservableObject
{
    private readonly ISession _session;
    private readonly ILogger? _logger;
    private readonly string _resolvedBaseUrl;

    // ── Base URL ──
    [ObservableProperty] private string _baseUrl = "";

    // ── URL bar ──
    public string[] HttpMethods { get; } = ["GET", "POST", "PUT", "DELETE", "PATCH"];
    [ObservableProperty] private string _selectedMethod = "GET";
    [ObservableProperty] private string _path = "/";

    // ── Presets ──
    public string[] Presets { get; } = ["Custom", "Collection Page", "Collection Delta", "Collection Write"];
    [ObservableProperty] private int _selectedPresetIndex;

    // ── Preset fields (visible when preset != Custom) ──
    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _set = "collection";
    [ObservableProperty] private int _limit = 300;
    [ObservableProperty] private string _paginationToken = "";
    [ObservableProperty] private string _lastSyncToken = "";
    [ObservableProperty] private string _itemUri = "spotify:track:";
    [ObservableProperty] private bool _isRemoved;

    // ── Body (custom mode) ──
    [ObservableProperty] private string _requestBody = "";
    [ObservableProperty] private string _contentType = "application/json";

    // ── Response ──
    [ObservableProperty] private string _responseBody = "";
    [ObservableProperty] private string _responseHeaders = "";
    [ObservableProperty] private int _selectedResponseTab;
    [ObservableProperty] private bool _isLoading;

    // ── Status bar ──
    [ObservableProperty] private string _statusCode = "";
    [ObservableProperty] private string _statusTime = "";
    [ObservableProperty] private string _statusSize = "";
    [ObservableProperty] private bool _isSuccess;

    public DebugViewModel(ISession session, ILogger<DebugViewModel>? logger = null)
    {
        _session = session;
        _logger = logger;

        _resolvedBaseUrl = session.SpClient?.BaseUrl ?? "https://spclient.wg.spotify.com";
        BaseUrl = _resolvedBaseUrl;

        var userData = session.GetUserData();
        if (userData != null)
            Username = userData.Username;
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

    [RelayCommand]
    private void ResetBaseUrl() => BaseUrl = _resolvedBaseUrl;

    [RelayCommand]
    private void FormatBody()
    {
        if (string.IsNullOrWhiteSpace(RequestBody)) return;
        try
        {
            var doc = JsonDocument.Parse(RequestBody);
            RequestBody = PrettyPrintJson(doc);
        }
        catch { /* not valid JSON, leave as-is */ }
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
            var accessToken = await _session.GetAccessTokenAsync();
            var url = $"{BaseUrl.TrimEnd('/')}{Path}";

            using var httpClient = new HttpClient();
            using var request = new HttpRequestMessage(new HttpMethod(SelectedMethod), url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
            request.Headers.UserAgent.ParseAdd("Spotify/8.9.0 Android/30");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

            // Build body
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

            // Status bar
            var statusInt = (int)response.StatusCode;
            StatusCode = $"{statusInt} {response.ReasonPhrase}";
            StatusTime = $"{sw.ElapsedMilliseconds}ms";
            IsSuccess = statusInt is >= 200 and < 300;

            // Response headers
            var hdrSb = new StringBuilder();
            foreach (var h in response.Headers)
                hdrSb.AppendLine($"{h.Key}: {string.Join(", ", h.Value)}");
            foreach (var h in response.Content.Headers)
                hdrSb.AppendLine($"{h.Key}: {string.Join(", ", h.Value)}");
            ResponseHeaders = hdrSb.ToString();

            // Response body
            var ct = response.Content.Headers.ContentType?.MediaType ?? "";
            if (ct.Contains("json"))
            {
                var raw = await response.Content.ReadAsStringAsync();
                try
                {
                    var doc = JsonDocument.Parse(raw);
                    ResponseBody = PrettyPrintJson(doc);
                }
                catch { ResponseBody = raw; }
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

            // Auto-switch to body tab
            SelectedResponseTab = 0;
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

    private static string PrettyPrintJson(JsonDocument doc)
    {
        using var ms = new System.IO.MemoryStream();
        using var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true });
        doc.WriteTo(writer);
        writer.Flush();
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    private string FormatProtobufResponse(byte[] bytes)
    {
        var sb = new StringBuilder();

        // Try to decode as PageResponse first
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
        catch { /* not a PageResponse */ }

        // Try DeltaResponse
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
        catch { /* not a DeltaResponse */ }

        // Fallback: hex dump
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
