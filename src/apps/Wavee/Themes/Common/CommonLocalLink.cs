// ── Themes/Common/CommonLocalLink.cs — "Your files and Spotify" in the WinUI picture (#240 phase 1; board frames 28-30) ─────────────────
//
// WinUI and Classic (Classic follows the WinUI design) register the offer and the review from here; Zune draws its own
// (`Themes/Zune/Structure/Views/Local/ZuneLocalLink.cs`). The opt-in's body is one picture for every theme (`CommonBlocks`): it sits in the
// house ContentDialog, which each theme already paints. Every sentence is the core's (`LocalLinkWords`, the props).
//
// Built from the WinUI controls a WinUI page uses for the same jobs, never a look of its own:
//   Offer   an InfoBar (informational): the sentence as its title, the promise as its message, "Show me" as its action button, its close
//           button "Not now" (the UX spec: "WinUI: InfoBar for banners").
//   Review  the discography page's head (a BreadcrumbBar trail "On this PC › Review matches" over the display title, then a SelectorBar of
//           the confidences with their counts, "Accept all exact" at the bar's end), an informational InfoBar ("Nothing changes until you
//           accept"), then one settings card per group, as Settings draws a SettingsExpander:
//             header   the match's cover, "Title · Artist", the plain evidence and the files' detail as its description; on the right the
//                      confidence (a status glyph and its word), Accept and the expand chevron. A decided group: its line and Undo.
//             items    (Details) one item per field — the field, "Your file: …", Spotify's value (accent where it improves); whose info
//                      wins (two radio buttons); "Shown in Wavee only" with Not this album and Accept.
//                      (a Check, or after "Not this") one item per candidate with Pick; then Search… (a search box item) and Not on Spotify.
//           Items hang off their header in one rounded group, 1 DIP apart, and open with the house disclosure (Design.Reveal.Drawer); a
//           decision changes a card in place (Design.Reveal.Resize): no card leaves from under the pointer.

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
using static FluentGpu.Dsl.Ui;

using K = Wavee.Themes.Common.CommonLocalLinkRules;
using W = Wavee.Strings.LocalFiles.Link;

namespace Wavee.Themes.Common;

/// <summary>"Your files and Spotify" in the WinUI picture (see the file header).</summary>
internal static class CommonLocalLink
{
    // ══ THE OFFER (board 28 ①) ════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>WinUI's and Classic's template of <see cref="ViewKeys.LocalLinkOffer"/>.</summary>
    public static Element Offer(LocalLinkOfferProps p, in ViewContext ctx)
    {
        float target = CommonTiers.Target(TierContext.WindowOf(ctx.Tier()));
        return Embed.Comp(new OfferProps(p, target), static () => new OfferHost()) with { Key = K.OfferKey };
    }

    sealed record OfferProps(LocalLinkOfferProps P, float Target);

    /// <summary>The InfoBar's close button is "Not now": keyed, so it can be named and traced.</summary>
    static readonly TemplateParts s_offerParts = OfferParts();

    static TemplateParts OfferParts()
    {
        var parts = new TemplateParts();
        parts.Set<BoxEl>(InfoBar.PartCloseButton, static b => b with { Key = K.NotNowKey });
        return parts;
    }

    /// <summary>The offer's host: it owns the overlay "Show me" opens the dialog on.</summary>
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
            return InfoBar.Create(InfoBarSeverity.Informational, p.Text, p.Sub, onClose: p.NotNow, isClosable: true,
                actionButton: CommonTouch.Button(p.ShowMeLabel, _show, op.Target) with { Key = K.ShowMeKey }, parts: s_offerParts);
        }
    }

    // ══ THE OPT-IN'S BODY (board 28 ②), every theme ═══════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.LocalLinkOptIn"/> (every theme).</summary>
    public static Element OptIn(LocalLinkOptInProps p, in ViewContext ctx)
    {
        var lines = new List<Element>(p.Lines.Count);
        foreach (var l in p.Lines)
        {
            lines.Add(new BoxEl
            {
                Direction = 0, Gap = K.OptInTextGap, AlignItems = FlexAlign.Start, MinWidth = 0f,
                Children =
                [
                    new BoxEl
                    {
                        Width = K.OptInGlyphBox, Height = K.OptInGlyphBox, Shrink = 0f, Corners = Radii.ControlAll, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                        Fill = Tok.FillSubtleSecondary, Children = [Icon(l.Glyph, K.OptInGlyph, Tok.AccentTextPrimary)],
                    },
                    new BoxEl
                    {
                        Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
                        Children = [Wrapping(R.Type.DenseTitle(l.Title) with { Color = Tok.TextPrimary, Case = R.Case.For(LabelKind.Label) }), Wrapping(R.Type.DenseMeta(l.Body) with { Color = Tok.TextSecondary })],
                    },
                ],
            });
        }
        return new BoxEl
        {
            Key = K.OptInKey, Direction = 1, MinWidth = 0f,
            Children =
            [
                Wrapping(R.Type.DenseTitle(p.Body) with { Color = Tok.TextPrimary }),
                new BoxEl { Direction = 1, Gap = K.OptInLineGap, MinWidth = 0f, Margin = new Edges4(0f, K.OptInBodyGap, 0f, 0f), Children = lines.ToArray() },
                new BoxEl { Height = 1f, Shrink = 0f, Fill = Tok.StrokeDividerDefault, Margin = new Edges4(0f, K.OptInRuleTop, 0f, 0f) },
                new BoxEl
                {
                    Direction = 1, MinWidth = 0f, Margin = new Edges4(0f, K.OptInBoxTop, 0f, 0f),
                    Children =
                    [
                        CheckBox.Create(Lbl(p.AutoExactLabel), null, p.SetAutoExact) with { Key = K.OptInKey + ":exact" },
                        Wrapping(R.Type.DenseMeta(p.AutoExactSub) with { Color = Tok.TextSecondary, Margin = new Edges4(K.OptInCheckInset, 0f, 0f, 0f) }),
                    ],
                },
            ],
        };
    }

    // ── shared helpers (Zune's review reads them too) ────────────────────────────────────────────────────────────────

    /// <summary>A group key as a node key: the inspector's paths split on "/", and an album key holds one ("ARTIST / ALBUM").</summary>
    internal static string Safe(string key) => key.Replace(" / ", "|", StringComparison.Ordinal).Replace(' ', '_');

    /// <summary>A label in the style's label case.</summary>
    internal static string Lbl(string label) => FluentGpu.Text.TextCaseMap.Apply(label, R.Case.For(LabelKind.Label));

    /// <summary>A verb in the style's command case.</summary>
    internal static string Cmd(string label) => FluentGpu.Text.TextCaseMap.Apply(label, R.Case.For(LabelKind.Command));

    static TextEl Wrapping(TextEl text) => text with { Wrap = TextWrap.WrapWholeWords, MinWidth = 0f };

    static TextEl Line(TextEl text) => text with { MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f };

    /// <summary>The review's filters, in the SelectorBar's order.</summary>
    internal enum Filter : byte { All, Exact, Strong, Likely, Check, Decided }

    internal static bool Shows(Filter f, LinkGroup g)
    {
        bool done = g.Decision is LinkDecision.Accepted or LinkDecision.NotOnSpotify;
        return f switch
        {
            Filter.All => true,
            Filter.Decided => done,
            Filter.Exact => !done && g.Confidence == LinkConfidence.Exact,
            Filter.Strong => !done && g.Confidence == LinkConfidence.Strong,
            Filter.Likely => !done && g.Confidence == LinkConfidence.Likely,
            _ => !done && g.Confidence == LinkConfidence.Check,
        };
    }

    /// <summary>How many groups a filter holds.</summary>
    internal static int CountOf(Filter f, LinkView v)
    {
        int n = 0;
        foreach (var g in v.Groups) if (Shows(f, g)) n++;
        return n;
    }

    /// <summary>"Find on Spotify…" is matching this group now and has no answer yet.</summary>
    internal static bool Finding(LinkView v, string key)
    {
        foreach (string k in v.Finding) if (k == key) return true;
        return false;
    }

    internal static string FilterWord(Filter f) => Loc.Get(f switch
    {
        Filter.All => W.All,
        Filter.Exact => W.Exact,
        Filter.Strong => W.Strong,
        Filter.Likely => W.Likely,
        Filter.Check => W.ToCheck,
        _ => W.Done,
    });

    // ══ THE REVIEW (boards 29, 30) ════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>WinUI's and Classic's template of <see cref="ViewKeys.LocalLinkReview"/>.</summary>
    public static Element Review(LocalLinkReviewProps p, in ViewContext ctx)
    {
        var tiers = ctx.Tier();
        return Embed.Comp(new ReviewProps(p, tiers.Content == WidthTier.Compact, CommonTiers.Target(TierContext.WindowOf(tiers))), static () => new ReviewHost())
            with { Key = K.ReviewKey };
    }

    sealed record ReviewProps(LocalLinkReviewProps P, bool Compact, float Target);

    sealed class ReviewHost : Component
    {
        readonly Signal<int> _filter = new((int)Filter.All);
        static readonly TemplateParts s_parts = new();
        static readonly string[] s_keys = ["ll-filter:all", "ll-filter:exact", "ll-filter:strong", "ll-filter:likely", "ll-filter:check", "ll-filter:decided"];
        Action<int>? _crumb;
        ReviewProps? _latest;

        public override Element Render()
        {
            var rp = UseProps<ReviewProps>();
            _latest = rp;
            var p = rp.P;
            var v = p.View.Value;
            string? focus = p.Focus.Value;
            var filter = (Filter)_filter.Value;
            if (filter != Filter.All && CountOf(filter, v) == 0) filter = Filter.All;   // the last of a confidence decided: the bar shows All
            bool focused = focus is { Length: > 0 };
            _crumb ??= i => { if (i == 0) _latest?.P.Close(); };

            var cards = new List<Element>(v.Groups.Count);
            foreach (var g in v.Groups)
            {
                if (focused ? g.Key != focus : !Shows(filter, g)) continue;
                cards.Add(Embed.Comp(new CardProps(g, p, rp.Compact, rp.Target, focused), static () => new Card()) with { Key = K.CardKey + Safe(g.Key) });
            }
            bool finding = focused && Finding(v, focus!);
            if (cards.Count == 0)
                cards.Add(new BoxEl
                {
                    Key = K.EmptyKey, Direction = 1, MinWidth = 0f,
                    Children = finding ? [Wrapping(R.Type.DenseMeta(Loc.Get(W.Finding)) with { Color = Tok.TextSecondary })]
                        : v.Phase == LinkPhase.Matching
                        ? [Wrapping(R.Type.DenseMeta(LocalLinkWords.ReviewSub(v)) with { Color = Tok.TextSecondary })]
                        :
                        [
                            Wrapping(R.Type.SheetTitle(Loc.Get(W.EmptyTitle)) with { Color = Tok.TextPrimary }),
                            Wrapping(R.Type.DenseMeta(Loc.Get(W.EmptyBody)) with { Color = Tok.TextSecondary }),
                        ],
                });

            // The trail over the title (the discography page's head): "On this PC" goes back to the page.
            Element trail = new BoxEl
            {
                Key = K.CrumbKey, Direction = 0, Height = K.TrailH, Shrink = 0f, MinWidth = 0f, AlignItems = FlexAlign.End,
                Children = [BreadcrumbBar.Create([p.PageTitle, Loc.Get(W.ReviewTitle)], _crumb, size: rp.Target > 0f ? ControlSize.Large : ControlSize.Medium)],
            };
            Element title = R.Type.SurfaceDisplay(Loc.Get(W.ReviewTitle)) with { Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f };

            // The confidences as a SelectorBar ("Exact 2"); a focused review ("Find on Spotify…" on a song) offers "All" back instead.
            Element bar;
            if (focused)
                bar = CommonTouch.Button(Loc.Get(W.All), p.ShowAll, rp.Target, glyph: Icons.Back) with { Key = K.ShowAllKey };
            else
            {
                // All, then each confidence that holds a group ("Exact 2"); an empty one is no choice.
                var shown = new List<Filter>(6);
                var labels = new List<string>(6);
                var keys = new List<string>(6);
                int selected = 0;
                for (int i = 0; i < s_keys.Length; i++)
                {
                    int n = CountOf((Filter)i, v);
                    if (n == 0 && i != (int)Filter.All) continue;
                    if (i == _filter.Peek()) selected = shown.Count;
                    shown.Add((Filter)i);
                    labels.Add(FilterWord((Filter)i) + " " + n.ToString("N0", System.Globalization.CultureInfo.CurrentCulture));
                    keys.Add(s_keys[i]);
                }
                var filters = shown.ToArray();
                // A FRESH signal per render (the discography page's rule): the filter, not the control, is the truth.
                bar = SelectorBar.Create(labels.ToArray(), new Signal<int>(selected), onChange: i => { if ((uint)i < (uint)filters.Length) _filter.Value = (int)filters[i]; },
                    parts: s_parts, itemKeys: keys.ToArray(), style: rp.Target > 0f ? new SelectorBarStyle { ItemHeight = rp.Target } : null);
            }
            Element allExact = Button.Create(Loc.Get(W.AcceptAllExact), p.AcceptAllExact, ButtonAppearance.Accent, rp.Target > 0f ? ControlSize.Large : ControlSize.Medium,
                Icons.Accept, isEnabled: v.Exact > 0) with { Key = K.AllExactKey, Shrink = 0f };
            Element bars = new BoxEl
            {
                Direction = 0, Gap = K.ToolbarGap, AlignItems = FlexAlign.Center, MinWidth = 0f,   // one row at every tier: the bar scrolls under its edge fade, nothing restacks
                Children =
                [
                    new ScrollEl
                    {
                        Horizontal = true, ContentSized = true, Grow = 1f, Shrink = 1f, MinWidth = 0f, SuppressScrollBar = true, AutoEdgeFade = true,
                        Content = new BoxEl { Direction = 0, Shrink = 0f, Children = [bar] },
                    },
                    allExact,
                ],
            };
            Element note = InfoBar.Create(InfoBarSeverity.Informational, Loc.Get(W.NothingChanges), LocalLinkWords.ReviewSub(v), isClosable: false);

            return new BoxEl
            {
                Direction = 1, MinWidth = 0f, Padding = new Edges4(0f, K.ReviewTop, 0f, K.ReviewBottom),
                Children =
                [
                    new BoxEl { Direction = 1, Gap = K.HeadGap, MinWidth = 0f, Children = [trail, title] },
                    new BoxEl { Key = K.ChipsKey, Direction = 1, MinWidth = 0f, Margin = new Edges4(0f, K.ToolbarTop, 0f, 0f), Children = [bars] },
                    new BoxEl { Direction = 1, MinWidth = 0f, Margin = new Edges4(0f, K.NoteTop, 0f, 0f), Children = [note] },
                    new BoxEl { Direction = 1, Gap = K.CardGap, MinWidth = 0f, Margin = new Edges4(0f, K.CardsTop, 0f, 0f), Children = cards.ToArray() },
                ],
            };
        }
    }

    // ── one card: a settings card and its items ──────────────────────────────────────────────────────────────────────

    /// <param name="Focused">The review was opened on this group ("Find on Spotify…"): its candidates show at once (F-B), each with its evidence,
    /// with the search box open and Not on Spotify, whatever its confidence.</param>
    sealed record CardProps(LinkGroup G, LocalLinkReviewProps P, bool Compact, float Target, bool Focused = false);

    sealed class Card : Component
    {
        readonly Signal<bool> _open = new(false), _searching = new(false);
        readonly Signal<string> _query = new("");
        CardProps? _latest;
        Action? _accept, _details, _notThis, _notOn, _undo, _search, _toSpotify, _toFiles;
        Action<string>? _submit;
        readonly Action?[] _picks = new Action?[8];

        public override Element Render()
        {
            var cp = UseProps<CardProps>();
            _latest = cp;
            var g = cp.G;
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
            bool asks = !decided && (g.Decision == LinkDecision.NotThis || g.Confidence == LinkConfidence.Check || cp.Focused);
            bool searching = _searching.Value || cp.Focused && asks;
            var items = new List<Element>(8);
            if (asks) Candidates(items, g, cp, searching);
            else if (!decided && open && g.Best is not null) Details(items, g, cp);

            var kids = new List<Element>(2) { Header(g, cp, decided, asks, open) };
            if (items.Count > 0)
                kids.Add(new BoxEl
                {
                    Key = K.DrawerKey + Safe(g.Key), Direction = 1, Gap = K.ItemGap, MinWidth = 0f, ClipToBounds = true, Animate = Design.Reveal.Drawer,
                    Children = items.ToArray(),
                });
            // One rounded group, the items 1 DIP apart under the header (a SettingsExpander's look).
            return new BoxEl
            {
                Direction = 1, Gap = K.ItemGap, MinWidth = 0f, Corners = Radii.ControlAll, ClipToBounds = true, BorderWidth = 1f,
                BorderColor = Tok.StrokeCardDefault, Animate = Design.Reveal.Resize, Children = kids.ToArray(),
            };
        }

        static SettingsCard.Style HeadStyle => SettingsCard.DefaultStyle with { BorderWidth = 0f, CornerRadius = 0f, HeaderIconSize = K.Cover };

        static SettingsCard.Style ItemStyle => SettingsExpander.DefaultStyle.ItemCardStyle with { BorderWidth = 0f, HeaderIconSize = K.CandidateCover };

        Element Header(LinkGroup g, CardProps cp, bool decided, bool asks, bool open)
        {
            var best = g.Decision == LinkDecision.NotThis ? null : g.Best;
            string key = Safe(g.Key);
            string name = g.Artist.Length > 0 ? g.Title + " · " + g.Artist : g.Title;
            string evidence = decided ? LocalLinkWords.Decided(g) : best is not null ? LocalLinkWords.Evidence(best, g.IsAlbum) : Loc.Get(W.NothingFound);
            var right = new List<Element>(4);
            if (decided)
                right.Add(CommonTouch.Button(Loc.Get(W.Undo), _undo!, cp.Target, glyph: Icons.Undo) with { Key = K.UndoKey + key });
            else
            {
                right.Add(Status(g.Confidence));
                if (!asks)
                {
                    right.Add(new BoxEl
                    {
                        Shrink = 0f, Opacity = open ? 0f : 1f, HitTestVisible = !open,
                        Children = [CommonTouch.Button(Loc.Get(W.Accept), _accept!, cp.Target) with { Key = K.AcceptKey + key }],
                    });
                    right.Add(Button.Create("", _details!, ButtonAppearance.Subtle, cp.Target > 0f ? ControlSize.Large : ControlSize.Medium, open ? Icons.ChevronUp : Icons.ChevronDown)
                        with { Key = K.DetailsKey + key, Shrink = 0f });
                }
            }
            Element cover = decided && g.Decision == LinkDecision.NotOnSpotify ? NoteTile(K.Cover)
                : best?.Cover is { } c ? Controls.Artwork(c, K.Cover, K.Cover, K.CoverRadius)
                : g.FileCover is { } f ? Controls.Artwork(f, K.Cover, K.Cover, K.CoverRadius) : NoteTile(K.Cover);
            return SettingsCard.Create(new SettingsCard.Options
            {
                Header = name,
                Description = evidence + " · " + g.Detail,
                HeaderIconElement = new BoxEl { Opacity = decided ? K.DecidedCoverOpacity : 1f, Children = [cover] },
                Content = new BoxEl { Direction = 0, Gap = K.ActionsGap, AlignItems = FlexAlign.Center, Shrink = 0f, Children = right.ToArray() },
                Style = HeadStyle,
            });
        }

        /// <summary>The confidence as WinUI says a status: a filled status glyph in its colour and the word.</summary>
        static Element Status(LinkConfidence c)
        {
            (string glyph, ColorF ink) = c switch
            {
                LinkConfidence.Exact => (Icons.Accept, Tok.SystemFillSuccess),
                LinkConfidence.Strong => (Icons.Accept, Tok.AccentTextPrimary),
                LinkConfidence.Likely => (Icons.Info, Tok.TextSecondary),
                _ => (Icons.Warning, Tok.SystemFillCaution),
            };
            return new BoxEl
            {
                Direction = 0, Gap = K.EvidenceGap, AlignItems = FlexAlign.Center, Shrink = 0f, Margin = new Edges4(0f, 0f, K.StatusGap, 0f),
                Children = [Icon(glyph, K.EvidenceGlyph, ink), R.Type.DenseMeta(LocalLinkWords.Confidence(c)) with { Color = Tok.TextSecondary, MaxLines = 1 }],
            };
        }

        /// <summary>Details (board 29's Strong card): one item per field, then whose info wins, then Not this album and Accept.</summary>
        void Details(List<Element> items, LinkGroup g, CardProps cp)
        {
            string key = Safe(g.Key);
            string mineWord = Loc.Get(W.YourFile);
            foreach (var f in g.Fields)
            {
                Element theirs = f.Kind == LinkFieldKind.Cover
                    ? Controls.Artwork(f.TheirCover, K.Swatch, K.Swatch, K.CoverRadius)
                    : Line(R.Type.DenseTitle(f.Theirs.Length > 0 ? f.Theirs : LocalLinkWords.Empty) with { Color = f.Improves ? Tok.AccentTextPrimary : Tok.TextPrimary });
                string mine = f.Kind == LinkFieldKind.Cover ? (f.MineCover is null ? LocalLinkWords.Empty : "") : f.Mine.Length > 0 ? f.Mine : LocalLinkWords.Empty;
                items.Add(SettingsCard.Create(new SettingsCard.Options
                {
                    Header = LocalLinkWords.Field(f.Kind),
                    Description = mine.Length > 0 ? mineWord + ": " + mine : mineWord,
                    Content = new BoxEl { Direction = 0, Gap = K.SwatchGap, AlignItems = FlexAlign.Center, Shrink = 0f, Children = [Icon(Icons.Forward, K.ArrowGlyph, Tok.TextTertiary), theirs] },
                    Style = ItemStyle,
                }));
            }
            bool wins = g.SpotifyWins;
            items.Add(SettingsCard.Create(new SettingsCard.Options
            {
                Header = Loc.Get(g.IsAlbum ? W.WhoseAlbum : W.WhoseSong),
                Content = new BoxEl
                {
                    Key = K.WinsKey + key, Direction = 0, Gap = K.RadioGap, AlignItems = FlexAlign.Center, Shrink = 0f,
                    Children =
                    [
                        RadioButton.Create(Loc.Get(W.Spotifys), wins, _toSpotify) with { Key = K.WinsKey + key + ":spotify" },
                        RadioButton.Create(Loc.Get(W.MyFiles), !wins, _toFiles) with { Key = K.WinsKey + key + ":files" },
                    ],
                },
                Style = ItemStyle,
            }));
            items.Add(SettingsCard.Create(new SettingsCard.Options
            {
                Header = Loc.Get(W.ShownOnly),
                Content = new BoxEl
                {
                    Direction = 0, Gap = K.FootGap, AlignItems = FlexAlign.Center, Shrink = 0f,
                    Children =
                    [
                        CommonTouch.Button(Loc.Get(g.IsAlbum ? W.NotThisAlbum : W.NotThisSong), _notThis!, cp.Target) with { Key = K.NotThisKey + key },
                        CommonTouch.Button(Loc.Get(W.Accept), _accept!, cp.Target, accent: true) with { Key = K.AcceptKey + key + ":foot" },
                    ],
                },
                Style = ItemStyle,
            }));
        }

        /// <summary>"Closest on Spotify" (a Check, or after "Not this"): one item per candidate with Pick, then Search… and Not on Spotify.</summary>
        void Candidates(List<Element> items, LinkGroup g, CardProps cp, bool searching)
        {
            string key = Safe(g.Key);
            for (int i = 0; i < g.Candidates.Count && i < K.CandidatesShown; i++)
            {
                var c = g.Candidates[i];
                int at = i;
                var pick = _picks[i] ??= () => { if (_latest is { } l) l.P.Pick(l.G.Key, at); };
                // Each candidate says why it was proposed (concept §6.2: evidence, never a score), then where it is from.
                string meta = LocalLinkWords.Evidence(c, g.IsAlbum) + " · " + (g.IsAlbum ? c.Artist + " · " + c.Detail : c.Detail);
                items.Add(SettingsCard.Create(new SettingsCard.Options
                {
                    Header = c.Title,
                    Description = meta,
                    HeaderIconElement = Controls.Artwork(c.Cover, K.CandidateCover, K.CandidateCover, K.CoverRadius),
                    Content = CommonTouch.Button(Loc.Get(W.Pick), pick, cp.Target) with { Key = K.PickKey + key + ":" + i },
                    Style = ItemStyle with { Padding = K.CandidatePad },
                }));
            }
            items.Add(SettingsCard.Create(new SettingsCard.Options
            {
                Header = Loc.Get(W.NotOnSpotifyHint),
                Content = new BoxEl
                {
                    Direction = 0, Gap = K.FootGap, AlignItems = FlexAlign.Center, Shrink = 0f,
                    // Opened on this group, the search box is already open under it: no toggle for it.
                    Children = cp.Focused
                        ? [CommonTouch.Button(Loc.Get(W.NotOnSpotify), _notOn!, cp.Target, glyph: Icons.Cancel) with { Key = K.NotOnKey + key }]
                        :
                        [
                            CommonTouch.Button(Loc.Get(W.Search), _search!, cp.Target, glyph: Icons.Search) with { Key = K.SearchKey + key },
                            CommonTouch.Button(Loc.Get(W.NotOnSpotify), _notOn!, cp.Target, glyph: Icons.Cancel) with { Key = K.NotOnKey + key },
                        ],
                },
                Style = ItemStyle,
            }));
            if (searching)
                items.Add(SettingsCard.Create(new SettingsCard.Options
                {
                    Header = Loc.Get(W.SearchHint),
                    Content = TextBox.Create(_query, null, new TextBox.TextBoxOptions
                    {
                        Placeholder = g.Title, Width = cp.Compact ? float.NaN : K.SearchBoxW, OnCommit = _submit,
                    }) with { Key = K.SearchBoxKey + key },
                    Style = ItemStyle,
                }));
        }

        static Element NoteTile(float size) => new BoxEl
        {
            Width = size, Height = size, Shrink = 0f, Corners = CornerRadius4.All(K.CoverRadius), AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Fill = Tok.FillSubtleSecondary, Children = [Icon(Icons.MusicNote, K.NoteGlyph, Tok.TextSecondary)],
        };
    }
}
