using System.Collections.Concurrent;
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

    private readonly object _lock = new();
    private readonly ConcurrentQueue<LogEntry> _logEntries = new();
    private readonly StringBuilder _inputBuffer = new();

    // Playback state
    private string? _trackTitle;
    private string? _trackArtist;
    private string? _trackAlbum;
    private long _positionMs;
    private long _durationMs;
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
        AnsiConsole.Clear();
        AnsiConsole.Cursor.Show();
    }

    /// <summary>
    /// Resumes live rendering after interactive prompts.
    /// </summary>
    public void ResumeLiveRendering()
    {
        _renderingPaused = false;
        AnsiConsole.Clear();
        AnsiConsole.Cursor.Hide();
    }

    /// <summary>
    /// Runs the live UI rendering loop.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _renderCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = _renderCts.Token;

        // Clear screen and hide cursor
        AnsiConsole.Clear();
        AnsiConsole.Cursor.Hide();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Skip rendering when paused (for interactive prompts)
                if (!_renderingPaused)
                {
                    // Render the full layout
                    RenderLayout();

                    // Handle keyboard input (non-blocking)
                    await HandleInputAsync(ct);
                }

                // Small delay for refresh rate
                await Task.Delay(RefreshIntervalMs, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
        }
        finally
        {
            AnsiConsole.Cursor.Show();
            AnsiConsole.Clear();
        }
    }

    private void RenderLayout()
    {
        // Move cursor to top-left
        AnsiConsole.Cursor.SetPosition(0, 0);

        var terminalHeight = System.Console.WindowHeight;
        var terminalWidth = System.Console.WindowWidth;

        // Build the layout
        var layout = BuildLayout(terminalWidth, terminalHeight - 1); // -1 for input line

        // Render to console (overwrite existing content)
        AnsiConsole.Write(layout);

        // Render input line at bottom
        RenderInputLine(terminalHeight - 1);
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
        long pos, dur;
        bool playing, paused;
        int vol;

        lock (_lock)
        {
            title = _trackTitle ?? "";
            artist = _trackArtist ?? "";
            album = _trackAlbum ?? "";
            pos = _positionMs;
            dur = _durationMs;
            playing = _isPlaying;
            paused = _isPaused;
            vol = _volumePercent;
        }

        var content = new List<IRenderable>();

        if (!string.IsNullOrEmpty(title))
        {
            // Status icon with better visuals
            var statusIcon = paused ? "[yellow]II[/]" : (playing ? "[green]>[/]" : "[dim].[/]");

            // Track title - prominent
            content.Add(new Markup($"  {statusIcon}  [bold white]{EscapeMarkup(title)}[/]"));

            // Artist - slightly dimmer
            if (!string.IsNullOrEmpty(artist))
            {
                content.Add(new Markup($"      [cyan]{EscapeMarkup(artist)}[/]"));
            }

            // Album - dim
            if (!string.IsNullOrEmpty(album))
            {
                content.Add(new Markup($"      [dim]{EscapeMarkup(album)}[/]"));
            }

            content.Add(new Text("")); // Spacer

            // Progress bar - full width, beautiful
            var progress = dur > 0 ? (double)pos / dur : 0;
            var posStr = FormatTime(pos);
            var durStr = FormatTime(dur);
            var progressBar = RenderSeekBar(progress, 50);
            content.Add(new Markup($"      [dim]{posStr}[/]  {progressBar}  [dim]{durStr}[/]"));

            // Volume bar - compact
            var volBar = RenderVolumeBar(vol, 20);
            var volIcon = vol == 0 ? "[dim]x[/]" : (vol < 30 ? "[dim])[/]" : (vol < 70 ? "[blue])[/]" : "[green]))[/]"));
            content.Add(new Markup($"      {volIcon} {volBar} [dim]{vol}%[/]"));
        }
        else
        {
            // No track playing - show placeholder
            content.Add(new Text(""));
            content.Add(new Markup("      [dim]No track playing[/]"));
            content.Add(new Text(""));
            content.Add(new Markup($"      [dim]0:00[/]  {RenderSeekBar(0, 50)}  [dim]0:00[/]"));
            var volBar = RenderVolumeBar(vol, 20);
            content.Add(new Markup($"      [dim])[/] {volBar} [dim]{vol}%[/]"));
        }

        return new Panel(new Rows(content))
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

    private void RenderInputLine(int row)
    {
        System.Console.SetCursorPosition(0, row);
        var input = _inputBuffer.ToString();
        var prompt = $"> {input}";

        // Clear line and write prompt
        System.Console.Write(prompt.PadRight(System.Console.WindowWidth - 1));
        System.Console.SetCursorPosition(prompt.Length, row);
    }

    private async Task HandleInputAsync(CancellationToken ct)
    {
        while (System.Console.KeyAvailable && !ct.IsCancellationRequested)
        {
            var key = System.Console.ReadKey(intercept: true);
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
