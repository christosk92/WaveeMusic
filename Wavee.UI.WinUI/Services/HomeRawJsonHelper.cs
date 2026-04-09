using System;
using System.Collections.Generic;
using System.Text.Json;
using Wavee.Core.Http.Pathfinder;

namespace Wavee.UI.WinUI.Services;

internal static class HomeRawJsonHelper
{
    public static List<string?> GetRawSectionJsonByIndex(HomeResponse response)
    {
        var result = new List<string?>();
        if (string.IsNullOrWhiteSpace(response.RawJson))
            return result;

        try
        {
            using var document = JsonDocument.Parse(response.RawJson);
            if (!document.RootElement.TryGetProperty("data", out var data)
                || !data.TryGetProperty("home", out var home)
                || !home.TryGetProperty("sectionContainer", out var sectionContainer)
                || !sectionContainer.TryGetProperty("sections", out var sections)
                || !sections.TryGetProperty("items", out var items)
                || items.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var section in items.EnumerateArray())
                result.Add(section.GetRawText());
        }
        catch (JsonException)
        {
            return result;
        }
        catch (InvalidOperationException)
        {
            return result;
        }

        return result;
    }
}
