using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Wavee.AI.Tools;

public sealed record AiToolDefinition(
    string Name,
    string Description,
    IReadOnlyList<string> ArgumentNames);

public sealed record AiToolCall(
    string Name,
    IReadOnlyDictionary<string, string> Arguments);

public sealed record AiToolResult(
    string Name,
    bool Succeeded,
    string Summary,
    object? Payload = null,
    string? ErrorMessage = null);

public interface IAiTool
{
    AiToolDefinition Definition { get; }

    Task<AiToolResult> InvokeAsync(
        IReadOnlyDictionary<string, string> arguments,
        CancellationToken cancellationToken = default);
}

public interface IAiToolRegistry
{
    IReadOnlyList<IAiTool> Tools { get; }

    bool TryGetTool(string name, out IAiTool tool);
}

public sealed class AiToolRegistry : IAiToolRegistry
{
    private readonly Dictionary<string, IAiTool> _tools;

    public AiToolRegistry(IEnumerable<IAiTool> tools)
    {
        _tools = new Dictionary<string, IAiTool>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in tools)
            _tools[tool.Definition.Name] = tool;
    }

    public IReadOnlyList<IAiTool> Tools => _tools.Values.ToArray();

    public bool TryGetTool(string name, out IAiTool tool)
        => _tools.TryGetValue(name, out tool!);
}

public sealed record WebSearchOptions(
    int MaxResults = 5,
    string? Locale = null,
    string? Recency = null);

public sealed record WebSearchResult(
    string Title,
    string Url,
    string Snippet,
    DateTimeOffset? PublishedAt = null,
    string? Source = null);

public interface IWebSearchToolProvider
{
    bool IsAvailable { get; }

    Task<IReadOnlyList<WebSearchResult>> SearchAsync(
        string query,
        WebSearchOptions? options = null,
        CancellationToken cancellationToken = default);
}

public sealed record WikipediaSummary(
    string Title,
    string Extract,
    string? Url = null,
    string? Lang = null,
    string? Description = null);

public interface IWikipediaLookup
{
    Task<WikipediaSummary?> LookupArtistAsync(
        string artistName,
        CancellationToken cancellationToken = default);

    Task<WikipediaSummary?> LookupAlbumAsync(
        string albumTitle,
        string? artistName,
        CancellationToken cancellationToken = default);
}
