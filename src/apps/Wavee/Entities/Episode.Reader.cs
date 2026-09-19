using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public readonly partial struct Episode
{
    sealed class TranscriptReader : Component
    {
        readonly Signal<bool> _follow = new(true);
        readonly Signal<int> _active = new(-1);
        readonly ItemsViewController _controller = new();
        readonly RepeatLayout _layout = RepeatLayout.VariableList(64);
        BoundItemsSource<TranscriptRow>? _items;
        Episode _episode;
        readonly record struct TranscriptRow(int Index, Spotify.Podcasts.TranscriptLine Line);
        public override Element Render()
        {
            var p = UseProps<ReaderProps>(); var e = ReaderEpisode(p.Subject);
            string url = e.IsValid ? e.TranscriptUrl : "";
            var key = new Spotify.Podcasts.TranscriptKey(p.Subject.Id, e.IsValid ? e.TranscriptLanguage : "", e.IsValid && e.TranscriptReadAlong);
            string account = Entities.Current.Key.Account;
            var resource = UseResource(ct => Platform.Args.Fake ? Task.FromResult(PodcastReaderFixtures.Transcript) : url.Length == 0
                ? Task.FromResult(new Spotify.Podcasts.Transcript(key.Language, [], 404))
                : Spotify.Podcasts.TranscriptAsync(key, url, account, ct),
                new Spotify.Podcasts.Transcript("", [], 0), account + ":" + url + ":" + key.Language + key.ReadAlong);
            var doc = resource.Loadable.Value.Value;
            _episode = e;
            _items ??= BoundItems.Project(resource.Loadable.Value, static d => d.Lines.Length,
                static (d, i) => new TranscriptRow(i, d.Lines[i]), default(TranscriptRow));
            UseSignalEffect(() =>
            {
                int position = Playback.CurrentId.Value == p.Subject.Id ? Playback.PositionMs.Value : -1;
                _active.SetIfChanged(PodcastReaderRules.CurrentLine(doc.Lines, position));
            });
            UseLayoutEffect(() =>
            {
                int active = _active.Value;
                if (_follow.Value && active >= 0)
                    _controller.StartBringItemIntoView(active, alignmentRatio: .35f, animate: !Design.Reduced);
            });
            if (!e.IsValid || (!Platform.Args.Fake && !e.Knows(EpisodeFields.Transcript)) || resource.Loadable.State.Value == (byte)LoadState.Pending)
                return PodcastReaderUI.Loading();
            if ((!Platform.Args.Fake && url.Length == 0) || doc.Status == 404) return PodcastReaderUI.Quiet(Loc.Get(Strings.Podcast.Reader.NoTranscript));
            if (!doc.Ok) return PodcastReaderUI.Failed(resource.Refresh);
            return new BoxEl
            {
                Direction = 1, Gap = Spacing.S, MinWidth = 0, Children =
                [
                    Button.Standard(Loc.Get(_follow.Value ? Strings.Podcast.Reader.Following : Strings.Podcast.Reader.Follow), () => _follow.Value = !_follow.Peek()),
                    new BoxEl { Direction = 1, Height = 440, MinHeight = 0, MinWidth = 0, Children =
                    [ItemsView.CreateBound(_items, TranscriptLine, _layout, new ListOptions<TranscriptRow>
                    {
                        ContentType = i => resource.Loadable.Value.Peek().Lines[i].Heading ? 1 : 0,
                        Controller = _controller, SelectionMode = ItemsSelectionMode.None, Selector = SelectorVisual.None, Grow = 1,
                        Scroll = new ScrollOptions
                        {
                            ScrollKey = "transcript:" + p.Subject.Text,
                            OnScrollGeometryChanged = (static g => g.UserScrollActive ? 1L : 0L, g => { if (g.UserScrollActive) _follow.Value = false; }),
                        },
                    })] },
                ],
            };
        }
        Element TranscriptLine(BoundItemScope<TranscriptRow> item)
        {
            var row = item.Item;
            return new BoxEl
            {
                Direction = 0, Gap = Spacing.M, Padding = Edges4.All(Spacing.S), MinWidth = 0, Grow = 1,
                Corners = Radii.ControlAll, Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
                OnClick = item.Invoke(r => StartAt(_episode, Playback.EpisodeStartKind.Position, r.Line.StartMs)),
                Fill = Prop.Of(() => _active.Value == row.Value.Index ? Tok.FillSubtleSecondary : ColorF.Transparent),
                Children =
                [
                    new TextEl(Prop.Of(() => FormatCache.DurationMmSs(row.Value.Line.StartMs))) { Size = 12, Color = Tok.TextTertiary, Width = 50 },
                    new TextEl(Prop.Of(() => row.Value.Line.Text))
                    {
                        Size = row.Peek().Line.Heading ? 17f : 15f, Weight = (ushort)(row.Peek().Line.Heading ? 600 : 400),
                        LineHeight = 24, Wrap = TextWrap.Wrap, MinWidth = 0, Grow = 1,
                        Color = Prop.Of(() => _active.Value == row.Value.Index ? Tok.AccentTextPrimary : Tok.TextSecondary),
                    },
                ],
            };
        }
    }
}

public static class PodcastReaderRules
{
    public static int CurrentLine(ReadOnlySpan<Spotify.Podcasts.TranscriptLine> lines, int positionMs)
    {
        if (positionMs < 0) return -1;
        int current = -1;
        for (int i = 0; i < lines.Length; i++)
            if (!lines[i].Heading && lines[i].StartMs <= positionMs && (current < 0 || lines[i].StartMs >= lines[current].StartMs)) current = i;
        return current;
    }

    public static int CurrentChapter(ReadOnlySpan<Spotify.Podcasts.Chapter> chapters, int positionMs)
    {
        if (positionMs < 0) return -1;
        int current = -1;
        for (int i = 0; i < chapters.Length; i++)
            if (chapters[i].StartMs <= positionMs && (current < 0 || chapters[i].StartMs >= chapters[current].StartMs)) current = i;
        return current >= 0 && chapters[current].EndMs > 0 && positionMs >= chapters[current].EndMs ? -1 : current;
    }

    public static bool CanComment(string eligibility)
        => eligibility == "ELIGIBILITY_STATUS_UNRESTRICTED";
}

internal static partial class PodcastReaderUI
{
    internal static Element Quiet(string text) => new TextEl(text)
        { Size = 13, LineHeight = 20, Wrap = TextWrap.Wrap, MinWidth = 0, Color = Tok.TextSecondary };
    internal static Element Heading(string text) => new TextEl(text)
        { Size = 20, LineHeight = 28, Weight = 600, Wrap = TextWrap.Wrap, MinWidth = 0, Color = Tok.TextPrimary };
    internal static Element Loading() => Quiet(Loc.Get(Strings.Podcast.LoadingMore));
    internal static Element Failed(Action retry) => new BoxEl
        { Direction = 1, Gap = Spacing.S, Children = [Quiet(Loc.Get(Strings.Podcast.Reader.Unavailable)), Button.Standard(Loc.Get(Strings.Podcast.Reader.Retry), retry)] };

    internal sealed class SpeedControl : Component
    {
        public override Element Render()
        {
            float speed = Playback.EpisodeSpeed.Value;
            var kids = new List<Element>
            {
                Quiet(Loc.Get(Strings.Podcast.Reader.Speed)),
                Button.Subtle("-", () => Playback.SetEpisodeSpeed(MathF.Round(speed - .1f, 1)), isEnabled: speed > .5f),
                Quiet(speed.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture) + "\u00d7"),
                Button.Subtle("+", () => Playback.SetEpisodeSpeed(MathF.Round(speed + .1f, 1)), isEnabled: speed < 3),
            };
            foreach (float preset in new[] { 1f, 1.5f, 2f, 3f })
                kids.Add(Button.Subtle(preset.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture) + "\u00d7", () => Playback.SetEpisodeSpeed(preset)));
            return new BoxEl { Direction = 0, Gap = Spacing.XS, Wrap = true, AlignItems = FlexAlign.Center, Children = kids.ToArray() };
        }
    }

    internal sealed record SaveProps(EntityUri Subject);
    internal sealed class SaveEpisode : Component
    {
        public override Element Render()
        {
            var p = UseProps<SaveProps>(); var e = Episode.ReaderEpisode(p.Subject);
            _ = Spotify.Podcasts.SavedChanged.Value;
            uint epoch = Entities.ScopeEpoch.Value;
            var read = UseResource(ct => Platform.Args.Fake ? Task.FromResult(new Spotify.Podcasts.Mutation(false, 404)) : Spotify.Podcasts.ReadSavedAsync(ct), new Spotify.Podcasts.Mutation(false, 0), DepKey.From((int)epoch));
            if (!Spotify.Podcasts.SavedReady)
                return Button.Subtle(Loc.Get(Strings.Podcast.Reader.SaveUnavailable), read.Refresh,
                    isEnabled: read.Loadable.State.Value != (byte)LoadState.Pending);
            bool saved = e.IsValid && Spotify.Podcasts.IsSaved(e);
            return Button.Subtle(Loc.Get(saved ? Strings.Podcast.Reader.RemoveSaved : Strings.Podcast.Reader.Save),
                () => Spotify.Podcasts.ToggleSaved(e), isEnabled: e.IsValid && !Spotify.Podcasts.SavedBusy);
        }
    }

    internal sealed record RecommendationProps(string Uri, bool Show);
    internal sealed class Recommendations : Component
    {
        readonly Signal<bool> _all = new(false);
        public override Element Render()
        {
            var p = UseProps<RecommendationProps>(); uint epoch = Entities.ScopeEpoch.Value;
            var resource = UseResource(ct => Platform.Args.Fake ? Task.FromResult(new Spotify.Podcasts.Page<Spotify.Podcasts.Recommendation>([], "", 0, "", 200)) : Spotify.Podcasts.RecommendationsAsync(p.Uri, p.Show, ct),
                new Spotify.Podcasts.Page<Spotify.Podcasts.Recommendation>([], "", 0, "", 0), epoch + ":" + p.Uri);
            var page = resource.Loadable.Value.Value;
            if (resource.Loadable.State.Value == (byte)LoadState.Pending) return Loading();
            if (!page.Ok) return Failed(resource.Refresh);
            if (page.Items.Length == 0) return new BoxEl();
            int count = _all.Value ? page.Items.Length : Math.Min(5, page.Items.Length);
            var rows = new List<Element> { Heading(Loc.Get(Strings.Podcast.Reader.Related)) };
            for (int i = 0; i < count; i++)
            {
                var item = page.Items[i];
                rows.Add(new BoxEl
                {
                    Key = item.Uri, Direction = 0, Gap = Spacing.M, Padding = Edges4.All(Spacing.S), MinWidth = 0,
                    Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, HoverFill = Tok.FillSubtleSecondary,
                    OnClick = () => { var uri = EntityUri.Parse(item.Uri); if (uri.IsValid) Shell.GoTo(Shell.For(uri, item.Title)); },
                    Children =
                    [
                        new ImageEl { Source = item.Image, Width = 56, Height = 56, DecodePx = 112, Fit = ImageFit.Cover, Corners = Radii.ControlAll },
                        new BoxEl { Direction = 1, MinWidth = 0, Grow = 1, Children = [Quiet(item.Title), Quiet(item.Subtitle)] },
                    ],
                });
            }
            if (page.Items.Length > 5) rows.Add(Button.Subtle(Loc.Get(_all.Value ? Strings.Podcast.Reader.ShowLess : Strings.Podcast.Reader.ShowAll), () => _all.Value = !_all.Peek()));
            return new BoxEl { Direction = 1, Gap = Spacing.S, MinWidth = 0, Children = rows.ToArray() };
        }
    }

    internal sealed record RatingProps(EntityUri Subject, Func<ColorF>? Tone);
    internal sealed class RatingEditor : Component
    {
        readonly Signal<bool> _busy = new(false);
        readonly Signal<string> _error = new("");
        public override Element Render()
        {
            var p = UseProps<RatingProps>(); var post = UsePost();
            uint epoch = Entities.ScopeEpoch.Value;
            _ = Entities.Current.Shows.Changed.Value;
            var show = Entities.Current.Shows.TryGetSlot(p.Subject.Id, out int slot) ? new Show(slot) : default;
            int rating = show.IsValid ? show.MyRating : 0;
            return new BoxEl
            {
                Direction = 1, Gap = Spacing.S, Width = 250, Padding = Edges4.All(Spacing.M), Children =
                [
                    Heading(Strings.Podcast.Rate.Title(show.IsValid ? show.Title : "")),
                    Controls.StarRow(rating, _busy.Value || Platform.Args.Fake ? null : value => Submit(value), 24, p.Tone),
                    Quiet(_error.Value),
                ],
            };
            async void Submit(int value)
            {
                _busy.Value = true; _error.Value = "";
                Spotify.Podcasts.Mutation result;
                try { result = await Spotify.Podcasts.RateAsync(p.Subject.Text, value, CancellationToken.None).ConfigureAwait(false); }
                catch { result = new(false, 0, true); }
                post(() =>
                {
                    if (Entities.ScopeEpoch.Peek() != epoch) return;
                    _busy.Value = false;
                    if (!result.Success) { _error.Value = Loc.Get(Strings.Podcast.Reader.MutationFailed); return; }
                    if (show.IsValid)
                    {
                        Entities.Invalidate(Entities.Current.Shows, [show.Slot], (uint)ShowFields.Rating);
                        Entities.Ensure(show, ShowFields.Rating);
                    }
                    Notify.Say(Loc.Get(Strings.Podcast.Reader.RatingSaved), InfoBarSeverity.Success);
                });
            }
        }
    }
}
