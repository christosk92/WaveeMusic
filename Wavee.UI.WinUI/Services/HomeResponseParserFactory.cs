using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Wavee.Core.Http.Pathfinder;
using Wavee.UI.WinUI.Data.Contracts;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Selects the appropriate parser for the home feed response format.
/// Parsers are tried in priority order (newest first).
/// </summary>
public sealed class HomeResponseParserFactory
{
    private readonly List<IHomeResponseParser> _parsers;
    private readonly ILogger? _logger;

    public HomeResponseParserFactory(ILogger<HomeResponseParserFactory>? logger = null)
    {
        _logger = logger;
        _parsers =
        [
            new HomeResponseParserV2(),
            new HomeResponseParserV1()
        ];
    }

    public HomeParseResult Parse(HomeResponse response)
    {
        // Quick diagnostics
        var sectionCount = response.Data?.Home?.SectionContainer?.Sections?.Items?.Count ?? -1;
        _logger?.LogDebug("HomeParserFactory: response has {SectionCount} sections", sectionCount);

        foreach (var parser in _parsers)
        {
            var name = parser.GetType().Name;
            try
            {
                var canParse = parser.CanParse(response);
                _logger?.LogDebug("HomeParserFactory: {Parser}.CanParse = {Result}", name, canParse);

                if (!canParse) continue;

                var result = parser.Parse(response);
                _logger?.LogDebug("HomeParserFactory: {Parser}.Parse returned {Sections} sections, {Items} total items",
                    name, result.Sections.Count,
                    result.Sections.Sum(s => s.Items.Count));
                return result;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "HomeParserFactory: {Parser} threw an exception", name);
            }
        }

        _logger?.LogWarning("HomeParserFactory: no parser matched — returning empty");
        return new HomeParseResult(
            response.Data?.Home?.Greeting?.TransformedLabel,
            [],
            []);
    }
}
