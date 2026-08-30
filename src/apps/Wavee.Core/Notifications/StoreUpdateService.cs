using System;
using System.Threading;
using System.Threading.Tasks;

namespace Wavee.Core;

/// <summary>
/// The updater of a Store-installed build: the Store owns downloads, staging and the restart, so this service never
/// polls the .appinstaller feed and never stages anything — it stays <see cref="AppUpdateSnapshot.Idle"/> (no toast
/// ever fires, <see cref="ShutdownUpdatePolicy"/> never applies on quit) and "Update now" simply opens the product
/// page in the Store app, which is where a pending update is shown and installed from.
/// </summary>
public sealed class StoreUpdateService : IAppUpdateService
{
    readonly SimpleEvent<int> _changed = new();
    readonly Action<string> _openUrl;

    public StoreUpdateService(string storeId, Action<string> openUrl)
    {
        if (string.IsNullOrWhiteSpace(storeId)) throw new ArgumentException("Store id required", nameof(storeId));
        _openUrl = openUrl ?? throw new ArgumentNullException(nameof(openUrl));
        FeedUrl = StoreLinks.ProductPage(storeId);
    }

    public AppUpdateSnapshot Current => AppUpdateSnapshot.Idle;
    public IObservable<int> Changed => _changed;
    /// <summary>Not a feed: the Store product page this build updates from.</summary>
    public string FeedUrl { get; }
    public Task CheckAsync(UpdateCheckOrigin origin, CancellationToken ct) => Task.CompletedTask;
    public Task ApplyAsync(CancellationToken ct) { _openUrl(FeedUrl); return Task.CompletedTask; }
    public void Snooze() { }
    public void Acknowledge() { }
}
