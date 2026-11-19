// ── Themes/Zune/Structure/Views/Show/ZuneShowRail.cs — Zune's show primary rings and topic words (themes plan 6.3; P5 Z6b2) ─────────────────
//
// What the library's show pane (the RING row: the accent play ring with its words — "resume" over "23 min left") and the show aside (the topic
// words) draw: `ShowReaderStyle.Primary` and `ShowReaderStyle.Topics`.
//
// WHAT THE CORE KEEPS: each slot is a core component (`PrimarySlot`, `TopicsSlot`) that owns its facts; it hands the decided values and verbs to a
// template here. The primary's decision (`ShowReaderRules.PrimaryOf`: follow, resume, episode 1 or latest), the follow write (`Controls.Library`)
// and the topics search are the core's.
//
// THE RINGS' SIZE IS THE TIER'S, AND A TEMPLATE HERE HAS NO VIEW CONTEXT (the core calls it from a slot builder): each ring is a small component
// that reads the facts it is DRAWN IN (`TierContext.DrawnIn`), so a crossing re-skins the rings alone (36 → 40 at Compact, the words at Standard).
//
// ONE HOME PER VERB (ZT5, audit A4). Where the band's row 2 carries "follow" (`ZuneDetailTiers.PrimaryInHero` false) the primary leaves its follow
// ring (the ghost play stays: a show's own primary is never a band verb).

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

    /// <summary>One ring: the 36 (40) disc and, where the tier writes one, its word (and a quiet second line under it). A ring that has nothing to
    /// start (<paramref name="enabled"/> false: a show with no episodes) stays in place, dimmed and inert, as the kit's disabled ring.</summary>
    internal static Element RingEl(string id, string glyph, string name, string? word, string? sub, bool filled, Action click, ZuneDetailLayout lay,
        bool enabled = true)
    {
        bool written = word is { Length: > 0 } && lay.PlayWord;
        var kids = new List<Element>(2) { K.Disc(glyph, lay.RingSize, lay.RingGlyph, filled ? s_accent : null) };
        if (written) kids.Add(RingWords(word!, sub, lay));
        BoxEl box = new()
        {
            Key = ZuneDetailRules.RingPrefix + id, Direction = 0, AlignItems = FlexAlign.Center, Gap = lay.RingLabelGap, Shrink = 0f,
            MinHeight = lay.RingSize, Cursor = enabled ? CursorId.Hand : null, Role = AutomationRole.Button, Focusable = enabled, AllowFocusOnInteraction = false,
            BlocksDragArm = true, IsEnabled = enabled, Opacity = enabled ? 1f : ZuneBandRules.DisabledOpacity,
            OnClick = click, Children = kids.ToArray(),
        };
        return K.StableNamed(box, name, written);
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
            // A show with no episodes keeps its play ring in place, disabled (sh8: "latest" offered nothing to play); follow stays live.
            bool playable = !a.Empty;
            Element PlayRing(ShowReaderRules.Primary which)
            {
                switch (which)
                {
                    case ShowReaderRules.Primary.Resume:
                        return RingEl("show-play", Icons.Play, Loc.Get(Strings.Podcast.Resume), Loc.Get(Strings.Podcast.Resume),
                            Strings.Podcast.Left(Episode.DurationWords(a.ResumeLeft)), filled: true, a.Resume, lay, playable);
                    case ShowReaderRules.Primary.PlayFirst:
                        return RingEl("show-play", Icons.Play, Strings.Podcast.Badge.Episode(FormatCache.Int(a.FirstNumber)),
                            Strings.Podcast.Badge.Episode(FormatCache.Int(a.FirstNumber)), null, filled: true, a.First, lay, playable);
                    default:
                        return RingEl("show-play", Icons.Play, Loc.Get(Strings.Podcast.Latest), Loc.Get(Strings.Podcast.Latest), null,
                            filled: true, a.Latest, lay, playable);
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

    // ══ THE TOPICS ════════════════════════════════════════════════════════════════════════════════════════════════════════

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
}
