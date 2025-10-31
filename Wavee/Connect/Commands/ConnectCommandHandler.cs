using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Wavee.Connect.Protocol;
using Wavee.Core.Utilities;

namespace Wavee.Connect.Commands;

/// <summary>
/// Handles incoming Spotify Connect commands from the Dealer WebSocket connection.
///
/// WHY: This is the bridge between raw dealer messages and typed command handlers.
/// - Parses dealer REQUEST messages into strongly-typed command objects
/// - Dispatches commands via observable streams (Rx.NET)
/// - Manages reply tracking with timeout handling
/// - Provides backpressure via bounded channels
///
/// ARCHITECTURE:
/// - Uses SafeSubject for exception isolation (one bad subscriber doesn't break others)
/// - AsyncWorker for non-blocking command dispatch
/// - Channel-based queuing with bounded capacity for backpressure
/// - TaskCompletionSource for reply tracking with timeout
/// </summary>
public sealed class ConnectCommandHandler : IAsyncDisposable
{
    private readonly ICommandSource _commandSource;
    private readonly ILogger? _logger;

    // Command observables - use SafeSubject for exception isolation
    private readonly SafeSubject<PlayCommand> _playCommands = new();
    private readonly SafeSubject<PauseCommand> _pauseCommands = new();
    private readonly SafeSubject<ResumeCommand> _resumeCommands = new();
    private readonly SafeSubject<SeekCommand> _seekCommands = new();
    private readonly SafeSubject<SkipNextCommand> _skipNextCommands = new();
    private readonly SafeSubject<SkipPrevCommand> _skipPrevCommands = new();
    private readonly SafeSubject<ShuffleCommand> _shuffleCommands = new();
    private readonly SafeSubject<RepeatContextCommand> _repeatContextCommands = new();
    private readonly SafeSubject<RepeatTrackCommand> _repeatTrackCommands = new();
    private readonly SafeSubject<SetQueueCommand> _setQueueCommands = new();
    private readonly SafeSubject<AddToQueueCommand> _addToQueueCommands = new();
    private readonly SafeSubject<TransferCommand> _transferCommands = new();

    // Command dispatch infrastructure
    private readonly AsyncWorker<ConnectCommand> _commandWorker;

    // Reply tracking - maps reply key to completion source
    private readonly Dictionary<string, TaskCompletionSource<RequestResult>> _pendingReplies = new();
    private readonly object _replyLock = new();

    // Subscription management
    private IDisposable? _requestSubscription;
    private readonly CompositeDisposable _disposables = new();

    /// <summary>
    /// Initializes the ConnectCommandHandler with a DealerClient (primary constructor for production).
    /// This constructor chains to the secondary constructor using a DealerClientCommandSource adapter.
    /// </summary>
    /// <param name="dealerClient">The dealer client to receive commands from</param>
    /// <param name="logger">Optional logger for diagnostics</param>
    public ConnectCommandHandler(DealerClient dealerClient, ILogger? logger = null)
        : this(new DealerClientCommandSource(dealerClient), logger)
    {
    }

    /// <summary>
    /// Initializes the ConnectCommandHandler with a command source (secondary constructor for testability).
    /// This constructor enables dependency injection of ICommandSource for better testability.
    /// </summary>
    /// <param name="commandSource">The command source to receive requests from and send replies to</param>
    /// <param name="logger">Optional logger for diagnostics</param>
    public ConnectCommandHandler(ICommandSource commandSource, ILogger? logger = null)
    {
        _commandSource = commandSource ?? throw new ArgumentNullException(nameof(commandSource));
        _logger = logger;

        // Create worker for async command processing (bounded capacity for backpressure)
        _commandWorker = new AsyncWorker<ConnectCommand>(
            name: "ConnectCommandHandler",
            worker: ProcessCommandAsync,
            logger: _logger,
            capacity: 100); // Max 100 pending commands

        // Subscribe to dealer requests for Connect commands
        _requestSubscription = _commandSource.Requests
            .Where(r => r.MessageIdent.StartsWith("hm://connect-state/v1/"))
            .Subscribe(
                onNext: OnDealerRequest,
                onError: ex => _logger?.LogError(ex, "Error in dealer request stream"));

        _disposables.Add(_requestSubscription);

        _logger?.LogInformation("ConnectCommandHandler initialized and listening for commands");
    }

    // ================================================================
    // OBSERVABLE COMMAND STREAMS - Subscribe to receive typed commands
    // ================================================================

    /// <summary>Play command stream - new playback started</summary>
    public IObservable<PlayCommand> PlayCommands => _playCommands;

    /// <summary>Pause command stream - playback paused</summary>
    public IObservable<PauseCommand> PauseCommands => _pauseCommands;

    /// <summary>Resume command stream - playback resumed</summary>
    public IObservable<ResumeCommand> ResumeCommands => _resumeCommands;

    /// <summary>Seek command stream - seek to position in track</summary>
    public IObservable<SeekCommand> SeekCommands => _seekCommands;

    /// <summary>Skip next command stream - skip to next track</summary>
    public IObservable<SkipNextCommand> SkipNextCommands => _skipNextCommands;

    /// <summary>Skip previous command stream - skip to previous track</summary>
    public IObservable<SkipPrevCommand> SkipPrevCommands => _skipPrevCommands;

    /// <summary>Shuffle command stream - toggle shuffle mode</summary>
    public IObservable<ShuffleCommand> ShuffleCommands => _shuffleCommands;

    /// <summary>Repeat context command stream - toggle repeat context mode</summary>
    public IObservable<RepeatContextCommand> RepeatContextCommands => _repeatContextCommands;

    /// <summary>Repeat track command stream - toggle repeat track mode</summary>
    public IObservable<RepeatTrackCommand> RepeatTrackCommands => _repeatTrackCommands;

    /// <summary>Set queue command stream - replace entire queue</summary>
    public IObservable<SetQueueCommand> SetQueueCommands => _setQueueCommands;

    /// <summary>Add to queue command stream - add track to queue</summary>
    public IObservable<AddToQueueCommand> AddToQueueCommands => _addToQueueCommands;

    /// <summary>Transfer command stream - playback transferred from another device</summary>
    public IObservable<TransferCommand> TransferCommands => _transferCommands;

    // ================================================================
    // COMMAND PROCESSING PIPELINE
    // ================================================================

    /// <summary>
    /// Called when a dealer request arrives. Parses and queues the command.
    /// </summary>
    private void OnDealerRequest(DealerRequest request)
    {
        try
        {
            // Parse the request into a strongly-typed command
            var command = ConnectCommand.Parse(request);

            if (command == null)
            {
                _logger?.LogWarning("Failed to parse dealer request: {MessageIdent}", request.MessageIdent);

                // Send error reply
                _ = SendReplyAsync(request.Key, RequestResult.DeviceDoesNotSupportCommand);
                return;
            }

            _logger?.LogDebug("Parsed command: {Endpoint} (MessageId: {MessageId})",
                command.Endpoint, command.MessageId);

            // Queue command for async processing
            if (!_commandWorker.TrySubmit(command))
            {
                _logger?.LogWarning("Command queue full, dropping command: {Endpoint}", command.Endpoint);
                _ = SendReplyAsync(request.Key, RequestResult.UpstreamError);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error processing dealer request: {MessageIdent}", request.MessageIdent);
            _ = SendReplyAsync(request.Key, RequestResult.UpstreamError);
        }
    }

    /// <summary>
    /// AsyncWorker callback - processes a single command.
    /// </summary>
    private async ValueTask ProcessCommandAsync(ConnectCommand command)
    {
        try
        {
            _logger?.LogDebug("Dispatching command: {Endpoint}", command.Endpoint);

            // Dispatch to appropriate observable stream based on command type
            switch (command)
            {
                case PlayCommand play:
                    _playCommands.OnNext(play);
                    break;
                case PauseCommand pause:
                    _pauseCommands.OnNext(pause);
                    break;
                case ResumeCommand resume:
                    _resumeCommands.OnNext(resume);
                    break;
                case SeekCommand seek:
                    _seekCommands.OnNext(seek);
                    break;
                case SkipNextCommand skipNext:
                    _skipNextCommands.OnNext(skipNext);
                    break;
                case SkipPrevCommand skipPrev:
                    _skipPrevCommands.OnNext(skipPrev);
                    break;
                case ShuffleCommand shuffle:
                    _shuffleCommands.OnNext(shuffle);
                    break;
                case RepeatContextCommand repeatContext:
                    _repeatContextCommands.OnNext(repeatContext);
                    break;
                case RepeatTrackCommand repeatTrack:
                    _repeatTrackCommands.OnNext(repeatTrack);
                    break;
                case SetQueueCommand setQueue:
                    _setQueueCommands.OnNext(setQueue);
                    break;
                case AddToQueueCommand addToQueue:
                    _addToQueueCommands.OnNext(addToQueue);
                    break;
                case TransferCommand transfer:
                    _transferCommands.OnNext(transfer);
                    break;
                default:
                    _logger?.LogWarning("Unknown command type: {Type}", command.GetType().Name);
                    await SendReplyAsync(command.Key, RequestResult.DeviceDoesNotSupportCommand);
                    return;
            }

            _logger?.LogDebug("Command dispatched successfully: {Endpoint}", command.Endpoint);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error dispatching command: {Endpoint}", command.Endpoint);
            await SendReplyAsync(command.Key, RequestResult.UpstreamError);
        }
    }

    // ================================================================
    // REPLY MANAGEMENT - Send success/error replies back to Spotify
    // ================================================================

    /// <summary>
    /// Sends a reply to a command request.
    /// </summary>
    /// <param name="key">The request key to reply to</param>
    /// <param name="result">The command execution result</param>
    public async Task SendReplyAsync(string key, RequestResult result)
    {
        try
        {
            await _commandSource.SendReplyAsync(key, result);

            _logger?.LogDebug("Sent reply for key {Key}: {Result}", key, result);

            // Complete any pending reply task
            lock (_replyLock)
            {
                if (_pendingReplies.TryGetValue(key, out var tcs))
                {
                    _pendingReplies.Remove(key);
                    tcs.TrySetResult(result);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to send reply for key {Key}", key);
        }
    }

    /// <summary>
    /// Waits for a reply to be sent for a specific request key with timeout.
    /// </summary>
    /// <param name="key">The request key to wait for</param>
    /// <param name="timeout">The timeout duration (default 5 seconds)</param>
    /// <returns>The command result, or UpstreamError on timeout</returns>
    public async Task<RequestResult> WaitForReplyAsync(string key, TimeSpan? timeout = null)
    {
        TaskCompletionSource<RequestResult> tcs;

        lock (_replyLock)
        {
            if (!_pendingReplies.TryGetValue(key, out tcs!))
            {
                tcs = new TaskCompletionSource<RequestResult>();
                _pendingReplies[key] = tcs;
            }
        }

        timeout ??= TimeSpan.FromSeconds(5);

        using var cts = new CancellationTokenSource(timeout.Value);
        using var registration = cts.Token.Register(() => tcs.TrySetCanceled());

        try
        {
            return await tcs.Task;
        }
        catch (OperationCanceledException)
        {
            _logger?.LogWarning("Reply timeout for key {Key}", key);

            lock (_replyLock)
            {
                _pendingReplies.Remove(key);
            }

            return RequestResult.UpstreamError;
        }
    }

    // ================================================================
    // DISPOSAL
    // ================================================================

    public async ValueTask DisposeAsync()
    {
        _logger?.LogInformation("Disposing ConnectCommandHandler");

        // Dispose subscriptions
        _disposables.Dispose();

        // Wait for worker to finish processing
        await _commandWorker.DisposeAsync();

        // Complete all observables
        _playCommands.OnCompleted();
        _pauseCommands.OnCompleted();
        _resumeCommands.OnCompleted();
        _seekCommands.OnCompleted();
        _skipNextCommands.OnCompleted();
        _skipPrevCommands.OnCompleted();
        _shuffleCommands.OnCompleted();
        _repeatContextCommands.OnCompleted();
        _repeatTrackCommands.OnCompleted();
        _setQueueCommands.OnCompleted();
        _addToQueueCommands.OnCompleted();
        _transferCommands.OnCompleted();

        // Cancel all pending replies
        lock (_replyLock)
        {
            foreach (var tcs in _pendingReplies.Values)
            {
                tcs.TrySetCanceled();
            }
            _pendingReplies.Clear();
        }

        _logger?.LogInformation("ConnectCommandHandler disposed");
    }
}
