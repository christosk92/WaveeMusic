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
    public static Element Page(in Shell.Route route)
    {
        var props = new ReaderProps(route.Subject, Shell.NameOf(route));
        return Embed.Comp(props, static () => new EpisodePage()) with { Key = "episode-page:" + props.RouteKey };
    }

    internal sealed record ReaderProps(EntityUri Subject, string RouteKey);

    sealed class EpisodePage : Component
    {
        ReaderProps _props = new(default, "");
        Episode _episode;
        Scope? _scope;
        readonly Detail.FrameSlots _slots;
        public EpisodePage()
        {
            _slots = new Detail.FrameSlots
            {
                Episodes = _ => Embed.Comp(_props, static () => new EpisodeReader()),
                Primary = _ => Embed.Comp(_props, static () => new EpisodePrimary()),
                Attribution = _ => Embed.Comp(_props, static () => new EpisodeAttribution()),
                Satellites = () => [Embed.Comp(_props, static () => new EpisodeActions())],
            };
        }
        public override Element Render()
        {
            _props = UseProps<ReaderProps>();
            uint epoch = Entities.ScopeEpoch.Value;
            _ = Entities.Current.Episodes.Changed.Value;
            _ = Entities.Current.Shows.Changed.Value;
            if (!ReferenceEquals(_scope, Entities.Current))
            {
                _scope = Entities.Current;
                _episode = Entities.Episode(_props.Subject);
            }
            var episode = _episode;
            UseEffect(() =>
            {
                if (episode.IsValid) Entities.Ensure(episode, EpisodeFields.All);
            }, DepKey.From(episode.Slot, (int)epoch));
            UseEffect(() =>
            {
                _ = Entities.Current.Episodes.Changed.Value;
                if (_episode.IsValid && _episode.Show.IsValid) Entities.Ensure(_episode.Show, ShowFields.Identity | ShowFields.Rating);
            });
            if (!episode.IsValid) return Controls.Vacancy(Controls.VacancyVoice.Error);
            if (!episode.Knows(EpisodeFields.Title) && Entities.Current.Episodes.IsFailed(episode.Slot, (uint)EpisodeFields.All))
                return PodcastReaderUI.Failed(() => Entities.Refresh(Entities.Current.Episodes, [episode.Slot], (uint)EpisodeFields.All));
            return Detail.Frame(new Detail.FrameSpec
            {
                Config = Detail.Config.Episode,
                RouteKey = _props.RouteKey,
                Identity = new Detail.Identity
                {
                    Subject = episode.Uri, Kind = DetailKind.Episode, Title = TitleOf(episode),
                    CoverUrl = ArtOf(episode), HeaderPending = !episode.Knows(EpisodeFields.Title),
                    Eyebrow = Loc.Get(Strings.Nav.Episode),
                    Meta = DateLabel(episode.PublishedAt) + " \u00b7 " + DurationLabel(episode.DurationMs),
                    MetaLoading = !episode.Knows(EpisodeFields.Duration),
                    ShareUrl = Actions.WebLinkOf(episode.Uri),
                },
                Slots = _slots,
            });
        }
    }

    internal static Episode ReaderEpisode(EntityUri uri)
    {
        _ = Entities.ScopeEpoch.Value;
        _ = Entities.Current.Episodes.Changed.Value;
        return Entities.Current.Episodes.TryGetSlot(uri.Id, out int slot) ? new Episode(slot) : default;
    }

    internal static void StartAt(Episode episode, Playback.EpisodeStartKind kind, int ms = 0)
    {
        if (!episode.IsValid || (episode.Flags & EpisodeFlags.Unplayable) != 0) return;
        Playback.PlayEpisode(episode.Id, episode.Show.IsValid ? episode.Show.Id : episode.Id, new Playback.EpisodeStart(kind, ms));
    }

    sealed class EpisodePrimary : Component
    {
        public override Element Render()
        {
            var p = UseProps<ReaderProps>(); var e = ReaderEpisode(p.Subject);
            var accent = UseContext(Design.AccentCtx.Slot);
            if (!e.IsValid) return new BoxEl();
            bool blocked = (e.Flags & EpisodeFlags.Unplayable) != 0;
            if (blocked) return PodcastReaderUI.Quiet(Loc.Get((e.Flags & EpisodeFlags.PreviewOnly) != 0
                ? Strings.Podcast.Reader.Preview : Strings.Podcast.Reader.Unavailable));
            bool playing = IsNowPlaying(e) && Playback.IsPlaying.Value;
            string label = playing ? Loc.Get(Strings.Podcast.Reader.Pause) : e.ProgressMs > 0 && !e.Completed
                ? Loc.Get(Strings.Podcast.Resume) : Loc.Get(Strings.Podcast.Reader.Play);
            return Detail.PlayPill(() => accent?.Value.Fill ?? Tok.AccentDefault,
                () => Invoke(e, () => StartAt(e, Playback.EpisodeStartKind.Resume)), label, playing ? Icons.Pause : Icons.Play);
        }
    }

    sealed class EpisodeAttribution : Component
    {
        public override Element Render()
        {
            var e = ReaderEpisode(UseProps<ReaderProps>().Subject);
            _ = Entities.Current.Shows.Changed.Value;
            if (!e.IsValid || !e.Show.IsValid) return new BoxEl();
            var show = e.Show;
            return Button.Subtle(show.Title, () => Shell.GoTo(Shell.For(show.Uri, show.Title)));
        }
    }

    sealed class EpisodeActions : Component
    {
        public override Element Render()
        {
            var e = ReaderEpisode(UseProps<ReaderProps>().Subject);
            if (!e.IsValid) return new BoxEl();
            return new BoxEl
            {
                Direction = 0, Gap = Spacing.XS, Wrap = true, Children =
                [
                    Button.Subtle(Loc.Get(e.Completed ? Strings.Podcast.Menu.MarkUnplayed : Strings.Podcast.Menu.MarkPlayed),
                        () => Entities.MarkEpisode(e, !e.Completed)),
                    Button.Subtle(Loc.Get(Strings.Menu.CopyLink), () => CopyLink(Actions.WebLinkOf(e.Uri))),
                    Embed.Comp(new PodcastReaderUI.SaveProps(e.Uri), static () => new PodcastReaderUI.SaveEpisode()),
                ],
            };
        }
    }

    sealed class EpisodeReader : Component
    {
        readonly Signal<int> _tab = new(0);
        public override Element Render()
        {
            var p = UseProps<ReaderProps>(); var e = ReaderEpisode(p.Subject);
            uint epoch = Entities.ScopeEpoch.Value;
            if (!e.IsValid) return new BoxEl();
            string key = epoch + ":" + p.Subject.Text;
            string[] labels = [Loc.Get(Strings.Podcast.Reader.About), Loc.Get(Strings.Podcast.Reader.Chapters),
                Loc.Get(Strings.Podcast.Reader.Transcript), Loc.Get(Strings.Podcast.Reader.Comments)];
            var tabs = new Element[labels.Length];
            for (int i = 0; i < tabs.Length; i++)
            {
                int n = i;
                tabs[i] = Button.Create(labels[i], () => _tab.Value = n,
                    _tab.Value == i ? ButtonAppearance.Accent : ButtonAppearance.Subtle);
            }
            Element section = _tab.Value switch
            {
                1 => Embed.Comp(p, static () => new ChapterReader()) with { Key = "chapters:" + key },
                2 => Embed.Comp(p, static () => new TranscriptReader()) with { Key = "transcript:" + key },
                3 => Embed.Comp(new PodcastReaderUI.DiscussionProps(p.Subject.Text, false), static () => new PodcastReaderUI.Discussion()) with { Key = "comments:" + key },
                _ => About(e),
            };
            return ScrollView(new BoxEl
            {
                Direction = 1, Gap = Spacing.L, MinWidth = 0, Padding = Edges4.All(Spacing.L), Children =
                [
                    new BoxEl { Direction = 0, Gap = Spacing.XS, Wrap = true, Children = tabs },
                    Embed.Comp(static () => new PodcastReaderUI.SpeedControl()),
                    section,
                    Embed.Comp(new PodcastReaderUI.RecommendationProps(p.Subject.Text, false), static () => new PodcastReaderUI.Recommendations()) with { Key = "related:" + key },
                    new BoxEl { Height = Show.BottomReserve },
                ],
            }) with { Grow = 1, MinHeight = 0, ScrollKey = "episode:" + p.RouteKey };
        }
        static Element About(Episode e)
        {
            string html = e.HtmlDescription.Length > 0 ? e.HtmlDescription : DescriptionOf(e);
            var children = new List<Element>
            {
                PodcastReaderUI.Heading(Loc.Get(Strings.Podcast.Reader.About)),
                Controls.RichTextFlex(html, 14, Tok.TextSecondary, Tok.AccentTextPrimary, 0, static key => Shell.GoTo(Shell.Parse(key))),
                Button.Standard(Loc.Get(Strings.Podcast.Reader.Beginning), () => StartAt(e, Playback.EpisodeStartKind.Beginning),
                    isEnabled: (e.Flags & EpisodeFlags.Unplayable) == 0),
            };
            if ((e.Flags & EpisodeFlags.PreviewOnly) != 0) children.Add(PodcastReaderUI.Quiet(Loc.Get(Strings.Podcast.Reader.Preview)));
            if ((e.Flags & EpisodeFlags.Paywalled) != 0) children.Add(PodcastReaderUI.Quiet(Loc.Get(Strings.Podcast.Reader.Paywall)));
            if ((e.Flags & EpisodeFlags.Video) != 0) children.Add(PodcastReaderUI.Quiet(Loc.Get(Strings.Podcast.Reader.Video)));
            if (e.ProgressMs > 0 || e.Completed) children.Add(ProgressRule(ReaderPctOf(e), () => Tok.AccentDefault, 3));
            return new BoxEl { Direction = 1, Gap = Spacing.M, MinWidth = 0, Children = children.ToArray() };
        }
    }

    sealed class ChapterReader : Component
    {
        public override Element Render()
        {
            var p = UseProps<ReaderProps>(); var e = ReaderEpisode(p.Subject);
            var resource = UseResource(ct => Platform.Args.Fake ? Task.FromResult(PodcastReaderFixtures.Chapters) : Spotify.Podcasts.ChaptersAsync(p.Subject.Text, ct),
                new Spotify.Podcasts.Page<Spotify.Podcasts.Chapter>([], "", 0, "", 0), p.Subject.Text);
            var page = resource.Loadable.Value.Value;
            if (resource.Loadable.State.Value == (byte)LoadState.Pending) return PodcastReaderUI.Loading();
            if (!page.Ok) return PodcastReaderUI.Failed(resource.Refresh);
            if (page.Items.Length == 0) return PodcastReaderUI.Quiet(Loc.Get(Strings.Podcast.Reader.NoChapters));
            int position = IsNowPlaying(e) ? Playback.PositionMs.Value : -1;
            int activeIndex = PodcastReaderRules.CurrentChapter(page.Items, position);
            var rows = new Element[page.Items.Length];
            for (int i = 0; i < rows.Length; i++)
            {
                var chapter = page.Items[i];
                bool active = activeIndex == i;
                rows[i] = Button.Create(FormatCache.DurationMmSs(chapter.StartMs) + "  " + chapter.Title,
                    () => StartAt(e, Playback.EpisodeStartKind.Position, chapter.StartMs), active ? ButtonAppearance.Accent : ButtonAppearance.Subtle);
            }
            return new BoxEl { Direction = 1, Gap = Spacing.XS, MinWidth = 0, Children = rows };
        }
    }
}
