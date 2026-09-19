using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;

namespace Wavee;

public readonly partial struct User
{
    sealed class SavedEpisodesReader : Component
    {
        BoundItemsSource<Episode.RowItem>? _items;
        readonly RepeatLayout _layout = RepeatLayout.VariableList(100f);
        readonly Episode.RowContext _context = new()
        {
            Tone = static () => Tok.AccentDefault,
            Play = static episode => Episode.Invoke(episode, () => Playback.PlayEpisode(episode.Id,
                Spotify.Podcasts.SavedPlaylistUri is { Length: > 0 } uri ? EntityId.Parse(uri) : episode.Show.Id,
                new Playback.EpisodeStart(Playback.EpisodeStartKind.Resume))),
        };
        Signal<string>? _error;
        Signal<bool>? _loaded;
        Action<Action>? _post;
        CancellationTokenSource? _cancel;

        public override Element Render()
        {
            _post = UsePost();
            _error = UseSignal("");
            _loaded = UseSignal(false);
            uint epoch = Entities.ScopeEpoch.Value;
            UseEffect(() =>
            {
                var cancel = _cancel = new CancellationTokenSource();
                _ = Refresh(cancel.Token, epoch);
                return () => { cancel.Cancel(); cancel.Dispose(); };
            }, DepKey.From((int)epoch));
            var rows = UseComputed(Rows);
            _items ??= BoundItems.Project(rows, static x => x.Length, static (x, i) => x[i], default(Episode.RowItem));
            _ = Spotify.Podcasts.SavedChanged.Value;
            Element body;
            if (!_loaded.Value && !Spotify.Podcasts.SavedReady)
                body = Episode.SeedReaderRow(narrow: false);
            else if (_error.Value.Length > 0 || (!Spotify.Podcasts.SavedReady && !Platform.Args.Fake))
                body = new BoxEl
                {
                    Direction = 1, Gap = Spacing.S, Padding = Edges4.All(Spacing.L),
                    Children =
                    [
                        new TextEl(Loc.Get(Strings.Podcast.Reader.SaveUnavailable)) { Color = Tok.TextSecondary },
                        Button.Standard(Loc.Get(Strings.Common.Retry), () => { if (_cancel is { } cancel) _ = Refresh(cancel.Token, epoch); }),
                    ],
                };
            else if (rows.Value.Length == 0)
                body = new TextEl(Loc.Get(Strings.Podcast.Reader.SavedEmpty)) { Color = Tok.TextSecondary };
            else
                body = ItemsView.CreateBound(_items, item => Episode.ReaderRow(item, _context, narrow: false), _layout,
                    new ListOptions<Episode.RowItem> { ItemComparer = EqualityComparer<Episode.RowItem>.Default });
            return new BoxEl { Direction = 1, Grow = 1f, MinHeight = 0f, Children = [body] };
        }

        static Episode.RowItem[] Rows()
        {
            _ = Entities.ScopeEpoch.Value;
            _ = Spotify.Podcasts.SavedChanged.Value;
            var table = Entities.Current.Episodes;
            _ = table.Changed.Value;
            var rows = new List<Episode.RowItem>();
            if (Platform.Args.Fake)
            {
                for (int slot = 1; slot < Math.Min(table.Count, 7); slot++)
                { var episode = new Episode(slot); rows.Add(new(episode, episode.Version)); }
                return rows.ToArray();
            }
            foreach (var id in Spotify.Podcasts.SavedEpisodeIds)
                if (table.TryGetSlot(id, out int slot))
                {
                    var episode = new Episode(slot);
                    rows.Add(new(episode, episode.Version));
                }
            return rows.ToArray();
        }

        async Task Refresh(CancellationToken ct, uint epoch)
        {
            try
            {
                if (!Platform.Args.Fake) await Spotify.Podcasts.ReadSavedAsync(ct).ConfigureAwait(false);
                _post!(() => { if (Entities.Current.Epoch == epoch && !ct.IsCancellationRequested) { _error!.Value = ""; _loaded!.Value = true; } });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Warn("podcast", "saved episodes read failed", ex);
                _post!(() => { if (Entities.Current.Epoch == epoch && !ct.IsCancellationRequested) { _error!.Value = "failed"; _loaded!.Value = true; } });
            }
        }
    }
}
