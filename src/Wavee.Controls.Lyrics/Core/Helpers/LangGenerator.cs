using System;
using System.Collections.Generic;
using BetterLyrics.Core.Abstractions;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;

// Dev-time localization-string scaffolding tool from the upstream BetterLyrics
// surface. Walks plugin config types via reflection, emits and syncs
// per-language JSON files. Has no in-repo callers and is not on any runtime
// path — kept against the upstream surface. All public entry points are
// annotated AOT-unsafe so the analyzer leaves them alone; trying to call
// them from AOT-published code will surface a propagating diagnostic at the
// call site.
public static class LangGenerator
{
    [RequiresUnreferencedCode("Reflects over arbitrary config types via Type.GetProperties.")]
    [RequiresDynamicCode("Reflects over arbitrary config types via Type.GetProperties.")]
    public static void Run(Type configType, string langsDir, IEnumerable<string> targetLanguages, string defaultLang = "en-US")
    {
        Directory.CreateDirectory(langsDir);
        var codeMap = ExtractFromCode(configType);
        foreach (var lang in targetLanguages)
        {
            string filePath = Path.Combine(langsDir, $"{lang}.json");

            if (lang.Equals(defaultLang, StringComparison.OrdinalIgnoreCase))
            {
                SaveJson(codeMap, filePath);
            }
            else
            {
                SyncTargetLanguage(codeMap, filePath);
            }
        }
    }

    [RequiresUnreferencedCode("Reflects over the TConfig type via Type.GetProperties.")]
    [RequiresDynamicCode("Reflects over the TConfig type via Type.GetProperties.")]
    public static void Run<TConfig>(string langsDir, IEnumerable<string> targetLanguages, string defaultLang = "en-US")
        where TConfig : PluginConfigBase
    {
        Run(typeof(TConfig), langsDir, targetLanguages, defaultLang);
    }

    [RequiresUnreferencedCode("Reads and rewrites a free-form Dictionary<string,string> via reflection-based JSON.")]
    [RequiresDynamicCode("Reads and rewrites a free-form Dictionary<string,string> via reflection-based JSON.")]
    private static void SyncTargetLanguage(SortedDictionary<string, string> codeMap, string filePath)
    {
        var currentMap = ReadJson(filePath);
        var newMap = new SortedDictionary<string, string>();

        foreach (var kvp in codeMap)
        {
            string key = kvp.Key;
            string defaultVal = kvp.Value;

            if (currentMap.TryGetValue(key, out var existingVal) && !string.IsNullOrWhiteSpace(existingVal))
            {
                newMap[key] = existingVal;
            }
            else
            {
                newMap[key] = defaultVal;
            }
        }

        SaveJson(newMap, filePath);
    }

    [RequiresUnreferencedCode("Reflects over the passed Type for properties + attributes.")]
    [RequiresDynamicCode("Reflects over the passed Type for properties + attributes.")]
    private static SortedDictionary<string, string> ExtractFromCode(Type type)
    {
        var dict = new SortedDictionary<string, string>();
        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);

        foreach (var prop in props)
        {
            if (!prop.CanWrite) continue;

            string baseKey = $"Settings.{prop.Name}";

            var attr = prop.GetCustomAttribute<DisplayAttribute>();
            string label = attr?.Name ?? SplitCamelCase(prop.Name);
            string desc = attr?.Description ?? "";

            dict[$"{baseKey}.Label"] = label;
            dict[$"{baseKey}.Desc"] = desc;

            if (prop.PropertyType.IsEnum)
            {
                foreach (string name in Enum.GetNames(prop.PropertyType))
                {
                    dict[$"{baseKey}.Option.{name}"] = name;
                }
            }
        }
        return dict;

    }

    [RequiresUnreferencedCode("Reflects over the TConfig type via Type.GetProperties.")]
    [RequiresDynamicCode("Reflects over the TConfig type via Type.GetProperties.")]
    private static SortedDictionary<string, string> ExtractFromCode<TConfig>()
    {
        return ExtractFromCode(typeof(TConfig));
    }

    [RequiresUnreferencedCode("Uses the reflection-based JsonSerializer.Deserialize overload over an open Dictionary<string,string> shape.")]
    [RequiresDynamicCode("Uses the reflection-based JsonSerializer.Deserialize overload over an open Dictionary<string,string> shape.")]
    private static Dictionary<string, string> ReadJson(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            var opts = new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip };
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json, opts) ?? new();
        }
        catch { return new(); }
    }

    [RequiresUnreferencedCode("Uses the reflection-based JsonSerializer.Serialize overload over an opaque object payload.")]
    [RequiresDynamicCode("Uses the reflection-based JsonSerializer.Serialize overload over an opaque object payload.")]
    private static void SaveJson(object data, string path)
    {
        var opts = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        File.WriteAllText(path, JsonSerializer.Serialize(data, opts));
    }

    private static string SplitCamelCase(string str) =>
        System.Text.RegularExpressions.Regex.Replace(str, "([A-Z])", " $1").Trim();

}
