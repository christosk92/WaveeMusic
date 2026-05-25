using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.AI.Tools;

namespace Wavee.UI.WinUI.Services.Ai;

public sealed class DisabledWebSearchToolProvider : IWebSearchToolProvider
{
    public bool IsAvailable => false;

    public Task<IReadOnlyList<WebSearchResult>> SearchAsync(
        string query,
        WebSearchOptions? options = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<WebSearchResult>>([]);
}
