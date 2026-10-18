// ── Themes/Zune/Structure/Views/Show/ZuneShowRail.cs — Zune's show rail slots (themes plan 6.3; P5 Z6b2) ───────────────────────────────
//
// Board Z-Show's identity column, drawn by the detail frame (`ZuneDetailRail`): the cover, the publisher link, the badge words ("podcast ·
// exclusive"), the rating line, the meta, the played LEDGER (a 3-dip bar and "120 played · 2 in progress · 690 to go"), the RING row (the
// accent play ring with its words — "resume" over "23 min left" — then save and share), the topic words. The frame places each slot; this file
// is what each one draws.
//
// WHAT THE CORE KEEPS: each slot is a core component (`PrimarySlot`, `LedgerSlot`, `RatingSlot`, `BadgesSlot`, `TopicsSlot`) that owns its tone
// memo, its overlay handle and its facts; it hands the decided values and verbs to a template here. The primary's decision
// (`ShowReaderRules.PrimaryOf`: follow, resume, episode 1 or latest), the follow write (`Controls.Library`), the trailer, the rate flyout's open
// and close and the topics search are the core's.
//
// THE RINGS' SIZE IS THE TIER'S, AND A TEMPLATE HERE HAS NO VIEW CONTEXT (the core calls it from a slot builder): each ring is a small component
// that reads the facts it is DRAWN IN (`TierContext.DrawnIn`), so a crossing re-skins the rings alone (36 → 40 at Compact, the words at Standard).
// Conflicts with the board: the disabled new-episode bell the stand-in draws has no ring (the board draws none); the "..." ring is the frame's
// menu, the "following" word of row 2 is the page's action.
//
// ONE HOME PER VERB (ZT5, audit A4). The band's row 2 carries "follow" from Small up (the window's band, `ZuneDetailTiers.PrimaryInHero`), so the
// rail draws no second follow: the primary leaves its follow ring (the ghost play stays: a show's own primary is never a band verb) and the save
// ring leaves the satellites. At Compact the band carries no action and the rail keeps both (Z-Show-Compact: "following" among the rings). The
// boards draw both (Z-Show: a rail heart beside the band's "following"); the owner's no-duplicates directive wins and the conflict is listed.

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
using static Wavee.Themes.Zune.ZuneShowRules;
using K = Wavee.Themes.Zune.ZuneDetailKit;

namespace Wavee.Themes.Zune;

internal static partial class ZuneShow
{
    // ── the layout record the rail's slots draw in ──

    /// <summary>The detail record the rail's rings are drawn in: the vertical arm's (the Compact record) below the content's Small edge, else the
    /// content tier's own (the same record the identity column reads).</summary>
    internal static ZuneDetailLayout RingLayoutOf(in TierFacts facts)
        // The vertical arm's ring row is one line at 400 (Z-Show-Compact, Z-Episode-Compact: resume, the rings, share, "..."): the saved ring stands without its
        // word there (its filled glyph is the state), so the row does not wrap its last ring onto a second line.
        => facts.Content == WidthTier.Compact ? ZuneDetailTiers.Hero with { SaveWord = false } : ZuneDetailTiers.Of(in facts);

    // ══ THE RINGS ════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>One ring: the 36 (40) disc and, where the tier writes one, its word (and a quiet second line under it).</summary>
    internal static Element RingEl(string id, string glyph, string name, string? word, string? sub, bool filled, Action click, ZuneDetailLayout lay)
    {
        bool written = word is { Length: > 0 } && lay.PlayWord;
        var kids = new List<Element>(2) { K.Disc(glyph, lay.RingSize, lay.RingGlyph, filled ? s_accent : null) };
        if (written) kids.Add(RingWords(word!, sub, lay));
        BoxEl box = new()
        {
            Key = ZuneDetailRules.RingPrefix + id, Direction = 0, AlignItems = FlexAlign.Center, Gap = lay.RingLabelGap, Shrink = 0f,
            MinHeight = lay.RingSize, Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
            BlocksDragArm = true, OnClick = click, Children = kids.ToArray(),
        };
        return written ? box : Controls.Named(box, name);
    }

    /// <summary>A ring's words: the label voice lowered as a command, and under it (when there is one) a quiet line.</summary>
    static Element RingWords(string word, string? sub, ZuneDetailLayout lay)
    {
        var first = R.Type.DenseTitle(word) with
        {
            Size = lay.LabelSize, Color = Tok.TextPrimary, Case = R.Case.For(LabelKind.Command), MaxLines = 1, Wrap = TextWrap.NoWrap,
        };
        if (sub is not { Length: > 0 }) return first;
        return new BoxEl
        {
            Direction = 1, MinWidth = 0f, Shrink = 0f,
            Children =
            [
                first,
                R.Type.TrackMeta(sub) with
                {
                    Size = lay.NotesSize, LineHeight = lay.NotesLine, Color = Tok.TextSecondary, Case = R.Case.For(LabelKind.Label),
                    MaxLines = 1, Wrap = TextWrap.NoWrap,
                },
            ],
        };
    }

    /// <summary>The CTA's primary by visit: new and not following → "follow" and a ghost play (episode 1 for a serial, else the latest); returning →
    /// "resume · N min left" on listen-next's resume; otherwise the latest. The accent is the app's, never the show tone.</summary>
    static Element Primary(ShowPrimaryArgs a)
        => Embed.Comp(a, static () => new PrimaryHost()) with { Key = "show:primary" };

    /// <summary>The primary's host: reads the tier it is drawn in and lays the rings out.</summary>
    sealed class PrimaryHost : Component
    {
        public override Element Render()
        {
            var a = UseProps<ShowPrimaryArgs>();
            var tiers = TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal)).Value.Tiers;
            var lay = RingLayoutOf(tiers);
            bool followInBand = !ZuneDetailTiers.PrimaryInHero(in tiers);
            Element PlayRing(ShowReaderRules.Primary which)
            {
                switch (which)
                {
                    case ShowReaderRules.Primary.Resume:
                        return RingEl("show-play", Icons.Play, Loc.Get(Strings.Podcast.Resume), Loc.Get(Strings.Podcast.Resume),
                            Strings.Podcast.Left(Episode.DurationWords(a.ResumeLeft)), filled: true, a.Resume, lay);
                    case ShowReaderRules.Primary.PlayFirst:
                        return RingEl("show-play", Icons.Play, Strings.Podcast.Badge.Episode(FormatCache.Int(a.FirstNumber)),
                            Strings.Podcast.Badge.Episode(FormatCache.Int(a.FirstNumber)), null, filled: true, a.First, lay);
                    default:
                        return RingEl("show-play", Icons.Play, Loc.Get(Strings.Podcast.Latest), Loc.Get(Strings.Podcast.Latest), null,
                            filled: true, a.Latest, lay);
                }
            }
            if (a.Primary != ShowReaderRules.Primary.Follow) return PlayRing(a.Primary);
            // The follow ring and the ghost play, in the same row either way (the ghost keeps its node when the band takes the follow over).
            return new BoxEl
            {
                Direction = 0, Wrap = true, Gap = lay.ActionsGap, AlignItems = FlexAlign.Center, MinWidth = 0f,
                Children = followInBand
                    ? [PlayRing(a.Ghost)]
                    : [
                        RingEl("show-follow", Icons.Add, Loc.Get(Strings.Artist.Follow), Loc.Get(Strings.Artist.Follow), null, filled: false, a.Follow, lay),
                        PlayRing(a.Ghost),
                    ],
            };
        }
    }

    // ══ THE LEDGER, THE RATING, THE BADGES, THE TOPICS, THE PUBLISHER ═══════════════════════════════════════════════════

    /// <summary>The played ledger (Z-Show): the 3-dip bar — played in the primary ink, in progress in the accent, the rest the track — and "84
    /// played · 3 in progress · 41 to go · 5 new", or, when the hydrate failed, "progress unavailable". The bar's parts ease their widths when a
    /// mark-played moves them.</summary>
    static Element Ledger(ShowLedgerArgs a)
    {
        if (a.Failed)
            return new BoxEl
            {
                Direction = 1, Width = a.Width, MinWidth = 0f,
                Children = [LedgerText(new TextSpan(Loc.Get(Strings.Podcast.ProgressUnavailable)))],
            };
        int sum = a.Played + a.InProgress + a.ToGo;
        float played = sum > 0 ? a.Played / (float)sum : 0f, progress = sum > 0 ? a.InProgress / (float)sum : 0f;
        TextSpan[] line = a.Fresh > 0
            ? [new TextSpan(Strings.Podcast.Ledger(a.Played, a.InProgress, a.ToGo) + " · "),
               new TextSpan(Strings.Podcast.LedgerNew(a.Fresh), Weight: 600, Color: R.Ink.AccentDecor)]
            : [new TextSpan(Strings.Podcast.Ledger(a.Played, a.InProgress, a.ToGo))];
        return new BoxEl
        {
            Direction = 1, Gap = LedgerGap, Width = a.Width, MinWidth = 0f,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, Height = LedgerBar, MinWidth = 0f, Shrink = 0f, Fill = Tok.FillSubtleTertiary, ClipToBounds = true,
                    Children =
                    [
                        new BoxEl { Key = "ledger:played", Grow = played, Basis = 0f, MinWidth = 0f, Fill = Tok.TextPrimary, Animate = LedgerSlide },
                        new BoxEl { Key = "ledger:progress", Grow = progress, Basis = 0f, MinWidth = 0f, Fill = Prop.Of(s_accent), Animate = LedgerSlide },
                        new BoxEl { Key = "ledger:rest", Grow = MathF.Max(0f, 1f - played - progress), Basis = 0f, MinWidth = 0f, Animate = LedgerSlide },
                    ],
                },
                LedgerText(line),
            ],
        };
    }

    static SpanTextEl LedgerText(params TextSpan[] spans) => new(spans)
    {
        Size = LedgerSize, LineHeight = LedgerLine, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, MinWidth = 0f,
    };

    /// <summary>The rating line: ★ 4.8 · "12,431 ratings" (· "yours 5★") — a button that opens the rate flyout, whose stars are DISABLED with their
    /// reason until the write lands.</summary>
    static Element Rating(ShowRatingArgs a) => Embed.Comp(a, static () => new RatingHost()) with { Key = "show:rating" };

    /// <summary>The rating line's host: it reads the tier it is DRAWN in (a template here has no view context) so the button is a 40 target at Compact,
    /// and keeps the newest args (the average and the count move while the page is open).</summary>
    sealed class RatingHost : Component, IPropsHost
    {
        ShowRatingArgs? _latest;
        readonly Signal<ShowRatingArgs?> _props = new(null);

        public void ApplyProps(object props)
        {
            _latest = (ShowRatingArgs)props;
            _props.Value = _latest;
        }

        public override Element Render()
        {
            _ = _props.Value;
            var tiers = TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal)).Value.Tiers;
            bool compact = tiers.Policy is not null && tiers.Window == WidthTier.Compact;
            return RatingRow(_latest!, compact ? ZuneListRules.CompactTarget : RatingMinHeight);
        }
    }

    static Element RatingRow(ShowRatingArgs a, float minHeight)
    {
        var button = new BoxEl
        {
            Direction = 0, Gap = RatingGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Shrink = 1f, MinHeight = minHeight,
            HoverFill = Tok.FillSubtleSecondary, Corners = R.Shape.All(ShapeRole.Control),
            Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand,
            OnClick = a.Toggle, OnRealized = a.Realized,
            Children =
            [
                Icon(Icons.FavoriteStarFill, RatingStar) with { Color = Prop.Of(s_accent) },
                R.Type.DenseTitle(a.Average) with { Color = Tok.TextPrimary, MaxLines = 1 },
                R.Type.DenseMeta(a.Count) with
                {
                    Color = Tok.TextSecondary, Case = R.Case.For(LabelKind.Label), MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
                },
            ],
        };
        return new BoxEl
        {
            Direction = 0, MinWidth = 0f, MaxWidth = float.IsFinite(a.Width) ? a.Width : float.NaN,
            Children = [Controls.Named(button, a.Name)],
        };
    }

    /// <summary>The badge words (Z-Show: "podcast · exclusive"): exclusive in the accent, explicit as the small boxed E, video in the quiet ink,
    /// one line of words with a dot between.</summary>
    static Element Badges(ShowBadgesArgs a)
    {
        var flags = a.Flags;
        var words = new List<Element>(5);
        void Add(Element word)
        {
            if (words.Count > 0)
                words.Add(R.Type.DenseMeta("·") with { Color = Tok.TextTertiary, MaxLines = 1, Shrink = 0f });
            words.Add(word);
        }
        if ((flags & ShowFlags.Exclusive) != 0)
            Add(R.Type.DenseMeta(Loc.Get(Strings.Podcast.Badge.Exclusive)) with
            {
                Color = Prop.Of(s_accent), Case = R.Case.For(LabelKind.Label), MaxLines = 1, Shrink = 0f,
            });
        if ((flags & ShowFlags.Explicit) != 0)
            Add(new BoxEl
            {
                Width = ExplicitBox, Height = ExplicitBox, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                BorderWidth = 1f, BorderColor = Tok.TextTertiary, Corners = R.Shape.All(ShapeRole.Control),
                Children = [R.Type.MicroMeta("E") with { Color = Tok.TextSecondary, MaxLines = 1 }],
            });
        if ((flags & ShowFlags.Video) != 0)
            Add(R.Type.DenseMeta(Loc.Get(Strings.Podcast.Badge.Video)) with
            {
                Color = Tok.TextSecondary, Case = R.Case.For(LabelKind.Label), MaxLines = 1, Shrink = 0f,
            });
        return new BoxEl
        {
            Direction = 0, Wrap = true, Gap = BadgeGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = words.ToArray(),
        };
    }

    /// <summary>The topic words (Z-Show: "football  sports  comedy"), under the rings: link words, wrapping, at the rail's measure. A topic is a name
    /// the catalogue wrote: it keeps its casing.</summary>
    static Element Topics(ShowTopicsArgs a)
    {
        var children = new Element[a.Topics.Length];
        for (int i = 0; i < children.Length; i++)
        {
            string topic = a.Topics[i];
            children[i] = new BoxEl
            {
                Key = "topic:" + topic, Direction = 0, AlignItems = FlexAlign.Center, MinHeight = TopicTarget, Shrink = 0f,
                Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink, Focusable = true, AllowFocusOnInteraction = false, OnClick = () => a.Open(topic),
                Children =
                [
                    R.Type.DenseMeta(topic) with
                    {
                        Color = Tok.TextSecondary, HoverColor = Tok.TextPrimary, MaxLines = 1, Wrap = TextWrap.NoWrap,
                    },
                ],
            };
        }
        return new BoxEl
        {
            Direction = 0, Wrap = true, Gap = TopicGap, MinWidth = 0,
            MaxWidth = float.IsFinite(a.Width) ? a.Width : float.NaN, Children = children,
        };
    }

    /// <summary>The publisher line (the Attribution slot): the publisher's name as written, in the 15 voice, one line, at the rail's measure.</summary>
    static Element Publisher(string publisher, float width) => new BoxEl
    {
        Direction = 0, MinWidth = 0f, MaxWidth = float.IsFinite(width) ? width : AboutMeasure,
        Children =
        [
            R.Type.TrackTitle(publisher) with
            {
                Color = Tok.TextPrimary, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
            },
        ],
    };

    // ══ THE SATELLITES ═══════════════════════════════════════════════════════════════════════════════════════════════════

    enum SatKind : byte { Trailer, Share, More }

    sealed record SatProps(SatKind Kind, string Uri, string Name, Action? Click, Func<ContextMenuModel?>? Menu);

    static Element Sat(SatKind kind, string uri, string name, Action? click, Func<ContextMenuModel?>? menu)
        => Embed.Comp(new SatProps(kind, uri, name, click, menu), static () => new SatHost());

    /// <summary>One satellite ring, drawn in the tier it is drawn in: the trailer, share and "..." (the heart is <see cref="HeartHost"/>'s).</summary>
    sealed class SatHost : Component
    {
        public override Element Render()
        {
            var p = UseProps<SatProps>();
            var lay = RingLayoutOf(TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal)).Value.Tiers);
            switch (p.Kind)
            {
                case SatKind.Trailer:
                    return K.Ring("trailer", Icons.TvMonitor, Loc.Get(Strings.Podcast.Trailer), false, null, p.Click, lay);
                case SatKind.Share:
                    return lay.ShareRing ? K.Ring("share", Icons.Share, Loc.Get(Strings.Menu.Share), false, null, p.Click, lay) : new BoxEl { Visible = false };
                default:
                    return lay.MoreRing && p.Menu is { } menu ? K.MoreRing(menu, lay) : new BoxEl { Visible = false };
            }
        }
    }

    sealed record HeartProps(string Uri, string Name, Prop<bool> Shown);

    /// <summary>The heart satellite: the save ring, present while the core says so (<see cref="ShowSatellitesArgs.HeartShown"/>: not while follow is the
    /// primary) AND the band does not carry "follow" (one home per verb). Its own component, so the presence it binds is one flag read in the tier it
    /// is drawn in.</summary>
    sealed class HeartHost : Component
    {
        public override Element Render()
        {
            var p = UseProps<HeartProps>();
            var tiers = TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal)).Value.Tiers;
            var lay = RingLayoutOf(tiers);
            bool inBand = !ZuneDetailTiers.PrimaryInHero(in tiers);
            return new BoxEl
            {
                Direction = 0, Shrink = 0f, Visible = inBand ? (Prop<bool>)false : p.Shown,
                Children = [K.SaveRing(p.Uri, p.Name, lay.SaveWord, s_accent, lay)],
            };
        }
    }

    /// <summary>The CTA's satellites: save (hidden while follow is the primary, and where the band carries follow) · trailer (when the show names one) ·
    /// share · "...". A FIXED array per slot set; what comes and goes binds its presence.</summary>
    static Element[] Satellites(ShowSatellitesArgs a) =>
    [
        Embed.Comp(new HeartProps(a.Uri, a.Name, a.HeartShown), static () => new HeartHost()) with { Key = "sat:heart" },
        new BoxEl { Key = "sat:trailer", Visible = a.TrailerShown, Children = [Sat(SatKind.Trailer, a.Uri, a.Name, a.PlayTrailer, null)] },
        Sat(SatKind.Share, a.Uri, a.Name, a.Share, null) with { Key = "sat:share" },
        Sat(SatKind.More, a.Uri, a.Name, null, a.More) with { Key = "sat:more" },
    ];
}
