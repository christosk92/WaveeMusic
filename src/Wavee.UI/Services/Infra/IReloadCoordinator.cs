using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wavee.UI.Threading;

namespace Wavee.UI.Services.Infra;

/// <summary>
/// Drains the "subscribe → cancel CTS → reload → marshal to dispatcher"
/// boilerplate from ViewModels. Each registration listens to one
/// <see cref="ChangeScope"/>; when that scope is published, any in-flight
/// reload from the same registration is cancelled and the new reload starts.
/// </summary>
public interface IReloadCoordinator
{
    /// <summary>
    /// Register a reload function. Dispose the returned token to unregister
    /// and cancel any in-flight reload owned by this registration.
    /// </summary>
    IDisposable RegisterReload(
        ChangeScope scope,
        Func<CancellationToken, Task> reload,
        string opName);
}

/// <summary>
/// Default <see cref="IReloadCoordinator"/>. Filters the bus stream by scope,
/// cancels previous reloads, awaits the new one, and marshals via the UI
/// dispatcher if one was supplied at construction.
/// </summary>
public sealed class ReloadCoordinator : IReloadCoordinator
{
    private readonly IChangeBus _changeBus;
    private readonly IUiDispatcher? _dispatcher;
    private readonly ILogger<ReloadCoordinator>? _logger;

    public ReloadCoordinator(
        IChangeBus changeBus,
        IUiDispatcher? dispatcher = null,
        ILogger<ReloadCoordinator>? logger = null)
    {
        _changeBus = changeBus;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    public IDisposable RegisterReload(
        ChangeScope scope,
        Func<CancellationToken, Task> reload,
        string opName)
    {
        var registration = new Registration(this, scope, reload, opName);
        registration.Subscribe();
        return registration;
    }

    private sealed class Registration : IDisposable
    {
        private readonly ReloadCoordinator _owner;
        private readonly ChangeScope _scope;
        private readonly Func<CancellationToken, Task> _reload;
        private readonly string _opName;
        private IDisposable? _subscription;
        private CancellationTokenSource? _inflight;
        private int _disposed;

        public Registration(
            ReloadCoordinator owner,
            ChangeScope scope,
            Func<CancellationToken, Task> reload,
            string opName)
        {
            _owner = owner;
            _scope = scope;
            _reload = reload;
            _opName = opName;
        }

        public void Subscribe()
        {
            _subscription = _owner._changeBus.Changes
                .Where(s => s == _scope)
                .Subscribe(_ => OnFired());
        }

        private void OnFired()
        {
            if (Volatile.Read(ref _disposed) != 0) return;

            var newCts = new CancellationTokenSource();
            var previous = Interlocked.Exchange(ref _inflight, newCts);
            previous?.Cancel();
            previous?.Dispose();

            var ct = newCts.Token;

            if (_owner._dispatcher is not null
                && !_owner._dispatcher.HasThreadAccess)
            {
                _owner._dispatcher.TryEnqueue(() => _ = RunReloadAsync(ct));
            }
            else
            {
                _ = RunReloadAsync(ct);
            }
        }

        private async Task RunReloadAsync(CancellationToken ct)
        {
            try
            {
                await _reload(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* superseded or disposed */ }
            catch (Exception ex)
            {
                _owner._logger?.LogError(ex,
                    "Reload failed: {OpName} (scope {Scope})", _opName, _scope);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _subscription?.Dispose();
            var inflight = Interlocked.Exchange(ref _inflight, null);
            inflight?.Cancel();
            inflight?.Dispose();
        }
    }
}
