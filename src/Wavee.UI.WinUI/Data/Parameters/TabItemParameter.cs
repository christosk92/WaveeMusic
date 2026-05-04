using System;
using System.IO;
using System.Text.Json;
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
        using var ms = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms);
        writer.WriteStartObject();
        writer.WriteString("InitialPageType", InitialPageType?.AssemblyQualifiedName);
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

            var typeName = root.GetProperty("InitialPageType").GetString();
            var pageType = typeName != null ? Type.GetType(typeName) : null;

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
