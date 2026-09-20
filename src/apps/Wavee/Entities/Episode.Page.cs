using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
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
            if (blocked) return PodcastReaderUI.Quiet(Loc.Get((e.Flags & EpisodeFlags.PreviewOnly) != 0
                ? Strings.Podcast.Reader.Preview : Strings.Podcast.Reader.Unavailable));
            bool playing = IsNowPlaying(e) && Playback.IsPlaying.Value;
            bool resuming = e.ProgressMs > 0 && !e.Completed;
            string left = resuming ? LeftLabel(e.ProgressMs, e.DurationMs) : "";
            string label = playing ? Loc.Get(Strings.Podcast.Reader.Pause)
                : resuming ? left.Length > 0 ? Loc.Get(Strings.Podcast.Resume) + " · " + left : Loc.Get(Strings.Podcast.Resume)
                : Loc.Get(Strings.Podcast.Reader.Play);
            // W11d: the app accent, never the show/episode tone — Design.AccentCtx carries the entity's own
            // art-derived tone (the leak the video frame caught as a pink Resume pill on this exact pill). A media
            // transport verb should not repaint with the cover; Detail.PlayPill's default (accent()==Tok.AccentDefault
            // here) is the one blue every other primary action already uses.
            return Detail.PlayPill(() => Tok.AccentDefault,
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
            // The shared inline link row (Controls.Art.cs:1119 RichTextRow), not a stretched full-bleed Hyperlink box:
            // one ellipsised line under the title, the same idiom an album's artist line uses. RichTextRow parses the
            // anchor through ParseRich, so the show name is clickable on its own without owning the whole row's hit box.
            string html = "<a href=\"" + show.Uri.Text + "\">" + EscapeHtml(show.Title) + "</a>";
            return Controls.RichTextRow(html, 14, Tok.TextSecondary, Tok.AccentTextPrimary,
                static key => Shell.GoTo(Shell.Parse(key)));
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
            return new BoxEl
            {
                Direction = 0, Gap = Spacing.XS, Wrap = true, Children =
                [
                    Embed.Comp(new PodcastReaderUI.SaveProps(e.Uri), static () => new PodcastReaderUI.SaveEpisode()),
                    Detail.RailSatellite(e.Completed ? Icons.Undo : Icons.Check,
                        Loc.Get(e.Completed ? Strings.Podcast.Menu.MarkUnplayed : Strings.Podcast.Menu.MarkPlayed),
                        () => Entities.MarkEpisode(e, !e.Completed)),
                    Detail.RailSatellite(Icons.Share, Loc.Get(Strings.Menu.CopyLink),
                        () => CopyLink(Actions.WebLinkOf(e.Uri))),
                    Detail.RailSatelliteMore(() => ReaderMenu(e)),
                ],
            };
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
        const int ClampLines = 4;
        public override Element Render()
        {
            var e = ReaderEpisode(UseProps<ReaderProps>().Subject);
            var (prose, known) = AboutProse(e);
            if (!known || string.IsNullOrWhiteSpace(prose)) return new BoxEl();
            return new BoxEl
            {
                Direction = 1, MinWidth = 0, Gap = Spacing.S, Margin = new Edges4(0, Spacing.XS, 0, 0), Children =
                [
                    new BoxEl { Height = 1, Shrink = 0, MinWidth = 0, Fill = Tok.StrokeDividerDefault },
                    Design.Type.Eyebrow(Loc.Get(Strings.Podcast.RailAbout)) with { Color = Tok.TextTertiary },
                    Controls.ExpandableRichTextFlex(prose, 13, Tok.TextSecondary, Tok.AccentTextPrimary, ClampLines,
                        "rail-about:" + e.Uri.Text, static key => Shell.GoTo(Shell.Parse(key))),
                ],
            };
        }
    }

    /// <summary>About · Chapters · Transcript · Comments as ONE scroll-spy pivot (plan §5.1), the same shape
    /// Artist.Page.cs's BandBar/ResolveSpy and Detail.UI.Hero.cs use: every section mounts STACKED in one scroll
    /// (no tab switch, no unmount), the pivot's underline follows the scroll position through the engine-free
    /// <see cref="Detail.ScrollSpy.ActiveSectionOf"/>, and a pivot click still reveals its section (bring-into-view).
    /// Mounted in a <see cref="Detail.Band"/>-geometry row at the top of the Episodes slot, so the header height and
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
        readonly Action _resolveSpy;
        readonly Func<ScrollGeometry, long> _projectScroll;
        readonly Action<ScrollGeometry> _onScroll;

        public EpisodeReader()
        {
            for (int i = 0; i < SectionCount; i++)
            {
                int index = i;
                _anchorRealized[i] = h => _anchors[index] = h;
                _sectionClicks[i] = () => GoToSection(index);
            }
            _captureViewport = h => _viewport = h;
            _resolveSpy = ResolveSpy;
            _projectScroll = static g => HashCode.Combine((int)(g.OffsetY / 24f), (int)MathF.Round(g.ViewportH / Spacing.XS));
            _onScroll = g => { _scrollY.Value = g.OffsetY; _viewportH.SetIfChanged(g.ViewportH); };
        }

        public override Element Render()
        {
            var p = UseProps<ReaderProps>(); var e = ReaderEpisode(p.Subject);
            uint epoch = Entities.ScopeEpoch.Value;
            if (!e.IsValid) return new BoxEl();
            string key = epoch + ":" + p.Subject.Text;
            UseEffect(_resolveSpy);   // auto-tracks _scrollY/_viewportH — re-answers on every scroll step let through

            (string Label, Action OnClick)[] pivotItems =
            [
                (Loc.Get(Strings.Podcast.Reader.About), _sectionClicks[0]),
                (Loc.Get(Strings.Podcast.Reader.Chapters), _sectionClicks[1]),
                (Loc.Get(Strings.Podcast.Reader.Transcript), _sectionClicks[2]),
                (Loc.Get(Strings.Podcast.Reader.Comments), _sectionClicks[3]),
            ];
            Element band = new BoxEl
            {
                Height = Detail.BandLayout.Height, Shrink = 0, MinWidth = 0, Direction = 1, Children =
                [
                    Detail.Band(float.NaN, Spacing.L, [Detail.Pivot(pivotItems, _active, static () => Tok.AccentDefault)])
                        with { Grow = 1, Basis = 0, MinWidth = 0 },
                    Detail.BandHairline(),
                ],
            };

            Element sections = new BoxEl
            {
                Direction = 1, MinWidth = 0, Padding = Edges4.All(Spacing.L), Gap = Spacing.XXL, Children =
                [
                    Section(0, PodcastReaderUI.Heading(Loc.Get(Strings.Podcast.Reader.About)), new BoxEl
                    {
                        Direction = 1, Gap = Spacing.XL, MinWidth = 0, Children =
                        [
                            About(e),
                            Embed.Comp(new PodcastReaderUI.RecommendationProps(p.Subject.Text, false),
                                static () => new PodcastReaderUI.Recommendations()) with { Key = "related:" + key },
                        ],
                    }),
                    Section(1, PodcastReaderUI.Heading(Loc.Get(Strings.Podcast.Reader.Chapters)),
                        Embed.Comp(p, static () => new ChapterReader()) with { Key = "chapters:" + key }),
                    Section(2, PodcastReaderUI.Heading(Loc.Get(Strings.Podcast.Reader.Transcript)), new BoxEl
                    {
                        // The transcript view manages its own internal (virtualized) scroll — Lyrics.Transcript.cs's
                        // ViewCore is shared with the rail/expanded player and expects a bounded ancestor. A scroll
                        // within THIS page scroll (rather than an unbounded natural height) is the honest tradeoff:
                        // every other section is truly stacked/natural-height, this one nests its own scroller.
                        Direction = 1, Height = 560, MinHeight = 0, MinWidth = 0, ClipToBounds = true, Children =
                        [Embed.Comp(p, static () => new TranscriptReader()) with { Key = "transcript:" + key }],
                    }),
                    Section(3, null, Embed.Comp(new PodcastReaderUI.DiscussionProps(p.Subject.Text, false),
                        static () => new PodcastReaderUI.Discussion()) with { Key = "comments:" + key }),
                ],
            };

            return new BoxEl
            {
                Direction = 1, Grow = 1, MinWidth = 0, MinHeight = 0, ClipToBounds = true, Children =
                [
                    band,
                    ScrollView(sections) with
                    {
                        Key = "episode-scroll:" + key, Grow = 1, MinHeight = 0, ScrollKey = key,
                        AutoEdgeFade = true, AutoEdgeFadeBand = 24f,
                        OnRealized = _captureViewport, OnScrollGeometryChanged = (_projectScroll, _onScroll),
                    },
                ],
            };
        }

        // Comments already renders its own "Comments (n)" heading (PodcastReaderUI.Discussion.Build) — no double heading.
        // OnRealized captures this section's anchor node for the spy/GoToSection — the outer box IS the anchor, so
        // its top is exactly what a pivot click parks under the band.
        Element Section(int index, Element? heading, Element body) => new BoxEl
        {
            Direction = 1, Gap = Spacing.M, MinWidth = 0, MaxWidth = 760, OnRealized = _anchorRealized[index],
            Children = heading is null ? [body] : [heading, body],
        };

        void GoToSection(int section)
        {
            var scene = Context.Scene;
            var node = _anchors[section];
            if (scene is null || node.IsNull || _viewport.IsNull || !scene.IsLive(node) || !scene.IsLive(_viewport)) return;
            FluentGpu.Scroll.ScrollIntoView.BringInto(Context, _viewport, node, margin: Detail.BandLayout.Height,
                alignmentRatio: 0f, animate: !Design.Reduced);
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
            int at = Detail.ScrollSpy.ActiveSectionOf(tops, 0f, viewportHeight);
            if (at >= 0) _active.SetIfChanged(at);
        }

        static Element About(Episode e)
        {
            string html = e.HtmlDescription.Length > 0 ? e.HtmlDescription : DescriptionOf(e);
            bool known = e.Knows(EpisodeFields.About) || e.Knows(EpisodeFields.Detail);
            const string seed = "Episode description and show notes. More about the conversation and the ideas explored in this episode.\n\nRead the full story, follow the links, and discover more from this show.";
            bool failed = Entities.Current.Episodes.IsFailed(e.Slot, (uint)EpisodeFields.About)
                && Entities.Current.Episodes.IsFailed(e.Slot, (uint)EpisodeFields.Detail);
            var description = known ? Loadable<string>.Ready(html) : failed
                ? Loadable<string>.Failed(new InvalidOperationException("Episode description unavailable.")) : Loadable<string>.Pending(seed);
            var children = new List<Element>
            {
                Skel.Region(description,
                    () => Controls.RichTextFlex(seed, 14, Tok.TextSecondary, Tok.AccentTextPrimary, 8),
                    text => AboutBody(text, e),
                    onFailed: () => PodcastReaderUI.Failed(() => Entities.Refresh(Entities.Current.Episodes, [e.Slot],
                        (uint)(EpisodeFields.About | EpisodeFields.Detail))),
                    isEmpty: string.IsNullOrWhiteSpace,
                    onEmpty: () => PodcastReaderUI.Quiet(Loc.Get(Strings.Podcast.Reader.Empty))),
            };
            if ((e.Flags & EpisodeFlags.PreviewOnly) != 0) children.Add(PodcastReaderUI.Quiet(Loc.Get(Strings.Podcast.Reader.Preview)));
            if ((e.Flags & EpisodeFlags.Paywalled) != 0) children.Add(PodcastReaderUI.Quiet(Loc.Get(Strings.Podcast.Reader.Paywall)));
            return new BoxEl { Direction = 1, Gap = Spacing.M, MinWidth = 0, MaxWidth = 760, Children = children.ToArray() };
        }

        /// <summary>The 8-line collapsible prose PLUS, when the show-notes text carries the CHAPTERS(hh:mm:ss)/bare
        /// (hh:mm:ss) convention (<see cref="RichTextBlocks"/>), a seek list under it — each entry starts the episode
        /// at that position if it is not already playing, or seeks it there if it is (Episode.StartAt, the existing
        /// play-at-position intent; PlayEpisode is a no-op restart when it is already the current episode).</summary>
        static Element AboutBody(string text, Episode e)
        {
            var (prose, chapters) = RichTextBlocks.Split(text);
            var rows = new List<Element>
            {
                Controls.ExpandableRichTextFlex(prose, 14, Tok.TextSecondary, Tok.AccentTextPrimary, 8,
                    e.Uri.Text, static key => Shell.GoTo(Shell.Parse(key))),
            };
            if (chapters.Length > 0)
            {
                var links = new Element[chapters.Length];
                for (int i = 0; i < links.Length; i++)
                {
                    var link = chapters[i];
                    links[i] = ChapterRow(e.Uri.Text + ":notes-chapter:" + i, link.StartMs, link.Title,
                        active: false, () => StartAt(e, Playback.EpisodeStartKind.Position, link.StartMs));
                }
                rows.Add(new BoxEl { Direction = 1, Gap = Spacing.XXS, MinWidth = 0, Children = links });
            }
            return new BoxEl { Direction = 1, Gap = Spacing.M, MinWidth = 0, Children = rows.ToArray() };
        }

        /// <summary>One chapter/timestamp row, shared by the Chapters section and the description's recognised
        /// CHAPTERS seek list: a fixed-width timestamp, the title, the active tint, and a click that seeks.</summary>
        internal static Element ChapterRow(string key, int startMs, string title, bool active, Action onClick) => new BoxEl
        {
            Key = key, Direction = 0, MinWidth = 0, Gap = Spacing.M,
            Padding = Edges4.All(Spacing.M), Corners = Radii.ControlAll,
            Fill = active ? Tok.FillSubtleSecondary : ColorF.Transparent,
            HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = onClick,
            Children =
            [
                active
                    ? new BoxEl { Width = 64, Shrink = 0, Direction = 0, Gap = Spacing.XS, AlignItems = FlexAlign.Center, Children =
                        [Controls.Equalizer(Playback.IsPlaying, Tok.AccentTextPrimary, 13f),
                         new TextEl(PodcastReaderRules.Timestamp(startMs)) { Size = 13, Color = Tok.AccentTextPrimary }] }
                    : new TextEl(PodcastReaderRules.Timestamp(startMs)) { Width = 64, Shrink = 0, Size = 13, Color = Tok.TextSecondary },
                title.Length > 0
                    ? new TextEl(title) { Grow = 1, Basis = 0, MinWidth = 0, Wrap = TextWrap.Wrap, Size = 14, LineHeight = 20,
                        Color = active ? Tok.AccentTextPrimary : Tok.TextPrimary }
                    : new BoxEl(),
            ],
        };
    }

    sealed class ChapterReader : Component
    {
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
            var rows = new Element[page.Items.Length];
            for (int i = 0; i < rows.Length; i++)
            {
                var chapter = page.Items[i];
                // Report 6d: no synthetic "Chapters N" fallback — a chapter with no title (D wave still filling
                // titles from kind 178) shows its start time alone rather than inventing a label the wire never sent.
                rows[i] = EpisodeReader.ChapterRow(chapter.Uri, chapter.StartMs, chapter.Title,
                    active: activeIndex == i, () => StartAt(e, Playback.EpisodeStartKind.Position, chapter.StartMs));
            }
            return new BoxEl { Direction = 1, Gap = Spacing.XS, MinWidth = 0, MaxWidth = 760, Children = rows };
        }
    }
}
