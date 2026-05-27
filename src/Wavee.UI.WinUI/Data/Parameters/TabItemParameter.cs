using System;
using System.IO;
using System.Text.Json;
using Wavee.UI.WinUI.Controls.PageHost;
using Wavee.UI.WinUI.Data.Enums;

namespace Wavee.UI.WinUI.Data.Parameters;

public sealed class TabItemParameter
{
    public Type? InitialPageType { get; set; }
    public object? NavigationParameter { get; set; }
    public string? Title { get; set; }
    public NavigationPageType PageType { get; set; }

    public TabItemParameter()
    {
    }

    public TabItemParameter(NavigationPageType pageType, object? parameter)
    {
        PageType = pageType;
        NavigationParameter = parameter;
    }

    public string Serialize()
    {
        // Persist by stable string key, not AssemblyQualifiedName. AOT/trim do
        // not guarantee type name stability across builds, so the prior
        // Type.GetType(qualifiedName) round-trip was an IL2057 site. The key
        // is sourced from PageTypeRegistry (registered alongside PageRegistry
        // during startup) and is the page's nameof literal.
        using var ms = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms);
        writer.WriteStartObject();

        string? key = null;
        if (InitialPageType is not null && PageTypeRegistry.TryGetKey(InitialPageType, out var k))
            key = k;
        writer.WriteString("InitialPageKey", key);
        writer.WriteString("NavigationParameter", NavigationParameter?.ToString());
        writer.WriteEndObject();
        writer.Flush();
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    public static TabItemParameter? Deserialize(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Type? pageType = null;
            if (root.TryGetProperty("InitialPageKey", out var keyEl)
                && keyEl.ValueKind == JsonValueKind.String
                && keyEl.GetString() is { Length: > 0 } key
                && PageTypeRegistry.TryGetType(key, out var resolved))
            {
                pageType = resolved;
            }

            return new TabItemParameter
            {
                InitialPageType = pageType,
                NavigationParameter = root.TryGetProperty("NavigationParameter", out var navParam)
                    ? navParam.GetString()
                    : null
            };
        }
        catch
        {
            return null;
        }
    }
}
