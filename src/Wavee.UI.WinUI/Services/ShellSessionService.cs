using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.Controls.PageHost;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.Contracts;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.Json;

namespace Wavee.UI.WinUI.Services;

public sealed partial class ShellSessionService : IShellSessionService
{
    // Discriminator strings stored in SerializedNavigationParameter.TypeName.
    // Replaces the prior approach of storing Type.FullName + resolving with
    // Type.GetType / Assembly.GetType — AOT cannot guarantee that round-trip.
    private const string ParameterKindString = "string";
    private const string ParameterKindCreatePlaylist = "CreatePlaylistParameter";

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
            SelectedTabIndex = layout.SelectedTabIndex,
            PlayerLocation = layout.PlayerLocation,
            SidebarPlayerCollapsed = layout.SidebarPlayerCollapsed,
            PlayerWindowDetached = layout.PlayerWindowDetached,
            PlayerWindowX = layout.PlayerWindowX,
            PlayerWindowY = layout.PlayerWindowY,
            PlayerWindowWidth = layout.PlayerWindowWidth,
            PlayerWindowHeight = layout.PlayerWindowHeight,
            PlayerWindowExpanded = layout.PlayerWindowExpanded,
            PlayerWindowExpandedMode = layout.PlayerWindowExpandedMode,
            PlayerWindowAlwaysOnTop = layout.PlayerWindowAlwaysOnTop,
            PlayerWindowExpandedX = layout.PlayerWindowExpandedX,
            PlayerWindowExpandedY = layout.PlayerWindowExpandedY,
            PlayerWindowExpandedWidth = layout.PlayerWindowExpandedWidth,
            PlayerWindowExpandedHeight = layout.PlayerWindowExpandedHeight,
            RightPanelWindowDetached = layout.RightPanelWindowDetached,
            RightPanelWindowX = layout.RightPanelWindowX,
            RightPanelWindowY = layout.RightPanelWindowY,
            RightPanelWindowWidth = layout.RightPanelWindowWidth,
            RightPanelWindowHeight = layout.RightPanelWindowHeight
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
            // PageTypeName is a stable string key (page nameof literal) sourced
            // from PageTypeRegistry — NOT Type.FullName. AOT cannot guarantee
            // the FullName↔Type round-trip survives trimming, so we look it up
            // through the same registry that PageRegistration populated at
            // startup.
            if (!PageTypeRegistry.TryGetType(tab.PageTypeName, out var pageType) || pageType is null)
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
        settings.ShellSession.Layout ??= new ShellLayoutState();
        settings.ShellSession.SidebarGroups ??= [];
        settings.ShellSession.Tabs ??= [];

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
        var pageType = tab.NavigationParameter?.InitialPageType ?? tab.ContentHost.ActivePage?.GetType();
        if (pageType == null)
            return null;

        // PageTypeName is the PageTypeRegistry key (a nameof literal), not
        // Type.FullName. Page types absent from the registry can't be restored,
        // which mirrors PageHost behaviour for any unregistered page.
        if (!PageTypeRegistry.TryGetKey(pageType, out var pageKey) || string.IsNullOrEmpty(pageKey))
            return null;

        return new TabSessionState
        {
            PageTypeName = pageKey,
            Parameter = SerializeParameter(tab.NavigationParameter?.NavigationParameter),
            Header = tab.Header,
            IsPinned = tab.IsPinned,
            IsCompact = tab.IsCompact
        };
    }

    // ── Navigation parameter persistence ────────────────────────────────
    //
    // Replaces the prior reflection-based approach
    // (JsonSerializer.Serialize(obj, Type, opts) + Type.GetType(string)) with
    // an explicit kind-discriminated switch. The two supported parameter
    // shapes today are bare strings (URIs, sort tags, search queries) and the
    // CreatePlaylistParameter record. Any other parameter type returns null on
    // serialize, which mirrors how PageTypeRegistry handles an unregistered
    // page: the parameter is lost on restore, the page still opens. If a new
    // typed parameter is introduced and needs to survive shell-session
    // restore, register it both here and in WaveeUiWinUiJsonContext.

    private static SerializedNavigationParameter? SerializeParameter(object? parameter)
    {
        return parameter switch
        {
            null => null,
            string s => new SerializedNavigationParameter
            {
                TypeName = ParameterKindString,
                // Store the string raw — no JSON wrapping needed for a primitive.
                Json = s
            },
            CreatePlaylistParameter cp => new SerializedNavigationParameter
            {
                TypeName = ParameterKindCreatePlaylist,
                Json = JsonSerializer.Serialize(cp, WaveeUiWinUiJsonContext.Default.CreatePlaylistParameter)
            },
            _ => null
        };
    }

    private static object? DeserializeParameter(SerializedNavigationParameter? parameter)
    {
        if (parameter == null || string.IsNullOrWhiteSpace(parameter.TypeName))
            return null;

        try
        {
            return parameter.TypeName switch
            {
                ParameterKindString => parameter.Json,
                ParameterKindCreatePlaylist when !string.IsNullOrWhiteSpace(parameter.Json) =>
                    (object?)JsonSerializer.Deserialize(parameter.Json, WaveeUiWinUiJsonContext.Default.CreatePlaylistParameter),
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }
}
