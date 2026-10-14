// ── Themes/Classic/Structure/Lists/ClassicTrackDrawer.cs — Classic's expanded-row drawer (themes plan 6.1, T8c) ────────────────
//
// From `Entities/Track.Drawer.cs` (sections 3 and 4: the facts strip, "Versions and formats") and `Entities/Track.Table.cs`
// (`TableHost.DrawerBox`): the picture of the drawer a row opens under itself. `StyleKeys.TrackDrawer` carries three templates:
//
//  - the FRAME the table mounts: the clip box that springs its presented height (`Design.Reveal.Drawer`: the drawer's open and close
//    motion, the rows below ride it), continuing the table's zebra with square top corners (Modern) or none (Classic), and the presence
//    box that carries the indent so the rail lands on the row's artwork centre;
//  - the BODY: the facts strip (hero figures over their captions, the middot prose line, the genre / flag line; a fact that lands
//    while the drawer is open fades up, what it opens WITH rides the reveal), the "Versions and formats" eyebrow and the connected
//    rows (the rail with an elbow at the last stub, the self row plated, the reserved music-video row, alternate audio with its
//    waveform) with the format split button;
//  - the FORMAT SPLIT BUTTON's two halves (30 x 28 play, 20 x 28 caret), on the core component that owns the radio ladder.
//
// WHAT THE CORE KEEPS. Which drawer is open, the keyed mount, the reveal wiring (`OnRealized`: the node the keyboard and the scroll
// anchor read), the zebra parity, the indent, the demand and the relations, the facts, the version entries and their order, the
// reserved video row (reserved BEFORE the relation answers, so the reveal targets its final height on the seed frame and the real row
// patches into it), the waveform's peaks, the "arriving" latch, the format override and its overlay and the play verb. A template draws
// and never names `Playback`, `Entities`, `Store` or `Shell` (WVTH002); numbers are `Rules\ClassicTrackDrawerLayout.cs` (WVTH003).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

using DrawerRules = global::Wavee.Track.DrawerRules;
using Fact = global::Wavee.Track.Fact;
using FactForm = global::Wavee.Track.FactForm;
using FactKind = global::Wavee.Track.FactKind;
using Facts = global::Wavee.Track.Facts;
using FactSplit = global::Wavee.Track.FactSplit;
using Format = global::Wavee.Track.Format;
using RowMetrics = global::Wavee.Track.RowMetrics;

/// <summary>Classic's style for the track drawer.</summary>
internal static class ClassicTrackDrawer
{
    /// <summary>The style Classic registers under <c>StyleKeys.TrackDrawer</c>. Stateless: every host and a test share it.</summary>
    public static readonly TrackDrawerStyle Style = new(Frame, Body, FormatSplit);

    // ══ THE FRAME ════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The drawer's mount, keying and reveal — its BODY is the profile's. One drawer at a time, keyed by ROW identity (item id, else
    /// uri#@display), continuing the row's zebra parity with square top corners. ONE spec on the clip box (Design.Reveal.Drawer): the presented
    /// height springs, the rows below ride it; the presence box is plain padding.</summary>
    static Element Frame(TrackDrawerFrameArgs a)
    {
        string rowKey = a.RowKey;
        bool classic = a.Classic;
        var odd = a.Odd;
        return new BoxEl
        {
            Key = "drawer:" + rowKey, Direction = 1, MinWidth = 0f, ClipToBounds = true, Animate = Design.Reveal.Drawer,
            OnRealized = a.OnRealized,
            Fill = classic ? (Prop<ColorF>)ColorF.Transparent
                : Prop.Of(() => odd() ? R.Layer.RowZebra : ColorF.Transparent),
            Margin = classic ? default : new Edges4(RowMetrics.RowInset, 0f, RowMetrics.RowInset, 0f),
            Corners = classic ? CornerRadius4.All(0f) : new CornerRadius4(0f, 0f, ClassicTrackDrawerLayout.ModernBottomRadius, ClassicTrackDrawerLayout.ModernBottomRadius),
            Children =
            [
                new BoxEl
                {
                    Key = "drawer-presence:" + rowKey, Direction = 1, MinWidth = 0f,
                    // The rail lands on the ROW'S ARTWORK CENTRE: indent = ArtCentreIndent − the rail's own offset.
                    Padding = new Edges4(a.Indent, 0f, Spacing.L, Spacing.S),
                    Children = [a.Body with { Key = "drawer-body:" + rowKey }],
                },
            ],
        };
    }

    // ══ THE BODY ═════════════════════════════════════════════════════════════════════════════════════════════════════

    static Element Body(TrackDrawerBodyArgs a)
    {
        var entries = a.Entries;
        var body = new Element[2 + entries.Count];
        body[0] = FactsStrip(a.Facts, arriving: a.Arriving);
        // A whole step of top air, not a hairline: the eyebrow heads the ROWS, not the flat strip above it.
        body[1] = R.Type.Eyebrow(Loc.Get(Strings.Detail.Versions.VersionsAndFormats)) with
        {
            Key = "versions-head", Color = Tok.TextTertiary, Margin = new Edges4(0f, Spacing.M, 0f, ClassicTrackDrawerLayout.HeadBottom),
        };
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            Element row = e.Reserved ? PendingVersionRow() : VersionRow(a, e.Version, e.Kind, e.RowKey, e.IsNow, e.Peaks);
            body[2 + i] = ConnectedRow(e.RowKey, row, isLast: i == entries.Count - 1);
        }
        return new BoxEl { Direction = 1, MinWidth = 0f, Children = body };
    }

    // ══ THE FORMAT SPLIT BUTTON ══════════════════════════════════════════════════════════════════════════════════════

    static Element FormatSplit(TrackDrawerFormatArgs f) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Stretch, Shrink = 0f, OnRealized = f.OnRealized,
        Children =
        [
            Half(Icons.Play, ClassicTrackDrawerLayout.PlayGlyph, Tok.TextPrimary, ClassicTrackDrawerLayout.PlayHalfWidth,
                 new CornerRadius4(Radii.Control, 0f, 0f, Radii.Control), f.Play),
            Half(Icons.ChevronDown, ClassicTrackDrawerLayout.CaretGlyph, Tok.TextSecondary, ClassicTrackDrawerLayout.CaretHalfWidth,
                 new CornerRadius4(0f, Radii.Control, Radii.Control, 0f), f.Open),
        ],
    };

    // ══ THE FACTS STRIP, THE VERSIONS ════════════════════════════════════════════════════════════════════════════════

    static float StatGap => Spacing.XL;

    static readonly EnterExit? s_fadeUp = new EnterExit(Opacity: 0f, Active: true);

    static readonly LayoutTransition s_shove = new(TransitionChannels.Position,
        TransitionDynamics.Tween(Expressive.Fast, Easing.SmoothOut));

    // Stable per-KIND fact keys in FactKind order (a changed value cross-swaps in place; a NEW fact fades up).
    static readonly string[] s_factKeys =
    [
        "fact:plays", "fact:bpm", "fact:key", "fact:added", "fact:duration", "fact:album", "fact:released",
        "fact:addedby", "fact:isrc", "fact:descriptors", "fact:explicit", "fact:video", "fact:local", "fact:unavailable",
    ];

    // Three lines of flat typography and no chrome: the hero figures, the middot prose line, the genre/flag line. A track
    // that states nothing yields a bare box (W26).
    static Element FactsStrip(IReadOnlyList<Fact> facts, bool arriving)
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
            else if (Facts.IsHeroFact(f.Kind)) heroes.Add(Stat(in f, arriving));
            else Join(prose, ProseRun(in f));
        }
        var lines = new List<Element>(3);
        if (heroes.Count > 0)
            lines.Add(new BoxEl
            {
                Key = "facts-hero", Direction = 0, Wrap = true, Gap = StatGap, MinWidth = 0f,
                Stagger = Design.Reduced || !arriving ? 0f : R.Motion.MastheadStaggerMs,   // reduced motion is a VALUE
                Children = heroes.ToArray(),
            });
        if (prose.Count > 0) lines.Add(FactLine("facts-prose", prose, arriving));
        if (genres.Count > 0) lines.Add(FactLine("facts-genres", genres, arriving));
        return new BoxEl
        {
            Key = "facts", Direction = 1, Gap = Spacing.M, MinWidth = 0f, Padding = new Edges4(0f, Spacing.XS, 0f, Spacing.S),
            Children = lines.ToArray(),
        };
    }

    // A whole line fades and FLIPs as one — a middot sentence staggered word by word reads as a teleprompter.
    static BoxEl FactLine(string key, List<Element> runs, bool arriving) => new()
    {
        Key = key, Enter = arriving ? s_fadeUp : null, Layout = s_shove, Direction = 0, Wrap = true, AlignItems = FlexAlign.Center,
        Gap = Spacing.XS, MinWidth = 0f, Children = runs.ToArray(),
    };

    // One hero figure over its caption, keyed by KIND. A pending fact is its own dash at 0.6.
    static Element Stat(in Fact f, bool arriving)
    {
        bool pending = f.Form == FactForm.Pending;
        var split = pending ? new FactSplit(f.Value, null) : Facts.HeroSplit(in f);
        return new BoxEl
        {
            Key = s_factKeys[(int)f.Kind], Enter = arriving ? s_fadeUp : null, Layout = s_shove, Direction = 1, Gap = 0f, Shrink = 0f,
            MinWidth = 0f, Margin = new Edges4(0f, 0f, StatGap, 0f), Opacity = pending ? 0.6f : 1f,
            Children =
            [
                R.Type.StatHero(split.Value, split.Unit),
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
        Action? open = uri.IsValid ? () => Track.OpenEntity(uri, name) : null;
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
                Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = ClassicTrackDrawerLayout.PendingGap, Children = [Controls.PendingBar(132f, 11f), Controls.PendingBar(84f, 9f)],
            },
        ],
    };

    static Element VersionRow(TrackDrawerBodyArgs a, Track version, byte kind, string rowKey, bool isNow, float[]? peaks)
    {
        Track parent = a.Track;
        var meta = new List<Element>(6) { Caption(KindLabel(kind)).Tertiary() };
        if (version.Knows(TrackFields.Audio) && version.Tempo > 0)
        {
            // The swatch leads the tempo exactly as the row's own tempo lane does (Track.TempoCell) — one idiom, two places.
            if (version.CamelotColor != 0)
                meta.Add(new BoxEl
                {
                    Width = ClassicTrackRowLayout.TempoDot, Height = ClassicTrackRowLayout.TempoDot, Corners = CornerRadius4.All(ClassicTrackDrawerLayout.TempoDotRadius), Opacity = ClassicTrackRowLayout.TempoDotOpacity, Shrink = 0f, AlignSelf = FlexAlign.Center,
                    Fill = R.Ink.DataDot(version.CamelotColor),
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
                Thumb(version, parent, kind, isNow, a.PlayVersion),
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = ClassicTrackDrawerLayout.TextGap,
                    Children =
                    [
                        // 13.5 / 540 is 0.2.9's own version-title cut (TrackVersionsPanel.cs:311-316), ported as pixels.
                        R.Type.DenseTitle(version.Knows(TrackFields.Title) ? version.Title : "") with
                        {
                            Weight = 540, Color = isNow ? Tok.AccentTextPrimary : Tok.TextPrimary,
                            MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                        },
                        new BoxEl { Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Children = meta.ToArray() },
                    ],
                },
                peaks is null ? new BoxEl() : Waveform.Create(new WaveformModel(peaks, DrawerRules.WaveBars), key: "waveform"),
                a.FormatButton(version, parent, kind),
            ],
        }.Interactive(Interaction.Subtle);
    }

    // 16:9 with a play badge for the video, the bare square otherwise — and the now-playing overlay on the version that IS
    // the deck's item (ch 01 W14, item 73).
    static Element Thumb(Track version, Track parent, byte kind, bool isNow, Action<Track, Track, byte> playVersion)
    {
        bool video = kind == DrawerRules.VideoKind;
        float w = video ? DrawerRules.VideoThumbW : DrawerRules.AudioThumb;
        float h = video ? DrawerRules.VideoThumbH : DrawerRules.AudioThumb;
        string? url = Controls.ArtUrl(video && !parent.VideoImageId.IsEmpty ? parent.VideoImageId : version.ImageId);
        Element art = Controls.Artwork(url, w, h, Radii.Control);
        if (!isNow && !video) return art;
        Element layer = isNow
            ? Controls.NowPlayingOverlay(version.Uri.Text, () => playVersion(version, parent, kind),
                                         fab: Math.Clamp(MathF.Min(w, h) * 0.62f, 22f, 28f), centred: true).Skeletonized(false)
            : new BoxEl
            {
                Width = w, Height = h, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HitTestPassThrough = true,
                Fill = ClassicTrackDrawerLayout.VideoScrim,
                Children =
                [
                    new BoxEl
                    {
                        Width = ClassicTrackDrawerLayout.PlayBadge, Height = ClassicTrackDrawerLayout.PlayBadge, Corners = CornerRadius4.All(11f), Shrink = 0f, AlignItems = FlexAlign.Center,
                        Justify = FlexJustify.Center, Fill = ClassicTrackDrawerLayout.PlayBadgeFill,
                        Children = [Icon(Icons.Play, 11f, ClassicTrackDrawerLayout.PlayBadgeInk)],
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

    static BoxEl Half(string glyph, float glyphSize, ColorF ink, float width, CornerRadius4 corners, Action onClick) => new()
    {
        Width = width, Height = ClassicTrackDrawerLayout.SplitHeight, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = corners,
        Fill = Tok.FillControlDefault, HoverFill = Tok.AccentDefault, BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault,
        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, BlocksDragArm = true, OnClick = onClick,
        Children = [Icon(glyph, glyphSize, ink)],
    };
}
