using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace Wavee.Sdk.Http;

/// <summary>
/// Supplies a token on demand. <paramref name="force"/> is true when the previous token was rejected, so the provider
/// must bypass its cache and mint a fresh one.
/// </summary>
/// <param name="force">True = ignore any cached value.</param>
/// <param name="ct">Cancels the mint.</param>
public delegate ValueTask<string> TokenProvider(bool force, CancellationToken ct);

/// <summary>
/// Sends requests carrying <c>Authorization: Bearer &lt;token&gt;</c> and an optional <c>client-token</c> header, and
/// retries EXACTLY ONCE on <see cref="HttpStatusCode.Unauthorized"/> with both providers forced. Requests cannot be
/// resent, so callers hand over a factory that builds a fresh <see cref="HttpRequestMessage"/> per attempt.
/// </summary>
public sealed class AuthenticatedHttp
{
    /// <summary>The header name carrying the client token.</summary>
    public const string ClientTokenHeader = "client-token";

    readonly HttpClient _http;
    readonly TokenProvider _bearerToken;
    readonly TokenProvider? _clientToken;

    /// <summary>Wrap a client with the two token providers.</summary>
    /// <param name="http">The client every request goes through.</param>
    /// <param name="bearerToken">Mints the <c>Authorization: Bearer</c> value.</param>
    /// <param name="clientToken">Optional; mints the <c>client-token</c> header value.</param>
    public AuthenticatedHttp(HttpClient http, TokenProvider bearerToken, TokenProvider? clientToken = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _bearerToken = bearerToken ?? throw new ArgumentNullException(nameof(bearerToken));
        _clientToken = clientToken;
    }

    /// <summary>
    /// Build, authenticate and send a request. On a 401 the response is disposed, both tokens are re-minted with
    /// <c>force: true</c>, and the request is built again and sent once more; the second response is returned whatever
    /// its status.
    /// </summary>
    /// <param name="requestFactory">Builds a FRESH request per attempt (called at most twice).</param>
    /// <param name="completionOption">Passed through to <see cref="HttpClient.SendAsync(HttpRequestMessage, HttpCompletionOption, CancellationToken)"/>.</param>
    /// <param name="ct">Cancels the mint and the send.</param>
    public async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> requestFactory,
        HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requestFactory);

        var response = await SendOnceAsync(requestFactory, force: false, completionOption, ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized) return response;

        response.Dispose();
        return await SendOnceAsync(requestFactory, force: true, completionOption, ct).ConfigureAwait(false);
    }

    /// <summary>Authenticated GET (convenience over <see cref="SendAsync"/>).</summary>
    /// <param name="url">Absolute request url.</param>
    /// <param name="completionOption">Passed through to the send.</param>
    /// <param name="ct">Cancels the mint and the send.</param>
    public Task<HttpResponseMessage> GetAsync(string url,
        HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead, CancellationToken ct = default)
        => SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), completionOption, ct);

    async Task<HttpResponseMessage> SendOnceAsync(Func<HttpRequestMessage> requestFactory, bool force,
        HttpCompletionOption completionOption, CancellationToken ct)
    {
        var request = requestFactory() ?? throw new InvalidOperationException("the request factory returned null");
        try
        {
            var bearer = await _bearerToken(force, ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(bearer))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

            if (_clientToken is { } provider)
            {
                var clientToken = await provider(force, ct).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(clientToken))
                {
                    request.Headers.Remove(ClientTokenHeader);
                    request.Headers.TryAddWithoutValidation(ClientTokenHeader, clientToken);
                }
            }
        }
        catch
        {
            request.Dispose();
            throw;
        }

        using (request)
            return await _http.SendAsync(request, completionOption, ct).ConfigureAwait(false);
    }
}
