using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Wavee.UI.WinUI.Controls.Settings;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Views;

public sealed partial class SettingsPage : Page, ITabBarItemContent, IDisposable
{
    private const int MaxDeferredShowSectionAttempts = 3;

    /// <summary>
    /// Shared catalog of searchable settings — read by <c>ShellViewModel</c> to power
    /// the omnibar's Settings section. Keep this list in sync with the actual settings
    /// UI; the omnibar uses <see cref="SettingsSearchEntry.Matches"/> to filter.
    /// </summary>
    internal static readonly IReadOnlyList<SettingsSearchEntry> SettingsSearchEntries =
    [
        new("General", "App", "general", "appearance", "appearance language display theme zoom locale metadata"),
        new("Theme", "General", "general", "appearance", "light dark system appearance"),
        new("App language", "General", "general", "language", "language locale english korean system"),
        new("Spotify metadata language", "General", "general", "language", "metadata locale language catalog names"),
        new("Display density", "General", "general", "display", "zoom compact spacious scaling"),

        new("On-device AI", "App", "ai", "availability", "local ai language model summarize explain lyrics"),
        new("AI availability", "On-device AI", "ai", "availability", "model hardware region install status"),

        new("Playback & audio", "App", "playback-audio", "queue", "queue autoplay quality normalization lyrics player equalizer eq"),
        new("Track click behavior", "Playback & audio", "playback-audio", "interaction", "single tap double tap track click"),
        new("Default play action", "Playback & audio", "playback-audio", "queue", "play clear queue play next play later"),
        new("Ask before playing", "Playback & audio", "playback-audio", "queue", "prompt replace queue confirm"),
        new("Autoplay", "Playback & audio", "playback-audio", "queue", "similar songs end playlist album artist"),
        new("Docked player", "Playback & audio", "playback-audio", "player", "bottom player sidebar floating popup visible"),
        new("Streaming quality", "Playback & audio", "playback-audio", "audio", "normal high very high 96 160 320 kbps bitrate"),
        new("Normalize volume", "Playback & audio", "playback-audio", "audio", "normalization loudness volume"),
        new("Lyrics sources", "Playback & audio", "playback-audio", "lyrics", "lyrics provider source reorder"),
        new("Equalizer preset", "Playback & audio", "playback-audio", "equalizer", "flat bass boost treble boost vocal radio eq proof"),
        new("Manual equalizer", "Playback & audio", "playback-audio", "equalizer", "bands gains curve reset"),

        new("Storage & local files", "App", "storage", "audio-cache", "cache local files network connection folders"),
        new("Caching profile", "Storage & local files", "storage", "memory", "memory cache browsing speed"),
        new("Audio cache", "Storage & local files", "storage", "audio-cache", "enable cache size limit location clear"),
        new("Clear collection revisions", "Storage & local files", "storage", "audio-cache", "refresh library liked songs albums artists shows resync"),
        new("Local files", "Storage & local files", "storage", "local-files", "watched folders rescan add folder remove files home shelf"),
        new("Connection", "Storage & local files", "storage", "connection", "auto reconnect timeout network"),

        new("Diagnostics", "App", "diagnostics", "health", "configuration logs clock sync audio health troubleshooting"),
        new("Clock sync", "Diagnostics", "diagnostics", "clock", "server time sync interval"),
        new("Logs", "Diagnostics", "diagnostics", "logs", "logging verbose new logs past logs filter copy export"),
        new("Verbose logging", "Diagnostics", "diagnostics", "logs", "logging diagnostic output bug reports audio engine restart"),
        new("Spotify Connect events", "Diagnostics", "diagnostics", "connect-events", "connect updates devices remote state local device cluster dealer put state"),
        new("About", "App", "about", "updates", "version update app information")
    ];

    public SettingsViewModel ViewModel { get; }

    public TabItemParameter? TabItemParameter => ViewModel.TabItemParameter;

    public event EventHandler<TabItemParameter>? ContentChanged;

    private GeneralSettingsSection? _generalSection;
    private AiSettingsSection? _aiSection;
    private PlaybackAudioSettingsSection? _playbackAudioSection;
    private StorageNetworkSettingsSection? _storageSection;
    private DiagnosticsSettingsSection? _diagnosticsSection;
    private AboutSettingsSection? _aboutSection;
    private string? _activeSectionTag;
    private string? _activeSearchSectionTag;
    private string? _activeSearchGroupKey;
    private string? _activeSearchTitle;
    private bool _preserveSearchFilterForNavigation;
    private int _deferredShowSectionAttempts;
    private bool _disposed;

    public SettingsPage()
    {
        ViewModel = Ioc.Default.GetRequiredService<SettingsViewModel>();
        InitializeComponent();

        // EQ settings are sent to AudioHost via IPC through IAudioPipelineControl.
        ViewModel.InitializeEqualizer(Ioc.Default.GetService<IAudioPipelineControl>());

        SettingsNavigation.SelectedItem = GeneralItem;
        ShowSection("general");
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // Omnibar Settings result deep-link: reuse the existing in-page search
        // selection path so chrome ("Showing settings for ...") and the
        // ISettingsSearchFilter group filter behave identically to picking the
        // entry through the in-page AutoSuggestBox.
        if (e.Parameter is SettingsNavigationParameter param)
        {
            var normalizedTag = NormalizeTag(param.SectionTag);
            _activeSearchSectionTag = normalizedTag;
            _activeSearchGroupKey = param.GroupKey;
            _activeSearchTitle = param.EntryTitle;
            UpdateSearchFilterChrome();

            var item = GetNavigationItem(normalizedTag);
            if (item is not null && !ReferenceEquals(SettingsNavigation.SelectedItem, item))
            {
                _preserveSearchFilterForNavigation = true;
                SettingsNavigation.SelectedItem = item;
                _preserveSearchFilterForNavigation = false;
            }

            ShowSection(normalizedTag);
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        Dispose();
        // Detach compiled x:Bind from VM.PropertyChanged after Dispose; the page
        // is destroyed on nav-away (NavCacheMode default = Disabled), no Update()
        // partner needed.
        Bindings?.StopTracking();
        base.OnNavigatedFrom(e);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        ViewModel.StopAudioDiagnostics();
        _diagnosticsSection?.Dispose();
        ViewModel.Dispose();
    }

    private void SettingsNavigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem selectedItem)
            return;

        if (!_preserveSearchFilterForNavigation)
            ClearActiveSearchFilter(applyToCurrentView: false);

        ShowSection(selectedItem.Tag?.ToString() ?? "general");
    }

    private void SettingsSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
            return;

        var query = sender.Text?.Trim();

        if (string.IsNullOrWhiteSpace(query))
        {
            sender.ItemsSource = null;
            ClearActiveSearchFilter(applyToCurrentView: true);
            return;
        }

        sender.ItemsSource = SettingsSearchEntries
            .Where(entry => entry.Matches(query))
            .Take(10)
            .ToList();
    }

    private void SettingsSearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var entry = args.ChosenSuggestion as SettingsSearchEntry
            ?? FindBestSearchMatch(args.QueryText);

        if (entry is not null)
            NavigateToSearchEntry(entry);
    }

    private void SettingsSearchBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is SettingsSearchEntry entry)
            NavigateToSearchEntry(entry);
    }

    private static SettingsSearchEntry? FindBestSearchMatch(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return null;

        return SettingsSearchEntries.FirstOrDefault(entry => entry.Matches(query));
    }

    private void NavigateToSearchEntry(SettingsSearchEntry entry)
    {
        SettingsSearchBox.ItemsSource = null;
        var normalizedTag = NormalizeTag(entry.Tag);
        _activeSearchSectionTag = normalizedTag;
        _activeSearchGroupKey = entry.GroupKey;
        _activeSearchTitle = entry.Title;
        UpdateSearchFilterChrome();

        var item = GetNavigationItem(normalizedTag);
        if (item is not null && !ReferenceEquals(SettingsNavigation.SelectedItem, item))
        {
            _preserveSearchFilterForNavigation = true;
            SettingsNavigation.SelectedItem = item;
            _preserveSearchFilterForNavigation = false;
        }

        ShowSection(normalizedTag);
    }

    private static string NormalizeTag(string tag) => tag switch
    {
        "playback" or "audio" => "playback-audio",
        "connect" => "diagnostics",
        _ => tag
    };

    private void ViewAllSettingsGroups_Click(object sender, RoutedEventArgs e)
    {
        SettingsSearchBox.Text = string.Empty;
        SettingsSearchBox.ItemsSource = null;
        ClearActiveSearchFilter(applyToCurrentView: true);
    }

    private void ClearActiveSearchFilter(bool applyToCurrentView)
    {
        var currentSectionTag = _activeSectionTag;
        _activeSearchSectionTag = null;
        _activeSearchGroupKey = null;
        _activeSearchTitle = null;
        UpdateSearchFilterChrome();

        if (applyToCurrentView && ContentHost?.Content is ISettingsSearchFilter filter)
            filter.ApplySearchFilter(null);

        if (applyToCurrentView && !string.IsNullOrWhiteSpace(currentSectionTag) && SectionDescriptionText != null)
        {
            var (_, description) = GetSectionChrome(currentSectionTag);
            SectionDescriptionText.Text = description;
        }
    }

    private void UpdateSearchFilterChrome()
    {
        if (ViewAllSettingsGroupsButton == null)
            return;

        ViewAllSettingsGroupsButton.Visibility = string.IsNullOrWhiteSpace(_activeSearchGroupKey)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private NavigationViewItem? GetNavigationItem(string tag)
    {
        // Persisted "last visited section" tags from prior builds may still be
        // playback/audio/connect — remap them so users don't get bounced back
        // to General after the merge.
        return tag switch
        {
            "ai" => AiItem,
            "playback" or "audio" or "playback-audio" => PlaybackAudioItem,
            "storage" => StorageItem,
            "diagnostics" or "connect" => DiagnosticsItem,
            "about" => AboutItem,
            _ => GeneralItem
        };
    }

    private void ShowSection(string tag)
    {
        // Belt-and-suspenders: normalize legacy tags so the rest of this
        // method only sees the post-merge canonical set, even if a future
        // caller forgets.
        tag = NormalizeTag(tag);

        if (ContentHost == null)
        {
            if (_deferredShowSectionAttempts < MaxDeferredShowSectionAttempts)
            {
                _deferredShowSectionAttempts++;
                DispatcherQueue.TryEnqueue(() => ShowSection(tag));
            }

            return;
        }

        _deferredShowSectionAttempts = 0;

        if (_activeSectionTag == "diagnostics" && tag != "diagnostics")
            ViewModel.StopAudioDiagnostics();

        UserControl view = tag switch
        {
            "ai" => _aiSection ??= new AiSettingsSection(
                new AiSettingsViewModel(
                    Ioc.Default.GetRequiredService<ISettingsService>(),
                    Ioc.Default.GetRequiredService<AiCapabilities>(),
                    Ioc.Default.GetService<AiNotificationService>())),
            "playback-audio" => _playbackAudioSection ??= new PlaybackAudioSettingsSection(ViewModel),
            "storage" => _storageSection ??= new StorageNetworkSettingsSection(ViewModel),
            "diagnostics" => _diagnosticsSection ??= new DiagnosticsSettingsSection(ViewModel),
            "about" => _aboutSection ??= new AboutSettingsSection(ViewModel),
            _ => _generalSection ??= new GeneralSettingsSection(ViewModel)
        };

        if (!ReferenceEquals(ContentHost.Content, view))
            ContentHost.Content = view;

        var (title, description) = GetSectionChrome(tag);
        SectionTitleText.Text = title;
        SectionDescriptionText.Text = tag == _activeSearchSectionTag && !string.IsNullOrWhiteSpace(_activeSearchTitle)
            ? $"Showing settings for \"{_activeSearchTitle}\"."
            : description;

        if (view is ISettingsSearchFilter filter)
        {
            var groupKey = tag == _activeSearchSectionTag ? _activeSearchGroupKey : null;
            filter.ApplySearchFilter(groupKey);
        }

        if (tag == "diagnostics" && _activeSectionTag != "diagnostics")
            ViewModel.StartAudioDiagnostics();

        _activeSectionTag = tag;
    }

    private static (string Title, string Description) GetSectionChrome(string tag)
    {
        return tag switch
        {
            "ai" => ("On-device AI", "Local AI features, availability, and setup."),
            "playback-audio" => ("Playback & audio", "Track click behavior, queue defaults, streaming quality, lyrics sources, equalizer presets, and manual EQ."),
            "storage" => ("Storage, cache & local files", "Memory cache behavior, audio cache limits, local music folders, and connection timeouts."),
            "diagnostics" => ("Diagnostics", "Audio health, clock sync, logs, Spotify Connect events, and troubleshooting tools."),
            "about" => ("About", "App version, update status, and project information."),
            _ => ("General", "Appearance, language, metadata locale, and display density.")
        };
    }

    internal sealed class SettingsSearchEntry
    {
        private readonly string _searchText;

        public SettingsSearchEntry(string title, string section, string tag, string groupKey, string keywords)
        {
            Title = title;
            Section = section;
            Tag = tag;
            GroupKey = groupKey;
            _searchText = $"{title} {section} {keywords}";
        }

        public string Title { get; }
        public string Section { get; }
        public string Tag { get; }
        public string GroupKey { get; }

        public bool Matches(string query)
        {
            var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return terms.Length > 0
                   && terms.All(term => _searchText.Contains(term, StringComparison.OrdinalIgnoreCase));
        }
    }
}
