using System.Reactive.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Rendering;
using Wavee.Connect;
using Wavee.Connect.Commands;
using Wavee.Connect.Connection;
using Wavee.Connect.Playback;
using Wavee.Connect.Protocol;
using Wavee.Core.DependencyInjection;
using Wavee.Core.Http;
using Wavee.Core.Library;
using Wavee.Core.Library.Spotify;
using Wavee.Core.Session;
using Wavee.Core.Audio;
using Wavee.Core.Http.Lyrics;
using Wavee.Core.Storage;
using Wavee.Core.Storage.Abstractions;

namespace Wavee.Console;

/// <summary>
/// Interactive console interface for Spotify Connect features using Spectre.Console UI.
/// </summary>
internal sealed class ConnectConsole : IDisposable, IAsyncDisposable
{
    private readonly Session _session;
    private readonly HttpClient _httpClient;
    private readonly SpectreUI _ui;
    private readonly ILogger? _logger;
    private readonly List<IDisposable> _subscriptions = new();
    private AudioPipeline? _audioPipeline;
    private ServiceProvider? _serviceProvider;
    private SpotifyLibraryService? _libraryService;
    private bool _disposed;

    // Lyrics state
    private string? _lastTrackUri;
    private LyricsData? _currentLyrics;

    public ConnectConsole(Session session, HttpClient httpClient, SpectreUI ui, ILogger? logger = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _logger = logger;
    }

    /// <summary>
    /// Runs the interactive console.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (_session.DeviceState == null)
        {
            _ui.AddLog("ERR", "Spotify Connect is not enabled. Set EnableConnect = true in SessionConfig.");
            return;
        }

        _ui.AddLog("INF", "Spotify Connect initialized successfully!");
        _ui.AddLog("INF", $"Device: {_session.Config.DeviceName} ({_session.Config.DeviceId[..8]}...)");

        // Initialize DI container with cache services
        try
        {
            var dbPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Wavee",
                "metadata.db");

            var services = new ServiceCollection();
            services.AddWaveeCache(options =>
            {
                options.DatabasePath = dbPath;
            });

            // Register ExtendedMetadataClient - we have session context here
            services.AddSingleton<IExtendedMetadataClient>(sp =>
            {
                var metadataDb = sp.GetRequiredService<IMetadataDatabase>();
                return new ExtendedMetadataClient(
                    _session,
                    _httpClient,
                    _session.SpClient.BaseUrl,
                    metadataDb,
                    _logger);
            });

            _serviceProvider = services.BuildServiceProvider();
            _ui.AddLog("INF", $"Cache services initialized");
        }
        catch (Exception ex)
        {
            _ui.AddLog("WRN", $"Cache services initialization failed: {ex.Message}");
        }

        // Initialize library services (uses unified MetadataDatabase from DI)
        try
        {
            var metadataDatabase = _serviceProvider?.GetService<IMetadataDatabase>();
            if (metadataDatabase == null)
            {
                _ui.AddLog("WRN", "MetadataDatabase not available from DI");
            }
            else
            {
                // Create LibraryChangeManager for real-time Dealer updates
                var libraryChangeManager = _session.Dealer != null
                    ? new LibraryChangeManager(_session.Dealer, _logger)
                    : null;

                // Create SpotifyLibraryService using unified MetadataDatabase
                _libraryService = new SpotifyLibraryService(
                    metadataDatabase,
                    _session.SpClient,
                    _session,
                    libraryChangeManager,
                    _serviceProvider?.GetService<IExtendedMetadataClient>(),
                    _logger);

                // Subscribe to sync progress
                _subscriptions.Add(_libraryService.SyncProgress.Subscribe(OnSyncProgress));

                _ui.AddLog("INF", "Library services initialized (using unified database)");
            }
        }
        catch (Exception ex)
        {
            _ui.AddLog("WRN", $"Library services initialization failed: {ex.Message}");
        }

        // Initialize audio pipeline for local playback
        try
        {
            var metadataDatabase = _serviceProvider?.GetService<IMetadataDatabase>();
            var cacheService = _serviceProvider?.GetService<ICacheService>();

            _audioPipeline = AudioPipelineFactory.CreateSpotifyPipeline(
                _session,
                _session.SpClient,
                _httpClient,
                options: AudioPipelineOptions.Default,
                metadataDatabase: metadataDatabase,
                cacheService: cacheService,
                extendedMetadataClient: null,  // Created by factory from metadataDatabase
                contextCache: null,            // Created by factory (or use DI in production)
                deviceId: _session.Config.DeviceId,
                eventService: _session.Events,
                commandHandler: _session.CommandHandler,
                deviceStateManager: _session.DeviceState,
                logger: _logger);

            // Subscribe to local playback state changes
            _subscriptions.Add(_audioPipeline.StateChanges.Subscribe(OnLocalPlaybackStateChanged));

            // Enable bidirectional mode
            if (_session.PlaybackState != null)
            {
                _session.PlaybackState.EnableBidirectionalMode(
                    _audioPipeline,
                    _session.SpClient,
                    _session);
                _ui.AddLog("INF", "Audio pipeline initialized - bidirectional playback enabled!");
            }
            else
            {
                _ui.AddLog("INF", "Audio pipeline initialized - playback ready!");
            }
        }
        catch (Exception ex)
        {
            _ui.AddLog("WRN", $"Audio pipeline initialization failed: {ex.Message}");
        }

        _ui.AddLog("INF", "Type 'help' for available commands");

        // Subscribe to all Connect events
        SubscribeToCommandEvents();
        SubscribeToPlaybackStateEvents();
        SubscribeToConnectionEvents();
        SubscribeToVolumeChanges();

        // Update initial device state
        _ui.UpdateDevice(
            _session.Config.DeviceName,
            _session.Config.DeviceId,
            _session.DeviceState?.IsActive ?? false);

        // Update initial volume
        var initialVolume = _session.GetVolumePercentage() ?? 50;
        _ui.UpdateVolume(initialVolume);

        // Set up command handler
        _ui.SetCommandHandler(HandleCommandAsync);

        // Run the UI (this blocks until exit)
        await _ui.RunAsync(cancellationToken);
    }

    private async Task<bool> HandleCommandAsync(string input, CancellationToken cancellationToken)
    {
        var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return false;

        var command = parts[0].ToLower();

        switch (command)
        {
            case "help":
                ShowHelp();
                return false;

            case "device":
                await HandleDeviceCommandAsync(parts, cancellationToken);
                return false;

            case "volume":
            case "vol":
                await HandleVolumeCommandAsync(parts, cancellationToken);
                return false;

            case "play":
                await HandlePlayCommandAsync(parts, cancellationToken);
                return false;

            case "pause":
                if (_audioPipeline != null)
                {
                    await _audioPipeline.PauseAsync(cancellationToken);
                    _ui.AddLog("INF", "Paused");
                }
                else
                {
                    _ui.AddLog("ERR", "Audio pipeline not available");
                }
                return false;

            case "resume":
                if (_audioPipeline != null)
                {
                    await _audioPipeline.ResumeAsync(cancellationToken);
                    _ui.AddLog("INF", "Resumed");
                }
                else
                {
                    _ui.AddLog("ERR", "Audio pipeline not available");
                }
                return false;

            case "seek":
                await HandleSeekCommandAsync(parts, cancellationToken);
                return false;

            case "next":
                if (_audioPipeline != null)
                {
                    await _audioPipeline.SkipNextAsync(cancellationToken);
                    _ui.AddLog("INF", "Skipped to next track");
                }
                return false;

            case "prev":
                if (_audioPipeline != null)
                {
                    await _audioPipeline.SkipPreviousAsync(cancellationToken);
                    _ui.AddLog("INF", "Skipped to previous track");
                }
                return false;

            case "sync":
                await HandleSyncCommandAsync(parts, cancellationToken);
                return false;

            case "library":
            case "lib":
                await HandleLibraryCommandAsync(parts, cancellationToken);
                return false;

            case "playlists":
            case "pl":
                await HandlePlaylistsCommandAsync(cancellationToken);
                return false;

            case "view-context":
            case "vc":
                await HandleViewContextCommandAsync(cancellationToken);
                return false;

            case "search":
            case "s":
                await HandleSearchCommandAsync(parts, cancellationToken);
                return false;

            case "quit":
            case "exit":
            case "q":
                _ui.AddLog("INF", "Shutting down...");
                return true;

            default:
                _ui.AddLog("WRN", $"Unknown command: {command}. Type 'help' for available commands.");
                return false;
        }
    }

    private async Task HandlePlayCommandAsync(string[] parts, CancellationToken cancellationToken)
    {
        if (_audioPipeline == null)
        {
            _ui.AddLog("ERR", "Audio pipeline not available");
            return;
        }

        if (parts.Length < 2)
        {
            _ui.AddLog("WRN", "Usage: play <spotify:track:xxx> or <file_path.mp3>");
            return;
        }

        // Join remaining parts in case file path has spaces, and trim any quotes
        var uri = string.Join(" ", parts.Skip(1)).Trim('"');
        _ui.AddLog("INF", $"Loading {uri}...");

        try
        {
            // Check if it's a local file (file:// URI or file path)
            var isLocalFile = uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ||
                              Path.IsPathRooted(uri) ||
                              File.Exists(uri);

            // Check if it's a Spotify context (playlist, album, artist, show)
            var isContext = !isLocalFile && (
                            uri.Contains(":playlist:") || uri.Contains("/playlist/") ||
                            uri.Contains(":album:") || uri.Contains("/album/") ||
                            uri.Contains(":artist:") || uri.Contains("/artist/") ||
                            uri.Contains(":show:") || uri.Contains("/show/"));

            var command = new PlayCommand
            {
                Endpoint = "play",
                MessageIdent = "local",
                MessageId = 0,
                SenderDeviceId = _session.Config.DeviceId,
                Key = "local/0",
                TrackUri = isContext ? null : uri,
                ContextUri = isContext ? uri : null
            };

            await _audioPipeline.PlayAsync(command, cancellationToken);
        }
        catch (Exception ex)
        {
            _ui.AddLog("ERR", $"Playback failed: {ex.Message}");
        }
    }

    private async Task HandleSeekCommandAsync(string[] parts, CancellationToken cancellationToken)
    {
        if (_audioPipeline == null)
        {
            _ui.AddLog("ERR", "Audio pipeline not available");
            return;
        }

        if (parts.Length < 2 || !int.TryParse(parts[1], out var seconds))
        {
            _ui.AddLog("WRN", "Usage: seek <seconds>");
            return;
        }

        var positionMs = seconds * 1000L;
        await _audioPipeline.SeekAsync(positionMs, cancellationToken);
        _ui.AddLog("INF", $"Seeked to {FormatTimeSpan(positionMs)}");
    }

    private Task HandleSyncCommandAsync(string[] parts, CancellationToken ct)
    {
        if (_libraryService == null)
        {
            _ui.AddLog("ERR", "Library service not initialized");
            return Task.CompletedTask;
        }

        var target = parts.Length > 1 ? parts[1].ToLower() : "all";

        _ui.AddLog("INF", $"Starting {target} sync...");

        // Run sync on background thread to avoid blocking UI
        _ = Task.Run(async () =>
        {
            try
            {
                switch (target)
                {
                    case "all":
                        await _libraryService.SyncAllAsync(ct);
                        break;
                    case "tracks":
                        await _libraryService.SyncTracksAsync(ct);
                        break;
                    case "albums":
                        await _libraryService.SyncAlbumsAsync(ct);
                        break;
                    case "artists":
                        await _libraryService.SyncArtistsAsync(ct);
                        break;
                    case "shows":
                        await _libraryService.SyncShowsAsync(ct);
                        break;
                    case "bans":
                        await _libraryService.SyncBansAsync(ct);
                        break;
                    case "artistbans":
                        await _libraryService.SyncArtistBansAsync(ct);
                        break;
                    case "listenlater":
                        await _libraryService.SyncListenLaterAsync(ct);
                        break;
                    case "ylpins":
                        await _libraryService.SyncYlPinsAsync(ct);
                        break;
                    case "enhanced":
                        await _libraryService.SyncEnhancedAsync(ct);
                        break;
                    case "playlists":
                        await _libraryService.SyncPlaylistsAsync(ct);
                        break;
                    default:
                        _ui.AddLog("WRN", "Usage: sync [all|tracks|albums|artists|shows|playlists|bans|artistbans|listenlater|ylpins|enhanced]");
                        break;
                }
                _ui.AddLog("INF", $"{target} sync completed");
            }
            catch (Exception ex)
            {
                _ui.AddLog("ERR", $"Sync failed: {ex.Message}");
            }
        }, ct);

        return Task.CompletedTask;
    }

    private async Task HandleLibraryCommandAsync(string[] parts, CancellationToken ct)
    {
        if (_libraryService == null)
        {
            _ui.AddLog("ERR", "Library service not initialized");
            return;
        }

        try
        {
            var state = await _libraryService.GetSyncStateAsync(ct);

            _ui.AddLog("INF", "--- Library Sync State ---");
            _ui.AddLog("INF", $"Tracks:      {state.Tracks.ItemCount} items, rev: {state.Tracks.Revision ?? "none"}");
            _ui.AddLog("INF", $"Albums:      {state.Albums.ItemCount} items, rev: {state.Albums.Revision ?? "none"}");
            _ui.AddLog("INF", $"Artists:     {state.Artists.ItemCount} items, rev: {state.Artists.Revision ?? "none"}");
            _ui.AddLog("INF", $"Shows:       {state.Shows.ItemCount} items, rev: {state.Shows.Revision ?? "none"}");
            _ui.AddLog("INF", $"Bans:        {state.Bans.ItemCount} items, rev: {state.Bans.Revision ?? "none"}");
            _ui.AddLog("INF", $"ArtistBans:  {state.ArtistBans.ItemCount} items, rev: {state.ArtistBans.Revision ?? "none"}");
            _ui.AddLog("INF", $"ListenLater: {state.ListenLater.ItemCount} items, rev: {state.ListenLater.Revision ?? "none"}");
            _ui.AddLog("INF", $"YlPins:      {state.YlPins.ItemCount} items, rev: {state.YlPins.Revision ?? "none"}");
            _ui.AddLog("INF", $"Enhanced:    {state.Enhanced.ItemCount} items, rev: {state.Enhanced.Revision ?? "none"}");
        }
        catch (Exception ex)
        {
            _ui.AddLog("ERR", $"Failed to get library state: {ex.Message}");
        }
    }

    private async Task HandlePlaylistsCommandAsync(CancellationToken ct)
    {
        if (_libraryService == null)
        {
            _ui.AddLog("ERR", "Library service not initialized");
            return;
        }

        var playlists = await _libraryService.GetPlaylistsAsync(ct);
        if (playlists.Count == 0)
        {
            _ui.AddLog("INF", "No playlists synced. Run 'sync playlists' first.");
            return;
        }

        // Pause live UI to show interactive prompt
        _ui.PauseLiveRendering();

        try
        {
            while (true)
            {
                AnsiConsole.Clear();

                // Build selection choices grouped by folder
                var prompt = new SelectionPrompt<PlaylistChoice>()
                    .Title("[cyan]Select a playlist[/]")
                    .PageSize(15)
                    .HighlightStyle(new Style(Color.Cyan1))
                    .UseConverter(c => c.Display);

                // Add back option first
                prompt.AddChoice(new PlaylistChoice { Display = "[dim]← Back[/]", IsBack = true });

                // Group by folder
                var grouped = playlists
                    .GroupBy(p => string.IsNullOrEmpty(p.FolderPath) ? "Playlists" : p.FolderPath)
                    .OrderBy(g => g.Key);

                foreach (var group in grouped)
                {
                    prompt.AddChoiceGroup(
                        new PlaylistChoice { Display = $"[yellow]{Markup.Escape(group.Key)}[/]", IsFolder = true },
                        group.Select(p => new PlaylistChoice
                        {
                            Uri = p.Uri,
                            Name = p.Name,
                            Display = $"{Markup.Escape(p.Name)} [dim]({p.TrackCount} tracks)[/]",
                            IsFolder = false
                        }));
                }

                var selected = AnsiConsole.Prompt(prompt);

                if (selected.IsBack)
                {
                    // User selected back - exit browser
                    return;
                }

                if (selected.IsFolder)
                {
                    // Folder selected - do nothing, let user select again
                    continue;
                }

                if (string.IsNullOrEmpty(selected.Uri))
                    continue;

                // Find the full playlist info
                var selectedPlaylist = playlists.FirstOrDefault(p => p.Uri == selected.Uri);
                if (selectedPlaylist == null)
                    continue;

                // Show action menu
                AnsiConsole.Clear();
                var actionPrompt = new SelectionPrompt<string>()
                    .Title($"[cyan]{Markup.Escape(selectedPlaylist.Name)}[/]")
                    .HighlightStyle(new Style(Color.Cyan1))
                    .AddChoices("Play", "View Details", "← Back");

                var action = AnsiConsole.Prompt(actionPrompt);

                switch (action)
                {
                    case "Play":
                        await PlayPlaylistAsync(selectedPlaylist.Uri, ct);
                        return; // Exit after starting playback

                    case "View Details":
                        var shouldExit = await ShowPlaylistDetailsAsync(selectedPlaylist, ct);
                        if (shouldExit)
                            return;
                        break;

                    case "← Back":
                        // Go back to playlist list
                        break;
                }
            }
        }
        catch (Exception ex) when (ex.Message.Contains("cancel", StringComparison.OrdinalIgnoreCase) || ex is OperationCanceledException)
        {
            // User cancelled - this is fine
        }
        catch (Exception ex)
        {
            _ui.AddLog("ERR", $"Failed to browse playlists: {ex.Message}");
        }
        finally
        {
            _ui.ResumeLiveRendering();
        }
    }

    private async Task PlayPlaylistAsync(string playlistUri, CancellationToken ct)
    {
        if (_audioPipeline == null)
        {
            _ui.AddLog("ERR", "Audio pipeline not available");
            return;
        }

        _ui.AddLog("INF", $"Playing playlist: {playlistUri}");

        var command = new PlayCommand
        {
            Endpoint = "play",
            MessageIdent = "local",
            MessageId = 0,
            SenderDeviceId = _session.Config.DeviceId,
            Key = "local/playlist",
            ContextUri = playlistUri
        };

        await _audioPipeline.PlayAsync(command, ct);
    }

    /// <summary>
    /// Handles the search command - searches Spotify for tracks, artists, albums, playlists.
    /// </summary>
    private async Task HandleSearchCommandAsync(string[] parts, CancellationToken ct)
    {
        if (parts.Length < 2)
        {
            _ui.AddLog("WRN", "Usage: search <query>");
            return;
        }

        var query = string.Join(" ", parts.Skip(1));
        _ui.AddLog("INF", $"Searching for '{query}'...");

        try
        {
            var results = await _session.Pathfinder.SearchAsync(query, limit: 10, cancellationToken: ct);

            if (results.Items.Count == 0)
            {
                _ui.AddLog("INF", "No results found.");
                return;
            }

            _ui.AddLog("INF", $"Found {results.Items.Count} results. Showing selection...");

            // Pause live UI to show interactive prompt
            _ui.PauseLiveRendering();

            try
            {
                AnsiConsole.Clear();

                // Build selection choices
                var prompt = new SelectionPrompt<SearchResultChoice>()
                    .Title($"[cyan]Search Results for \"{Markup.Escape(query)}\"[/]")
                    .PageSize(15)
                    .HighlightStyle(new Style(Color.Cyan1))
                    .UseConverter(c => c.Display);

                // Add back option first
                prompt.AddChoice(new SearchResultChoice { Display = "[dim]← Back[/]", IsBack = true });

                // Add results grouped by type
                var byType = results.Items.GroupBy(i => i.Type).OrderBy(g => g.Key);
                foreach (var group in byType)
                {
                    var typeLabel = group.Key switch
                    {
                        Wavee.Core.Http.Pathfinder.SearchResultType.Track => "[green]Tracks[/]",
                        Wavee.Core.Http.Pathfinder.SearchResultType.Artist => "[yellow]Artists[/]",
                        Wavee.Core.Http.Pathfinder.SearchResultType.Album => "[blue]Albums[/]",
                        Wavee.Core.Http.Pathfinder.SearchResultType.Playlist => "[magenta]Playlists[/]",
                        _ => "[dim]Other[/]"
                    };

                    var typePrefix = group.Key switch
                    {
                        Wavee.Core.Http.Pathfinder.SearchResultType.Track => "[green]♪[/]",
                        Wavee.Core.Http.Pathfinder.SearchResultType.Artist => "[yellow]★[/]",
                        Wavee.Core.Http.Pathfinder.SearchResultType.Album => "[blue]▣[/]",
                        Wavee.Core.Http.Pathfinder.SearchResultType.Playlist => "[magenta]≡[/]",
                        _ => " "
                    };

                    prompt.AddChoiceGroup(
                        new SearchResultChoice { Display = typeLabel, IsBack = true },
                        group.Select(item => new SearchResultChoice
                        {
                            Uri = item.Uri,
                            Name = item.Name,
                            Display = $"{typePrefix} {Markup.Escape(item.GetDisplayString())}",
                            Type = item.Type
                        }));
                }

                var selected = AnsiConsole.Prompt(prompt);

                if (selected.IsBack || string.IsNullOrEmpty(selected.Uri))
                {
                    return;
                }

                // Play the selected item
                await PlaySearchResultAsync(selected.Uri, selected.Type, ct);
            }
            finally
            {
                _ui.ResumeLiveRendering();
            }
        }
        catch (SpClientException ex)
        {
            _ui.AddLog("ERR", $"Search failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            _ui.AddLog("ERR", $"Search error: {ex.Message}");
        }
    }

    /// <summary>
    /// Plays a search result.
    /// </summary>
    private async Task PlaySearchResultAsync(string uri, Wavee.Core.Http.Pathfinder.SearchResultType type, CancellationToken ct)
    {
        if (_audioPipeline == null)
        {
            _ui.AddLog("ERR", "Audio pipeline not available");
            return;
        }

        var typeLabel = type switch
        {
            Wavee.Core.Http.Pathfinder.SearchResultType.Track => "track",
            Wavee.Core.Http.Pathfinder.SearchResultType.Artist => "artist",
            Wavee.Core.Http.Pathfinder.SearchResultType.Album => "album",
            Wavee.Core.Http.Pathfinder.SearchResultType.Playlist => "playlist",
            _ => "item"
        };

        _ui.AddLog("INF", $"Playing {typeLabel}: {uri}");

        // For tracks, use TrackUri; for contexts (artist, album, playlist), use ContextUri
        var isTrack = type == Wavee.Core.Http.Pathfinder.SearchResultType.Track;

        var command = new PlayCommand
        {
            Endpoint = "play",
            MessageIdent = "local",
            MessageId = 0,
            SenderDeviceId = _session.Config.DeviceId,
            Key = $"local/search/{typeLabel}",
            TrackUri = isTrack ? uri : null,
            ContextUri = isTrack ? null : uri
        };

        await _audioPipeline.PlayAsync(command, ct);
    }

    /// <summary>
    /// Helper class for search result selection prompt.
    /// </summary>
    private sealed class SearchResultChoice
    {
        public string? Uri { get; init; }
        public string? Name { get; init; }
        public required string Display { get; init; }
        public bool IsBack { get; init; }
        public Wavee.Core.Http.Pathfinder.SearchResultType Type { get; init; }
    }

    /// <summary>
    /// Shows playlist details with track list.
    /// Returns true if playback started (caller should exit browser).
    /// </summary>
    private async Task<bool> ShowPlaylistDetailsAsync(SpotifyPlaylist playlist, CancellationToken ct)
    {
        AnsiConsole.Clear();

        // Show playlist metadata panel
        var infoRows = new List<IRenderable>
        {
            new Markup($"[bold white]{Markup.Escape(playlist.Name)}[/]"),
            new Markup($"[dim]by[/] [cyan]{Markup.Escape(playlist.OwnerName ?? playlist.OwnerId ?? "Unknown")}[/]"),
            new Markup($"[dim]{playlist.TrackCount} tracks[/]")
        };

        if (!string.IsNullOrEmpty(playlist.Description))
        {
            infoRows.Add(new Text(""));
            infoRows.Add(new Markup($"[dim italic]{Markup.Escape(playlist.Description)}[/]"));
        }

        var panel = new Panel(new Rows(infoRows))
            .Header("[green] Playlist Details [/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Green)
            .Expand();

        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();

        // Fetch track URIs via context-resolve API
        AnsiConsole.MarkupLine("[dim]Loading tracks...[/]");

        Protocol.Context.Context context;
        try
        {
            context = await _session.SpClient.ResolveContextAsync(playlist.Uri, ct);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to load tracks: {Markup.Escape(ex.Message)}[/]");
            AnsiConsole.MarkupLine("[dim]Press any key to go back...[/]");
            System.Console.ReadKey(intercept: true);
            return false;
        }

        // Collect track URIs from context
        var trackUris = new List<string>();
        foreach (var page in context.Pages)
        {
            foreach (var track in page.Tracks)
            {
                if (!string.IsNullOrEmpty(track.Uri))
                    trackUris.Add(track.Uri);
            }
        }

        // Fetch track metadata via extended metadata API + cache
        AnsiConsole.MarkupLine($"[dim]Fetching metadata for {trackUris.Count} items...[/]");

        var tracks = new List<(string Uri, string Title, string Artist, int Index)>();
        var extendedMetadataClient = _serviceProvider?.GetService<IExtendedMetadataClient>();
        var cacheService = _serviceProvider?.GetService<ICacheService>();

        if (extendedMetadataClient != null && cacheService != null && trackUris.Count > 0)
        {
            try
            {
                // Only fetch tracks (episodes not cached yet)
                var trackOnlyUris = trackUris.Where(u => u.StartsWith("spotify:track:")).ToList();

                // Batch fetch track metadata (populates cache)
                if (trackOnlyUris.Count > 0)
                {
                    var trackRequests = trackOnlyUris.Select(uri => (
                        uri,
                        (IEnumerable<Wavee.Protocol.ExtendedMetadata.ExtensionKind>)
                            new[] { Wavee.Protocol.ExtendedMetadata.ExtensionKind.TrackV4 }
                    ));
                    await extendedMetadataClient.GetBatchedExtensionsAsync(trackRequests, ct);
                }

                // Read from cache
                var cachedTracks = await cacheService.GetTracksAsync(trackOnlyUris, ct);

                // Map results preserving order
                for (int i = 0; i < trackUris.Count; i++)
                {
                    var uri = trackUris[i];
                    var title = "Unknown";
                    var artist = "Unknown";

                    if (cachedTracks.TryGetValue(uri, out var trackEntry))
                    {
                        title = trackEntry.Title ?? "Unknown";
                        artist = trackEntry.Artist ?? "Unknown";
                    }
                    else if (uri.StartsWith("spotify:episode:"))
                    {
                        // Episode - show as podcast episode
                        title = $"Episode: {ExtractTrackId(uri)}";
                        artist = "Podcast";
                    }

                    tracks.Add((uri, title, artist, i));
                }
            }
            catch (Exception ex)
            {
                _ui.AddLog("WRN", $"Failed to fetch metadata: {ex.Message}");
                // Fall back to URIs only
                for (int i = 0; i < trackUris.Count; i++)
                {
                    tracks.Add((trackUris[i], ExtractTrackId(trackUris[i]), "Unknown", i));
                }
            }
        }
        else
        {
            // No metadata client - just use URIs
            for (int i = 0; i < trackUris.Count; i++)
            {
                tracks.Add((trackUris[i], ExtractTrackId(trackUris[i]), "Unknown", i));
            }
        }

        while (true)
        {
            AnsiConsole.Clear();
            AnsiConsole.Write(panel);
            AnsiConsole.WriteLine();

            // Build track selection prompt
            var trackPrompt = new SelectionPrompt<TrackChoice>()
                .Title($"[cyan]{tracks.Count} tracks[/]")
                .PageSize(20)
                .HighlightStyle(new Style(Color.Cyan1))
                .UseConverter(c => c.Display);

            trackPrompt.AddChoice(new TrackChoice { Display = "[dim]← Back[/]", IsBack = true });
            trackPrompt.AddChoice(new TrackChoice { Display = "[green]▶ Play All[/]", IsPlayAll = true });

            foreach (var (uri, title, artist, idx) in tracks)
            {
                trackPrompt.AddChoice(new TrackChoice
                {
                    Uri = uri,
                    Display = $"{idx + 1}. {Markup.Escape(title)} [dim]- {Markup.Escape(artist)}[/]",
                    Index = idx
                });
            }

            var selected = AnsiConsole.Prompt(trackPrompt);

            if (selected.IsBack)
            {
                return false; // Go back to action menu
            }

            if (selected.IsPlayAll)
            {
                await PlayPlaylistAsync(playlist.Uri, ct);
                return true; // Exit browser
            }

            if (!string.IsNullOrEmpty(selected.Uri))
            {
                // Play context starting at this track
                await PlayTrackInContextAsync(playlist.Uri, selected.Uri, selected.Index, ct);
                return true; // Exit browser
            }
        }
    }

    /// <summary>
    /// Plays a specific track within a context (playlist/album).
    /// </summary>
    private async Task PlayTrackInContextAsync(string contextUri, string trackUri, int trackIndex, CancellationToken ct)
    {
        if (_audioPipeline == null)
        {
            _ui.AddLog("ERR", "Audio pipeline not available");
            return;
        }

        _ui.AddLog("INF", $"Playing track {trackIndex + 1} in context");

        var command = new PlayCommand
        {
            Endpoint = "play",
            MessageIdent = "local",
            MessageId = 0,
            SenderDeviceId = _session.Config.DeviceId,
            Key = "local/playlist",
            ContextUri = contextUri,
            TrackUri = trackUri,
            SkipToIndex = trackIndex
        };

        await _audioPipeline.PlayAsync(command, ct);
    }

    /// <summary>
    /// Shows the current playback context with all tracks, highlighting the currently playing track.
    /// </summary>
    private async Task HandleViewContextCommandAsync(CancellationToken ct)
    {
        // Get current context from UI state
        var contextUri = _ui.CurrentContextUri;
        var currentTrackUid = _ui.CurrentTrackUid;

        if (string.IsNullOrEmpty(contextUri))
        {
            _ui.AddLog("INF", "No playlist or album context is currently playing");
            return;
        }

        _ui.PauseLiveRendering();

        try
        {
            await ShowContextTracksAsync(contextUri, currentTrackUid, ct);
        }
        catch (Exception ex) when (ex.Message.Contains("cancel", StringComparison.OrdinalIgnoreCase)
                                   || ex is OperationCanceledException)
        {
            // User cancelled - this is fine
        }
        catch (Exception ex)
        {
            _ui.AddLog("ERR", $"Failed to show context: {ex.Message}");
        }
        finally
        {
            _ui.ResumeLiveRendering();
        }
    }

    /// <summary>
    /// Displays all tracks in a context with the currently playing track highlighted.
    /// </summary>
    private async Task ShowContextTracksAsync(string contextUri, string? currentTrackUid, CancellationToken ct)
    {
        AnsiConsole.Clear();
        AnsiConsole.MarkupLine("[dim]Loading tracks...[/]");

        // Resolve context to get all tracks with UIDs
        Protocol.Context.Context context;
        try
        {
            context = await _session.SpClient.ResolveContextAsync(contextUri, ct);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to load context: {Markup.Escape(ex.Message)}[/]");
            AnsiConsole.MarkupLine("[dim]Press any key to go back...[/]");
            System.Console.ReadKey(intercept: true);
            return;
        }

        // Extract track info from context pages (including UIDs)
        var trackInfos = new List<(string Uri, string? Uid, int Index)>();
        int index = 0;
        foreach (var page in context.Pages)
        {
            foreach (var track in page.Tracks)
            {
                if (!string.IsNullOrEmpty(track.Uri))
                {
                    trackInfos.Add((track.Uri, track.Uid, index++));
                }
            }
        }

        if (trackInfos.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No tracks found in context[/]");
            AnsiConsole.MarkupLine("[dim]Press any key to go back...[/]");
            System.Console.ReadKey(intercept: true);
            return;
        }

        // Fetch metadata for display
        AnsiConsole.MarkupLine($"[dim]Fetching metadata for {trackInfos.Count} tracks...[/]");
        var tracks = await FetchContextTrackMetadataAsync(trackInfos, ct);

        // Find the index of currently playing track by UID
        int currentIndex = -1;
        if (!string.IsNullOrEmpty(currentTrackUid))
        {
            currentIndex = tracks.FindIndex(t =>
                string.Equals(t.Uid, currentTrackUid, StringComparison.Ordinal));
        }

        // Show interactive track list
        await ShowContextTrackListAsync(contextUri, tracks, currentIndex, ct);
    }

    /// <summary>
    /// Fetches metadata for context track list display.
    /// </summary>
    private async Task<List<ContextTrackInfo>> FetchContextTrackMetadataAsync(
        List<(string Uri, string? Uid, int Index)> trackInfos,
        CancellationToken ct)
    {
        var results = new List<ContextTrackInfo>();
        var extendedMetadataClient = _serviceProvider?.GetService<IExtendedMetadataClient>();
        var cacheService = _serviceProvider?.GetService<ICacheService>();

        if (extendedMetadataClient != null && cacheService != null)
        {
            // Batch fetch track metadata
            var trackUris = trackInfos
                .Where(t => t.Uri.StartsWith("spotify:track:"))
                .Select(t => t.Uri)
                .ToList();

            if (trackUris.Count > 0)
            {
                try
                {
                    var requests = trackUris.Select(uri => (
                        uri,
                        (IEnumerable<Wavee.Protocol.ExtendedMetadata.ExtensionKind>)
                            new[] { Wavee.Protocol.ExtendedMetadata.ExtensionKind.TrackV4 }
                    ));
                    await extendedMetadataClient.GetBatchedExtensionsAsync(requests, ct);
                }
                catch (Exception ex)
                {
                    _ui.AddLog("WRN", $"Metadata fetch warning: {ex.Message}");
                }
            }

            // Read from cache
            var cachedTracks = await cacheService.GetTracksAsync(
                trackInfos.Select(t => t.Uri), ct);

            foreach (var (uri, uid, idx) in trackInfos)
            {
                string title = "Unknown";
                string artist = "Unknown";

                if (cachedTracks.TryGetValue(uri, out var cached))
                {
                    title = cached.Title ?? "Unknown";
                    artist = cached.Artist ?? "Unknown";
                }
                else if (uri.StartsWith("spotify:episode:"))
                {
                    title = $"Episode: {ExtractTrackId(uri)}";
                    artist = "Podcast";
                }

                results.Add(new ContextTrackInfo(uri, uid, title, artist, idx));
            }
        }
        else
        {
            // Fallback: use URI as identifier
            foreach (var (uri, uid, idx) in trackInfos)
            {
                results.Add(new ContextTrackInfo(uri, uid, ExtractTrackId(uri), "Unknown", idx));
            }
        }

        return results;
    }

    /// <summary>
    /// Shows the track list with currently playing track highlighted.
    /// </summary>
    private async Task ShowContextTrackListAsync(
        string contextUri,
        List<ContextTrackInfo> tracks,
        int currentIndex,
        CancellationToken ct)
    {
        AnsiConsole.Clear();

        // Build context type display
        var contextType = contextUri.Contains(":playlist:") ? "Playlist"
                        : contextUri.Contains(":album:") ? "Album"
                        : contextUri.Contains(":artist:") ? "Artist"
                        : contextUri.Contains(":show:") ? "Show"
                        : "Context";

        var headerPanel = new Panel(new Markup($"[bold]{contextType}[/] - {tracks.Count} tracks"))
            .Header("[green] NOW PLAYING FROM [/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Green)
            .Expand();

        AnsiConsole.Write(headerPanel);
        AnsiConsole.WriteLine();

        // Build track selection prompt
        var trackPrompt = new SelectionPrompt<ContextTrackChoice>()
            .Title("[cyan]Select a track to play[/]")
            .PageSize(20)
            .HighlightStyle(new Style(Color.Cyan1))
            .UseConverter(c => c.Display);

        // Add back option
        trackPrompt.AddChoice(new ContextTrackChoice { Display = "[dim]← Back[/]", IsBack = true });

        // Add tracks with currently playing indicator
        foreach (var track in tracks)
        {
            var isPlaying = track.Index == currentIndex;
            var playingIndicator = isPlaying ? "[green]>[/] " : "   ";
            var trackStyle = isPlaying ? "[bold green]" : "";
            var trackStyleEnd = isPlaying ? "[/]" : "";

            var display = $"{playingIndicator}{track.Index + 1}. {trackStyle}{Markup.Escape(track.Title)}{trackStyleEnd} [dim]- {Markup.Escape(track.Artist)}[/]";

            trackPrompt.AddChoice(new ContextTrackChoice
            {
                Uri = track.Uri,
                Uid = track.Uid,
                Display = display,
                Index = track.Index
            });
        }

        var selected = AnsiConsole.Prompt(trackPrompt);

        if (selected.IsBack)
        {
            return;
        }

        // Play selected track in context
        if (!string.IsNullOrEmpty(selected.Uri))
        {
            await PlayTrackInContextAsync(contextUri, selected.Uri, selected.Index, ct);
        }
    }

    /// <summary>
    /// Track info for context view display.
    /// </summary>
    private record ContextTrackInfo(string Uri, string? Uid, string Title, string Artist, int Index);

    /// <summary>
    /// Selection choice for context track list.
    /// </summary>
    private record ContextTrackChoice
    {
        public string? Uri { get; init; }
        public string? Uid { get; init; }
        public required string Display { get; init; }
        public int Index { get; init; }
        public bool IsBack { get; init; }
    }

    /// <summary>
    /// Helper record for playlist selection choices.
    /// </summary>
    private record PlaylistChoice
    {
        public string? Uri { get; init; }
        public string? Name { get; init; }
        public required string Display { get; init; }
        public bool IsFolder { get; init; }
        public bool IsBack { get; init; }
    }

    /// <summary>
    /// Helper record for track selection choices.
    /// </summary>
    private record TrackChoice
    {
        public string? Uri { get; init; }
        public required string Display { get; init; }
        public bool IsBack { get; init; }
        public bool IsPlayAll { get; init; }
        public int Index { get; init; }
    }

    private void OnSyncProgress(SyncProgress progress)
    {
        var progressText = progress.Total > 0
            ? $"({progress.Current}/{progress.Total})"
            : $"({progress.Current})";
        _ui.AddLog("SYN", $"[{progress.CollectionType}] {progress.Message} {progressText}");
    }

    private void OnLocalPlaybackStateChanged(LocalPlaybackState state)
    {
        _ui.UpdateNowPlaying(
            state.TrackTitle,
            state.TrackArtist,
            state.TrackAlbum,
            state.PositionMs,
            state.DurationMs,
            state.IsPlaying,
            state.IsPaused);

        // Update context info for "View Playlist" feature
        _ui.UpdateContext(state.ContextUri, state.TrackUid);

        // Update device active status based on playback state
        // Device is active when playing or paused (track loaded)
        var deviceActive = state.IsPlaying || state.IsPaused;
        _ui.UpdateDevice(_session.Config.DeviceName, _session.Config.DeviceId, deviceActive);

        // Fetch lyrics when track changes
        if (state.TrackUri != _lastTrackUri && !string.IsNullOrEmpty(state.TrackUri))
        {
            _lastTrackUri = state.TrackUri;
            _currentLyrics = null;
            _ui.UpdateLyrics(null, state.TrackUri);

            // Fetch lyrics in background (use any available image URL)
            var imageUri = state.ImageXLargeUrl ?? state.ImageLargeUrl ?? state.ImageUrl ?? state.ImageSmallUrl;
            if (!string.IsNullOrEmpty(imageUri))
            {
                _ = FetchLyricsAsync(state.TrackUri, imageUri);
            }
        }
    }

    private async Task FetchLyricsAsync(string trackUri, string imageUri)
    {
        try
        {
            // Extract track ID from URI (spotify:track:xxx -> xxx)
            var trackId = SpotifyId.FromUri(trackUri).ToBase62();

            var response = await _session.SpClient.GetLyricsAsync(trackId, imageUri);

            if (response?.Lyrics != null &&
                response.Lyrics.IsSynced &&
                response.Lyrics.Lines.Count > 0)
            {
                _currentLyrics = response.Lyrics;
                _ui.UpdateLyrics(_currentLyrics, trackUri);
                _logger?.LogDebug("Loaded {LineCount} synced lyrics lines for {TrackId}",
                    response.Lyrics.Lines.Count, trackId);
            }
            else
            {
                _logger?.LogDebug("No synced lyrics available for {TrackId}", trackId);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug("Failed to fetch lyrics for {TrackUri}: {Error}", trackUri, ex.Message);
            // Silently fail - lyrics are optional
        }
    }

    private void ShowHelp()
    {
        _ui.AddLog("INF", "--- Commands ---");
        _ui.AddLog("INF", "play <uri>  - Play track, context, or local file");
        _ui.AddLog("INF", "search <q>  - Search for music (or 's')");
        _ui.AddLog("INF", "pause       - Pause playback");
        _ui.AddLog("INF", "resume      - Resume playback");
        _ui.AddLog("INF", "next        - Skip to next track");
        _ui.AddLog("INF", "prev        - Skip to previous track");
        _ui.AddLog("INF", "seek <sec>  - Seek to position");
        _ui.AddLog("INF", "vol [0-100] - Set or show volume");
        _ui.AddLog("INF", "device on|off - Toggle device active");
        _ui.AddLog("INF", "sync [type] - Sync library (all|tracks|albums|artists|shows|playlists|...)");
        _ui.AddLog("INF", "library     - Show library sync state");
        _ui.AddLog("INF", "playlists   - Show synced playlists");
        _ui.AddLog("INF", "view-context - View current playlist/album (or press V)");
        _ui.AddLog("INF", "quit        - Exit application");
    }

    private async Task HandleDeviceCommandAsync(string[] parts, CancellationToken cancellationToken)
    {
        if (parts.Length < 2)
        {
            _ui.AddLog("WRN", "Usage: device on|off");
            return;
        }

        var action = parts[1].ToLower();
        switch (action)
        {
            case "on":
            case "active":
                var activated = await _session.SetDeviceActiveAsync(true, cancellationToken);
                if (activated)
                {
                    _ui.AddLog("INF", "Device is now active");
                    _ui.UpdateDevice(_session.Config.DeviceName, _session.Config.DeviceId, true);
                }
                else
                    _ui.AddLog("ERR", "Failed to activate device");
                break;

            case "off":
            case "inactive":
                var deactivated = await _session.SetDeviceActiveAsync(false, cancellationToken);
                if (deactivated)
                {
                    _ui.AddLog("INF", "Device is now inactive");
                    _ui.UpdateDevice(_session.Config.DeviceName, _session.Config.DeviceId, false);
                }
                else
                    _ui.AddLog("ERR", "Failed to deactivate device");
                break;

            default:
                _ui.AddLog("WRN", $"Unknown device action: {action}. Use 'on' or 'off'.");
                break;
        }
    }

    private async Task HandleVolumeCommandAsync(string[] parts, CancellationToken cancellationToken)
    {
        if (parts.Length == 1)
        {
            var volumePct = _session.GetVolumePercentage() ?? 0;
            _ui.AddLog("INF", $"Current volume: {volumePct}%");
            return;
        }

        var arg = parts[1].ToLower();

        if (arg == "+" || arg == "up")
        {
            await AdjustVolumeAsync(+5, cancellationToken);
        }
        else if (arg == "-" || arg == "down")
        {
            await AdjustVolumeAsync(-5, cancellationToken);
        }
        else if (int.TryParse(arg, out var pct))
        {
            var result = await _session.SetVolumePercentageAsync(pct, cancellationToken);
            if (result)
            {
                _ui.AddLog("INF", $"Volume set to {pct}%");
                _ui.UpdateVolume(pct);
            }
            else
                _ui.AddLog("ERR", "Failed to set volume");
        }
        else
        {
            _ui.AddLog("WRN", "Usage: vol [0-100] or vol +/-");
        }
    }

    private async Task AdjustVolumeAsync(int delta, CancellationToken cancellationToken)
    {
        var currentPct = _session.GetVolumePercentage() ?? 50;
        var newPct = Math.Clamp(currentPct + delta, 0, 100);

        var result = await _session.SetVolumePercentageAsync(newPct, cancellationToken);
        if (result)
        {
            var direction = delta > 0 ? "Increased" : "Decreased";
            _ui.AddLog("INF", $"{direction} volume to {newPct}%");
            _ui.UpdateVolume(newPct);
        }
        else
        {
            _ui.AddLog("ERR", "Failed to adjust volume");
        }
    }

    private void SubscribeToCommandEvents()
    {
        if (_session.CommandHandler == null)
            return;

        _subscriptions.Add(_session.CommandHandler.PlayCommands.Subscribe(cmd =>
        {
            var details = $"Track: {cmd.TrackUri ?? "N/A"}, Context: {cmd.ContextUri ?? "N/A"}";
            _ui.AddLog("CMD", $"Play - {details}");
        }));

        _subscriptions.Add(_session.CommandHandler.PauseCommands.Subscribe(_ =>
            _ui.AddLog("CMD", "Pause")));

        _subscriptions.Add(_session.CommandHandler.ResumeCommands.Subscribe(_ =>
            _ui.AddLog("CMD", "Resume")));

        _subscriptions.Add(_session.CommandHandler.SeekCommands.Subscribe(cmd =>
            _ui.AddLog("CMD", $"Seek to {FormatTimeSpan(cmd.PositionMs)}")));

        _subscriptions.Add(_session.CommandHandler.ShuffleCommands.Subscribe(cmd =>
        {
            _ui.AddLog("CMD", $"Shuffle {(cmd.Enabled ? "ON" : "OFF")}");
            _ui.UpdateOptions(cmd.Enabled, false, false); // TODO: track all options
        }));

        _subscriptions.Add(_session.CommandHandler.SkipNextCommands.Subscribe(_ =>
            _ui.AddLog("CMD", "Skip Next")));

        _subscriptions.Add(_session.CommandHandler.SkipPrevCommands.Subscribe(_ =>
            _ui.AddLog("CMD", "Skip Prev")));

        _subscriptions.Add(_session.CommandHandler.TransferCommands.Subscribe(_ =>
        {
            _ui.AddLog("CMD", "Transfer - Playback transferred to this device");
            _ui.UpdateDevice(_session.Config.DeviceName, _session.Config.DeviceId, true);
        }));
    }

    private void SubscribeToPlaybackStateEvents()
    {
        if (_session.PlaybackState == null)
            return;

        _subscriptions.Add(_session.PlaybackState.TrackChanged.Subscribe(state =>
        {
            if (state.Track != null)
            {
                _ui.UpdateNowPlaying(
                    state.Track.Title,
                    state.Track.Artist,
                    state.Track.Album,
                    state.PositionMs,
                    state.DurationMs,
                    state.Status == PlaybackStatus.Playing,
                    state.Status == PlaybackStatus.Paused);

                // Fetch lyrics for cluster state changes (when playing from other devices)
                if (state.Track.Uri != _lastTrackUri)
                {
                    _lastTrackUri = state.Track.Uri;
                    _currentLyrics = null;
                    _ui.UpdateLyrics(null, state.Track.Uri);

                    // Use any available image URL from track metadata
                    var imageUri = state.Track.ImageXLargeUrl ?? state.Track.ImageLargeUrl ??
                                   state.Track.ImageUrl ?? state.Track.ImageSmallUrl;
                    if (!string.IsNullOrEmpty(imageUri))
                    {
                        _ = FetchLyricsAsync(state.Track.Uri, imageUri);
                    }
                }
            }
        }));

        _subscriptions.Add(_session.PlaybackState.PlaybackStatusChanged.Subscribe(state =>
        {
            var isPlaying = state.Status == PlaybackStatus.Playing;
            var isPaused = state.Status == PlaybackStatus.Paused;
            _ui.UpdateNowPlaying(null, null, null, state.PositionMs, state.DurationMs, isPlaying, isPaused);
        }));

        _subscriptions.Add(_session.PlaybackState.PositionChanged
            .Throttle(TimeSpan.FromMilliseconds(500))
            .Subscribe(state => _ui.UpdatePosition(state.PositionMs)));

        _subscriptions.Add(_session.PlaybackState.OptionsChanged.Subscribe(state =>
        {
            _ui.UpdateOptions(
                state.Options.Shuffling,
                state.Options.RepeatingContext,
                state.Options.RepeatingTrack);
        }));
    }

    private void SubscribeToConnectionEvents()
    {
        if (_session.Dealer == null)
            return;

        _subscriptions.Add(_session.Dealer.ConnectionState.Subscribe(state =>
        {
            var status = state switch
            {
                ConnectionState.Connected => "Connected",
                ConnectionState.Connecting => "Connecting...",
                ConnectionState.Disconnected => "Disconnected",
                _ => state.ToString()
            };

            _ui.UpdateDealerStatus(status);
        }));
    }

    private void SubscribeToVolumeChanges()
    {
        if (_session.DeviceState?.Volume == null)
            return;

        var subscription = _session.DeviceState.Volume
            .DistinctUntilChanged()
            .Subscribe(volume =>
            {
                var pct = ConnectStateHelpers.VolumeToPercentage(volume);
                _ui.UpdateVolume(pct);
            });

        _subscriptions.Add(subscription);
    }

    private static string FormatTimeSpan(long milliseconds)
    {
        var ts = TimeSpan.FromMilliseconds(milliseconds);
        return $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";
    }

    private static string ExtractTrackId(string uri)
    {
        // "spotify:track:xxx" -> "xxx"
        var parts = uri.Split(':');
        return parts.Length >= 3 ? parts[2] : uri;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        foreach (var subscription in _subscriptions)
            subscription?.Dispose();
        _subscriptions.Clear();

        if (_audioPipeline != null)
        {
            _audioPipeline.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _audioPipeline = null;
        }

        if (_libraryService != null)
        {
            _libraryService.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _libraryService = null;
        }

        _serviceProvider?.Dispose();
        _serviceProvider = null;

        _disposed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        foreach (var subscription in _subscriptions)
            subscription?.Dispose();
        _subscriptions.Clear();

        if (_audioPipeline != null)
        {
            await _audioPipeline.DisposeAsync();
            _audioPipeline = null;
        }

        if (_libraryService != null)
        {
            await _libraryService.DisposeAsync();
            _libraryService = null;
        }

        if (_serviceProvider != null)
        {
            await _serviceProvider.DisposeAsync();
            _serviceProvider = null;
        }

        _disposed = true;
    }
}
