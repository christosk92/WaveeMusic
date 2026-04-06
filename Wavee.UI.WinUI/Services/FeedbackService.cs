using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wavee.Core.Feedback;
using Wavee.UI.WinUI.Data.Contracts;

namespace Wavee.UI.WinUI.Services;

public sealed class FeedbackService : IFeedbackService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger? _logger;

    public FeedbackService(HttpClient httpClient, ILogger<FeedbackService>? logger = null)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<FeedbackSubmitResponse?> SubmitAsync(FeedbackSubmitRequest request, CancellationToken ct = default)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                "/api/feedback",
                request,
                FeedbackJsonContext.Default.FeedbackSubmitRequest,
                ct);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync(
                    FeedbackJsonContext.Default.FeedbackSubmitResponse, ct);
                _logger?.LogInformation("Feedback submitted: {Id}", result?.Id);
                return result;
            }

            _logger?.LogWarning("Feedback submission failed: {Status}", response.StatusCode);
            return null;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to submit feedback");
            return null;
        }
    }
}
