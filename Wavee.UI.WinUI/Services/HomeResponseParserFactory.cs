using System;
using System.Collections.Generic;
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

    public HomeResponseParserFactory(IEnumerable<IHomeResponseParser> parsers)
    {
        _parsers = new List<IHomeResponseParser>(parsers);
    }

    public HomeResponseParserFactory()
    {
        _parsers =
        [
            new HomeResponseParserV2(), // entity/trait format (2025+)
            new HomeResponseParserV1()  // content-based format (legacy)
        ];
    }

    public HomeParseResult Parse(HomeResponse response)
    {
        foreach (var parser in _parsers)
        {
            if (parser.CanParse(response))
                return parser.Parse(response);
        }

        // No parser matched — return empty result
        return new HomeParseResult(
            response.Data?.Home?.Greeting?.TransformedLabel,
            [],
            []);
    }
}
