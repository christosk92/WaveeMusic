using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

internal static partial class PodcastReaderUI
{
    internal sealed record DiscussionProps(string Uri, bool Replies);
    internal sealed record CommentPageProps(string Uri, bool Replies, string Token, int Generation, Action Refresh);
    internal sealed record CommentProps(Spotify.Podcasts.Comment Comment, bool Reply, Action Refresh);

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
            var page = resource.Loadable.Value.Value;
            if (resource.Loadable.State.Value == (byte)LoadState.Pending) return Loading();
            if (!page.Ok) return Failed(Refresh);
            var rows = new List<Element>();
            if (!p.Replies) rows.Add(Heading(Loc.Get(Strings.Podcast.Reader.Comments) + " (" + page.Total + ")"));
            foreach (var item in page.Items)
                rows.Add(Embed.Comp(new CommentProps(item, p.Replies, Refresh), static () => new CommentCard()) with { Key = item.Uri + ":" + item.GetHashCode() });
            if (page.Items.Length == 0) rows.Add(Quiet(Loc.Get(Strings.Podcast.Reader.NoComments)));
            if (page.NextToken.Length > 0)
                rows.Add(Embed.Comp(new CommentPageProps(p.Uri, p.Replies, page.NextToken, _generation.Value, Refresh), static () => new MoreComments())
                    with { Key = "more:" + _generation.Value + ":" + page.NextToken });
            bool eligible = !Platform.Args.Fake && (p.Replies || PodcastReaderRules.CanComment(page.Eligibility));
            if (eligible)
            {
                rows.Add(TextBox.Create(_text, options: new TextBox.TextBoxOptions
                { Placeholder = Loc.Get(p.Replies ? Strings.Podcast.Reader.ReplyPlaceholder : Strings.Podcast.Reader.CommentPlaceholder) }));
                if (!_consent.Value)
                    rows.Add(CheckBox.Create(Loc.Get(Strings.Podcast.Reader.Consent), _consent, onChange: value => Platform.Settings.Set(consentKey, value)));
                rows.Add(Button.Standard(Loc.Get(p.Replies ? Strings.Podcast.Reader.PostReply : Strings.Podcast.Reader.Submit), Submit,
                    isEnabled: !_busy.Value && _consent.Value && !string.IsNullOrWhiteSpace(_text.Value)));
            }
            else rows.Add(Quiet(Loc.Get(page.Eligibility == "ELIGIBILITY_STATUS_ALREADY_COMMENTED"
                ? Strings.Podcast.Reader.OneComment : Strings.Podcast.Reader.Eligibility)));
            if (_error.Value.Length > 0) rows.Add(Quiet(_error.Value));
            return new BoxEl { Direction = 1, Gap = Spacing.M, MinWidth = 0,
                Padding = p.Replies ? new Edges4(Spacing.M, 0, 0, 0) : default, Children = rows.ToArray() };

            void Refresh()
            {
                Spotify.Podcasts.InvalidateDiscussion();
                _generation.Value++;
                resource.Refresh();
            }
            async void Submit()
            {
                if (_busy.Peek() || !_consent.Peek() || string.IsNullOrWhiteSpace(_text.Peek())) return;
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
                : Button.Subtle(Loc.Get(Strings.Podcast.Reader.LoadMore), () => _open.Value = true);
        }
    }

    internal sealed class CommentPage : Component
    {
        public override Element Render()
        {
            var p = UseProps<CommentPageProps>(); uint epoch = Entities.ScopeEpoch.Value;
            var resource = UseResource(ct => Platform.Args.Fake ? Task.FromResult(PodcastReaderFixtures.Comments) : Spotify.Podcasts.CommentsAsync(epoch, p.Uri, p.Token, p.Replies, ct),
                new Spotify.Podcasts.Page<Spotify.Podcasts.Comment>([], "", 0, "", 0), epoch + ":" + p.Uri + ":" + p.Token);
            var page = resource.Loadable.Value.Value;
            if (resource.Loadable.State.Value == (byte)LoadState.Pending) return Loading();
            if (!page.Ok) return Failed(resource.Refresh);
            var rows = new List<Element>();
            foreach (var item in page.Items)
                rows.Add(Embed.Comp(new CommentProps(item, p.Replies, p.Refresh), static () => new CommentCard()) with { Key = item.Uri + ":" + item.GetHashCode() });
            if (page.NextToken.Length > 0 && page.NextToken != p.Token)
                rows.Add(Embed.Comp(p with { Token = page.NextToken }, static () => new MoreComments()) with { Key = "more:" + page.NextToken });
            return new BoxEl { Direction = 1, Gap = Spacing.M, MinWidth = 0, Children = rows.ToArray() };
        }
    }

    internal sealed class CommentCard : Component
    {
        readonly Signal<bool> _replies = new(false), _revealed = new(false), _busy = new(false), _reactions = new(false);
        public override Element Render()
        {
            var p = UseProps<CommentProps>(); var c = p.Comment; var post = UsePost(); uint epoch = Entities.ScopeEpoch.Value;
            var rows = new List<Element>();
            string author = c.Author.Length == 0 ? Loc.Get(Strings.Podcast.Reader.Anonymous) : c.Author;
            rows.Add(new BoxEl { Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Children =
                [new ImageEl { Source = c.Avatar, Width = 28, Height = 28, DecodePx = 56, Fit = ImageFit.Cover, Corners = CornerRadius4.All(14) }, Quiet(author)] });
            if (DateTimeOffset.TryParse(c.Created, out var created)) rows.Add(Quiet(created.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture)));
            if (c.Pinned) rows.Add(Quiet(Loc.Get(Strings.Podcast.Reader.Pinned)));
            if (c.Pending) rows.Add(Quiet(Loc.Get(Strings.Podcast.Reader.Pending)));
            if (c.Sensitive && !_revealed.Value)
                rows.Add(Button.Subtle(Loc.Get(Strings.Podcast.Reader.Reveal), () => _revealed.Value = true));
            else rows.Add(Quiet(c.Text));
            var actions = new List<Element>
            {
                Button.Subtle((c.MyReaction.Length > 0 ? c.MyReaction : "\u2661") + " " + c.Reactions,
                    React, isEnabled: !_busy.Value && !Platform.Args.Fake),
                Button.Subtle(Loc.Get(Strings.Podcast.Reader.Reactions), () => _reactions.Value = !_reactions.Peek()),
            };
            if (!p.Reply)
                actions.Add(Button.Subtle(Loc.Get(Strings.Podcast.Reader.Reply) + " (" + c.Replies + ")", () => _replies.Value = !_replies.Peek(),
                    isEnabled: c.Replies > 0 || !c.ReplyLimit));
            rows.Add(new BoxEl { Direction = 0, Gap = Spacing.S, Wrap = true, Children = actions.ToArray() });
            if (_reactions.Value) rows.Add(Embed.Comp(new ReactionProps(c.Uri, ""), static () => new Reactions()) with { Key = "reactions:" + c.Uri });
            if (_replies.Value && !p.Reply)
                rows.Add(c.ReplyLimit
                    ? Embed.Comp(new CommentPageProps(c.Uri, true, "", 0, p.Refresh), static () => new CommentPage())
                    : Embed.Comp(new DiscussionProps(c.Uri, true), static () => new Discussion()));
            return new BoxEl { Direction = 1, Gap = Spacing.S, MinWidth = 0, Padding = Edges4.All(Spacing.M),
                Corners = Radii.ControlAll, Fill = Tok.FillSubtleSecondary, Children = rows.ToArray() };

            async void React()
            {
                _busy.Value = true;
                Spotify.Podcasts.Mutation result;
                try { result = await Spotify.Podcasts.ReactionAsync(c.Uri, c.MyReaction.Length > 0 ? c.MyReaction : "\u2764\uFE0F", c.MyReaction.Length > 0, CancellationToken.None).ConfigureAwait(false); }
                catch { result = new(false, 0, true); }
                post(() =>
                {
                    if (Entities.ScopeEpoch.Peek() != epoch) return;
                    _busy.Value = false;
                    if (!result.Success) Notify.Say(Loc.Get(Strings.Podcast.Reader.MutationFailed), InfoBarSeverity.Warning);
                    p.Refresh();
                });
            }
        }
    }

    internal sealed record ReactionProps(string Uri, string Token);
    internal sealed class Reactions : Component
    {
        readonly Signal<bool> _more = new(false);
        public override Element Render()
        {
            var p = UseProps<ReactionProps>(); uint epoch = Entities.ScopeEpoch.Value;
            var resource = UseResource(ct => Platform.Args.Fake ? Task.FromResult(new Spotify.Podcasts.Page<Spotify.Podcasts.Reaction>([], "", 0, "", 200)) : Spotify.Podcasts.ReactionsAsync(p.Uri, p.Token, "", ct),
                new Spotify.Podcasts.Page<Spotify.Podcasts.Reaction>([], "", 0, "", 0), epoch + ":" + p.Uri + ":" + p.Token);
            var page = resource.Loadable.Value.Value;
            if (resource.Loadable.State.Value == (byte)LoadState.Pending) return Loading();
            if (!page.Ok) return Failed(resource.Refresh);
            var rows = new List<Element>();
            foreach (var item in page.Items) rows.Add(Quiet(item.Emoji + " " + item.Author));
            if (rows.Count == 0) rows.Add(Quiet(Loc.Get(Strings.Podcast.Reader.Empty)));
            if (page.NextToken.Length > 0 && page.NextToken != p.Token)
                rows.Add(_more.Value ? Embed.Comp(p with { Token = page.NextToken }, static () => new Reactions())
                    : Button.Subtle(Loc.Get(Strings.Podcast.Reader.LoadMore), () => _more.Value = true));
            return new BoxEl { Direction = 1, Gap = Spacing.XS, Children = rows.ToArray() };
        }
    }
}
