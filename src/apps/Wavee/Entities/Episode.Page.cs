using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;
using Contracts = Wavee.Theming.Contracts;
using ViewSlots = FluentGpu.Theming.Slots;

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
                About = _ => Embed.Comp(_props, static () => new EpisodeRailAbout()),
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
            if (!e.IsValid) return new BoxEl();
            bool blocked = (e.Flags & EpisodeFlags.Unplayable) != 0;
            if (blocked) return ViewSlots.View(Contracts.ViewKeys.EpisodePrimary, new Contracts.EpisodePrimaryProps(
                Loc.Get((e.Flags & EpisodeFlags.PreviewOnly) != 0 ? Strings.Podcast.Reader.Preview : Strings.Podcast.Reader.Unavailable),
                "", false, static () => { }));
            bool playing = IsNowPlaying(e) && Playback.IsPlaying.Value;
            bool resuming = e.ProgressMs > 0 && !e.Completed;
            string left = resuming ? LeftLabel(e.ProgressMs, e.DurationMs) : "";
            string label = playing ? Loc.Get(Strings.Podcast.Reader.Pause)
                : resuming ? left.Length > 0 ? Loc.Get(Strings.Podcast.Resume) + " · " + left : Loc.Get(Strings.Podcast.Resume)
                : Loc.Get(Strings.Podcast.Reader.Play);
            return ViewSlots.View(Contracts.ViewKeys.EpisodePrimary, new Contracts.EpisodePrimaryProps(
                null, label, playing, () => Invoke(e, () => StartAt(e, Playback.EpisodeStartKind.Resume))));
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
            // The anchor is the show name alone (the picture is `Episode.Attribution`'s): the name is clickable on its own without owning the
            // whole row's hit box.
            string html = "<a href=\"" + show.Uri.Text + "\">" + EscapeHtml(show.Title) + "</a>";
            return ViewSlots.View(Contracts.ViewKeys.EpisodeAttribution, new Contracts.EpisodeAttributionProps(html, DetailVerbs.NavigateKey));
        }

        static string EscapeHtml(string s) => s.IndexOfAny(['&', '<', '>']) < 0 ? s
            : s.Replace("&", "&amp;", StringComparison.Ordinal)
               .Replace("<", "&lt;", StringComparison.Ordinal)
               .Replace(">", "&gt;", StringComparison.Ordinal);
    }

    sealed class EpisodeActions : Component
    {
        public override Element Render()
        {
            var e = ReaderEpisode(UseProps<ReaderProps>().Subject);
            if (!e.IsValid) return new BoxEl();
            return ViewSlots.View(Contracts.ViewKeys.EpisodeActions, new Contracts.EpisodeActionsProps(
                Embed.Comp(new PodcastReaderUI.SaveProps(e.Uri), static () => new PodcastReaderUI.SaveEpisode()),
                e.Completed, () => Entities.MarkEpisode(e, !e.Completed), () => CopyLink(Actions.WebLinkOf(e.Uri)), () => ReaderMenu(e)));
        }
        static ContextMenuModel? ReaderMenu(Episode e)
        {
            var menu = Menu(e, new MenuOptions(ShowGoToShow: true));
            if (menu is null) return null;
            var rows = new List<MenuFlyoutItem>
            {
                new(Loc.Get(Strings.Podcast.Reader.Beginning), Icons.Play,
                    (e.Flags & EpisodeFlags.Unplayable) == 0, () => StartAt(e, Playback.EpisodeStartKind.Beginning)),
            };
            rows.AddRange(menu.Value.Rows);
            return menu.Value with { Rows = rows };
        }
    }

    /// <summary>The episode's show-notes PROSE (the recognised chapter-seek run split off) plus whether the fetch has
    /// answered at all. ONE resolution, read by both the rail's short About block and the page's About section, so the
    /// two can never disagree about what the description says.</summary>
    internal static (string Prose, bool Known) AboutProse(Episode e)
    {
        if (!e.IsValid) return ("", false);
        string html = e.HtmlDescription.Length > 0 ? e.HtmlDescription : DescriptionOf(e);
        bool known = e.Knows(EpisodeFields.About) || e.Knows(EpisodeFields.Detail);
        return (RichTextBlocks.Split(html).Prose, known);
    }

    /// <summary>The rail's SHORT about block (podcast-episode-peek §1): a small label, the description clamped to four
    /// lines, and the inline overflow affordance the clamp itself decides on — <see cref="Controls.ExpandableRichTextFlex"/>
    /// reserves "… More" on the last line ONLY when the body actually overflows and swaps it for "Less" once expanded,
    /// so a one-paragraph episode gets no dead button. The SAME block parser the About section runs, so a link in the
    /// rail is the same link; the full text, the chapter seek list and everything else stay in the About section.
    /// <para>Nothing until the description has answered: an unanswered fetch renders an empty box rather than a label
    /// over a blank, and the rail's <c>LateRow</c> fades the block in when it lands.</para></summary>
    sealed class EpisodeRailAbout : Component
    {
        public override Element Render()
        {
            var e = ReaderEpisode(UseProps<ReaderProps>().Subject);
            var (prose, known) = AboutProse(e);
            if (!known || string.IsNullOrWhiteSpace(prose)) return new BoxEl();
            return ViewSlots.View(Contracts.ViewKeys.EpisodeRailAbout, new Contracts.EpisodeRailAboutProps(
                prose, "rail-about:" + e.Uri.Text, DetailVerbs.NavigateKey));
        }
    }

    /// <summary>About · Chapters · Transcript · Comments as ONE scroll-spy pivot (plan §5.1), the same shape
    /// `ClassicArtistPage`'s BandBar and Artist.Page.cs's ResolveSpy and Detail.UI.Hero.cs use: every section mounts STACKED in one scroll
    /// (no tab switch, no unmount), the pivot's underline follows the scroll position through the engine-free
    /// <see cref="Detail.ScrollSpy.ActiveSectionOf"/>, and a pivot click still reveals its section (bring-into-view).
    /// Mounted in a `ClassicDetailBand.Band`-geometry row at the top of the Episodes slot, so the header height and
    /// pivot metrics are the same tokens Show/Album/Artist's own bands use.</summary>
    sealed class EpisodeReader : Component
    {
        const int SectionCount = 4;
        readonly Signal<int> _active = new(0);
        readonly Signal<float> _scrollY = new(0f);
        readonly Signal<float> _viewportH = new(0f);
        readonly NodeHandle[] _anchors = new NodeHandle[SectionCount];
        readonly Action<NodeHandle>[] _anchorRealized = new Action<NodeHandle>[SectionCount];
        readonly Action[] _sectionClicks = new Action[SectionCount];
        NodeHandle _viewport;
        readonly Action<NodeHandle> _captureViewport;
        readonly Action _resolveSpy, _watchScroll;
        /// <summary>The reader viewport's scroll handle; the spy's coarse geometry is projected off it.</summary>
        readonly ScrollHandle _scroll = new();
        long _scrollKey = long.MinValue;

        // ── row 2 (P5 Z6b2, contract 1.26): a band-style structure (Zune) draws the four words in its band's row 2 and its reader draws no pivot,
        // so the reader publishes them through the `PageChromeSink` the content host provides around the page: one view per section, the spy's
        // answer current, a click scrolling its section. Gated on what they are built from (the episode, the current section). Classic draws its
        // own pivot and reads none of it. ──
        Contracts.PageChromeSink _sink = Contracts.PageChromeSink.None;
        readonly Action _publishChrome;
        string _chromeUri = "";
        string? _chromeTitle;   // the episode's name, row 2's title (contract 1.30)
        long _chromeSig = long.MinValue;

        public EpisodeReader()
        {
            _publishChrome = PublishChrome;
            for (int i = 0; i < SectionCount; i++)
            {
                int index = i;
                _anchorRealized[i] = h => _anchors[index] = h;
                _sectionClicks[i] = () => GoToSection(index);
            }
            _captureViewport = h => _viewport = h;
            _resolveSpy = ResolveSpy;
            // A COARSE key: the 24-DIP write floor and the viewport height in 4-DIP steps.
            _watchScroll = () =>
            {
                float y = (float)_scroll.Offset.Value, vh = (float)_scroll.ViewportSignal.Value;
                long key = HashCode.Combine((int)(y / 24f), (int)MathF.Round(vh / Spacing.XS));
                if (key == _scrollKey) return;
                _scrollKey = key;
                _scrollY.Value = y;
                _viewportH.SetIfChanged(vh);
            };
        }

        public override Element Render()
        {
            var p = UseProps<ReaderProps>(); var e = ReaderEpisode(p.Subject);
            uint epoch = Entities.ScopeEpoch.Value;
            if (!e.IsValid) return new BoxEl();
            string key = epoch + ":" + p.Subject.Text;
            UseEffect(_resolveSpy);   // auto-tracks _scrollY/_viewportH — re-answers on every scroll step let through
            UseSignalEffect(_watchScroll);
            _sink = UseContext(Contracts.PageChromeSink.Context);
            _chromeUri = e.Uri.Text;
            _chromeTitle = e.Knows(EpisodeFields.Title) && e.Title.Length > 0 ? e.Title : null;
            UseEffect(_publishChrome);   // auto-tracks the spy's answer (_active) — row 2 follows the scroll

            return ViewSlots.View(Contracts.ViewKeys.EpisodeReader, new Contracts.EpisodeReaderProps(
                _sectionClicks, _active, _anchorRealized,
                About: ViewSlots.View(Contracts.ViewKeys.EpisodeAbout, AboutProps(e)),
                Related: Embed.Comp(new PodcastReaderUI.RecommendationProps(p.Subject.Text, false),
                    static () => new PodcastReaderUI.Recommendations()) with { Key = "related:" + key },
                // The sections' shapes for a loading page's derived shimmer (audit B7): the chapter rows and the placeholder comments at their size,
                // never the deriver's one default bar for an opaque component.
                Chapters: Embed.Comp(p, static () => new ChapterReader()) with { Key = "chapters:" + key, SkeletonProxy = ChapterReader.Shape },
                Transcript: Embed.Comp(p, static () => new TranscriptReader()) with { Key = "transcript:" + key },
                Comments: Embed.Comp(new PodcastReaderUI.DiscussionProps(p.Subject.Text, false),
                    static () => new PodcastReaderUI.Discussion()) with { Key = "comments:" + key, SkeletonProxy = s_commentsShape },
                Key: key, ScrollKey: UseContext(Shell.PageScrollScope) + key, CaptureViewport: _captureViewport, Scroll: _scroll));
        }

        /// <summary>Publishes row 2: About · Chapters · Transcript · Comments, the spy's section current (see the field block).</summary>
        void PublishChrome()
        {
            int active = _active.Value;
            if (_chromeUri.Length == 0) return;
            long sig = HashCode.Combine(_chromeUri, active, _chromeTitle);
            if (sig == _chromeSig) return;
            _chromeSig = sig;
            var views = new Contracts.PageView[SectionCount]
            {
                new("about", Loc.Get(Strings.Podcast.Tabs.About), active == 0, _sectionClicks[0]),
                new("chapters", Loc.Get(Strings.Podcast.Tabs.Chapters), active == 1, _sectionClicks[1]),
                new("transcript", Loc.Get(Strings.Podcast.Tabs.Transcript), active == 2, _sectionClicks[2]),
                new("comments", Loc.Get(Strings.Podcast.Tabs.Comments), active == 3, _sectionClicks[3]),
            };
            _sink.Publish(new Contracts.PageChromeRow(views, [], Title: _chromeTitle));
        }

        void GoToSection(int section)
        {
            var scene = Context.Scene;
            var node = _anchors[section];
            if (scene is null || node.IsNull || _viewport.IsNull || !scene.IsLive(node) || !scene.IsLive(_viewport)) return;
            scene.BringIntoView(_viewport, node, align: 0f, Design.Reduced ? ScrollMove.Immediate : ScrollMove.Glide,
                margin: Detail.BandLayout.Height);
        }

        /// <summary>THE SPY: Detail.ScrollSpy.ActiveSectionOf over each anchor's viewport-relative top (the SAME
        /// "quarter viewport" line Artist's BandLayout.ActiveSection uses, with no collapsing band to offset it).</summary>
        void ResolveSpy()
        {
            _ = _scrollY.Value;
            _ = _viewportH.Value;
            var scene = Context.Scene;
            if (scene is null || _viewport.IsNull || !scene.IsLive(_viewport)) return;
            RectF vp = scene.AbsoluteRect(_viewport);
            Span<float> tops = stackalloc float[SectionCount];
            for (int i = 0; i < SectionCount; i++)
            {
                var node = _anchors[i];
                tops[i] = node.IsNull || !scene.IsLive(node) ? float.NaN : scene.AbsoluteRect(node).Y - vp.Y;
            }
            float viewportHeight = _viewportH.Peek();
            if (viewportHeight <= 0f) viewportHeight = vp.H;
            int at = Detail.ScrollSpy.ActiveSectionOf(tops, 0f, viewportHeight, _scrollY.Peek());
            if (at >= 0) _active.SetIfChanged(at);
        }

        /// <summary>The About section's props: the description's load state is the host's decision (answered, failed, or still pending the
        /// shimmer's loading text), what the quiet notes say, and the verbs a link, a retry and a chapter seek call.</summary>
        static Contracts.EpisodeAboutProps AboutProps(Episode e)
        {
            string html = e.HtmlDescription.Length > 0 ? e.HtmlDescription : DescriptionOf(e);
            bool known = e.Knows(EpisodeFields.About) || e.Knows(EpisodeFields.Detail);
            const string seed = "Episode description and show notes. More about the conversation and the ideas explored in this episode.\n\nRead the full story, follow the links, and discover more from this show.";
            bool failed = Entities.Current.Episodes.IsFailed(e.Slot, (uint)EpisodeFields.About)
                && Entities.Current.Episodes.IsFailed(e.Slot, (uint)EpisodeFields.Detail);
            var description = known ? Loadable<string>.Ready(html) : failed
                ? Loadable<string>.Failed(new InvalidOperationException("Episode description unavailable.")) : Loadable<string>.Pending(seed);
            return new Contracts.EpisodeAboutProps(description, seed, e.Uri.Text,
                (e.Flags & EpisodeFlags.PreviewOnly) != 0, (e.Flags & EpisodeFlags.Paywalled) != 0,
                () => Entities.Refresh(Entities.Current.Episodes, [e.Slot], (uint)(EpisodeFields.About | EpisodeFields.Detail)),
                DetailVerbs.NavigateKey, ms => StartAt(e, Playback.EpisodeStartKind.Position, ms), Playback.IsPlaying);
        }
    }

    static readonly Func<Element> s_commentsShape = static () => PodcastReaderUI.CommentSeedView(false);

    sealed class ChapterReader : Component
    {
        static readonly Action s_noop = static () => { };

        /// <summary>The section's shape for a derived shimmer: its content over the seed chapters (what its own region shimmers).</summary>
        internal static readonly Func<Element> Shape = static () => Content(ChapterResources.Seed, default, s_noop);

        ChapterResources.Lease? _lease;
        ChapterResources.Identity _identity;
        public override Element Render()
        {
            var p = UseProps<ReaderProps>(); var e = ReaderEpisode(p.Subject);
            uint epoch = Entities.ScopeEpoch.Value;
            var identity = new ChapterResources.Identity(epoch, Entities.Current.Key.Account, e.Id);
            if (_lease is null || _identity != identity)
            {
                _lease?.Dispose(); _identity = identity;
                _lease = ChapterResources.Shared.Acquire(identity);
            }
            UseLayoutEffect(() => _lease?.Start(), DepKey.From((int)epoch, e.Slot));
            UseEffect(() => () => _lease?.Dispose(), DepKey.Empty);
            var lease = _lease;
            return Skel.Region(lease.Data, page => Content(page, e, lease.Refresh),
                onFailed: () => PodcastReaderUI.Failed(lease.Refresh));
        }
        static Element Content(Spotify.Podcasts.Page<Spotify.Podcasts.Chapter> page, Episode e, Action retry)
        {
            if (!page.Ok) return PodcastReaderUI.Failed(retry);
            if (page.Items.Length == 0) return PodcastReaderUI.Quiet(Loc.Get(Strings.Podcast.Reader.NoChapters));
            int position = IsNowPlaying(e) ? Playback.PositionMs.Value : -1;
            int activeIndex = PodcastReaderRules.CurrentChapter(page.Items, position);
            var rows = new Contracts.EpisodeChapterRow[page.Items.Length];
            for (int i = 0; i < rows.Length; i++)
            {
                var chapter = page.Items[i];
                // Report 6d: no synthetic "Chapters N" fallback — a chapter with no title (D wave still filling
                // titles from kind 178) shows its start time alone rather than inventing a label the wire never sent.
                rows[i] = new Contracts.EpisodeChapterRow(chapter.Uri, chapter.StartMs, chapter.Title, activeIndex == i);
            }
            // Derivable: the region's shimmer is the theme's chapter rows over the seed's, at their size, not one default bar (audit B7).
            return ViewSlots.DerivableView(Contracts.ViewKeys.EpisodeChapters, new Contracts.EpisodeChaptersProps(
                rows, ms => StartAt(e, Playback.EpisodeStartKind.Position, ms), Playback.IsPlaying));
        }
    }
}
