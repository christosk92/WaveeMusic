import { writeFileSync } from 'fs';

const output = {
  nodes: [
    // FILE NODES
    {
      id: "file:src/Wavee.UI.WinUI/Data/DTOs/NowPlayingTrackAdapter.cs",
      type: "file",
      name: "NowPlayingTrackAdapter.cs",
      filePath: "src/Wavee.UI.WinUI/Data/DTOs/NowPlayingTrackAdapter.cs",
      summary: "DTO adapter that maps a now-playing track to a flat view model record with display-ready properties like DurationFormatted, IsLiked, and OriginalIndex.",
      tags: ["data-model", "dto", "adapter"],
      complexity: "simple"
    },
    {
      id: "file:src/Wavee.UI.WinUI/Data/Enums/LibrarySortBy.cs",
      type: "file",
      name: "LibrarySortBy.cs",
      filePath: "src/Wavee.UI.WinUI/Data/Enums/LibrarySortBy.cs",
      summary: "Enum defining the sort criteria options available for the user library views (e.g., by name, date added, artist).",
      tags: ["type-definition", "enum", "library"],
      complexity: "simple"
    },
    {
      id: "file:src/Wavee.UI.WinUI/Data/Enums/LibrarySortDirection.cs",
      type: "file",
      name: "LibrarySortDirection.cs",
      filePath: "src/Wavee.UI.WinUI/Data/Enums/LibrarySortDirection.cs",
      summary: "Enum representing ascending or descending sort direction for library views.",
      tags: ["type-definition", "enum", "library"],
      complexity: "simple"
    },
    {
      id: "file:src/Wavee.UI.WinUI/Data/Enums/LibraryViewMode.cs",
      type: "file",
      name: "LibraryViewMode.cs",
      filePath: "src/Wavee.UI.WinUI/Data/Enums/LibraryViewMode.cs",
      summary: "Enum defining the display mode options for library views such as grid, list, or compact.",
      tags: ["type-definition", "enum", "library"],
      complexity: "simple"
    },
    {
      id: "file:src/Wavee.UI.WinUI/Data/Enums/NavigationPageType.cs",
      type: "file",
      name: "NavigationPageType.cs",
      filePath: "src/Wavee.UI.WinUI/Data/Enums/NavigationPageType.cs",
      summary: "Enum enumerating all navigable page types in the WinUI app, used to drive the factory-registry page navigation system.",
      tags: ["type-definition", "enum", "navigation"],
      complexity: "simple"
    },
    {
      id: "file:src/Wavee.UI.WinUI/Data/Enums/PlayerLocation.cs",
      type: "file",
      name: "PlayerLocation.cs",
      filePath: "src/Wavee.UI.WinUI/Data/Enums/PlayerLocation.cs",
      summary: "Enum representing where the player UI is docked or displayed (e.g., bottom bar, sidebar, floating window).",
      tags: ["type-definition", "enum", "player"],
      complexity: "simple"
    },
    {
      id: "file:src/Wavee.UI.WinUI/Data/Enums/RightPanelMode.cs",
      type: "file",
      name: "RightPanelMode.cs",
      filePath: "src/Wavee.UI.WinUI/Data/Enums/RightPanelMode.cs",
      summary: "Enum defining the content modes available for the right panel (e.g., queue, lyrics, related, now-playing).",
      tags: ["type-definition", "enum", "right-panel"],
      complexity: "simple"
    },
    {
      id: "file:src/Wavee.UI.WinUI/Data/Messages/AppMessages.cs",
      type: "file",
      name: "AppMessages.cs",
      filePath: "src/Wavee.UI.WinUI/Data/Messages/AppMessages.cs",
      summary: "Central hub of 32 MVVM Toolkit messenger message types covering playback, auth, library sync, navigation, notifications, and UI-state changes for the entire WinUI app.",
      tags: ["event-handler", "messaging", "data-model"],
      complexity: "complex"
    },
    {
      id: "file:src/Wavee.UI.WinUI/Data/Messages/TrackMetadataMessages.cs",
      type: "file",
      name: "TrackMetadataMessages.cs",
      filePath: "src/Wavee.UI.WinUI/Data/Messages/TrackMetadataMessages.cs",
      summary: "Defines messenger message types for requesting and delivering track metadata enrichment (single track, queue batch, images, extended top tracks).",
      tags: ["event-handler", "messaging", "data-model"],
      complexity: "moderate"
    },
    {
      id: "file:src/Wavee.UI.WinUI/Data/Models/ActivityItem.cs",
      type: "file",
      name: "ActivityItem.cs",
      filePath: "src/Wavee.UI.WinUI/Data/Models/ActivityItem.cs",
      summary: "Defines the IActivityItem interface and a hierarchy of activity item records (progress, notification, Spotify) used to populate the activity feed UI.",
      tags: ["data-model", "interface", "activity"],
      complexity: "moderate"
    },
    {
      id: "file:src/Wavee.UI.WinUI/Data/Models/AppModel.cs",
      type: "file",
      name: "AppModel.cs",
      filePath: "src/Wavee.UI.WinUI/Data/Models/AppModel.cs",
      summary: "Observable MVVM model for runtime app-level UI state (sidebar width, panel open/close, tab index, player location) initialized from persisted AppSettings.",
      tags: ["data-model", "singleton", "state-management"],
      complexity: "moderate"
    },
    {
      id: "file:src/Wavee.UI.WinUI/Data/Models/AppSettings.cs",
      type: "file",
      name: "AppSettings.cs",
      filePath: "src/Wavee.UI.WinUI/Data/Models/AppSettings.cs",
      summary: "Comprehensive serializable settings model holding all user preferences including theme, audio quality, EQ, caching, library tabs, AI features, window geometry, and home section configuration.",
      tags: ["data-model", "configuration", "serialization"],
      complexity: "complex"
    },
    {
      id: "file:src/Wavee.UI.WinUI/Data/Models/CachingProfile.cs",
      type: "file",
      name: "CachingProfile.cs",
      filePath: "src/Wavee.UI.WinUI/Data/Models/CachingProfile.cs",
      summary: "Record type defining a named audio cache profile with size limits and per-resource-type allocation fields.",
      tags: ["data-model", "configuration", "cache"],
      complexity: "simple"
    },
    {
      id: "file:src/Wavee.UI.WinUI/Data/Models/CachingProfilePresets.cs",
      type: "file",
      name: "CachingProfilePresets.cs",
      filePath: "src/Wavee.UI.WinUI/Data/Models/CachingProfilePresets.cs",
      summary: "Static factory providing named CachingProfile presets (Off, Minimal, Balanced, Aggressive, Custom) with helpers to estimate storage usage and produce display labels.",
      tags: ["factory", "configuration", "cache"],
      complexity: "complex"
    },
    {
      id: "file:src/Wavee.UI.WinUI/Data/Models/ChangelogEntry.cs",
      type: "file",
      name: "ChangelogEntry.cs",
      filePath: "src/Wavee.UI.WinUI/Data/Models/ChangelogEntry.cs",
      summary: "Record types for changelog data: ChangelogRelease (version, title, feature list, announcement) and ChangelogFeature (title, glyph, descriptions, image asset).",
      tags: ["data-model", "serialization"],
      complexity: "simple"
    },
    {
      id: "file:src/Wavee.UI.WinUI/Data/Models/Common/ImageModel.cs",
      type: "file",
      name: "ImageModel.cs",
      filePath: "src/Wavee.UI.WinUI/Data/Models/Common/ImageModel.cs",
      summary: "Common image model record used across the app to carry an image URL plus width and height metadata for display-sizing decisions.",
      tags: ["data-model", "utility"],
      complexity: "simple"
    },
    {
      id: "file:src/Wavee.UI.WinUI/Data/Models/Common/LoadState.cs",
      type: "file",
      name: "LoadState.cs",
      filePath: "src/Wavee.UI.WinUI/Data/Models/Common/LoadState.cs",
      summary: "Type representing the async load state (Idle, Loading, Loaded, Failed) used by view models to drive loading indicators.",
      tags: ["type-definition", "data-model", "utility"],
      complexity: "simple"
    },
    {
      id: "file:src/Wavee.UI.WinUI/Data/Models/NotificationInfo.cs",
      type: "file",
      name: "NotificationInfo.cs",
      filePath: "src/Wavee.UI.WinUI/Data/Models/NotificationInfo.cs",
      summary: "Model carrying the data for an in-app notification (message, severity, optional action label and callback) used by the notification system.",
      tags: ["data-model", "notification"],
      complexity: "simple"
    },
    {
      id: "file:src/Wavee.UI.WinUI/Data/Models/ShellSessionState.cs",
      type: "file",
      name: "ShellSessionState.cs",
      filePath: "src/Wavee.UI.WinUI/Data/Models/ShellSessionState.cs",
      summary: "Serializable state snapshot for the shell session encompassing layout (sidebar, right panel, player window geometry), sidebar group expansion, and open tab list for session restore.",
      tags: ["data-model", "serialization", "state-management"],
      complexity: "moderate"
    },
    {
      id: "file:src/Wavee.UI.WinUI/Data/Parameters/ArtistDiscographyNavigationParameter.cs",
      type: "file",
      name: "ArtistDiscographyNavigationParameter.cs",
      filePath: "src/Wavee.UI.WinUI/Data/Parameters/ArtistDiscographyNavigationParameter.cs",
      summary: "Navigation parameter record carrying the artist URI and optional initial album URI for deep-linking into the discography expander page.",
      tags: ["data-model", "navigation"],
      complexity: "simple"
    },
    {
      id: "file:src/Wavee.UI.WinUI/Data/Parameters/ContentNavigationParameter.cs",
      type: "file",
      name: "ContentNavigationParameter.cs",
      filePath: "src/Wavee.UI.WinUI/Data/Parameters/ContentNavigationParameter.cs",
      summary: "Generic content navigation parameter carrying a Spotify URI and optional display context for playlist or album page deep-links.",
      tags: ["data-model", "navigation"],
      complexity: "simple"
    },
    {
      id: "file:src/Wavee.UI.WinUI/Data/Parameters/EpisodeNavigationParameter.cs",
      type: "file",
      name: "EpisodeNavigationParameter.cs",
      filePath: "src/Wavee.UI.WinUI/Data/Parameters/EpisodeNavigationParameter.cs",
      summary: "Navigation parameter for podcast episode pages, carrying the episode URI and associated show URI.",
      tags: ["data-model", "navigation"],
      complexity: "simple"
    },
    {
      id: "file:src/Wavee.UI.WinUI/Data/Parameters/TabItemParameter.cs",
      type: "file",
      name: "TabItemParameter.cs",
      filePath: "src/Wavee.UI.WinUI/Data/Parameters/TabItemParameter.cs",
      summary: "Tab navigation parameter that encapsulates initial page type and navigation parameter, with Serialize/Deserialize helpers for persisting tab state to ShellSessionState.",
      tags: ["data-model", "navigation", "serialization"],
      complexity: "moderate"
    },
    {
      id: "file:src/Wavee.UI.WinUI/Data/Stores/AlbumStore.cs",
      type: "file",
      name: "AlbumStore.cs",
      filePath: "src/Wavee.UI.WinUI/Data/Stores/AlbumStore.cs",
      summary: "Two-tier cache store for album metadata combining in-memory hot cache with on-disk cold storage, plus a HintPartial method for inserting incomplete records ahead of a full fetch.",
      tags: ["service", "cache", "data-model"],
      complexity: "moderate"
    },
    {
      id: "file:src/Wavee.UI.WinUI/Data/Stores/ArtistStore.cs",
      type: "file",
      name: "ArtistStore.cs",
      filePath: "src/Wavee.UI.WinUI/Data/Stores/ArtistStore.cs",
      summary: "Two-tier cache store for artist metadata mirroring AlbumStore hot/cold read-write pattern for fast repeated artist page loads.",
      tags: ["service", "cache", "data-model"],
      complexity: "moderate"
    },

    // CLASS NODES
    {
      id: "class:src/Wavee.UI.WinUI/Data/DTOs/NowPlayingTrackAdapter.cs:NowPlayingTrackAdapter",
      type: "class",
      name: "NowPlayingTrackAdapter",
      filePath: "src/Wavee.UI.WinUI/Data/DTOs/NowPlayingTrackAdapter.cs",
      lineRange: [14, 56],
      summary: "Flat record adapter mapping a now-playing queue item to view model properties including formatted duration, like state, and original queue index.",
      tags: ["data-model", "dto", "adapter"],
      complexity: "simple"
    },
    {
      id: "class:src/Wavee.UI.WinUI/Data/Models/AppModel.cs:AppModel",
      type: "class",
      name: "AppModel",
      filePath: "src/Wavee.UI.WinUI/Data/Models/AppModel.cs",
      lineRange: [9, 127],
      summary: "Observable MVVM model synchronizing runtime shell layout state with persisted AppSettings via property-changed callbacks for sidebar, right panel, tabs, and player location.",
      tags: ["data-model", "singleton", "state-management"],
      complexity: "moderate"
    },
    {
      id: "class:src/Wavee.UI.WinUI/Data/Models/AppSettings.cs:AppSettings",
      type: "class",
      name: "AppSettings",
      filePath: "src/Wavee.UI.WinUI/Data/Models/AppSettings.cs",
      lineRange: [6, 274],
      summary: "Root serializable settings class with 75+ properties spanning theme, audio, cache, EQ, library, AI, and window state used as the JSON-persisted app configuration.",
      tags: ["data-model", "configuration", "serialization"],
      complexity: "complex"
    },
    {
      id: "class:src/Wavee.UI.WinUI/Data/Models/CachingProfilePresets.cs:CachingProfilePresets",
      type: "class",
      name: "CachingProfilePresets",
      filePath: "src/Wavee.UI.WinUI/Data/Models/CachingProfilePresets.cs",
      lineRange: [22, 182],
      summary: "Static factory class supplying named audio-cache presets and utilities for estimating disk usage and generating human-readable labels.",
      tags: ["factory", "configuration", "cache"],
      complexity: "complex"
    },
    {
      id: "class:src/Wavee.UI.WinUI/Data/Parameters/TabItemParameter.cs:TabItemParameter",
      type: "class",
      name: "TabItemParameter",
      filePath: "src/Wavee.UI.WinUI/Data/Parameters/TabItemParameter.cs",
      lineRange: [8, 60],
      summary: "Serializable tab navigation parameter with constructor overloads and JSON Serialize/Deserialize for persisting open tabs across app sessions.",
      tags: ["data-model", "navigation", "serialization"],
      complexity: "moderate"
    },
    {
      id: "class:src/Wavee.UI.WinUI/Data/Stores/AlbumStore.cs:AlbumStore",
      type: "class",
      name: "AlbumStore",
      filePath: "src/Wavee.UI.WinUI/Data/Stores/AlbumStore.cs",
      lineRange: [23, 70],
      summary: "Two-tier album metadata cache with async hot/cold read-write and a HintPartial fast-path for pre-filling incomplete records.",
      tags: ["service", "cache", "data-model"],
      complexity: "moderate"
    },
    {
      id: "class:src/Wavee.UI.WinUI/Data/Stores/ArtistStore.cs:ArtistStore",
      type: "class",
      name: "ArtistStore",
      filePath: "src/Wavee.UI.WinUI/Data/Stores/ArtistStore.cs",
      lineRange: [22, 53],
      summary: "Two-tier artist metadata cache with async hot/cold read-write mirroring AlbumStore interface.",
      tags: ["service", "cache", "data-model"],
      complexity: "moderate"
    },

    // FUNCTION NODES
    {
      id: "function:src/Wavee.UI.WinUI/Data/Models/AppModel.cs:InitializeFromSettings",
      type: "function",
      name: "InitializeFromSettings",
      filePath: "src/Wavee.UI.WinUI/Data/Models/AppModel.cs",
      lineRange: [51, 72],
      summary: "Hydrates all AppModel observable properties from a persisted AppSettings instance, setting sidebar width, panel states, tab index, and player location in one pass.",
      tags: ["utility", "state-management"],
      complexity: "moderate"
    },
    {
      id: "function:src/Wavee.UI.WinUI/Data/Models/CachingProfilePresets.cs:Get",
      type: "function",
      name: "Get",
      filePath: "src/Wavee.UI.WinUI/Data/Models/CachingProfilePresets.cs",
      lineRange: [50, 119],
      summary: "Returns the CachingProfile record for a given preset enum value with size-proportional allocation fields for tracks, podcasts, images, and videos.",
      tags: ["factory", "cache", "configuration"],
      complexity: "moderate"
    },
    {
      id: "function:src/Wavee.UI.WinUI/Data/Models/CachingProfilePresets.cs:EstimateMegabytes",
      type: "function",
      name: "EstimateMegabytes",
      filePath: "src/Wavee.UI.WinUI/Data/Models/CachingProfilePresets.cs",
      lineRange: [143, 163],
      summary: "Estimates total cache footprint in megabytes for a CachingProfile by summing per-resource-type byte limits.",
      tags: ["utility", "cache"],
      complexity: "simple"
    },
    {
      id: "function:src/Wavee.UI.WinUI/Data/Parameters/TabItemParameter.cs:Serialize",
      type: "function",
      name: "Serialize",
      filePath: "src/Wavee.UI.WinUI/Data/Parameters/TabItemParameter.cs",
      lineRange: [25, 35],
      summary: "Serializes the TabItemParameter to a SerializedNavigationParameter record by JSON-encoding the navigation parameter and storing the page type name.",
      tags: ["serialization", "navigation"],
      complexity: "simple"
    },
    {
      id: "function:src/Wavee.UI.WinUI/Data/Parameters/TabItemParameter.cs:Deserialize",
      type: "function",
      name: "Deserialize",
      filePath: "src/Wavee.UI.WinUI/Data/Parameters/TabItemParameter.cs",
      lineRange: [37, 59],
      summary: "Deserializes a SerializedNavigationParameter into a typed TabItemParameter by mapping the page type name to NavigationPageType and reconstructing the navigation parameter via JSON.",
      tags: ["serialization", "navigation"],
      complexity: "moderate"
    }
  ],
  edges: [
    // contains: file -> class
    { source: "file:src/Wavee.UI.WinUI/Data/DTOs/NowPlayingTrackAdapter.cs", target: "class:src/Wavee.UI.WinUI/Data/DTOs/NowPlayingTrackAdapter.cs:NowPlayingTrackAdapter", type: "contains", direction: "forward", weight: 1.0 },
    { source: "file:src/Wavee.UI.WinUI/Data/Models/AppModel.cs", target: "class:src/Wavee.UI.WinUI/Data/Models/AppModel.cs:AppModel", type: "contains", direction: "forward", weight: 1.0 },
    { source: "file:src/Wavee.UI.WinUI/Data/Models/AppSettings.cs", target: "class:src/Wavee.UI.WinUI/Data/Models/AppSettings.cs:AppSettings", type: "contains", direction: "forward", weight: 1.0 },
    { source: "file:src/Wavee.UI.WinUI/Data/Models/CachingProfilePresets.cs", target: "class:src/Wavee.UI.WinUI/Data/Models/CachingProfilePresets.cs:CachingProfilePresets", type: "contains", direction: "forward", weight: 1.0 },
    { source: "file:src/Wavee.UI.WinUI/Data/Parameters/TabItemParameter.cs", target: "class:src/Wavee.UI.WinUI/Data/Parameters/TabItemParameter.cs:TabItemParameter", type: "contains", direction: "forward", weight: 1.0 },
    { source: "file:src/Wavee.UI.WinUI/Data/Stores/AlbumStore.cs", target: "class:src/Wavee.UI.WinUI/Data/Stores/AlbumStore.cs:AlbumStore", type: "contains", direction: "forward", weight: 1.0 },
    { source: "file:src/Wavee.UI.WinUI/Data/Stores/ArtistStore.cs", target: "class:src/Wavee.UI.WinUI/Data/Stores/ArtistStore.cs:ArtistStore", type: "contains", direction: "forward", weight: 1.0 },
    // contains: file -> function
    { source: "file:src/Wavee.UI.WinUI/Data/Models/AppModel.cs", target: "function:src/Wavee.UI.WinUI/Data/Models/AppModel.cs:InitializeFromSettings", type: "contains", direction: "forward", weight: 1.0 },
    { source: "file:src/Wavee.UI.WinUI/Data/Models/CachingProfilePresets.cs", target: "function:src/Wavee.UI.WinUI/Data/Models/CachingProfilePresets.cs:Get", type: "contains", direction: "forward", weight: 1.0 },
    { source: "file:src/Wavee.UI.WinUI/Data/Models/CachingProfilePresets.cs", target: "function:src/Wavee.UI.WinUI/Data/Models/CachingProfilePresets.cs:EstimateMegabytes", type: "contains", direction: "forward", weight: 1.0 },
    { source: "file:src/Wavee.UI.WinUI/Data/Parameters/TabItemParameter.cs", target: "function:src/Wavee.UI.WinUI/Data/Parameters/TabItemParameter.cs:Serialize", type: "contains", direction: "forward", weight: 1.0 },
    { source: "file:src/Wavee.UI.WinUI/Data/Parameters/TabItemParameter.cs", target: "function:src/Wavee.UI.WinUI/Data/Parameters/TabItemParameter.cs:Deserialize", type: "contains", direction: "forward", weight: 1.0 },
    // contains: class -> function
    { source: "class:src/Wavee.UI.WinUI/Data/Models/AppModel.cs:AppModel", target: "function:src/Wavee.UI.WinUI/Data/Models/AppModel.cs:InitializeFromSettings", type: "contains", direction: "forward", weight: 1.0 },
    { source: "class:src/Wavee.UI.WinUI/Data/Models/CachingProfilePresets.cs:CachingProfilePresets", target: "function:src/Wavee.UI.WinUI/Data/Models/CachingProfilePresets.cs:Get", type: "contains", direction: "forward", weight: 1.0 },
    { source: "class:src/Wavee.UI.WinUI/Data/Models/CachingProfilePresets.cs:CachingProfilePresets", target: "function:src/Wavee.UI.WinUI/Data/Models/CachingProfilePresets.cs:EstimateMegabytes", type: "contains", direction: "forward", weight: 1.0 },
    { source: "class:src/Wavee.UI.WinUI/Data/Parameters/TabItemParameter.cs:TabItemParameter", target: "function:src/Wavee.UI.WinUI/Data/Parameters/TabItemParameter.cs:Serialize", type: "contains", direction: "forward", weight: 1.0 },
    { source: "class:src/Wavee.UI.WinUI/Data/Parameters/TabItemParameter.cs:TabItemParameter", target: "function:src/Wavee.UI.WinUI/Data/Parameters/TabItemParameter.cs:Deserialize", type: "contains", direction: "forward", weight: 1.0 },
    // exports: file -> class/function
    { source: "file:src/Wavee.UI.WinUI/Data/DTOs/NowPlayingTrackAdapter.cs", target: "class:src/Wavee.UI.WinUI/Data/DTOs/NowPlayingTrackAdapter.cs:NowPlayingTrackAdapter", type: "exports", direction: "forward", weight: 0.8 },
    { source: "file:src/Wavee.UI.WinUI/Data/Models/AppModel.cs", target: "class:src/Wavee.UI.WinUI/Data/Models/AppModel.cs:AppModel", type: "exports", direction: "forward", weight: 0.8 },
    { source: "file:src/Wavee.UI.WinUI/Data/Models/AppSettings.cs", target: "class:src/Wavee.UI.WinUI/Data/Models/AppSettings.cs:AppSettings", type: "exports", direction: "forward", weight: 0.8 },
    { source: "file:src/Wavee.UI.WinUI/Data/Models/CachingProfilePresets.cs", target: "class:src/Wavee.UI.WinUI/Data/Models/CachingProfilePresets.cs:CachingProfilePresets", type: "exports", direction: "forward", weight: 0.8 },
    { source: "file:src/Wavee.UI.WinUI/Data/Parameters/TabItemParameter.cs", target: "class:src/Wavee.UI.WinUI/Data/Parameters/TabItemParameter.cs:TabItemParameter", type: "exports", direction: "forward", weight: 0.8 },
    { source: "file:src/Wavee.UI.WinUI/Data/Stores/AlbumStore.cs", target: "class:src/Wavee.UI.WinUI/Data/Stores/AlbumStore.cs:AlbumStore", type: "exports", direction: "forward", weight: 0.8 },
    { source: "file:src/Wavee.UI.WinUI/Data/Stores/ArtistStore.cs", target: "class:src/Wavee.UI.WinUI/Data/Stores/ArtistStore.cs:ArtistStore", type: "exports", direction: "forward", weight: 0.8 },
    { source: "file:src/Wavee.UI.WinUI/Data/Models/CachingProfilePresets.cs", target: "function:src/Wavee.UI.WinUI/Data/Models/CachingProfilePresets.cs:Get", type: "exports", direction: "forward", weight: 0.8 },
    { source: "file:src/Wavee.UI.WinUI/Data/Models/CachingProfilePresets.cs", target: "function:src/Wavee.UI.WinUI/Data/Models/CachingProfilePresets.cs:EstimateMegabytes", type: "exports", direction: "forward", weight: 0.8 },
    { source: "file:src/Wavee.UI.WinUI/Data/Parameters/TabItemParameter.cs", target: "function:src/Wavee.UI.WinUI/Data/Parameters/TabItemParameter.cs:Serialize", type: "exports", direction: "forward", weight: 0.8 },
    { source: "file:src/Wavee.UI.WinUI/Data/Parameters/TabItemParameter.cs", target: "function:src/Wavee.UI.WinUI/Data/Parameters/TabItemParameter.cs:Deserialize", type: "exports", direction: "forward", weight: 0.8 },
    // depends_on: semantic cross-file relationships
    { source: "file:src/Wavee.UI.WinUI/Data/Models/AppModel.cs", target: "file:src/Wavee.UI.WinUI/Data/Models/AppSettings.cs", type: "depends_on", direction: "forward", weight: 0.6 },
    { source: "file:src/Wavee.UI.WinUI/Data/Parameters/TabItemParameter.cs", target: "file:src/Wavee.UI.WinUI/Data/Enums/NavigationPageType.cs", type: "depends_on", direction: "forward", weight: 0.6 },
    { source: "file:src/Wavee.UI.WinUI/Data/Models/AppSettings.cs", target: "file:src/Wavee.UI.WinUI/Data/Models/CachingProfile.cs", type: "depends_on", direction: "forward", weight: 0.6 },
    { source: "file:src/Wavee.UI.WinUI/Data/Models/AppSettings.cs", target: "file:src/Wavee.UI.WinUI/Data/Enums/LibrarySortBy.cs", type: "depends_on", direction: "forward", weight: 0.6 },
    { source: "file:src/Wavee.UI.WinUI/Data/Models/AppSettings.cs", target: "file:src/Wavee.UI.WinUI/Data/Enums/LibrarySortDirection.cs", type: "depends_on", direction: "forward", weight: 0.6 },
    { source: "file:src/Wavee.UI.WinUI/Data/Models/AppSettings.cs", target: "file:src/Wavee.UI.WinUI/Data/Enums/LibraryViewMode.cs", type: "depends_on", direction: "forward", weight: 0.6 },
    { source: "file:src/Wavee.UI.WinUI/Data/Models/ShellSessionState.cs", target: "file:src/Wavee.UI.WinUI/Data/Enums/PlayerLocation.cs", type: "depends_on", direction: "forward", weight: 0.6 },
    { source: "file:src/Wavee.UI.WinUI/Data/Models/ShellSessionState.cs", target: "file:src/Wavee.UI.WinUI/Data/Enums/RightPanelMode.cs", type: "depends_on", direction: "forward", weight: 0.6 }
  ]
};

const nodeCount = output.nodes.length;
const edgeCount = output.edges.length;
console.log(`nodeCount: ${nodeCount}, edgeCount: ${edgeCount}`);

writeFileSync(
  'C:/WAVEE/WaveeMusic/.understand-anything/intermediate/batch-40.json',
  JSON.stringify(output, null, 2),
  'utf8'
);
console.log('Written batch-40.json');
