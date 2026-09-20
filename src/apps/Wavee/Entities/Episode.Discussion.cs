using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Scene;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

internal static partial class PodcastReaderUI
{
    internal sealed record DiscussionProps(string Uri, bool Replies);
    internal sealed record CommentPageProps(string Uri, bool Replies, string Token, int Generation, Action Refresh);
    internal sealed record CommentProps(Spotify.Podcasts.Comment Comment, bool Reply, Action Refresh);

    static readonly Spotify.Podcasts.Comment[] CommentSeed =
    [new("seed:1", "A thoughtful conversation about this episode.", "Listener", "", "", false, false, false, 0, 0, "", false),
     new("seed:2", "Thank you for sharing these ideas.", "Listener", "", "", false, false, false, 0, 0, "", false)];

    const float ThreadAvatar = 32, ReplyAvatar = 24;
    static readonly TemplateParts ConsentParts = ConsentTemplate();

    static TemplateParts ConsentTemplate()
    {
        var parts = new TemplateParts
        {
            [CheckBox.PartRoot] = root => root with { MinWidth = 0, AlignItems = FlexAlign.Start },
            [CheckBox.PartBox] = box => box with { Shrink = 0 },
        };
        parts.Set<TextEl>(CheckBox.PartLabel, label => label with
        {
            Grow = 1, Basis = 0, Shrink = 1, MinWidth = 0, Wrap = TextWrap.Wrap, MaxLines = 3,
            Size = 12, LineHeight = 18, Color = Tok.TextSecondary,
        });
        return parts;
    }

    static Element CommentBodies(IEnumerable<Spotify.Podcasts.Comment> comments, bool reply) => new BoxEl
    {
        Direction = 1, Gap = Spacing.L, MinWidth = 0, MaxWidth = 760,
        Children = comments.Select(c => CommentFrame(c, reply, [CommentText(c.Text)])).ToArray(),
    };

    static Element CommentText(string text) => new TextEl(text)
    { Size = 14, LineHeight = 21, Wrap = TextWrap.Wrap, MinWidth = 0, Color = Tok.TextPrimary };

    // The same avatar/header/body columns supply the live thread and its derived pending geometry.
    //
    // ONE LEFT EDGE (podcast-episode-peek §3). This frame IS the comment's grid: an avatar column and one content
    // column, and every row the card builds — the name/timestamp line, the body, the reaction/Reply row, the replies
    // disclosure — is a child of that content column. Nothing under the name gets an inset of its own (the actions
    // row's old -Spacing.S pull is exactly the misalignment this replaces): only the avatar sits outside the edge the
    // rest of the comment shares.
    static Element CommentFrame(Spotify.Podcasts.Comment c, bool reply, Element[] body)
    {
        float avatar = reply ? ReplyAvatar : ThreadAvatar;
        var heading = new List<Element>
        {
            new TextEl(c.Author.Length == 0 ? Loc.Get(Strings.Podcast.Reader.Anonymous) : c.Author)
            { Size = 13, LineHeight = 20, Weight = 600, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, MinWidth = 0 },
        };
        if (DateTimeOffset.TryParse(c.Created, out var date))
            heading.Add(new TextEl(date.ToLocalTime().ToString("d", System.Globalization.CultureInfo.CurrentCulture))
                { Size = 12, LineHeight = 20, Color = Tok.TextTertiary, Wrap = TextWrap.NoWrap });
        if (c.Pinned) heading.Add(Controls.Chip(Loc.Get(Strings.Podcast.Reader.Pinned)));
        if (c.Pending) heading.Add(Controls.Chip(Loc.Get(Strings.Podcast.Reader.Pending)));
        var content = new List<Element>
        {
            new BoxEl { Direction = 0, Wrap = true, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0, Children = heading.ToArray() },
        };
        content.AddRange(body);
        return new BoxEl
        {
            Direction = 0, Gap = Spacing.M, MinWidth = 0, AlignItems = FlexAlign.Start,
            Padding = new Edges4(0, Spacing.S, 0, Spacing.S), Children =
            [
                PersonPicture.Create("", avatar, displayName: c.Author, imageSourcePath: c.Avatar) with { Shrink = 0 },
                new BoxEl { Direction = 1, Grow = 1, Basis = 0, MinWidth = 0, Gap = Spacing.S, Children = content.ToArray() },
            ],
        };
    }

    static Element ReplyThread(Element content) => new BoxEl
    {
        Direction = 0, MinWidth = 0, Gap = Spacing.M, Margin = new Edges4(0, Spacing.S, 0, 0), Children =
        [
            new BoxEl { Width = 1, Shrink = 0, AlignSelf = FlexAlign.Stretch, Fill = Tok.StrokeDividerDefault },
            new BoxEl { Direction = 1, Grow = 1, Basis = 0, MinWidth = 0, Children = [content] },
        ],
    };

    const float PillEmojiSize = 14, PillEmojiOverlap = -4, ReplyStackAvatar = 20, ReplyStackOverlap = -7;

    /// <summary>ONE reaction pill (podcast-episode-peek §3), replacing the bare outline heart and its number: a
    /// rounded filled pill carrying the emoji people ACTUALLY used — up to three, most-used first, overlapping — and
    /// the total after them, tinted with the accent when you are one of them, reading "React" with a single neutral
    /// face when there are none, and opening the picker either way.
    /// <para>Which emoji, in what order, what the label says and whether the pill reads as pressed is
    /// <see cref="PodcastReactionRules.Pill"/>'s decision, not this builder's.</para></summary>
    static BoxEl ReactionPill(ReactionSession session, Action open)
    {
        var pill = PodcastReactionRules.Pill(session.Counts.Value, session.Current.Value, session.Total.Value);
        var glyphs = new Element[pill.Emoji.Length];
        for (int i = 0; i < glyphs.Length; i++)
            glyphs[i] = new TextEl(pill.Emoji[i])
            {
                Size = PillEmojiSize, LineHeight = PillEmojiSize + 2, Shrink = 0,
                Margin = new Edges4(0, 0, i == glyphs.Length - 1 ? 0 : PillEmojiOverlap, 0),
            };
        return new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Shrink = 0,
            Padding = new Edges4(Spacing.M, Spacing.XS, Spacing.M, Spacing.XS), Corners = Radii.PillAll,
            Fill = pill.Pressed ? Tok.AccentDefault with { A = 0.22f } : Tok.FillSubtleSecondary,
            HoverFill = pill.Pressed ? Tok.AccentDefault with { A = 0.32f } : Tok.FillSubtleTertiary,
            PressedFill = pill.Pressed ? Tok.AccentDefault with { A = 0.16f } : Tok.FillSubtleSecondary,
            Cursor = CursorId.Hand, Focusable = true, Role = AutomationRole.Button, OnClick = open,
            Children =
            [
                new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0, Children = glyphs },
                new TextEl(pill.CallToAction ? Loc.Get(Strings.Podcast.React) : FormatCache.Int(pill.Total))
                {
                    Size = 12, LineHeight = 16, Weight = 600, Shrink = 0,
                    Color = pill.Pressed ? Tok.AccentTextPrimary : Tok.TextSecondary,
                },
            ],
        };
    }

    /// <summary>The replies DISCLOSURE (podcast-episode-peek §3) under the pill row, on the comment's one left edge:
    /// up to three repliers' avatars stacked with overlap, "N replies", and a chevron that expands the thread in
    /// place. The count lives here and nowhere else — "Reply" beside the pill is a plain verb with no number in it.
    /// The stack is skipped entirely when the answer carried no replier avatars, rather than inventing faces.</summary>
    static Element RepliesDisclosure(Spotify.Podcasts.Comment c, Signal<bool> expanded)
    {
        bool open = expanded.Value;
        int faces = DiscussionLayout.ReplyAvatarCount(c.ReplyAvatars.Length, c.Replies);
        var row = new List<Element>(3);
        if (faces > 0)
        {
            var stack = new Element[faces];
            for (int i = 0; i < faces; i++)
                stack[i] = PersonPicture.Create("", ReplyStackAvatar, imageSourcePath: c.ReplyAvatars[i]) with
                { Shrink = 0, Margin = new Edges4(0, 0, i == faces - 1 ? 0 : ReplyStackOverlap, 0) };
            row.Add(new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0, Children = stack });
        }
        row.Add(new TextEl(Strings.Podcast.RepliesCount(c.Replies))
            { Size = 13, LineHeight = 18, Weight = 600, Shrink = 0, Color = Tok.TextSecondary });
        row.Add(Icon(open ? Icons.ChevronUp : Icons.ChevronDown, 12, Tok.TextTertiary));
        return new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, AlignSelf = FlexAlign.Start, MinWidth = 0,
            Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.M, Spacing.XS), Corners = Radii.PillAll,
            HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            Cursor = CursorId.Hand, Focusable = true, Role = AutomationRole.Button,
            OnClick = () => expanded.Value = !expanded.Peek(),
            Children = row.ToArray(),
        };
    }

    internal sealed class Discussion : Component
    {
        readonly Signal<string> _text = new("");
        readonly Signal<bool> _busy = new(false);
        readonly Signal<string> _error = new("");
        readonly Signal<int> _generation = new(0);
        readonly Signal<bool> _consent = new(false);
        public override Element Render()
        {
            var p = UseProps<DiscussionProps>();
            var post = UsePost(); uint epoch = Entities.ScopeEpoch.Value;
            var consentKey = new SettingKey<bool>("podcast.commentConsent." + Entities.Current.Key.Account, false);
            UseEffect(() => _consent.Value = Platform.Settings.Get(consentKey), DepKey.From((int)epoch));
            var resource = UseResource(ct => Platform.Args.Fake ? Task.FromResult(PodcastReaderFixtures.Comments) : Spotify.Podcasts.CommentsAsync(epoch, p.Uri, "", p.Replies, ct),
                new Spotify.Podcasts.Page<Spotify.Podcasts.Comment>([], "", 0, "", 0), epoch + ":" + p.Uri);
            return Skel.Region(resource.Loadable, () => CommentBodies(CommentSeed, p.Replies), Build,
                onFailed: () => Failed(Refresh));

            Element Build(Spotify.Podcasts.Page<Spotify.Podcasts.Comment> page)
            {
                if (!page.Ok) return Failed(Refresh);
                var rows = new List<Element>();
                if (!p.Replies) rows.Add(Heading(Loc.Get(Strings.Podcast.Reader.Comments) + " (" + page.Total + ")"));
                foreach (var item in page.Items)
                    rows.Add(Embed.Comp(new CommentProps(item, p.Replies, Refresh), static () => new CommentCard()) with { Key = item.Uri });
                if (page.Items.Length == 0) rows.Add(Quiet(Loc.Get(Strings.Podcast.Reader.NoComments)));
                if (page.NextToken.Length > 0)
                    rows.Add(Embed.Comp(new CommentPageProps(p.Uri, p.Replies, page.NextToken, _generation.Value, Refresh), static () => new MoreComments())
                        with { Key = "more:" + _generation.Value + ":" + page.NextToken });
                bool eligible = DiscussionLayout.ShowComposer(p.Replies, Platform.Args.Fake, page.Eligibility);
                if (eligible)
                {
                    var composer = new List<Element>
                    {
                        TextBox.Create(_text, options: new TextBox.TextBoxOptions
                        {
                            Placeholder = Loc.Get(p.Replies ? Strings.Podcast.Reader.ReplyPlaceholder : Strings.Podcast.Reader.CommentPlaceholder),
                            Width = float.NaN, AcceptsReturn = true, Height = 80,
                        }),
                    };
                    if (DiscussionLayout.ShowConsent(eligible, _consent.Value))
                        composer.Add(CheckBox.Create(Loc.Get(Strings.Podcast.Reader.Consent), _consent,
                            onChange: value => Platform.Settings.Set(consentKey, value), parts: ConsentParts));
                    composer.Add(Button.Standard(Loc.Get(p.Replies ? Strings.Podcast.Reader.PostReply : Strings.Podcast.Reader.Submit), Submit,
                        isEnabled: DiscussionLayout.CanSubmit(_consent.Value, _busy.Value, _text.Value)) with { AlignSelf = FlexAlign.Start });
                    rows.Add(new BoxEl
                    {
                        Direction = 1, MinWidth = 0, Gap = Spacing.M, Margin = new Edges4(0, Spacing.M, 0, 0),
                        Children = composer.ToArray(),
                    });
                }
                else rows.Add(Quiet(Loc.Get(DiscussionLayout.AlreadyCommented(page.Eligibility) ? Strings.Podcast.Reader.OneComment
                    : DiscussionLayout.CommentsDisabled(page.Eligibility) ? Strings.Podcast.CommentsOff : Strings.Podcast.Reader.Eligibility)));
                if (_error.Value.Length > 0) rows.Add(Quiet(_error.Value));
                return new BoxEl { Direction = 1, Gap = Spacing.M, MinWidth = 0, MaxWidth = 760, Children = rows.ToArray() };
            }

            void Refresh()
            {
                Spotify.Podcasts.InvalidateDiscussion();
                _generation.Value++;
                resource.Refresh();
            }
            async void Submit()
            {
                if (!DiscussionLayout.CanSubmit(_consent.Peek(), _busy.Peek(), _text.Peek())) return;
                _busy.Value = true; _error.Value = "";
                Spotify.Podcasts.Mutation result;
                try { result = await Spotify.Podcasts.CommentAsync(p.Uri, _text.Peek().Trim(), p.Replies, CancellationToken.None).ConfigureAwait(false); }
                catch { result = new(false, 0, true); }
                post(() =>
                {
                    if (epoch != Entities.ScopeEpoch.Peek()) return;
                    _busy.Value = false;
                    if (result.Success) _text.Value = "";
                    else _error.Value = Loc.Get(Strings.Podcast.Reader.MutationFailed);
                    // The response has no created ID; reconcile even an ambiguous result rather than retrying the write.
                    Refresh();
                });
            }
        }
    }

    internal sealed class MoreComments : Component
    {
        readonly Signal<bool> _open = new(false);
        public override Element Render()
        {
            var p = UseProps<CommentPageProps>();
            return _open.Value
                ? Embed.Comp(p, static () => new CommentPage())
                : Button.Subtle(Loc.Get(Strings.Podcast.Reader.LoadMore), () => _open.Value = true) with { AlignSelf = FlexAlign.Start };
        }
    }

    internal sealed class CommentPage : Component
    {
        public override Element Render()
        {
            var p = UseProps<CommentPageProps>(); uint epoch = Entities.ScopeEpoch.Value;
            var resource = UseResource(ct => Platform.Args.Fake ? Task.FromResult(PodcastReaderFixtures.Comments) : Spotify.Podcasts.CommentsAsync(epoch, p.Uri, p.Token, p.Replies, ct),
                new Spotify.Podcasts.Page<Spotify.Podcasts.Comment>([], "", 0, "", 0), epoch + ":" + p.Uri + ":" + p.Token);
            return Skel.Region(resource.Loadable, () => CommentBodies(CommentSeed, p.Replies), Build,
                onFailed: () => Failed(resource.Refresh));
            Element Build(Spotify.Podcasts.Page<Spotify.Podcasts.Comment> page)
            {
                if (!page.Ok) return Failed(resource.Refresh);
                var rows = new List<Element>();
                foreach (var item in page.Items)
                    rows.Add(Embed.Comp(new CommentProps(item, p.Replies, p.Refresh), static () => new CommentCard()) with { Key = item.Uri });
                if (page.NextToken.Length > 0 && page.NextToken != p.Token)
                    rows.Add(Embed.Comp(p with { Token = page.NextToken }, static () => new MoreComments()) with { Key = "more:" + page.NextToken });
                return new BoxEl { Direction = 1, Gap = Spacing.M, MinWidth = 0, Children = rows.ToArray() };
            }
        }
    }

    internal sealed class CommentCard : Component
    {
        readonly Signal<bool> _replies = new(false), _revealed = new(false);
        public override Element Render()
        {
            var p = UseProps<CommentProps>(); var c = p.Comment;
            uint epoch = Entities.ScopeEpoch.Value;
            var overlay = UseContext(Overlay.Service);
            var reactionAnchor = UseRef<NodeHandle>(default);
            var handle = UseRef<OverlayHandle?>(null);
            var sessionRef = UseRef<ReactionSession?>(null);
            var session = sessionRef.Value ??= new ReactionSession(c, epoch);
            UseEffect(() => session.Sync(c), DepKey.FromRef(c));
            UseEffect(() => { return () => handle.Value?.Close(); }, epoch);
            void Open(Ref<NodeHandle> anchor)
            {
                if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
                handle.Value = overlay.Open(() => anchor.Value,
                    () => Embed.Comp(new ReactionProps(session, p.Refresh), static () => new ReactionFlyout()),
                    FlyoutPlacement.BottomLeft,
                    new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup));
                handle.Value.ClosedAction = () => handle.Value = null;
            }
            var rows = new List<Element>();
            if (c.Sensitive && !_revealed.Value)
                rows.Add(Button.Subtle(Loc.Get(Strings.Podcast.Reader.Reveal), () => _revealed.Value = true)
                    with { AlignSelf = FlexAlign.Start });
            else rows.Add(CommentText(c.Text));
            var actions = new List<Element>
            {
                ToolTip.Wrap(ReactionPill(session, () => Open(reactionAnchor)) with
                    { OnRealized = h => reactionAnchor.Value = h }, Loc.Get(Strings.Podcast.Reader.Reactions)),
            };
            // Reply is the VERB, with no count in it — the count belongs to the disclosure below. It opens the thread
            // (which is where the reply composer lives); the disclosure toggles the same thread for reading.
            if (!p.Reply)
                actions.Add(Button.Subtle(Loc.Get(Strings.Podcast.Reader.Reply),
                    () => _replies.Value = true, isEnabled: !c.ReplyLimit));
            rows.Add(new BoxEl
            {
                Direction = 0, Gap = Spacing.XS, Wrap = true, MinWidth = 0,
                AlignItems = FlexAlign.Center, Children = actions.ToArray(),
            });
            if (DiscussionLayout.ShowReplies(p.Reply, c.Replies)) rows.Add(RepliesDisclosure(c, _replies));
            if (_replies.Value && !p.Reply)
                rows.Add(ReplyThread(c.ReplyLimit
                    ? Embed.Comp(new CommentPageProps(c.Uri, true, "", 0, p.Refresh), static () => new CommentPage())
                    : Embed.Comp(new DiscussionProps(c.Uri, true), static () => new Discussion())));
            return CommentFrame(c, p.Reply, rows.ToArray());
        }
    }

    internal sealed class ReactionSession(Spotify.Podcasts.Comment comment, uint epoch)
    {
        public readonly string Uri = comment.Uri;
        public readonly uint Epoch = epoch;
        public readonly Signal<string> Current = new(comment.MyReaction), Error = new("");
        public readonly Signal<int> Total = new(comment.Reactions), Revision = new(0);
        public readonly Signal<bool> Busy = new(false);
        /// <summary>The per-emoji breakdown the PILL stacks. The comments answer carries only a total and your own
        /// reaction, so this starts EMPTY and fills from the reactions roster — the canonical page the picker reads
        /// and every reaction writes back. Until then the pill shows your own emoji (or the neutral face) with the
        /// real total, rather than claiming emoji nobody sent.</summary>
        public readonly Signal<Spotify.Podcasts.ReactionCount[]> Counts = new([]);
        public void Sync(Spotify.Podcasts.Comment value)
        {
            if (Busy.Peek()) return;
            Current.Value = value.MyReaction;
            Total.Value = value.Reactions;
        }
    }

    internal sealed record ReactionProps(ReactionSession Session, Action Refresh);
    internal sealed record ReactionPageProps(string Uri, string Token, string Emoji, uint Epoch);
    internal sealed class ReactionFlyout : Component
    {
        readonly Signal<string> _filter = new("");
        public override Element Render()
        {
            var p = UseProps<ReactionProps>(); var session = p.Session;
            var post = UsePost();
            var viewport = UseContextSignal(Viewport.Size).Value;
            string filter = _filter.Value;
            int revision = session.Revision.Value;
            var resource = UseResource(ct => ReadReactionPage(session.Uri, "", filter, session.Epoch, ct), ReactionSeed,
                session.Epoch + ":" + session.Uri + ":" + filter + ":" + revision);
            UseSignalEffect(() =>
            {
                if (resource.Loadable.State.Value == (byte)LoadState.Ready && resource.Loadable.Value.Value is { Ok: true } page
                    && filter.Length == 0 && Entities.ScopeEpoch.Peek() == session.Epoch)
                {
                    session.Total.Value = page.Total;
                    session.Counts.Value = page.ReactionCounts;   // the pill's emoji, straight from the roster
                }
            });
            var palette = new Element[2];
            for (int row = 0; row < 2; row++)
            {
                var buttons = new List<Element>();
                for (int column = 0; column < 4; column++)
                {
                    int index = row * 4 + column;
                    string emoji = PodcastReactionRules.Palette[index];
                    buttons.Add(ToolTip.Wrap(Button.Subtle(emoji, () => React(emoji), isEnabled: !session.Busy.Value && !Platform.Args.Fake) with
                    {
                        Width = 56, Height = 36, Grow = 1, Basis = 0, MinWidth = 0,
                        Fill = PodcastReactionRules.Remove(session.Current.Value, emoji) ? Tok.FillSubtleSecondary : ColorF.Transparent,
                        BorderWidth = PodcastReactionRules.Remove(session.Current.Value, emoji) ? 1 : 0, BorderColor = Tok.AccentDefault,
                    }, ReactionName(index), grow: 1));
                }
                palette[row] = new BoxEl { Direction = 0, Gap = Spacing.XS, MinWidth = 0, Children = buttons.ToArray() };
            }
            var body = new List<Element>
            {
                new TextEl(Loc.Get(Strings.Podcast.Reader.Reactions)) { Size = 14, Weight = 600, Color = Tok.TextPrimary },
                new BoxEl { Direction = 1, Gap = Spacing.XS, Shrink = 0, MinWidth = 0, Children = palette },
            };
            if (session.Error.Value.Length > 0)
                body.Add(new TextEl(session.Error.Value) { Size = 12, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MinWidth = 0 });
            body.Add(Skel.Region(resource.Loadable,
                () => ReactionRoster(ReactionSeed, filter, session, revision),
                page => page.Ok ? ReactionRoster(page, filter, session, revision) : Failed(resource.Refresh),
                onFailed: () => Failed(resource.Refresh)));
            return new BoxEl
            {
                Direction = 1, Width = Math.Min(320, Math.Max(1, viewport.Width - 24)),
                Height = Math.Min(420, Math.Max(1, viewport.Height - 32)), MinHeight = 0, MinWidth = 0,
                Padding = Edges4.All(Spacing.M), Gap = Spacing.S, ClipToBounds = true, Children = body.ToArray(),
            };

            async void React(string emoji)
            {
                if (session.Busy.Peek() || Entities.ScopeEpoch.Peek() != session.Epoch) return;
                session.Busy.Value = true; session.Error.Value = "";
                string current = session.Current.Peek();
                bool remove = PodcastReactionRules.Remove(current, emoji);
                Spotify.Podcasts.Mutation result;
                Spotify.Podcasts.Page<Spotify.Podcasts.Reaction>? canonical = null;
                try
                {
                    result = await Spotify.Podcasts.ReactionAsync(session.Uri, emoji, remove, CancellationToken.None).ConfigureAwait(false);
                    if (result.Success && Entities.ScopeEpoch.Peek() == session.Epoch)
                        canonical = await Spotify.Podcasts.ReactionsAsync(session.Uri, "", "", CancellationToken.None).ConfigureAwait(false);
                }
                catch { result = new(false, 0, true); }
                post(() =>
                {
                    if (Entities.ScopeEpoch.Peek() != session.Epoch) return;
                    session.Busy.Value = false;
                    if (result.Success)
                    {
                        session.Current.Value = remove ? "" : emoji;
                        if (canonical is { Ok: true })
                        {
                            session.Total.Value = canonical.Total;
                            session.Counts.Value = canonical.ReactionCounts;
                        }
                    }
                    else session.Error.Value = Loc.Get(Strings.Podcast.Reader.MutationFailed);
                    session.Revision.Value++;
                    p.Refresh();
                });
            }
        }

        Element ReactionRoster(Spotify.Podcasts.Page<Spotify.Podcasts.Reaction> page, string filter, ReactionSession session, int revision)
        {
            var filters = new List<Element>
            {
                Filter(Loc.Get(Strings.Podcast.Reader.ReactionAll) + " " + page.Total, ""),
            };
            foreach (var count in page.ReactionCounts)
                if (count.Count > 0) filters.Add(Filter(count.Emoji + " " + count.Count, count.Emoji));
            return new BoxEl
            {
                Direction = 1, Grow = 1, Basis = 0, MinHeight = 0, MinWidth = 0, Gap = Spacing.S, Children =
                [
                    new BoxEl { Direction = 0, Wrap = true, Gap = Spacing.XS, Shrink = 0, MinWidth = 0, Children = filters.ToArray() },
                    ScrollView(ReactionPeople(page, new ReactionPageProps(session.Uri, "", filter, session.Epoch))) with
                    {
                        Grow = 1, Basis = 0, MinHeight = 0, AutoEdgeFade = true,
                        ScrollKey = "reactions:" + session.Uri + ":" + filter + ":" + revision,
                        Key = "participants:" + filter + ":" + revision,
                    },
                ],
            };
            BoxEl Filter(string label, string emoji) => Button.Subtle(label, () => _filter.Value = emoji) with
            {
                Fill = filter == emoji ? Tok.FillSubtleSecondary : ColorF.Transparent,
                BorderWidth = filter == emoji ? 1 : 0, BorderColor = Tok.StrokeControlDefault,
            };
        }
    }

    static readonly Spotify.Podcasts.Page<Spotify.Podcasts.Reaction> ReactionSeed = new(
        [new("\u2764\uFE0F", "Listener", "", ""), new("\uD83D\uDC4D", "Listener", "", "")], "", 2, "", 200);

    static Task<Spotify.Podcasts.Page<Spotify.Podcasts.Reaction>> ReadReactionPage(string uri, string token, string emoji, uint epoch, CancellationToken ct)
    {
        if (Entities.ScopeEpoch.Peek() != epoch) return Task.FromResult(new Spotify.Podcasts.Page<Spotify.Podcasts.Reaction>([], "", 0, "", 409));
        return Platform.Args.Fake ? Task.FromResult(new Spotify.Podcasts.Page<Spotify.Podcasts.Reaction>([], "", 0, "", 200))
            : Spotify.Podcasts.ReactionsAsync(uri, token, emoji, ct);
    }

    static Element ReactionPeople(Spotify.Podcasts.Page<Spotify.Podcasts.Reaction> page, ReactionPageProps props)
    {
        var rows = new List<Element>();
        foreach (var item in page.Items)
            rows.Add(new BoxEl
            {
                Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0,
                Padding = new Edges4(0, Spacing.S, 0, Spacing.S), Children =
                [
                    PersonPicture.Create("", 28, displayName: item.Author, imageSourcePath: item.Avatar) with { Shrink = 0 },
                    new TextEl(item.Author.Length > 0 ? item.Author : Loc.Get(Strings.Podcast.Reader.Anonymous))
                        { Size = 13, Grow = 1, Basis = 0, MinWidth = 0, Wrap = TextWrap.Wrap, Color = Tok.TextPrimary },
                    new TextEl(item.Emoji) { Size = 18 },
                ],
            });
        if (rows.Count == 0) rows.Add(Quiet(Loc.Get(Strings.Podcast.Reader.Empty)));
        if (page.NextToken.Length > 0 && page.NextToken != props.Token)
            rows.Add(Embed.Comp(props with { Token = page.NextToken }, static () => new MoreReactions()) with { Key = page.NextToken });
        return new BoxEl { Direction = 1, Gap = Spacing.XS, MinWidth = 0, Children = rows.ToArray() };
    }

    internal sealed class MoreReactions : Component
    {
        readonly Signal<bool> _open = new(false);
        public override Element Render()
        {
            var p = UseProps<ReactionPageProps>();
            return _open.Value ? Embed.Comp(p, static () => new ReactionPage())
                : Button.Subtle(Loc.Get(Strings.Podcast.Reader.LoadMore), () => _open.Value = true);
        }
    }
    internal sealed class ReactionPage : Component
    {
        public override Element Render()
        {
            var p = UseProps<ReactionPageProps>();
            var resource = UseResource(ct => ReadReactionPage(p.Uri, p.Token, p.Emoji, p.Epoch, ct), ReactionSeed,
                p.Epoch + ":" + p.Uri + ":" + p.Token + ":" + p.Emoji);
            return Skel.Region(resource.Loadable, () => ReactionPeople(ReactionSeed, p),
                page => page.Ok ? ReactionPeople(page, p) : Failed(resource.Refresh), onFailed: () => Failed(resource.Refresh));
        }
    }

    static string ReactionName(int index) => Loc.Get(index switch
    {
        0 => Strings.Podcast.Reader.ReactionHeart, 1 => Strings.Podcast.Reader.ReactionLaugh,
        2 => Strings.Podcast.Reader.ReactionSmile, 3 => Strings.Podcast.Reader.ReactionSurprise,
        4 => Strings.Podcast.Reader.ReactionSad, 5 => Strings.Podcast.Reader.ReactionClap,
        6 => Strings.Podcast.Reader.ReactionFire, _ => Strings.Podcast.Reader.ReactionLike,
    });
}

// ── engine-free layout decisions (Wave E, plan §5.4) ────────────────────────────────────────────────────────────
//
// Split out of Discussion.Build so the composer/consent/eligibility branching is unit-tested directly
// (DiscussionLayoutTests.cs, against the captured vc4-275-comments.json fixture) without a live component tree.
// TOP-LEVEL and public: PodcastReaderUI itself is `internal`, and this assembly carries no InternalsVisibleTo
// (Controls.cs / Playlist.UI.cs), so a type Wavee.Tests must reach cannot live nested inside it.

/// <summary>Which rows indent, when the composer shows, and when the consent row shows above it — the three
/// decisions Episode.Discussion.cs's <c>Discussion</c>/<c>CommentCard</c> components read instead of inlining the
/// same branches. No FluentGpu type crosses this boundary.</summary>
public static class DiscussionLayout
{
    /// <summary>A comment row sits one avatar column deep exactly when it renders inside a reply thread
    /// (Discussion/CommentPage mounted with <c>Replies == true</c>) — replies never nest past this ONE indent,
    /// matching <c>ReplyThread</c>'s single rail (a reply's own replies still render flat under it).</summary>
    public static int IndentOf(bool isReplyLevel) => isReplyLevel ? 1 : 0;

    /// <summary>The composer (text box + submit) mounts when the surface is eligible to write: a reply is always
    /// open to whoever can already see the thread, a top-level comment additionally needs the discussion
    /// response's own eligibility (<see cref="PodcastReaderRules.CanComment"/>), and neither ever mounts under
    /// Fake demo data (there is no network to post to).</summary>
    public static bool ShowComposer(bool isReplyLevel, bool fake, string eligibility)
        => !fake && (isReplyLevel || PodcastReaderRules.CanComment(eligibility));

    /// <summary>The consent checkbox row shows above the composer only until the account has consented once —
    /// after that the composer alone is enough (consent is persisted per account, keyed on the Spotify account id).</summary>
    public static bool ShowConsent(bool showComposer, bool consented) => showComposer && !consented;

    /// <summary>"Post"/"Post reply" is enabled only with consent, non-empty text, and no write already in flight.</summary>
    public static bool CanSubmit(bool consented, bool busy, string text)
        => consented && !busy && !string.IsNullOrWhiteSpace(text);

    /// <summary>The account has already used its one comment on this episode — distinct from every other closed
    /// reason (<see cref="CommentsDisabled"/>), so the quiet text never claims comments are off when only THIS
    /// account is capped.</summary>
    public static bool AlreadyCommented(string eligibility) => eligibility == "ELIGIBILITY_STATUS_ALREADY_COMMENTED";

    /// <summary>The show/episode turned comments off entirely — distinct from a transport failure (that path never
    /// reaches this rule: <c>Discussion.Build</c> returns <c>Failed</c> + retry on <c>!page.Ok</c> before eligibility
    /// is ever read) and from <see cref="AlreadyCommented"/> (a per-account cap, not a closed episode).</summary>
    public static bool CommentsDisabled(string eligibility)
        => eligibility is "ELIGIBILITY_STATUS_DISABLED" or "ELIGIBILITY_STATUS_COMMENTS_DISABLED";

    /// <summary>The replies disclosure stacks at most this many repliers' avatars.</summary>
    public const int MaxReplyAvatars = 3;

    /// <summary>The replies disclosure exists only on a TOP-LEVEL comment that actually has replies \u2014 a reply's own
    /// replies render flat under it (<c>ReplyThread</c>'s single rail), and a comment with none shows no affordance.</summary>
    public static bool ShowReplies(bool isReplyLevel, int replies) => !isReplyLevel && replies > 0;

    /// <summary>How many avatars the stack draws: capped at <see cref="MaxReplyAvatars"/>, never more than the answer
    /// gave us, and never more than there are replies (a wire that over-lists must not draw a fourth face).</summary>
    public static int ReplyAvatarCount(int available, int replies)
        => Math.Max(0, Math.Min(MaxReplyAvatars, Math.Min(available, replies)));
}

/// <summary>What ONE reaction pill renders: the emoji it stacks (most-used first), the total beside them, whether it
/// reads as pressed (you reacted) and whether it is the empty CALL TO ACTION ("React" + a single neutral face) rather
/// than a count.</summary>
public readonly record struct ReactionPillModel(string[] Emoji, int Total, bool Pressed, bool CallToAction);

public static class PodcastReactionRules
{
    public static readonly string[] Palette = ["\u2764\uFE0F", "\uD83D\uDE02", "\uD83D\uDE05", "\uD83D\uDE2E", "\uD83D\uDE22", "\uD83D\uDC4F", "\uD83D\uDD25", "\uD83D\uDC4D"];
    public static bool Remove(string current, string picked) => current.Length > 0
        && string.Equals(current.Replace("\uFE0F", "", StringComparison.Ordinal), picked.Replace("\uFE0F", "", StringComparison.Ordinal), StringComparison.Ordinal);

    /// <summary>At most this many emoji overlap inside the pill.</summary>
    public const int PillEmojiCap = 3;

    /// <summary>The one face the pill shows when it has nothing truthful to show: the empty "React" state, and the
    /// stand-in while the per-emoji breakdown is still unknown and you have not reacted yourself.</summary>
    public const string NeutralFace = "\uD83D\uDE42";

    /// <summary>THE PILL's composition (podcast-episode-peek \u00A73) \u2014 a real decision, so it is a pure function with
    /// tests rather than branches inlined in the builder.
    /// <list type="bullet">
    /// <item>Nothing at all (no total, no reaction of yours) \u21D2 one neutral face and the "React" label.</item>
    /// <item>A known breakdown \u21D2 up to <see cref="PillEmojiCap"/> emoji, MOST-USED FIRST, ties keeping the answer's
    /// own order; zero counts and blank emoji never appear.</item>
    /// <item>No breakdown yet (the comments answer carries only a total) \u21D2 your own emoji if you reacted, else the
    /// neutral face \u2014 the pill never claims an emoji nobody sent.</item>
    /// <item>Your own reaction TINTS the pill, and keeps the total at least 1 even if the count has not caught up
    /// with the write yet.</item></list></summary>
    public static ReactionPillModel Pill(IReadOnlyList<Spotify.Podcasts.ReactionCount>? counts, string? myReaction, int total)
    {
        string mine = myReaction ?? "";
        bool pressed = mine.Length > 0;
        int shown = Math.Max(total < 0 ? 0 : total, pressed ? 1 : 0);
        if (shown == 0) return new([NeutralFace], 0, false, true);
        var emoji = TopEmoji(counts);
        if (emoji.Length == 0) emoji = [pressed ? mine : NeutralFace];
        return new(emoji, shown, pressed, false);
    }

    /// <summary>The most-used emoji, capped, stable on ties (a strict &gt; keeps the answer's own order).</summary>
    static string[] TopEmoji(IReadOnlyList<Spotify.Podcasts.ReactionCount>? counts)
    {
        if (counts is null || counts.Count == 0) return [];
        Span<int> chosen = stackalloc int[PillEmojiCap];
        int picked = 0;
        while (picked < PillEmojiCap)
        {
            int best = -1;
            for (int i = 0; i < counts.Count; i++)
            {
                var candidate = counts[i];
                if (candidate is null || candidate.Count <= 0 || string.IsNullOrEmpty(candidate.Emoji)) continue;
                bool taken = false;
                for (int k = 0; k < picked; k++) if (chosen[k] == i) { taken = true; break; }
                if (taken) continue;
                if (best < 0 || candidate.Count > counts[best].Count) best = i;
            }
            if (best < 0) break;
            chosen[picked++] = best;
        }
        if (picked == 0) return [];
        var result = new string[picked];
        for (int i = 0; i < picked; i++) result[i] = counts[chosen[i]].Emoji;
        return result;
    }
}
