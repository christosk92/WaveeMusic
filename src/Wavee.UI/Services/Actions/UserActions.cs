using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Wavee.UI.Services.Actions;

public sealed record UserActionDescriptor(string Kind, string PayloadJson);

public interface IUserAction
{
    Guid Id { get; }
    string Category { get; }
    string Title { get; }
    string? Message { get; }
    string? IconGlyph { get; }
    Task ExecuteAsync(CancellationToken ct = default);
}

public interface IUndoableUserAction : IUserAction
{
    string UndoLabel { get; }
    UserActionDescriptor Descriptor { get; }
    Task UndoAsync(CancellationToken ct = default);
}

public sealed record CompletedUserAction(
    Guid Id,
    string Category,
    string Title,
    string? Message,
    string? IconGlyph,
    string UndoLabel,
    UserActionDescriptor Descriptor);

public interface IUserActionActivitySink
{
    Task RecordAsync(CompletedUserAction action, CancellationToken ct = default);
    Task MarkUndoneAsync(Guid activityId, CancellationToken ct = default);
}

public interface IUserActionFactory
{
    IUndoableUserAction Create(UserActionDescriptor descriptor);
}

public interface IUserActionRunner
{
    Task RunAsync(IUserAction action, CancellationToken ct = default);
    Task UndoAsync(Guid activityId, UserActionDescriptor descriptor, CancellationToken ct = default);
}

public sealed class UserActionRunner : IUserActionRunner
{
    private readonly IUserActionActivitySink _activitySink;
    private readonly Func<IUserActionFactory> _factoryProvider;
    private readonly ILogger<UserActionRunner>? _logger;
    private static readonly AsyncLocal<int> SuppressDepth = new();

    public UserActionRunner(
        IUserActionActivitySink activitySink,
        Func<IUserActionFactory> factoryProvider,
        ILogger<UserActionRunner>? logger = null)
    {
        _activitySink = activitySink ?? throw new ArgumentNullException(nameof(activitySink));
        _factoryProvider = factoryProvider ?? throw new ArgumentNullException(nameof(factoryProvider));
        _logger = logger;
    }

    public async Task RunAsync(IUserAction action, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        await action.ExecuteAsync(ct).ConfigureAwait(false);

        if (SuppressDepth.Value > 0 || action is not IUndoableUserAction undoable)
            return;

        await _activitySink.RecordAsync(
            new CompletedUserAction(
                undoable.Id,
                undoable.Category,
                undoable.Title,
                undoable.Message,
                undoable.IconGlyph,
                undoable.UndoLabel,
                undoable.Descriptor),
            ct).ConfigureAwait(false);
    }

    public async Task UndoAsync(Guid activityId, UserActionDescriptor descriptor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var factory = _factoryProvider();
        var action = factory.Create(descriptor);

        SuppressDepth.Value++;
        try
        {
            await action.UndoAsync(ct).ConfigureAwait(false);
            await _activitySink.MarkUndoneAsync(activityId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Undo failed for action {Kind}", descriptor.Kind);
            throw;
        }
        finally
        {
            SuppressDepth.Value--;
        }
    }
}
