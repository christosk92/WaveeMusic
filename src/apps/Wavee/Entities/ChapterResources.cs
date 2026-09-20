using FluentGpu.Signals;
using ChapterPage = Wavee.Spotify.Podcasts.Page<Wavee.Spotify.Podcasts.Chapter>;

namespace Wavee;

/// <summary>UI-owned leases: the chapter reader and transport share one account-scoped request, never a global
/// last-episode result. The last consumer cancels and removes the entry. Late completions cannot revive it.</summary>
public sealed class ChapterResources(
    Func<ChapterResources.Identity, CancellationToken, Task<ChapterPage>> fetch,
    Action<Action> post, Func<ChapterResources.Identity, bool> current)
{
    public readonly record struct Identity(uint Epoch, string Account, EntityId Episode);
    readonly Dictionary<Identity, Entry> _entries = [];
    readonly Func<Identity, CancellationToken, Task<ChapterPage>> _fetch = fetch;
    readonly Action<Action> _post = post;
    readonly Func<Identity, bool> _current = current;
    public static readonly ChapterPage Seed = new(
        Enumerable.Range(0, 5).Select(i => new Spotify.Podcasts.Chapter("seed:" + i, "Chapter title", i * 90000, 0)).ToArray(), "", 5, "", 200);
    public static readonly ChapterResources Shared = new(
        static (key, ct) => Platform.Args.Fake ? Task.FromResult(PodcastReaderFixtures.Chapters)
            : Spotify.Podcasts.ChaptersAsync(key.Episode.Text, key.Account, ct),
        static action => Store.Post(action),
        static key => key.Epoch == Entities.ScopeEpoch.Peek()
            && string.Equals(key.Account, Entities.Current.Key.Account, StringComparison.Ordinal));

    public Lease Acquire(Identity key)
    {
        if (!_entries.TryGetValue(key, out var entry)) _entries.Add(key, entry = new Entry(this, key));
        entry.Users++;
        return new Lease(this, key, entry);
    }

    public sealed class Lease : IDisposable
    {
        readonly ChapterResources _owner;
        readonly Identity _key;
        Entry? _entry;
        internal Lease(ChapterResources owner, Identity key, Entry entry) { _owner = owner; _key = key; _entry = entry; Data = entry.Data; }
        public Loadable<ChapterPage> Data { get; }
        public void Start() { if (_entry is { Started: false } entry) entry.Refresh(); }
        public void Refresh() => _entry?.Refresh();
        public void Dispose()
        {
            if (_entry is not { } entry) return;
            _entry = null;
            if (--entry.Users != 0) return;
            entry.Cancel();
            if (_owner._entries.TryGetValue(_key, out var present) && ReferenceEquals(present, entry)) _owner._entries.Remove(_key);
        }
    }

    internal sealed class Entry(ChapterResources owner, Identity key)
    {
        internal readonly Loadable<ChapterPage> Data = Loadable<ChapterPage>.Pending(Seed);
        internal int Users;
        internal bool Started;
        CancellationTokenSource? _request;
        internal void Cancel() { _request?.Cancel(); _request?.Dispose(); _request = null; }
        internal void Refresh()
        {
            Cancel(); Started = true;
            var request = _request = new CancellationTokenSource();
            if (!Data.IsReady) Data.SetPending(Seed);
            _ = Fetch(request, request.Token);
        }
        bool Accept(CancellationTokenSource request, CancellationToken token)
            => Users > 0 && ReferenceEquals(_request, request) && !token.IsCancellationRequested && owner._current(key);
        async Task Fetch(CancellationTokenSource request, CancellationToken token)
        {
            try
            {
                var page = await owner._fetch(key, token).ConfigureAwait(false);
                owner._post(() =>
                {
                    if (!Accept(request, token)) return;
                    if (page.Ok) Data.SetReady(page);
                    else if (page.Status is 404 or 204) Data.SetReady(new([], "", 0, "", 200));
                    else Data.SetFailed(new InvalidOperationException("Chapters response " + page.Status));
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                owner._post(() => { if (Accept(request, token)) Data.SetFailed(error); });
            }
        }
    }
}
