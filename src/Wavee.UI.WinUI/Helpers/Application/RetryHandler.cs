using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Wavee.UI.WinUI.Helpers.Application;

/// <summary>
/// Global HTTP delegating handler that retries requests on 429 (Too Many Requests)
/// and 503 (Service Unavailable) with exponential backoff. Respects Retry-After header.
/// </summary>
internal sealed class RetryHandler : DelegatingHandler
{
    private const int MaxRetries = 3;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage? response = null;

        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            var requestToSend = attempt == 0 ? request : CloneRequest(request);
            response = await base.SendAsync(requestToSend, cancellationToken);

            if (response.StatusCode is not (HttpStatusCode.TooManyRequests
                or HttpStatusCode.ServiceUnavailable))
                return response;

            if (attempt == MaxRetries)
                return response;

            // Respect Retry-After header if present, else exponential backoff
            var delay = response.Headers.RetryAfter?.Delta
                ?? TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));

            response.Dispose();
            await Task.Delay(delay, cancellationToken);
        }

        return response!;
    }

    private static HttpRequestMessage CloneRequest(HttpRequestMessage original)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri);
        clone.Version = original.Version;
        if (original.Content != null)
            clone.Content = original.Content;
        foreach (var header in original.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return clone;
    }
}
