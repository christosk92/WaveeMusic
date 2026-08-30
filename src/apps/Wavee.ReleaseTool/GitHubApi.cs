using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Wavee.ReleaseTool;

/// <summary>How a single GitHub call ended. Only <see cref="Missing"/> is an authoring mistake.</summary>
enum GitHubOutcome
{
    /// <summary>200 — the payload is present.</summary>
    Ok,
    /// <summary>404 — the issue/PR number does not exist in the repo. An error: the changelog references nothing.</summary>
    Missing,
    /// <summary>403/429 — rate limited or forbidden. A warning: keep the authored snapshot.</summary>
    Throttled,
    /// <summary>Anything else, including a transport failure. A warning: keep the authored snapshot.</summary>
    Failed,
}

/// <summary>One issue/PR lookup.</summary>
readonly record struct GitHubIssueResult(GitHubOutcome Outcome, GitHubIssue? Issue, string Detail);

/// <summary>
/// The two GitHub endpoints this tool needs, over a bare <see cref="HttpClient"/>: get-an-issue (which also serves
/// PRs) and generate-release-notes. No token is required for the first (60 requests/hour per IP); the second is
/// skipped entirely when no token is available.
/// </summary>
sealed class GitHubApi : IDisposable
{
    const string ApiRoot = "https://api.github.com";
    readonly HttpClient _http;
    readonly bool _hasToken;

    public GitHubApi(string? token, string toolVersion)
    {
        _hasToken = !string.IsNullOrWhiteSpace(token);
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Wavee.ReleaseTool/" + (toolVersion.Length > 0 ? toolVersion : "0"));
        if (_hasToken)
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    /// <summary>True when an <c>Authorization</c> header is in play — generate-notes needs one.</summary>
    public bool Authenticated => _hasToken;

    /// <summary><c>GET /repos/{repo}/issues/{number}</c>. Never throws.</summary>
    public async Task<GitHubIssueResult> GetIssueAsync(string repo, int number, CancellationToken ct)
    {
        string url = $"{ApiRoot}/repos/{repo}/issues/{number}";
        try
        {
            using var res = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (res.StatusCode == HttpStatusCode.NotFound)
                return new GitHubIssueResult(GitHubOutcome.Missing, null, "404 Not Found");
            if (res.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                return new GitHubIssueResult(GitHubOutcome.Throttled, null, (int)res.StatusCode + " " + res.ReasonPhrase);
            if (!res.IsSuccessStatusCode)
                return new GitHubIssueResult(GitHubOutcome.Failed, null, (int)res.StatusCode + " " + res.ReasonPhrase);

            byte[] body = await res.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            var issue = JsonSerializer.Deserialize(body, GitHubJsonContext.Default.GitHubIssue);
            return issue is null
                ? new GitHubIssueResult(GitHubOutcome.Failed, null, "empty response body")
                : new GitHubIssueResult(GitHubOutcome.Ok, issue, "");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new GitHubIssueResult(GitHubOutcome.Failed, null, ex.Message);
        }
    }

    /// <summary>
    /// <c>POST /repos/{repo}/releases/generate-notes</c> — the commits/contributors appendix. Requires a token;
    /// returns null (silently) when there is none, or when the call fails.
    /// </summary>
    public async Task<string?> GenerateNotesAsync(string repo, string tag, string? previousTag, CancellationToken ct)
    {
        if (!_hasToken) return null;
        string url = $"{ApiRoot}/repos/{repo}/releases/generate-notes";
        try
        {
            var payload = new GenerateNotesRequest { TagName = tag, PreviousTagName = previousTag };
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(payload, GitHubJsonContext.Default.GenerateNotesRequest);
            using var content = new ByteArrayContent(json);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            using var res = await _http.PostAsync(url, content, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return null;
            byte[] body = await res.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            var parsed = JsonSerializer.Deserialize(body, GitHubJsonContext.Default.GenerateNotesResponse);
            return string.IsNullOrWhiteSpace(parsed?.Body) ? null : parsed!.Body;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return null; }
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>The subset of <c>GET /repos/{o}/{r}/issues/{n}</c> the snapshot needs.</summary>
sealed class GitHubIssue
{
    [JsonPropertyName("number")] public int Number { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("state")] public string? State { get; set; }
    [JsonPropertyName("state_reason")] public string? StateReason { get; set; }
    /// <summary>Present (non-null) only when the number is a pull request.</summary>
    [JsonPropertyName("pull_request")] public GitHubPullRequestRef? PullRequest { get; set; }
    [JsonPropertyName("user")] public GitHubUser? User { get; set; }

    /// <summary>GitHub returns PRs from the issues endpoint; the <c>pull_request</c> member is the discriminator.</summary>
    public bool IsPullRequest => PullRequest is not null;
    /// <summary>A PR that landed carries a <c>merged_at</c> timestamp.</summary>
    public bool Merged => !string.IsNullOrEmpty(PullRequest?.MergedAt);
}

sealed class GitHubPullRequestRef
{
    [JsonPropertyName("merged_at")] public string? MergedAt { get; set; }
}

sealed class GitHubUser
{
    [JsonPropertyName("login")] public string? Login { get; set; }
}

sealed class GenerateNotesRequest
{
    [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
    [JsonPropertyName("previous_tag_name")] public string? PreviousTagName { get; set; }
}

sealed class GenerateNotesResponse
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("body")] public string? Body { get; set; }
}

/// <summary>
/// The tool's own source-gen context. It stays here, not in Wavee.Core: the app never speaks to the GitHub
/// REST API with these shapes, and Wavee.Core's context owns only the release-notes documents.
/// </summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(GitHubIssue))]
[JsonSerializable(typeof(GenerateNotesRequest))]
[JsonSerializable(typeof(GenerateNotesResponse))]
internal partial class GitHubJsonContext : JsonSerializerContext
{
}
