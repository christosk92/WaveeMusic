using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.AI.Tools;

namespace Wavee.UI.WinUI.Services.Ai;

/// <summary>
/// Default <see cref="IWebSearchToolProvider"/> registered with DI. Routes to
/// the user-configured JSON endpoint when available (better quality, costs the
/// user a key), otherwise falls back to the DuckDuckGo lite scrape so the
/// always-on grounding works without configuration.
/// </summary>
public sealed partial class CompositeWebSearchToolProvider : IWebSearchToolProvider
{
    private readonly ConfigurableWebSearchToolProvider _configurable;
    private readonly DuckDuckGoLiteWebSearchProvider _fallback;

    internal CompositeWebSearchToolProvider(
        ConfigurableWebSearchToolProvider configurable,
        DuckDuckGoLiteWebSearchProvider fallback)
    {
        _configurable = configurable;
        _fallback = fallback;
    }

    public bool IsAvailable => true;

    public Task<IReadOnlyList<WebSearchResult>> SearchAsync(
        string query,
        WebSearchOptions? options = null,
        CancellationToken cancellationToken = default)
        => _configurable.IsAvailable
            ? _configurable.SearchAsync(query, options, cancellationToken)
            : _fallback.SearchAsync(query, options, cancellationToken);
}
