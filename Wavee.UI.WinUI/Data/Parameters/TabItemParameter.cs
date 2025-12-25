using System;
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
        return JsonSerializer.Serialize(new
        {
            InitialPageType = InitialPageType?.AssemblyQualifiedName,
            NavigationParameter
        });
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
