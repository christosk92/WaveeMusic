// ── Themes/Zune/Structure/Views/Local/ZuneLocalLink.cs — Zune's "Your files and Spotify": the offer and the review (#240 phase 1) ────────
//
// Board frames 28-30 in Zune's voice (the UX spec's Themes line: "large lowercase type, text verbs, banners as full-width bands"): no plates, no
// pills, no borders, no glyph buttons. Every sentence is the core's (`LocalLinkWords`, the props); the shared opt-in body sits in Zune's dialog.
//
//   Offer   a full-width band across the column (the hairline-free subtle ground), the sentence in the track voice and its promise under it,
//           "show me" and "not now" as accent text verbs.
//   Review  "on this pc › review matches" as the trail (the first step a verb back), "review matches" in the display voice, the line of what
//           was found, then the filter rail (the discography page's sub-pivot: lowercase words, the current one heavier over the accent line,
//           each with its count) and "accept all exact" at its end; "nothing changes until you accept" under it. Then the groups, flat on the
//           ground with air between them:
//             the file's art → Spotify's (64 each) · "Title · Artist" in the track voice · the detail · a dot in the confidence's ink and the
//             evidence · the verbs ("accept", "details", or "undo" once decided);
//             details: flat field rows (the field, the file's value, the arrow, Spotify's, the accent where it improves), whose info wins
//             (Zune's radio buttons), "not this album" and "accept";
//             a check: the candidates (art, title, detail, "pick"), then "search" (a search box) and "not on spotify" with its promise.
//           The drawer opens with the house disclosure and a group's height changes in place: nothing jumps.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using Wavee.Themes.Common;
using static FluentGpu.Dsl.Ui;

using C = Wavee.Themes.Common.CommonLocalLink;
using W = Wavee.Strings.LocalFiles.Link;
using Z = Wavee.Themes.Zune.ZuneLocalLinkRules;

namespace Wavee.Themes.Zune;

/// <summary>Zune's offer and review (see the file header).</summary>
internal static class ZuneLocalLink
{
    // ── the voice ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A text verb: the accent word in the command case on a 40-high target.</summary>
    static Element Verb(string key, string word, Action act, bool enabled = true) => new BoxEl
    {
        Key = key, Direction = 0, AlignItems = FlexAlign.Center, MinHeight = Z.VerbH, Shrink = 0f, MinWidth = 0f,
        Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = enabled, IsEnabled = enabled, AllowFocusOnInteraction = false,
        OnClick = act,
        Children =
        [
            R.Type.TrackMeta(word) with
            {
                Case = R.Case.For(LabelKind.Command), Color = enabled ? Tok.AccentTextPrimary : Tok.TextDisabled, HoverColor = Tok.TextPrimary,
                MaxLines = 1, Wrap = TextWrap.NoWrap,
            },
        ],
    };

    static TextEl Line(TextEl t) => t with { MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f };

    static TextEl Wrapping(TextEl t) => t with { Wrap = TextWrap.WrapWholeWords, MinWidth = 0f };

    static float LeadOf(in ViewContext ctx)
    {
        var tiers = ctx.Tier();
        return ZuneFrameRules.ColumnLead(tiers.ContentWidth, ZuneTiers.GutterOf(in tiers));
    }

    // ══ THE OFFER (board 28 ①) ════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Zune's template of <see cref="ViewKeys.LocalLinkOffer"/>: a full-width band (the page places it without a gutter).</summary>
    public static Element Offer(LocalLinkOfferProps p, in ViewContext ctx)
        => Embed.Comp(new OfferProps(p, LeadOf(in ctx)), static () => new OfferHost()) with { Key = Z.OfferKey };

    sealed record OfferProps(LocalLinkOfferProps P, float Lead);

    sealed class OfferHost : Component
    {
        IOverlayService? _overlay;
        OfferProps? _latest;
        Action? _show;

        public override Element Render()
        {
            var op = UseProps<OfferProps>();
            _latest = op;
            _overlay = UseContext(Overlay.Service);
            _show ??= () => _latest?.P.ShowMe(_overlay);
            var p = op.P;
            Element words = new BoxEl
            {
                Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
                Children =
                [
                    Wrapping(R.Type.TrackTitle(p.Text) with { Color = Tok.TextPrimary }),
                    Wrapping(R.Type.TrackMeta(p.Sub) with { Color = Tok.TextSecondary }),
                ],
            };
            Element verbs = new BoxEl
            {
                Direction = 0, Gap = Z.VerbGap, AlignItems = FlexAlign.Center, Shrink = 0f,
                Children = [Verb(Z.ShowMeKey, p.ShowMeLabel, _show), Verb(Z.NotNowKey, p.NotNowLabel, p.NotNow)],
            };
            return new BoxEl
            {
                // One row that wraps its verbs under the words when they do not fit (width, not tier: a crossing never restacks it).
                Direction = 0, Wrap = true, Gap = Z.BandGap, AlignItems = FlexAlign.Center, MinWidth = 0f,
                Padding = new Edges4(op.Lead, Z.BandPadY, op.Lead, Z.BandPadY), Fill = Tok.FillSubtleSecondary, Margin = new Edges4(0f, Z.BandTop, 0f, 0f),
                Children = [words, verbs],
            };
        }
    }

    // ══ THE REVIEW (boards 29, 30) ════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Zune's template of <see cref="ViewKeys.LocalLinkReview"/>.</summary>
    public static Element Review(LocalLinkReviewProps p, in ViewContext ctx)
        => Embed.Comp(new ReviewProps(p), static () => new ReviewHost()) with { Key = Z.ReviewKey };

    sealed record ReviewProps(LocalLinkReviewProps P);

    sealed class ReviewHost : Component
    {
        readonly Signal<C.Filter> _filter = new(C.Filter.All);
        readonly Action?[] _go = new Action?[6];

        public override Element Render()
        {
            var rp = UseProps<ReviewProps>();
            var p = rp.P;
            var v = p.View.Value;
            string? focus = p.Focus.Value;
            bool focused = focus is { Length: > 0 };
            var filter = _filter.Value;

            var groups = new List<Element>(v.Groups.Count);
            foreach (var g in v.Groups)
            {
                if (focused ? g.Key != focus : !C.Shows(filter, g)) continue;
                groups.Add(Embed.Comp(new GroupProps(g, p, focused), static () => new Group()) with { Key = Z.GroupKey + C.Safe(g.Key) });
            }
            if (groups.Count == 0)
                groups.Add(new BoxEl
                {
                    Key = Z.EmptyKey, Direction = 1, MinWidth = 0f,
                    Children = focused && C.Finding(v, focus!) ? [Wrapping(R.Type.TrackMeta(Loc.Get(W.Finding)) with { Color = Tok.TextSecondary })] :
                    [
                        Wrapping(R.Type.TrackTitle(Loc.Get(W.EmptyTitle)) with { Color = Tok.TextPrimary, Case = R.Case.For(LabelKind.Heading) }),
                        Wrapping(R.Type.TrackMeta(Loc.Get(W.EmptyBody)) with { Color = Tok.TextSecondary }),
                    ],
                });

            // "on this pc › review matches": the first step goes back.
            Element trail = new BoxEl
            {
                Direction = 0, Gap = Z.DotGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Margin = new Edges4(0f, Z.HeadTop, 0f, 0f),
                Children =
                [
                    Verb(Z.BackKey, p.PageTitle, p.Close),
                    R.Type.TrackMeta("›") with { Color = Tok.TextTertiary },
                    Line(R.Type.TrackMeta(Loc.Get(W.ReviewTitle)) with { Color = Tok.TextSecondary, Case = R.Case.For(LabelKind.Navigation) }),
                ],
            };
            // The rail: the confidences as sub-pivot words (or, opened on one song, the one way back to every group).
            var rail = new List<Element>(7);
            if (focused) rail.Add(Verb(Z.ShowAllKey, Loc.Get(W.All), p.ShowAll));
            else
                for (int i = 0; i < 6; i++)
                {
                    var f = (C.Filter)i;
                    int n = C.CountOf(f, v);
                    if (n == 0 && f != C.Filter.All) continue;
                    rail.Add(Tab(f, n, f == filter));
                }
            Element head = new BoxEl
            {
                // One row at every tier: the words wrap by width, the verb keeps its place at the end (a crossing never restacks it).
                Key = Z.RailKey, Direction = 0, AlignItems = FlexAlign.End, Gap = Z.VerbGap, MinWidth = 0f,
                Margin = new Edges4(0f, Z.RailTop, 0f, 0f),
                Children =
                [
                    new BoxEl { Direction = 0, Wrap = true, Gap = Z.RailGap, AlignItems = FlexAlign.End, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, Children = rail.ToArray() },
                    Verb(Z.AllExactKey, Loc.Get(W.AcceptAllExact), p.AcceptAllExact, v.Exact > 0),
                ],
            };
            return new BoxEl
            {
                Direction = 1, MinWidth = 0f, Padding = new Edges4(0f, 0f, 0f, Z.ReviewBottom),
                Children =
                [
                    trail,
                    R.Type.HeroTitle(Loc.Get(W.ReviewTitle)) with
                    {
                        Color = Tok.TextPrimary, Case = R.Case.For(LabelKind.Heading), MaxLines = 2, Wrap = TextWrap.WrapWholeWords, MinWidth = 0f, Margin = new Edges4(0f, Z.TitleTop, 0f, 0f),
                    },
                    Wrapping(R.Type.TrackMeta(LocalLinkWords.ReviewSub(v)) with { Color = Tok.TextSecondary, Margin = new Edges4(0f, Z.SubTop, 0f, 0f) }),
                    head,
                    Wrapping(R.Type.TrackMeta(Loc.Get(W.NothingChanges)) with { Color = Tok.TextTertiary, Case = R.Case.For(LabelKind.Label), Margin = new Edges4(0f, Z.NoteTop, 0f, 0f) }),
                    new BoxEl { Direction = 1, Gap = Z.GroupGap, MinWidth = 0f, Margin = new Edges4(0f, Z.GroupsTop, 0f, 0f), Children = groups.ToArray() },
                ],
            };
        }

        /// <summary>One rail word (the discography sub-pivot): the word, its count, the accent line under the current one.</summary>
        Element Tab(C.Filter f, int count, bool current)
        {
            int i = (int)f;
            var go = _go[i] ??= () => _filter.Value = (C.Filter)i;
            return new BoxEl
            {
                Key = "zll-filter:" + f.ToString().ToLowerInvariant(), Direction = 1, Shrink = 0f, MinHeight = Z.VerbH, Justify = FlexJustify.End,
                Cursor = CursorId.Hand, Role = AutomationRole.RadioButton, Focusable = true, AllowFocusOnInteraction = false, OnClick = go,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0, Gap = Z.CountGap, AlignItems = FlexAlign.End,
                        Children =
                        [
                            R.Type.SubPivot(C.FilterWord(f)) with
                            {
                                Color = current ? Tok.TextPrimary : Tok.TextSecondary, HoverColor = Tok.TextPrimary, Weight = current ? Z.RailSelected : Z.RailRest,
                                Case = R.Case.For(LabelKind.Navigation), MaxLines = 1, Wrap = TextWrap.NoWrap,
                            },
                            R.Type.TrackMeta(count.ToString("N0", CultureInfo.CurrentCulture)) with { Color = Tok.TextTertiary, MaxLines = 1 },
                        ],
                    },
                    new BoxEl { Height = Z.RailLine, Fill = current ? Prop.Of(static () => R.Ink.AccentAction) : ColorF.Transparent, HitTestVisible = false },
                ],
            };
        }
    }

    // ── one group ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <param name="Focused">The review was opened on this group ("Find on Spotify…"): its candidates and the search box show at once.</param>
    sealed record GroupProps(LinkGroup G, LocalLinkReviewProps P, bool Focused = false);

    sealed class Group : Component
    {
        readonly Signal<bool> _open = new(false), _searching = new(false);
        readonly Signal<string> _query = new("");
        GroupProps? _latest;
        Action? _accept, _details, _notThis, _notOn, _undo, _search, _toSpotify, _toFiles;
        Action<string>? _submit;
        readonly Action?[] _picks = new Action?[8];

        public override Element Render()
        {
            var gp = UseProps<GroupProps>();
            _latest = gp;
            var g = gp.G;
            _accept ??= () => { if (_latest is { } l) { _open.Value = false; l.P.Accept(l.G.Key); } };
            _details ??= () => _open.Value = !_open.Peek();
            _notThis ??= () => { if (_latest is { } l) { _open.Value = false; l.P.NotThis(l.G.Key); } };
            _notOn ??= () => { if (_latest is { } l) l.P.NotOnSpotify(l.G.Key); };
            _undo ??= () => { if (_latest is { } l) l.P.Undo(l.G.Key); };
            _search ??= () => _searching.Value = !_searching.Peek();
            _toSpotify ??= () => { if (_latest is { } l) l.P.SetSpotifyWins(l.G.Key, true); };
            _toFiles ??= () => { if (_latest is { } l) l.P.SetSpotifyWins(l.G.Key, false); };
            _submit ??= text => { if (_latest is { } l && text.Trim().Length > 0) l.P.Search(l.G.Key, text); };
            bool open = _open.Value;
            bool decided = g.Decision is LinkDecision.Accepted or LinkDecision.NotOnSpotify;
            bool asks = !decided && (g.Decision == LinkDecision.NotThis || g.Confidence == LinkConfidence.Check || gp.Focused);
            bool searching = _searching.Value || gp.Focused && asks;
            string key = C.Safe(g.Key);
            var best = g.Decision == LinkDecision.NotThis ? null : g.Best;

            Element art = new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f, Opacity = decided ? Z.DecidedOpacity : 1f,
                Children =
                [
                    g.FileCover is { } f ? Controls.Artwork(f, Z.Art, Z.Art, 0f) : Blank(Z.Art),
                    new BoxEl { Width = Z.ArrowW, Shrink = 0f, AlignItems = FlexAlign.Center, Children = [Icon(Icons.Forward, Z.ArrowGlyph, Tok.TextTertiary)] },
                    g.Decision != LinkDecision.NotOnSpotify && best?.Cover is { } c ? Controls.Artwork(c, Z.Art, Z.Art, 0f) : Blank(Z.Art),
                ],
            };
            var title = R.Type.TrackTitle("");
            Element name = new SpanTextEl(g.Artist.Length > 0
                ? [new TextSpan(g.Title, Color: Tok.TextPrimary), new TextSpan(" · " + g.Artist, Color: Tok.TextSecondary)]
                : [new TextSpan(g.Title, Color: Tok.TextPrimary)])
            {
                Size = title.Size, LineHeight = title.LineHeight, Weight = title.Weight, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            };
            (string said, ColorF dot) = decided
                ? (LocalLinkWords.Decided(g), g.Decision == LinkDecision.Accepted ? Tok.SystemFillSuccess : Tok.TextTertiary)
                : best is not null ? (LocalLinkWords.Confidence(g.Confidence) + " · " + LocalLinkWords.Evidence(best, g.IsAlbum), Ink(g.Confidence))
                : (LocalLinkWords.Confidence(LinkConfidence.Check) + " · " + Loc.Get(W.NothingFound), Ink(LinkConfidence.Check));
            Element evidence = new BoxEl
            {
                Direction = 0, Gap = Z.DotGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Margin = new Edges4(0f, Z.LineGap, 0f, 0f),
                Children =
                [
                    new BoxEl { Width = Z.Dot, Height = Z.Dot, Shrink = 0f, Corners = CornerRadius4.All(Z.Dot / 2f), Fill = dot },
                    Line(R.Type.TrackMeta(said) with { Color = Tok.TextPrimary }),
                ],
            };
            var verbs = new List<Element>(3);
            if (decided) verbs.Add(Verb(Z.UndoKey + key, Loc.Get(W.Undo), _undo!));
            else if (!asks)
            {
                verbs.Add(Verb(Z.AcceptKey + key, Loc.Get(W.Accept), _accept!));
                verbs.Add(Verb(Z.DetailsKey + key, Loc.Get(W.Details), _details!));
            }
            var words = new List<Element>(5)
            {
                name,
                Line(R.Type.TrackMeta(g.Detail + (!decided && !asks && g.Improves > 0 ? " · " + LocalLinkWords.Improves(g.Improves) : "")) with { Color = Tok.TextSecondary }),
                evidence,
            };
            if (verbs.Count > 0) words.Add(new BoxEl { Direction = 0, Gap = Z.VerbGap, MinWidth = 0f, Children = verbs.ToArray() });

            var drawer = new List<Element>(8);
            if (asks) Candidates(drawer, g, searching, key, gp.Focused);
            else if (!decided && open && best is not null) Details(drawer, g, key);
            if (drawer.Count > 0) words.Add(new BoxEl
            {
                Key = Z.DrawerKey + key, Direction = 1, MinWidth = 0f, ClipToBounds = true, Animate = Design.Reveal.Drawer,
                Padding = new Edges4(0f, Z.DrawerTop, 0f, 0f), Children = drawer.ToArray(),
            });
            return new BoxEl
            {
                Direction = 0, Gap = Z.ArtGap, AlignItems = FlexAlign.Start, MinWidth = 0f, Animate = Design.Reveal.Resize,
                Children = [art, new BoxEl { Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, Children = words.ToArray() }],
            };
        }

        static ColorF Ink(LinkConfidence c) => c switch
        {
            LinkConfidence.Exact => Tok.SystemFillSuccess,
            LinkConfidence.Strong => R.Ink.AccentAction,
            LinkConfidence.Likely => Tok.TextSecondary,
            _ => Tok.SystemFillCaution,
        };

        static Element Blank(float size) => new BoxEl
        {
            Width = size, Height = size, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Fill = Tok.FillSubtleSecondary,
            Children = [Icon(Icons.MusicNote, Z.ArrowGlyph, Tok.TextSecondary)],
        };

        /// <summary>Details: flat field rows, whose info wins, then "not this album" and "accept".</summary>
        void Details(List<Element> rows, LinkGroup g, string key)
        {
            foreach (var f in g.Fields)
            {
                Element mine = f.Kind == LinkFieldKind.Cover
                    ? (f.MineCover is { } mc ? Controls.Artwork(mc, Z.Swatch, Z.Swatch, 0f) : Line(R.Type.TrackMeta(LocalLinkWords.Empty) with { Color = Tok.TextTertiary }))
                    : Line(R.Type.TrackMeta(f.Mine.Length > 0 ? f.Mine : LocalLinkWords.Empty) with { Color = f.Mine.Length > 0 ? Tok.TextSecondary : Tok.TextTertiary });
                Element theirs = f.Kind == LinkFieldKind.Cover
                    ? Controls.Artwork(f.TheirCover, Z.Swatch, Z.Swatch, 0f)
                    : Line(R.Type.TrackMeta(f.Theirs.Length > 0 ? f.Theirs : LocalLinkWords.Empty) with { Color = f.Improves ? Tok.AccentTextPrimary : Tok.TextPrimary });
                rows.Add(new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f, MinHeight = Z.FieldRow,
                    Children =
                    [
                        Line(R.Type.TrackMeta(LocalLinkWords.Field(f.Kind)) with { Color = Tok.TextTertiary, Case = R.Case.For(LabelKind.Label), Width = Z.FieldLabel, Shrink = 0f }),
                        new BoxEl { Direction = 0, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, Children = [mine] },
                        new BoxEl { Width = Z.FieldArrow, Shrink = 0f, Children = [Icon(Icons.Forward, Z.ArrowGlyph, Tok.TextTertiary)] },
                        new BoxEl { Direction = 0, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, Children = [theirs] },
                    ],
                });
            }
            bool wins = g.SpotifyWins;
            rows.Add(new BoxEl
            {
                Key = Z.WinsKey + key, Direction = 0, Wrap = true, Gap = Z.VerbGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Margin = new Edges4(0f, Z.DrawerTop, 0f, 0f),
                Children =
                [
                    Line(R.Type.TrackMeta(Loc.Get(g.IsAlbum ? W.WhoseAlbum : W.WhoseSong)) with { Color = Tok.TextSecondary, Case = R.Case.For(LabelKind.Label) }),
                    RadioButton.Create(C.Lbl(Loc.Get(W.Spotifys)), wins, _toSpotify) with { Key = Z.WinsKey + key + ":spotify" },
                    RadioButton.Create(C.Lbl(Loc.Get(W.MyFiles)), !wins, _toFiles) with { Key = Z.WinsKey + key + ":files" },
                ],
            });
            rows.Add(new BoxEl
            {
                Direction = 0, Wrap = true, Gap = Z.VerbGap, AlignItems = FlexAlign.Center, MinWidth = 0f,
                Children =
                [
                    Verb(Z.AcceptKey + key + ":foot", Loc.Get(W.Accept), _accept!),
                    Verb(Z.NotThisKey + key, Loc.Get(g.IsAlbum ? W.NotThisAlbum : W.NotThisSong), _notThis!),
                    Line(R.Type.TrackMeta(Loc.Get(W.ShownOnly)) with { Color = Tok.TextTertiary, Case = R.Case.For(LabelKind.Label) }),
                ],
            });
        }

        /// <summary>A check: the candidates with "pick", then "search" and "not on spotify".</summary>
        void Candidates(List<Element> rows, LinkGroup g, bool searching, string key, bool focused)
        {
            for (int i = 0; i < g.Candidates.Count && i < Z.CandidatesShown; i++)
            {
                var c = g.Candidates[i];
                int at = i;
                var pick = _picks[i] ??= () => { if (_latest is { } l) l.P.Pick(l.G.Key, at); };
                rows.Add(new BoxEl
                {
                    Direction = 0, Gap = Z.CandidateGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Margin = new Edges4(0f, 0f, 0f, Z.CandidateBottom),
                    Children =
                    [
                        Controls.Artwork(c.Cover, Z.CandidateArt, Z.CandidateArt, 0f),
                        new BoxEl
                        {
                            Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
                            Children =
                            [
                                Line(R.Type.TrackMeta(c.Title) with { Color = Tok.TextPrimary }),
                                Line(R.Type.TrackMeta(g.IsAlbum ? c.Artist + " · " + c.Detail : c.Detail) with { Color = Tok.TextSecondary }),
                                // Why it was proposed (concept §6.2: evidence, never a score).
                                Line(R.Type.TrackMeta(LocalLinkWords.Evidence(c, g.IsAlbum)) with { Color = Tok.TextTertiary }),
                            ],
                        },
                        Verb(Z.PickKey + key + ":" + i, Loc.Get(W.Pick), pick),
                    ],
                });
            }
            rows.Add(new BoxEl
            {
                Direction = 0, Wrap = true, Gap = Z.VerbGap, AlignItems = FlexAlign.Center, MinWidth = 0f,
                // Opened on this group, the search box is already open under it: no verb for it.
                Children = focused
                    ?
                    [
                        Verb(Z.NotOnKey + key, Loc.Get(W.NotOnSpotify), _notOn!),
                        Wrapping(R.Type.TrackMeta(Loc.Get(W.NotOnSpotifyHint)) with { Color = Tok.TextTertiary, Grow = 1f, Shrink = 1f, Basis = 0f }),
                    ]
                    :
                    [
                        Verb(Z.SearchKey + key, Loc.Get(W.SearchHint), _search!),
                        Verb(Z.NotOnKey + key, Loc.Get(W.NotOnSpotify), _notOn!),
                        Wrapping(R.Type.TrackMeta(Loc.Get(W.NotOnSpotifyHint)) with { Color = Tok.TextTertiary, Grow = 1f, Shrink = 1f, Basis = 0f }),
                    ],
            });
            if (searching)
                rows.Add(new BoxEl
                {
                    Key = Z.SearchBoxKey + key, Direction = 1, MinWidth = 0f, ClipToBounds = true, Animate = Design.Reveal.Drawer,
                    Children = [TextBox.Create(_query, null, new TextBox.TextBoxOptions { Placeholder = g.Title, Width = Z.SearchW, OnCommit = _submit })],
                });
        }
    }
}
