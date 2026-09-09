using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Wavee.SpotifyLive.Audio;

/// <summary>One owner for host state. Awaiting I/O yields the owner; continuations return to its mailbox.</summary>
internal sealed class AudioHostMailbox : SynchronizationContext, IAsyncDisposable
{
    readonly Channel<Action> _messages = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions
    {
        SingleReader = true,
        AllowSynchronousContinuations = false
    });
    readonly Action<Exception> _onError;
    readonly Task _reader;
    int _operations;
    TaskCompletionSource? _idle;

    public AudioHostMailbox(Action<Exception> onError)
    {
        _onError = onError;
        _reader = Task.Run(ReadAsync);
    }

    public override void Post(SendOrPostCallback callback, object? state)
    {
        if (!_messages.Writer.TryWrite(() => callback(state)))
            throw new ObjectDisposedException(nameof(AudioHostMailbox));
    }

    public void Enqueue(Func<Task> operation) => Post(_ => Run(operation), null);

    async void Run(Func<Task> operation)
    {
        _operations++;
        try { await operation(); }
        catch (OperationCanceledException) { }
        catch (Exception error) { _onError(error); }
        finally
        {
            if (--_operations == 0) _idle?.TrySetResult();
        }
    }

    public Task InvokeAsync(Func<Task> operation)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(async () =>
        {
            try { await operation(); completion.TrySetResult(); }
            catch (OperationCanceledException error) { completion.TrySetCanceled(error.CancellationToken); }
            catch (Exception error) { completion.TrySetException(error); }
        });
        return completion.Task;
    }

    async Task ReadAsync()
    {
        await foreach (Action action in _messages.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try { action(); }
            catch (Exception error) { _onError(error); }
            finally { SetSynchronizationContext(previous); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(_ =>
        {
            if (_operations == 0) idle.TrySetResult();
            else _idle = idle;
        }, null);
        await idle.Task.ConfigureAwait(false);
        _messages.Writer.TryComplete();
        await _reader.ConfigureAwait(false);
    }
}
