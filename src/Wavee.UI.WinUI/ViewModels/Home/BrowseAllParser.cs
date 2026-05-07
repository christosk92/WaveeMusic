using System.Collections.Generic;
using System.Text.Json;
using Wavee.Core.Http.Pathfinder;

namespace Wavee.UI.WinUI.ViewModels.Home;

/// <summary>
/// Walks a <see cref="BrowseAllResponse"/> into a flat list of
/// <see cref="BrowseAllItem"/>s. Item-level data lives under different
/// schemas depending on the wrapper typename:
/// <list type="bullet">
///   <item><c>BrowseSectionContainerWrapper</c> →
///         <c>data.data.cardRepresentation.{title.transformedLabel, backgroundColor.hex}</c></item>
///   <item><c>BrowseXlinkResponseWrapper</c> →
///         <c>data.{title.transformedLabel, backgroundColor.hex}</c></item>
/// </list>
/// We probe both — same code path either way — so adding a new wrapper kind
/// later that reuses one of these shapes works without changes here.
/// </summary>
internal static class BrowseAllParser
{
    public static IList<BrowseAllItem> Extract(BrowseAllResponse? response)
    {
        var items = new List<BrowseAllItem>();
        var sectionItems = response?.Data?.BrowseStart?.Sections?.Items;
        if (sectionItems == null) return items;

        foreach (var section in sectionItems)
        {
            var entries = section.SectionItems?.Items;
            if (entries == null) continue;
            foreach (var entry in entries)
            {
                if (TryExtract(entry, out var item))
                    items.Add(item);
            }
        }
        return items;
    }

    private static bool TryExtract(BrowseAllItemEntry entry, out BrowseAllItem item)
    {
        item = null!;
        var uri = entry.Uri;
        if (string.IsNullOrEmpty(uri)) return false;
        if (entry.Content?.Data is not JsonElement data) return false;

        // Walk to the card representation. BrowseSectionContainerWrapper buries
        // it one level deeper (data.cardRepresentation); BrowseXlinkResponseWrapper
        // exposes title/backgroundColor at the wrapper root.
        var card = data;
        if (data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("data", out var nested)
            && nested.ValueKind == JsonValueKind.Object
            && nested.TryGetProperty("cardRepresentation", out var rep))
        {
            card = rep;
        }

        if (card.ValueKind != JsonValueKind.Object) return false;
        if (!card.TryGetProperty("title", out var titleEl)) return false;
        if (titleEl.ValueKind != JsonValueKind.Object) return false;
        if (!titleEl.TryGetProperty("transformedLabel", out var labelEl)) return false;
        var label = labelEl.GetString();
        if (string.IsNullOrEmpty(label)) return false;

        var hex = "";
        if (card.TryGetProperty("backgroundColor", out var bgEl)
            && bgEl.ValueKind == JsonValueKind.Object
            && bgEl.TryGetProperty("hex", out var hexEl)
            && hexEl.ValueKind == JsonValueKind.String)
        {
            hex = hexEl.GetString() ?? "";
        }

        item = new BrowseAllItem
        {
            Label = label!,
            AccentHex = hex,
            Uri = uri!
        };
        return true;
    }
}
