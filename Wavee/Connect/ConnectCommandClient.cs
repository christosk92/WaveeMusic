using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reactive.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Wavee.Core.Session;

namespace Wavee.Connect;

/// <summary>
/// Generic, reusable client for sending Spotify Connect State commands.
/// Protocol: POST connect-state/v1/player/command/from/{from}/to/{to}
/// with ack-based confirmation via dealer cluster updates.
/// </summary>
/// <remarks>
/// This client knows nothing about specific command semantics (play, pause, etc.).
/// It sends any command endpoint with optional data and waits for ack confirmation.
/// Reusable for playback, volume, transfer, queue, and future command types.
/// </remarks>
public sealed class ConnectCommandClient : IAsyncDisposable
{
    private static readonly TimeSpan DefaultAckTimeout = TimeSpan.FromSeconds(10);

    private readonly Session _session;
    private readonly HttpClient _httpClient;
    private readonly ILogger? _logger;

    // Pending ack_ids waiting for confirmation
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pendingAcks = new();
    private IDisposable? _clusterSubscription;
    private bool _subscribed;
    private bool _disposed;

    /// <summary>
    /// Creates a new ConnectCommandClient. DealerClient subscription is deferred
    /// until first use (Session.Dealer may be null during DI construction).
    /// </summary>
    public ConnectCommandClient(
        Session session,
        HttpClient httpClient,
        ILogger? logger = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger;
    }

    private void EnsureSubscribed()
    {
        if (_subscribed) return;
        var dealer = _session.Dealer;
        if (dealer == null) return;

        _clusterSubscription = dealer.Messages
            .Where(m => m.Uri.StartsWith("hm://connect-state/", StringComparison.OrdinalIgnoreCase))
            .Subscribe(OnClusterUpdate);
        _subscribed = true;
        _logger?.LogDebug("ConnectCommandClient subscribed to dealer cluster updates");
    }

    /// <summary>
    /// Sends a command to a target device and waits for ack confirmation.
    /// </summary>
    /// <param name="targetDeviceId">Device ID to send the command to.</param>
    /// <param name="endpoint">Command endpoint (e.g. "pause", "resume", "play", "seek_to").</param>
    /// <param name="commandData">Additional command data merged into the command object. May be null for simple commands.</param>
    /// <param name="ackTimeout">How long to wait for ack. Defaults to 10 seconds.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<ConnectCommandResult> SendCommandAsync(
        string targetDeviceId,
        string endpoint,
        Dictionary<string, object>? commandData = null,
        TimeSpan? ackTimeout = null,
        CancellationToken ct = default)
    {
        EnsureSubscribed();
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDeviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);

        var timeout = ackTimeout ?? DefaultAckTimeout;

        try
        {
            var accessToken = await _session.GetAccessTokenAsync(ct);
            var baseUrl = _session.SpClient.BaseUrl;
            var url = $"{baseUrl}/connect-state/v1/player/command/from/{_session.Config.DeviceId}/to/{targetDeviceId}";

            // Build command body
            var commandId = Guid.NewGuid().ToString("N");
            var command = new Dictionary<string, object>
            {
                ["endpoint"] = endpoint,
                ["logging_params"] = new Dictionary<string, object>
                {
                    ["page_instance_ids"] = Array.Empty<string>(),
                    ["interaction_ids"] = Array.Empty<string>(),
                    ["command_id"] = commandId
                }
            };

            // Merge additional command data
            if (commandData != null)
            {
                foreach (var (key, value) in commandData)
                {
                    if (key != "endpoint") // Don't overwrite endpoint
                        command[key] = value;
                }
            }

            var body = new Dictionary<string, object> { ["command"] = command };
            var json = SerializeDynamic(body);

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
            request.Content = new StringContent(json, Encoding.UTF8, new System.Net.Http.Headers.MediaTypeHeaderValue("application/json"));

            _logger?.LogDebug("Connect command: {Endpoint} → from/{From}/to/{To}",
                endpoint, _session.Config.DeviceId, targetDeviceId);

            using var response = await _httpClient.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                var statusMessage = $"HTTP {(int)response.StatusCode} {response.StatusCode}";
                _logger?.LogWarning("Connect command {Endpoint} failed: {Status}", endpoint, statusMessage);
                return ConnectCommandResult.Failure(statusMessage, response.StatusCode);
            }

            // Parse ack_id
            var responseJson = await response.Content.ReadAsStringAsync(ct);
            string? ackId = null;
            try
            {
                var ackResponse = JsonSerializer.Deserialize(responseJson, ConnectAckJsonContext.Default.ConnectAckResponse);
                ackId = ackResponse?.AckId;
            }
            catch { /* Response may not be JSON — that's ok */ }

            if (string.IsNullOrEmpty(ackId))
            {
                _logger?.LogDebug("Connect command {Endpoint} accepted (no ack_id)", endpoint);
                return ConnectCommandResult.Success(null);
            }

            // Wait for ack confirmation from dealer
            _logger?.LogDebug("Waiting for ack: {AckId} (timeout: {Timeout}s)", ackId, timeout.TotalSeconds);
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingAcks[ackId] = tcs;

            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(timeout);

                await tcs.Task.WaitAsync(timeoutCts.Token);
                _logger?.LogInformation("Connect command {Endpoint} confirmed (ack: {AckId})", endpoint, ackId);
                return ConnectCommandResult.Success(ackId);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger?.LogWarning("Connect command {Endpoint} timed out (ack: {AckId})", endpoint, ackId);
                return ConnectCommandResult.Timeout(ackId, timeout);
            }
            finally
            {
                _pendingAcks.TryRemove(ackId, out _);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return ConnectCommandResult.Failure("Cancelled.", null);
        }
        catch (HttpRequestException ex)
        {
            _logger?.LogWarning(ex, "Connect command {Endpoint} network error", endpoint);
            return ConnectCommandResult.Failure($"Network error: {ex.Message}", null);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Unexpected error in connect command: {Endpoint}", endpoint);
            return ConnectCommandResult.Failure($"Unexpected error: {ex.Message}", null);
        }
    }

    /// <summary>
    /// AOT-safe serialization of nested Dictionary/Array/primitive structures.
    /// </summary>
    private static string SerializeDynamic(object value)
    {
        using var ms = new System.IO.MemoryStream();
        using var writer = new Utf8JsonWriter(ms);
        WriteDynamicValue(writer, value);
        writer.Flush();
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteDynamicValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string s:
                writer.WriteStringValue(s);
                break;
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case int i:
                writer.WriteNumberValue(i);
                break;
            case long l:
                writer.WriteNumberValue(l);
                break;
            case double d:
                writer.WriteNumberValue(d);
                break;
            case Dictionary<string, object> dict:
                writer.WriteStartObject();
                foreach (var (k, v) in dict)
                {
                    writer.WritePropertyName(k);
                    WriteDynamicValue(writer, v);
                }
                writer.WriteEndObject();
                break;
            case Dictionary<string, string> strDict:
                writer.WriteStartObject();
                foreach (var (k, v) in strDict)
                {
                    writer.WritePropertyName(k);
                    writer.WriteStringValue(v);
                }
                writer.WriteEndObject();
                break;
            case System.Collections.IEnumerable enumerable:
                writer.WriteStartArray();
                foreach (var item in enumerable)
                    WriteDynamicValue(writer, item);
                writer.WriteEndArray();
                break;
            default:
                writer.WriteStringValue(value.ToString());
                break;
        }
    }

    private void OnClusterUpdate(Protocol.DealerMessage _)
    {
        // Any cluster update confirms all pending commands
        foreach (var (ackId, tcs) in _pendingAcks)
        {
            if (tcs.TrySetResult(true))
                _logger?.LogTrace("Ack confirmed by cluster update: {AckId}", ackId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _clusterSubscription.Dispose();
        foreach (var (_, tcs) in _pendingAcks)
            tcs.TrySetCanceled();
        _pendingAcks.Clear();
    }
}

/// <summary>
/// Result of a Connect State command.
/// </summary>
public sealed record ConnectCommandResult
{
    public bool IsSuccess { get; init; }
    public bool IsTimeout { get; init; }
    public string? AckId { get; init; }
    public string? ErrorMessage { get; init; }
    public HttpStatusCode? HttpStatus { get; init; }

    public static ConnectCommandResult Success(string? ackId) =>
        new() { IsSuccess = true, AckId = ackId };

    public static ConnectCommandResult Failure(string message, HttpStatusCode? status) =>
        new() { IsSuccess = false, ErrorMessage = message, HttpStatus = status };

    public static ConnectCommandResult Timeout(string ackId, TimeSpan timeout) =>
        new() { IsSuccess = false, IsTimeout = true, AckId = ackId,
            ErrorMessage = $"Command not confirmed within {timeout.TotalSeconds}s." };
}

// ── JSON ──

internal sealed record ConnectAckResponse
{
    [JsonPropertyName("ack_id")]
    public string? AckId { get; init; }
}

[JsonSerializable(typeof(ConnectAckResponse))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class ConnectAckJsonContext : JsonSerializerContext { }
