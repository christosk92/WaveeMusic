using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Wavee.Console;

/// <summary>
/// Spectre.Console-based UI rendering engine with live display and input handling.
/// </summary>
internal sealed class SpectreUI : IDisposable
{
    private const int MaxLogEntries = 15;
    private const int RefreshIntervalMs = 100;

    // Windows Virtual Terminal constants
    private const int STD_OUTPUT_HANDLE = -11;
    private const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;
    private const uint DISABLE_NEWLINE_AUTO_RETURN = 0x0008;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    private readonly object _lock = new();
    private readonly ConcurrentQueue<LogEntry> _logEntries = new();
    private readonly StringBuilder _inputBuffer = new();

    // Playback state
    private string? _trackTitle;
    private string? _trackArtist;
    private string? _trackAlbum;
    private long _positionMs;
    private long _durationMs;
    private long _positionTimestamp; // When position was last updated
    private bool _isPlaying;
    private bool _isPaused;

    // Device state
    private string _deviceName = "Unknown";
    private string _deviceId = "";
    private bool _deviceActive;
    private string _dealerStatus = "Disconnected";
    private int _volumePercent;
    private bool _shuffleEnabled;
    private bool _repeatContext;
    private bool _repeatTrack;

    // Context state (for "View Playlist" feature)
    private string? _contextUri;
    private string? _currentTrackUid;

    // Lyrics state
    private Core.Http.Lyrics.LyricsData? _lyrics;
    private string? _lyricsTrackUri;
    private int _cachedLyricIndex = -1;

    private bool _disposed;
    private volatile bool _renderingPaused;
    private CancellationTokenSource? _renderCts;
    private Func<string, CancellationToken, Task<bool>>? _commandHandler;

    public SpectreUI()
    {
    }

    /// <summary>
    /// Sets the command handler for processing user input.
    /// </summary>
    public void SetCommandHandler(Func<string, CancellationToken, Task<bool>> handler)
    {
        _commandHandler = handler;
    }

    /// <summary>
    /// Updates the device information.
    /// </summary>
    public void UpdateDevice(string name, string id, bool active)
    {
        lock (_lock)
        {
            _deviceName = name;
            _deviceId = id;
            _deviceActive = active;
        }
    }

    /// <summary>
    /// Updates the dealer connection status.
    /// </summary>
    public void UpdateDealerStatus(string status)
    {
        lock (_lock)
        {
            _dealerStatus = status;
        }
    }

    /// <summary>
    /// Updates the now playing information.
    /// Only updates track info if non-null (preserves existing values for position-only updates).
    /// </summary>
    public void UpdateNowPlaying(string? title, string? artist, string? album, long positionMs, long durationMs, bool isPlaying, bool isPaused)
    {
        lock (_lock)
        {
            // Only update track info if provided (preserve existing for position-only updates)
            if (title != null) _trackTitle = title;
            if (artist != null) _trackArtist = artist;
            if (album != null) _trackAlbum = album;
            _positionMs = positionMs;
            _positionTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _durationMs = durationMs;
            _isPlaying = isPlaying;
            _isPaused = isPaused;
        }
    }

    /// <summary>
    /// Updates just the playback position.
    /// </summary>
    public void UpdatePosition(long positionMs)
    {
        lock (_lock)
        {
            _positionMs = positionMs;
            _positionTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
    }

    /// <summary>
    /// Clears the track info (when playback stops).
    /// </summary>
    public void ClearTrack()
    {
        lock (_lock)
        {
            _trackTitle = null;
            _trackArtist = null;
            _trackAlbum = null;
            _positionMs = 0;
            _durationMs = 0;
            _isPlaying = false;
            _isPaused = false;
        }
    }

    /// <summary>
    /// Updates the volume level.
    /// </summary>
    public void UpdateVolume(int percent)
    {
        lock (_lock)
        {
            _volumePercent = Math.Clamp(percent, 0, 100);
        }
    }

    /// <summary>
    /// Updates playback options.
    /// </summary>
    public void UpdateOptions(bool shuffle, bool repeatContext, bool repeatTrack)
    {
        lock (_lock)
        {
            _shuffleEnabled = shuffle;
            _repeatContext = repeatContext;
            _repeatTrack = repeatTrack;
        }
    }

    /// <summary>
    /// Updates the current context URI and track UID for "View Playlist" functionality.
    /// </summary>
    public void UpdateContext(string? contextUri, string? trackUid)
    {
        lock (_lock)
        {
            _contextUri = contextUri;
            _currentTrackUid = trackUid;
        }
    }

    /// <summary>
    /// Gets the current context URI (playlist/album being played).
    /// </summary>
    public string? CurrentContextUri
    {
        get { lock (_lock) { return _contextUri; } }
    }

    /// <summary>
    /// Gets the current track UID.
    /// </summary>
    public string? CurrentTrackUid
    {
        get { lock (_lock) { return _currentTrackUid; } }
    }

    /// <summary>
    /// Updates the lyrics for the current track.
    /// </summary>
    /// <param name="lyrics">Lyrics data (null to clear).</param>
    /// <param name="trackUri">Track URI these lyrics belong to.</param>
    public void UpdateLyrics(Core.Http.Lyrics.LyricsData? lyrics, string? trackUri)
    {
        lock (_lock)
        {
            // Reset cache when track changes
            if (_lyricsTrackUri != trackUri)
            {
                _cachedLyricIndex = -1;
            }
            _lyrics = lyrics;
            _lyricsTrackUri = trackUri;
        }
    }

    /// <summary>
    /// Gets the index of the current lyric line based on playback position.
    /// Uses caching to avoid O(n) search every frame.
    /// </summary>
    private int GetCurrentLyricIndex(long positionMs)
    {
        if (_lyrics?.Lines == null || _lyrics.Lines.Count == 0)
            return -1;

        var lines = _lyrics.Lines;

        // Check if we're still on the same cached line (fast path)
        if (_cachedLyricIndex >= 0 && _cachedLyricIndex < lines.Count)
        {
            var currentLine = lines[_cachedLyricIndex];
            var nextLineStart = _cachedLyricIndex < lines.Count - 1
                ? lines[_cachedLyricIndex + 1].StartTimeMilliseconds
                : long.MaxValue;

            if (positionMs >= currentLine.StartTimeMilliseconds && positionMs < nextLineStart)
            {
                return _cachedLyricIndex; // Still on same line, skip search
            }
        }

        // Full search when cache miss
        int newIndex = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].StartTimeMilliseconds <= positionMs)
                newIndex = i;
            else
                break;
        }

        _cachedLyricIndex = newIndex;
        return newIndex;
    }

    /// <summary>
    /// Adds a log entry to the log panel.
    /// </summary>
    public void AddLog(string level, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, message);
        _logEntries.Enqueue(entry);

        // Trim old entries
        while (_logEntries.Count > MaxLogEntries)
        {
            _logEntries.TryDequeue(out _);
        }
    }

    /// <summary>
    /// Pauses live rendering to allow interactive prompts.
    /// </summary>
    public void PauseLiveRendering()
    {
        _renderingPaused = true;
        System.Console.Clear();
        System.Console.CursorVisible = true;
    }

    /// <summary>
    /// Resumes live rendering after interactive prompts.
    /// </summary>
    public void ResumeLiveRendering()
    {
        _renderingPaused = false;
        System.Console.Clear();
        System.Console.CursorVisible = false;
    }

    /// <summary>
    /// Enables Windows Virtual Terminal Processing for ANSI escape sequences.
    /// </summary>
    /// <returns>True if VT processing is available (enabled or non-Windows), false otherwise.</returns>
    private static bool EnableVirtualTerminalProcessing()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return true; // Non-Windows typically supports ANSI natively

        try
        {
            var handle = GetStdHandle(STD_OUTPUT_HANDLE);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1))
                return false;

            if (!GetConsoleMode(handle, out var mode))
                return false;

            mode |= ENABLE_VIRTUAL_TERMINAL_PROCESSING | DISABLE_NEWLINE_AUTO_RETURN;
            return SetConsoleMode(handle, mode);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Runs the live UI rendering loop.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _renderCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = _renderCts.Token;

        // Initial setup
        System.Console.Clear();
        System.Console.CursorVisible = false;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Skip rendering when paused (for interactive prompts)
                if (!_renderingPaused)
                {
                    RenderLayout();
                    await HandleInputAsync(ct);
                }

                await Task.Delay(RefreshIntervalMs, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
        }
        finally
        {
            System.Console.CursorVisible = true;
        }
    }

    private void RenderLayout()
    {
        var terminalHeight = System.Console.WindowHeight;
        var terminalWidth = System.Console.WindowWidth;

        // Build the layout
        var layout = BuildLayout(terminalWidth, terminalHeight - 1);

        // Render Spectre output to a string buffer
        using var stringWriter = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Yes,
            ColorSystem = ColorSystemSupport.TrueColor,
            Out = new AnsiConsoleOutput(stringWriter)
        });
        console.Write(layout);
        var output = stringWriter.ToString();

        // Clear and rewrite (most reliable approach)
        System.Console.Clear();
        System.Console.Write(output);

        // Input line at bottom
        var input = _inputBuffer.ToString();
        System.Console.SetCursorPosition(0, terminalHeight - 1);
        System.Console.Write($"> {input}".PadRight(terminalWidth - 1));
    }

    private IRenderable BuildLayout(int width, int height)
    {
        // Calculate panel heights
        var nowPlayingHeight = 6;
        var statusHeight = 4;
        var logsHeight = Math.Max(5, height - nowPlayingHeight - statusHeight - 4);

        var rows = new List<IRenderable>();

        // Header
        rows.Add(new Rule("[bold cyan]Wavee - Spotify Connect[/]").LeftJustified());

        // Now Playing Panel
        rows.Add(BuildNowPlayingPanel());

        // Status Panel
        rows.Add(BuildStatusPanel());

        // Logs Panel
        rows.Add(BuildLogsPanel(logsHeight));

        return new Rows(rows);
    }

    private Panel BuildNowPlayingPanel()
    {
        string title, artist, album;
        long pos, dur, posTimestamp;
        bool playing, paused;
        int vol;
        string? contextUri;
        Core.Http.Lyrics.LyricsData? lyrics;

        lock (_lock)
        {
            title = _trackTitle ?? "";
            artist = _trackArtist ?? "";
            album = _trackAlbum ?? "";
            pos = _positionMs;
            posTimestamp = _positionTimestamp;
            dur = _durationMs;
            playing = _isPlaying;
            paused = _isPaused;
            vol = _volumePercent;
            contextUri = _contextUri;
            lyrics = _lyrics;
        }

        // Interpolate position if playing (position advances with time)
        if (playing && !paused && posTimestamp > 0)
        {
            var elapsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - posTimestamp;
            pos = Math.Min(pos + elapsed, dur);
        }

        // Left side: Track info
        var leftContent = new List<IRenderable>();

        if (!string.IsNullOrEmpty(title))
        {
            // Status icon with better visuals
            var statusIcon = paused ? "[yellow]II[/]" : (playing ? "[green]>[/]" : "[dim].[/]");

            // Track title - prominent
            leftContent.Add(new Markup($"{statusIcon}  [bold white]{EscapeMarkup(title)}[/]"));

            // Artist - slightly dimmer
            if (!string.IsNullOrEmpty(artist))
            {
                leftContent.Add(new Markup($"   [cyan]{EscapeMarkup(artist)}[/]"));
            }

            // Album - dim
            if (!string.IsNullOrEmpty(album))
            {
                leftContent.Add(new Markup($"   [dim]{EscapeMarkup(album)}[/]"));
            }

            leftContent.Add(new Text("")); // Spacer

            // Progress bar
            var progress = dur > 0 ? (double)pos / dur : 0;
            var posStr = FormatTime(pos);
            var durStr = FormatTime(dur);
            var progressBar = RenderSeekBar(progress, 40);
            leftContent.Add(new Markup($"   [dim]{posStr}[/]  {progressBar}  [dim]{durStr}[/]"));

            // Volume bar - compact
            var volBar = RenderVolumeBar(vol, 15);
            var volIcon = vol == 0 ? "[dim]x[/]" : (vol < 30 ? "[dim])[/]" : (vol < 70 ? "[blue])[/]" : "[green]))[/]"));
            leftContent.Add(new Markup($"   {volIcon} {volBar} [dim]{vol}%[/]"));

            // Context hint (View Playlist)
            if (!string.IsNullOrEmpty(contextUri))
            {
                var contextType = GetContextType(contextUri);
                leftContent.Add(new Markup($"   [dim]Press[/] [cyan]V[/] [dim]to view {contextType}[/]"));
            }
        }
        else
        {
            // No track playing - show placeholder
            leftContent.Add(new Text(""));
            leftContent.Add(new Markup("   [dim]No track playing[/]"));
            leftContent.Add(new Text(""));
            leftContent.Add(new Markup($"   [dim]0:00[/]  {RenderSeekBar(0, 40)}  [dim]0:00[/]"));
            var volBar = RenderVolumeBar(vol, 15);
            leftContent.Add(new Markup($"   [dim])[/] {volBar} [dim]{vol}%[/]"));
        }

        // Right side: Lyrics
        var rightContent = new List<IRenderable>();

        if (lyrics != null && lyrics.Lines.Count > 0)
        {
            rightContent.Add(new Markup("[grey]─ Lyrics ─[/]"));

            var currentIndex = GetCurrentLyricIndex(pos);

            // Show 2 lines before, current, and 4 lines after (7 total)
            var startIndex = Math.Max(0, currentIndex - 2);
            var endIndex = Math.Min(lyrics.Lines.Count - 1, Math.Max(0, currentIndex) + 4);

            for (int i = startIndex; i <= endIndex; i++)
            {
                var line = lyrics.Lines[i];
                var words = string.IsNullOrWhiteSpace(line.Words) ? "♪" : EscapeMarkup(line.Words);

                if (i < currentIndex)
                {
                    // Past lines - dimmed
                    rightContent.Add(new Markup($"[grey]{words}[/]"));
                }
                else if (i == currentIndex)
                {
                    // Current line - highlighted and bold
                    rightContent.Add(new Markup($"[bold green]{words}[/]"));
                }
                else
                {
                    // Upcoming lines - visible but not highlighted
                    rightContent.Add(new Markup($"[white]{words}[/]"));
                }
            }
        }
        else
        {
            rightContent.Add(new Markup("[dim]No lyrics[/]"));
        }

        // Create side-by-side layout using Grid
        var grid = new Grid();
        grid.AddColumn(new GridColumn().Width(60)); // Track info column
        grid.AddColumn(new GridColumn().PadLeft(2)); // Lyrics column (flexible)

        var leftPanel = new Rows(leftContent);
        var rightPanel = new Rows(rightContent);

        grid.AddRow(leftPanel, rightPanel);

        return new Panel(grid)
            .Header("[bold green] NOW PLAYING [/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Green)
            .Expand();
    }

    private static string RenderSeekBar(double progress, int width)
    {
        progress = Math.Clamp(progress, 0, 1);
        var filledWidth = (int)(progress * width);
        var emptyWidth = width - filledWidth - 1;

        if (filledWidth == 0)
        {
            // At the start
            return $"[white]o[/][dim]{new string('-', width - 1)}[/]";
        }
        else if (filledWidth >= width)
        {
            // At the end
            return $"[green]{new string('━', width - 1)}[/][white]o[/]";
        }
        else
        {
            // In the middle
            var filled = new string('━', filledWidth);
            var empty = new string('─', Math.Max(0, emptyWidth));
            return $"[green]{filled}[/][white]o[/][dim]{empty}[/]";
        }
    }

    private static string RenderVolumeBar(int percent, int width)
    {
        var progress = percent / 100.0;
        var filledWidth = (int)(progress * width);
        var emptyWidth = width - filledWidth;

        var filled = new string('█', filledWidth);
        var empty = new string('░', emptyWidth);

        // Color based on volume level
        var color = percent < 30 ? "dim" : (percent < 70 ? "blue" : "green");
        return $"[{color}]{filled}[/][dim]{empty}[/]";
    }

    private Panel BuildStatusPanel()
    {
        string device, dealer;
        bool active, shuffle, repCtx, repTrack;

        lock (_lock)
        {
            device = _deviceName;
            active = _deviceActive;
            dealer = _dealerStatus;
            shuffle = _shuffleEnabled;
            repCtx = _repeatContext;
            repTrack = _repeatTrack;
        }

        var deviceIcon = active ? "[green]@[/]" : "[yellow]@[/]";
        var deviceStatus = active ? "[green]Active[/]" : "[yellow]Inactive[/]";
        var dealerIcon = dealer == "Connected" ? "[green]*[/]" : (dealer == "Connecting..." ? "[yellow]~[/]" : "[red]x[/]");
        var dealerColor = dealer == "Connected" ? "green" : (dealer == "Connecting..." ? "yellow" : "red");

        var shuffleIcon = shuffle ? "[green]S[/]" : "[dim]S[/]";
        var repeatIcon = repTrack ? "[green]1[/]" : (repCtx ? "[green]R[/]" : "[dim]R[/]");

        var content = new List<IRenderable>
        {
            new Markup($"  {deviceIcon} [bold]{EscapeMarkup(device)}[/] {deviceStatus}"),
            new Markup($"  {dealerIcon} Dealer: [{dealerColor}]{EscapeMarkup(dealer)}[/]"),
            new Markup($"  {shuffleIcon} Shuffle   {repeatIcon} Repeat")
        };

        return new Panel(new Rows(content))
            .Header("[bold blue] STATUS [/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Blue)
            .Expand();
    }

    private Panel BuildLogsPanel(int height)
    {
        var entries = _logEntries.ToArray();
        var content = new List<IRenderable>();

        // Take last N entries that fit
        var visibleEntries = entries.TakeLast(height - 2).ToArray();

        foreach (var entry in visibleEntries)
        {
            var levelColor = entry.Level switch
            {
                "ERR" or "FTL" => "red",
                "WRN" => "yellow",
                "INF" => "blue",
                "CMD" => "cyan",
                "DBG" => "dim",
                "VRB" => "dim",
                _ => "white"
            };

            var time = entry.Timestamp.ToString("HH:mm:ss");
            // Use proper markup: [{color}]text[/] - don't nest brackets
            content.Add(new Markup($"[dim]{time}[/] [{levelColor}]{entry.Level}[/] {EscapeMarkup(entry.Message)}"));
        }

        // Fill remaining space with empty lines
        while (content.Count < height - 2)
        {
            content.Add(new Text(""));
        }

        return new Panel(new Rows(content))
            .Header("[bold magenta] LOGS [/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Purple)
            .Expand();
    }

    private async Task HandleInputAsync(CancellationToken ct)
    {
        while (System.Console.KeyAvailable && !ct.IsCancellationRequested)
        {
            var key = System.Console.ReadKey(intercept: true);

            // Handle 'V' key for "View Playlist" when input buffer is empty
            if ((key.Key == ConsoleKey.V || key.KeyChar == 'v') && _inputBuffer.Length == 0)
            {
                string? contextUri;
                lock (_lock)
                {
                    contextUri = _contextUri;
                }

                if (!string.IsNullOrEmpty(contextUri) && _commandHandler != null)
                {
                    try
                    {
                        await _commandHandler("view-context", ct);
                    }
                    catch (Exception ex)
                    {
                        AddLog("ERR", $"View context failed: {ex.Message}");
                    }
                }
                continue;
            }

            if (key.Key == ConsoleKey.Enter)
            {
                var command = _inputBuffer.ToString().Trim();
                _inputBuffer.Clear();

                if (!string.IsNullOrEmpty(command))
                {
                    AddLog("CMD", $"> {command}");

                    if (_commandHandler != null)
                    {
                        try
                        {
                            var shouldExit = await _commandHandler(command, ct);
                            if (shouldExit)
                            {
                                _renderCts?.Cancel();
                                return;
                            }
                        }
                        catch (Exception ex)
                        {
                            AddLog("ERR", $"Command failed: {ex.Message}");
                        }
                    }
                }
            }
            else if (key.Key == ConsoleKey.Backspace)
            {
                if (_inputBuffer.Length > 0)
                {
                    _inputBuffer.Length--;
                }
            }
            else if (key.Key == ConsoleKey.Escape)
            {
                _inputBuffer.Clear();
            }
            else if (!char.IsControl(key.KeyChar))
            {
                _inputBuffer.Append(key.KeyChar);
            }
        }
    }

    private static string FormatTime(long ms)
    {
        var ts = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";
    }

    private static string EscapeMarkup(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        return text
            .Replace("[", "[[")
            .Replace("]", "]]");
    }

    private static string GetContextType(string contextUri)
    {
        if (contextUri.Contains(":playlist:")) return "playlist";
        if (contextUri.Contains(":album:")) return "album";
        if (contextUri.Contains(":artist:")) return "artist";
        if (contextUri.Contains(":show:")) return "show";
        return "context";
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _renderCts?.Cancel();
        _renderCts?.Dispose();
    }

    private record LogEntry(DateTime Timestamp, string Level, string Message);
}
