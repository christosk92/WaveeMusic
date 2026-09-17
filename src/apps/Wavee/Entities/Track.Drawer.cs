// ── Entities/Track.Drawer.cs ───────────────────────────────────────────────────────────────────────────────────────
// the expanded-row drawer BODY: the facts strip, "Versions and formats" (the connector rail, the self row, the reserved
// music-video row, alternate audio), the waveform, the format split button + its radio ladder, and the View-credits sheet
//
// Role: UI (+ the pure `DrawerRules` section, pinned by Wavee.Tests/TrackDrawerRulesTests.cs)
// Owner: M
// Wave: 5
// Budget: 700 lines
// Spec: ch 01 W14, W15, W26, W27, §3 drawer rows, §7 DATA GAPS 1/13/14, §9 trap 8 · ch 04 §5 drawer motion, items 49-52,
//       68 · ch 05 W14 · 0.2.9 `Components/{TrackVersionsPanel, TrackFactsStrip, FormatSplitButton}.cs`,
//       `Actions/TrackCreditsDialog.cs`
//
// WHO MOUNTS WHAT. The TABLE owns the drawer's mount, keying ("drawer-body:" + rowKey), clip, zebra parity, reflow and
// indent (`Track.Table.cs` DrawerBox). This file owns only the BODY a page hands the table through `TableProfile.Drawer`
// (`DrawerSeam` for the album page): ONE component re-pushed a data-only props record, subscribed to exactly the tables it
// paints (the track, its album, the adder, the tags/versions/waveform relations, the deck's current item).
//
// THE DEMAND (on mount, per track, per scope): TrackFields.Audio | Tags | Isrc | Files | Video, the TrackVersions /
// TrackWaveform / TrackCredits edges, and — as versions land — Identity | Audio | Files over the version targets.
//
// THE RESERVED VIDEO ROW (ch 01 §9 trap 8): a track the catalogue says HAS a video reserves the 76×43 row under the key
// "v:video" BEFORE the versions relation answers, so the reflow solves its final height on the first frame and the real
// row PATCHES into the reserved node (`DrawerRules.VideoRowFor`; a failed relation reserves nothing, W26).
//
// THE FORMAT OVERRIDE (ch 01 DATA GAP 14): the radio ladder reads/writes `Playback.FormatOverrideFor` /
// `Playback.SetFormatOverride` — a persisted per-uri map the PLAYBACK host owns (shared-file patch). Its wire codec and the
// format → rung map are `DrawerRules` here, so they are pure and tested.

using System.Globalization;
using System.Runtime.InteropServices;
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

public readonly partial struct Track
{
    // ══ 1. THE PUBLIC SURFACE ════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>What a page knows about the ROW that the track does not carry. Build it with NAMED arguments — a record
    /// struct's <c>default</c> / <c>new()</c> skips the parameter defaults (it would hide the album).</summary>
    public readonly record struct DrawerOptions(int AddedAt = 0, User AddedBy = default, bool ShowAlbum = true);

    /// <summary>The expanded-row drawer BODY: the facts strip, then "Versions and formats" (ch 01 W14).</summary>
    public static Element Drawer(Track t, in DrawerOptions o)
        => Embed.Comp(new DrawerProps(t, o), static () => new DrawerHost());

    /// <summary>The <c>TableProfile.Drawer</c> seam for a surface with no membership metadata (the album page). The album
    /// fact stays ON: 0.2.9's album drawer states it (ch 05 W14 "prose facts (Album, ISRC…)").</summary>
    public static readonly Func<Track, int, Element?> DrawerSeam =
        static (t, _) => Drawer(t, new DrawerOptions(AddedAt: 0, AddedBy: default, ShowAlbum: true));

    /// <summary>The overlay host the format ladder and the credits sheet open on; null ⇒ both are inert, never a crash.</summary>
    public static IOverlayService? DrawerOverlay { get; set; }

    /// <summary>The "View credits" sheet (ch 01 W27). A no-op without an overlay host.</summary>
    public static void OpenCredits(Track t)
    {
        var overlay = DrawerOverlay;
        if (Controls.IsNullOverlay(overlay) || !t.IsValid) return;
        Controls.Sheet(overlay, t.Knows(TrackFields.Title) ? t.Title : "",
            Embed.Comp(new CreditsProps(t), static () => new CreditsHost()));
    }

    // ══ 2. THE DRAWER HOST ═══════════════════════════════════════════════════════════════════════════════════════════

    sealed record DrawerProps(Track Track, DrawerOptions Options);

    const int MaxVersionRows = 16;
    const float StatGap = Spacing.XL;
    static readonly EnterExit s_fadeUp = new(Opacity: 0f, Active: true);
    static readonly LayoutTransition s_shove = new(TransitionChannels.Position,
        TransitionDynamics.Tween(Expressive.Fast, Easing.SmoothOut));

    // Stable per-KIND fact keys in FactKind order (a changed value cross-swaps in place; a NEW fact fades up).
    static readonly string[] s_factKeys =
    [
        "fact:plays", "fact:bpm", "fact:key", "fact:added", "fact:duration", "fact:album", "fact:released",
        "fact:addedby", "fact:isrc", "fact:descriptors", "fact:explicit", "fact:video", "fact:local", "fact:unavailable",
    ];

    sealed class DrawerHost : Component
    {
        DrawerProps? _latest;
        float[]? _peaks;
        int _peaksParent = -1;
        uint _peaksVersion, _peaksEpoch;
        readonly Action _demand, _demandVersions;

        public DrawerHost() { _demand = Demand; _demandVersions = DemandVersions; }

        public override Element Render()
        {
            var p = UseProps<DrawerProps>();
            _latest = p;
            uint epoch = Entities.ScopeEpoch.Value;        // FIRST: a scope switch re-points every table below
            var scope = Entities.Current;
            var edges = scope.Edges;
            _ = scope.Tracks.Changed.Value;
            _ = scope.Albums.Changed.Value;
            _ = scope.Users.Changed.Value;
            _ = edges.TrackTags.Changed.Value;
            _ = edges.TrackVersions.Changed.Value;
            _ = edges.TrackWaveform.Changed.Value;
            var playing = Playback.CurrentId.Value;          // a version row that IS the deck's item re-skins
            var t = p.Track;
            UseEffect(_demand, DepKey.From(t.Slot, (int)epoch));
            UseEffect(_demandVersions);
            if (!t.IsValid) return new BoxEl();

            // Flat, in order: the GUARANTEED self row, the music video (reserved before the relation answers), then every
            // alternate audio — no group headings; the kind label leads each meta line instead (ch 01 W14).
            var entries = new List<(byte Kind, Track Version)>(4) { (DrawerRules.SelfRow, t) };
            var targets = edges.TrackVersions.Targets(t.Slot);
            var kinds = edges.TrackVersions.Payload(t.Slot);
            Span<int> order = stackalloc int[Math.Min(kinds.Length, MaxVersionRows)];
            int ordered = DrawerRules.VersionOrder(kinds, order);
            bool edgeVideo = ordered > 0 && kinds[order[0]].Kind == TrackVersionKind.Video;
            switch (DrawerRules.VideoRowFor(t.HasVideo, edges.TrackVersions.Readiness(t.Slot), edgeVideo,
                                            t.VideoCounterpart.IsValid))
            {
                case DrawerRules.VideoRow.Edge: entries.Add((DrawerRules.VideoKind, new Track(targets[order[0]]))); break;
                case DrawerRules.VideoRow.Counterpart: entries.Add((DrawerRules.VideoKind, t.VideoCounterpart)); break;
                case DrawerRules.VideoRow.Reserved: entries.Add((DrawerRules.VideoKind, default)); break;
            }
            for (int i = edgeVideo ? 1 : 0; i < ordered; i++) entries.Add((DrawerRules.AudioKind, new Track(targets[order[i]])));

            var body = new Element[2 + entries.Count];
            body[0] = FactsStrip(FactsFor(t, p.Options));
            // A whole step of top air, not a hairline: the eyebrow heads the ROWS, not the flat strip above it.
            body[1] = Design.Type.Eyebrow(Loc.Get(Strings.Detail.Versions.VersionsAndFormats)) with
            {
                Key = "versions-head", Color = Tok.TextTertiary, Margin = new Edges4(0f, Spacing.M, 0f, 2f),
            };
            float[]? peaks = PeaksFor(edges, t.Slot, epoch);
            for (int i = 0; i < entries.Count; i++)
            {
                var (kind, version) = entries[i];
                bool reserved = kind == DrawerRules.VideoKind && !version.IsValid;
                string rowKey = kind == DrawerRules.VideoKind ? DrawerRules.VideoRowKey : version.Uri.Text;
                bool isNow = !reserved && !playing.IsEmpty && playing == version.Id;
                Element row = reserved ? PendingVersionRow() : VersionRow(version, t, kind, rowKey, isNow, i == 0 ? peaks : null);
                body[2 + i] = ConnectedRow(rowKey, row, isLast: i == entries.Count - 1);
            }
            return new BoxEl { Direction = 1, MinWidth = 0f, Children = body };
        }

        void Demand()
        {
            if (_latest is not { Track.IsValid: true } p) return;
            var t = p.Track;
            Entities.Ensure(t, TrackFields.Audio | TrackFields.Tags | TrackFields.Isrc | TrackFields.Files | TrackFields.Video);
            Entities.EnsureEdge(FetchEdge.TrackVersions, t.Slot);
            Entities.EnsureEdge(FetchEdge.TrackWaveform, t.Slot);
            Entities.EnsureEdge(FetchEdge.TrackCredits, t.Slot);   // the View-credits sheet opens warm
        }

        // Auto-tracked: as the versions relation (or the kind-99 counterpart) lands, ask for what a version row paints.
        void DemandVersions()
        {
            _ = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            _ = scope.Edges.TrackVersions.Changed.Value;
            _ = scope.Tracks.Changed.Value;
            if (_latest is not { Track.IsValid: true } p) return;
            var targets = scope.Edges.TrackVersions.Targets(p.Track.Slot);
            if (targets.Length > 0)
                Entities.Ensure(MemoryMarshal.Cast<int, Track>(targets), TrackFields.Identity | TrackFields.Audio | TrackFields.Files);
            var counterpart = p.Track.VideoCounterpart;
            if (counterpart.IsValid) Entities.Ensure(counterpart, TrackFields.Identity);
        }

        // The waveform's 0..1 peaks, re-derived only when the relation's version moves (never per render).
        float[]? PeaksFor(Edges edges, int parent, uint epoch)
        {
            if (edges.TrackWaveform.State(parent) == EdgeState.Unknown) return null;
            uint version = edges.TrackWaveform.Version(parent);
            if (_peaks is null || _peaksParent != parent || _peaksVersion != version || _peaksEpoch != epoch)
            {
                var magnitudes = edges.TrackWaveform.Payload(parent);
                _peaks = new float[magnitudes.Length];
                DrawerRules.Peaks(magnitudes, _peaks);
                (_peaksParent, _peaksVersion, _peaksEpoch) = (parent, version, epoch);
            }
            return _peaks.Length == 0 ? null : _peaks;
        }
    }

    // ══ 3. THE FACTS STRIP (ch 01 W14) ═══════════════════════════════════════════════════════════════════════════════

    // The enrichment dash appears only while THIS drawer's own asks (kind 222 via Audio, kind 185 via PlayCount) are
    // unanswered; a known zero states nothing.
    static IReadOnlyList<Fact> FactsFor(Track t, in DrawerOptions o)
    {
        var input = FactsInput.Of(t, o.AddedAt, Store.ToUnix(Entities.Now));
        if (!o.ShowAlbum) input = input with { AlbumName = null, AlbumUri = default };
        var by = o.AddedBy;
        string? byName = by.IsValid && by.Knows(UserFields.Identity) && !by.NameId.IsEmpty ? Entities.Strings.Resolve(by.NameId) : null;
        if (by.IsValid && byName is null) input = input with { AddedByRaw = EntityUri.IdOf(by.Uri.Text).ToString() };
        return Facts.For(in input, new FactsOptions(
            TempoPending: !t.Knows(TrackFields.Audio), PlaysPending: !t.Knows(TrackFields.PlayCount), HasVideo: t.HasVideo,
            AddedByName: byName, AddedByProfile: byName is null ? default : by,
            Culture: CultureInfo.CurrentCulture, Zone: TimeZoneInfo.Local,
            MajorWord: Loc.Get(Strings.Detail.TrackFacts.Major), MinorWord: Loc.Get(Strings.Detail.TrackFacts.Minor)));
    }

    // Three lines of flat typography and no chrome: the hero figures, the middot prose line, the genre/flag line. A track
    // that states nothing yields a bare box (W26).
    static Element FactsStrip(IReadOnlyList<Fact> facts)
    {
        if (facts.Count == 0) return new BoxEl { Key = "facts" };
        var heroes = new List<Element>(4);
        var prose = new List<Element>(10);
        var genres = new List<Element>(10);
        for (int i = 0; i < facts.Count; i++)
        {
            var f = facts[i];
            if (f.Form == FactForm.Chips && f.Chips is { Count: > 0 } tags)
                for (int k = 0; k < tags.Count; k++) Join(genres, GenreRun(tags[k]));
            else if (f.Form == FactForm.Flag) Join(genres, FlagRun(f.Kind));
            else if (f.Form == FactForm.Chips) continue;
            else if (Facts.IsHeroFact(f.Kind)) heroes.Add(Stat(in f));
            else Join(prose, ProseRun(in f));
        }
        var lines = new List<Element>(3);
        if (heroes.Count > 0)
            lines.Add(new BoxEl
            {
                Key = "facts-hero", Direction = 0, Wrap = true, Gap = StatGap, MinWidth = 0f,
                Stagger = Design.Reduced ? 0f : Design.Motion.MastheadStaggerMs,   // reduced motion is a VALUE
                Children = heroes.ToArray(),
            });
        if (prose.Count > 0) lines.Add(FactLine("facts-prose", prose));
        if (genres.Count > 0) lines.Add(FactLine("facts-genres", genres));
        return new BoxEl
        {
            Key = "facts", Direction = 1, Gap = Spacing.M, MinWidth = 0f, Padding = new Edges4(0f, Spacing.XS, 0f, Spacing.S),
            Children = lines.ToArray(),
        };
    }

    // A whole line fades and FLIPs as one — a middot sentence staggered word by word reads as a teleprompter.
    static BoxEl FactLine(string key, List<Element> runs) => new()
    {
        Key = key, Enter = s_fadeUp, Layout = s_shove, Direction = 0, Wrap = true, AlignItems = FlexAlign.Center,
        Gap = Spacing.XS, MinWidth = 0f, Children = runs.ToArray(),
    };

    // One hero figure over its caption, keyed by KIND. A pending fact is its own dash at 0.6.
    static Element Stat(in Fact f)
    {
        bool pending = f.Form == FactForm.Pending;
        var split = pending ? new FactSplit(f.Value, null) : Facts.HeroSplit(in f);
        return new BoxEl
        {
            Key = s_factKeys[(int)f.Kind], Enter = s_fadeUp, Layout = s_shove, Direction = 1, Gap = 0f, Shrink = 0f,
            MinWidth = 0f, Margin = new Edges4(0f, 0f, StatGap, 0f), Opacity = pending ? 0.6f : 1f,
            Children =
            [
                Design.Type.StatHero(split.Value, split.Unit),
                Caption(Loc.Get(Facts.LabelKey(f.Kind))) with
                {
                    Weight = 600, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                },
            ],
        };
    }

    static Element ProseRun(in Fact f)
    {
        if (f.Form == FactForm.Link) return LinkValue(f.Value, f.LinkUri);
        if (f.Kind == FactKind.AddedBy && f.Person.IsValid) return AddedByChip(f.Person, f.Value);
        if (!DrawerRules.NeedsLabel(f.Kind)) return OneLine(Caption(f.Value).Secondary());
        // Label and value shape as ONE paragraph, so a wrap can never strand "Released" on the line above its date.
        return CaptionSpans(new TextSpan[]
        {
            new(Loc.Get(Facts.LabelKey(f.Kind)) + " ", Color: Tok.TextTertiary), new(f.Value, Color: Tok.TextSecondary),
        });
    }

    static TextEl OneLine(TextEl t) => t with { Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MaxLines = 1, MinWidth = 0f, Shrink = 1f };

    static SpanTextEl CaptionSpans(TextSpan[] spans)
    {
        var caption = Caption("");
        return new SpanTextEl(spans)
        {
            Size = caption.Size, Weight = caption.ResolvedWeight, LineHeight = caption.LineHeight, LineStacking = caption.LineStacking,
            LineBounds = caption.LineBounds, Color = Tok.TextSecondary,
            Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MaxLines = 1, MinWidth = 0f, Shrink = 1f,
        };
    }

    // The album as a quiet link (no accent, no underline — the hand cursor is the affordance).
    static Element LinkValue(string name, EntityUri uri)
    {
        Action? open = uri.IsValid ? () => Shell.GoTo(Shell.For(uri, name)) : null;
        return CaptionSpans(new TextSpan[] { new(name, OnClick: open) });
    }

    // The resolved adder: lead-in, the Added-by lane's avatar at the strip's 20, the name. No click — no user route.
    static Element AddedByChip(User person, string name) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XXS, Shrink = 0f, MinWidth = 0f,
        Children =
        [
            Caption(Loc.Get(Facts.LabelKey(FactKind.AddedBy)) + " ").Tertiary() with { Wrap = TextWrap.NoWrap, MaxLines = 1, Shrink = 0f },
            PersonPicture.Create("", 20f, displayName: name, imageSourcePath: Controls.ArtUrl(person.ImageId)),
            OneLine(Caption(name).Secondary()),
        ],
    };

    static Element GenreRun(string label) => OneLine(Caption(label).Tertiary());

    // A flag with its mark leading as an inline pair; Explicit has no glyph (the row carries the E badge).
    static Element FlagRun(FactKind kind)
    {
        string label = Loc.Get(Facts.LabelKey(kind));
        string? glyph = kind switch
        {
            FactKind.Video => Icons.Movie, FactKind.LocalFile => Icons.Folder, FactKind.Unavailable => Icons.Info, _ => null,
        };
        if (glyph is null) return GenreRun(label);
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XXS, Shrink = 0f, MinWidth = 0f,
            Children = [Icon(glyph, 11f, Tok.TextTertiary), GenreRun(label)],
        };
    }

    // Separators are CHILDREN, so the wrap row measures them and a line packs honestly.
    static void Join(List<Element> line, Element run)
    {
        if (line.Count > 0) line.Add(Caption("·").Tertiary() with { Wrap = TextWrap.NoWrap, MaxLines = 1, Shrink = 0f });
        line.Add(run);
    }

    // ══ 4. VERSIONS AND FORMATS (ch 01 W14/W15, ch 05 W14, ch 04 item 68) ════════════════════════════════════════════

    // One entry hung off the connector rail. The last entry's rail stops at its own stub (an elbow).
    // StrokeDividerDefault, NOT StrokeCardDefault: the card stroke is black in both themes and the spine vanished on dark.
    static Element ConnectedRow(string rowKey, Element body, bool isLast) => new BoxEl
    {
        Key = "cv:" + rowKey, Direction = 0, AlignItems = FlexAlign.Stretch, MinWidth = 0f,
        Children =
        [
            new BoxEl
            {
                Width = DrawerRules.GutterW, Shrink = 0f, ZStack = true, HitTestPassThrough = true,
                Children =
                [
                    new BoxEl
                    {
                        Width = 1f, Shrink = 0f, AlignSelf = FlexAlign.Start, Height = isLast ? DrawerRules.RowH / 2f : float.NaN,
                        Margin = new Edges4(DrawerRules.RailX, 0f, 0f, 0f), Fill = Prop.Of(static () => Tok.StrokeDividerDefault),
                    },
                    new BoxEl
                    {
                        Width = DrawerRules.StubW, Height = 1f, Shrink = 0f, AlignSelf = FlexAlign.Start,
                        Margin = new Edges4(DrawerRules.RailX, DrawerRules.RowH / 2f, 0f, 0f),
                        Fill = Prop.Of(static () => Tok.StrokeDividerDefault),
                    },
                ],
            },
            body,
        ],
    };

    // W15: the reserved music-video row — the real row's geometry and key, placeholder blocks, deliberately inert (a row
    // that cannot say which video it is must not offer to play one).
    static Element PendingVersionRow() => new BoxEl
    {
        Key = "ver:" + DrawerRules.VideoRowKey, Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinWidth = 0f,
        Grow = 1f, Height = DrawerRules.RowH, Padding = new Edges4(Spacing.XS, 0f, Spacing.XS, 0f),
        Corners = CornerRadius4.All(Radii.Control), HitTestPassThrough = true,
        Children =
        [
            new BoxEl
            {
                Width = DrawerRules.VideoThumbW, Height = DrawerRules.VideoThumbH, Shrink = 0f,
                Corners = CornerRadius4.All(Radii.Control), Fill = Tok.FillSubtleSecondary,
            },
            new BoxEl
            {
                Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = 6f, Children = [PendingBar(132f, 11f), PendingBar(84f, 9f)],
            },
        ],
    };

    static Element PendingBar(float w, float h) => new BoxEl
    {
        Width = w, Height = h, Shrink = 0f, AlignSelf = FlexAlign.Start, Corners = CornerRadius4.All(h / 2f), Fill = Tok.FillSubtleSecondary,
    };

    static Element VersionRow(Track version, Track parent, byte kind, string rowKey, bool isNow, float[]? peaks)
    {
        var meta = new List<Element>(6) { Caption(KindLabel(kind)).Tertiary() };
        if (version.Knows(TrackFields.Audio) && version.Tempo > 0)
        {
            // The swatch leads the tempo exactly as the row's own tempo lane does (Track.TempoCell) — one idiom, two places.
            if (version.CamelotColor != 0)
                meta.Add(new BoxEl
                {
                    Width = 6f, Height = 6f, Corners = CornerRadius4.All(1.5f), Opacity = 0.85f, Shrink = 0f, AlignSelf = FlexAlign.Center,
                    Fill = Design.Palette.DataDotInk(version.CamelotColor, Tok.Theme),
                });
            meta.Add(Caption(Format.TempoLabel(version.Tempo)));
            if (Facts.KeyLabel(Format.CamelotLabel(version.Camelot), Format.KeyLabel(version.Key)) is { Length: > 0 } key)
            {
                meta.Add(Caption("·").Tertiary());
                meta.Add(Caption(key).Tertiary());
            }
        }
        if (version.DurationMs > 0) meta.Add(Caption(Format.DurationCell(version.DurationMs)).Tertiary());

        return new BoxEl
        {
            Key = "ver:" + rowKey, Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinWidth = 0f, Grow = 1f,
            Height = DrawerRules.RowH, Padding = new Edges4(Spacing.XS, 0f, Spacing.XS, 0f), Corners = CornerRadius4.All(Radii.Control),
            Fill = kind == DrawerRules.SelfRow ? Tok.FillSubtleSecondary : ColorF.Transparent,   // rows, not cards: only self is plated
            Children =
            [
                Thumb(version, parent, kind, isNow),
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = 2f,
                    Children =
                    [
                        // 13.5 / 540 is 0.2.9's own version-title cut (TrackVersionsPanel.cs:311-316), ported as pixels.
                        new TextEl(version.Knows(TrackFields.Title) ? version.Title : "")
                        {
                            Size = 13.5f, Weight = 540, Color = isNow ? Tok.AccentTextPrimary : Tok.TextPrimary,
                            MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                        },
                        new BoxEl { Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Children = meta.ToArray() },
                    ],
                },
                peaks is null ? new BoxEl() : Waveform.Create(new WaveformModel(peaks, DrawerRules.WaveBars), key: "waveform"),
                Embed.Comp(new FormatProps(version, parent, kind), static () => new FormatSplitHost()) with { Key = "fmt:" + version.Uri.Text },
            ],
        }.Interactive(Interaction.Subtle);
    }

    // 16:9 with a play badge for the video, the bare square otherwise — and the now-playing overlay on the version that IS
    // the deck's item (ch 01 W14, item 73).
    static Element Thumb(Track version, Track parent, byte kind, bool isNow)
    {
        bool video = kind == DrawerRules.VideoKind;
        float w = video ? DrawerRules.VideoThumbW : DrawerRules.AudioThumb;
        float h = video ? DrawerRules.VideoThumbH : DrawerRules.AudioThumb;
        string? url = Controls.ArtUrl(video && !parent.VideoImageId.IsEmpty ? parent.VideoImageId : version.ImageId);
        Element art = Controls.Artwork(url, w, h, Radii.Control);
        if (!isNow && !video) return art;
        Element layer = isNow
            ? Controls.NowPlayingOverlay(version.Uri.Text, () => PlayVersion(version, parent, kind),
                                         fab: Math.Clamp(MathF.Min(w, h) * 0.62f, 22f, 28f), centred: true).Skeletonized(false)
            : new BoxEl
            {
                Width = w, Height = h, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HitTestPassThrough = true,
                Fill = new ColorF(0f, 0f, 0f, 0.28f),
                Children =
                [
                    new BoxEl
                    {
                        Width = 22f, Height = 22f, Corners = CornerRadius4.All(11f), Shrink = 0f, AlignItems = FlexAlign.Center,
                        Justify = FlexJustify.Center, Fill = new ColorF(1f, 1f, 1f, 0.92f),
                        Children = [Icon(Icons.Play, 11f, ColorF.FromRgba(17, 17, 17))],
                    },
                ],
            };
        return new BoxEl
        {
            Width = w, Height = h, Shrink = 0f, ZStack = true, Corners = CornerRadius4.All(Radii.Control), ClipToBounds = true,
            Children = [art, layer],
        };
    }

    static string KindLabel(byte kind) => kind switch
    {
        DrawerRules.VideoKind => Loc.Get(Strings.Detail.Versions.MusicVideo),
        DrawerRules.AudioKind => Loc.Get(Strings.Detail.Versions.AlternateAudio),
        _ => Loc.Get(Strings.Detail.Versions.ThisTrack),
    };

    // Play the FORM the user clicked: a music video plays through the PARENT song (kind 99 keys the video on the song —
    // DetailTracks.cs:3329-3342); an alternate recording plays itself. The deck's own item toggles.
    static void PlayVersion(Track version, Track parent, byte kind)
    {
        var target = kind == DrawerRules.VideoKind ? parent : version;
        if (target.IsValid) Invoke(target, () => Playback.PlayContext(target.Id));
    }

    // ── the format split button: [30 × 28 play | 20 × 28 caret] keyed "fmt:" + uri ──

    sealed record FormatProps(Track Version, Track Parent, byte Kind);

    sealed class FormatSplitHost : Component
    {
        FormatProps? _latest;
        IOverlayService? _overlay;
        Ref<NodeHandle>? _anchor;
        Ref<OverlayHandle?>? _handle;
        readonly Action _play, _open, _close;
        readonly Action<NodeHandle> _capture;
        readonly Func<NodeHandle> _anchorOf;

        public FormatSplitHost()
        {
            _play = () => { if (_latest is { } p) PlayVersion(p.Version, p.Parent, p.Kind); };
            _open = OpenLadder;
            _close = () => _handle?.Value?.Close();
            _capture = h => { if (_anchor is not null) _anchor.Value = h; };
            _anchorOf = () => _anchor is null ? default : _anchor.Value;
        }

        public override Element Render()
        {
            _latest = UseProps<FormatProps>();
            _overlay = UseContext(Overlay.Service);
            _anchor = UseRef<NodeHandle>(default);
            _handle = UseRef<OverlayHandle?>(null);
            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Stretch, Shrink = 0f, OnRealized = _capture,
                Children =
                [
                    Half(Icons.Play, 11f, Tok.TextPrimary, 30f, new CornerRadius4(Radii.Control, 0f, 0f, Radii.Control), _play),
                    Half(Icons.ChevronDown, 9f, Tok.TextSecondary, 20f, new CornerRadius4(0f, Radii.Control, Radii.Control, 0f), _open),
                ],
            };
        }

        static BoxEl Half(string glyph, float glyphSize, ColorF ink, float width, CornerRadius4 corners, Action onClick) => new()
        {
            Width = width, Height = 28f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = corners,
            Fill = Tok.FillControlDefault, HoverFill = Tok.AccentDefault, BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault,
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, BlocksDragArm = true, OnClick = onClick,
            Children = [Icon(glyph, glyphSize, ink)],
        };

        // The radio ladder the account was actually offered (best first) + "Use my default quality". ZERO rungs ⇒ the caret
        // does nothing at all — an empty menu is a dead end (W14). Built at click, never per frame.
        void OpenLadder()
        {
            var p = _latest;
            var overlay = _overlay;
            var cell = _handle;
            if (p is null || cell is null || Controls.IsNullOverlay(overlay) || !p.Version.IsValid) return;
            if (cell.Value is { IsOpen: true } open) { open.Close(); return; }
            var ladder = p.Version.Formats;
            if (ladder.IsEmpty) return;
            Span<int> order = stackalloc int[Math.Min(ladder.Length, 32)];
            int n = DrawerRules.LadderOrder(ladder, order);
            var id = p.Version.Id;
            byte? current = Playback.FormatOverrideFor(id);
            var items = new MenuFlyoutItem[n + 2];
            for (int i = 0; i < n; i++)
            {
                var rung = ladder[order[i]];
                byte format = rung.FormatId;
                items[i] = MenuFlyoutItem.RadioItem(DrawerRules.RadioLabel(format, rung.Kbps), current == format,
                    () => Playback.SetFormatOverride(id, format), enabled: DrawerRules.Decodable(format));
            }
            items[n] = MenuFlyoutItem.Separator;
            // The way BACK out of an override — without it the choice is a one-way door.
            items[n + 1] = MenuFlyoutItem.RadioItem(Loc.Get(Strings.Detail.Versions.UseDefaultQuality), current is null,
                () => Playback.SetFormatOverride(id, null));
            var handle = overlay.Open(_anchorOf, () => MenuFlyout.Create(items, _close), FlyoutPlacement.BottomEdgeAlignedRight,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
            handle.ClosedAction = () => { if (ReferenceEquals(cell.Value, handle)) cell.Value = null; };
            cell.Value = handle;
        }
    }

    // ══ 5. THE VIEW-CREDITS SHEET (ch 01 W27, item 74) ═══════════════════════════════════════════════════════════════

    sealed record CreditsProps(Track Track);

    sealed class CreditsHost : Component
    {
        CreditsProps? _latest;
        readonly Action _demand;

        public CreditsHost() => _demand = () =>
        {
            if (_latest is { Track.IsValid: true } p) Entities.EnsureEdge(FetchEdge.TrackCredits, p.Track.Slot);
        };

        public override Element Render()
        {
            var p = UseProps<CreditsProps>();
            _latest = p;
            uint epoch = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            _ = scope.Edges.TrackCredits.Changed.Value;
            _ = scope.Artists.Changed.Value;
            UseEffect(_demand, DepKey.From(p.Track.Slot, (int)epoch));
            int parent = p.Track.IsValid ? p.Track.Slot : 0;
            var credits = scope.Edges.TrackCredits;
            // Bars while unanswered — never an empty sheet that fills in (W27).
            Element body = DrawerRules.CreditsViewFor(credits.Readiness(parent), credits.Count(parent)) switch
            {
                DrawerRules.CreditsView.Loading => new BoxEl
                {
                    Direction = 1, Gap = 10f, Padding = new Edges4(0f, Spacing.S, 0f, Spacing.S),
                    Children = [LoadingBar(12f, 120f), LoadingBar(14f, 220f), LoadingBar(14f, 190f), LoadingBar(14f, 210f)],
                },
                DrawerRules.CreditsView.List => CreditsList(scope.Edges, parent),
                _ => new TextEl(Loc.Get(Strings.Menu.NoCredits)) { Size = 13f, Color = Tok.TextSecondary },
            };
            return new BoxEl { Direction = 1, MinWidth = 0f, MaxWidth = 440f, Children = [body] };
        }
    }

    // The server's group headings in its own casing (never re-cased), one row per contributor, in a bounded scroll.
    static Element CreditsList(Edges edges, int parent)
    {
        var targets = edges.TrackCredits.Targets(parent);
        var rows = edges.TrackCredits.Payload(parent);
        var keys = new string[rows.Length];
        for (int i = 0; i < rows.Length; i++)
            keys[i] = DrawerRules.CreditGroupKey(Entities.Strings.Resolve(rows[i].Group), Entities.Strings.Resolve(rows[i].Role));
        var order = new int[rows.Length];
        int n = DrawerRules.CreditOrder(keys, order);
        var kids = new List<Element>(n + 4);
        for (int k = 0; k < n; k++)
        {
            int i = order[k];
            if (keys[i].Length > 0 && DrawerRules.StartsGroup(keys, order.AsSpan(0, n), k))
                kids.Add(Design.Type.Eyebrow(keys[i]) with { Color = Tok.TextTertiary });
            kids.Add(CreditRow(Entities.Strings.Resolve(rows[i].Name), Entities.Strings.Resolve(rows[i].Role), targets[i]));
        }
        return ScrollView(new BoxEl { Direction = 1, Gap = Spacing.S, MinWidth = 0f, Children = kids.ToArray() }) with { MaxHeight = 420f };
    }

    // A credit linked to an artist slot is an accent link; an unlinked contributor is plain text.
    static Element CreditRow(string name, string role, int artistSlot)
    {
        Element label = artistSlot > 0
            ? new SpanTextEl(new TextSpan[] { new(name, OnClick: () => GoToArtist(new Artist(artistSlot))) })
            {
                Size = 13f, Weight = 650, Color = Tok.AccentTextPrimary, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            }
            : new TextEl(name) { Size = 13f, Weight = 650, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis };
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S,
            Children =
            [
                new BoxEl { Grow = 1f, Basis = 0f, MinWidth = 0f, Children = [label] },
                role.Length == 0 ? new BoxEl() : new TextEl(role) { Size = 11f, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            ],
        };
    }

    static Element LoadingBar(float h, float w) => new BoxEl
    {
        Height = h, Width = w, Corners = CornerRadius4.All(Radii.Control), Fill = Tok.FillSubtleSecondary,
    };

    // ══ 6. THE PURE SECTION — DrawerRules (Wavee.Tests/TrackDrawerRulesTests.cs) ═════════════════════════════════════

    /// <summary>Every drawer decision that is not layout: version order, the reserved video row, format labels / ladder
    /// order / decodability / rung, the persisted override codec, credit grouping and the sheet's state. No engine type.</summary>
    public static class DrawerRules
    {
        public const float VideoThumbW = 76f, VideoThumbH = 43f, AudioThumb = 43f;
        /// <summary>The connector gutter, the rail's x inside it (= the indent the table subtracts) and the stub.</summary>
        public const float GutterW = 20f, RailX = RowMetrics.RailOffset, StubW = 9f;
        /// <summary>A version row: the 43 thumb + 4 above + 4 below (ch 04 item 68).</summary>
        public const float RowH = AudioThumb + 2f * Spacing.XS;
        /// <summary>The music-video row's key is its KIND: it is reserved before its uri is known, and kind 99 yields at
        /// most one counterpart.</summary>
        public const string VideoRowKey = "v:video";
        public const int WaveBars = 64, MaxOverrides = 256;
        public const byte SelfRow = 0, VideoKind = 1, AudioKind = 2;

        public enum VideoRow : byte { None, Reserved, Edge, Counterpart }

        /// <summary>The relation's own video; else the kind-99 counterpart; else, while the relation is UNANSWERED and the
        /// catalogue says there is a video, the reserved row. A failed or contradicting answer reserves nothing (W26).</summary>
        public static VideoRow VideoRowFor(bool hasVideo, EdgeState versions, bool edgeHasVideo, bool counterpart)
            => edgeHasVideo ? VideoRow.Edge
             : counterpart ? VideoRow.Counterpart
             : hasVideo && versions == EdgeState.Unknown ? VideoRow.Reserved
             : VideoRow.None;

        /// <summary>Indices into the relation: the FIRST video, then every alternate audio in wire order
        /// (TrackVersionsPanel.cs:114-119). Returns the count written.</summary>
        public static int VersionOrder(ReadOnlySpan<VersionEdge> kinds, Span<int> into)
        {
            int n = 0;
            for (int i = 0; i < kinds.Length && n < into.Length; i++)
                if (kinds[i].Kind == TrackVersionKind.Video) { into[n++] = i; break; }
            for (int i = 0; i < kinds.Length && n < into.Length; i++)
                if (kinds[i].Kind == TrackVersionKind.Audio) into[n++] = i;
            return n;
        }

        /// <summary>A short label per wire format (<c>metadata.AudioFile.Format</c>); an unknown id still renders.</summary>
        public static string FormatLabel(byte formatId) => formatId switch
        {
            0 => "OGG 96", 1 => "OGG 160", 2 => "OGG 320",
            3 => "MP3 256", 4 => "MP3 320", 5 or 7 => "MP3 160", 6 => "MP3 96",
            8 => "AAC 24", 9 => "AAC 48",
            16 => "FLAC", 22 => "FLAC 24-bit",
            18 => "xHE-AAC 24", 19 => "xHE-AAC 16", 20 => "xHE-AAC 12",
            _ => "Format " + formatId.ToString(CultureInfo.InvariantCulture),
        };

        /// <summary>"320 kbps", or empty when the wire stated no bitrate.</summary>
        public static string KbpsLabel(ushort kbps) => kbps == 0 ? "" : kbps.ToString(CultureInfo.InvariantCulture) + " kbps";

        /// <summary>The radio row: label + THREE spaces + bitrate (FormatSplitButton.cs:105-106).</summary>
        public static string RadioLabel(byte formatId, ushort kbps)
            => kbps == 0 ? FormatLabel(formatId) : FormatLabel(formatId) + "   " + KbpsLabel(kbps);

        /// <summary>Exactly the formats <c>Spotify.Audio.FormatOf</c> can open (Ogg, MP3, FLAC); the rest render DISABLED.</summary>
        public static bool Decodable(byte formatId) => formatId is <= 6 or 16 or 22;

        /// <summary>Best first: bitrate descending, a rung with no stated bitrate last, ties in wire order.</summary>
        public static int LadderOrder(ReadOnlySpan<FormatEdge> ladder, Span<int> into)
        {
            int n = Math.Min(ladder.Length, into.Length);
            for (int i = 0; i < n; i++)
            {
                int rank = Rank(ladder[i].Kbps), j = i;
                while (j > 0 && Rank(ladder[into[j - 1]].Kbps) < rank) { into[j] = into[j - 1]; j--; }
                into[j] = i;
            }
            return n;

            static int Rank(ushort kbps) => kbps == 0 ? -1 : kbps;
        }

        /// <summary>The bandwidth rung an override asks the playback ladder for (the ladder picks a FILE per rung).</summary>
        public static Spotify.Audio.Quality QualityOf(byte formatId) => formatId switch
        {
            0 or 6 or 8 or 9 or 19 or 20 => Spotify.Audio.Quality.Normal96,
            1 or 5 or 7 or 18 => Spotify.Audio.Quality.High160,
            16 or 22 => Spotify.Audio.Quality.Lossless,
            _ => Spotify.Audio.Quality.VeryHigh320,
        };

        /// <summary>The group a credit files under: the server's heading, else its role (TrackCreditsDialog.cs:74).</summary>
        public static string CreditGroupKey(string? group, string? role) => !string.IsNullOrWhiteSpace(group) ? group : role ?? "";

        /// <summary>Group-by in FIRST-APPEARANCE order, members in wire order within a group. Writes original indices.</summary>
        public static int CreditOrder(ReadOnlySpan<string> keys, Span<int> into)
        {
            int w = 0;
            for (int i = 0; i < keys.Length && w < into.Length; i++)
            {
                bool seen = false;
                for (int k = 0; k < i && !seen; k++) seen = string.Equals(keys[k], keys[i], StringComparison.Ordinal);
                if (seen) continue;
                for (int j = i; j < keys.Length && w < into.Length; j++)
                    if (string.Equals(keys[j], keys[i], StringComparison.Ordinal)) into[w++] = j;
            }
            return w;
        }

        /// <summary>Does the row at <paramref name="position"/> of the grouped order open a new group (its heading)?</summary>
        public static bool StartsGroup(ReadOnlySpan<string> keys, ReadOnlySpan<int> order, int position)
            => position == 0 || !string.Equals(keys[order[position]], keys[order[position - 1]], StringComparison.Ordinal);

        public enum CreditsView : byte { Loading, Empty, List }

        /// <summary>Bars while unanswered; "No credits available" once the answer (or the failure) says there are none.</summary>
        public static CreditsView CreditsViewFor(EdgeState readiness, int count)
            => readiness == EdgeState.Unknown ? CreditsView.Loading : count > 0 ? CreditsView.List : CreditsView.Empty;

        /// <summary>The prose facts that take a tertiary lead-in: the two dates and the person (TrackFactsStrip.cs:221).</summary>
        public static bool NeedsLabel(FactKind kind) => kind is FactKind.Added or FactKind.Released or FactKind.AddedBy;

        /// <summary>Kind-237 magnitudes (0-255, loudest = 255) as the waveform control's 0..1 peaks.</summary>
        public static int Peaks(ReadOnlySpan<byte> magnitudes, Span<float> into)
        {
            int n = Math.Min(magnitudes.Length, into.Length);
            for (int i = 0; i < n; i++) into[i] = magnitudes[i] / 255f;
            return n;
        }

        /// <summary>The persisted per-uri format override map (ch 01 DATA GAP 14): <c>uri=id;uri=id</c>, oldest first.</summary>
        public static class FormatOverrides
        {
            /// <summary>A malformed entry is skipped; a repeated uri keeps its LAST value.</summary>
            public static List<KeyValuePair<string, byte>> Parse(string? text)
            {
                var map = new List<KeyValuePair<string, byte>>();
                if (string.IsNullOrEmpty(text)) return map;
                foreach (var entry in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    int eq = entry.LastIndexOf('=');
                    if (eq > 0 && byte.TryParse(entry.AsSpan(eq + 1), NumberStyles.None, CultureInfo.InvariantCulture, out byte id))
                        Put(map, entry[..eq], id, int.MaxValue);
                }
                return map;
            }

            public static string Serialize(IReadOnlyList<KeyValuePair<string, byte>> map)
            {
                var sb = new System.Text.StringBuilder(map.Count * 32);
                for (int i = 0; i < map.Count; i++)
                    (i > 0 ? sb.Append(';') : sb).Append(map[i].Key).Append('=').Append(map[i].Value.ToString(CultureInfo.InvariantCulture));
                return sb.ToString();
            }

            public static byte? Find(IReadOnlyList<KeyValuePair<string, byte>> map, string uri)
            {
                for (int i = 0; i < map.Count; i++)
                    if (string.Equals(map[i].Key, uri, StringComparison.Ordinal)) return map[i].Value;
                return null;
            }

            /// <summary>Set (appended as the newest) or clear (null) one uri; past <paramref name="cap"/> the oldest entry
            /// drops. A uri carrying ';' or '=' is refused. Returns whether the map changed.</summary>
            public static bool Put(List<KeyValuePair<string, byte>> map, string uri, byte? formatId, int cap = MaxOverrides)
            {
                if (string.IsNullOrEmpty(uri) || uri.AsSpan().IndexOfAny(';', '=') >= 0) return false;
                int at = -1;
                for (int i = 0; i < map.Count && at < 0; i++)
                    if (string.Equals(map[i].Key, uri, StringComparison.Ordinal)) at = i;
                if (formatId is not { } id)
                {
                    if (at < 0) return false;
                    map.RemoveAt(at);
                    return true;
                }
                if (at >= 0 && map[at].Value == id && at == map.Count - 1) return false;
                if (at >= 0) map.RemoveAt(at);
                map.Add(new KeyValuePair<string, byte>(uri, id));
                while (map.Count > Math.Max(1, cap)) map.RemoveAt(0);
                return true;
            }
        }
    }
}
