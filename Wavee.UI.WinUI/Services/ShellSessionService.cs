using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Helpers.Navigation;

namespace Wavee.UI.WinUI.Services;

public sealed class ShellSessionService : IShellSessionService
{
    private static readonly JsonSerializerOptions ParameterJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ISettingsService _settings;

    public ShellSessionService(ISettingsService settings)
    {
        _settings = settings;
    }

    public bool AskBeforeClosingTabs => _settings.Settings.AskBeforeClosingTabs;

    public CloseTabsBehavior CloseTabsBehavior => _settings.Settings.CloseTabsBehavior;

    public ShellLayoutState GetLayoutSnapshot()
    {
        var layout = GetOrCreateState().Layout;
        return new ShellLayoutState
        {
            SidebarWidth = layout.SidebarWidth,
            SidebarDisplayMode = layout.SidebarDisplayMode,
            IsSidebarPaneOpen = layout.IsSidebarPaneOpen,
            RightPanelWidth = layout.RightPanelWidth,
            IsRightPanelOpen = layout.IsRightPanelOpen,
            RightPanelMode = layout.RightPanelMode,
            SelectedTabIndex = layout.SelectedTabIndex
        };
    }

    public string? GetSelectedSidebarTag() => GetOrCreateState().SelectedSidebarTag;

    public bool TryGetSidebarGroupExpansion(string tag, out bool isExpanded)
    {
        var group = GetOrCreateState().SidebarGroups
            .FirstOrDefault(x => string.Equals(x.Tag, tag, StringComparison.Ordinal));

        if (group == null)
        {
            isExpanded = true;
            return false;
        }

        isExpanded = group.IsExpanded;
        return true;
    }

    public IReadOnlyList<RestoredTabState> GetRestorableTabs()
    {
        var restoredTabs = new List<RestoredTabState>();

        foreach (var tab in GetOrCreateState().Tabs)
        {
            var pageType = ResolveType(tab.PageTypeName);
            if (pageType == null)
                continue;

            var parameter = DeserializeParameter(tab.Parameter);
            var header = string.IsNullOrWhiteSpace(tab.Header)
                ? NavigationHelpers.GetDefaultHeader(pageType, parameter)
                : tab.Header!;

            restoredTabs.Add(new RestoredTabState(
                pageType,
                parameter,
                header,
                tab.IsPinned,
                tab.IsCompact));
        }

        return restoredTabs;
    }

    public void UpdateLayout(Action<ShellLayoutState> update)
    {
        _settings.Update(settings =>
        {
            var state = GetOrCreateState(settings);
            update(state.Layout);
            SyncLegacyLayoutFields(settings, state.Layout);
        });
    }

    public void UpdateSelectedSidebarTag(string? tag)
    {
        _settings.Update(settings =>
        {
            var state = GetOrCreateState(settings);
            state.SelectedSidebarTag = string.IsNullOrWhiteSpace(tag) ? null : tag;
        });
    }

    public void UpdateSidebarGroupExpansion(string tag, bool isExpanded)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return;

        _settings.Update(settings =>
        {
            var state = GetOrCreateState(settings);
            var group = state.SidebarGroups.FirstOrDefault(x => string.Equals(x.Tag, tag, StringComparison.Ordinal));
            if (group == null)
            {
                state.SidebarGroups.Add(new SidebarGroupState
                {
                    Tag = tag,
                    IsExpanded = isExpanded
                });
            }
            else
            {
                group.IsExpanded = isExpanded;
            }
        });
    }

    public void SaveTabs(IReadOnlyList<TabBarItem> tabs, int selectedIndex)
    {
        _settings.Update(settings =>
        {
            var state = GetOrCreateState(settings);
            state.Layout.SelectedTabIndex = tabs.Count == 0
                ? 0
                : Math.Clamp(selectedIndex, 0, tabs.Count - 1);
            state.Tabs = tabs
                .Select(CreateTabState)
                .Where(x => x != null)
                .Cast<TabSessionState>()
                .ToList();
        });
    }

    public void ClearTabs()
    {
        _settings.Update(settings =>
        {
            var state = GetOrCreateState(settings);
            state.Tabs.Clear();
            state.Layout.SelectedTabIndex = 0;
        });
    }

    public void UpdateClosePreference(bool askBeforeClosingTabs, CloseTabsBehavior behavior)
    {
        _settings.Update(settings =>
        {
            settings.AskBeforeClosingTabs = askBeforeClosingTabs;
            settings.CloseTabsBehavior = behavior;
        });
    }

    private ShellSessionState GetOrCreateState()
        => GetOrCreateState(_settings.Settings);

    private static ShellSessionState GetOrCreateState(AppSettings settings)
    {
        settings.ShellSession ??= new ShellSessionState();

        if (!settings.ShellSession.Initialized)
        {
            settings.ShellSession.Layout.SidebarWidth = settings.SidebarWidth;
            settings.ShellSession.Layout.RightPanelWidth = settings.RightPanelWidth;
            settings.ShellSession.Initialized = true;
        }

        return settings.ShellSession;
    }

    private static void SyncLegacyLayoutFields(AppSettings settings, ShellLayoutState layout)
    {
        settings.SidebarWidth = layout.SidebarWidth;
        settings.RightPanelWidth = layout.RightPanelWidth;
    }

    private static TabSessionState? CreateTabState(TabBarItem tab)
    {
        var pageType = tab.NavigationParameter?.InitialPageType ?? tab.ContentFrame.Content?.GetType();
        if (pageType == null || string.IsNullOrWhiteSpace(pageType.FullName))
            return null;

        return new TabSessionState
        {
            PageTypeName = pageType.FullName,
            Parameter = SerializeParameter(tab.NavigationParameter?.NavigationParameter),
            Header = tab.Header,
            IsPinned = tab.IsPinned,
            IsCompact = tab.IsCompact
        };
    }

    private static SerializedNavigationParameter? SerializeParameter(object? parameter)
    {
        if (parameter == null)
            return null;

        var type = parameter.GetType();
        if (string.IsNullOrWhiteSpace(type.FullName))
            return null;

        try
        {
            return new SerializedNavigationParameter
            {
                TypeName = type.FullName,
                Json = JsonSerializer.Serialize(parameter, type, ParameterJsonOptions)
            };
        }
        catch
        {
            return null;
        }
    }

    private static object? DeserializeParameter(SerializedNavigationParameter? parameter)
    {
        if (parameter == null || string.IsNullOrWhiteSpace(parameter.TypeName) || string.IsNullOrWhiteSpace(parameter.Json))
            return null;

        var type = ResolveType(parameter.TypeName);
        if (type == null)
            return null;

        try
        {
            return JsonSerializer.Deserialize(parameter.Json, type, ParameterJsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static Type? ResolveType(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
            return null;

        return Type.GetType(typeName, throwOnError: false)
               ?? typeof(ShellSessionService).Assembly.GetType(typeName, throwOnError: false)
               ?? typeof(CreatePlaylistParameter).Assembly.GetType(typeName, throwOnError: false)
               ?? typeof(string).Assembly.GetType(typeName, throwOnError: false);
    }
}
