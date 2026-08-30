using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wavee;

internal sealed record PersistedWorkspaceTab(string Route, string? Arg);
internal sealed record WorkspaceTabsDocument(int Version, int LastSelected, PersistedWorkspaceTab[] Tabs);
internal readonly record struct WorkspaceTabsSnapshot(PersistedWorkspaceTab[] Tabs, int LastSelected);

/// <summary>AOT-safe codec for the pinned subset of the shell workspace. Ordinary tabs are deliberately session-only.</summary>
internal static class WorkspaceTabsPersistence
{
    const int CurrentVersion = 1;
    const int MaxTabs = 128;

    public static WorkspaceTabsSnapshot Decode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new([], -1);
        try
        {
            var doc = JsonSerializer.Deserialize(raw, WorkspaceTabsJsonContext.Default.WorkspaceTabsDocument);
            if (doc is null || doc.Version != CurrentVersion || doc.Tabs is null) return new([], -1);
            var valid = new List<PersistedWorkspaceTab>(Math.Min(doc.Tabs.Length, MaxTabs));
            int selected = -1;
            for (int i = 0; i < doc.Tabs.Length && valid.Count < MaxTabs; i++)
            {
                var tab = doc.Tabs[i];
                if (tab is null || string.IsNullOrWhiteSpace(tab.Route) || tab.Route.Length > 1024
                    || (tab.Arg?.Length ?? 0) > 4096) continue;
                if (i == doc.LastSelected) selected = valid.Count;
                valid.Add(new PersistedWorkspaceTab(tab.Route, tab.Arg));
            }
            if (selected >= valid.Count) selected = valid.Count > 0 ? 0 : -1;
            return new(valid.ToArray(), selected);
        }
        catch { return new([], -1); }
    }

    public static string Encode(IReadOnlyList<PersistedWorkspaceTab> tabs, int lastSelected)
    {
        int count = Math.Min(tabs.Count, MaxTabs);
        var copy = new PersistedWorkspaceTab[count];
        for (int i = 0; i < count; i++) copy[i] = tabs[i];
        int selected = count == 0 ? -1 : Math.Clamp(lastSelected, 0, count - 1);
        return JsonSerializer.Serialize(
            new WorkspaceTabsDocument(CurrentVersion, selected, copy),
            WorkspaceTabsJsonContext.Default.WorkspaceTabsDocument);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WorkspaceTabsDocument))]
internal sealed partial class WorkspaceTabsJsonContext : JsonSerializerContext;
