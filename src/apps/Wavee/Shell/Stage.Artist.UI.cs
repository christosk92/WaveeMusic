// ── Shell/Stage.Artist.UI.cs ───────────────────────────────────────────────────────────────────────────────────────
// The stage's Artist pane: the header band (the artist's photo across the pane, name, verified, listeners · rank, tour,
// Follow / Go to artist), then About (the whole bio, Read more) + the gallery strip beside This song (credits grouped by
// person), Popular (top five, playable), Where people listen and Fans also like; `ArtistRules` holds the decisions
//
// Role: UI
// Owner: K
// Wave: 7
// Budget: 800 lines
// Spec: docs/plans/wavee/fullscreen-flagship-implementation.md §4.10 (the Artist mode)
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// ONE ROOT, SIX READERS. `ArtistStage` resolves the artist exactly as the rail does (Rail.NowPlayingArtist), asks for what
// the pane paints ONCE per (artist, track, scope) and lays the pane out from `Stage.Layout` — every DIP from PaneW/PaneH,
// every type size × QueueScale. Each section is its own component on the tables it reads (the header on the artist row,
// This song on the credits edge, Popular on the popular edge + the track rows + the deck's current id …), so a publication
// re-renders only the section that paints it; the root re-renders on a track, layout or scope change.
//
//   ScrollEl[ HeaderBand · Row[ About(bio · gallery) | This song · Popular · Where people listen · Fans also like ] ]
//   narrow (PaneW < 1100): ScrollEl[ HeaderBand · This song · Popular · About ]
//
//   • All ink is StageInk (Ink · Plate · Stroke · Veil); the accent is the context's AccentSet. No white literal anywhere.
//   • The header photo is laid out at the pane's own width and a height tied to the pane (0.3 · PaneH, clamped), cover-fit:
//     never a fixed 1200 × 240 slot leaving grey beside it.
//   • Navigation CLOSES the stage first (V-U29): the shell body is collapsed under it.

using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

using Ink = Wavee.Design.StageInk;

namespace Wavee;

public static partial class Stage
{
    // ══ 1. THE PURE RULES (Wavee.Tests/StageArtistRulesTests.cs) ════════════════════════════════════════════════════

    /// <summary>Every Artist-pane decision that is not a tree: the column split, the header height, the reading measure,
    /// the listener line, credits grouped by person, how many avatars / tiles fit. System-only (no Element, no signal, no
    /// entity read) so the tests drive the real arithmetic. Public: this assembly has no <c>InternalsVisibleTo</c>.</summary>
    public static class ArtistRules
    {
        /// <summary>Two columns from this pane width; one column under it.</summary>
        public const float TwoColumnMinW = 1100f;
        /// <summary>The header band's share of the pane height and its clamp (the floor grows with the type scale, so a
        /// 72-DIP name never overflows its band).</summary>
        public const float HeaderFrac = 0.3f, HeaderMin = 200f, HeaderMax = 480f;
        /// <summary>The left column's share of the two-column row: at least / at most this much of it.</summary>
        public const float LeftMinFrac = 0.45f, LeftMaxFrac = 0.6f;
        /// <summary>The reading measure in ems: 68 characters at Segoe's ~0.5 em average advance.</summary>
        public const float MeasureEms = 34f;
        public const int PopularCount = 5, CityCount = 5, GalleryCount = 4, FansMax = 8, CreditPeopleMax = 10, BioLines = 6;

        /// <summary>One contributor: the name, its distinct roles ", " joined in wire order, and the artist it links to
        /// (0 = an unlinked contributor).</summary>
        public readonly record struct CreditPerson(string Name, string Roles, int ArtistSlot);

        public static bool TwoColumns(float paneW) => paneW >= TwoColumnMinW;

        /// <summary>0.3 · the pane height, rounded, clamped to [HeaderMin · scale, HeaderMax].</summary>
        public static float HeaderHeight(float paneH, float scale)
            => Math.Clamp(MathF.Round(paneH * HeaderFrac), MathF.Min(HeaderMin * MathF.Max(1f, scale), HeaderMax), HeaderMax);

        /// <summary>~68 characters of <paramref name="typeSize"/> type.</summary>
        public static float ReadingMeasure(float typeSize) => MathF.Round(typeSize * MeasureEms);

        /// <summary>The left (About) column: the reading measure, held between 45 % and 60 % of the row; the right column
        /// takes the rest.</summary>
        public static float LeftColumnW(float paneW, float gap, float measure)
        {
            float row = MathF.Max(0f, paneW - gap);
            return MathF.Floor(Math.Clamp(measure, row * LeftMinFrac, row * LeftMaxFrac));
        }

        /// <summary>"7.9M" — the compact count the rest of the app uses (Detail.Text.CompactCount, current culture).</summary>
        public static string ListenerCount(long n) => Detail.Text.CompactCount(n);

        /// <summary>The non-empty parts " · " joined ("7.9M monthly listeners · #312 in the world").</summary>
        public static string MetaLine(string listeners, string rank)
            => listeners.Length == 0 ? rank : rank.Length == 0 ? listeners : listeners + " · " + rank;

        /// <summary>Does a bio of <paramref name="plainChars"/> characters overflow <paramref name="lines"/> lines of
        /// <paramref name="typeSize"/> type at <paramref name="width"/>? (Only then does Read more show.)</summary>
        public static bool BioClamps(int plainChars, float width, float typeSize, int lines)
        {
            if (plainChars <= 0 || lines <= 0) return false;
            int perLine = Math.Max(1, (int)(width / MathF.Max(1f, typeSize * 0.5f)));
            return plainChars > perLine * lines;
        }

        /// <summary>How many <paramref name="tile"/>-wide items fit in <paramref name="width"/> with <paramref name="gap"/>
        /// between them, capped by what is there and by <paramref name="max"/>.</summary>
        public static int FitCount(float width, float tile, float gap, int available, int max)
        {
            if (tile <= 0f || width <= 0f) return 0;
            int fit = (int)MathF.Floor((width + gap) / (tile + gap));
            return Math.Max(0, Math.Min(Math.Min(available, max), fit));
        }

        /// <summary>The edge of each of <paramref name="n"/> equal tiles across <paramref name="width"/>.</summary>
        public static float TileW(float width, float gap, int n)
            => n <= 0 ? 0f : MathF.Max(0f, MathF.Floor((width - gap * (n - 1)) / n));

        /// <summary>A city bar's share of the loudest city, 0..1.</summary>
        public static float CityFraction(uint listeners, uint max) => max == 0 ? 0f : Math.Clamp((float)listeners / max, 0f, 1f);

        /// <summary>The credits GROUPED BY PERSON, in first-appearance order: one entry per name (case-insensitive), its
        /// distinct roles in wire order, its artist link from the credit's own target or — when the credit carries none —
        /// from a billed artist of the same name. At most <paramref name="max"/> people.</summary>
        public static CreditPerson[] GroupCredits(ReadOnlySpan<string> names, ReadOnlySpan<string> roles, ReadOnlySpan<int> slots,
                                                  ReadOnlySpan<string> billedNames, ReadOnlySpan<int> billedSlots, int max)
        {
            var people = new List<string>(Math.Min(names.Length, Math.Max(0, max)));
            var roleLists = new List<List<string>>(people.Capacity);
            var links = new List<int>(people.Capacity);
            for (int i = 0; i < names.Length; i++)
            {
                string name = (names[i] ?? "").Trim();
                if (name.Length == 0) continue;
                int at = -1;
                for (int k = 0; k < people.Count && at < 0; k++)
                    if (string.Equals(people[k], name, StringComparison.OrdinalIgnoreCase)) at = k;
                if (at < 0)
                {
                    if (people.Count >= max) continue;
                    at = people.Count;
                    people.Add(name);
                    roleLists.Add(new List<string>(2));
                    links.Add(0);
                }
                string role = i < roles.Length ? (roles[i] ?? "").Trim() : "";
                if (role.Length > 0)
                {
                    var list = roleLists[at];
                    bool seen = false;
                    for (int r = 0; r < list.Count && !seen; r++) seen = string.Equals(list[r], role, StringComparison.OrdinalIgnoreCase);
                    if (!seen) list.Add(role);
                }
                if (links[at] <= 0 && i < slots.Length && slots[i] > 0) links[at] = slots[i];
            }
            var result = new CreditPerson[people.Count];
            for (int k = 0; k < people.Count; k++)
            {
                int link = links[k];
                for (int b = 0; link <= 0 && b < billedNames.Length && b < billedSlots.Length; b++)
                    if (billedSlots[b] > 0 && string.Equals(billedNames[b], people[k], StringComparison.OrdinalIgnoreCase)) link = billedSlots[b];
                result[k] = new CreditPerson(people[k], string.Join(", ", roleLists[k]), Math.Max(0, link));
            }
            return result;
        }
    }

    // ══ 2. THE PANE ═════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The Artist pane (PaneHost mounts it under KeepAlive). Re-renders on the playing track's key, the layout and
    /// the scope; asks for the artist's overview, its popular + related edges and the track's credits once per (artist,
    /// track, scope), never per render.</summary>
    sealed class ArtistStage : Component
    {
        public override Element Render()
        {
            var ctx = UseContext(StageContext)!;
            var L = ctx.Layout.Value;
            var track = ctx.RowValue();
            _ = Entities.ScopeEpoch.Value;                     // FIRST: a scope switch re-points the table reads below
            uint epoch = Entities.ScopeEpoch.Peek();
            _ = Entities.Current.Edges.TrackArtists.Changed.Value;   // the billed artists land after the row
            var artist = Rail.NowPlayingArtist(track);        // the rail's resolver (§4.12)
            int a = artist.Slot, t = track.Slot;
            UseEffect(() => Demand(a, t), DepKey.From(a, t, (int)epoch, 0));

            float s = L.QueueScale;
            if (!artist.IsValid)
                return new BoxEl
                {
                    Grow = 1f, MinHeight = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children = [new TextEl(Loc.Get(Strings.Player.NothingPlaying)) { Size = Px(14f, s), LineHeight = Px(20f, s), Color = Ink.InkTertiary }],
                };

            float paneW = L.PaneW, gap = Px(Spacing.XXXL, s);
            float measure = ArtistRules.ReadingMeasure(Px(BioSize, s));
            Element header = Embed.Comp(new HeaderProps(a, paneW, ArtistRules.HeaderHeight(L.PaneH, s), s), static () => new HeaderBand());
            Element song = Embed.Comp(new SongProps(t, s), static () => new SongCredits());
            Element popular = Embed.Comp(new ArtistProps(a, s), static () => new PopularList());
            Element body;
            if (ArtistRules.TwoColumns(paneW))
            {
                float left = ArtistRules.LeftColumnW(paneW, gap, measure);
                float right = MathF.Max(0f, paneW - gap - left);
                body = new BoxEl
                {
                    Direction = 0, Gap = gap, AlignItems = FlexAlign.Start, MinWidth = 0f,
                    Children =
                    [
                        new BoxEl { Direction = 1, Width = left, Shrink = 0f, MinWidth = 0f,
                                    Children = [Embed.Comp(new AboutProps(a, left, measure, s), static () => new AboutBlock())] },
                        new BoxEl
                        {
                            Direction = 1, Width = right, Shrink = 0f, MinWidth = 0f,
                            Children =
                            [
                                song, popular,
                                Embed.Comp(new ArtistProps(a, s), static () => new CityList()),
                                Embed.Comp(new FansProps(a, right, s), static () => new FansRow()),
                            ],
                        },
                    ],
                };
            }
            else
                body = new BoxEl
                {
                    Direction = 1, MinWidth = 0f,
                    Children = [song, popular, Embed.Comp(new AboutProps(a, paneW, measure, s), static () => new AboutBlock())],
                };

            return new ScrollEl
            {
                Grow = 1f, MinHeight = 0f, AutoEdgeFade = true, ScrollKey = "stageartist",
                Content = new BoxEl { Direction = 1, MinWidth = 0f, Gap = gap, Padding = new Edges4(0f, 0f, 0f, gap), Children = [header, body] },
            };
        }

        /// <summary>What the pane paints, asked ONCE per key: the overview (stats, bio, header, tour — and with it the
        /// gallery and the cities), the popular and related edges while nobody has answered, the track's credits.</summary>
        static void Demand(int artistSlot, int trackSlot)
        {
            var e = Entities.Current.Edges;
            var artist = new Artist(artistSlot);
            if (artist.IsValid)
            {
                Entities.Ensure(artist, ArtistFields.Overview);
                if (e.ArtistPopular.State(artistSlot) == EdgeState.Unknown) Entities.EnsureEdge(FetchEdge.ArtistPopular, artistSlot);
                if (e.ArtistRelated.State(artistSlot) == EdgeState.Unknown) Entities.EnsureEdge(FetchEdge.ArtistRelated, artistSlot);
            }
            if (new Track(trackSlot).IsValid && e.TrackCredits.State(trackSlot) == EdgeState.Unknown)
                Entities.EnsureEdge(FetchEdge.TrackCredits, trackSlot);
        }
    }

    // ── the shared pieces ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The About body's reading size (× QueueScale): 18/28.</summary>
    const float BioSize = 18f, BioLine = 28f;

    static float Px(float v, float s) => MathF.Round(v * s);

    static BoxEl NoSection() => new() { HitTestVisible = false };

    /// <summary>A section title in the stage's display face, 20/28 × scale.</summary>
    static TextEl SectionTitle(string title, float s) => new(title)
    {
        Size = Px(20f, s), LineHeight = Px(28f, s), Weight = 600, FontFamily = DisplayFace, Color = Ink.Ink, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
    };

    /// <summary>A right-column section: the title over the body on the stage's quiet plate (ink at a low alpha, a hairline
    /// that inverts with the ink — a step on either arm). The bottom padding is the gap, so an absent section leaves none.</summary>
    static Element PlateSection(string title, float s, Element body) => new BoxEl
    {
        Direction = 1, MinWidth = 0f, Padding = new Edges4(0f, 0f, 0f, Px(Spacing.XXL, s)),
        Children =
        [
            new BoxEl
            {
                Direction = 1, MinWidth = 0f, Gap = Px(Spacing.M, s), Padding = Edges4.All(Px(Spacing.L, s)), Corners = Radii.CardAll,
                Fill = Ink.Plate, BorderWidth = 1f, BorderColor = Ink.Stroke,
                Children = [SectionTitle(title, s), body],
            },
        ],
    };

    /// <summary>Open an artist page from the pane: leave the stage FIRST (V-U29).</summary>
    static void GoToArtist(Action<string>? begin, int artistSlot)
    {
        var a = new Artist(artistSlot);
        if (!a.IsValid || !a.Uri.IsValid) return;
        Close(begin, "navigate");
        Shell.GoTo(Shell.For(a.Uri, a.Name));
    }

    sealed record HeaderProps(int Artist, float W, float H, float S);
    sealed record ArtistProps(int Artist, float S);
    sealed record AboutProps(int Artist, float W, float Measure, float S);
    sealed record SongProps(int Track, float S);
    sealed record FansProps(int Artist, float W, float S);

    // ══ 3. THE HEADER BAND ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The artist's photo across the pane (HeroImageId — the landscape header, else the portrait), the stage veil
    /// rising from the bottom, and over it: name (48 display × scale) + the verified badge, "listeners · rank", the tour
    /// line, then Follow and Go to artist. Subscribed to the artist table only.</summary>
    sealed class HeaderBand : Component
    {
        readonly Action _go;
        readonly Func<ColorF> _accent;
        StageCtx? _ctx;
        Action<string>? _begin;
        int _artist;

        public HeaderBand()
        {
            _go = () => GoToArtist(_begin, _artist);
            _accent = () => _ctx is { } c ? c.Accent.Value.Fill : Tok.AccentDefault;   // read by FollowToggle's own render
        }

        public override Element Render()
        {
            _ctx = UseContext(StageContext)!;
            _begin = UseContext(SharedTransition.Begin);
            var p = UseProps<HeaderProps>();
            _ = Entities.ScopeEpoch.Value;
            _ = Entities.Current.Artists.Changed.Value;
            var set = _ctx.Accent.Value;
            var a = new Artist(p.Artist);
            _artist = p.Artist;
            if (!a.IsValid) return NoSection();
            float s = p.S;

            // name + the verified badge on one line; the name ellipsises, the badge never shrinks
            var nameRow = new List<Element>(2)
            {
                new TextEl(a.Name)
                {
                    Size = Px(48f, s), LineHeight = Px(56f, s), Weight = 700, FontFamily = DisplayFace, Color = Ink.Ink,
                    Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
                },
            };
            if (a.IsVerified)
                nameRow.Add(new BoxEl { Shrink = 0f, Children = [Controls.Named(InfoBadge.Icon(Icons.Accept, color: set.Fill), Loc.Get(Strings.Artist.Verified))] });

            var lines = new List<Element>(3)
            {
                new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Px(Spacing.M, s), MinWidth = 0f, Children = nameRow.ToArray() },
            };
            string listeners = a.MonthlyListeners > 0 ? Strings.Stage.Artist.ListenersShort(ArtistRules.ListenerCount(a.MonthlyListeners)) : "";
            string rank = a.WorldRank > 0 ? Strings.Stage.Artist.WorldRank(FormatCache.Int(a.WorldRank)) : "";
            string meta = ArtistRules.MetaLine(listeners, rank);
            if (meta.Length > 0)
                lines.Add(new TextEl(meta) { Size = Px(18f, s), LineHeight = Px(26f, s), Color = Ink.InkSecondary, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f });
            if (!a.TourHeadlineId.IsEmpty)
            {
                string eyebrow = Entities.Strings.Resolve(a.TourEyebrowId), headline = Entities.Strings.Resolve(a.TourHeadlineId);
                var tour = new List<Element>(2);
                if (eyebrow.Length > 0)
                    tour.Add(Design.Type.Eyebrow(eyebrow) with { Size = Px(12f, s), Color = set.Text, MaxLines = 1, Shrink = 0f });
                tour.Add(new TextEl(headline) { Size = Px(14f, s), LineHeight = Px(20f, s), Color = Ink.InkSecondary, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f });
                lines.Add(new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Px(Spacing.S, s), MinWidth = 0f, Children = tour.ToArray() });
            }

            string uri = a.Uri.Text, name = a.Name;
            var actions = new List<Element>(2);
            if (uri.Length > 0)   // keyed on the uri: FollowToggle carries it as a mount-frozen field
                actions.Add(Embed.Comp(() => new Controls.FollowToggle { Uri = uri, Name = name, Accent = _accent }) with { Key = "stage-follow:" + uri });
            actions.Add(Button.Create(Loc.Get(Strings.Stage.GoToArtist), _go, ButtonAppearance.Standard, ControlSize.Medium));

            float pad = Px(Spacing.XXL, s);
            return new BoxEl
            {
                Height = p.H, Corners = Radii.CardAll, ClipToBounds = true, ZStack = true, Fill = Ink.Plate, BorderWidth = 1f, BorderColor = Ink.Stroke,
                Children =
                [
                    // Controls.Artwork returns Element (no Align/Justify): a stretched BoxEl hosts it (V-U6). Laid out at the
                    // pane's own width × the band's height and cover-fit, so the photo fills the band edge to edge.
                    new BoxEl { AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
                                Children = [Controls.Artwork(Controls.ArtUrl(a.HeroImageId), p.W, p.H, 0f)] },
                    // the stage veil rising from the bottom (Veil is the ink's opposite on either arm)
                    new BoxEl
                    {
                        AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
                        Gradient = GradientDown(new GradientStop(0f, Shade(0f)), new GradientStop(0.35f, Shade(0.12f)), new GradientStop(1f, Shade(0.9f))),
                    },
                    new BoxEl
                    {
                        AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Stretch, Direction = 0, AlignItems = FlexAlign.End, Gap = Px(Spacing.XL, s),
                        Padding = new Edges4(pad, 0f, pad, pad),
                        Children =
                        [
                            new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = Px(Spacing.XS, s), Children = lines.ToArray() },
                            new BoxEl { Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = Spacing.S, Children = actions.ToArray() },
                        ],
                    },
                ],
            };
        }
    }

    // ══ 4. ABOUT ════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>"About": the WHOLE biography (its links route through the stage's close-then-navigate) at 18/28 × scale,
    /// held to ~68 characters, clamped to six lines behind Read more / Show less when it overflows; then up to four gallery
    /// photos. The open state is the artist it was opened FOR, so the next artist starts clamped with no reset write.</summary>
    sealed class AboutBlock : Component
    {
        readonly Signal<int> _openFor = new(0);
        readonly Action _toggle;
        readonly Action<string> _nav;
        Action<string>? _begin;
        int _artist;
        StringId _bioKey;
        int _bioChars;

        public AboutBlock()
        {
            _toggle = () => _openFor.Value = _openFor.Peek() == _artist ? 0 : _artist;
            _nav = key => { Close(_begin, "navigate"); Shell.GoTo(Shell.Parse(key)); };
        }

        public override Element Render()
        {
            var ctx = UseContext(StageContext)!;
            _begin = UseContext(SharedTransition.Begin);
            var p = UseProps<AboutProps>();
            _ = Entities.ScopeEpoch.Value;
            _ = Entities.Current.Artists.Changed.Value;
            _ = Entities.Current.Edges.ArtistGallery.Changed.Value;
            var set = ctx.Accent.Value;
            var a = new Artist(p.Artist);
            _artist = p.Artist;
            if (!a.IsValid) return NoSection();
            StringId bioId = a.BioId.IsEmpty ? a.BioLeadId : a.BioId;
            string bio = Entities.Strings.Resolve(bioId);
            var gallery = a.GallerySlots;
            if (bio.Length == 0 && gallery.Length == 0) return NoSection();

            float s = p.S, size = Px(BioSize, s), line = Px(BioLine, s);
            float textW = MathF.Min(p.W, p.Measure);
            if (!bioId.Equals(_bioKey)) { _bioKey = bioId; _bioChars = HomeCardText.PlainText(bio)?.Length ?? 0; }   // once per bio, not per render
            bool clamps = ArtistRules.BioClamps(_bioChars, textW, size, ArtistRules.BioLines);
            bool open = _openFor.Value == p.Artist;

            var kids = new List<Element>(4) { SectionTitle(Loc.Get(Strings.Stage.Artist.About), s) };
            if (bio.Length > 0)
            {
                Element text = Controls.RichText(bio, size, Ink.Ink, set.Text, textW, clamps && !open ? ArtistRules.BioLines : 0, _nav);
                kids.Add(text is SpanTextEl span ? span with { LineHeight = line } : text);
                if (clamps)
                    kids.Add(new BoxEl
                    {
                        AlignSelf = FlexAlign.Start, Padding = new Edges4(0f, Spacing.XXS, 0f, Spacing.XXS),
                        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand, OnClick = _toggle,
                        Children = [new TextEl(Loc.Get(open ? Strings.Stage.Artist.ShowLess : Strings.Stage.Artist.ReadMore)) { Size = Px(14f, s), LineHeight = Px(20f, s), Weight = 600, Color = set.Text, HoverColor = Ink.Ink }],
                    });
            }
            int n = Math.Min(gallery.Length, ArtistRules.GalleryCount);
            if (n > 0)
            {
                float gap = Px(Spacing.S, s), tile = ArtistRules.TileW(p.W, gap, n);
                var tiles = new Element[n];
                for (int i = 0; i < n; i++)
                    tiles[i] = new BoxEl { Shrink = 0f, HitTestVisible = false, Children = [Controls.Artwork(Controls.ArtUrl(gallery[i]), tile, tile, Radii.Card)] };
                kids.Add(new BoxEl { Direction = 0, Gap = gap, Margin = new Edges4(0f, Px(Spacing.S, s), 0f, 0f), Children = tiles });
            }
            return new BoxEl { Direction = 1, MinWidth = 0f, Gap = Px(Spacing.M, s), Padding = new Edges4(0f, 0f, 0f, Px(Spacing.XXL, s)), Children = kids.ToArray() };
        }
    }

    // ══ 5. THIS SONG ════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>"This song": the track's credits (Edges.TrackCredits, the drawer's source) GROUPED BY PERSON — "Joey Tempest
    /// · Composer, Lyricist" — each name a link when it resolves to an artist (the credit's own target, else a billed artist
    /// of that name). Absent until the credits answer.</summary>
    sealed class SongCredits : Component
    {
        Action<string>? _begin;

        public override Element Render()
        {
            _begin = UseContext(SharedTransition.Begin);
            var p = UseProps<SongProps>();
            _ = Entities.ScopeEpoch.Value;
            var e = Entities.Current.Edges;
            _ = e.TrackCredits.Changed.Value;
            _ = e.TrackArtists.Changed.Value;
            _ = Entities.Current.Artists.Changed.Value;   // the billed names land after the credits
            var t = new Track(p.Track);
            if (!t.IsValid) return NoSection();
            var payload = e.TrackCredits.Payload(t.Slot);
            if (payload.Length == 0) return NoSection();
            var targets = e.TrackCredits.Targets(t.Slot);

            var names = new string[payload.Length];
            var roles = new string[payload.Length];
            for (int i = 0; i < payload.Length; i++)
            {
                names[i] = Entities.Strings.Resolve(payload[i].Name);
                roles[i] = Entities.Strings.Resolve(payload[i].Role);
            }
            var billed = t.ArtistSlots;
            var billedNames = new string[billed.Length];
            for (int i = 0; i < billed.Length; i++) billedNames[i] = billed[i] > 0 ? new Artist(billed[i]).Name : "";
            var people = ArtistRules.GroupCredits(names, roles, targets, billedNames, billed, ArtistRules.CreditPeopleMax);
            if (people.Length == 0) return NoSection();

            float s = p.S;
            var rows = new Element[people.Length];
            for (int i = 0; i < people.Length; i++)
            {
                var person = people[i];
                int slot = person.ArtistSlot;
                bool link = slot > 0;
                var parts = new List<Element>(3)
                {
                    new TextEl(person.Name) { Size = Px(15f, s), LineHeight = Px(22f, s), Weight = 600, Color = Ink.Ink, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f },
                };
                if (person.Roles.Length > 0)
                {
                    parts.Add(new TextEl("·") { Size = Px(15f, s), LineHeight = Px(22f, s), Color = Ink.InkTertiary, Shrink = 0f });
                    parts.Add(new TextEl(Strings.Stage.Artist.Roles(person.Roles)) { Size = Px(15f, s), LineHeight = Px(22f, s), Color = Ink.InkSecondary, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Grow = 1f, Basis = 0f, Shrink = 1f });
                }
                rows[i] = new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = Px(Spacing.S, s), MinWidth = 0f, MinHeight = Px(36f, s),
                    Padding = new Edges4(Px(Spacing.S, s), 0f, Px(Spacing.S, s), 0f), Corners = Radii.ControlAll,
                    Fill = Ink.GlassRest, HoverFill = link ? Ink.GlassHover : Ink.GlassRest, PressedFill = link ? Ink.GlassPressed : Ink.GlassRest,
                    BrushTransitionMs = Design.Motion.Faster,
                    Role = link ? AutomationRole.Hyperlink : AutomationRole.Text, Focusable = link, AllowFocusOnInteraction = false,
                    Cursor = link ? CursorId.Hand : (CursorId?)null, OnClick = link ? () => GoToArtist(_begin, slot) : null,
                    Children = parts.ToArray(),
                };
            }
            return PlateSection(Loc.Get(Strings.Stage.Artist.ThisSong), s, new BoxEl { Direction = 1, MinWidth = 0f, Gap = Spacing.XXS, Children = rows });
        }
    }

    // ══ 6. POPULAR ══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>"Popular": the artist's top five, the playing one on the plate in accent text with the speaker glyph. A
    /// click plays the chart FROM that row with the chart's own rows as the queue (Artist.UI.Chart StartAt — never
    /// PlayContext(artist, track), which the server resolves to a different list); the deck row toggles (Track.Invoke).
    /// The rows' text is asked once per popular-edge version.</summary>
    sealed class PopularList : Component
    {
        public override Element Render()
        {
            var ctx = UseContext(StageContext)!;
            var p = UseProps<ArtistProps>();
            _ = Entities.ScopeEpoch.Value;
            uint epoch = Entities.ScopeEpoch.Peek();
            var scope = Entities.Current;
            _ = scope.Edges.ArtistPopular.Changed.Value;
            _ = scope.Tracks.Changed.Value;
            int artistSlot = p.Artist;
            uint version = scope.Edges.ArtistPopular.Version(artistSlot);
            UseEffect(() => EnsureTop(artistSlot), DepKey.From(artistSlot, (int)version, (int)epoch, 0));
            var set = ctx.Accent.Value;
            var a = new Artist(artistSlot);
            if (!a.IsValid) return NoSection();
            var slots = a.PopularSlots;
            int n = Math.Min(slots.Length, ArtistRules.PopularCount);
            if (n == 0) return NoSection();

            float s = p.S;
            bool art = !Prefs.Appearance.TrackArtworkHidden();
            var rows = new List<Element>(n);
            for (int i = 0; i < n; i++)
            {
                var t = new Track(slots[i]);
                if (!t.IsValid || !t.Knows(TrackFields.Title)) continue;   // a thin row waits for its text, never a bare uri
                rows.Add(Row(t, i, artistSlot, art, set, s));
            }
            if (rows.Count == 0) return NoSection();
            return PlateSection(Loc.Get(Strings.Stage.Artist.Popular), s, new BoxEl { Direction = 1, MinWidth = 0f, Gap = Spacing.XXS, Children = rows.ToArray() });
        }

        static Element Row(Track t, int index, int artistSlot, bool art, AccentSet set, float s)
        {
            bool now = Track.IsNowPlaying(t);                       // subscribes Playback.CurrentId
            bool playing = now && Playback.IsPlaying.Value;
            var kids = new List<Element>(4)
            {
                new BoxEl
                {
                    Width = Px(24f, s), Shrink = 0f, Direction = 0, Justify = FlexJustify.End, AlignItems = FlexAlign.Center, HitTestVisible = false,
                    Children = [playing
                        ? new TextEl(Icons.Volume) { Size = Px(14f, s), FontFamily = Theme.IconFont, Color = set.Text }
                        : new TextEl(FormatCache.Int(index + 1)) { Size = Px(14f, s), LineHeight = Px(20f, s), Color = now ? set.Text : Ink.InkTertiary, Wrap = TextWrap.NoWrap }],
                },
            };
            if (art)
            {
                StringId image = t.ImageId.IsEmpty ? t.Album.ImageId : t.ImageId;   // a thin chart hit may carry no cover of its own
                kids.Add(new BoxEl { Shrink = 0f, HitTestVisible = false, Children = [Controls.Artwork(Controls.ArtUrl(image), Px(40f, s), Px(40f, s), Radii.Control)] });
            }
            Element title = new TextEl(t.Title)
            {
                Size = Px(15f, s), LineHeight = Px(22f, s), Weight = 600, Color = now ? set.Text : Ink.Ink,
                Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
            };
            uint plays = t.PlayCount;
            var text = new List<Element>(2)
            {
                t.IsExplicit
                    ? new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS, MinWidth = 0f,
                                  Children = [title, Controls.ExplicitBadge(Px(14f, s), Ink.InkTertiary)] }
                    : title,
            };
            if (plays > 0)
                text.Add(new TextEl(Track.Format.PlaysLabel(plays)) { Size = Px(12f, s), LineHeight = Px(16f, s), Color = Ink.InkTertiary, Wrap = TextWrap.NoWrap, MaxLines = 1 });
            kids.Add(new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Justify = FlexJustify.Center, Gap = 1f, HitTestVisible = false, Children = text.ToArray() });
            kids.Add(new TextEl(t.DurationMs > 0 ? Playback.TimeFormat.Clock(t.DurationMs) : "") { Size = Px(13f, s), LineHeight = Px(18f, s), Color = Ink.InkTertiary, Wrap = TextWrap.NoWrap, Shrink = 0f });

            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Px(Spacing.M, s), MinWidth = 0f, Height = Px(56f, s),
                Padding = new Edges4(Px(Spacing.S, s), 0f, Px(Spacing.M, s), 0f), Corners = Radii.ControlAll,
                Fill = now ? Ink.Plate : Ink.GlassRest, HoverFill = now ? Ink.PlateHover : Ink.GlassHover, PressedFill = Ink.GlassPressed,
                BorderWidth = now ? 1f : 0f, BorderColor = Ink.Stroke, BrushTransitionMs = Design.Motion.Faster,
                PressScale = Design.Motion.ScaleSubtle.Press,
                Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true, AllowFocusOnInteraction = false,
                OnClick = () => Track.Invoke(t, () => PlayFrom(artistSlot, index)),
                Children = kids.ToArray(),
            };
        }

        static void EnsureTop(int artistSlot)
        {
            var a = new Artist(artistSlot);
            if (!a.IsValid) return;
            var slots = a.PopularSlots;
            int n = Math.Min(slots.Length, ArtistRules.PopularCount);
            if (n > 0) Entities.Ensure(MemoryMarshal.Cast<int, Track>(slots[..n]), TrackFields.Row);
        }

        /// <summary>Play the chart from <paramref name="index"/>, the chart's own rows as the queue (Artist.UI.Chart StartAt).
        /// The slots are re-read at CLICK time: the edge may have been re-committed since the render.</summary>
        static void PlayFrom(int artistSlot, int index)
        {
            var a = new Artist(artistSlot);
            if (!a.IsValid) return;
            var slots = a.PopularSlots;
            if ((uint)index >= (uint)slots.Length) return;
            var refs = ArrayPool<EntityRef>.Shared.Rent(slots.Length);
            try
            {
                for (int i = 0; i < slots.Length; i++) refs[i] = new EntityRef(EntityKind.Track, slots[i]);
                Playback.PlayRows(refs.AsSpan(0, slots.Length), index, a.Id);
            }
            finally { ArrayPool<EntityRef>.Shared.Return(refs); }
        }
    }

    // ══ 7. WHERE PEOPLE LISTEN · FANS ALSO LIKE ═════════════════════════════════════════════════════════════════════

    /// <summary>"Where people listen": the top five cities, each a name · compact count over a 4-DIP accent bar
    /// proportional to the loudest city (the artist page's CityBars, in the stage's ink).</summary>
    sealed class CityList : Component
    {
        public override Element Render()
        {
            var ctx = UseContext(StageContext)!;
            var p = UseProps<ArtistProps>();
            _ = Entities.ScopeEpoch.Value;
            _ = Entities.Current.Edges.ArtistCities.Changed.Value;
            var set = ctx.Accent.Value;
            var a = new Artist(p.Artist);
            if (!a.IsValid) return NoSection();
            var cities = a.TopCities;
            int n = Math.Min(cities.Length, ArtistRules.CityCount);
            if (n == 0) return NoSection();
            uint max = 0;
            for (int i = 0; i < n; i++) if (cities[i].Listeners > max) max = cities[i].Listeners;

            float s = p.S, bar = Px(4f, s);
            var rows = new Element[n];
            for (int i = 0; i < n; i++)
            {
                float frac = ArtistRules.CityFraction(cities[i].Listeners, max);
                rows[i] = new BoxEl
                {
                    Direction = 1, Gap = Px(Spacing.XS, s), MinWidth = 0f,
                    Children =
                    [
                        new BoxEl
                        {
                            Direction = 0, AlignItems = FlexAlign.Center, Gap = Px(Spacing.S, s), MinWidth = 0f,
                            Children =
                            [
                                new TextEl(Entities.Strings.Resolve(cities[i].City)) { Size = Px(15f, s), LineHeight = Px(22f, s), Color = Ink.Ink, Grow = 1f, Basis = 0f, MinWidth = 0f, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                                new TextEl(ArtistRules.ListenerCount(cities[i].Listeners)) { Size = Px(13f, s), LineHeight = Px(18f, s), Color = Ink.InkSecondary, Wrap = TextWrap.NoWrap, Shrink = 0f },
                            ],
                        },
                        // the bar's ground is the plate rung, its fill the accent; widths by flex share, never a scale
                        new BoxEl
                        {
                            Direction = 0, Height = bar, Corners = CornerRadius4.All(bar * 0.5f), ClipToBounds = true, Fill = Ink.Plate,
                            Children =
                            [
                                new BoxEl { Grow = MathF.Max(0.001f, frac), Basis = 0f, Height = bar, Corners = CornerRadius4.All(bar * 0.5f), Fill = set.Fill },
                                new BoxEl { Grow = MathF.Max(0.001f, 1f - frac), Basis = 0f, Height = bar },
                            ],
                        },
                    ],
                };
            }
            return PlateSection(Loc.Get(Strings.Stage.Artist.TopCities), s, new BoxEl { Direction = 1, MinWidth = 0f, Gap = Px(Spacing.M, s), Children = rows });
        }
    }

    /// <summary>"Fans also like": as many related artists as fit the column (≤ 8), each a round avatar over its name; a click
    /// opens the artist (closing the stage). Their identities are asked once per related-edge version.</summary>
    sealed class FansRow : Component
    {
        Action<string>? _begin;

        public override Element Render()
        {
            _begin = UseContext(SharedTransition.Begin);
            var p = UseProps<FansProps>();
            _ = Entities.ScopeEpoch.Value;
            uint epoch = Entities.ScopeEpoch.Peek();
            var scope = Entities.Current;
            _ = scope.Edges.ArtistRelated.Changed.Value;
            _ = scope.Artists.Changed.Value;
            int artistSlot = p.Artist;
            uint version = scope.Edges.ArtistRelated.Version(artistSlot);
            UseEffect(() => EnsureRelated(artistSlot), DepKey.From(artistSlot, (int)version, (int)epoch, 0));
            var a = new Artist(artistSlot);
            if (!a.IsValid) return NoSection();
            var related = a.RelatedSlots;

            float s = p.S, avatar = Px(72f, s), gap = Px(Spacing.M, s);
            float inner = p.W - 2f * Px(Spacing.L, s) - 2f;     // the plate's padding and hairline
            int n = ArtistRules.FitCount(inner, avatar, gap, related.Length, ArtistRules.FansMax);
            if (n == 0) return NoSection();
            var tiles = new List<Element>(n);
            for (int i = 0; i < related.Length && tiles.Count < n; i++)
            {
                var r = new Artist(related[i]);
                if (!r.IsValid || r.Name.Length == 0) continue;
                int slot = r.Slot;
                tiles.Add(new BoxEl
                {
                    Direction = 1, Width = avatar, Shrink = 0f, Gap = Px(Spacing.XS, s), AlignItems = FlexAlign.Center,
                    Role = AutomationRole.Hyperlink, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand,
                    PressScale = Design.Motion.ScaleSubtle.Press, OnClick = () => GoToArtist(_begin, slot),
                    Children =
                    [
                        Controls.Artwork(Controls.ArtUrl(r.ImageId), avatar, avatar, avatar * 0.5f),
                        new TextEl(r.Name) { Size = Px(12f, s), LineHeight = Px(16f, s), Color = Ink.InkSecondary, HoverColor = Ink.Ink, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MaxWidth = avatar, MinWidth = 0f },
                    ],
                });
            }
            if (tiles.Count == 0) return NoSection();
            return PlateSection(Loc.Get(Strings.Stage.Artist.FansAlsoLike), s, new BoxEl { Direction = 0, Gap = gap, MinWidth = 0f, Children = tiles.ToArray() });
        }

        static void EnsureRelated(int artistSlot)
        {
            var a = new Artist(artistSlot);
            if (!a.IsValid) return;
            var related = a.RelatedSlots;
            int n = Math.Min(related.Length, ArtistRules.FansMax);
            if (n > 0) Entities.Ensure(MemoryMarshal.Cast<int, Artist>(related[..n]), ArtistFields.Identity);
        }
    }
}
