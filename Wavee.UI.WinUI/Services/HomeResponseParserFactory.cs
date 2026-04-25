using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Wavee.Core.Http.Pathfinder;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.ViewModels;

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
                result = new HomeParseResult(
                    result.Greeting,
                    CombineBaselineSections(result.Sections),
                    result.Chips);
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

    private static List<HomeSection> CombineBaselineSections(List<HomeSection> sections)
    {
        var baselineSections = sections
            .Where(section => section.SectionType == HomeSectionType.Baseline)
            .ToList();

        foreach (var section in baselineSections)
        {
            foreach (var item in section.Items)
                item.BaselineGroupTitle = section.Title;
        }

        if (baselineSections.Count <= 1)
            return sections;

        var firstBaselineIndex = sections.FindIndex(section => section.SectionType == HomeSectionType.Baseline);
        var combined = new HomeSection
        {
            Title = "More like your music",
            SectionType = HomeSectionType.Baseline,
            SectionUri = "spotify:section:home-feed-baseline-group"
        };

        var seenUris = new HashSet<string>(StringComparer.Ordinal);
        foreach (var section in baselineSections)
        {
            foreach (var item in section.Items)
            {
                if (!string.IsNullOrWhiteSpace(item.Uri) && !seenUris.Add(item.Uri))
                    continue;

                combined.Items.Add(item);
            }
        }

        if (combined.Items.Count == 0)
            return sections.Where(section => section.SectionType != HomeSectionType.Baseline).ToList();

        // Visual-identity accent for the merged baseline grouping — pull from
        // the first item that carries an extracted dark color so the combined
        // shelf gets the same per-section tint treatment as Generic sections.
        combined.AccentColorHex = combined.Items
            .FirstOrDefault(i => !string.IsNullOrEmpty(i.ColorHex))?.ColorHex;

        var result = sections
            .Where(section => section.SectionType != HomeSectionType.Baseline)
            .ToList();

        result.Insert(Math.Clamp(firstBaselineIndex, 0, result.Count), combined);
        return result;
    }
}
