using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Wavee.Tests.Modules;

/// <summary>One request a module made, snapshotted before <see cref="HttpClient"/> disposed it.</summary>
/// <param name="Method">The verb.</param>
/// <param name="Url">The absolute url, query included.</param>
/// <param name="Headers">Every request header, lower-cased keys, first value only.</param>
/// <param name="Body">The request body as text (empty for a body-less request).</param>
public sealed record RecordedRequest(string Method, string Url, IReadOnlyDictionary<string, string> Headers, string Body)
{
    /// <summary>Reads one header, or null when it was not sent.</summary>
    /// <param name="name">Header name (case-insensitive).</param>
    public string? Header(string name) => Headers.TryGetValue(name.ToLowerInvariant(), out string? v) ? v : null;
}

/// <summary>
/// The test transport every module test runs on: a list of (predicate → reply) rules over an in-memory handler.
/// Nothing here touches the network, and every request the module made is kept for assertions.
/// </summary>
public sealed class ScriptedHttpHandler : HttpMessageHandler
{
    private readonly List<(Func<RecordedRequest, bool> Match, Func<RecordedRequest, HttpResponseMessage> Reply)> _rules = [];

    /// <summary>Every request the module sent, in order.</summary>
    public List<RecordedRequest> Requests { get; } = [];

    /// <summary>Adds a rule. The first matching rule wins.</summary>
    /// <param name="match">Predicate over the recorded request.</param>
    /// <param name="reply">Builds the response.</param>
    public ScriptedHttpHandler On(Func<RecordedRequest, bool> match, Func<RecordedRequest, HttpResponseMessage> reply)
    {
        _rules.Add((match, reply));
        return this;
    }

    /// <summary>Adds a rule keyed on a url substring.</summary>
    /// <param name="urlContains">Substring the url must contain.</param>
    /// <param name="status">Status code to answer with.</param>
    /// <param name="body">Response body.</param>
    /// <param name="contentType">Content type, or null to send none.</param>
    /// <param name="headers">Extra response headers.</param>
    public ScriptedHttpHandler OnUrl(string urlContains, HttpStatusCode status, string body,
        string? contentType = null, params (string Name, string Value)[] headers)
        => On(r => r.Url.Contains(urlContains, StringComparison.Ordinal),
            _ => Respond(status, body, contentType, headers));

    /// <summary>Adds a rule keyed on a substring of the REQUEST body (how the YouTube client table is scripted).</summary>
    /// <param name="bodyContains">Substring the request body must contain.</param>
    /// <param name="status">Status code to answer with.</param>
    /// <param name="body">Response body.</param>
    /// <param name="contentType">Content type, or null to send none.</param>
    public ScriptedHttpHandler OnBody(string bodyContains, HttpStatusCode status, string body,
        string? contentType = "application/json")
        => On(r => r.Body.Contains(bodyContains, StringComparison.Ordinal),
            _ => Respond(status, body, contentType));

    /// <summary>Builds a response with an optional content type and extra headers.</summary>
    /// <param name="status">Status code.</param>
    /// <param name="body">Body text.</param>
    /// <param name="contentType">Content type, or null to strip it entirely.</param>
    /// <param name="headers">Extra headers; unknown names land on the response, known ones on the content.</param>
    /// <param name="contentLength">Explicit <c>Content-Length</c>, or null to leave what the body implies.</param>
    public static HttpResponseMessage Respond(HttpStatusCode status, string body, string? contentType = null,
        (string Name, string Value)[]? headers = null, long? contentLength = null)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)),
        };

        response.Content.Headers.ContentType = contentType is null ? null : MediaTypeHeaderValue.Parse(contentType);
        if (contentLength is { } length) response.Content.Headers.ContentLength = length;

        foreach ((string name, string value) in headers ?? [])
        {
            if (!response.Headers.TryAddWithoutValidation(name, value))
            {
                response.Content.Headers.TryAddWithoutValidation(name, value);
            }
        }

        return response;
    }

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        string body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        // NonValidated keeps the value exactly as TryAddWithoutValidation stored it; the validated enumerator
        // re-parses (a User-Agent comes back as separate products, which then re-joins wrong).
        Dictionary<string, string> headers = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, HeaderStringValues> h in request.Headers.NonValidated)
        {
            headers[h.Key.ToLowerInvariant()] = h.Value.ToString();
        }

        if (request.Content is not null)
        {
            foreach (KeyValuePair<string, HeaderStringValues> h in request.Content.Headers.NonValidated)
            {
                headers[h.Key.ToLowerInvariant()] = h.Value.ToString();
            }
        }

        var recorded = new RecordedRequest(request.Method.Method, request.RequestUri!.ToString(), headers, body);
        Requests.Add(recorded);

        foreach ((Func<RecordedRequest, bool> match, Func<RecordedRequest, HttpResponseMessage> reply) in _rules)
        {
            if (match(recorded)) return reply(recorded);
        }

        throw new InvalidOperationException($"No scripted reply for {recorded.Method} {recorded.Url}");
    }
}
