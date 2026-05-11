using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Wavee.UI.WinUI.Controls.Settings;

public interface ISettingsSearchFilter
{
    void ApplySearchFilter(string? groupKey);
}

internal static class SettingsGroupFilter
{
    private static readonly Dictionary<FrameworkElement, Visibility> OriginalVisibilities = new();

    public static void Apply(Panel root, string? groupKey)
    {
        var showAll = string.IsNullOrWhiteSpace(groupKey);

        foreach (var child in root.Children)
        {
            if (child is not FrameworkElement element)
                continue;

            OriginalVisibilities.TryAdd(element, element.Visibility);

            if (showAll)
            {
                element.Visibility = OriginalVisibilities[element];
                continue;
            }

            element.Visibility = Matches(element.Tag, groupKey)
                ? OriginalVisibilities[element]
                : Visibility.Collapsed;
        }
    }

    private static bool Matches(object? tag, string groupKey)
    {
        if (tag is not string tags || string.IsNullOrWhiteSpace(tags))
            return false;

        return tags.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(tagPart => string.Equals(tagPart, groupKey, StringComparison.OrdinalIgnoreCase));
    }
}
